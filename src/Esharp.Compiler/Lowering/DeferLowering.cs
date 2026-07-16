using System.Linq;
using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers <c>defer { D }</c> into a <see cref="BoundTryStatement"/> whose <c>.finally</c>-equivalent
/// (a <see cref="BoundCatchClause"/> with <c>IsFinally: true</c>) holds <c>D</c>, scoped over the
/// rest of the enclosing block. <c>defer</c> runs on every exit path — fall-through, <c>return</c>,
/// <c>break</c>, and an exception unwinding — which is exactly a <c>.finally</c> region over the
/// statements that follow it. Stacking defers nests the regions, giving the LIFO order: the
/// last <c>defer</c> reached wraps the least, so its cleanup runs first.
///
/// <para>The transform is block-structural, so it overrides <see cref="RewriteBlockStatement"/>:
/// it lets the base recurse first (lowering nested blocks, defers inside lambda bodies, and the
/// defer bodies themselves — the lambda-body position the old hand-rolled walk skipped), then, if
/// this block holds any defer, rebuilds it into nested try regions from the last defer outward.</para>
/// </summary>
public sealed class DeferLowering : IBoundTreePass
{
    public static readonly DeferLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new DeferRewriter());
}

sealed class DeferRewriter : LoweringRewriter
{
    // BoundTreeRewriter's default lambda descent calls its private block helper,
    // which rewrites children but bypasses this pass's block-structural override.
    // Route lambda bodies through RewriteStatement so defers introduced in spawn/select
    // lambdas receive the same nested try/finally lowering as ordinary function bodies.
    protected override BoundExpression RewriteFunctionLiteralExpression(BoundFunctionLiteralExpression node)
    {
        var body = (BoundBlockStatement)RewriteStatement(node.Body);
        return ReferenceEquals(body, node.Body) ? node : node with { Body = body };
    }

    // The base RewriteTryStatement rewrites the try/catch bodies through the private
    // RewriteBlock helper, which bypasses this pass's RewriteBlockStatement override — so a
    // `defer` inside a `try` block (`try { let s = open()  defer { s.close() }  … }`, the
    // spec's `finally`-equivalent) would survive lowering. Route those blocks through
    // RewriteStatement (which dispatches to the override) so try-body defers become nested
    // try/finally regions exactly like function-body defers.
    protected override BoundStatement RewriteTryStatement(BoundTryStatement node)
    {
        var body = (BoundBlockStatement)RewriteStatement(node.Body);
        var catches = node.Catches.Select(c =>
        {
            var cb = (BoundBlockStatement)RewriteStatement(c.Body);
            var guard = c.Guard is { } g ? RewriteExpression(g) : null;
            return ReferenceEquals(cb, c.Body) && ReferenceEquals(guard, c.Guard)
                ? c
                : c with { Body = cb, Guard = guard };
        }).ToList();
        return node with { Body = body, Catches = catches };
    }

    protected override BoundStatement RewriteBlockStatement(BoundBlockStatement node)
    {
        var lowered = (BoundBlockStatement)base.RewriteBlockStatement(node);

        var firstDefer = -1;
        for (var i = 0; i < lowered.Statements.Count; i++)
            if (lowered.Statements[i] is BoundDeferStatement) { firstDefer = i; break; }
        if (firstDefer < 0) return lowered;

        return BuildDeferRegions([.. lowered.Statements], lowered.Span);
    }

    // Build nested try/finally regions, last defer (innermost = runs first) outward.
    static BoundBlockStatement BuildDeferRegions(List<BoundStatement> stmts, SourceSpan span)
    {
        var deferIndices = new List<int>();
        for (var i = 0; i < stmts.Count; i++)
            if (stmts[i] is BoundDeferStatement) deferIndices.Add(i);

        var result = stmts;
        for (var di = deferIndices.Count - 1; di >= 0; di--)
        {
            var idx       = deferIndices[di];
            var deferStmt = (BoundDeferStatement)result[idx];

            // Everything after this defer is its try body; the defer body is the finally.
            var tryBody = new BoundBlockStatement([.. result.Skip(idx + 1)]);
            var tryStmt = new BoundTryStatement(
                Body: tryBody,
                Catches:
                [
                    new BoundCatchClause(
                        ExceptionType: null, BindingName: null,
                        Body: deferStmt.Body, Guard: null, IsFinally: true),
                ]) { Span = deferStmt.Span };

            result = [.. result.Take(idx), tryStmt];
        }

        return new BoundBlockStatement(result) { Span = span };
    }
}
