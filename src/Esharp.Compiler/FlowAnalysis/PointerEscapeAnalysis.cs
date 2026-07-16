using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Lowering;
using Esharp.Syntax;

// Historical interprocedural pass, git mv'd from Lowering/ to FlowAnalysis/ (B3).
// The CFG-framework replacement is EscapeAnalysis.cs in this same directory.
// Retained for the interprocedural fixpoint design and for diff history.
// Namespace updated from Esharp.Lowering to Esharp.FlowAnalysis.
namespace Esharp.FlowAnalysis;

/// Whole-module pass that decides the CLR representation of every `*T` parameter.
///
/// `*T` has one *semantic* — Go's pointer: nullable, aliasing, first-class — and
/// a *representation* the compiler chooses, exactly as `data` chooses struct vs
/// class. The choice:
///
///   - **escapes** the frame (returned, stored, captured, put in a collection) or
///     is **nullable** (compared/assigned `nil`)  →  `__Ptr_T` heap wrapper
///     (`HeapPointerBoundType`).
///   - provably **neither**  →  managed pointer `ref T` (`ByRefBoundType`),
///     zero-allocation and aliasing the caller's storage.
///
/// `__Ptr_T` is the conservative top of the lattice: when escape/nullability
/// cannot be disproven, the wrapper is used (always correct, only costs an
/// allocation). The managed pointer is applied only when proven safe.
///
/// Parameter representation is interprocedural — a `*T` that flows as an argument
/// into a callee parameter that is itself a wrapper must be a wrapper too (a
/// managed pointer can't be re-wrapped without losing aliasing). That single
/// clause is the only cross-function dependency; everything else is local. It is
/// resolved by a monotone worklist fixpoint: start every parameter at the
/// managed-pointer bottom and raise to wrapper as forcing facts are discovered,
/// until stable. Member access, dereference, and method calls on a `*T` do NOT
/// escape it (the value is read, not leaked), so the common by-ref receiver
/// downgrades to `ref T` and aliases its caller — the point of the optimization.
///
/// External / unresolved calls are treated as managed-pointer-compatible (the BCL
/// `ref`/`in` convention), so passing a `*T` to a BCL API does not force the
/// wrapper.
internal static class PointerEscapeAnalysis
{
    /// <summary>
    /// Realize pointer representations once every source unit is bound.  `*T`
    /// has one source meaning but a callee's durable-vs-managed representation
    /// constrains every caller, including callers declared in another file.
    /// </summary>
    public static IReadOnlyList<BoundCompilationUnit> Run(
        IReadOnlyList<BoundCompilationUnit> units, DiagnosticBag diagnostics, bool showAllocations = false)
    {
        if (units.Count == 0) return units;
        var members = units.SelectMany(unit => unit.Members).ToList();
        var rewritten = RunMembers(members, diagnostics, showAllocations);

        // RunMembers preserves member order. Re-slice the result onto the
        // original units rather than flattening the compilation into one host.
        var offset = 0;
        var result = new List<BoundCompilationUnit>(units.Count);
        foreach (var unit in units)
        {
            var count = unit.Members.Count;
            result.Add(unit with { Members = rewritten.GetRange(offset, count) });
            offset += count;
        }
        return result;
    }

    static List<BoundMember> RunMembers(List<BoundMember> members, DiagnosticBag diagnostics, bool showAllocations)
    {
        var funcs = Flatten(members).ToList();
        if (funcs.Count == 0) return members;

        // name -> overloads, for resolving call targets to callee parameter reps.
        var byName = new Dictionary<string, List<BoundFunctionDeclaration>>(StringComparer.Ordinal);
        foreach (var f in funcs)
            (byName.TryGetValue(f.Name, out var l) ? l : byName[f.Name] = new()).Add(f);

        // A raw function pointer is an ABI boundary: unlike a direct call, its
        // callee may be invoked after the originating frame has gone away. Any
        // `*T` slot on an address-taken function therefore uses the durable
        // carrier form. This also gives the calli signature one stable CLR
        // shape rather than letting a source `*T` mean `T&` at one call site and
        // `__Ptr_T` at another.
        var addressTaken = FunctionPointerSignatureRewriter.CollectAddressTakenFunctions(funcs);

        // Representation table: per function, per parameter index, true = wrapper.
        // A non-`*T` parameter is irrelevant and stays false.
        var wrapper = new Dictionary<BoundFunctionDeclaration, bool[]>(ReferenceEqualityComparer.Instance);
        foreach (var f in funcs)
        {
            var arr = new bool[f.Parameters.Count];
            for (var i = 0; i < f.Parameters.Count; i++)
                // Seed the bottom: a `*T` parameter starts as a managed pointer
                // candidate. Non-pointer params are never raised.
                arr[i] = addressTaken.Contains((f.Name, f.Parameters.Count))
                    && !f.Parameters[i].ReadOnlyByRef
                    && (f.Parameters[i].Type is HeapPointerBoundType || f.Parameters[i].ByRef);
            wrapper[f] = arr;
        }

        // Monotone worklist fixpoint. Recompute each function's forcing facts under
        // the current callee-rep assumptions; raise any parameter that must be a
        // wrapper. Re-enqueue callers when a callee's reps change (a callee param
        // turning wrapper can force a caller's argument to wrapper).
        var queue = new Queue<BoundFunctionDeclaration>(funcs);
        var inQueue = new HashSet<BoundFunctionDeclaration>(funcs, ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            var f = queue.Dequeue();
            inQueue.Remove(f);

            var changed = false;
            var reps = wrapper[f];
            var tracked = PointerParamPositions(f);
            if (tracked.Count == 0) continue;

            var facts = new EscapeFacts(tracked.Keys.ToHashSet(StringComparer.Ordinal), byName, wrapper);
            facts.Walk(f.Body);

            // A pointer parameter live across a suspension would otherwise be
            // copied into an async state-machine field as `T&`, which the CLR
            // forbids. Reuse the same liveness authority as AsyncLowering so
            // only parameters actually read after an await are raised; dead
            // pointer parameters keep the zero-allocation managed-ref form.
            var asyncLiveNames = f.HasAwait
                ? AwaitPointAnalyzer.Analyze(f.Body, f.Parameters).SpilledLocals
                    .Select(local => local.Name)
                    .ToHashSet(StringComparer.Ordinal)
                : null;

            foreach (var (name, idx) in tracked)
            {
                if (reps[idx]) continue; // already wrapper — monotone, never lowers
                if (facts.Escaped.Contains(name)
                    || facts.Nullable.Contains(name)
                    || asyncLiveNames?.Contains(name) == true)
                {
                    reps[idx] = true;
                    changed = true;
                }
            }

            if (changed)
                foreach (var caller in funcs)
                    if (!inQueue.Contains(caller)) { queue.Enqueue(caller); inQueue.Add(caller); }
        }

        // Rewrite each function to its decided representation, then stitch the
        // rewritten functions back into the member tree.
        var rewritten = new Dictionary<BoundFunctionDeclaration, BoundFunctionDeclaration>(ReferenceEqualityComparer.Instance);
        foreach (var f in funcs)
        {
            if (showAllocations)
                for (var i = 0; i < f.Parameters.Count; i++)
                    if (wrapper[f][i] && !f.Parameters[i].ReadOnlyByRef)
                        diagnostics.Warn(f.Span, DiagnosticDescriptors.PointerPromotionAllocation,
                            $"Pointer parameter '{f.Parameters[i].Name}' requires a heap-backed cell because it escapes or is nullable.");
            rewritten[f] = RewriteFunction(f, wrapper[f]);
        }

        // The address-of node records a first-class callable signature in its
        // own bound type. Retarget it after the parameter fixed point so both
        // the function's emitted signature and every `calli` site agree on the
        // selected `*T` representation.
        foreach (var f in funcs)
            rewritten[f] = FunctionPointerSignatureRewriter.Rewrite(rewritten[f], byName, rewritten);

        // Deciding a pointer parameter's representation is only half of the
        // problem.  An address originating in a local must be promoted once when
        // it crosses a durable boundary (return, heap field, wrapper callee,
        // capture), even if that address first travelled through a pointer local.
        // This runs after the parameter fixed point so call arguments see the
        // final callee contract.
        foreach (var f in funcs)
        {
            var durableAliases = MarkEscapingAddressLocals(rewritten[f], byName, wrapper);
            if (durableAliases.Count > 0)
            {
                if (showAllocations)
                    foreach (var local in durableAliases.Keys)
                        diagnostics.Warn(f.Span, DiagnosticDescriptors.PointerPromotionAllocation,
                            $"Address of local '{local.Name}' escapes and promotes the local to a heap-backed cell.");
                rewritten[f] = LocalLowering.RealizeEscapingAddressableLocals(rewritten[f], durableAliases);
            }
        }

        // Keep malformed IL out of the diagnostic path.  The optimizer normally
        // realizes every durable pointer alias above; if a future bound-tree shape
        // bypasses that realization, stop on the source expression that leaked the
        // managed borrow rather than relying on ILVerify's post-emission ES0900.
        PointerLifetimeContainment.Validate(rewritten.Values, diagnostics);

        return members.Select(m => ReplaceFunctions(m, rewritten)).ToList();
    }

    // === Function discovery / reassembly ===

    static IEnumerable<BoundFunctionDeclaration> Flatten(IEnumerable<BoundMember> members)
    {
        foreach (var m in members)
            switch (m)
            {
                case BoundFunctionDeclaration f: yield return f; break;
                case BoundDataDeclaration d:
                    foreach (var im in d.InstanceMethods) yield return im;
                    break;
                case BoundStaticFuncDeclaration s:
                    foreach (var sf in s.Functions) yield return sf;
                    break;
            }
    }

    static BoundMember ReplaceFunctions(BoundMember m, Dictionary<BoundFunctionDeclaration, BoundFunctionDeclaration> map) => m switch
    {
        BoundFunctionDeclaration f when map.TryGetValue(f, out var r) => r,
        BoundDataDeclaration d => d with { InstanceMethods = d.InstanceMethods.Select(im => map.GetValueOrDefault(im, im)).ToList() },
        BoundStaticFuncDeclaration s => s with { Functions = s.Functions.Select(sf => map.GetValueOrDefault(sf, sf)).ToList() },
        _ => m,
    };

    // An address can outlive its stack local only when it flows to heap-owned storage or
    // crosses the function boundary.  Keep the *originating local* as the fact, rather
    // than only looking for the spelling `&local` at a sink: `var p = &local; return p`
    // has the same lifetime requirement and must use one shared wrapper-backed cell.
    static Dictionary<Esharp.Symbols.LocalSymbol, BoundType> MarkEscapingAddressLocals(
        BoundFunctionDeclaration function,
        Dictionary<string, List<BoundFunctionDeclaration>> byName,
        Dictionary<BoundFunctionDeclaration, bool[]> wrapper)
    {
        var aliases = new Dictionary<Esharp.Symbols.LocalSymbol, HashSet<Esharp.Symbols.LocalSymbol>>(
            ReferenceEqualityComparer.Instance);
        var durableAliases = new Dictionary<Esharp.Symbols.LocalSymbol, BoundType>(ReferenceEqualityComparer.Instance);
        var aliasElementTypes = new Dictionary<Esharp.Symbols.LocalSymbol, BoundType>(ReferenceEqualityComparer.Instance);
        // The normal path is symbol-backed.  A few older synthesized address
        // nodes predate BoundNameExpression.Symbol, so retain a narrow fallback
        // while those nodes are migrated; it is never the authority for ordinary
        // source names.
        var knownLocalsByName = new Dictionary<string, Esharp.Symbols.LocalSymbol>(StringComparer.Ordinal);

        void Mark(IEnumerable<Esharp.Symbols.LocalSymbol> origins)
        {
            foreach (var origin in origins)
                origin.AddressEscapes = true;
        }

        HashSet<Esharp.Symbols.LocalSymbol> Origins(BoundExpression expression)
        {
            var result = new HashSet<Esharp.Symbols.LocalSymbol>(ReferenceEqualityComparer.Instance);
            Add(expression, result);
            return result;
        }

        void Add(BoundExpression expression, HashSet<Esharp.Symbols.LocalSymbol> into)
        {
            switch (expression)
            {
                case BoundAddressOfVariableExpression { Target: BoundNameExpression target }:
                    if (target.Symbol is Esharp.Symbols.LocalSymbol local)
                        into.Add(local);
                    else if (knownLocalsByName.TryGetValue(target.Name, out var legacyLocal))
                        into.Add(legacyLocal);
                    break;
                case BoundNameExpression { Symbol: Esharp.Symbols.LocalSymbol aliasLocal }
                    when aliases.TryGetValue(aliasLocal, out var origins):
                    into.UnionWith(origins);
                    break;
                case BoundConversion conversion:
                    Add(conversion.Operand, into);
                    break;
                case BoundConditionalExpression conditional:
                    Add(conditional.Consequence, into);
                    Add(conditional.Alternative, into);
                    break;
                case BoundNullCoalescingExpression coalescing:
                    Add(coalescing.Left, into);
                    Add(coalescing.Right, into);
                    break;
            }
        }

        void RecordAlias(Esharp.Symbols.LocalSymbol? alias, BoundExpression value)
        {
            if (alias is null) return;
            knownLocalsByName[alias.Name] = alias;
            var origins = Origins(value);
            if (origins.Count == 0) aliases.Remove(alias);
            else aliases[alias] = origins;
        }

        void RecordDurableAlias(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundNameExpression { Type: ByRefBoundType byRef } name:
                    if (name.Symbol is Esharp.Symbols.LocalSymbol local && aliases.ContainsKey(local))
                        durableAliases[local] = new HeapPointerBoundType(byRef.Inner);
                    else if (knownLocalsByName.TryGetValue(name.Name, out var legacyLocal)
                             && aliases.ContainsKey(legacyLocal))
                        durableAliases[legacyLocal] = new HeapPointerBoundType(byRef.Inner);
                    break;
                case BoundConversion conversion:
                    RecordDurableAlias(conversion.Operand);
                    break;
                case BoundConditionalExpression conditional:
                    RecordDurableAlias(conditional.Consequence);
                    RecordDurableAlias(conditional.Alternative);
                    break;
                case BoundNullCoalescingExpression coalescing:
                    RecordDurableAlias(coalescing.Left);
                    RecordDurableAlias(coalescing.Right);
                    break;
            }
        }

        BoundFunctionDeclaration? ResolveCallee(string? name, int argCount)
        {
            if (name is null || !byName.TryGetValue(name, out var overloads)) return null;
            return overloads.FirstOrDefault(candidate => candidate.Parameters.Count == argCount)
                ?? (overloads.Count == 1 ? overloads[0] : null);
        }

        static bool CarriesDurablePointerElement(BoundType type) => type switch
        {
            // A generic receiver such as List<*Cell> / Dictionary<K, *Cell>
            // owns its element slots. Passing an address alias into one of its
            // mutating calls must therefore promote the alias before emission;
            // otherwise CodeGen tries to pass `Cell&` where the closed CLR
            // method requires `__Ptr_Cell`.
            HeapPointerBoundType => true,
            ExternalType external => external.TypeArgs.Any(CarriesDurablePointerElement),
            ExternalCSharpType external => external.TypeArgs.Any(CarriesDurablePointerElement),
            DataType data => data.TypeArgs.Any(CarriesDurablePointerElement),
            ChoiceType choice => choice.TypeArgs.Any(CarriesDurablePointerElement),
            ResultType result => CarriesDurablePointerElement(result.OkType)
                || CarriesDurablePointerElement(result.ErrorType),
            TupleType tuple => tuple.ElementTypes.Any(CarriesDurablePointerElement),
            _ => false,
        };

        void VisitStatement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements) VisitStatement(child);
                    break;
                case BoundVariableDeclaration declaration:
                    if (declaration.Local is { } declaredLocal
                        && declaration.DeclaredType is ByRefBoundType declaredByRef)
                        aliasElementTypes[declaredLocal] = declaredByRef.Inner;
                    RecordAlias(declaration.Local, declaration.Initializer);
                    VisitExpression(declaration.Initializer, escaping: false);
                    break;
                case BoundAssignment assignment:
                    if (assignment.Target is BoundNameExpression { Symbol: Esharp.Symbols.LocalSymbol local })
                        RecordAlias(local, assignment.Value);
                    VisitExpression(assignment.Target, escaping: false);
                    // A property/field store and an indexed `*T` element store
                    // are durable sinks.  The latter was previously omitted,
                    // leaving `map[key] = pointerAlias` as a managed ref until
                    // the emitter/verifier rejected the eventual CLR call.
                    VisitExpression(assignment.Value,
                        assignment.Target is BoundMemberAccessExpression
                        || assignment.Target is BoundIndexExpression { Type: HeapPointerBoundType });
                    break;
                case BoundCompoundAssignment compound:
                    VisitExpression(compound.Target, escaping: false);
                    VisitExpression(compound.Value, escaping: false);
                    break;
                case BoundReturnStatement { Expression: { } returned }:
                    // A pointer local in ordinary value position is implicitly
                    // dereferenced by E# (`return p` from `-> int` reads `p`).
                    // Only a pointer-valued return leaks the address identity.
                    // Treating every name-shaped return as a durable escape
                    // retyped the value read to __Ptr_T and produced invalid IL.
                    VisitExpression(returned, escaping: function.ReturnType is HeapPointerBoundType or ByRefBoundType);
                    break;
                case BoundThrowStatement { Expression: { } thrown }:
                    VisitExpression(thrown, escaping: true);
                    break;
                case BoundExpressionStatement expression:
                    VisitExpression(expression.Expression, escaping: false);
                    break;
                case BoundIfStatement conditional:
                    VisitExpression(conditional.Condition, escaping: false);
                    VisitStatement(conditional.Then);
                    if (conditional.Else is not null) VisitStatement(conditional.Else);
                    break;
                case BoundWhileStatement loop:
                    VisitExpression(loop.Condition, escaping: false);
                    VisitStatement(loop.Body);
                    break;
                case BoundTryStatement attempt:
                    VisitStatement(attempt.Body);
                    foreach (var clause in attempt.Catches) VisitStatement(clause.Body);
                    break;
                case BoundForEachStatement each:
                    VisitExpression(each.Collection, escaping: false);
                    VisitStatement(each.Body);
                    break;
                case BoundDeferStatement defer:
                    VisitStatement(defer.Body);
                    break;
            }
        }

        void VisitExpression(BoundExpression expression, bool escaping)
        {
            switch (expression)
            {
                case BoundAddressOfVariableExpression address:
                    if (escaping) Mark(Origins(address));
                    VisitExpression(address.Target, escaping: false);
                    break;
                case BoundNameExpression:
                    if (escaping)
                    {
                        Mark(Origins(expression));
                        RecordDurableAlias(expression);
                    }
                    break;
                case BoundCallExpression call:
                {
                    BoundExpression? receiver = null;
                    string? name = null;
                    switch (call.Target)
                    {
                        case BoundNameExpression target:
                            name = target.Name;
                            break;
                        case BoundMemberAccessExpression member:
                            name = member.MemberName;
                            receiver = member.Target;
                            break;
                        default:
                            VisitExpression(call.Target, escaping: false);
                            break;
                    }

                    var callee = ResolveCallee(name, call.Arguments.Count + (receiver is null ? 0 : 1));
                    var reps = callee is not null && wrapper.TryGetValue(callee, out var current) ? current : null;
                    var functionPointerParameters = call.Target is BoundNameExpression
                        {
                            Type: FunctionPointerType functionPointer
                        }
                        ? functionPointer.ParameterTypes
                        : null;
                    // External generic receivers do not carry a BoundFunction
                    // contract, but their closed type arguments still tell us
                    // that a pointer-valued element slot is durable. Conservatively
                    // raise an address alias supplied to such a receiver. This
                    // preserves alias identity for List<*T>.Add, dictionary-like
                    // APIs, and other BCL containers rather than materializing a
                    // second wrapper or emitting a managed ref into a heap slot.
                    var externalDurableReceiver = reps is null
                        && receiver is not null
                        && CarriesDurablePointerElement(receiver.Type);
                    var index = 0;
                    if (receiver is not null)
                        VisitExpression(receiver, reps is not null && index < reps.Length && reps[index++]);
                    foreach (var argument in call.Arguments)
                    {
                        var argumentEscapes = (reps is not null && index < reps.Length && reps[index])
                            || externalDurableReceiver
                            || (functionPointerParameters is not null
                                && index < functionPointerParameters.Count
                                && functionPointerParameters[index] is HeapPointerBoundType);
                        index++;
                        // `*local` is the call-site ref-pass spelling. When its
                        // selected callee slot is durable, it has the exact same
                        // identity consequence as `&local`: promote the local,
                        // then let emission pass its one __Ptr_T carrier. Treating
                        // the unary node as an ordinary value expression here used
                        // to allocate a second wrapper at the call and silently
                        // split writes from the source local.
                        if (argumentEscapes
                            && argument is BoundUnaryExpression
                            {
                                Op: SyntaxTokenKind.Star,
                                Operand: var refPassOperand,
                            })
                        {
                            if (refPassOperand is BoundNameExpression
                                { Symbol: Esharp.Symbols.LocalSymbol sourceLocal })
                                sourceLocal.AddressEscapes = true;
                            else
                                Mark(Origins(refPassOperand));
                            VisitExpression(refPassOperand, escaping: false);
                            continue;
                        }
                        VisitExpression(argument, argumentEscapes);
                    }
                    break;
                }
                case BoundMemberAccessExpression member:
                    VisitExpression(member.Target, escaping: false);
                    break;
                case BoundIndexExpression index:
                    VisitExpression(index.Target, escaping: false);
                    VisitExpression(index.Index, escaping: false);
                    break;
                case BoundUnaryExpression unary:
                    VisitExpression(unary.Operand, escaping: false);
                    break;
                case BoundBinaryExpression binary:
                    VisitExpression(binary.Left, escaping: false);
                    VisitExpression(binary.Right, escaping: false);
                    break;
                case BoundConversion conversion:
                    VisitExpression(conversion.Operand, escaping);
                    break;
                case BoundConditionalExpression conditional:
                    VisitExpression(conditional.Condition, escaping: false);
                    VisitExpression(conditional.Consequence, escaping);
                    VisitExpression(conditional.Alternative, escaping);
                    break;
                case BoundNullCoalescingExpression coalescing:
                    VisitExpression(coalescing.Left, escaping);
                    VisitExpression(coalescing.Right, escaping);
                    break;
                // `await call(&local)` is still a normal call boundary for
                // pointer realization. Skipping the await wrapper used to leave
                // the caller's local unpromoted while the async callee received
                // a fresh __Ptr_T, silently splitting one source location into
                // two values across suspension.
                case BoundAwaitExpression awaitExpression:
                    VisitExpression(awaitExpression.Inner, escaping);
                    break;
                case BoundObjectCreationExpression creation:
                    foreach (var field in creation.Fields) VisitExpression(field.Value, escaping: true);
                    foreach (var argument in creation.ConstructorArguments) VisitExpression(argument, escaping: true);
                    break;
                case BoundListLiteralExpression list:
                    foreach (var element in list.Elements) VisitExpression(element, escaping: true);
                    break;
                case BoundTupleLiteralExpression tuple:
                    foreach (var element in tuple.Elements) VisitExpression(element, escaping: true);
                    break;
                case BoundFunctionLiteralExpression function:
                    foreach (var capture in function.CapturedVariables)
                        foreach (var (alias, origins) in aliases)
                            if (alias.Name == capture.Name) Mark(origins);
                    break;
                case BoundSpawnExpression spawn:
                    foreach (var capture in spawn.CapturedVariables)
                        foreach (var (alias, origins) in aliases)
                            if (alias.Name == capture.Name) Mark(origins);
                    break;
            }
        }

        VisitStatement(function.Body);

        // A managed local alias that is live on the far side of an await would
        // become an illegal state-machine field. The async pass owns precise
        // lexical liveness (including branches and protected regions), while
        // this pass owns pointer representation. Join the two facts here so
        // `var p = &cell; await ...; p.x` raises both `cell` and `p` together.
        if (function.HasAwait)
        {
            var asyncLiveNames = AwaitPointAnalyzer.Analyze(function.Body, function.Parameters)
                .SpilledLocals
                .Select(local => local.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var (alias, origins) in aliases)
                if (asyncLiveNames.Contains(alias.Name))
                    Mark(origins);
        }

        // A durable pointer is an identity, not a copied pointee.  Once one
        // alias of `&cell` crosses the frame, every live pointer local that
        // denotes that same cell must be wrapper-shaped as well.  Otherwise a
        // later alias can be emitted as `ref Cell` and either produce invalid
        // IL or materialize a second wrapper with the wrong aliasing semantics.
        foreach (var (alias, origins) in aliases)
        {
            if (!origins.Any(origin => origin.AddressEscapes)
                || !aliasElementTypes.TryGetValue(alias, out var elementType))
                continue;
            durableAliases[alias] = new HeapPointerBoundType(elementType);
        }
        return durableAliases;
    }

    // Map of parameter name -> index for every reshapeable `*T` parameter of `f`.
    // A pointer parameter is either the `__Ptr_T` wrapper form (carried in `Type`)
    // or the managed-pointer form (carried by the `ByRef` flag with `Type` = the
    // element type, e.g. `*int`). `readonly *T` is a fixed `in T` borrow and is
    // excluded.
    static Dictionary<string, int> PointerParamPositions(BoundFunctionDeclaration f)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < f.Parameters.Count; i++)
        {
            var p = f.Parameters[i];
            if (p.ReadOnlyByRef) continue;
            if (p.Type is HeapPointerBoundType || p.ByRef)
                result[p.Name] = i;
        }
        return result;
    }

    // === Rewrite ===

    static BoundFunctionDeclaration RewriteFunction(BoundFunctionDeclaration f, bool[] wrapper)
    {
        var changedNames = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        var newParams = new List<BoundParameter>(f.Parameters.Count);
        for (var i = 0; i < f.Parameters.Count; i++)
        {
            var p = f.Parameters[i];
            var isPtr = !p.ReadOnlyByRef && (p.Type is HeapPointerBoundType || p.ByRef);
            if (!isPtr) { newParams.Add(p); continue; }

            var inner = p.Type is HeapPointerBoundType hp ? hp.Inner : p.Type;
            var currentlyWrapper = p.Type is HeapPointerBoundType;
            if (wrapper[i] == currentlyWrapper) { newParams.Add(p); continue; } // representation unchanged

            if (wrapper[i])
            {
                // Upgrade managed pointer → `__Ptr_T` wrapper (the param escapes / is nullable).
                var t = new HeapPointerBoundType(inner);
                newParams.Add(new BoundParameter(p.Name, t, ByRef: false));
                changedNames[p.Name] = t; // name was the bare element type; now the wrapper
            }
            else
            {
                // Downgrade `__Ptr_T` wrapper → managed pointer `ref T` (provably local).
                newParams.Add(new BoundParameter(p.Name, inner, ByRef: true));
                changedNames[p.Name] = inner; // name now denotes the value behind a managed pointer
            }
        }

        if (changedNames.Count == 0) return f;
        var newBody = (BoundBlockStatement)RetypeNames.Rewrite(f.Body, changedNames);
        return f with { Parameters = newParams, Body = newBody };
    }
}

/// <summary>
/// Reconciles the bound signature carried by <c>&amp;function</c> with the
/// interprocedural representation selected for that function. Function-pointer
/// signatures are CLR call-site contracts, so they cannot retain the binder's
/// provisional <c>T&amp;</c> spelling after the target has been raised to
/// <c>__Ptr_T</c>.
/// </summary>
sealed class FunctionPointerSignatureRewriter(
    IReadOnlyDictionary<string, List<BoundFunctionDeclaration>> byName,
    IReadOnlyDictionary<BoundFunctionDeclaration, BoundFunctionDeclaration> rewritten)
    : BoundTreeRewriter
{
    readonly Dictionary<Esharp.Symbols.LocalSymbol, FunctionPointerType> _localSignatures
        = new(ReferenceEqualityComparer.Instance);

    public static HashSet<(string Name, int Arity)> CollectAddressTakenFunctions(
        IEnumerable<BoundFunctionDeclaration> functions)
    {
        var collector = new AddressTakenCollector();
        foreach (var function in functions)
            collector.VisitNode(function.Body);
        return collector.Functions;
    }

    public static BoundFunctionDeclaration Rewrite(
        BoundFunctionDeclaration function,
        IReadOnlyDictionary<string, List<BoundFunctionDeclaration>> byName,
        IReadOnlyDictionary<BoundFunctionDeclaration, BoundFunctionDeclaration> rewritten)
    {
        var rewriter = new FunctionPointerSignatureRewriter(byName, rewritten);
        var body = (BoundBlockStatement)rewriter.RewriteStatement(function.Body);
        return ReferenceEquals(body, function.Body) ? function : function with { Body = body };
    }

    public override BoundExpression RewriteExpression(BoundExpression expression)
    {
        if (expression is BoundAddressOfExpression address
            && ResolveCallee(address.FunctionName, address.ParameterTypes.Count) is { } original
            && rewritten.TryGetValue(original, out var final))
        {
            var parameters = final.Parameters.Select(ToFunctionPointerParameter).ToList();
            return new BoundAddressOfExpression(address.FunctionName, parameters, final.ReturnType)
            {
                Span = address.Span,
            };
        }
        return base.RewriteExpression(expression);
    }

    protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
    {
        var rewrittenNode = (BoundVariableDeclaration)base.RewriteVariableDeclaration(node);
        if (rewrittenNode.Initializer.Type is not FunctionPointerType signature)
            return rewrittenNode;

        if (rewrittenNode.Local is { } local)
            _localSignatures[local] = signature;

        return rewrittenNode.DeclaredType is FunctionPointerType
            ? rewrittenNode with { DeclaredType = signature }
            : rewrittenNode;
    }

    protected override BoundExpression RewriteNameExpression(BoundNameExpression node)
    {
        if (node.Symbol is Esharp.Symbols.LocalSymbol local
            && _localSignatures.TryGetValue(local, out var signature))
        {
            return new BoundNameExpression(node.Name, signature)
            {
                Symbol = node.Symbol,
                Span = node.Span,
            };
        }
        return node;
    }

    BoundFunctionDeclaration? ResolveCallee(string name, int arity) =>
        byName.TryGetValue(name, out var overloads)
            ? overloads.FirstOrDefault(candidate => candidate.Parameters.Count == arity)
                ?? (overloads.Count == 1 ? overloads[0] : null)
            : null;

    static BoundType ToFunctionPointerParameter(BoundParameter parameter) => parameter.ByRef
        ? new ByRefBoundType(parameter.Type)
        : parameter.Type;

    sealed class AddressTakenCollector : BoundTreeVisitor
    {
        public HashSet<(string Name, int Arity)> Functions { get; } = [];

        protected override void VisitAddressOfExpression(BoundAddressOfExpression node)
        {
            Functions.Add((node.FunctionName, node.ParameterTypes.Count));
            base.VisitAddressOfExpression(node);
        }
    }
}

/// <summary>
/// Final source-level containment for the managed-borrow / durable-pointer split.
/// A verifier failure is an indispensable backstop, but it has no source span and
/// arrives after emission.  This check is intentionally narrow: direct <c>&amp;x</c>
/// is a legal boundary form that the emitter turns into a carrier, while a surviving
/// named <c>ref T</c> alias at a durable sink means pointer realization missed an
/// identity edge and must be reported before code generation.
/// </summary>
static class PointerLifetimeContainment
{
    public static void Validate(
        IEnumerable<BoundFunctionDeclaration> functions,
        DiagnosticBag diagnostics)
    {
        foreach (var function in functions)
            VisitStatement(function.Body, function.ReturnType);

        void VisitStatement(BoundStatement statement, BoundType returnType)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements) VisitStatement(child, returnType);
                    break;
                case BoundVariableDeclaration { DeclaredType: HeapPointerBoundType, Initializer: var initializer }:
                    Diagnose(initializer);
                    VisitExpression(initializer);
                    break;
                case BoundVariableDeclaration declaration:
                    VisitExpression(declaration.Initializer);
                    break;
                case BoundAssignment assignment:
                    VisitExpression(assignment.Target);
                    if (assignment.Target is BoundMemberAccessExpression or BoundIndexExpression)
                        Diagnose(assignment.Value);
                    VisitExpression(assignment.Value);
                    break;
                case BoundCompoundAssignment compound:
                    VisitExpression(compound.Target);
                    VisitExpression(compound.Value);
                    break;
                case BoundReturnStatement { Expression: { } returned }:
                    // A pointer local read from an ordinary value-returning
                    // function is an implicit pointee read, not a pointer
                    // escape. Match the escape analysis' boundary rule so the
                    // defensive diagnostic never rejects verified ref-local
                    // code such as `func f() -> int { return p }`.
                    if (returnType is HeapPointerBoundType or ByRefBoundType)
                        Diagnose(returned);
                    VisitExpression(returned);
                    break;
                case BoundThrowStatement { Expression: { } thrown }:
                    Diagnose(thrown);
                    VisitExpression(thrown);
                    break;
                case BoundExpressionStatement expression:
                    VisitExpression(expression.Expression);
                    break;
                case BoundIfStatement conditional:
                    VisitExpression(conditional.Condition);
                    VisitStatement(conditional.Then, returnType);
                    if (conditional.Else is not null) VisitStatement(conditional.Else, returnType);
                    break;
                case BoundWhileStatement loop:
                    VisitExpression(loop.Condition);
                    VisitStatement(loop.Body, returnType);
                    break;
                case BoundForEachStatement each:
                    VisitExpression(each.Collection);
                    VisitStatement(each.Body, returnType);
                    break;
                case BoundTryStatement attempt:
                    VisitStatement(attempt.Body, returnType);
                    foreach (var clause in attempt.Catches) VisitStatement(clause.Body, returnType);
                    break;
                case BoundDeferStatement deferred:
                    VisitStatement(deferred.Body, returnType);
                    break;
            }
        }

        void VisitExpression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundCallExpression call:
                    VisitExpression(call.Target);
                    foreach (var argument in call.Arguments) VisitExpression(argument);
                    break;
                case BoundMemberAccessExpression member:
                    VisitExpression(member.Target);
                    break;
                case BoundIndexExpression index:
                    VisitExpression(index.Target);
                    VisitExpression(index.Index);
                    break;
                case BoundUnaryExpression unary:
                    VisitExpression(unary.Operand);
                    break;
                case BoundBinaryExpression binary:
                    VisitExpression(binary.Left);
                    VisitExpression(binary.Right);
                    break;
                case BoundConversion conversion:
                    VisitExpression(conversion.Operand);
                    break;
                case BoundConditionalExpression conditional:
                    VisitExpression(conditional.Condition);
                    VisitExpression(conditional.Consequence);
                    VisitExpression(conditional.Alternative);
                    break;
                case BoundNullCoalescingExpression coalescing:
                    VisitExpression(coalescing.Left);
                    VisitExpression(coalescing.Right);
                    break;
                case BoundObjectCreationExpression creation:
                    foreach (var field in creation.Fields)
                    {
                        Diagnose(field.Value);
                        VisitExpression(field.Value);
                    }
                    foreach (var argument in creation.ConstructorArguments)
                    {
                        Diagnose(argument);
                        VisitExpression(argument);
                    }
                    break;
                case BoundListLiteralExpression list:
                    foreach (var element in list.Elements)
                    {
                        Diagnose(element);
                        VisitExpression(element);
                    }
                    break;
                case BoundTupleLiteralExpression tuple:
                    foreach (var element in tuple.Elements)
                    {
                        Diagnose(element);
                        VisitExpression(element);
                    }
                    break;
            }
        }

        void Diagnose(BoundExpression expression)
        {
            var name = UnwrapManagedAlias(expression);
            if (name is null) return;
            diagnostics.Report(name.Span, DiagnosticDescriptors.UnrealizedDurablePointerAlias, name.Name);
        }

        static BoundNameExpression? UnwrapManagedAlias(BoundExpression expression) => expression switch
        {
            BoundNameExpression { Type: ByRefBoundType } name => name,
            BoundConversion conversion => UnwrapManagedAlias(conversion.Operand),
            _ => null,
        };
    }
}

/// Collects, for a function body, which tracked `*T` names *escape* the frame
/// (their pointer value leaks out) and which are *nullable* (touch `nil`). The
/// walker is context-sensitive: a name read through a member access (`p.x`),
/// dereference (`*p`), or as a method receiver into a managed-pointer parameter
/// does NOT escape — only a name whose pointer value flows into an escaping sink
/// (return, assignment to a field/var, collection, capture, or a wrapper-form
/// callee parameter) does.
internal sealed class EscapeFacts
{
    public readonly HashSet<string> Escaped = new(StringComparer.Ordinal);
    public readonly HashSet<string> Nullable = new(StringComparer.Ordinal);

    readonly HashSet<string> _tracked;
    readonly Dictionary<string, List<BoundFunctionDeclaration>> _byName;
    readonly Dictionary<BoundFunctionDeclaration, bool[]> _wrapper;
    // Local pointer aliases are still frame-local.  Preserve the tracked
    // parameter's identity through `var alias = p` so only a later durable use
    // raises p to the heap carrier.
    readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public EscapeFacts(HashSet<string> tracked,
        Dictionary<string, List<BoundFunctionDeclaration>> byName,
        Dictionary<BoundFunctionDeclaration, bool[]> wrapper)
    {
        _tracked = tracked;
        _byName = byName;
        _wrapper = wrapper;
    }

    public void Walk(BoundStatement stmt) => Stmt(stmt);

    void Stmt(BoundStatement stmt)
    {
        switch (stmt)
        {
            case BoundBlockStatement b:
                foreach (var s in b.Statements) Stmt(s);
                break;
            case BoundVariableDeclaration v:
                RecordAlias(v.Name, v.Initializer);
                Expr(v.Initializer, escaping: false);
                break;
            case BoundLetGuard g:
                Expr(g.Initializer, escaping: true);
                Stmt(g.ElseBody);
                break;
            case BoundAssignment a:
                Expr(a.Target, escaping: false); // lvalue: `p.x = ...` derefs p, no escape
                if (a.Target is BoundNameExpression tn && TryTrackedRoot(tn, out var assignedRoot) && IsNull(a.Value))
                    Nullable.Add(assignedRoot);     // `p = nil` (including an alias of p)
                if (a.Target is BoundNameExpression targetName)
                    RecordAlias(targetName.Name, a.Value);
                Expr(a.Value, escaping: a.Target is BoundMemberAccessExpression);
                break;
            case BoundCompoundAssignment ca:
                Expr(ca.Target, escaping: false);
                Expr(ca.Value, escaping: false);
                break;
            case BoundIfStatement i:
                Expr(i.Condition, escaping: false);
                Stmt(i.Then);
                if (i.Else is not null) Stmt(i.Else);
                break;
            case BoundWhileStatement w:
                Expr(w.Condition, escaping: false);
                Stmt(w.Body);
                break;
            case BoundForEachStatement fe:
                Expr(fe.Collection, escaping: false);
                Stmt(fe.Body);
                break;
            case BoundReturnStatement r:
                if (r.Expression is not null) Expr(r.Expression, escaping: true);
                break;
            case BoundExpressionStatement e:
                Expr(e.Expression, escaping: false);
                break;
            case BoundMatchStatement m:
                Expr(m.Subject, escaping: false);
                foreach (var arm in m.Arms) Stmt(arm.Body);
                break;
            case BoundTryStatement tr:
                Stmt(tr.Body);
                foreach (var c in tr.Catches) Stmt(c.Body);
                break;
            case BoundDeferStatement d:
                Stmt(d.Body);
                break;
            case BoundThrowStatement th:
                if (th.Expression is not null) Expr(th.Expression, escaping: true);
                break;
            case BoundSelectStatement sel:
                foreach (var arm in sel.Arms)
                {
                    if (arm.Channel is not null) Expr(arm.Channel, escaping: false);
                    if (arm.Value is not null) Expr(arm.Value, escaping: true);
                    Stmt(arm.Body);
                }
                break;
        }
    }

    void Expr(BoundExpression expr, bool escaping)
    {
        switch (expr)
        {
            case BoundNameExpression name:
                if (escaping && TryTrackedRoot(name, out var root)) Escaped.Add(root);
                break;

            case BoundMemberAccessExpression ma:
                Expr(ma.Target, escaping: false);   // p.x derefs p
                break;
            case BoundNullConditionalAccessExpression nca:
                Expr(nca.Target, escaping: false);
                break;
            case BoundIndexExpression idx:
                Expr(idx.Target, escaping: false);
                Expr(idx.Index, escaping: false);
                break;
            case BoundUnaryExpression u:
                // `*p` / `&p` / arithmetic — the operand value is used, not leaked.
                Expr(u.Operand, escaping: false);
                break;

            case BoundBinaryExpression b:
                // nil comparison marks the pointer nullable (a managed pointer can't be null).
                if (b.Op is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals)
                {
                    if (IsNull(b.Right) && TryTrackedRoot(b.Left, out var leftRoot)) Nullable.Add(leftRoot);
                    if (IsNull(b.Left) && TryTrackedRoot(b.Right, out var rightRoot)) Nullable.Add(rightRoot);
                }
                Expr(b.Left, escaping: false);
                Expr(b.Right, escaping: false);
                break;

            case BoundCallExpression call:
                VisitCall(call);
                break;

            case BoundHeapAllocExpression ha:
                Expr(ha.Inner, escaping: escaping); // &T{...} — its field initializers escape into the heap cell
                break;
            case BoundAddressOfVariableExpression aov:
                Expr(aov.Target, escaping: false); // &x reads the lvalue; the local-hoist analysis tracks this separately
                break;
            case BoundObjectCreationExpression oc:
                foreach (var f in oc.Fields) Expr(f.Value, escaping: true); // stored into a field
                break;
            case BoundWithExpression we:
                Expr(we.Target, escaping: false);
                foreach (var f in we.Fields) Expr(f.Value, escaping: true);
                break;
            case BoundListLiteralExpression list:
                foreach (var el in list.Elements) Expr(el, escaping: true); // stored in a collection
                break;
            case BoundTupleLiteralExpression tup:
                foreach (var el in tup.Elements) Expr(el, escaping: true);
                break;

            case BoundConditionalExpression cond:
                Expr(cond.Condition, escaping: false);
                Expr(cond.Consequence, escaping);
                Expr(cond.Alternative, escaping);
                break;
            case BoundNullCoalescingExpression nc:
                Expr(nc.Left, escaping);
                Expr(nc.Right, escaping);
                break;
            // BoundParenthesizedExpression is dropped in the frozen contract (binder
            // unwraps parenthesized expressions in-place, never inserting them into
            // the bound tree). BoundConversion covers Narrow/safe/assert casts.
            case BoundConversion conv:
                // A conversion doesn't leak the pointer itself; what leaks is the
                // inner operand if it ends up stored/returned in the escaping context.
                Expr(conv.Operand, escaping);
                break;
            case BoundInterpolatedStringExpression interp:
                foreach (var part in interp.Parts)
                    if (part.Expr is not null) Expr(part.Expr, escaping: false);
                break;

            case BoundAwaitExpression aw:
                Expr(aw.Inner, escaping);
                break;
            case BoundTryUnwrapExpression tu:
                Expr(tu.Inner, escaping);
                break;
            case BoundResultCallExpression rc:
                Expr(rc.Argument, escaping: true); // wrapped into a Result that flows out
                break;
            case BoundDotCaseExpression dc:
                foreach (var a in dc.Arguments) Expr(a, escaping: true);
                break;
            case BoundRangeExpression range:
                if (range.Target is not null) Expr(range.Target, escaping: false);
                if (range.Start is not null) Expr(range.Start, escaping: false);
                if (range.End is not null) Expr(range.End, escaping: false);
                break;

            case BoundSpawnExpression sp:
                foreach (var cap in sp.CapturedVariables)
                    if (_tracked.Contains(cap.Name)) Escaped.Add(cap.Name); // captured across a thread boundary
                Stmt(sp.Body);
                break;
            case BoundFunctionLiteralExpression fl:
                foreach (var cap in fl.CapturedVariables)
                    if (_tracked.Contains(cap.Name)) Escaped.Add(cap.Name); // captured by a closure
                Stmt(fl.Body);
                break;
            case BoundChanCreationExpression chan:
                if (chan.Capacity is not null) Expr(chan.Capacity, escaping: false);
                break;
        }
    }

    void VisitCall(BoundCallExpression call)
    {
        // Resolve the callee's parameter representations so each argument escapes
        // iff it flows into a wrapper-form parameter. A receiver of a promoted
        // method call is argument 0.
        BoundExpression? receiver = null;
        string? calleeName = null;
        switch (call.Target)
        {
            case BoundNameExpression n:
                calleeName = n.Name;
                break;
            case BoundMemberAccessExpression ma:
                calleeName = ma.MemberName;
                receiver = ma.Target;
                break;
        }

        var callee = ResolveCallee(calleeName, (receiver is null ? 0 : 1) + call.Arguments.Count);
        var reps = callee is not null && _wrapper.TryGetValue(callee, out var w) ? w : null;

        // Soundness: a *known* user function with no resolvable overload (ambiguous
        // arity) could store the pointer — treat its arguments as escaping. A name
        // not in the module is an external/BCL call, assumed managed-pointer-
        // compatible (`ref`/`in`), so its pointer arguments do not escape.
        var unresolvedUser = calleeName is not null && callee is null && _byName.ContainsKey(calleeName);

        bool ArgEscapes(int idx) => unresolvedUser || (reps is not null && idx < reps.Length && reps[idx]);

        var paramIdx = 0;
        if (receiver is not null)
            Expr(receiver, escaping: ArgEscapes(paramIdx++));
        foreach (var arg in call.Arguments)
        {
            var escapes = ArgEscapes(paramIdx++);
            // E# spells a pointer-parameter forward as `callee(*p)`. The unary
            // ref-pass is normally a pointee use, but it becomes a durable flow
            // precisely when the selected callee parameter is __Ptr_T. Raise the
            // tracked source parameter here so the next fixed-point iteration
            // gives this function the same wrapper ABI instead of forwarding a
            // Cell& into a __Ptr_Cell slot.
            if (escapes
                && arg is BoundUnaryExpression
                {
                    Op: SyntaxTokenKind.Star,
                    Operand: var refPassOperand,
                }
                && TryTrackedRoot(refPassOperand, out var root))
                Escaped.Add(root);
            Expr(arg, escaping: escapes);
        }
    }

    BoundFunctionDeclaration? ResolveCallee(string? name, int argCount)
    {
        if (name is null || !_byName.TryGetValue(name, out var overloads)) return null;
        // Prefer an exact arity match; fall back to the sole overload.
        return overloads.FirstOrDefault(o => o.Parameters.Count == argCount)
            ?? (overloads.Count == 1 ? overloads[0] : null);
    }

    static bool IsNull(BoundExpression e) =>
        e is BoundLiteralExpression { Value: null } || e.Type is NullType;

    void RecordAlias(string name, BoundExpression value)
    {
        if (TryTrackedRoot(value, out var root)) _aliases[name] = root;
        else _aliases.Remove(name);
    }

    bool TryTrackedRoot(BoundExpression expression, out string root)
    {
        switch (expression)
        {
            case BoundNameExpression name when _tracked.Contains(name.Name):
                root = name.Name;
                return true;
            case BoundNameExpression name when _aliases.TryGetValue(name.Name, out var alias):
                root = alias;
                return true;
            case BoundConversion conversion:
                return TryTrackedRoot(conversion.Operand, out root);
            default:
                root = "";
                return false;
        }
    }
}

/// Rebuilds a bound subtree, replacing the static type of every `BoundNameExpression`
/// whose name is in `changed`. Used after escape analysis reshapes a parameter's
/// representation so that references to it carry the new pointer type and the
/// emitter dispatches member access / slots correctly. Member-access and call
/// node types are field/return types and are preserved structurally.
internal static class RetypeNames
{
    public static BoundStatement Rewrite(BoundStatement stmt, Dictionary<string, BoundType> changed)
    {
        if (changed.Count == 0) return stmt;
        var r = S(stmt, changed);
        r.Span = stmt.Span;
        return r;
    }

    static BoundStatement S(BoundStatement stmt, Dictionary<string, BoundType> c)
    {
        BoundStatement result = stmt switch
        {
            BoundBlockStatement b => new BoundBlockStatement(b.Statements.Select(s => S(s, c)).ToList()),
            BoundVariableDeclaration v => new BoundVariableDeclaration(v.Mutable, v.Name, v.DeclaredType, E(v.Initializer, c)),
            BoundLetGuard g => new BoundLetGuard(g.Name, g.DeclaredType, E(g.Initializer, c), (BoundBlockStatement)S(g.ElseBody, c)),
            BoundAssignment a => new BoundAssignment(E(a.Target, c), E(a.Value, c)),
            BoundCompoundAssignment ca => new BoundCompoundAssignment(E(ca.Target, c), ca.Op, E(ca.Value, c)),
            BoundIfStatement i => new BoundIfStatement(E(i.Condition, c), S(i.Then, c), i.Else is null ? null : S(i.Else, c)),
            BoundWhileStatement w => new BoundWhileStatement(E(w.Condition, c), S(w.Body, c)),
            BoundForEachStatement fe => new BoundForEachStatement(fe.Identifier, E(fe.Collection, c), S(fe.Body, c), fe.ElementType, fe.DestructuredNames, fe.IsAwait),
            BoundReturnStatement r => new BoundReturnStatement(r.Expression is null ? null : E(r.Expression, c)),
            BoundExpressionStatement e => new BoundExpressionStatement(E(e.Expression, c)),
            BoundMatchStatement m => new BoundMatchStatement(E(m.Subject, c), m.SubjectType,
                m.Arms.Select(a => new BoundMatchArm(a.Pattern, (BoundBlockStatement)S(a.Body, c))).ToList()),
            BoundTryStatement tr => new BoundTryStatement((BoundBlockStatement)S(tr.Body, c),
                tr.Catches.Select(cc => new BoundCatchClause(cc.ExceptionType, cc.BindingName, (BoundBlockStatement)S(cc.Body, c), cc.Guard is null ? null : E(cc.Guard, c))).ToList()),
            BoundDeferStatement d => new BoundDeferStatement((BoundBlockStatement)S(d.Body, c)),
            BoundThrowStatement th => new BoundThrowStatement(th.Expression is null ? null : E(th.Expression, c)),
            _ => stmt,
        };
        result.Span = stmt.Span;
        return result;
    }

    static BoundExpression E(BoundExpression e, Dictionary<string, BoundType> c) => e switch
    {
        BoundNameExpression n when c.TryGetValue(n.Name, out var t) => new BoundNameExpression(n.Name, t)
        {
            Symbol = n.Symbol,
            Span = n.Span,
        },
        BoundNameExpression => e,
        BoundUnaryExpression u => new BoundUnaryExpression(u.Op, E(u.Operand, c), u.Type),
        BoundBinaryExpression b => new BoundBinaryExpression(E(b.Left, c), b.Op, E(b.Right, c), b.Type),
        BoundMemberAccessExpression ma => new BoundMemberAccessExpression(E(ma.Target, c), ma.MemberName, ma.Type),
        BoundNullConditionalAccessExpression nca => new BoundNullConditionalAccessExpression(E(nca.Target, c), nca.MemberName, nca.Type),
        BoundCallExpression call => new BoundCallExpression(E(call.Target, c), call.Arguments.Select(a => E(a, c)).ToList(), call.Type, call.ExplicitTypeArguments),
        BoundObjectCreationExpression oc => new BoundObjectCreationExpression(oc.ObjectType, oc.Fields.Select(f => new BoundFieldInit(f.Name, E(f.Value, c))).ToList()),
        BoundWithExpression we => new BoundWithExpression(E(we.Target, c), we.Fields.Select(f => new BoundFieldInit(f.Name, E(f.Value, c))).ToList(), we.Type),
        // BoundParenthesizedExpression is dropped — binder unwraps in-place.
        BoundConversion conv => new BoundConversion(E(conv.Operand, c), conv.TargetType, conv.Kind),
        BoundConditionalExpression cnd => new BoundConditionalExpression(E(cnd.Condition, c), E(cnd.Consequence, c), E(cnd.Alternative, c), cnd.Type),
        BoundNullCoalescingExpression nc => new BoundNullCoalescingExpression(E(nc.Left, c), E(nc.Right, c), nc.Type),
        BoundListLiteralExpression l => new BoundListLiteralExpression(l.Elements.Select(x => E(x, c)).ToList(), l.ElementType, l.Type),
        BoundTupleLiteralExpression t => new BoundTupleLiteralExpression(t.Elements.Select(x => E(x, c)).ToList(), t.Type),
        BoundIndexExpression idx => new BoundIndexExpression(E(idx.Target, c), E(idx.Index, c), idx.Type),
        BoundRangeExpression rg => new BoundRangeExpression(rg.Target is null ? null : E(rg.Target, c), rg.Start is null ? null : E(rg.Start, c), rg.End is null ? null : E(rg.End, c), rg.Type),
        BoundDotCaseExpression dc => new BoundDotCaseExpression(dc.CaseName, dc.ResolvedTypeName, dc.Arguments.Select(a => E(a, c)).ToList(), dc.Type),
        BoundAwaitExpression aw => new BoundAwaitExpression(E(aw.Inner, c), aw.Type),
        BoundTryUnwrapExpression tu => new BoundTryUnwrapExpression(E(tu.Inner, c), tu.UnwrappedType, tu.TempName),
        BoundResultCallExpression rc => new BoundResultCallExpression(rc.IsOk, E(rc.Argument, c), rc.OkType, rc.ErrorType),
        BoundInterpolatedStringExpression interp => new BoundInterpolatedStringExpression(
            interp.Parts.Select(part => part.Expr is null ? part : new BoundInterpolationPart(part.Literal, E(part.Expr, c))).ToList(), interp.Type),
        _ => e,
    };
}
