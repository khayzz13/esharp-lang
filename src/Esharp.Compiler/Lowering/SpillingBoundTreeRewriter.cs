using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// Base for lowering passes that must <b>hoist statements</b> before a consumer (a temp
/// declaration, a nil-test branch, a Result early-return) while still descending into
/// <em>every</em> child position. It inherits total traversal from the generated
/// <see cref="BoundTreeRewriter"/> — lambda bodies, object-creation fields and
/// constructor-arguments, index, tuple, every call arg — and layers a statement-hoist
/// buffer on top, so a derived pass overrides only the FEATURE nodes it lowers and calls
/// <see cref="Hoist"/> to splice setup before the enclosing statement.
///
/// <para>
/// <b>The conditional-hoist problem (the load-bearing part).</b> A hoist that originates
/// inside a <em>conditionally-evaluated</em> sub-expression — the right operand of
/// <c>&amp;&amp;</c>/<c>||</c>, or a ternary branch — must NOT splice before the enclosing
/// statement (that would run it unconditionally and reorder side effects). When a branch
/// produces hoists, this base <b>materializes</b> the whole conditional into a temp +
/// guarded <c>if</c> so the hoisted setup runs only on the branch that produced it. A
/// branch with no hoists takes the cheap structural path with no temp. Ternary is normally
/// already eliminated by <see cref="ExpressionFormLowering"/>; the short-circuit operators
/// are not, so they are the real case — but both are handled here for correctness.
/// </para>
///
/// <para>Every function/init body is a <see cref="BoundBlockStatement"/>, and statements
/// live in blocks, so <see cref="RewriteBlockStatement"/> is always the hoist boundary —
/// a per-statement buffer is opened around each statement and spliced in front of it.</para>
/// </summary>
public abstract class SpillingBoundTreeRewriter : LoweringRewriter
{
    readonly Stack<List<BoundStatement>> _hoistScopes = new();

    static readonly PrimitiveType BoolT = new("bool");

    /// Append a statement to run immediately before the statement currently being rewritten.
    protected void Hoist(BoundStatement stmt) => _hoistScopes.Peek().Add(stmt);

    // ── Hoist boundary: every statement gets its own per-statement buffer ────────────────

    protected override BoundStatement RewriteBlockStatement(BoundBlockStatement node)
    {
        List<BoundStatement>? result = null;
        for (var i = 0; i < node.Statements.Count; i++)
        {
            var original = node.Statements[i];

            var buffer = new List<BoundStatement>();
            _hoistScopes.Push(buffer);
            var rewritten = RewriteStatement(original);
            _hoistScopes.Pop();

            var changed = buffer.Count > 0 || !ReferenceEquals(rewritten, original);
            if (changed && result is null)
                result = [.. node.Statements.Take(i)];
            if (result is not null)
            {
                result.AddRange(buffer);
                result.Add(rewritten);
            }
        }
        return result is null ? node : node with { Statements = result };
    }

    // ── Conditional-context handling ─────────────────────────────────────────────────────

    /// Rewrite <paramref name="expr"/> capturing any hoists it produces into a private buffer
    /// rather than the enclosing statement's buffer. Returns the rewritten expression and the
    /// (possibly empty) list of statements that must run on the path that evaluates it. Used by
    /// a derived pass whose own FEATURE node (e.g. <c>??</c>, <c>?.</c>) conditionally evaluates
    /// an operand, so that operand's setup lands inside the guard, not before the statement.
    protected (BoundExpression Expr, List<BoundStatement> Hoists) RewriteCapturing(BoundExpression expr)
    {
        var buffer = new List<BoundStatement>();
        _hoistScopes.Push(buffer);
        var rewritten = RewriteExpression(expr);
        _hoistScopes.Pop();
        return (rewritten, buffer);
    }

    protected override BoundExpression RewriteConditionalExpression(BoundConditionalExpression node)
    {
        // The condition is evaluated unconditionally — its hoists go to the enclosing buffer.
        var condition = RewriteExpression(node.Condition);
        var (consequence, conseqHoists) = RewriteCapturing(node.Consequence);
        var (alternative, altHoists)    = RewriteCapturing(node.Alternative);

        if (conseqHoists.Count == 0 && altHoists.Count == 0)
        {
            return ReferenceEquals(condition, node.Condition)
                && ReferenceEquals(consequence, node.Consequence)
                && ReferenceEquals(alternative, node.Alternative)
                ? node
                : node with { Condition = condition, Consequence = consequence, Alternative = alternative };
        }

        // A branch hoists → materialize:  var __t = default; if (cond) {h; __t=conseq} else {h; __t=alt}
        var temp = MaterializeConditional(node.Type, condition,
            consequence, conseqHoists, alternative, altHoists);
        return temp;
    }

    protected override BoundExpression RewriteBinaryExpression(BoundBinaryExpression node)
    {
        var shortCircuit = node.Op is SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword
                                   or SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword;
        if (!shortCircuit)
            return base.RewriteBinaryExpression(node);

        // Left is evaluated unconditionally; right only when left does not short-circuit.
        var left = RewriteExpression(node.Left);
        var (right, rightHoists) = RewriteCapturing(node.Right);

        if (rightHoists.Count == 0)
            return ReferenceEquals(left, node.Left) && ReferenceEquals(right, node.Right)
                ? node
                : node with { Left = left, Right = right };

        // Materialize:  var __t = left;  if (<should-eval-right>) { h; __t = right }
        // && : eval right when __t is true.   || : eval right when __t is false.
        var isAnd = node.Op is SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword;
        var tempName = FreshTemp("sc");
        var tempRef  = new BoundNameExpression(tempName, node.Type);
        Hoist(new BoundVariableDeclaration(Mutable: true, tempName, node.Type, left));

        var gate = isAnd
            ? (BoundExpression)tempRef
            : new BoundUnaryExpression(SyntaxTokenKind.Bang, tempRef, BoolT);
        var thenStmts = new List<BoundStatement>(rightHoists) { new BoundAssignment(tempRef, right) };
        Hoist(new BoundIfStatement(gate, new BoundBlockStatement(thenStmts), Else: null));
        return tempRef;
    }

    BoundExpression MaterializeConditional(
        BoundType resultType, BoundExpression condition,
        BoundExpression consequence, List<BoundStatement> conseqHoists,
        BoundExpression alternative, List<BoundStatement> altHoists)
    {
        var tempName = FreshTemp("cond");
        var tempRef  = new BoundNameExpression(tempName, resultType);
        Hoist(new BoundVariableDeclaration(Mutable: true, tempName, resultType,
            new BoundDefaultExpression(resultType)));

        var thenStmts = new List<BoundStatement>(conseqHoists) { new BoundAssignment(tempRef, consequence) };
        var elseStmts = new List<BoundStatement>(altHoists)    { new BoundAssignment(tempRef, alternative) };
        Hoist(new BoundIfStatement(condition,
            new BoundBlockStatement(thenStmts), new BoundBlockStatement(elseStmts)));
        return tempRef;
    }
}
