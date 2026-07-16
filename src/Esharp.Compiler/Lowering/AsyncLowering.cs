using Esharp.BoundTree;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// Lowers every async function (<see cref="BoundFunctionDeclaration.HasAwait"/> == true)
/// into a state-machine struct + a synchronous stub that starts the machine.
///
/// <para>
/// For each async function this pass produces:
/// <list type="number">
///   <item>
///     A synthesized state-machine struct TypeSymbol (via
///     <see cref="SynthesizedSymbolSink.SynthesizeStateMachineStruct"/>) carrying:
///     <list type="bullet">
///       <item><c>_state: int</c> — current resume point; -1 = not started, -2 = done</item>
///       <item><c>_builder</c> — the async method builder (type driven by
///             <see cref="AsyncReturnShape"/>)</item>
///       <item>One field per original parameter (copied at call time by the stub)</item>
///       <item>One spill field per local live across an await point</item>
///       <item>One awaiter field per distinct await site (<c>_awaiter_N</c>)</item>
///     </list>
///   </item>
///   <item>
///     A MoveNext method body synthesized in CORE bound-tree nodes. The body is a
///     state-dispatch if-chain; each await point becomes a suspend/resume block:
///     <list type="bullet">
///       <item>On entry: dispatch on <c>_state</c> to the matching resume point</item>
///       <item>At each await site N:
///         spill live locals → struct fields,
///         get awaiter, if not completed: set state = N, AwaitUnsafeOnCompleted, return;
///         resume: GetResult</item>
///       <item>At the end: <c>_builder.SetResult(result)</c></item>
///       <item>In the surrounding try/catch: <c>_builder.SetException(ex)</c></item>
///     </list>
///   </item>
///   <item>SetStateMachine body = empty (struct-based SM; never called by the runtime).</item>
///   <item>
///     A replacement synchronous stub function (same name/visibility/params, HasAwait=false):
///     creates the SM struct, sets builder, calls builder.Start&lt;SM&gt;(ref sm), returns builder.Task.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// After this pass, no <see cref="BoundAwaitExpression"/> survives at the top level.
/// CodeGen emits the synthesized struct's methods directly — no special async scaffolding
/// needed in the emitter.
/// </para>
/// </summary>
public sealed class AsyncLowering : IBoundTreePass
{
    public static readonly AsyncLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
    {
        var changed  = false;
        var newUnits = new List<BoundCompilationUnit>(program.Units.Count);

        foreach (var unit in program.Units)
        {
            var synthDecls  = new List<BoundMember>();
            var newMembers  = new List<BoundMember>(unit.Members.Count);
            var unitChanged = false;

            foreach (var member in unit.Members)
            {
                switch (member)
                {
                    case BoundFunctionDeclaration fn when fn.HasAwait:
                    {
                        var (stub, smDecl) = LowerAsyncFunction(fn, sink);
                        newMembers.Add(stub);
                        synthDecls.Add(smDecl);
                        unitChanged = true;
                        break;
                    }
                    case BoundDataDeclaration data:
                    {
                        var (newData, extraSynth) = LowerDataDeclaration(data, sink);
                        newMembers.Add(newData);
                        synthDecls.AddRange(extraSynth);
                        if (!ReferenceEquals(newData, data)) unitChanged = true;
                        break;
                    }
                    case BoundStaticFuncDeclaration sf:
                    {
                        // A `static func` is an emitted CLR static class, but its
                        // member bodies are ordinary E# functions.  Lower awaits in
                        // those bodies exactly as we do for top-level functions;
                        // otherwise helpers such as TaskScope.RunAsync and
                        // ChanSelect.SelectAsync retain FEATURE await nodes all the
                        // way to CodeGen.
                        var functions = new List<BoundFunctionDeclaration>(sf.Functions.Count);
                        var loweredAny = false;
                        foreach (var fn in sf.Functions)
                        {
                            if (!fn.HasAwait)
                            {
                                functions.Add(fn);
                                continue;
                            }

                            var (stub, smDecl) = LowerAsyncFunction(fn, sink);
                            functions.Add(stub);
                            synthDecls.Add(smDecl);
                            loweredAny = true;
                        }

                        newMembers.Add(loweredAny ? sf with { Functions = functions } : sf);
                        unitChanged |= loweredAny;
                        break;
                    }
                    default:
                        newMembers.Add(member);
                        break;
                }
            }

            if (unitChanged)
            {
                newMembers.AddRange(synthDecls);
                newUnits.Add(unit with { Members = newMembers });
                changed = true;
            }
            else
            {
                newUnits.Add(unit);
            }
        }

        return changed ? program with { Units = newUnits } : program;
    }

    // ─── Data declaration ─────────────────────────────────────────────────────

    static (BoundDataDeclaration Result, List<BoundMember> ExtraSynth)
        LowerDataDeclaration(BoundDataDeclaration data, SynthesizedSymbolSink sink)
    {
        if (data.InstanceMethods is not { Count: > 0 } methods)
            return (data, []);

        var newMethods = new List<BoundFunctionDeclaration>(methods.Count);
        var extraSynth = new List<BoundMember>();
        var changed    = false;

        foreach (var fn in methods)
        {
            if (fn.HasAwait)
            {
                var (stub, smDecl) = LowerAsyncFunction(fn, sink, data.TypeParameters);
                extraSynth.Add(smDecl);
                newMethods.Add(stub);
                changed = true;
            }
            else
            {
                newMethods.Add(fn);
            }
        }

        if (!changed) return (data, []);
        return (data with { InstanceMethods = newMethods }, extraSynth);
    }

    // ─── Core: single async function → stub + SM struct declaration ───────────

    static (BoundFunctionDeclaration Stub, BoundDataDeclaration SmDecl)
        LowerAsyncFunction(BoundFunctionDeclaration fn, SynthesizedSymbolSink sink,
            IReadOnlyList<string>? containingTypeParameters = null)
    {
        // The backend's slot table is name-keyed, while the binder carries the
        // actual lexical identity on LocalSymbol. Give only colliding async
        // locals a private lowered spelling before liveness and state-machine
        // synthesis, so an inner `value` can never overwrite outer `value`.
        var uniquelyNamedBody = AsyncLocalShadowRenaming.Rewrite(fn.Body);
        if (!ReferenceEquals(uniquelyNamedBody, fn.Body))
            fn = fn with { Body = uniquelyNamedBody };

        var shape      = fn.AsyncShape;
        var resultType = UnwrapResultType(fn.ReturnType, shape);
        // A nested state-machine type must carry the generic parameters from both
        // its declaring data type and the method. Without those formal parameters,
        // fields such as `self: Ch<T>` and `AsyncTaskMethodBuilder<T>` resolve after
        // lowering as open `Ch` / object, which is unverifiable IL.
        var stateMachineTypeParameters = (containingTypeParameters ?? [])
            .Concat(fn.TypeParameters.Where(p => !(containingTypeParameters ?? []).Contains(p, StringComparer.Ordinal)))
            .ToList();
        // Type-body receiver syntax is initially an open `Owner` placeholder. The
        // state machine has to capture the owner's self-instantiation (`Owner<T>`),
        // otherwise both the generated field and every rewritten `self.member`
        // access carry an invalid open generic receiver.
        var stateMachineParameters = fn.Parameters.Select((p, index) =>
        {
            if (index == 0 && p.Name == "self" && containingTypeParameters is { Count: > 0 }
                && p.Type is DataType data)
            {
                return p with
                {
                    Type = data with
                    {
                        TypeArguments = containingTypeParameters
                            .Select(name => (BoundType)new PrimitiveType(name)).ToList(),
                    },
                };
            }
            return p;
        }).ToList();
        var parameters = stateMachineParameters.Select(p => (p.Name, p.Type)).ToList();

        // A CLR async-state-machine field cannot be a managed byref.  A durable
        // property location that crosses an await is therefore raised before
        // liveness analysis: the machine stores the evaluated property receiver
        // and re-enters the property's get/set/loca protocol at each use.  This is
        // deliberately distinct from `*T`; class-valued properties never produce
        // a source-visible or metadata-visible `*Class`.
        var preliminaryAnalysis = AwaitPointAnalyzer.Analyze(fn.Body);
        var raisedBody = AsyncPropertyLocationRaising.Rewrite(
            fn.Body,
            preliminaryAnalysis.SpilledLocals.Select(local => local.Name).ToHashSet(StringComparer.Ordinal));
        if (!ReferenceEquals(raisedBody, fn.Body))
            fn = fn with { Body = raisedBody };

        var stateMachineFunction = fn with { Parameters = stateMachineParameters };

        // 1. Analyse the raised body for spilled locals and await sites.
        var analysis = AwaitPointAnalyzer.Analyze(fn.Body);

        // 2. Mint the state-machine TypeSymbol (placeholder body — overwritten below).
        var (smSym, smBoundView, moveNextSym, setSmSym) = sink.SynthesizeStateMachineStruct(
            methodName:   fn.Name,
            shape:        shape,
            resultType:   resultType,
            parameters:   parameters,
            typeParameters: stateMachineTypeParameters,
            originalBody: new BoundBlockStatement([])   // placeholder; overwritten
        );

        // 3. Hoist liveness-selected locals → struct fields. A local whose
        // explicit address already escaped cannot be copied as a raw value when
        // it crosses an await: callers and callee-side `*T` parameters must keep
        // seeing the exact same durable cell. Store its __Ptr_T carrier in state
        // instead; CodeGen maps the source name through Value on that carrier.
        var escapedSpilledLocals = FindEscapedSpilledLocals(fn.Body, analysis.SpilledLocals);
        foreach (var (localName, localType) in analysis.SpilledLocals)
        {
            var stateType = escapedSpilledLocals.Contains(localName)
                ? (BoundType)new HeapPointerBoundType(localType)
                : localType;
            smSym.AddField(SpillField(SpillFieldName(localName), smSym, stateType));
        }

        // 4. Mint one awaiter field per await site.
        for (var i = 0; i < analysis.AwaitSites.Count; i++)
        {
            var site         = analysis.AwaitSites[i];
            var awaiterBound = AwaiterBoundType(site.AwaitableType);
            smSym.AddField(SpillField($"_awaiter_{i}", smSym, awaiterBound));
        }

        // 5. Build the MoveNext body in CORE bound nodes.
        var moveNextBody = BuildMoveNextBody(
            stateMachineFunction, analysis, smBoundView, shape, resultType, escapedSpilledLocals);

        // AsyncSpillLowering's reserved temporaries are produced before the normal
        // lowering pipeline, while MoveNextRewriter is allowed to reshape their
        // enclosing statements (notably protected regions).  Complete the state
        // layout from the emitted body as an integrity check: every surviving
        // `__spill_N` declaration gets a durable state slot even when a prior
        // structural pass made it invisible to the source-liveness walk.  Without
        // this check the first codegen consumer reports an opaque undefined-local
        // error; the state machine itself owns this representation invariant.
        var emittedSyntheticSpills = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        AwaitPointAnalyzer.CollectSyntheticSpillDeclarations(moveNextBody, emittedSyntheticSpills);
        foreach (var (name, type) in emittedSyntheticSpills)
            if (!smSym.Fields.Any(field => field.Name == SpillFieldName(name)))
                smSym.AddField(SpillField(SpillFieldName(name), smSym, type));

        sink.RegisterMethodBody(moveNextSym, moveNextBody);

        // 6. Build the BoundDataDeclaration for the SM struct.
        var moveNextDecl = new BoundFunctionDeclaration(
            IsPublic:       true,
            Name:           "MoveNext",
            TypeParameters: [],
            // MoveNext is an instance method on the synthesized struct.  Give its
            // bound declaration the same synthetic receiver shape as
            // SetStateMachine so CodeGen seeds the state-machine fields (captured
            // parameters, spills, builder, and awaiters) as `this.field` slots.
            // Without this, an async body such as `await producer(ch, token)`
            // loses its captured generic delegate/Chan<T> parameters and tries to
            // emit `producer` as an undefined Object member.
            Parameters:     [new BoundParameter("this", smBoundView, ByRef: false)],
            ReturnType:     new PrimitiveType("void"),
            Body:           moveNextBody,
            Attributes:     [],
            HasAwait:       false)
        { Symbol = moveNextSym, ContractFma = fn.ContractFma };

        // Instance methods carry the receiver as bound-param[0] (CodeGen drops it with
        // Skip(1) and lets Cecil's HasThis supply `this`). SetStateMachine's real
        // parameter is the IAsyncStateMachine argument, so it MUST sit at index 1 behind
        // a receiver placeholder — otherwise Skip(1) eats the real param and the method
        // emits as `SetStateMachine()` (arity 0), failing the interface slot (ES9510).
        var setSmDecl = new BoundFunctionDeclaration(
            IsPublic:   true,
            Name:       "SetStateMachine",
            TypeParameters: [],
            Parameters: [new BoundParameter("this", smBoundView, ByRef: false),
                new BoundParameter("stateMachine", new ExternalType("IAsyncStateMachine"),
                    ByRef: false)],
            ReturnType: new PrimitiveType("void"),
            Body:       new BoundBlockStatement([]),
            Attributes: [])
        { Symbol = setSmSym };

        var smBoundFields = smSym.Fields
            .Select(f => new BoundField(f.Name, f.Bound ?? new PrimitiveType("object"),
                IsPublic: true, Mutable: true))
            .ToList();

        var smDecl = new BoundDataDeclaration(
            IsPublic:        false,
            IsReadonly:      false,
            Name:            smSym.Name,
            TypeParameters:  stateMachineTypeParameters,
            DeriveTraits:    [],
            Fields:          smBoundFields,
            InstanceMethods: [moveNextDecl, setSmDecl],
            Classification:  DataClassification.Struct,
            Attributes:      ["CompilerGenerated"],
            InterfaceTypes:  [new ExternalType("IAsyncStateMachine")]);

        // 7. Build the synchronous stub body. The stub's EMITTED return type is the
        //    awaitable wrapper (`ValueTask<int>` for an uncolored `-> int`), not the
        //    declared unwrapped result — that is what `builder.Task`/`.ValueTask` yields
        //    and what the binder told every call site to expect.
        var wrappedReturn = WrapReturnType(resultType, shape);
        var stubBody = BuildStubBody(fn, smBoundView, shape, resultType, wrappedReturn);

        var stub = new BoundFunctionDeclaration(
            IsPublic:        fn.IsPublic,
            Name:            fn.Name,
            TypeParameters:  fn.TypeParameters,
            Parameters:      fn.Parameters,
            ReturnType:      wrappedReturn,
            Body:            stubBody,
            Attributes:      fn.Attributes,
            HasAwait:        false,
            InheritanceRole: fn.InheritanceRole,
            AsyncShape:      shape)
        {
            Symbol = fn.Symbol,
            AsyncStateMachineType = smBoundView,
            ContractFma = fn.ContractFma,
        };

        return (stub, smDecl);
    }

    // ─── MoveNext body synthesis ──────────────────────────────────────────────

    /// <summary>
    /// Synthesize the MoveNext body in CORE bound-tree nodes.
    ///
    /// Structure:
    /// <code>
    ///   try {
    ///     if (_state == 0) { restore locals; goto resume_0 }
    ///     if (_state == 1) { restore locals; goto resume_1 }
    ///     ...
    ///     // Original body with await-sites replaced by suspend/resume blocks:
    ///     // At await site N:
    ///     //   spill live locals to struct fields
    ///     //   _awaiter_N = awaitable.GetAwaiter()
    ///     //   if (!_awaiter_N.IsCompleted) { _state = N; builder.AwaitUnsafeOnCompleted(...); return }
    ///     //   resume_N: result = _awaiter_N.GetResult()
    ///     _builder.SetResult(result)
    ///   } catch (Exception ex) {
    ///     _state = -2; _builder.SetException(ex)
    ///   }
    /// </code>
    ///
    /// "goto resume_N" is represented as the resume block being the body of the corresponding
    /// `if (_state == N)` branch. The initial-path statements AND the resume-path statements
    /// are both represented in the body; CodeGen is free to optimize the dispatch into a
    /// switch/br table.
    /// </summary>
    internal const string TryEndLabel = "__async_tryend";
    internal const string ResultLocal = "__result";
    // Spill fields are deliberately not source names.  The MoveNext emitter
    // seeds every state-machine field into its slot table; naming a field `value`
    // would let an inner `value` declaration overwrite the outer spilled binding
    // before the first suspension. This applies equally to AsyncSpillLowering's
    // `__spill_N` locals: their source declaration needs its own lexical slot
    // before we copy it into durable state storage.
    internal static string SpillFieldName(string sourceName) => "__async_state_" + sourceName;

    static HashSet<string> FindEscapedSpilledLocals(
        BoundBlockStatement body,
        IReadOnlyList<(string Name, BoundType Type)> spilledLocals)
    {
        var spilledNames = spilledLocals.Select(local => local.Name).ToHashSet(StringComparer.Ordinal);
        var escaped = new HashSet<string>(StringComparer.Ordinal);
        Visit(body);
        return escaped;

        void Visit(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundVariableDeclaration { Name: var name, Local: { AddressEscapes: true } }
                    when spilledNames.Contains(name):
                    escaped.Add(name);
                    break;
                case BoundBlockStatement block:
                    foreach (var child in block.Statements) Visit(child);
                    break;
                case BoundIfStatement conditional:
                    Visit(conditional.Then);
                    if (conditional.Else is not null) Visit(conditional.Else);
                    break;
                case BoundWhileStatement loop:
                    Visit(loop.Body);
                    break;
                case BoundTryStatement attempt:
                    Visit(attempt.Body);
                    foreach (var clause in attempt.Catches) Visit(clause.Body);
                    break;
                case BoundForEachStatement each:
                    Visit(each.Body);
                    break;
                case BoundDeferStatement defer:
                    Visit(defer.Body);
                    break;
            }
        }
    }

    static BoundBlockStatement BuildMoveNextBody(
        BoundFunctionDeclaration fn,
        AwaitAnalysisResult analysis,
        ExternalType smType,
        AsyncReturnShape shape,
        BoundType resultType,
        IReadOnlySet<string> escapedSpilledLocals)
    {
        const string exLocal  = "__ex";
        var intType           = new PrimitiveType("int");
        var voidType          = new PrimitiveType("void");
        var isVoid            = resultType is VoidType or PrimitiveType { Name: "void" };
        var builderType       = BuilderBoundType(shape, resultType);
        var thisExpr          = new BoundNameExpression("this", smType);
        var stateField        = new BoundMemberAccessExpression(thisExpr, "_state",   intType);
        var builderAccess     = new BoundMemberAccessExpression(thisExpr, "_builder", builderType);
        var minusTwo          = new BoundLiteralExpression(-2, "-2", intType);

        // The async body is rewritten into ONE linear statement sequence (no main/resume
        // duplication). Each await becomes an inline suspend + a `__resume_N:` label; a
        // state-switch dispatch at the top `goto`s the matching label. Every `return e`
        // becomes `__result = e; goto __async_tryend`, so all completion converges on the
        // single SetResult emitted AFTER the try (Roslyn-correct: a throwing SetResult is
        // not caught by the state machine's own catch). The suspend is a plain `return`,
        // which CodeGen lowers to `leave` to the method epilogue — past the after-try
        // SetResult — so suspension never completes the builder.
        var rw        = new MoveNextRewriter(
            analysis, smType, builderType, resultType, isVoid, fn, escapedSpilledLocals);
        var bodyStmts = rw.RewriteBlock(fn.Body);

        var tryStmts = new List<BoundStatement>(bodyStmts.Count + analysis.AwaitSites.Count + 1);

        // State-dispatch: if (_state == N) goto <resume_N | outermost region pre-entry>.
        // A top-level await jumps to its resume label; an await inside a try routes to the
        // outermost enclosing region's pre-entry (its dispatcher then forwards inward).
        for (var i = 0; i < analysis.AwaitSites.Count; i++)
        {
            tryStmts.Add(new BoundIfStatement(
                new BoundBinaryExpression(stateField, SyntaxTokenKind.EqualsEquals,
                    new BoundLiteralExpression(i, i.ToString(), intType), new PrimitiveType("bool")),
                new BoundGotoStatement(rw.TopTarget(i)),
                Else: null));
        }

        tryStmts.AddRange(bodyStmts);
        // Convergence point for every `return` and for a void body's fall-through.
        tryStmts.Add(new BoundLabelStatement(TryEndLabel));

        // catch (Exception __ex) { _state = -2; _builder.SetException(__ex); return }
        // The `return` leaves to the method epilogue, skipping the after-try SetResult.
        var catchStmts = new List<BoundStatement>
        {
            new BoundAssignment(stateField, minusTwo),
            new BoundExpressionStatement(new BoundCallExpression(
                new BoundMemberAccessExpression(builderAccess, "SetException", voidType),
                [new BoundNameExpression(exLocal, new ExternalType("Exception"))], voidType)),
            new BoundReturnStatement(null),
        };

        var tryStatement = new BoundTryStatement(
            Body:    new BoundBlockStatement(tryStmts),
            Catches: [new BoundCatchClause(
                ExceptionType: new ExternalType("Exception"),
                BindingName:   exLocal,
                Body:          new BoundBlockStatement(catchStmts))]);

        // MoveNext body: declare __result (non-void), reserve the compiler-owned
        // spill-local slots, run the try, then complete the builder. A spill may
        // be defined on a resume path, so its lexical home must exist before any
        // generated spill/restore edge. The durable field has a separate name.
        var moveNext = new List<BoundStatement>(4 + analysis.SpilledLocals.Count);
        if (!isVoid)
            moveNext.Add(new BoundVariableDeclaration(
                Mutable: true, ResultLocal, resultType, new BoundDefaultExpression(resultType)));
        foreach (var (name, type) in analysis.SpilledLocals)
            if (name.StartsWith(AsyncSpill.AwaitFacts.Prefix, StringComparison.Ordinal))
                moveNext.Add(new BoundVariableDeclaration(
                    Mutable: true, name, type, new BoundDefaultExpression(type)));
        moveNext.Add(tryStatement);
        // Normal-completion tail (reached only by fall-through from __async_tryend; the
        // suspend and the catch both `return` past it): mark done, complete the builder.
        moveNext.Add(new BoundAssignment(stateField, minusTwo));
        moveNext.Add(new BoundExpressionStatement(new BoundCallExpression(
            new BoundMemberAccessExpression(builderAccess, "SetResult", voidType),
            isVoid ? [] : [new BoundNameExpression(ResultLocal, resultType)], voidType)));

        return new BoundBlockStatement(moveNext);
    }

    // ─── Stub body ────────────────────────────────────────────────────────────

    static BoundBlockStatement BuildStubBody(
        BoundFunctionDeclaration fn,
        ExternalType smType,
        AsyncReturnShape shape,
        BoundType resultType,
        BoundType wrappedReturn)
    {
        var stmts      = new List<BoundStatement>();
        const string smVar = "__sm";

        // let __sm = SM { _state = -1, param0 = param0, ... }
        var fieldInits = new List<BoundFieldInit>
        {
            new("_state", new BoundLiteralExpression(-1, "-1", new PrimitiveType("int"))),
        };
        foreach (var p in fn.Parameters)
            fieldInits.Add(new BoundFieldInit(p.Name, new BoundNameExpression(p.Name, p.Type)));

        stmts.Add(new BoundVariableDeclaration(Mutable: true, smVar, smType,
            new BoundObjectCreationExpression(smType, fieldInits)));

        var smRef        = new BoundNameExpression(smVar, smType);
        var builderType  = BuilderBoundType(shape, resultType);
        var builderField = new BoundMemberAccessExpression(smRef, "_builder", builderType);

        // __sm._builder = AsyncXxxMethodBuilder.Create()
        var builderTypeName = BuilderTypeName(shape, resultType);
        var createTarget = new BoundMemberAccessExpression(
            new BoundNameExpression(builderTypeName, builderType), "Create", builderType);
        stmts.Add(new BoundAssignment(builderField,
            new BoundCallExpression(createTarget, [], builderType)));

        // __sm._builder.Start<SM>(ref __sm)
        var startTarget = new BoundMemberAccessExpression(builderField, "Start",
            new PrimitiveType("void"));
        var refArg = new BoundOutArgumentExpression(smVar, smType, DeclaresLocal: false);
        stmts.Add(new BoundExpressionStatement(
            new BoundCallExpression(startTarget, [refArg], new PrimitiveType("void"),
                ExplicitTypeArguments: [smType])));

        // return __sm._builder.Task. BOTH builders expose the completed awaitable through a
        // property named `Task` — `AsyncValueTaskMethodBuilder<T>.Task` returns `ValueTask<T>`,
        // `AsyncTaskMethodBuilder<T>.Task` returns `Task<T>` (there is no `.ValueTask`). Its
        // type is the awaitable WRAPPER, which is also the stub's emitted return.
        if (shape is AsyncReturnShape.Task or AsyncReturnShape.ValueTask)
        {
            stmts.Add(new BoundReturnStatement(
                new BoundMemberAccessExpression(builderField, "Task", wrappedReturn)));
        }
        else
        {
            stmts.Add(new BoundReturnStatement(null));
        }

        return new BoundBlockStatement(stmts);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// The state machine's RESULT type — the `T` in `builder.SetResult(T)` and the builder's
    /// type argument — derived from the function's DECLARED return type. Mirrors
    /// <c>AsyncBinder.ClassifyReturn</c> (the binder authority Lowering can't reference across
    /// the dependency edge): a declared <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> unwraps to
    /// <c>T</c>, a bare <c>Task</c>/<c>ValueTask</c> or <c>void</c> is the void result, and the
    /// uncolored default (a bare <c>-&gt; int</c>) IS the result type already — it is the
    /// unwrapped value the body returns, never re-unwrapped to void.
    static BoundType UnwrapResultType(BoundType returnType, AsyncReturnShape shape) =>
        returnType switch
        {
            VoidType => new PrimitiveType("void"),
            ExternalType { Name: "Task" or "ValueTask", TypeArgs: [] } => new PrimitiveType("void"),
            ExternalType { Name: "Task" or "ValueTask", TypeArgs: [var inner] } => inner,
            PrimitiveType { Name: "void" } => new PrimitiveType("void"),
            _ => returnType, // uncolored `-> int`: the declared type IS the result
        };

    /// The async function's EMITTED (CLR) return type — the awaitable wrapper the synchronous
    /// stub actually returns. Uncolored E# declares the unwrapped result (`-> int`); emission
    /// wraps it per the shape (`ValueTask<int>`), matching what the binder tells call sites
    /// (`await fetch()` expects `ValueTask<int>`). Void results yield the bare wrapper.
    static BoundType WrapReturnType(BoundType resultType, AsyncReturnShape shape)
    {
        var isVoid = resultType is VoidType or PrimitiveType { Name: "void" };
        return shape switch
        {
            AsyncReturnShape.Void => new PrimitiveType("void"),
            AsyncReturnShape.Task => isVoid
                ? new ExternalType("Task")
                : new ExternalType("Task", [resultType]),
            _ => isVoid
                ? new ExternalType("ValueTask")
                : new ExternalType("ValueTask", [resultType]),
        };
    }

    static BoundType BuilderBoundType(AsyncReturnShape shape, BoundType resultType)
    {
        var isVoid = resultType is PrimitiveType { Name: "void" };
        return shape switch
        {
            AsyncReturnShape.Void  => new ExternalType("AsyncVoidMethodBuilder"),
            AsyncReturnShape.Task  => isVoid
                ? new ExternalType("AsyncTaskMethodBuilder")
                : new ExternalType("AsyncTaskMethodBuilder", [resultType]),
            _ => isVoid
                ? new ExternalType("AsyncValueTaskMethodBuilder")
                : new ExternalType("AsyncValueTaskMethodBuilder", [resultType]),
        };
    }

    static string BuilderTypeName(AsyncReturnShape shape, BoundType resultType)
    {
        return shape switch
        {
            AsyncReturnShape.Void  => "AsyncVoidMethodBuilder",
            AsyncReturnShape.Task  => "AsyncTaskMethodBuilder",
            _                      => "AsyncValueTaskMethodBuilder",
        };
    }

    /// Awaiter type for a given awaitable — a placeholder carrying the awaitable
    /// STRUCTURALLY as its single type argument (`__Awaiter&lt;Task&lt;int&gt;&gt;`), not a
    /// lossy mangled name. The backend recognizes <see cref="BackendPlaceholders.Awaiter"/>
    /// and resolves the concrete awaiter struct from the awaitable's `GetAwaiter()` return
    /// type (`Task&lt;int&gt;` → `TaskAwaiter&lt;int&gt;`), exactly as a direct emitter would.
    internal static BoundType AwaiterBoundType(BoundType awaitableType) =>
        new ExternalType(BackendPlaceholders.Awaiter, [awaitableType]);

    static FieldSymbol SpillField(string name, TypeSymbol declaring, BoundType bound) =>
        new()
        {
            Name          = name,
            DeclaringType = declaring,
            Type          = TypeRef.InferencePending,
            Bound         = bound,
            IsPublic      = true,
            Mutable       = true,
        };
}

// ─── MoveNext body rewriter ────────────────────────────────────────────────────

/// <summary>
/// Rewrites an async function body into the single linear statement stream that becomes
/// the state machine's MoveNext, in CORE bound nodes. Unlike a main-path/resume-tail
/// split, the body is emitted ONCE; each await expands inline to:
/// <code>
///   this._awaiter_N = X.GetAwaiter()
///   if (this._awaiter_N.IsCompleted) goto __completed_N   // synchronous fast path
///   _state = N
///   &lt;spill live locals to fields&gt;
///   _builder.AwaitUnsafeOnCompleted(ref this._awaiter_N, ref this)
///   return                                                 // suspend (leave to epilogue)
///   __resume_N:                                            // async resume entry
///   _state = -1
///   &lt;restore live locals from fields&gt;
///   __completed_N:                                         // sync + async converge
///   &lt;bind this._awaiter_N.GetResult()&gt;
/// </code>
/// The state-switch dispatch (built by <see cref="AsyncLowering.BuildMoveNextBody"/>)
/// <c>goto</c>s <c>__resume_N</c>; the sync path <c>goto</c>s <c>__completed_N</c>, so the
/// live-local restore runs only on resume. Every <c>return e</c> becomes
/// <c>__result = e; goto __async_tryend</c>, converging on the single SetResult after the
/// try. Parameter references resolve to <c>this.&lt;param&gt;</c> fields (params live on the
/// SM struct); locals stay real locals, spilled to fields only across a suspend.
/// </summary>
internal sealed class MoveNextRewriter
{
    readonly AwaitAnalysisResult _analysis;
    readonly ExternalType        _smType;
    readonly BoundType           _builderType;
    readonly BoundType           _resultType;
    readonly bool                _isVoid;
    readonly Dictionary<string, BoundType> _hoisted; // parameter names → exact SM field type
    readonly IReadOnlySet<string> _durableSpilledLocals;
    // AsyncSpillLowering materializes values evaluated before a nested await as
    // reserved locals. Track the ones already emitted in source order so an
    // await can preserve them even if an earlier structural lowering obscured
    // their lexical scope from AwaitPointAnalyzer.
    readonly Dictionary<string, BoundType> _activeSyntheticSpills = new(StringComparer.Ordinal);
    int _siteIndex;
    int _protectedRegionDepth;

    // ── Region staging (await inside a user try) ────────────────────────────────
    // A resume cannot branch straight into a `.try`. Each protected region gets a pre-entry
    // label (placed before its `.try`) and a body-entry dispatcher (placed at the top of its
    // try body). The outer state dispatch routes a region-await to the OUTERMOST region's
    // pre-entry; that region's dispatcher forwards inward (to the next region's pre-entry, or
    // the await's resume label when innermost). A region with a finally guards it with
    // `if (_state >= 0) goto __finallyend` so a suspend's `leave` skips cleanup.
    readonly Dictionary<BoundTryStatement, int> _regionId;
    readonly string[] _topTarget;   // outer-dispatch label per await site
    readonly Dictionary<BoundTryStatement, List<(int State, string Target)>> _regionDispatch
        = new(ReferenceEqualityComparer.Instance);

    static readonly PrimitiveType IntT  = new("int");
    static readonly PrimitiveType VoidT = new("void");
    static readonly PrimitiveType BoolT = new("bool");

    static string ResumeLabel(int i)   => $"__resume_{i}";
    static string PreEntryLabel(int id) => $"__preentry_{id}";
    static string FinallyEndLabel(int id) => $"__finallyend_{id}";

    /// The outer state-dispatch target for await site <paramref name="i"/>: its resume label
    /// directly when top-level, else the outermost enclosing region's pre-entry.
    public string TopTarget(int i) => _topTarget[i];

    public MoveNextRewriter(
        AwaitAnalysisResult analysis, ExternalType smType, BoundType builderType,
        BoundType resultType, bool isVoid, BoundFunctionDeclaration fn,
        IReadOnlySet<string> durableSpilledLocals)
    {
        _analysis    = analysis;
        _smType      = smType;
        _builderType = builderType;
        _resultType  = resultType;
        _isVoid      = isVoid;
        _hoisted     = fn.Parameters.ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
        _durableSpilledLocals = durableSpilledLocals;

        _regionId = new Dictionary<BoundTryStatement, int>(ReferenceEqualityComparer.Instance);
        for (var r = 0; r < analysis.Regions.Count; r++) _regionId[analysis.Regions[r]] = r;

        _topTarget = new string[analysis.AwaitSites.Count];
        for (var i = 0; i < analysis.AwaitSites.Count; i++)
        {
            var chain = analysis.AwaitSites[i].EnclosingTries;
            if (chain is not { Count: > 0 })
            {
                _topTarget[i] = ResumeLabel(i);
                continue;
            }
            _topTarget[i] = PreEntryLabel(_regionId[chain[0]]);
            for (var k = 0; k < chain.Count; k++)
            {
                var target = k == chain.Count - 1 ? ResumeLabel(i) : PreEntryLabel(_regionId[chain[k + 1]]);
                if (!_regionDispatch.TryGetValue(chain[k], out var list))
                    _regionDispatch[chain[k]] = list = [];
                list.Add((i, target));
            }
        }
    }

    BoundNameExpression This => new("this", _smType);
    BoundMemberAccessExpression Field(string name, BoundType t) => new(This, name, t);

    public List<BoundStatement> RewriteBlock(BoundBlockStatement block)
    {
        var output = new List<BoundStatement>(block.Statements.Count);
        foreach (var s in block.Statements) RewriteStatement(s, output);
        return output;
    }

    BoundBlockStatement RewriteToBlock(BoundStatement s)
    {
        var output = new List<BoundStatement>();
        RewriteStatement(s, output);
        return new BoundBlockStatement(output);
    }

    void RewriteStatement(BoundStatement s, List<BoundStatement> output)
    {
        switch (s)
        {
            case BoundVariableDeclaration v when ContainsAwait(v.Initializer):
                EmitAwait(v.Initializer, output, r =>
                    new BoundVariableDeclaration(v.Mutable, v.Name, v.DeclaredType, r));
                RememberSyntheticSpill(v.Name, v.DeclaredType);
                break;
            case BoundVariableDeclaration v:
                output.Add(v with { Initializer = RewriteExpr(v.Initializer) });
                RememberSyntheticSpill(v.Name, v.DeclaredType);
                break;

            case BoundAssignment a when ContainsAwait(a.Value):
                EmitAwait(a.Value, output, r => new BoundAssignment(RewriteExpr(a.Target), r));
                break;
            case BoundAssignment a:
                output.Add(a with { Target = RewriteExpr(a.Target), Value = RewriteExpr(a.Value) });
                break;

            case BoundExpressionStatement { Expression: BoundAwaitExpression } es:
                EmitAwait(es.Expression, output, bind: null);
                break;
            case BoundExpressionStatement e:
                output.Add(e with { Expression = RewriteExpr(e.Expression) });
                break;

            case BoundReturnStatement { Expression: BoundAwaitExpression } r:
                EmitAwait(r.Expression!, output, EmitReturnBinding);
                break;
            case BoundReturnStatement r:
                EmitReturn(r.Expression is null ? null : RewriteExpr(r.Expression), output);
                break;

            case BoundIfStatement i:
                output.Add(new BoundIfStatement(
                    RewriteExpr(i.Condition),
                    RewriteToBlock(i.Then),
                    i.Else is null ? null : RewriteToBlock(i.Else)));
                break;
            case BoundWhileStatement w:
                output.Add(new BoundWhileStatement(RewriteExpr(w.Condition), RewriteToBlock(w.Body)));
                break;
            case BoundBlockStatement b:
                output.Add(new BoundBlockStatement(RewriteBlock(b)));
                break;

            case BoundTryStatement tr:
                RewriteTry(tr, output);
                break;

            // `throw e` may reference a parameter (→ this.<param>); `throw await e` is the exotic
            // await-in-throw. A bare rethrow has no expression.
            case BoundThrowStatement { Expression: { } } th when ContainsAwait(th.Expression):
                EmitAwait(th.Expression!, output, r => new BoundThrowStatement(r));
                break;
            case BoundThrowStatement th:
                output.Add(th.Expression is null ? th : th with { Expression = RewriteExpr(th.Expression) });
                break;

            case BoundConstStatement cs:
                output.Add(cs with { Value = RewriteExpr(cs.Value) });
                break;

            default:
                output.Add(s);
                break;
        }
    }

    // A user try. If it encloses an await (a "region"), stage it: a pre-entry label before the
    // `.try`, a body-entry dispatcher forwarding staged states inward, and — for a finally — a
    // guard so a suspend's `leave` skips cleanup. Otherwise it is a plain try whose bodies are
    // rewritten for name-hoisting and any (non-region) awaits.
    void RewriteTry(BoundTryStatement tr, List<BoundStatement> output)
    {
        _protectedRegionDepth++;
        var body = RewriteBlock(tr.Body);
        var isRegion = _regionId.TryGetValue(tr, out var rid);

        if (isRegion && _regionDispatch.TryGetValue(tr, out var entries))
        {
            // Body-entry dispatcher: a state staged through this region's pre-entry is forwarded
            // to its resume label (innermost) or the next inner region's pre-entry. This is a `br`
            // within the `.try` — legal, unlike a branch into it from outside.
            var dispatched = new List<BoundStatement>(entries.Count + body.Count);
            foreach (var (state, target) in entries)
                dispatched.Add(new BoundIfStatement(StateEq(state), new BoundGotoStatement(target), Else: null));
            dispatched.AddRange(body);
            body = dispatched;
        }

        var catches = tr.Catches.Select(c =>
        {
            var cbody = RewriteBlock(c.Body);
            if (isRegion && c.IsFinally)
            {
                // if (_state >= 0) goto __finallyend;  <cleanup>;  __finallyend:
                // A suspend leaves _state >= 0, so the leave that exits the `.try` skips cleanup;
                // on real exit _state is -1/-2 and cleanup runs exactly once.
                var guarded = new List<BoundStatement>(cbody.Count + 2)
                {
                    new BoundIfStatement(StateGe0(), new BoundGotoStatement(FinallyEndLabel(rid)), Else: null),
                };
                guarded.AddRange(cbody);
                guarded.Add(new BoundLabelStatement(FinallyEndLabel(rid)));
                cbody = guarded;
            }
            // The filter runs in the generated MoveNext just like the catch body.
            // It can reference parameters such as `self`, which now live in state
            // machine fields, so it must go through the same hoisting rewrite.
            return c with
            {
                Body = new BoundBlockStatement(cbody),
                Guard = c.Guard is null ? null : RewriteExpr(c.Guard),
            };
        }).ToList();
        _protectedRegionDepth--;

        if (isRegion)
            output.Add(new BoundLabelStatement(PreEntryLabel(rid)));   // pre-entry: fall into the `.try`
        output.Add(new BoundTryStatement(new BoundBlockStatement(body), catches));
    }

    BoundExpression StateEq(int state) =>
        new BoundBinaryExpression(Field("_state", IntT), SyntaxTokenKind.EqualsEquals,
            new BoundLiteralExpression(state, state.ToString(), IntT), BoolT);

    BoundExpression StateGe0() =>
        new BoundBinaryExpression(Field("_state", IntT), SyntaxTokenKind.GreaterEquals,
            new BoundLiteralExpression(0, "0", IntT), BoolT);

    // `return e` converges on the single after-try SetResult: stash the value, then jump.
    void EmitReturn(BoundExpression? value, List<BoundStatement> output)
    {
        if (!_isVoid && value is not null)
            output.Add(new BoundAssignment(
                new BoundNameExpression(AsyncLowering.ResultLocal, _resultType), value));
        output.Add(new BoundGotoStatement(AsyncLowering.TryEndLabel)
        {
            ExitsProtectedRegion = _protectedRegionDepth > 0,
        });
    }

    // `return await X`: the awaited result is the function's result (or, for a void async,
    // GetResult is still called to observe completion/exceptions before converging).
    BoundStatement EmitReturnBinding(BoundExpression getResult)
    {
        var stmts = new List<BoundStatement>(2);
        if (_isVoid)
            stmts.Add(new BoundExpressionStatement(getResult));
        else
            stmts.Add(new BoundAssignment(
                new BoundNameExpression(AsyncLowering.ResultLocal, _resultType), getResult));
        stmts.Add(new BoundGotoStatement(AsyncLowering.TryEndLabel)
        {
            ExitsProtectedRegion = _protectedRegionDepth > 0,
        });
        return new BoundBlockStatement(stmts);
    }

    // Expand one await (in simple statement position) into suspend/resume, then `bind` the
    // GetResult value (null = discard a void/unused result).
    void EmitAwait(BoundExpression initializer, List<BoundStatement> output, Func<BoundExpression, BoundStatement>? bind)
    {
        if (initializer is not BoundAwaitExpression aw)
        {
            // Non-simple await position (await nested inside a larger expression). AsyncSpill
            // is meant to hoist these to a temp; until it runs in the pipeline, pass through
            // (a surviving BoundAwaitExpression trips the CORE assertion).
            var rewritten = RewriteExpr(initializer);
            output.Add(bind is null ? new BoundExpressionStatement(rewritten) : bind(rewritten));
            return;
        }

        var idx          = _siteIndex++;
        var site         = idx < _analysis.AwaitSites.Count ? _analysis.AwaitSites[idx] : null;
        var liveLocals   = MergeActiveSyntheticSpills(site?.LiveLocals ?? []);
        var awaiterType  = AsyncLowering.AwaiterBoundType(aw.Inner.Type);
        var awaiterField = Field($"_awaiter_{idx}", awaiterType);
        var resultIsVoid = aw.ResultType is VoidType or PrimitiveType { Name: "void" };
        var getResultTy  = resultIsVoid ? (BoundType)VoidT : aw.ResultType;

        // this._awaiter_N = X.GetAwaiter()
        output.Add(new BoundAssignment(awaiterField,
            new BoundCallExpression(
                new BoundMemberAccessExpression(RewriteExpr(aw.Inner), "GetAwaiter", awaiterType),
                [], awaiterType)));

        // if (this._awaiter_N.IsCompleted) goto __completed_N   — synchronous fast path
        output.Add(new BoundIfStatement(
            new BoundMemberAccessExpression(awaiterField, "IsCompleted", BoolT),
            new BoundGotoStatement($"__completed_{idx}"), Else: null));

        // suspend: _state = N; spill live locals; AwaitUnsafeOnCompleted(ref awaiter, ref this); return
        output.Add(new BoundAssignment(Field("_state", IntT),
            new BoundLiteralExpression(idx, idx.ToString(), IntT)));
        foreach (var lv in liveLocals)
            if (!_durableSpilledLocals.Contains(lv.Name))
                output.Add(new BoundAssignment(Field(AsyncLowering.SpillFieldName(lv.Name), lv.Type), new BoundNameExpression(lv.Name, lv.Type)));
        output.Add(new BoundExpressionStatement(new BoundCallExpression(
            new BoundMemberAccessExpression(Field("_builder", _builderType), "AwaitUnsafeOnCompleted", VoidT),
            [
                new BoundOutArgumentExpression($"_awaiter_{idx}", awaiterType, DeclaresLocal: false),
                new BoundOutArgumentExpression("this", _smType, DeclaresLocal: false),
            ],
            VoidT, ExplicitTypeArguments: [awaiterType, _smType])));
        output.Add(new BoundReturnStatement(null));

        // __resume_N: async resume entry — _state = -1; restore live locals from fields
        output.Add(new BoundLabelStatement($"__resume_{idx}"));
        output.Add(new BoundAssignment(Field("_state", IntT),
            new BoundLiteralExpression(-1, "-1", IntT)));
        foreach (var lv in liveLocals)
            if (!_durableSpilledLocals.Contains(lv.Name))
                output.Add(new BoundAssignment(new BoundNameExpression(lv.Name, lv.Type), Field(AsyncLowering.SpillFieldName(lv.Name), lv.Type)));

        // __completed_N: sync + async converge — bind GetResult()
        output.Add(new BoundLabelStatement($"__completed_{idx}"));
        var getResult = new BoundCallExpression(
            new BoundMemberAccessExpression(awaiterField, "GetResult", getResultTy), [], getResultTy);
        output.Add(bind is null ? new BoundExpressionStatement(getResult) : bind(getResult));
    }

    void RememberSyntheticSpill(string name, BoundType type)
    {
        if (name.StartsWith(AsyncSpill.AwaitFacts.Prefix, StringComparison.Ordinal))
            _activeSyntheticSpills[name] = type;
    }

    IReadOnlyList<(string Name, BoundType Type)> MergeActiveSyntheticSpills(
        IReadOnlyList<(string Name, BoundType Type)> liveLocals)
    {
        if (_activeSyntheticSpills.Count == 0) return liveLocals;
        var merged = liveLocals.ToList();
        var names = new HashSet<string>(merged.Select(local => local.Name), StringComparer.Ordinal);
        foreach (var (name, type) in _activeSyntheticSpills)
            if (names.Add(name)) merged.Add((name, type));
        return merged;
    }

    // Rewrite parameter references to `this.<param>` field access; recurse through every
    // expression that can hold one. Locals (incl. spilled ones) stay as-is — they are real
    // CLR locals, touched as fields only by the spill/restore the suspend emits.
    BoundExpression RewriteExpr(BoundExpression e) => e switch
    {
        BoundNameExpression n when _hoisted.TryGetValue(n.Name, out var fieldType) => Field(n.Name, fieldType),
        BoundUnaryExpression u   => u with { Operand = RewriteExpr(u.Operand) },
        BoundBinaryExpression b  => b with { Left = RewriteExpr(b.Left), Right = RewriteExpr(b.Right) },
        BoundMemberAccessExpression ma => ma with { Target = RewriteExpr(ma.Target) },
        BoundCallExpression c    => c with { Target = RewriteExpr(c.Target), Arguments = c.Arguments.Select(RewriteExpr).ToList() },
        BoundObjectCreationExpression oc => oc with { Fields = oc.Fields.Select(f => f with { Value = RewriteExpr(f.Value) }).ToList() },
        BoundIndexExpression ix  => ix with { Target = RewriteExpr(ix.Target), Index = RewriteExpr(ix.Index) },
        BoundArrayCreationExpression ac => ac with { Size = RewriteExpr(ac.Size) },
        BoundTupleLiteralExpression t => t with { Elements = t.Elements.Select(RewriteExpr).ToList() },
        BoundConversion cv       => cv with { Operand = RewriteExpr(cv.Operand) },
        BoundMethodGroupConversion mg when mg.Receiver is not null => mg with { Receiver = RewriteExpr(mg.Receiver) },
        BoundResultCallExpression rc => rc with { Argument = RewriteExpr(rc.Argument) },
        BoundAddressOfVariableExpression av => av with { Target = RewriteExpr(av.Target) },
        BoundHeapAllocExpression ha => ha with { Inner = RewriteExpr(ha.Inner) },
        BoundAwaitExpression aw  => aw with { Inner = RewriteExpr(aw.Inner) },
        _ => e,
    };

    static bool ContainsAwait(BoundExpression e) => e switch
    {
        BoundAwaitExpression          => true,
        BoundCallExpression c         => ContainsAwait(c.Target) || c.Arguments.Any(ContainsAwait),
        BoundMemberAccessExpression m => ContainsAwait(m.Target),
        BoundBinaryExpression b       => ContainsAwait(b.Left) || ContainsAwait(b.Right),
        BoundUnaryExpression u        => ContainsAwait(u.Operand),
        BoundConversion cv            => ContainsAwait(cv.Operand),
        _                             => false,
    };
}

// ─── Await-point / spill analysis ─────────────────────────────────────────────

/// One await site in the async function body.
internal sealed record AwaitSite(
    /// Type of the expression being awaited (e.g. Task&lt;int&gt;).
    BoundType AwaitableType,
    /// Type returned by GetResult() on the awaiter (e.g. int).
    BoundType ResultType,
    /// Locals live at this await point — must survive suspension in struct fields.
    IReadOnlyList<(string Name, BoundType Type)> LiveLocals,
    /// Statements that execute after this site's GetResult() — the "resume tail".
    IReadOnlyList<BoundStatement> TailStatements,
    /// When the await initializes a local binding, the local's name (so the resume
    /// path can bind the result to the same name rather than discarding it).
    string? ResultLocalName = null,
    /// The protected regions (user <c>try</c> bodies, by original-tree reference) enclosing
    /// this await, OUTERMOST first. Drives region-aware resume staging — a resume cannot branch
    /// straight into a <c>.try</c>, so it routes through the outermost region's pre-entry and is
    /// forwarded inward by each region's body-entry dispatcher. Empty for a top-level await.
    IReadOnlyList<BoundTryStatement>? EnclosingTries = null);

/// Full analysis result for one async function body.
internal sealed record AwaitAnalysisResult(
    IReadOnlyList<AwaitSite>               AwaitSites,
    IReadOnlyList<(string Name, BoundType Type)> SpilledLocals,
    /// Distinct protected regions that enclose at least one await, in first-encounter order —
    /// each gets a pre-entry label and a body-entry dispatcher in MoveNext.
    IReadOnlyList<BoundTryStatement>      Regions);

/// Raises durable property-location locals that are live across an await into an
/// owner-plus-protocol representation.  The source local may have a
/// <see cref="ByRefBoundType"/>, but the replacement local always has the receiver's
/// ordinary type, which is legal in an async state-machine field.  Every read/write
/// of the original name is reconstructed as an access to the original property on
/// that saved receiver, preserving single receiver evaluation and property policy.
static class AsyncPropertyLocationRaising
{
    sealed record Location(
        string RootName,
        string OwnerName,
        BoundExpression Receiver,
        BoundMemberAccessExpression Property,
        BoundType PointeeType);

    public static BoundBlockStatement Rewrite(BoundBlockStatement body, HashSet<string> spilledNames)
    {
        if (spilledNames.Count == 0) return body;

        // A property location can be copied through ordinary local declarations
        // before the await:
        //
        //   var first = &owner.value
        //   var live = first
        //   await work()
        //   live += 1
        //
        // The old direct-declaration-only lookup saw `live` as `ref T` and put
        // that managed reference in the state machine. Track declaration-time
        // aliases back to their direct property root so the root evaluates its
        // receiver exactly once and every alias remains a projection of the same
        // property location. Pointer-local assignments write through the selected
        // location (including compound assignments after AssignmentLowering), so
        // they deliberately retain this alias identity.
        var aliases = new Dictionary<string, Location>(StringComparer.Ordinal);
        Find(body);
        if (aliases.Count == 0) return body;

        var selectedRoots = aliases
            .Where(pair => spilledNames.Contains(pair.Key))
            .Select(pair => pair.Value.RootName)
            .ToHashSet(StringComparer.Ordinal);
        if (selectedRoots.Count == 0) return body;

        var locations = aliases
            .Where(pair => selectedRoots.Contains(pair.Value.RootName))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return (BoundBlockStatement)new Rewriter(locations).RewriteStatement(body);

        void Find(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundVariableDeclaration
                {
                    Name: var name,
                    Initializer: BoundAddressOfVariableExpression
                    {
                        IsScopedPropertyBorrow: false,
                        Target: BoundMemberAccessExpression property,
                        PointeeType: var pointee,
                    }
                }:
                    aliases[name] = new Location(
                        name,
                        $"<property_location_owner_{name}>__async",
                        property.Target,
                        property,
                        pointee);
                    break;
                case BoundVariableDeclaration
                {
                    Name: var name,
                    DeclaredType: ByRefBoundType,
                    Initializer: BoundNameExpression source,
                } when aliases.TryGetValue(source.Name, out var location):
                    aliases[name] = location;
                    break;
                case BoundVariableDeclaration { Name: var name }:
                    aliases.Remove(name);
                    break;
                case BoundBlockStatement block:
                    foreach (var child in block.Statements) Find(child);
                    break;
                case BoundIfStatement conditional:
                    Find(conditional.Then);
                    if (conditional.Else is not null) Find(conditional.Else);
                    break;
                case BoundWhileStatement loop:
                    Find(loop.Body);
                    break;
                case BoundTryStatement attempt:
                    Find(attempt.Body);
                    foreach (var clause in attempt.Catches) Find(clause.Body);
                    break;
                case BoundForEachStatement each:
                    Find(each.Body);
                    break;
                case BoundDeferStatement defer:
                    Find(defer.Body);
                    break;
            }
        }
    }

    sealed class Rewriter(Dictionary<string, Location> locations) : LoweringRewriter
    {
        public override BoundExpression RewriteExpression(BoundExpression expression)
        {
            if (expression is BoundNameExpression name
                && locations.TryGetValue(name.Name, out var location))
                return PropertyValue(location);
            return base.RewriteExpression(expression);
        }

        protected override BoundExpression RewriteAddressOfVariableExpression(BoundAddressOfVariableExpression node)
        {
            if (node.Target is BoundNameExpression name
                && locations.TryGetValue(name.Name, out var location))
                return new BoundAddressOfVariableExpression(PropertyValue(location), location.PointeeType);
            return base.RewriteAddressOfVariableExpression(node);
        }

        protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
        {
            if (locations.TryGetValue(node.Name, out var location))
            {
                if (node.Name == location.RootName)
                {
                    var receiver = RewriteExpression(location.Receiver);
                    return Synth.Let(location.OwnerName, location.Receiver.Type, receiver)
                        with { Span = node.Span };
                }

                // The root declaration above materializes the evaluated receiver.
                // This alias has no CLR-local representation: every subsequent use
                // is rewritten to `owner.property`, preserving the one location.
                return new BoundBlockStatement([]) { Span = node.Span };
            }
            return base.RewriteVariableDeclaration(node);
        }

        BoundMemberAccessExpression PropertyValue(Location location)
        {
            var owner = Synth.Name(location.OwnerName, location.Receiver.Type);
            return new BoundMemberAccessExpression(owner, location.Property.MemberName, location.PointeeType)
            {
                Member = location.Property.Member,
                IsPropertyLocationProjection = true,
                Span = location.Property.Span,
            };
        }
    }
}

/// <summary>
/// Gives shadowed source locals distinct lowered names for the async backend.
/// The binder's LocalSymbol is already identity-based; this bridge prevents the
/// legacy name-keyed IL slot table from conflating two otherwise valid bindings.
/// </summary>
static class AsyncLocalShadowRenaming
{
    public static BoundBlockStatement Rewrite(BoundBlockStatement body)
    {
        var collector = new Collector();
        collector.VisitNode(body);
        var duplicateNames = collector.Declarations
            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
            // AsyncSpillLowering owns globally unique `__spill_N` names. They
            // are a lowering protocol, not source lexical declarations; renaming
            // one side of that protocol here breaks the generated definition/use
            // identity and formerly surfaced later as an undefined IL local.
            // A lowering may replay one logical declaration in more than one
            // generated position. Only distinct binder LocalSymbols represent
            // source shadowing; synthesized declarations have no LocalSymbol and
            // must not trigger a source-name rewrite.
            .Where(group => !group.Key.StartsWith(AsyncSpill.AwaitFacts.Prefix, StringComparison.Ordinal)
                && group.Select(declaration => declaration.Local)
                    .Where(symbol => symbol is not null)
                    .Distinct(ReferenceEqualityComparer.Instance)
                    .Count() > 1)
            .ToList();
        if (duplicateNames.Count == 0) return body;

        var names = new Dictionary<BoundVariableDeclaration, string>(ReferenceEqualityComparer.Instance);
        var ordinal = 0;
        foreach (var group in duplicateNames)
            foreach (var declaration in group)
                if (declaration.Local is not null)
                    names[declaration] = $"__async_local_{ordinal++}_{declaration.Name}";

        return (BoundBlockStatement)new Rewriter(names).RewriteStatement(body);
    }

    sealed class Collector : BoundTreeVisitor
    {
        public List<BoundVariableDeclaration> Declarations { get; } = [];

        protected override void VisitVariableDeclaration(BoundVariableDeclaration node)
        {
            Declarations.Add(node);
            base.VisitVariableDeclaration(node);
        }
    }

    sealed class Rewriter(IReadOnlyDictionary<BoundVariableDeclaration, string> names) : BoundTreeRewriter
    {
        readonly Stack<Dictionary<string, string>> _scopes = new();

        protected override BoundStatement RewriteBlockStatement(BoundBlockStatement node)
        {
            var scope = _scopes.TryPeek(out var outer)
                ? new Dictionary<string, string>(outer, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            _scopes.Push(scope);
            try
            {
                var statements = new List<BoundStatement>(node.Statements.Count);
                var changed = false;
                foreach (var statement in node.Statements)
                {
                    var rewritten = RewriteStatement(statement);
                    statements.Add(rewritten);
                    changed |= !ReferenceEquals(rewritten, statement);
                    if (statement is BoundVariableDeclaration declaration
                        && names.TryGetValue(declaration, out var loweredName))
                        scope[declaration.Name] = loweredName;
                }
                return changed ? node with { Statements = statements } : node;
            }
            finally
            {
                _scopes.Pop();
            }
        }

        protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
        {
            var rewritten = (BoundVariableDeclaration)base.RewriteVariableDeclaration(node);
            return names.TryGetValue(node, out var name)
                ? rewritten with { Name = name }
                : rewritten;
        }

        protected override BoundExpression RewriteNameExpression(BoundNameExpression node)
        {
            var name = _scopes.TryPeek(out var scope) && scope.TryGetValue(node.Name, out var byScope)
                ? byScope
                : null;
            if (name is null) return node;
            return new BoundNameExpression(name, node.Type)
            {
                Symbol = node.Symbol,
                Span = node.Span,
            };
        }
    }
}

/// <summary>
/// Intra-function analysis that identifies:
/// <list type="bullet">
///   <item>The ordered set of await sites, their types, and live-local sets.</item>
///   <item>The union of all spilled locals (= all live locals across any await point).</item>
///   <item>The "resume tail" — statements that follow each await in its enclosing block.</item>
///   <item>The protected-region chain enclosing each await, and the distinct regions overall.</item>
/// </list>
/// </summary>
internal static class AwaitPointAnalyzer
{
    public static AwaitAnalysisResult Analyze(
        BoundBlockStatement body,
        IReadOnlyList<BoundParameter>? parameters = null)
    {
        var scope   = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        if (parameters is not null)
            foreach (var parameter in parameters)
                scope[parameter.Name] = parameter.Type;
        var sites   = new List<AwaitSite>();
        var spilled = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        var chain   = new List<BoundTryStatement>();

        WalkBlock(body, scope, sites, spilled, chain, continuation: []);

        // AsyncSpillLowering's named temporaries are part of its own evaluation
        // protocol. They may be referenced by a rewritten resume fragment that
        // is not expressible as an ordinary lexical continuation. They therefore
        // survive every suspension in this function: conservative for compiler
        // temps, and necessary for an operand evaluated before a nested await.
        var syntheticSpills = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        CollectSyntheticSpillDeclarations(body, syntheticSpills);
        foreach (var (name, type) in syntheticSpills)
            spilled[name] = type;
        if (syntheticSpills.Count > 0)
        {
            sites = sites.Select(site => site with
            {
                LiveLocals = MergeSyntheticSpills(site, syntheticSpills),
            }).ToList();
        }

        // Distinct enclosing regions, in first-encounter order (drives pre-entry-label ids).
        var regions = new List<BoundTryStatement>();
        var seen    = new HashSet<BoundTryStatement>(ReferenceEqualityComparer.Instance);
        foreach (var s in sites)
            if (s.EnclosingTries is { } tries)
                foreach (var t in tries)
                    if (seen.Add(t)) regions.Add(t);

        return new AwaitAnalysisResult(
            AwaitSites:    sites,
            SpilledLocals: spilled.Select(kv => (kv.Key, kv.Value)).ToList(),
            Regions:       regions);
    }

    static IReadOnlyList<(string Name, BoundType Type)> MergeSyntheticSpills(
        AwaitSite site,
        IReadOnlyDictionary<string, BoundType> syntheticSpills)
    {
        var merged = site.LiveLocals.ToList();
        var names = new HashSet<string>(merged.Select(local => local.Name), StringComparer.Ordinal);
        foreach (var (name, type) in syntheticSpills)
            // The await's result binding does not exist until GetResult; spilling
            // it before suspension would observe an uninitialized value. Every
            // other compiler spill has a reserved lexical slot and may have been
            // evaluated before this await even when a prior structural lowering
            // erased that fact from the statement tail.
            if (name != site.ResultLocalName && names.Add(name))
                merged.Add((name, type));
        return merged;
    }

    static void WalkBlock(
        BoundBlockStatement block,
        Dictionary<string, BoundType> scope,
        List<AwaitSite> sites,
        Dictionary<string, BoundType> spilled,
        List<BoundTryStatement> chain,
        IReadOnlyList<BoundStatement> continuation)
    {
        // Blocks can shadow an outer name.  Restore the prior binding on exit
        // rather than simply removing it, otherwise an inner `value` erases the
        // outer `value` from later liveness and the state machine forgets it.
        var declared = new List<(string Name, bool HadPrior, BoundType? Prior)>();
        var stmts    = block.Statements;

        for (var i = 0; i < stmts.Count; i++)
        {
            var tail = i + 1 < stmts.Count
                ? (IReadOnlyList<BoundStatement>)stmts.Skip(i + 1).Concat(continuation).ToList()
                : continuation;
            WalkStatement(stmts[i], scope, sites, spilled, declared, tail, chain);
        }

        for (var i = declared.Count - 1; i >= 0; i--)
        {
            var (name, hadPrior, prior) = declared[i];
            if (hadPrior) scope[name] = prior!;
            else scope.Remove(name);
        }
    }

    static void WalkStatement(
        BoundStatement stmt,
        Dictionary<string, BoundType> scope,
        List<AwaitSite> sites,
        Dictionary<string, BoundType> spilled,
        List<(string Name, bool HadPrior, BoundType? Prior)> declaredHere,
        IReadOnlyList<BoundStatement> tailStmts,
        List<BoundTryStatement> chain)
    {
        switch (stmt)
        {
            case BoundVariableDeclaration v:
            {
                if (ContainsAwait(v.Initializer))
                {
                    var (awaitableType, resultType) = ExtractAwaitTypes(v.Initializer);
                    var live = LiveAfter(scope, tailStmts);
                    foreach (var (name, type) in live) spilled[name] = type;
                    sites.Add(new AwaitSite(awaitableType, resultType, live, tailStmts, v.Name, [.. chain]));
                }
                else
                {
                    WalkExpr(v.Initializer, scope, sites, spilled, tailStmts, chain);
                }
                var hadPrior = scope.TryGetValue(v.Name, out var prior);
                scope[v.Name] = v.DeclaredType;
                declaredHere.Add((v.Name, hadPrior, prior));
                break;
            }

            case BoundExpressionStatement e:
                WalkExpr(e.Expression, scope, sites, spilled, tailStmts, chain);
                break;

            case BoundAssignment a:
                WalkExpr(a.Target, scope, sites, spilled, tailStmts, chain);
                WalkExpr(a.Value,  scope, sites, spilled, tailStmts, chain);
                break;

            case BoundReturnStatement { Expression: { } expr }:
                WalkExpr(expr, scope, sites, spilled, tailStmts, chain);
                break;

            case BoundThrowStatement { Expression: { } texpr }:
                WalkExpr(texpr, scope, sites, spilled, tailStmts, chain);
                break;

            case BoundIfStatement i:
                WalkExpr(i.Condition, scope, sites, spilled, tailStmts, chain);
                WalkBlock(AsBlock(i.Then), scope, sites, spilled, chain, tailStmts);
                if (i.Else is not null) WalkBlock(AsBlock(i.Else), scope, sites, spilled, chain, tailStmts);
                break;

            case BoundWhileStatement w:
                WalkExpr(w.Condition, scope, sites, spilled, tailStmts, chain);
                // A suspension in the loop body can resume into another
                // iteration, so the condition/body are also a conservative
                // continuation in addition to the statements after the loop.
                WalkBlock(AsBlock(w.Body), scope, sites, spilled, chain,
                    [w, .. tailStmts]);
                break;

            case BoundBlockStatement b:
                WalkBlock(b, scope, sites, spilled, chain, tailStmts);
                break;

            case BoundTryStatement tr:
                // The try BODY is a protected region: push it for the body walk. The catch /
                // finally handlers sit OUTSIDE the `.try`, so they walk under the outer chain
                // (an await directly inside a catch/finally handler — exotic — is not region-staged).
                chain.Add(tr);
                WalkBlock(tr.Body, scope, sites, spilled, chain, tailStmts);
                chain.RemoveAt(chain.Count - 1);
                foreach (var c in tr.Catches) WalkBlock(c.Body, scope, sites, spilled, chain, tailStmts);
                break;
        }
    }

    static void WalkExpr(
        BoundExpression expr,
        Dictionary<string, BoundType> scope,
        List<AwaitSite> sites,
        Dictionary<string, BoundType> spilled,
        IReadOnlyList<BoundStatement> tailStmts,
        List<BoundTryStatement> chain,
        IReadOnlyList<BoundExpression>? followingExpressions = null)
    {
        switch (expr)
        {
            case BoundAwaitExpression aw:
            {
                var live = LiveAfter(scope, tailStmts, followingExpressions);
                foreach (var (name, type) in live) spilled[name] = type;
                // aw.Inner.Type = awaitable type (e.g. Task<int>); aw.ResultType = result (int).
                sites.Add(new AwaitSite(aw.Inner.Type, aw.ResultType, live, tailStmts, null, [.. chain]));
                WalkExpr(aw.Inner, scope, sites, spilled, tailStmts, chain, followingExpressions);
                break;
            }

            case BoundCallExpression c:
                WalkExpr(c.Target, scope, sites, spilled, tailStmts, chain,
                    [.. c.Arguments, .. (followingExpressions ?? [])]);
                for (var i = 0; i < c.Arguments.Count; i++)
                    WalkExpr(c.Arguments[i], scope, sites, spilled, tailStmts, chain,
                        [.. c.Arguments.Skip(i + 1), .. (followingExpressions ?? [])]);
                break;

            case BoundMemberAccessExpression ma:
                WalkExpr(ma.Target, scope, sites, spilled, tailStmts, chain, followingExpressions);
                break;

            case BoundBinaryExpression bin:
                WalkExpr(bin.Left, scope, sites, spilled, tailStmts, chain,
                    [bin.Right, .. (followingExpressions ?? [])]);
                WalkExpr(bin.Right, scope, sites, spilled, tailStmts, chain, followingExpressions);
                break;

            case BoundUnaryExpression u:
                WalkExpr(u.Operand, scope, sites, spilled, tailStmts, chain, followingExpressions);
                break;

            case BoundConversion cv:
                WalkExpr(cv.Operand, scope, sites, spilled, tailStmts, chain, followingExpressions);
                break;

            // `&local` is a use of the local's storage, not an opaque terminal.
            // If it appears after an await, the underlying local has to be hoisted
            // even when the pointer itself is only a short-lived readonly borrow.
            // Omitting this edge initialized the resume-local to default and then
            // passed that default through the address expression.
            case BoundAddressOfVariableExpression address:
                WalkExpr(address.Target, scope, sites, spilled, tailStmts, chain, followingExpressions);
                break;
        }
    }

    static bool ContainsAwait(BoundExpression e) => e switch
    {
        BoundAwaitExpression         => true,
        BoundCallExpression c        => c.Arguments.Any(ContainsAwait) || ContainsAwait(c.Target),
        BoundMemberAccessExpression m => ContainsAwait(m.Target),
        BoundBinaryExpression b      => ContainsAwait(b.Left) || ContainsAwait(b.Right),
        BoundUnaryExpression u       => ContainsAwait(u.Operand),
        BoundConversion cv           => ContainsAwait(cv.Operand),
        _                            => false,
    };

    static (BoundType AwaitableType, BoundType ResultType) ExtractAwaitTypes(BoundExpression e) => e switch
    {
        BoundAwaitExpression aw => (aw.Inner.Type, aw.ResultType),
        BoundCallExpression c when ContainsAwait(c)   => ExtractAwaitTypes(c.Target),
        BoundMemberAccessExpression m when ContainsAwait(m) => ExtractAwaitTypes(m.Target),
        _                       => (new ExternalType("Task"), new PrimitiveType("void")),
    };

    static BoundBlockStatement AsBlock(BoundStatement s) =>
        s as BoundBlockStatement ?? new BoundBlockStatement([s]) { Span = s.Span };

    static List<(string Name, BoundType Type)> LiveAfter(
        Dictionary<string, BoundType> scope,
        IReadOnlyList<BoundStatement> continuation,
        IReadOnlyList<BoundExpression>? followingExpressions = null)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in continuation) CollectStatementUses(statement, used);
        if (followingExpressions is not null)
            foreach (var expression in followingExpressions) CollectExpressionUses(expression, used);
        return scope.Where(entry => used.Contains(entry.Key)
                // AsyncSpillLowering names the already-evaluated pieces of a
                // suspended expression `__spill_N`. They are liveness roots even
                // when their synthetic continuation is represented outside this
                // statement tail.
                || entry.Key.StartsWith(AsyncSpill.AwaitFacts.Prefix, StringComparison.Ordinal))
            .Select(entry => (entry.Key, entry.Value))
            .ToList();
    }

    static void CollectStatementUses(BoundStatement statement, HashSet<string> used)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                foreach (var child in block.Statements) CollectStatementUses(child, used);
                break;
            case BoundVariableDeclaration declaration:
                CollectExpressionUses(declaration.Initializer, used);
                break;
            case BoundExpressionStatement expression:
                CollectExpressionUses(expression.Expression, used);
                break;
            case BoundAssignment assignment:
                CollectExpressionUses(assignment.Target, used);
                CollectExpressionUses(assignment.Value, used);
                break;
            case BoundCompoundAssignment compound:
                CollectExpressionUses(compound.Target, used);
                CollectExpressionUses(compound.Value, used);
                break;
            case BoundReturnStatement { Expression: { } returned }:
                CollectExpressionUses(returned, used);
                break;
            case BoundThrowStatement { Expression: { } thrown }:
                CollectExpressionUses(thrown, used);
                break;
            case BoundIfStatement conditional:
                CollectExpressionUses(conditional.Condition, used);
                CollectStatementUses(conditional.Then, used);
                if (conditional.Else is not null) CollectStatementUses(conditional.Else, used);
                break;
            case BoundWhileStatement loop:
                CollectExpressionUses(loop.Condition, used);
                CollectStatementUses(loop.Body, used);
                break;
            case BoundForEachStatement each:
                CollectExpressionUses(each.Collection, used);
                CollectStatementUses(each.Body, used);
                break;
            case BoundTryStatement attempt:
                CollectStatementUses(attempt.Body, used);
                foreach (var clause in attempt.Catches) CollectStatementUses(clause.Body, used);
                break;
            case BoundDeferStatement deferred:
                CollectStatementUses(deferred.Body, used);
                break;
        }
    }

    static void CollectExpressionUses(BoundExpression expression, HashSet<string> used)
    {
        switch (expression)
        {
            case BoundNameExpression name:
                used.Add(name.Name);
                break;
            case BoundCallExpression call:
                CollectExpressionUses(call.Target, used);
                foreach (var argument in call.Arguments) CollectExpressionUses(argument, used);
                break;
            case BoundMemberAccessExpression member:
                CollectExpressionUses(member.Target, used);
                break;
            case BoundIndexExpression index:
                CollectExpressionUses(index.Target, used);
                CollectExpressionUses(index.Index, used);
                break;
            case BoundUnaryExpression unary:
                CollectExpressionUses(unary.Operand, used);
                break;
            case BoundBinaryExpression binary:
                CollectExpressionUses(binary.Left, used);
                CollectExpressionUses(binary.Right, used);
                break;
            case BoundConversion conversion:
                CollectExpressionUses(conversion.Operand, used);
                break;
            case BoundAddressOfVariableExpression address:
                // Liveness follows the addressed location, rather than treating
                // a borrow as a value with no local dependency.
                CollectExpressionUses(address.Target, used);
                break;
            case BoundConditionalExpression conditional:
                CollectExpressionUses(conditional.Condition, used);
                CollectExpressionUses(conditional.Consequence, used);
                CollectExpressionUses(conditional.Alternative, used);
                break;
            case BoundNullCoalescingExpression coalescing:
                CollectExpressionUses(coalescing.Left, used);
                CollectExpressionUses(coalescing.Right, used);
                break;
            case BoundObjectCreationExpression creation:
                foreach (var field in creation.Fields) CollectExpressionUses(field.Value, used);
                foreach (var argument in creation.ConstructorArguments) CollectExpressionUses(argument, used);
                break;
            case BoundListLiteralExpression list:
                foreach (var element in list.Elements) CollectExpressionUses(element, used);
                break;
            case BoundTupleLiteralExpression tuple:
                foreach (var element in tuple.Elements) CollectExpressionUses(element, used);
                break;
            case BoundFunctionLiteralExpression function:
                foreach (var capture in function.CapturedVariables) used.Add(capture.Name);
                break;
            case BoundSpawnExpression spawn:
                foreach (var capture in spawn.CapturedVariables) used.Add(capture.Name);
                break;
            case BoundMethodGroupConversion { Receiver: { } receiver }:
                CollectExpressionUses(receiver, used);
                break;
            case BoundAwaitExpression awaitExpression:
                CollectExpressionUses(awaitExpression.Inner, used);
                break;
        }
    }

    internal static void CollectSyntheticSpillDeclarations(
        BoundStatement statement,
        Dictionary<string, BoundType> spilled)
    {
        // The spill rewrite can place a named temporary under any statement
        // form that carries a block (not only the forms this analysis happens
        // to walk for await liveness).  Use the canonical bound-tree traversal
        // here instead of duplicating that structural knowledge.  Missing one
        // container must never reach CodeGen as an undefined `__spill_N` slot.
        var collector = new SyntheticSpillCollector(spilled);
        collector.VisitNode(statement);
    }

    sealed class SyntheticSpillCollector(Dictionary<string, BoundType> spilled) : BoundTreeVisitor
    {
        protected override void VisitVariableDeclaration(BoundVariableDeclaration node)
        {
            if (node.Name.StartsWith(AsyncSpill.AwaitFacts.Prefix, StringComparison.Ordinal))
                spilled[node.Name] = node.DeclaredType;
            base.VisitVariableDeclaration(node);
        }

        protected override void VisitNameExpression(BoundNameExpression node)
        {
            // After await expansion the declaration that originally introduced a
            // spill may sit on the resume path, while uses appear in the staged
            // protected body.  The reserved name and bound type remain stable,
            // so a use is sufficient to complete the state layout as well.
            if (node.Name.StartsWith(AsyncSpill.AwaitFacts.Prefix, StringComparison.Ordinal))
                spilled.TryAdd(node.Name, node.Type);
            base.VisitNameExpression(node);
        }
    }
}
