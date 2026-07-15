using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Lowering;

/// <summary>
/// Lowers local declaration storage after feature lowering has introduced its
/// temporaries and before closure conversion captures them.
/// </summary>
/// <remarks>
/// A bare typed local is a mutable value slot; <c>let</c> and <c>var</c> are
/// compiler-managed readonly and mutable locations respectively. Binding records
/// that source representation on <see cref="LocalSymbol"/>. This pass owns the
/// shared storage consequences: synthesized local stores receive the same explicit
/// coercion boundary as source declarations, and pointer escape analysis asks this
/// class to raise an addressable local to one durable <c>__Ptr_T</c> carrier.
/// </remarks>
public sealed class LocalLowering : IBoundTreePass
{
    public static readonly LocalLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new LocalStoreRewriter());

    /// <summary>
    /// Realizes every source address alias proven to cross a durable boundary.
    /// The declaration, all symbol-backed uses, and closure capture manifests are
    /// rewritten as one wrapper carrier so writes through either spelling retain
    /// one identity. This is invoked after pointer representation analysis, before
    /// the ordinary lowering pipeline.
    /// </summary>
    internal static BoundFunctionDeclaration RealizeEscapingAddressableLocals(
        BoundFunctionDeclaration function,
        IReadOnlyDictionary<LocalSymbol, BoundType> durableAliases)
    {
        var rewriter = new EscapingAddressLocalRewriter(durableAliases);
        var body = (BoundBlockStatement)rewriter.RewriteStatement(function.Body);
        return ReferenceEquals(body, function.Body) ? function : function with { Body = body };
    }

    sealed class LocalStoreRewriter : BoundTreeRewriter
    {
        protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
        {
            var rewritten = (BoundVariableDeclaration)base.RewriteVariableDeclaration(node);
            // Sync futures deliberately retain their ValueTask slot until
            // SyncFutureLowering installs the join. Pointer carriers are already
            // representation-selected by escape analysis and are not conversions.
            if (rewritten.IsSyncFuture
                || rewritten.DeclaredType is HeapPointerBoundType or ByRefBoundType)
                return rewritten;

            var initializer = PropertyLowering.CoerceStore(rewritten.Initializer, rewritten.DeclaredType);
            return ReferenceEquals(initializer, rewritten.Initializer)
                ? rewritten
                : rewritten with { Initializer = initializer };
        }
    }

    sealed class EscapingAddressLocalRewriter(
        IReadOnlyDictionary<LocalSymbol, BoundType> durableAliases)
        : BoundTreeRewriter
    {
        // Function literals retain a compact capture manifest in addition to their
        // body nodes. Bridge the promoted local identity into that representation so
        // closure conversion cannot emit a stale T& display field for a __Ptr_T use.
        readonly IReadOnlyDictionary<string, BoundType> _durableAliasesByName = durableAliases
            .GroupBy(pair => pair.Key.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

        protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
        {
            var rewritten = (BoundVariableDeclaration)base.RewriteVariableDeclaration(node);
            return rewritten.Local is { } local && durableAliases.TryGetValue(local, out var durableType)
                ? rewritten with { DeclaredType = durableType }
                : rewritten;
        }

        protected override BoundExpression RewriteNameExpression(BoundNameExpression node)
        {
            if (node.Symbol is not LocalSymbol local
                || !durableAliases.TryGetValue(local, out var durableType))
                return node;
            return new BoundNameExpression(node.Name, durableType)
            {
                Symbol = node.Symbol,
                Span = node.Span,
            };
        }

        protected override BoundExpression RewriteFunctionLiteralExpression(BoundFunctionLiteralExpression node)
        {
            var rewritten = (BoundFunctionLiteralExpression)base.RewriteFunctionLiteralExpression(node);
            List<BoundCapturedVariable>? captures = null;
            for (var i = 0; i < rewritten.CapturedVariables.Count; i++)
            {
                var capture = rewritten.CapturedVariables[i];
                var replacement = capture.Type is ByRefBoundType
                    && _durableAliasesByName.TryGetValue(capture.Name, out var durableType)
                    ? capture with { Type = durableType }
                    : capture;
                if (!ReferenceEquals(replacement, capture) && captures is null)
                    captures = [.. rewritten.CapturedVariables.Take(i)];
                captures?.Add(replacement);
            }
            return captures is null ? rewritten : rewritten with { CapturedVariables = captures };
        }
    }
}
