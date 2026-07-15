using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Lowering.AsyncSpill;

/// The expression half of the async spill pass: linearizes an expression into a
/// sequence of side-effect statements (the "prelude") plus an await-free
/// replacement value, so that no `await` is ever evaluated with a live operand on
/// the stack beneath it.
///
/// `Linearize(expr, prelude)` appends to `prelude` and returns the value. The
/// invariant it establishes: every `BoundAwaitExpression` ends up as the direct
/// initializer of a synthesized `let __spill_N = await …` in some prelude, never
/// nested inside another expression's evaluation. Sub-expressions evaluated before
/// an await are spilled into temps at their original position so source-order
/// side effects are preserved.
sealed class SpillExpressionRewriter
{
    static readonly PrimitiveType Bool = new("bool");

    readonly AwaitFacts _facts;

    public SpillExpressionRewriter(AwaitFacts facts) => _facts = facts;

    /// A receiver that is a bare static type name (`Math`, `Console`, `Task`) rather than a
    /// value: the binder types it as an ExternalType / StaticFuncType whose name equals the
    /// identifier. Such a receiver pushes nothing on the stack, so it must not be spilled.
    static bool IsStaticTypeReceiver(BoundExpression e) => e switch
    {
        BoundNameExpression { Type: StaticFuncType } => true,
        BoundNameExpression { Name: var n, Type: ExternalType ext } => ext.Name == n && ext.TypeArgs.Count == 0,
        _ => false,
    };

    /// Linearize `expr`: append its side-effecting statements to `prelude` and
    /// return an await-free value expression. An await-free subtree is returned
    /// unchanged (no temps, spans preserved).
    public BoundExpression Linearize(BoundExpression expr, List<BoundStatement> prelude)
    {
        if (!_facts.ContainsAwait(expr))
            return expr;

        switch (expr)
        {
            case BoundAwaitExpression aw:
            {
                var inner = Linearize(aw.Inner, prelude);
                var awaitNode = aw with { Inner = inner };
                // A valueless await (`await someTask` of a non-generic Task/ValueTask,
                // or one whose result type stayed unresolved) yields nothing to store,
                // so it can't bind to a temp — and it only occurs at statement level,
                // where it already suspends with an empty stack. Leave it in place.
                if (IsValueless(aw.Type))
                    return awaitNode;
                var name = _facts.NextTemp();
                prelude.Add(Decl(name, aw.Type, awaitNode, aw.Span));
                return Name(name, aw.Type, aw.Span);
            }

            // ── short-circuit / conditional forms: the conditionally-evaluated part
            // cannot be hoisted unconditionally, so rewrite to explicit control flow
            // with a result temp. Only triggered when that conditional part awaits;
            // otherwise these fall through to the sequential rebuild below. ──
            case BoundBinaryExpression { Op: SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword } andB
                when _facts.ContainsAwait(andB.Right):
                return RewriteAndAlso(andB, prelude);
            case BoundBinaryExpression { Op: SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword } orB
                when _facts.ContainsAwait(orB.Right):
                return RewriteOrElse(orB, prelude);
            case BoundConditionalExpression cnd
                when _facts.ContainsAwait(cnd.Consequence) || _facts.ContainsAwait(cnd.Alternative):
                return RewriteConditional(cnd, prelude);
            case BoundNullCoalescingExpression nc when _facts.ContainsAwait(nc.Right):
                return RewriteNullCoalescing(nc, prelude);
            case BoundMatchExpression me when me.Arms.Any(a => _facts.ContainsAwait(a.Value)):
                return RewriteMatchExpression(me, prelude);

            // ── sequential multi-child forms: linearize children left-to-right,
            // spilling each child that precedes an awaiting sibling. ──
            case BoundBinaryExpression b:
            {
                var s = Sequence([b.Left, b.Right], prelude);
                return b with { Left = s[0], Right = s[1] };
            }
            case BoundUnaryExpression u:
                return u with { Operand = Linearize(u.Operand, prelude) };
            case BoundCallExpression c:
            {
                // Only a real *value* receiver lands on the stack ahead of the
                // arguments and must be spilled when an argument awaits — never a
                // bare function-name callee (which pushes nothing; spilling it into
                // a `let` would read it as an undefined variable).
                var argsAwait = c.Arguments.Any(_facts.ContainsAwait);
                if (c.Target is BoundMemberAccessExpression ma)
                {
                    // A STATIC-type-name receiver (`Math.Max(await …, 2)`, `Console.WriteLine(await …)`)
                    // is a type token, not a value on the stack — spilling it into a `let` would
                    // read `Math`/`Console` as an undefined variable in MoveNext (probe4 #2). Leave
                    // it in place; the type token re-emits after resume.
                    if (IsStaticTypeReceiver(ma.Target))
                        return c with { Target = ma, Arguments = Sequence(c.Arguments, prelude) };
                    var recv = Linearize(ma.Target, prelude);
                    if (argsAwait) recv = Spill(recv, prelude);
                    return c with { Target = ma with { Target = recv }, Arguments = Sequence(c.Arguments, prelude) };
                }
                if (c.Target is BoundNameExpression)
                    return c with { Arguments = Sequence(c.Arguments, prelude) };
                // Computed delegate-valued callee: evaluated before the args.
                var target = Linearize(c.Target, prelude);
                if (argsAwait) target = Spill(target, prelude);
                return c with { Target = target, Arguments = Sequence(c.Arguments, prelude) };
            }
            case BoundMemberAccessExpression m:
                return m with { Target = Linearize(m.Target, prelude) };
            case BoundIndexExpression ix:
            {
                var s = Sequence([ix.Target, ix.Index], prelude);
                return ix with { Target = s[0], Index = s[1] };
            }
            case BoundListLiteralExpression l:
                return l with { Elements = Sequence(l.Elements, prelude) };
            case BoundTupleLiteralExpression t:
                return t with { Elements = Sequence(t.Elements, prelude) };
            case BoundObjectCreationExpression oc:
            {
                var s = Sequence(oc.Fields.Select(f => f.Value).ToList(), prelude);
                return oc with { Fields = oc.Fields.Select((f, i) => f with { Value = s[i] }).ToList() };
            }
            case BoundWithExpression w:
            {
                var children = new List<BoundExpression> { w.Target };
                children.AddRange(w.Fields.Select(f => f.Value));
                var s = Sequence(children, prelude);
                return w with { Target = s[0], Fields = w.Fields.Select((f, i) => f with { Value = s[i + 1] }).ToList() };
            }
            case BoundInterpolatedStringExpression istr:
                return RewriteInterpolation(istr, prelude);
            case BoundRangeExpression r:
                return RewriteRange(r, prelude);
            case BoundDotCaseExpression dc:
                return dc with { Arguments = Sequence(dc.Arguments, prelude) };
            case BoundResultCallExpression rc:
                return rc with { Argument = Linearize(rc.Argument, prelude) };
            case BoundTryUnwrapExpression tu:
                return tu with { Inner = Linearize(tu.Inner, prelude) };
            case BoundTypeTestExpression tt:
                return tt with { Operand = Linearize(tt.Operand, prelude) };
            case BoundConversion cv:
                return cv with { Operand = Linearize(cv.Operand, prelude) };
            case BoundNullConditionalAccessExpression nca:
                // The conditional member read has no await; only the target can. Spill the target.
                return nca with { Target = Linearize(nca.Target, prelude) };
            case BoundConditionalExpression cnd:
                // Only the (always-evaluated) condition awaits.
                return cnd with { Condition = Linearize(cnd.Condition, prelude) };
            case BoundNullCoalescingExpression nc:
                // Only the (always-evaluated) left awaits.
                return nc with { Left = Linearize(nc.Left, prelude) };
            case BoundMatchExpression me:
                // Only the subject awaits.
                return me with { Subject = Linearize(me.Subject, prelude) };

            default:
                // Await-free by construction (ContainsAwait already returned false for
                // leaves / lambda+spawn scopes), or a form with no awaitable child.
                return expr;
        }
    }

    /// Linearize a left-to-right child sequence. A child is spilled into a temp
    /// when any later sibling contains an await — pinning its side effect before
    /// that await and keeping its value off the live stack across the suspend.
    List<BoundExpression> Sequence(IReadOnlyList<BoundExpression> children, List<BoundStatement> prelude)
    {
        var result = new List<BoundExpression>(children.Count);
        for (var i = 0; i < children.Count; i++)
        {
            var laterAwaits = false;
            for (var j = i + 1; j < children.Count; j++)
                if (_facts.ContainsAwait(children[j])) { laterAwaits = true; break; }

            var value = Linearize(children[i], prelude);
            result.Add(laterAwaits ? Spill(value, prelude) : value);
        }
        return result;
    }

    BoundExpression RewriteInterpolation(BoundInterpolatedStringExpression istr, List<BoundStatement> prelude)
    {
        // Holes evaluate left-to-right; spill a hole whose later holes await.
        var holeIndices = new List<int>();
        for (var i = 0; i < istr.Parts.Count; i++)
            if (istr.Parts[i].Expr is not null) holeIndices.Add(i);

        var holeValues = Sequence(holeIndices.Select(i => istr.Parts[i].Expr!).ToList(), prelude);

        var newParts = istr.Parts.ToList();
        for (var k = 0; k < holeIndices.Count; k++)
            newParts[holeIndices[k]] = newParts[holeIndices[k]] with { Expr = holeValues[k] };
        return istr with { Parts = newParts };
    }

    BoundExpression RewriteRange(BoundRangeExpression r, List<BoundStatement> prelude)
    {
        // Order: Target, Start, End. Linearize the present operands as a sequence.
        var present = new List<BoundExpression>();
        if (r.Target is not null) present.Add(r.Target);
        if (r.Start is not null) present.Add(r.Start);
        if (r.End is not null) present.Add(r.End);
        var s = Sequence(present, prelude);
        var idx = 0;
        var newTarget = r.Target is null ? null : s[idx++];
        var newStart = r.Start is null ? null : s[idx++];
        var newEnd = r.End is null ? null : s[idx++];
        return r with { Target = newTarget, Start = newStart, End = newEnd };
    }

    BoundExpression RewriteAndAlso(BoundBinaryExpression b, List<BoundStatement> prelude)
    {
        // a && b  →  let __a = a'; var __r = __a; if (__a) { <b'>; __r = b' }
        var a = Spill(Linearize(b.Left, prelude), prelude);
        var r = _facts.NextTemp();
        prelude.Add(Decl(r, Bool, a, b.Span, mutable: true));
        var thenBody = new List<BoundStatement>();
        var rhs = Linearize(b.Right, thenBody);
        thenBody.Add(new BoundAssignment(Name(r, Bool), rhs));
        prelude.Add(new BoundIfStatement(a, Block(thenBody), null));
        return Name(r, Bool, b.Span);
    }

    BoundExpression RewriteOrElse(BoundBinaryExpression b, List<BoundStatement> prelude)
    {
        // a || b  →  let __a = a'; var __r = __a; if (__a) { } else { <b'>; __r = b' }
        var a = Spill(Linearize(b.Left, prelude), prelude);
        var r = _facts.NextTemp();
        prelude.Add(Decl(r, Bool, a, b.Span, mutable: true));
        var elseBody = new List<BoundStatement>();
        var rhs = Linearize(b.Right, elseBody);
        elseBody.Add(new BoundAssignment(Name(r, Bool), rhs));
        prelude.Add(new BoundIfStatement(a, Block([]), Block(elseBody)));
        return Name(r, Bool, b.Span);
    }

    BoundExpression RewriteConditional(BoundConditionalExpression cnd, List<BoundStatement> prelude)
    {
        // c ? x : y  →  let __c = c'; var __r: T = default(T); if (__c) { __r = x' } else { __r = y' }
        var c = Spill(Linearize(cnd.Condition, prelude), prelude);
        var r = _facts.NextTemp();
        var t = cnd.Type;
        prelude.Add(Decl(r, t, new BoundObjectCreationExpression(t, []), cnd.Span, mutable: true));
        prelude.Add(new BoundIfStatement(c, BranchAssign(r, t, cnd.Consequence), BranchAssign(r, t, cnd.Alternative)));
        return Name(r, t, cnd.Span);
    }

    BoundExpression RewriteNullCoalescing(BoundNullCoalescingExpression nc, List<BoundStatement> prelude)
    {
        // a ?? b (b awaits)  →
        //   let __a = a'; var __r: T = default(T);
        //   if (__a == nil) { __r = b' } else { __r = __a ?? default(T) }
        // The else's `__a ?? default(T)` reuses the existing await-free `??` to
        // unwrap __a (nilable→T or ref→ref); __a is non-nil there so default is dead.
        var a = Spill(Linearize(nc.Left, prelude), prelude);
        var r = _facts.NextTemp();
        var t = nc.Type;
        prelude.Add(Decl(r, t, new BoundObjectCreationExpression(t, []), nc.Span, mutable: true));
        var isNil = new BoundBinaryExpression(a, SyntaxTokenKind.EqualsEquals, new BoundLiteralExpression(null, "null", a.Type), Bool);
        var elseBody = Block([new BoundAssignment(Name(r, t), new BoundNullCoalescingExpression(a, new BoundObjectCreationExpression(t, []), t))]);
        prelude.Add(new BoundIfStatement(isNil, BranchAssign(r, t, nc.Right), elseBody));
        return Name(r, t, nc.Span);
    }

    BoundExpression RewriteMatchExpression(BoundMatchExpression me, List<BoundStatement> prelude)
    {
        // match … { arm => value } (an arm value awaits)  →  a match STATEMENT
        // whose arm bodies assign the linearized value into a result temp.
        var subject = Spill(Linearize(me.Subject, prelude), prelude);
        var r = _facts.NextTemp();
        var t = me.Type;
        prelude.Add(Decl(r, t, new BoundObjectCreationExpression(t, []), me.Span, mutable: true));
        var arms = me.Arms.Select(arm =>
        {
            var body = new List<BoundStatement>();
            var v = Linearize(arm.Value, body);
            body.Add(new BoundAssignment(Name(r, t), v));
            return new BoundMatchArm(arm.Pattern, Block(body), arm.Guard);
        }).ToList();
        prelude.Add(new BoundMatchStatement(subject, me.SubjectType, arms));
        return Name(r, t, me.Span);
    }

    // ── small builders ──

    /// Spill `value` into a fresh `let` temp unless it is already pure (a literal,
    /// `default`, or a prior spill temp — all order-safe to read later).
    public BoundExpression Spill(BoundExpression value, List<BoundStatement> prelude)
    {
        if (IsPure(value)) return value;
        var name = _facts.NextTemp();
        prelude.Add(Decl(name, value.Type, value, value.Span));
        return Name(name, value.Type, value.Span);
    }

    /// An await whose result cannot (or need not) be bound to a temp: a `void`
    /// result, a non-generic `Task`/`ValueTask` (GetResult returns void), or an
    /// unresolved result type. Such awaits only appear at statement level, where
    /// they already suspend with an empty stack — leaving them in place is correct.
    static bool IsValueless(BoundType t) =>
        t is VoidType or InferredType
        || t is ExternalType { Name: "Task" or "ValueTask", TypeArgs.Count: 0 };

    static bool IsPure(BoundExpression e) =>
        e is BoundLiteralExpression or BoundObjectCreationExpression { Fields.Count: 0 }
        || (e is BoundNameExpression n && n.Name.StartsWith(AwaitFacts.Prefix, StringComparison.Ordinal));

    BoundBlockStatement BranchAssign(string name, BoundType type, BoundExpression branchExpr)
    {
        var body = new List<BoundStatement>();
        var v = Linearize(branchExpr, body);
        body.Add(new BoundAssignment(Name(name, type), v));
        return Block(body);
    }

    static BoundVariableDeclaration Decl(string name, BoundType type, BoundExpression init, SourceSpan span, bool mutable = false) =>
        new(mutable, name, type, init) { Span = span };

    static BoundNameExpression Name(string name, BoundType type, SourceSpan span = default) =>
        new(name, type) { Span = span };

    static BoundBlockStatement Block(IReadOnlyList<BoundStatement> stmts) => new(stmts);
}
