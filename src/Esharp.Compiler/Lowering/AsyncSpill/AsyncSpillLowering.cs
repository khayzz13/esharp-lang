using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Lowering.AsyncSpill;

/// Post-bind rewrite that makes an async function body emit verifiable IL by
/// ensuring no `await` is ever evaluated with a live operand on the state
/// machine's evaluation stack. It linearizes each statement's expressions
/// (`SpillExpressionRewriter`), splicing the resulting `let __spill_N = …`
/// prelude statements in front of the statement that consumes them, so every
/// `await` becomes the initializer of its own local — the only shape that
/// survives a `MoveNext` suspend/resume.
///
/// Runs immediately after `AsyncLetLowering` in `DeclarationBinder.BindFunction`,
/// only for bodies that contain an await. Synthesized temps need no symbol
/// registration: the async emitter's `DeclareLocal` promotes any local it emits
/// to a state-machine field, so the temps survive suspension automatically.
///
/// Mirrors the structural-rewrite + span-preservation discipline of
/// `PointerEscapeAnalysis.RetypeNames` and `AsyncLetLowering`.
internal static class AsyncSpillLowering
{
    public static BoundBlockStatement Rewrite(BoundBlockStatement body)
    {
        var facts = new AwaitFacts();
        var rewriter = new SpillExpressionRewriter(facts);
        return new Walker(facts, rewriter).RewriteBlock(body);
    }

    sealed class Walker(AwaitFacts facts, SpillExpressionRewriter rw)
    {
        public BoundBlockStatement RewriteBlock(BoundBlockStatement block)
        {
            var output = new List<BoundStatement>(block.Statements.Count);
            foreach (var s in block.Statements)
                RewriteStatement(s, output);
            return new BoundBlockStatement(output) { Span = block.Span };
        }

        /// Rewrite a statement that sits in `Then`/`Else`/loop-body position into a
        /// single block (its prelude + body together).
        BoundStatement RewriteToBlock(BoundStatement s)
        {
            if (s is BoundBlockStatement b) return RewriteBlock(b);
            var output = new List<BoundStatement>();
            RewriteStatement(s, output);
            return new BoundBlockStatement(output) { Span = s.Span };
        }

        void RewriteStatement(BoundStatement stmt, List<BoundStatement> output)
        {
            switch (stmt)
            {
                case BoundBlockStatement b:
                    output.Add(RewriteBlock(b));
                    break;

                case BoundVariableDeclaration v:
                {
                    var init = rw.Linearize(v.Initializer, output);
                    output.Add(v with { Initializer = init });
                    break;
                }

                case BoundLetGuard g:
                {
                    var init = rw.Linearize(g.Initializer, output);
                    output.Add(g with { Initializer = init, ElseBody = RewriteBlock(g.ElseBody) });
                    break;
                }

                case BoundExpressionStatement e:
                {
                    var val = rw.Linearize(e.Expression, output);
                    output.Add(e with { Expression = val });
                    break;
                }

                case BoundReturnStatement { Expression: { } re } r:
                {
                    var val = rw.Linearize(re, output);
                    output.Add(r with { Expression = val });
                    break;
                }

                case BoundThrowStatement { Expression: { } te } th:
                {
                    var val = rw.Linearize(te, output);
                    output.Add(th with { Expression = val });
                    break;
                }

                case BoundRaiseStatement raise:
                {
                    var args = LinearizeSequenceStmt(raise.Arguments, output);
                    output.Add(raise with { Arguments = args });
                    break;
                }

                case BoundAssignment a:
                    RewriteAssignment(a, output);
                    break;

                case BoundCompoundAssignment ca:
                    RewriteCompoundAssignment(ca, output);
                    break;

                case BoundIfStatement i:
                {
                    var cond = rw.Linearize(i.Condition, output);
                    var then = RewriteToBlock(i.Then);
                    var els = i.Else is null ? null : RewriteToBlock(i.Else);
                    output.Add(new BoundIfStatement(cond, then, els) { Span = i.Span });
                    break;
                }

                case BoundWhileStatement w:
                    RewriteWhile(w, output);
                    break;

                case BoundForEachStatement fe:
                {
                    // The collection is evaluated once, before the loop.
                    var coll = rw.Linearize(fe.Collection, output);
                    output.Add(fe with { Collection = coll, Body = RewriteToBlock(fe.Body) });
                    break;
                }

                case BoundMatchStatement m:
                {
                    var subject = rw.Linearize(m.Subject, output);
                    var arms = m.Arms.Select(arm => arm with { Body = RewriteBlock(arm.Body) }).ToList();
                    output.Add(m with { Subject = subject, Arms = arms });
                    break;
                }

                case BoundDeferStatement d:
                    output.Add(d with { Body = RewriteBlock(d.Body) });
                    break;

                case BoundTryStatement tr:
                {
                    var catches = tr.Catches.Select(c => c with { Body = RewriteBlock(c.Body) }).ToList();
                    output.Add(tr with { Body = RewriteBlock(tr.Body), Catches = catches });
                    break;
                }

                case BoundSelectStatement sel:
                {
                    var arms = sel.Arms.Select(arm => arm with { Body = RewriteBlock(arm.Body) }).ToList();
                    output.Add(sel with { Arms = arms });
                    break;
                }

                default:
                    // Break / Continue / Const / (a stray) AsyncLet / Return-void /
                    // Throw-rethrow: no awaitable sub-expression — pass through.
                    output.Add(stmt);
                    break;
            }
        }

        List<BoundExpression> LinearizeSequenceStmt(IReadOnlyList<BoundExpression> exprs, List<BoundStatement> output)
        {
            // Same left-to-right spill discipline the expression rewriter uses for
            // call arguments, applied to a statement's argument list.
            var result = new List<BoundExpression>(exprs.Count);
            for (var i = 0; i < exprs.Count; i++)
            {
                var laterAwaits = false;
                for (var j = i + 1; j < exprs.Count; j++)
                    if (facts.ContainsAwait(exprs[j])) { laterAwaits = true; break; }
                var value = rw.Linearize(exprs[i], output);
                result.Add(laterAwaits ? rw.Spill(value, output) : value);
            }
            return result;
        }

        void RewriteAssignment(BoundAssignment a, List<BoundStatement> output)
        {
            var valueAwaits = facts.ContainsAwait(a.Value);
            // Evaluate (and, if the value awaits, spill) the target's l-value
            // sub-expressions BEFORE the value, so they don't sit on the stack
            // across the suspend and source order is preserved.
            var target = LinearizeTarget(a.Target, output, spill: valueAwaits);
            var value = rw.Linearize(a.Value, output);
            output.Add(new BoundAssignment(target, value) { Span = a.Span });
        }

        BoundExpression LinearizeTarget(BoundExpression target, List<BoundStatement> output, bool spill)
        {
            switch (target)
            {
                case BoundMemberAccessExpression m:
                {
                    var recv = rw.Linearize(m.Target, output);
                    if (spill) recv = rw.Spill(recv, output);
                    return m with { Target = recv };
                }
                case BoundIndexExpression ix:
                {
                    var t = rw.Linearize(ix.Target, output);
                    if (spill) t = rw.Spill(t, output);
                    var idx = rw.Linearize(ix.Index, output);
                    if (spill) idx = rw.Spill(idx, output);
                    return ix with { Target = t, Index = idx };
                }
                default:
                    // A bare name (or other simple l-value) has no sub-expression to
                    // strand on the stack; the store happens after the value reduces.
                    return target;
            }
        }

        void RewriteCompoundAssignment(BoundCompoundAssignment ca, List<BoundStatement> output)
        {
            if (!facts.ContainsAwait(ca.Value))
            {
                output.Add(ca);
                return;
            }

            // `target op= (await …)` would read the target, then suspend with that
            // read live on the stack. Desugar to an explicit assignment with a
            // load-before-await of the target read:
            //   let __o = recv; let __old = __o.f; let __v = value'; __o.f = __old op __v
            var baseOp = BaseOp(ca.Op);
            var t = ca.Target.Type;

            BoundExpression readTarget, writeTarget;
            switch (ca.Target)
            {
                case BoundMemberAccessExpression m:
                {
                    var recv = rw.Spill(rw.Linearize(m.Target, output), output);
                    readTarget = m with { Target = recv };
                    writeTarget = m with { Target = recv };
                    break;
                }
                case BoundIndexExpression ix:
                {
                    var arr = rw.Spill(rw.Linearize(ix.Target, output), output);
                    var idx = rw.Spill(rw.Linearize(ix.Index, output), output);
                    readTarget = ix with { Target = arr, Index = idx };
                    writeTarget = ix with { Target = arr, Index = idx };
                    break;
                }
                default:
                    // Bare name: read and write the same name.
                    readTarget = ca.Target;
                    writeTarget = ca.Target;
                    break;
            }

            var old = rw.Spill(readTarget, output);          // read BEFORE the await
            var value = rw.Linearize(ca.Value, output);      // the await(s)
            var combined = ca.Combined is { } resolved
                ? resolved with { Left = old, Right = value }
                : new BoundBinaryExpression(old, baseOp, value, t);
            output.Add(new BoundAssignment(writeTarget, combined) { Span = ca.Span });
        }

        void RewriteWhile(BoundWhileStatement w, List<BoundStatement> output)
        {
            if (!facts.ContainsAwait(w.Condition))
            {
                output.Add(w with { Body = RewriteToBlock(w.Body) });
                return;
            }

            // The condition awaits and is re-evaluated every iteration, so its
            // prelude must live inside the loop:
            //   while (true) { <cond-prelude>; if (cond') { <body> } else { break } }
            // `while (true)` + a `break` is correctly NOT treated as infinite by
            // definite-return analysis (it checks for a reachable break).
            var inner = new List<BoundStatement>();
            var cond = rw.Linearize(w.Condition, inner);
            var body = RewriteToBlock(w.Body);
            inner.Add(new BoundIfStatement(cond, body,
                new BoundBlockStatement([new BoundBreakStatement()])));
            var trueLit = new BoundLiteralExpression(true, "true", new PrimitiveType("bool"));
            output.Add(new BoundWhileStatement(trueLit, new BoundBlockStatement(inner)) { Span = w.Span });
        }

        // Map a compound-assignment operator to its base binary operator so the
        // synthesized `BoundBinaryExpression` lands on a case the emitter handles.
        // Keep this exhaustive with StatementBinder/AssignmentLowering so an await on
        // the right-hand side cannot discard the selected primitive or user operator.
        static SyntaxTokenKind BaseOp(SyntaxTokenKind op) => op switch
        {
            SyntaxTokenKind.PlusEquals => SyntaxTokenKind.Plus,
            SyntaxTokenKind.MinusEquals => SyntaxTokenKind.Minus,
            SyntaxTokenKind.StarEquals => SyntaxTokenKind.Star,
            SyntaxTokenKind.SlashEquals => SyntaxTokenKind.Slash,
            SyntaxTokenKind.PercentEquals => SyntaxTokenKind.Percent,
            SyntaxTokenKind.AmpersandEquals => SyntaxTokenKind.Ampersand,
            SyntaxTokenKind.PipeEquals => SyntaxTokenKind.Pipe,
            SyntaxTokenKind.CaretEquals => SyntaxTokenKind.Caret,
            SyntaxTokenKind.ShiftLeftEquals => SyntaxTokenKind.ShiftLeft,
            SyntaxTokenKind.ShiftRightEquals => SyntaxTokenKind.ShiftRight,
            SyntaxTokenKind.UnsignedShiftRightEquals => SyntaxTokenKind.UnsignedShiftRight,
            _ => op,
        };
    }
}
