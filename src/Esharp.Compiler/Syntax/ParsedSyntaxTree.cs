using Esharp.Diagnostics;

namespace Esharp.Syntax;

/// A lossless parse result: the root node, the verbatim source, the full token
/// stream (with trivia attached), and diagnostics. This is the bundle the printer,
/// navigator, and a future LSP/SemanticModel consume — the tree alone is abstract
/// (it drops punctuation and trivia), so byte-exact reconstruction needs the source
/// and tokens alongside it.
///
/// <b>[Δ]</b> The <see cref="Tokens"/> list is now the <b>primary formatter surface</b>:
/// the E# formatter (written in E#) drives entirely from the token stream + trivia,
/// never re-traversing the AST, so a format-on-save operation is a single O(n) pass
/// over this list with no re-parse.
///
/// <see cref="GetIndex"/> builds (and caches) a <see cref="SyntaxIndex"/> for fast
/// position→node lookups, used by LSP hover, go-to-definition, and semantic tokens.
public sealed class ParsedSyntaxTree
{
    public CompilationUnitSyntax Root { get; }
    /// The verbatim source text — the formatter slices tokens from this when it
    /// needs a verbatim textual range.
    public string Source { get; }
    /// The full lossless token stream, trivia attached to each token's
    /// <see cref="SyntaxToken.Leading"/> list.  The formatter's primary input.
    public IReadOnlyList<SyntaxToken> Tokens { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    // Lazily-built position index; null until the first GetIndex() call.
    SyntaxIndex? _index;
    readonly object _indexLock = new();

    public ParsedSyntaxTree(
        CompilationUnitSyntax root,
        string source,
        IReadOnlyList<SyntaxToken> tokens,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Root = root;
        Source = source;
        Tokens = tokens;
        Diagnostics = diagnostics;
    }

    /// Returns (building once, on demand) a <see cref="SyntaxIndex"/> for O(log n)
    /// position→node lookup.  Safe to call from multiple threads simultaneously.
    public SyntaxIndex GetIndex()
    {
        if (_index is not null) return _index;
        lock (_indexLock)
        {
            _index ??= SyntaxIndex.Build(Root);
            return _index;
        }
    }

    /// Invalidate the cached index — call when a tree is cloned with modifications
    /// (e.g. after an incremental reparse from the Blender).
    public void InvalidateIndex() { lock (_indexLock) { _index = null; } }
}
