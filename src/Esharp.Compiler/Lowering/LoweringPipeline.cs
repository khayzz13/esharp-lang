using Esharp.BoundTree;
using Esharp.Symbols;
using Esharp.Syntax;
using Esharp.Diagnostics;

namespace Esharp.Lowering;

/// <summary>
/// The ordered lowering driver. Runs all <see cref="IBoundTreePass"/> implementations in
/// dependency order and asserts that no FEATURE node survives for CodeGen to see.
///
/// Pass order is load-bearing:
///
///   1.  <see cref="PropertyLowering"/>           — member/property stores → declared-type storage conversions
///   2.  <see cref="AsyncStreamLowering"/>        — split IAsyncEnumerable yield functions into producer + wrapper
///   3.  <see cref="AsyncForeachLowering"/>       — expand `await for v in src` into async-enumerator drain
///   4.  <see cref="ExpressionFormLowering"/>     — hoist if/match expressions into temp locals
///   5.  <see cref="ScopedMutLowering"/>          — scoped property lends → setup/try/finally/resume at call sites
///   6.  <see cref="MatchLowering"/>              — match statements/expressions → if/branch chains
///   7.  <see cref="ResultLowering"/>             — ok/error/? → struct-init / early-return branches
///   8.  <see cref="NullFlowLowering"/>           — ?? and ?. → null-test + branch
///   9.  <see cref="WithLowering"/>               — `with { f: v }` → copy + field stores
///   10. <see cref="LetGuardLowering"/>           — `let x = e else { }` → nil-guard + bind
///   11. <see cref="ForEachLowering"/>            — sync for-each → GetEnumerator/MoveNext loop (+ loop-scoped dispose)
///   12. <see cref="AssignmentLowering"/>         — compound assignments → binary + assign
///   13. <see cref="EventLowering"/>              — raise → capture-then-invoke
///   14. <see cref="ConcurrencyLowering"/>        — chan/spawn/select → stdlib construction/calls
///   15. <see cref="DeferLowering"/>              — defer → try/finally after spawn/select introduce lambda bodies
///   16. <see cref="LocalLowering"/>              — local storage conversions and address-carrier normalization
///   17. <see cref="ClosureConversion"/>          — lambda → display class + delegate
///   18. <see cref="IteratorLowering"/>           — IAsyncEnumerable wrapper → iterator struct
///   19. <see cref="AsyncLowering"/>              — async functions → state-machine struct + stub
///
/// (Interpolation is NOT a pass — a <c>BoundInterpolatedStringExpression</c> is a CORE leaf CodeGen
/// emits directly as <c>string.Concat</c>. Union layout is NOT a pass — CodeGen emits the tag-enum +
/// struct and the case tags itself. Syntax.PropertyLowering and AsyncLetLowering run at parse/bind
/// time; the semantic <see cref="PropertyLowering"/> pass below owns bound storage lowering.)
///
/// After the last pass the pipeline calls <see cref="AssertCoreOnly"/> which walks every remaining
/// bound node and panics (in debug) / reports diagnostics (in release) if any FEATURE node is still
/// present. CodeGen asserts the same invariant at entry.
/// </summary>
public sealed class LoweringPipeline
{
    // Note: Syntax.PropertyLowering and AsyncLetLowering run at parse/bind time inside
    // Esharp.Compiler (DeclarationParser, DeclarationBinder); their names do not
    // denote the bound-tree passes below.
    static readonly IReadOnlyList<IBoundTreePass> Passes =
    [
        PropertyLowering.Instance,       // 1
        AsyncStreamLowering.Instance,    // 2
        AsyncForeachLowering.Instance,   // 3
        ExpressionFormLowering.Instance, // 4
        ScopedMutLowering.Instance,      // 5
        MatchLowering.Instance,          // 6
        ResultLowering.Instance,         // 7
        NullFlowLowering.Instance,       // 8
        WithLowering.Instance,           // 9
        LetGuardLowering.Instance,       // 10
        ForEachLowering.Instance,        // 11
        AssignmentLowering.Instance,     // 12
        EventLowering.Instance,          // 13
        ConcurrencyLowering.Instance,    // 14
        DeferLowering.Instance,          // 15
        LocalLowering.Instance,          // 16
        ClosureConversion.Instance,      // 17
        IteratorLowering.Instance,       // 18
        AsyncLowering.Instance,          // 19
    ];

    readonly SynthesizedSymbolSink _sink;

    public LoweringPipeline(SynthesizedSymbolSink sink) => _sink = sink;

    /// <summary>
    /// Run all passes in order. Returns the fully-lowered program carrying only CORE nodes.
    /// Throws <see cref="LoweringAssertionException"/> (debug) or appends diagnostics
    /// (release) if any FEATURE node survives.
    /// </summary>
    public BoundProgram Run(BoundProgram program)
    {
        var current = program;
        var diagnostics = program.Data.Diagnostics;
        foreach (var pass in Passes)
        {
            // Each pass under the ICE guard: a crash in one lowering pass becomes a
            // located ES9500 (Lower phase + the pass that failed) and the pipeline
            // continues from that pass's input, so the failure reads as a clear
            // diagnostic rather than an opaque stack trace — and any FEATURE node the
            // skipped pass would have eliminated is then caught downstream by
            // AssertCoreOnly / CodeGen.
            var input = current;
            var thisPass = pass;
            using var _ = CompilerTrace.Work($"lowering pass {thisPass.GetType().Name}");
            current = CompilerTrace.Guard(diagnostics, CompilerPhase.Lower, default(SourceSpan),
                () => thisPass.Lower(input, _sink), fallback: input);
        }

        AssertCoreOnly(current);
        return current;
    }

    // ─── CORE-only assertion ──────────────────────────────────────────────────

    /// <summary>
    /// Walk the entire bound tree and collect any surviving FEATURE nodes. Throws in debug;
    /// appends to a diagnostic list in release builds so CodeGen can report them.
    /// </summary>
    public static void AssertCoreOnly(BoundProgram program)
    {
        var violations = new List<(string NodeType, SourceSpan Span)>();

        foreach (var unit in program.Units)
        foreach (var member in unit.Members)
            CheckMember(member, violations);

        if (violations.Count == 0) return;

        var summary = string.Join("\n", violations.Select(v => $"  FEATURE node '{v.NodeType}' at {v.Span}"));
#if DEBUG
        throw new LoweringAssertionException(
            $"Lowering pipeline produced {violations.Count} surviving FEATURE node(s):\n{summary}");
#else
        foreach (var (nodeType, span) in violations)
            program.Data.Diagnostics.Report(span, DiagnosticDescriptors.InternalError,
                $"FEATURE node '{nodeType}' was not lowered before CodeGen");
#endif
    }

    static void CheckMember(BoundMember member, List<(string, SourceSpan)> violations)
    {
        switch (member)
        {
            case BoundFunctionDeclaration fn:
                CheckBlock(fn.Body, violations);
                break;
            case BoundDataDeclaration d:
                if (d.InstanceMethods is { Count: > 0 } methods)
                    foreach (var fn in methods) CheckBlock(fn.Body, violations);
                break;
            case BoundStaticFuncDeclaration sf:
                foreach (var fn in sf.Functions) CheckBlock(fn.Body, violations);
                break;
            case BoundNamespaceInitDeclaration init:
                CheckBlock(init.Body, violations);
                break;
            case BoundNamespaceStateDeclaration state:
                if (state.Field.DefaultValue is not null) CheckExpression(state.Field.DefaultValue, violations);
                if (state.ComputedGetter is not null) CheckExpression(state.ComputedGetter, violations);
                if (state.SetterBody is not null) CheckExpression(state.SetterBody, violations);
                break;
        }
    }

    static void CheckBlock(BoundBlockStatement block, List<(string, SourceSpan)> violations)
    {
        foreach (var s in block.Statements)
            CheckStatement(s, violations);
    }

    static void CheckStatement(BoundStatement s, List<(string, SourceSpan)> violations)
    {
        switch (s)
        {
            // ── FEATURE statements — must not reach CodeGen ───────────────────

            case BoundMatchStatement m:
                violations.Add((nameof(BoundMatchStatement), m.Span));
                break;

            case BoundDeferStatement d:
                violations.Add((nameof(BoundDeferStatement), d.Span));
                CheckBlock(d.Body, violations);
                break;

            case BoundRaiseStatement r:
                violations.Add((nameof(BoundRaiseStatement), r.Span));
                break;

            case BoundForEachStatement fe:
                violations.Add((nameof(BoundForEachStatement), fe.Span));
                break;

            case BoundLetGuard g:
                violations.Add((nameof(BoundLetGuard), g.Span));
                break;

            case BoundCompoundAssignment ca:
                violations.Add((nameof(BoundCompoundAssignment), ca.Span));
                break;

            case BoundSelectStatement sel:
                violations.Add((nameof(BoundSelectStatement), sel.Span));
                break;

            case BoundAsyncLetStatement al:
                violations.Add((nameof(BoundAsyncLetStatement), al.Span));
                break;

            case BoundYieldStatement y:
                violations.Add((nameof(BoundYieldStatement), y.Span));
                break;

            // ── CORE structural statements — recurse ───────────────────────────

            case BoundBlockStatement b:
                CheckBlock(b, violations);
                break;

            case BoundIfStatement i:
                CheckBlock(AsBlock(i.Then), violations);
                if (i.Else is not null) CheckBlock(AsBlock(i.Else), violations);
                break;

            case BoundWhileStatement w:
                CheckBlock(AsBlock(w.Body), violations);
                break;

            case BoundTryStatement tr:
                CheckBlock(tr.Body, violations);
                foreach (var c in tr.Catches) CheckBlock(c.Body, violations);
                break;

            case BoundVariableDeclaration v:
                CheckExpression(v.Initializer, violations);
                break;

            case BoundReturnStatement r when r.Expression is not null:
                CheckExpression(r.Expression, violations);
                break;

            case BoundExpressionStatement e:
                CheckExpression(e.Expression, violations);
                break;

            case BoundAssignment a:
                CheckExpression(a.Target, violations);
                CheckExpression(a.Value, violations);
                break;
        }
    }

    static void CheckExpression(BoundExpression e, List<(string, SourceSpan)> violations)
    {
        switch (e)
        {
            // ── FEATURE expressions — must not reach CodeGen ──────────────────

            case BoundMatchExpression m:
                violations.Add((nameof(BoundMatchExpression), m.Span));
                break;

            case BoundIfExpression ie:
                violations.Add((nameof(BoundIfExpression), ie.Span));
                break;

            case BoundTryUnwrapExpression u:
                violations.Add((nameof(BoundTryUnwrapExpression), u.Span));
                break;

            case BoundWithExpression w:
                violations.Add((nameof(BoundWithExpression), w.Span));
                break;

            case BoundNullCoalescingExpression nc:
                violations.Add((nameof(BoundNullCoalescingExpression), nc.Span));
                break;

            case BoundNullConditionalAccessExpression nca:
                violations.Add((nameof(BoundNullConditionalAccessExpression), nca.Span));
                break;

            // BoundInterpolatedStringExpression is CORE — CodeGen emits string.Concat directly. Recurse.
            case BoundInterpolatedStringExpression interp:
                foreach (var part in interp.Parts)
                    if (part.Expr is not null) CheckExpression(part.Expr, violations);
                break;

            // BoundResultCallExpression (ok/error) must be lowered by ResultLowering.
            case BoundResultCallExpression res:
                violations.Add((nameof(BoundResultCallExpression), res.Span));
                break;

            case BoundChanCreationExpression ch:
                violations.Add((nameof(BoundChanCreationExpression), ch.Span));
                break;

            case BoundSpawnExpression sp:
                violations.Add((nameof(BoundSpawnExpression), sp.Span));
                break;

            // BoundFunctionLiteralExpression must be lowered by ClosureConversion.
            case BoundFunctionLiteralExpression fl:
                violations.Add((nameof(BoundFunctionLiteralExpression), fl.Span));
                break;

            // BoundAwaitExpression must be lowered by AsyncLowering.
            case BoundAwaitExpression aw:
                violations.Add((nameof(BoundAwaitExpression), aw.Span));
                break;

            // BoundConditionalExpression is CORE — CodeGen emits the ternary directly. Recurse.
            case BoundConditionalExpression cond:
                CheckExpression(cond.Condition, violations);
                CheckExpression(cond.Consequence, violations);
                CheckExpression(cond.Alternative, violations);
                break;

            // ── CORE expressions — recurse ────────────────────────────────────

            // BoundDotCaseExpression is CORE: CodeGen emits it directly (a `.case`
            // leaf resolved against its contextual type at emit time). It is NOT a
            // feature node — CodeGen's FeatureNodeCollector classifies it as a CORE
            // leaf, and this assertion must match that classification exactly.
            case BoundDotCaseExpression:
                break;

            case BoundCallExpression c:
                CheckExpression(c.Target, violations);
                foreach (var arg in c.Arguments) CheckExpression(arg, violations);
                break;

            case BoundMemberAccessExpression ma:
                CheckExpression(ma.Target, violations);
                break;

            case BoundBinaryExpression bin:
                CheckExpression(bin.Left, violations);
                CheckExpression(bin.Right, violations);
                break;

            case BoundUnaryExpression u:
                CheckExpression(u.Operand, violations);
                break;

            // Conversions (CORE — survive lowering, CodeGen handles them).
            case BoundConversion cv:
                CheckExpression(cv.Operand, violations);
                break;

            case BoundObjectCreationExpression oc:
                foreach (var fi in oc.Fields) CheckExpression(fi.Value, violations);
                break;

            case BoundIndexExpression ix:
                CheckExpression(ix.Target, violations);
                CheckExpression(ix.Index,  violations);
                break;

            case BoundTupleLiteralExpression tl:
                foreach (var el in tl.Elements) CheckExpression(el, violations);
                break;
        }
    }

    static BoundBlockStatement AsBlock(BoundStatement s) =>
        s as BoundBlockStatement ?? new BoundBlockStatement([s]) { Span = s.Span };
}

/// <summary>
/// Thrown in DEBUG builds when a FEATURE node survives the lowering pipeline.
/// </summary>
public sealed class LoweringAssertionException(string message) : Exception(message);
