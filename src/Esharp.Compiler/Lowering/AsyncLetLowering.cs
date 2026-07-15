using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Symbols;

namespace Esharp.Lowering;

/// <summary>
/// Lowers Swift-style <c>async let</c> bindings into an eager task-start at the declaration plus an
/// implicit await at the binding's first textual use:
/// <code>
///   async let a = loadAsync(2)        var __async_let_a = loadAsync(2)   // started here, in flight
///   async let b = loadAsync(3)   →    var __async_let_b = loadAsync(3)   // started here too — fan-out
///   let av = a?                       let a = await __async_let_a;  let av = a?
///   let bv = b?                       let b = await __async_let_b;  let bv = b?
/// </code>
/// Both tasks start at their declarations, so their work overlaps; each join happens where the value
/// is first used, in use order. After this pass the tree carries only plain
/// <see cref="BoundVariableDeclaration"/> + <see cref="BoundAwaitExpression"/>, so neither backend
/// needs a dedicated <see cref="BoundAsyncLetStatement"/> case.
///
/// <para><b>Join at the exact first use.</b> The await is injected before the first statement whose
/// own directly-evaluated expressions (a condition, an initializer, a captured closure) reference the
/// name — never before a statement that only references it inside a nested control-flow body. A
/// reference reached only through an <c>if</c>/<c>while</c>/<c>match</c> arm is joined when the
/// recursion enters that body, so an <c>async let</c> over a <c>Result</c> joined with <c>?</c>
/// short-circuits the enclosing function only on the path that actually uses it — matching the spec,
/// and unlike a join hoisted to the enclosing block, which would await (and propagate) on a path that
/// may never run. Once joined on a path the name is removed from the pending set, so it is never
/// re-awaited in a nested or later statement. A closure body is its own async scope (its own pending
/// set), so an <c>async let</c> declared inside a lambda is lowered too.</para>
///
/// <para>Invoked at bind time from <c>DeclarationBinder.BindFunction</c> (before
/// <c>AsyncSpillLowering</c>), so the rewrite is in place before either backend sees the tree.</para>
/// </summary>
public sealed class AsyncLetLowering
{
    readonly SymbolTable   _symbols;
    readonly DiagnosticBag _diagnostics;

    AsyncLetLowering(SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _symbols     = symbols;
        _diagnostics = diagnostics;
    }

    /// <param name="body">The function body to rewrite.</param>
    /// <param name="symbols">Distinguishes an async user function (already in flight) from a sync one
    /// (wrapped in <c>Task.Run</c> for real parallelism) and from an external/awaitable call.</param>
    /// <param name="diagnostics">Sink for ES3004 (sync call thread-pooled) / ES3005 (non-call).</param>
    public static BoundBlockStatement Rewrite(BoundBlockStatement body, SymbolTable symbols, DiagnosticBag diagnostics)
        => new AsyncLetLowering(symbols, diagnostics)
            .RewriteBlock(body, new Dictionary<string, Pending>(StringComparer.Ordinal));

    /// A binding whose task is started but not yet joined. <see cref="TaskType"/> is the started
    /// <c>Task&lt;T&gt;</c>'s type (what the await sees); <see cref="ValueType"/> is the unwrapped
    /// <c>T</c> the join yields. <see cref="NeedsAwait"/> is false only for an ES3005 fallback stored
    /// directly so binding can continue.
    readonly record struct Pending(string TaskName, BoundType TaskType, BoundType ValueType, bool NeedsAwait);

    // ── Block walk: declare the eager task, join at first use ────────────────────
    BoundBlockStatement RewriteBlock(BoundBlockStatement block, Dictionary<string, Pending> outer)
    {
        // A copy: a name joined on THIS path must not leak to a sibling block, and the set flowing
        // INTO a nested block reflects what is still unjoined at that point.
        var pending = new Dictionary<string, Pending>(outer, StringComparer.Ordinal);
        var result  = new List<BoundStatement>(block.Statements.Count);

        foreach (var stmt in block.Statements)
        {
            if (stmt is BoundAsyncLetStatement al)
            {
                var (started, needsAwait) = ClassifyAndStart(al);
                var taskName = "__async_let_" + al.Name;
                result.Add(Synth.Var(taskName, started.Type, started));
                pending[al.Name] = new Pending(taskName, started.Type, al.DeclaredType, needsAwait);
                continue;
            }

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            CollectDirectReferences(stmt, referenced);

            foreach (var name in pending.Keys.ToList())
            {
                if (!referenced.Contains(name)) continue;

                var p       = pending[name];
                var taskRef = Synth.Name(p.TaskName, p.TaskType);
                BoundExpression init = p.NeedsAwait ? new BoundAwaitExpression(taskRef, p.ValueType) : taskRef;
                result.Add(Synth.Let(name, p.ValueType, init));
                pending.Remove(name);   // joined on this path — never re-await later or in a nested body
            }

            result.Add(RewriteStatement(stmt, pending));
        }

        return new BoundBlockStatement(result) { Span = block.Span };
    }

    // ── Statement walk: recurse into bodies (threading pending) and into closures ──
    BoundStatement RewriteStatement(BoundStatement stmt, Dictionary<string, Pending> pending)
    {
        BoundStatement result = stmt switch
        {
            BoundBlockStatement b => RewriteBlock(b, pending),

            BoundIfStatement i => i with
            {
                Condition = RewriteClosures(i.Condition),
                Then = RewriteStatement(i.Then, pending),
                Else = i.Else is null ? null : RewriteStatement(i.Else, pending),
            },
            BoundWhileStatement w => w with
            {
                Condition = RewriteClosures(w.Condition),
                Body = RewriteStatement(w.Body, pending),
            },
            BoundForEachStatement fe => fe with
            {
                Collection = RewriteClosures(fe.Collection),
                Body = RewriteStatement(fe.Body, pending),
            },
            BoundMatchStatement m => m with
            {
                Subject = RewriteClosures(m.Subject),
                Arms = [.. m.Arms.Select(a => a with { Body = (BoundBlockStatement)RewriteStatement(a.Body, pending) })],
            },
            BoundTryStatement tr => tr with
            {
                Body = (BoundBlockStatement)RewriteStatement(tr.Body, pending),
                Catches = [.. tr.Catches.Select(c => c with { Body = (BoundBlockStatement)RewriteStatement(c.Body, pending) })],
            },
            BoundDeferStatement d => d with { Body = (BoundBlockStatement)RewriteStatement(d.Body, pending) },
            BoundLetGuard g => g with
            {
                Initializer = RewriteClosures(g.Initializer),
                ElseBody = (BoundBlockStatement)RewriteStatement(g.ElseBody, pending),
            },

            // Leaf statements: rewrite their expressions so an async let declared inside a closure
            // body is lowered (each closure is its own async scope).
            BoundVariableDeclaration v => v with { Initializer = RewriteClosures(v.Initializer) },
            BoundAssignment a => a with { Target = RewriteClosures(a.Target), Value = RewriteClosures(a.Value) },
            BoundCompoundAssignment ca => ca with { Target = RewriteClosures(ca.Target), Value = RewriteClosures(ca.Value) },
            BoundExpressionStatement e => e with { Expression = RewriteClosures(e.Expression) },
            BoundReturnStatement { Expression: { } re } r => r with { Expression = RewriteClosures(re) },
            BoundThrowStatement { Expression: { } te } t => t with { Expression = RewriteClosures(te) },

            _ => stmt,
        };

        if (!ReferenceEquals(result, stmt)) result.Span = stmt.Span;
        return result;
    }

    // A closure body is its own async scope — rewrite it with a fresh pending set. (A reference from
    // the closure to an ENCLOSING async let was already joined before the closure was created, by the
    // capture-aware reference collection below.) Recurse through the expression forms that can hold a
    // closure so a deeply-nested lambda is reached.
    BoundExpression RewriteClosures(BoundExpression e) => e switch
    {
        BoundFunctionLiteralExpression fl => fl with { Body = RewriteBlock(fl.Body, new(StringComparer.Ordinal)) },
        BoundSpawnExpression sp           => sp with { Body = RewriteBlock(sp.Body, new(StringComparer.Ordinal)) },

        BoundCallExpression c             => c with { Target = RewriteClosures(c.Target), Arguments = [.. c.Arguments.Select(RewriteClosures)] },
        BoundMemberAccessExpression ma    => ma with { Target = RewriteClosures(ma.Target) },
        BoundBinaryExpression b           => b with { Left = RewriteClosures(b.Left), Right = RewriteClosures(b.Right) },
        BoundUnaryExpression u            => u with { Operand = RewriteClosures(u.Operand) },
        BoundConversion cv                => cv with { Operand = RewriteClosures(cv.Operand) },
        BoundConditionalExpression co     => co with { Condition = RewriteClosures(co.Condition), Consequence = RewriteClosures(co.Consequence), Alternative = RewriteClosures(co.Alternative) },
        BoundObjectCreationExpression oc  => oc with { Fields = [.. oc.Fields.Select(f => f with { Value = RewriteClosures(f.Value) })] },
        BoundIndexExpression ix           => ix with { Target = RewriteClosures(ix.Target), Index = RewriteClosures(ix.Index) },
        BoundTupleLiteralExpression tl    => tl with { Elements = [.. tl.Elements.Select(RewriteClosures)] },
        BoundListLiteralExpression ll     => ll with { Elements = [.. ll.Elements.Select(RewriteClosures)] },
        BoundAwaitExpression aw           => aw with { Inner = RewriteClosures(aw.Inner) },
        BoundResultCallExpression rc      => rc with { Argument = RewriteClosures(rc.Argument) },
        BoundTryUnwrapExpression tu       => tu with { Inner = RewriteClosures(tu.Inner) },
        _ => e,
    };

    // ── Classify the initializer and start the task ──────────────────────────────
    // A: external/awaitable call (X.Method(args))            → use as-is, await at first use
    // B: call to an async user function                      → already returns ValueTask<T> at IL
    //    level; re-type to `var` so the slot is the real awaitable, await at first use
    // C: call to a SYNC user function                        → wrap in Task.Run for real fan-out (ES3004)
    // D: non-call                                            → ES3005; store directly, no await
    (BoundExpression Started, bool NeedsAwait) ClassifyAndStart(BoundAsyncLetStatement al)
    {
        var init = al.Initializer;

        if (init is BoundCallExpression { Target: BoundMemberAccessExpression { Target: BoundNameExpression } })
            return (init, true);   // A

        if (init is BoundCallExpression { Target: BoundNameExpression callee } call)
        {
            if (_symbols.TryGetFunction(callee.Name) is { IsAsync: true } asyncMethod)
            {
                // MethodSymbol.ReturnType is the call-site awaitable shape populated by
                // signature binding (normally ValueTask<T> for an uncoloured async func).
                // Erasing it to `var` made AsyncLowering resolve the awaiter on Object.
                var declaredType = asyncMethod.ReturnType ?? call.Type;
                var awaitableType = asyncMethod.HasExplicitAsyncWrapperReturn
                    ? declaredType
                    : declaredType is VoidType or PrimitiveType { Name: "void" }
                        ? new ExternalType("ValueTask")
                        : new ExternalType("ValueTask", [declaredType]);
                return (call with { Type = awaitableType }, true);   // B
            }

            if (_symbols.HasFunction(callee.Name))
            {
                _diagnostics.Warn(al.Span,
                    $"ES3004: async let '{al.Name}': call to '{callee.Name}' will run on the thread pool. " +
                    $"Add await inside '{callee.Name}' to make it naturally async, or wrap explicitly in Task.Run.");
                return WrapInTaskRun(al, init);   // C
            }

            return (init, true);   // a call to a name that isn't a user function (e.g. a delegate local)
        }

        _diagnostics.Report(al.Span,   // D
            $"ES3005: async let '{al.Name}': initializer must be a function call or an awaitable expression. " +
            $"Wrap non-call expressions in Task.Run(() => ...).");
        return (init, false);
    }

    // Wrap a sync call in `Task.Run<TResult>(() => call)` so it genuinely runs on the thread pool.
    // The explicit type argument selects the generic `Task.Run<TResult>(Func<TResult>)` overload (not
    // the `Action` one that returns a bare `Task`), so the result is the `Task<TResult>` the join awaits.
    (BoundExpression Started, bool NeedsAwait) WrapInTaskRun(BoundAsyncLetStatement al, BoundExpression call)
    {
        var lambda = new BoundFunctionLiteralExpression(
            Parameters: [], ReturnType: al.DeclaredType,
            Body: Synth.Block(new BoundReturnStatement(call)),
            CapturedVariables: []);

        var taskRun = new BoundCallExpression(
            Target: Synth.Member(Synth.Name("Task", new ExternalType("Task")), "Run", InferredType.Instance),
            Arguments: [lambda],
            Type: new ExternalType("Task", [al.DeclaredType]),
            ExplicitTypeArguments: [al.DeclaredType]);
        return (taskRun, true);
    }

    // ── Reference collection ─────────────────────────────────────────────────────

    // Names referenced by a statement's OWN directly-evaluated expressions — the positions that run
    // whenever control reaches the statement (a condition, an initializer, a call, a captured closure).
    // It does NOT descend into a nested control-flow BODY (an if/while/for/match/try/defer block):
    // those run conditionally or deferred, so the join for a name first-used there is injected when the
    // recursion enters that body, not hoisted before the whole statement.
    static void CollectDirectReferences(BoundStatement stmt, HashSet<string> acc)
    {
        switch (stmt)
        {
            case BoundVariableDeclaration v:     CollectRefs(v.Initializer, acc); break;
            case BoundAssignment a:              CollectRefs(a.Target, acc); CollectRefs(a.Value, acc); break;
            case BoundCompoundAssignment ca:     CollectRefs(ca.Target, acc); CollectRefs(ca.Value, acc); break;
            case BoundExpressionStatement e:     CollectRefs(e.Expression, acc); break;
            case BoundReturnStatement r:         if (r.Expression is { } re) CollectRefs(re, acc); break;
            case BoundThrowStatement t:          if (t.Expression is { } te) CollectRefs(te, acc); break;
            case BoundIfStatement i:             CollectRefs(i.Condition, acc); break;       // not Then/Else
            case BoundWhileStatement w:          CollectRefs(w.Condition, acc); break;       // not Body
            case BoundForEachStatement fe:       CollectRefs(fe.Collection, acc); break;     // not Body
            case BoundMatchStatement m:          CollectRefs(m.Subject, acc); break;         // not arms
            case BoundLetGuard g:                CollectRefs(g.Initializer, acc); break;     // not ElseBody
            case BoundAsyncLetStatement al:      CollectRefs(al.Initializer, acc); break;
            case BoundSelectStatement sel:
                foreach (var arm in sel.Arms)
                {
                    if (arm.Channel is { } ch) CollectRefs(ch, acc);
                    if (arm.Value   is { } va) CollectRefs(va, acc);
                }
                break;
            // try / defer have no directly-evaluated expression — their bodies are handled by recursion.
        }
    }

    // Deep reference collection over an expression, INCLUDING closure bodies: a closure captures the
    // value at creation time, so a reference inside one is a use here (the join must precede it).
    static void CollectRefs(BoundExpression expr, HashSet<string> acc)
    {
        switch (expr)
        {
            case BoundNameExpression n:           acc.Add(n.Name); break;
            case BoundUnaryExpression u:          CollectRefs(u.Operand, acc); break;
            case BoundBinaryExpression b:         CollectRefs(b.Left, acc); CollectRefs(b.Right, acc); break;
            case BoundMemberAccessExpression ma:  CollectRefs(ma.Target, acc); break;
            case BoundCallExpression c:           CollectRefs(c.Target, acc); foreach (var a in c.Arguments) CollectRefs(a, acc); break;
            case BoundObjectCreationExpression o: foreach (var f in o.Fields) CollectRefs(f.Value, acc); break;
            case BoundIndexExpression ix:         CollectRefs(ix.Target, acc); CollectRefs(ix.Index, acc); break;
            case BoundTupleLiteralExpression tl:  foreach (var x in tl.Elements) CollectRefs(x, acc); break;
            case BoundListLiteralExpression ll:   foreach (var x in ll.Elements) CollectRefs(x, acc); break;
            case BoundConditionalExpression co:   CollectRefs(co.Condition, acc); CollectRefs(co.Consequence, acc); CollectRefs(co.Alternative, acc); break;
            case BoundRangeExpression rg:         if (rg.Target is { } rt) CollectRefs(rt, acc); if (rg.Start is { } rs) CollectRefs(rs, acc); if (rg.End is { } rn) CollectRefs(rn, acc); break;
            case BoundDotCaseExpression dc:       foreach (var a in dc.Arguments) CollectRefs(a, acc); break;
            case BoundConversion cv:              CollectRefs(cv.Operand, acc); break;
            case BoundAwaitExpression aw:         CollectRefs(aw.Inner, acc); break;
            case BoundResultCallExpression rc:    CollectRefs(rc.Argument, acc); break;
            case BoundTryUnwrapExpression tu:     CollectRefs(tu.Inner, acc); break;
            case BoundChanCreationExpression ch:  if (ch.Capacity is { } cap) CollectRefs(cap, acc); break;
            case BoundFunctionLiteralExpression fl: CollectRefsInBlock(fl.Body, acc); break;
            case BoundSpawnExpression sp:         CollectRefsInBlock(sp.Body, acc); break;
        }
    }

    // Deep reference collection over a closure body (every statement, every position) — inside a
    // closure all references are captures, so the full nesting is walked.
    static void CollectRefsInBlock(BoundBlockStatement block, HashSet<string> acc)
    {
        foreach (var s in block.Statements) CollectRefsInStatement(s, acc);
    }

    static void CollectRefsInStatement(BoundStatement stmt, HashSet<string> acc)
    {
        switch (stmt)
        {
            case BoundBlockStatement b:          CollectRefsInBlock(b, acc); break;
            case BoundVariableDeclaration v:     CollectRefs(v.Initializer, acc); break;
            case BoundAssignment a:              CollectRefs(a.Target, acc); CollectRefs(a.Value, acc); break;
            case BoundCompoundAssignment ca:     CollectRefs(ca.Target, acc); CollectRefs(ca.Value, acc); break;
            case BoundExpressionStatement e:     CollectRefs(e.Expression, acc); break;
            case BoundReturnStatement r:         if (r.Expression is { } re) CollectRefs(re, acc); break;
            case BoundThrowStatement t:          if (t.Expression is { } te) CollectRefs(te, acc); break;
            case BoundAsyncLetStatement al:      CollectRefs(al.Initializer, acc); break;
            case BoundLetGuard g:                CollectRefs(g.Initializer, acc); CollectRefsInStatement(g.ElseBody, acc); break;
            case BoundIfStatement i:             CollectRefs(i.Condition, acc); CollectRefsInStatement(i.Then, acc); if (i.Else is { } el) CollectRefsInStatement(el, acc); break;
            case BoundWhileStatement w:          CollectRefs(w.Condition, acc); CollectRefsInStatement(w.Body, acc); break;
            case BoundForEachStatement fe:       CollectRefs(fe.Collection, acc); CollectRefsInStatement(fe.Body, acc); break;
            case BoundMatchStatement m:          CollectRefs(m.Subject, acc); foreach (var arm in m.Arms) CollectRefsInBlock(arm.Body, acc); break;
            case BoundTryStatement tr:           CollectRefsInBlock(tr.Body, acc); foreach (var c in tr.Catches) CollectRefsInBlock(c.Body, acc); break;
            case BoundDeferStatement d:          CollectRefsInBlock(d.Body, acc); break;
            case BoundSelectStatement sel:
                foreach (var arm in sel.Arms)
                {
                    if (arm.Channel is { } ch) CollectRefs(ch, acc);
                    if (arm.Value   is { } va) CollectRefs(va, acc);
                    CollectRefsInBlock(arm.Body, acc);
                }
                break;
        }
    }
}
