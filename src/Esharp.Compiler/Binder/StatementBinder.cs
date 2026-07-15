using Esharp.Diagnostics;
using Esharp.Syntax;

// Moved to Esharp.Binder (Pillar 2 — B2) per module-map.md.
// Consumes: frozen BoundTree/Symbol/Diagnostic contract (Pillar 1).
namespace Esharp.Binder;

/// Statement binding: declarations and assignments (with mutability checks),
/// control flow, try/catch, defer, select / async-let, and the statement
/// dispatch (which owns the statement-level span stamp).
internal sealed class StatementBinder : BinderUnit
{
    internal StatementBinder(Binder binder) : base(binder) { }


    // === Statements ===

    internal BoundBlockStatement BindBlock(BlockStatementSyntax syntax)
    {
        // Smart-casts established mid-block (a guard-return that narrows the rest of the
        // block) are visible to later statements but must not leak past the block —
        // save the narrowing state on entry and restore it on exit.
        var saved = SaveNarrows();
        var statements = syntax.Statements.Select(BindStatement).ToList();
        RestoreNarrows(saved);
        return new BoundBlockStatement(statements);
    }

    internal BoundStatement BindStatement(StatementSyntax syntax)
    {
        var bound = syntax switch
        {
            VariableDeclarationStatementSyntax v => BindVariableDeclaration(v),
            LetGuardStatementSyntax g => BindLetGuard(g),
            AssignmentStatementSyntax a => BindAssignment(a),
            CompoundAssignmentStatementSyntax c => BindCompoundAssignment(c),
            IfStatementSyntax i => BindIf(i),
            WhileStatementSyntax w => BindWhile(w),
            ForEachStatementSyntax f => BindForEach(f),
            ReturnStatementSyntax r => BindReturn(r),
            RaiseStatementSyntax ra => BindRaise(ra),
            ExpressionStatementSyntax e => new BoundExpressionStatement(Expressions.BindExpression(e.Expression)),
            BlockStatementSyntax b => BindBlock(b),
            MatchStatementSyntax m => Match.BindMatch(m),
            DeferStatementSyntax d => new BoundDeferStatement(BindBlock(d.Body)),
            AsyncLetStatementSyntax al => BindAsyncLet(al),
            SelectStatementSyntax sel => BindSelect(sel),
            TryStatementSyntax tr => BindTry(tr),
            ThrowStatementSyntax th => new BoundThrowStatement(th.Expression is null ? null : Expressions.BindExpression(th.Expression)),
            BreakStatementSyntax => new BoundBreakStatement(),
            ContinueStatementSyntax => new BoundContinueStatement(),
            ConstStatementSyntax c => BindConstStatement(c),
            YieldStatementSyntax y => BindStrayYield(y),
            MutYieldStatementSyntax y => BindStrayMutYield(y),
            _ => UnknownStatement(syntax),
        };
        bound.Span = syntax.Span;
        return bound;
    }

    // Total bind: a statement node the dispatch doesn't know is an internal
    // compiler error — report it located and keep binding.
    BoundStatement UnknownStatement(StatementSyntax syntax)
    {
        Diagnostics.Report(syntax.Span,
            $"ES9001: internal compiler error — no binder for statement node '{syntax.GetType().Name}'.");
        return new BoundExpressionStatement(new BoundErrorExpression());
    }

    // `raise EventName(args)` fires an event on the current `self`. Resolves the event
    // off self's type, binds args against the event delegate's Invoke shape, and carries
    // the delegate type for the emitter's null-safe read-then-Invoke lowering.
    BoundStatement BindRaise(RaiseStatementSyntax syntax)
    {
        BoundType? eventType = null;
        if (Scope.Lookup("self") is DataType dt && FieldsOf(dt) is { } fields)
        {
            eventType = fields.FirstOrDefault(f => f.IsEvent && f.Name == syntax.EventName)?.Bound;
        }
        if (eventType is null)
        {
            Diagnostics.Report(syntax.Span,
                $"ES2142: no event '{syntax.EventName}' on the current type — 'raise' fires a field-style event declared on the enclosing 'class'.");
            eventType = new VoidType();
        }

        var shape = Expressions.TryGetExpectedDelegateShape(eventType);
        var args = new List<BoundExpression>();
        for (var i = 0; i < syntax.Arguments.Count; i++)
        {
            var expected = shape is { } s && i < s.ParameterTypes.Count ? s.ParameterTypes[i] : null;
            args.Add(Expressions.BindExpression(syntax.Arguments[i], expected));
        }
        return new BoundRaiseStatement(syntax.EventName, eventType, args);
    }

    // Yield is a first-class FEATURE node: lowering needs its resolved value type and
    // source span to build the producer's awaited channel write.
    BoundStatement BindStrayYield(YieldStatementSyntax y)
    {
        if (Ctx.CurrentFunctionAllowsYield
            && Ctx.CurrentReturnType is ExternalType { Name: "IAsyncEnumerable", TypeArgs: [var elementType] })
            return new BoundYieldStatement(Expressions.BindExpression(y.Value, elementType));

        Diagnostics.Report(y.Span,
            "ES2131: 'yield' is only valid directly in a function whose return type is 'IAsyncEnumerable<T>'.");
        return new BoundExpressionStatement(Expressions.BindExpression(y.Value));
    }

    BoundStatement BindStrayMutYield(MutYieldStatementSyntax y)
    {
        // DeclarationBinder consumes the direct lend point while binding a scoped
        // mut accessor. A nested / ordinary statement position cannot lend a
        // location because no protected resume region would own it.
        Diagnostics.Report(y.Span,
            "ES2213: `yield &location` is only valid directly inside a property `mut { ... }` accessor.");
        return new BoundExpressionStatement(Expressions.BindExpression(y.Location));
    }

    BoundTryStatement BindTry(TryStatementSyntax syntax)
    {
        var body = BindBlock(syntax.Body);
        var catches = new List<BoundCatchClause>();
        foreach (var c in syntax.Catches)
        {
            var prevScope = Scope;
            Scope = Scope.Child();
            // An untyped catch that still binds — `catch (e)` — catches the universal
            // `System.Exception` and binds it (spec: "bound, any exception"), so the
            // binding carries `Exception` and the body may read `e.Message` etc. A bare
            // `catch` / `catch ()` (no binding) keeps a null type → catch-all, no slot.
            BoundType? excType = c.ExceptionType is not null ? ResolveType(c.ExceptionType) : null;
            if (c.BindingName is not null)
            {
                var bindType = excType ?? ResolveType(new NamedTypeSyntax("Exception"));
                DeclareLocal(c.BindingName, bindType, mutable: true, c.NameSpan, isParameter: false);
                excType ??= bindType;
            }
            // The guard sees the catch scope (so it may reference the bound exception),
            // and is bound before the body — it is the CLR exception filter for this clause.
            var guard = c.Guard is not null ? Expressions.BindExpression(c.Guard) : null;
            var catchBody = BindBlock(c.Body);
            catches.Add(new BoundCatchClause(excType, c.BindingName, catchBody, guard));
            Scope = prevScope;
        }
        return new BoundTryStatement(body, catches);
    }

    BoundStatement BindVariableDeclaration(VariableDeclarationStatementSyntax syntax)
    {
        // Handle ? unwrap specially
        if (syntax.Initializer is TryUnwrapExpressionSyntax tryUnwrap)
        {
            // Route through the shared authority so the Result-only guard (ES2191) and
            // unwrapped-type computation live in one place, not a divergent copy.
            var bound = Expressions.BindTryUnwrap(tryUnwrap);
            var declType = syntax.ExplicitType is not null ? ResolveHeapPointerAware(syntax.ExplicitType) : bound.UnwrappedType;
            var tuLocal = DeclareLocal(syntax.Name, declType, syntax.Mutable, syntax.NameSpan, isParameter: false,
                syntax.Representation);
            return new BoundVariableDeclaration(syntax.Mutable, syntax.Name, declType, bound) { Local = tuLocal };
        }

        // Propagate the explicit `let x: T = ...` annotation as the initializer's
        // expected type so function literals can land as function pointers when
        // `T` is `&(...)`, and other expected-type-driven inference still fires.
        var explicitTypeHint = syntax.ExplicitType is not null ? ResolveHeapPointerAware(syntax.ExplicitType) : null;
        var initializer = Expressions.BindExpression(syntax.Initializer, explicitTypeHint);
        var type = explicitTypeHint ?? initializer.Type;

        // A function literal deliberately carries InferredType while its body is being
        // bound: its final callable shape comes from its parameters, result, asyncness,
        // and possibly a target slot. Once an unannotated local owns that literal, the
        // local itself must have the concrete delegate type. Otherwise later uses such
        // as `await work()` see `object`, and async spilling likewise loses the delegate
        // ABI. An explicitly typed declaration remains authoritative (including fnptr
        // destinations); this is only the inferred-local form.
        if (explicitTypeHint is null && initializer is BoundFunctionLiteralExpression functionLiteral)
            type = InferFunctionLiteralDelegateType(functionLiteral);

        // A bare-value async function called from a synchronous function starts
        // immediately and binds as a deferred value. The lowering below keeps the
        // real ValueTask<T> in a hidden slot and joins it only at this name's first
        // use. Explicit Task/ValueTask returns deliberately keep their normal,
        // caller-handled shape.
        var isSyncFuture = explicitTypeHint is null
            && Ctx.CurrentFunctionName is { } callerName
            && Data.Symbols.TryGetFunction(callerName) is { IsAsync: false }
            && initializer is BoundCallExpression { Target: BoundNameExpression callee }
            && Data.Symbols.TryGetFunction(callee.Name) is { IsAsync: true, HasExplicitAsyncWrapperReturn: false, ReturnType: { } result };
        if (isSyncFuture)
            type = Data.Symbols.TryGetFunction(((BoundNameExpression)((BoundCallExpression)initializer).Target).Name)!.ReturnType!;

        // Boxing diagnostic: value type stored as interface reference
        if (type is InterfaceType && initializer.Type is DataType boxedDt
            && ClassifyData(boxedDt.Name) == DataClassification.Struct)
        {
            Diagnostics.Warn(syntax.Span,
                $"Value type '{boxedDt.Name}' will be boxed when stored as interface reference '{TypeDisplayName(type)}'. Consider using 'class' to avoid boxing.");
        }

        // Make the implicit store coercion explicit (value→interface box, T→T? wrap) as a
        // BoundConversion node, so CodeGen emits it from the node rather than re-deriving
        // it inline at the store. A no-op when the initializer already matches the slot.
        if (!isSyncFuture)
            initializer = Expressions.Coerce(initializer, type);

        var local = DeclareLocal(syntax.Name, type, syntax.Mutable, syntax.NameSpan, isParameter: false,
            syntax.Representation);
        return new BoundVariableDeclaration(syntax.Mutable, syntax.Name, type, initializer)
        {
            Local = local,
            IsSyncFuture = isSyncFuture,
        };
    }

    static BoundType InferFunctionLiteralDelegateType(BoundFunctionLiteralExpression literal)
    {
        var returnType = literal.IsAsync
            ? WrapAsyncLiteralReturn(literal.ReturnType, literal.AsyncShape)
            : literal.ReturnType;
        var parameterTypes = literal.Parameters.Select(parameter => parameter.Type).ToList();
        if (returnType is VoidType or PrimitiveType { Name: "void" })
            return parameterTypes.Count == 0 ? new ExternalType("Action") : new ExternalType("Action", parameterTypes);
        return new ExternalType("Func", [.. parameterTypes, returnType]);
    }

    // The delegate observes the wrapper returned by an async lambda, while the
    // lambda's bound ReturnType remains its source-level result for AsyncLowering.
    static BoundType WrapAsyncLiteralReturn(BoundType resultType, AsyncReturnShape shape)
    {
        // `func() -> Task<T> { await ... }` already names the callable wrapper.
        // Only an uncolored result (`func() -> T` or an inferred arrow result)
        // needs ValueTask<T> synthesized around it for the delegate signature.
        if (shape is AsyncReturnShape.Void
            || resultType is ExternalType { Name: "Task" or "ValueTask" })
            return resultType;
        var isVoid = resultType is VoidType or PrimitiveType { Name: "void" };
        return shape switch
        {
            AsyncReturnShape.Void => new PrimitiveType("void"),
            AsyncReturnShape.Task => isVoid ? new ExternalType("Task") : new ExternalType("Task", [resultType]),
            _ => isVoid ? new ExternalType("ValueTask") : new ExternalType("ValueTask", [resultType]),
        };
    }

    BoundStatement BindConstStatement(ConstStatementSyntax syntax)
    {
        var explicitTypeHint = syntax.Type is not null ? ResolveHeapPointerAware(syntax.Type) : null;
        var bound = Expressions.BindExpression(syntax.Value, explicitTypeHint);
        var lit = ConstantFolder.Fold(bound);
        if (lit is null)
        {
            Diagnostics.Report(syntax.Span,
                $"ES1011: 'const {syntax.Name}' must fold to a literal value at compile time. Use 'let' for runtime values.");
            lit = new BoundLiteralExpression(0, "0", new PrimitiveType("int"));
        }
        var type = explicitTypeHint ?? lit.Type;
        Scope.DeclareConst(syntax.Name, lit);
        return new BoundConstStatement(syntax.Name, type, lit);
    }


    BoundLetGuard BindLetGuard(LetGuardStatementSyntax syntax)
    {
        BoundExpression initializer;
        if (syntax.Initializer is TryUnwrapExpressionSyntax tu)
            initializer = Expressions.BindExpression(tu.Inner);
        else
            initializer = Expressions.BindExpression(syntax.Initializer);

        var type = syntax.ExplicitType is not null ? ResolveType(syntax.ExplicitType) : initializer.Type;
        // After the guard succeeds the binding is the unwrapped, non-null value, so
        // it carries the inner type (`int` for an `int?` initializer, not
        // `Nullable<int>`). Reference nullables already resolve to the bare type.
        if (type is NullableType nt) type = nt.Inner;
        DeclareLocal(syntax.Name, type, mutable: false, syntax.NameSpan, isParameter: false);
        var elseBody = BindBlock(syntax.ElseBody);
        return new BoundLetGuard(syntax.Name, type, initializer, elseBody);
    }

    BoundAssignment BindAssignment(AssignmentStatementSyntax syntax)
    {
        var target = Expressions.BindExpression(syntax.Target);
        CheckMutability(target, syntax.Target);
        // Target-type the RHS by the assignment slot, so target-typed forms (a bare `default`,
        // a `.case`, an `if`-expression) resolve against the destination type.
        var value = Expressions.BindExpression(syntax.Expression, target.Type);
        // Diagnostic: assigning T to *T without & is an error
        if (target.Type is HeapPointerBoundType && value.Type is DataType valDt
            && !(value is BoundHeapAllocExpression) && !(value is BoundLiteralExpression { Value: null })
            && !(value is BoundAddressOfVariableExpression))
        {
            Diagnostics.Report(syntax.Span,
                $"Cannot assign '{valDt.Name}' to '*{valDt.Name}' — use 'new {valDt.Name} {{ ... }}' to heap-allocate.");
        }
        return new(target, value);
    }

    BoundCompoundAssignment BindCompoundAssignment(CompoundAssignmentStatementSyntax syntax)
    {
        var target = Expressions.BindExpression(syntax.Target);
        // `+=` / `-=` on an event field is subscription, not mutation — events are
        // declared immutable (only the accessors touch the backing field), so the
        // immutable-field check must not fire here. The expected handler type is the
        // event's delegate type, so a function-literal handler binds to it.
        if (IsEventSubscriptionTarget(target, syntax.Operator, out var eventDelegateType))
            return new BoundCompoundAssignment(target, syntax.Operator,
                Expressions.BindExpression(syntax.Value, eventDelegateType))
            { IsEventSubscription = true };
        CheckMutability(target, syntax.Target);
        return new(target, syntax.Operator, Expressions.BindExpression(syntax.Value));
    }

    bool IsEventSubscriptionTarget(BoundExpression target, SyntaxTokenKind op, out BoundType? eventDelegateType)
    {
        eventDelegateType = null;
        if (op is not (SyntaxTokenKind.PlusEquals or SyntaxTokenKind.MinusEquals)) return false;
        if (target is not BoundMemberAccessExpression member) return false;
        var t = member.Target.Type is HeapPointerBoundType hp ? hp.Inner : member.Target.Type;
        if (t is DataType dt && FieldsOf(dt) is { } fields)
        {
            var ev = fields.FirstOrDefault(f => f.IsEvent && f.Name == member.MemberName);
            if (ev?.Bound is { } evType) { eventDelegateType = evType; return true; }
        }

        // BCL events have no in-compilation FieldSymbol. Preserve the same event
        // fact through lowering by looking at the closed reflected receiver type.
        if (ResolveBoundTypeToRuntime(t)?.GetEvent(member.MemberName) is { EventHandlerType: { } handler })
        {
            eventDelegateType = MapRuntimeTypeToBoundType(handler);
            return true;
        }
        return false;
    }

    void CheckMutability(BoundExpression target, ExpressionSyntax syntax)
    {
        if (target is BoundNameExpression name)
        {
            var mutable = Scope.LookupMutable(name.Name);
            var namespaceInitPropertyWrite = Ctx.InNamespaceInitializer
                && (name.Symbol is null
                    || name.Symbol is Esharp.Symbols.FieldSymbol
                    {
                        IsProperty: true,
                        DeclaringType.TypeKind: Esharp.Symbols.TypeSymbolKind.NamespaceHost,
                    })
                && Data.NamespaceInitWritableProperties.TryGetValue(Ctx.CurrentNamespace, out var writable)
                && writable.Contains(name.Name);
            if (mutable == false && !namespaceInitPropertyWrite)
                Diagnostics.Report(syntax.Span, $"Cannot assign to immutable binding '{name.Name}'.");
        }
        else if (target is BoundMemberAccessExpression member)
        {
            var targetType = member.Target.Type;
            // Mutating a field through an immutable value-type binding mutates the value
            // itself — rejected. This is how a `readonly func (c: T)` receiver enforces its
            // no-mutation contract (`c` is an immutable `in this` binding), and it holds for
            // any `let`-bound struct. A class binding is exempt: the reference is immutable,
            // the object it points at is not.
            if (member.Target is BoundNameExpression rootName
                && Scope.LookupMutable(rootName.Name) == false
                && targetType is DataType rootDt && Data.Symbols.DataDecl(rootDt.Name) is { IsRef: false })
                Diagnostics.Report(syntax.Span,
                    $"Cannot assign to field '{member.MemberName}' through immutable binding '{rootName.Name}'.");
            if (targetType is DataType dt && Data.Symbols.DataDecl(dt.Name) is { } decl)
            {
                var field = decl.Fields.FirstOrDefault(f => f.Name == member.MemberName);
                if (field is not null && !field.Mutable && !PropertyMutAllowsWrite(decl, field))
                    Diagnostics.Report(syntax.Span, $"Cannot assign to immutable field '{member.MemberName}'.");
                // A captured header param is a synthesized private `let` field —
                // it stores exactly once, in the primary ctor. Methods may read it,
                // never write it.
                else if (field is null && decl.HeaderParameters?.Any(p => p.Name == member.MemberName) == true)
                    Diagnostics.Report(syntax.Span,
                        $"Cannot assign to captured header parameter '{member.MemberName}' — it is an immutable ('let') capture field.");
            }
        }
    }

    static bool PropertyMutAllowsWrite(DataDeclarationSyntax owner, FieldSyntax property)
    {
        var prop = property.Property;
        if (prop is null) return false;

        // A direct mut borrows the named field's representation, so mutability is
        // exactly that storage's mutability, not the spelling (`let`/`var`) of the
        // public property declaration.
        if (prop.MutStorageName is { } directStorage)
            return owner.Fields.FirstOrDefault(f => f.Name == directStorage)?.Mutable == true;

        // A scoped mut lends the local that appears at its contextual yield point.
        // Only a preceding `var` declaration makes the lend writable. This is the
        // source-level counterpart of DeclarationBinder's bound capability fact.
        if (prop.ScopedMutBody is not { } scoped) return false;
        var declared = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var statement in scoped.Statements)
        {
            if (statement is VariableDeclarationStatementSyntax local)
            {
                declared[local.Name] = local.Mutable;
                continue;
            }
            if (statement is MutYieldStatementSyntax
                {
                    Location: AddressOfExpressionSyntax
                    {
                        Target: NameExpressionSyntax { Name: var name }
                    }
                })
                return declared.TryGetValue(name, out var mutable) && mutable;
        }
        return false;
    }

    BoundIfStatement BindIf(IfStatementSyntax syntax)
    {
        var cond = Expressions.BindExpression(syntax.Condition);
        var (pos, neg) = Narrowing.Extract(cond);

        // Bind the `then` branch under the facts that hold when the condition is true.
        var saved = SaveNarrows();
        ApplyNarrows(pos);
        var then = BindStatement(syntax.ThenStatement);
        RestoreNarrows(saved);

        // …and the `else` branch under the facts that hold when it is false.
        BoundStatement? els = null;
        if (syntax.ElseStatement is not null)
        {
            var savedElse = SaveNarrows();
            ApplyNarrows(neg);
            els = BindStatement(syntax.ElseStatement);
            RestoreNarrows(savedElse);
        }

        // Guard-return: when one branch always exits (returns / throws / diverges), the
        // *other* branch's facts hold for the rest of the enclosing block — the
        // `if x is not T { return }` idiom narrows `x` to `T` below it. The block's
        // save/restore (BindBlock) bounds how far these persist.
        var thenExits = Match.BranchAlwaysExits(then);
        var elseExits = els is not null && Match.BranchAlwaysExits(els);
        if (thenExits && !elseExits) ApplyNarrows(neg);
        else if (elseExits && !thenExits) ApplyNarrows(pos);

        return new BoundIfStatement(cond, then, els);
    }

    BoundWhileStatement BindWhile(WhileStatementSyntax syntax) =>
        new(Expressions.BindExpression(syntax.Condition), BindStatement(syntax.Body));

    BoundForEachStatement BindForEach(ForEachStatementSyntax syntax)
    {
        // Preserve the parser's async-iteration fact through binding. AsyncForeachLowering
        // owns the protocol expansion after types are known.
        if (syntax.IsAwait && Ctx.InNamespaceInitializer)
        {
            Diagnostics.Report(syntax.Span,
                "ES2208: 'await for' is not valid inside a namespace 'init' block.");
        }
        if (syntax.IsAwait)
            Ctx.CurrentFunctionHasAwait = true;
        var collection = Expressions.BindExpression(syntax.Collection);
        var elementType = InferForEachElementType(collection.Type);
        var prevScope = Scope;
        Scope = Scope.Child();
        DeclareLocal(syntax.Identifier, elementType, mutable: true, syntax.NameSpan, isParameter: false);
        if (syntax.DestructuredNames is { Count: > 0 })
            for (var i = 0; i < syntax.DestructuredNames.Count; i++)
            {
                var nameSpan = syntax.DestructuredNameSpans is { } spans && i < spans.Count
                    ? spans[i] : default;
                DeclareLocal(syntax.DestructuredNames[i], DestructuredElementType(elementType, i),
                    mutable: true, nameSpan, isParameter: false);
            }
        var body = BindStatement(syntax.Body);
        Scope = prevScope;
        return new BoundForEachStatement(syntax.Identifier, collection, body, elementType,
            syntax.DestructuredNames, IsAwait: syntax.IsAwait);
    }

    /// The static type of the i-th name in a `for (a, b) in coll` destructure, so member
    /// access on it (`a.Length`) resolves. A `(T1, T2)` tuple element gives `Ti`; a
    /// `KeyValuePair<K, V>` element (the `for (k, v) in dict` case) gives K for index 0 and
    /// V for index 1. Anything else stays `var`.
    BoundType DestructuredElementType(BoundType elementType, int index)
    {
        if (elementType is TupleType tt && index < tt.ElementTypes.Count)
            return tt.ElementTypes[index];

        if (elementType is ExternalType { TypeArgs.Count: > 0 } ext
            && (ext.Name == "KeyValuePair" || ext.Name.EndsWith(".KeyValuePair") || ext.Name.EndsWith("KeyValuePair`2"))
            && index < ext.TypeArgs.Count)
        {
            return ext.TypeArgs[index];
        }

        return InferredType.Instance;
    }

    /// <summary>
    /// Infers the element type for a for..in loop by resolving the collection's runtime type
    /// and extracting T from IEnumerable&lt;T&gt;.
    /// </summary>
    BoundType InferForEachElementType(BoundType collectionType)
    {
        // Ranges produce int
        if (collectionType is PrimitiveType { Name: "Range" or "int" })
            return new PrimitiveType("int");

        // Chan<T> → T
        if (collectionType is ChanType ct)
            return ct.ElementType;

        // `T[]` carries its element structurally.  Arrays are not ExternalType,
        // so letting them fall through used to infer `object`; lowering then cast
        // an `int[]` to IEnumerable<object> and emitted an object index for the
        // original `int[]`.  Preserve the exact element before any reflection path.
        if (collectionType is ArrayBoundType array)
            return array.ElementType;

        // IAsyncEnumerable<T> carries the same structural element contract as its
        // synchronous counterpart; await-for must preserve T rather than falling
        // through reflection and erasing it to object.
        if (collectionType is ExternalType { Name: "IAsyncEnumerable", TypeArgs: [var asyncElement] })
            return asyncElement;

        // System.String's enumerator is non-generic at the surface but its element
        // contract is always char. Preserve that fact during binding so lowering
        // never has to guess from an erased collection expression.
        if (collectionType is PrimitiveType { Name: "string" })
            return new PrimitiveType("char");

        // User-defined class/struct implementing IEnumerable<T> on a *concrete* receiver
        // (not the interface type, which hits the ExternalType branch below). The runtime
        // form erases the element to `object`; re-derive T from the symbol's declared
        // conformance set so `for v in Seq(...)` where `Seq : IEnumerable<int>` binds
        // `v : int` instead of `object` (which would fail IL verification on use).
        if (collectionType is DataType { Symbol: { } userSym }
            && ElementFromConformances(userSym, "IEnumerable") is { } userElem)
            return userElem;

        // Generic external with a user-defined type arg (`List<Json>`): the runtime
        // form erases the arg to `object`. Re-derive the element type from the
        // *bound* args via the open `IEnumerable<T>`'s generic-parameter position
        // (List<T> → T at 0). Falls through to reflection when the element isn't a
        // bare type parameter (e.g. Dictionary<K,V> → KeyValuePair<K,V>).
        if (collectionType is ExternalType { TypeArgs.Count: > 0 } extGen)
        {
            var boundArgs = extGen.TypeArgs;
            var openType = FindOpenGenericByName(extGen.Name, boundArgs.Count);
            if (openType is not null)
            {
                var ienumOpen = openType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                if (ienumOpen is null && openType.IsGenericType
                    && openType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ienumOpen = openType;
                var elemArg = ienumOpen?.GetGenericArguments()[0];
                if (elemArg is { IsGenericParameter: true }
                    && elemArg.GenericParameterPosition < boundArgs.Count)
                    return boundArgs[elemArg.GenericParameterPosition];
            }
        }

        // ExternalType — resolve via reflection and find IEnumerable<T>
        if (collectionType is ExternalType ext)
        {
            var runtime = ResolveExternalToRuntime(ext);
            if (runtime is not null)
            {
                // Array → element type directly
                if (runtime.IsArray)
                    return MapRuntimeTypeToBoundType(runtime.GetElementType()!);

                // IEnumerable<T> → T
                var ienumT = runtime.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                if (ienumT is not null)
                    return MapRuntimeTypeToBoundType(ienumT.GetGenericArguments()[0]);

                // The type itself might BE IEnumerable<T> (e.g. IEnumerable<JsonElement>)
                if (runtime.IsGenericType && runtime.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return MapRuntimeTypeToBoundType(runtime.GetGenericArguments()[0]);
            }
        }

        return InferredType.Instance;
    }

    /// Walk a user type's declared conformances (transitively, plus its base class)
    /// for `ifaceName<T>` and return the bound `T` — used by `for-in` (IEnumerable)
    /// and `await for` (IAsyncEnumerable) over a concrete user receiver. Null when the
    /// type does not conform with a single type argument.
    BoundType? ElementFromConformances(Esharp.Symbols.TypeSymbol sym, string ifaceName)
    {
        foreach (var iface in sym.Interfaces)
        {
            if ((iface.Symbol.Name == ifaceName || iface.Symbol.Name == ifaceName + "`1")
                && iface.TypeArguments.Count == 1)
                return BoundFromTypeRef(iface.TypeArguments[0]);
            // The declared interface may itself extend `ifaceName<T>`.
            if (ElementFromConformances(iface.Symbol, ifaceName) is { } nested)
                return nested;
        }
        return sym.BaseType is { } b ? ElementFromConformances(b, ifaceName) : null;
    }

    /// Map a conformance type argument to its bound form: the symbol's canonical
    /// bound view when present, else a primitive by name (`int`, `string`, …), else
    /// an external reference.
    static BoundType BoundFromTypeRef(Esharp.Symbols.TypeRef r) =>
        r.Symbol.BoundView is { } bv ? bv
        : r.Symbol.Name is "int" or "string" or "bool" or "float" or "double" or "byte"
            or "char" or "long" or "short" or "uint" or "ulong" or "ushort" or "sbyte" or "decimal"
            ? new PrimitiveType(r.Symbol.Name)
        : new ExternalType(r.Symbol.Name);

    BoundReturnStatement BindReturn(ReturnStatementSyntax syntax)
    {
        if (Ctx.InNamespaceInitializer)
            Diagnostics.Report(syntax.Span, "ES2207: 'return' is not valid inside a namespace 'init' block.");
        // Thread the declared return type so target-typed conversions (method-group →
        // delegate, lambda → delegate/fn-ptr) fire in return position too.
        return new BoundReturnStatement(syntax.Expression is not null
            ? Expressions.BindExpression(syntax.Expression, Ctx.CurrentReturnType)
            : null);
    }

    BoundAsyncLetStatement BindAsyncLet(AsyncLetStatementSyntax syntax)
    {
        if (Ctx.InNamespaceInitializer)
            Diagnostics.Report(syntax.Span, "ES2208: 'async let' is not valid inside a namespace 'init' block.");
        // The async-let lowering OWNS the suspension point: it starts the
        // initializer eagerly and injects the implicit await at first use.
        // Binding the initializer must therefore NOT auto-await a call to an
        // async user function — otherwise the initializer becomes a
        // BoundAwaitExpression instead of the raw call, and the lowering's
        // classifier can't see the awaitable (it would mis-fire ES3005). The
        // `Ctx.BindingAwaitInner` flag suppresses that auto-await, exactly as it
        // does for the inner of an explicit `await`.
        var prev = Ctx.BindingAwaitInner;
        Ctx.BindingAwaitInner = true;
        var initializer = Expressions.BindExpression(syntax.Initializer);
        Ctx.BindingAwaitInner = prev;

        // The user-visible binding is the UNWRAPPED value: `async let a =
        // Task.FromResult(20)` makes `a` an `int`, awaited at first use. Unwrap an
        // awaitable initializer (`Task<int>` → `int`); a plain value (a sync user
        // call the lowering will wrap in Task.Run) keeps its own type.
        BoundType type;
        if (syntax.ExplicitType is not null)
            type = ResolveType(syntax.ExplicitType);
        else
        {
            var unwrapped = Expressions.InferAwaitResultType(initializer.Type);
            type = unwrapped is InferredType ? initializer.Type : unwrapped;
        }
        DeclareLocal(syntax.Name, type, mutable: false, syntax.NameSpan, isParameter: false);
        Ctx.CurrentFunctionHasAwait = true;
        return new BoundAsyncLetStatement(syntax.Name, type, initializer);
    }

    BoundSelectStatement BindSelect(SelectStatementSyntax syntax)
    {
        // select lowers to Esharp.Stdlib.ChanSelect.Select(arms) which internally waits
        // via .AsTask().GetAwaiter().GetResult() — the enclosing function does not need to
        // be async. The transpiler also uses the same helper now.
        var arms = new List<BoundSelectArm>();
        var captures = new List<BoundCapturedVariable>();

        foreach (var arm in syntax.Arms)
        {
            var prevScope = Scope;
            var armScope = Scope.Child();
            Scope = armScope;

            BoundExpression? channel = arm.Channel is not null ? Expressions.BindExpression(arm.Channel) : null;
            BoundExpression? value = arm.Value is not null ? Expressions.BindExpression(arm.Value) : null;

            BoundType? bindingType = null;
            if (arm.Binding is not null)
            {
                // The recv binding's type IS the channel's element type. Anything
                // less (an inference hole, erased to `object` downstream) types the
                // hoisted display-class field `object` while the TryReceive store
                // writes the raw element — unverifiable IL.
                bindingType = channel?.Type is ChanType chanType
                    ? chanType.ElementType
                    : InferredType.Instance;
                DeclareLocal(arm.Binding, bindingType, mutable: true, arm.NameSpan, isParameter: false);
            }

            var body = BindBlock(arm.Body);
            arms.Add(new BoundSelectArm(arm.Kind, arm.Binding, bindingType, channel, value, body));

            // Every arm becomes a method on the enclosing function's display class, so any
            // name referenced in the arm (body AND channel/value expressions) that isn't local
            // to the arm (e.g. outer `let ch` / `var total`) must be hoisted.
            Expressions.CollectCaptures(body, armScope, captures);
            if (channel is not null) Expressions.CollectCapturesExpr(channel, armScope, captures);
            if (value is not null) Expressions.CollectCapturesExpr(value, armScope, captures);

            Scope = prevScope;
        }
        return new BoundSelectStatement(arms, captures);
    }
}
