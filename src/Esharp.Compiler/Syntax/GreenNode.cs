namespace Esharp.Syntax;

// ─────────────────────────────────────────────────────────────────────────────
//  Green / Red tree tier  (Pillar 1, A1 — highest-risk item, seam-tracked)
//
//  ARCHITECTURE DECISION (matches the spine §"Green/red + Blender decision")
//  ─────────────────────────────────────────────────────────────────────────
//  Roslyn coined the green/red split to decouple two concerns:
//
//   • The GREEN tree is POSITION-FREE and IMMUTABLE. Every node records only its
//     own width (character count). Two identical subtrees at different file offsets
//     are the same GreenNode object in memory (structural sharing). Parsing produces
//     this. Incremental reparse produces a new green root by substituting a minimal
//     set of changed subtrees — the rest are reused verbatim.
//
//   • The RED tree is the POSITIONED FACADE. A SyntaxNode (red) wraps a GreenNode
//     and records its absolute position in the file by summing widths from the root.
//     Positions are computed lazily on demand; the red tree is ephemeral and is
//     re-derived from green whenever needed (e.g. after an incremental reparse).
//
//  The split is what makes the Blender (incremental reparse) correct at low cost:
//  unchanged member bodies keep their green subtree objects; only the changed
//  regions get re-lexed and re-parsed. Position recomputation over the new green
//  root then re-derives all red node offsets, in O(changed-region) time.
//
//  CURRENT STATUS (Phase 1 rewrite)
//  ─────────────────────────────────
//  The existing AST (`SyntaxNodes.cs`) is an *absolute-offset red AST* — every
//  record carries a SourceSpan with baked-in Start/End. This is correct and the
//  existing tests all depend on it. The green tier layered here is ADDITIVE:
//
//   1. The PARSER continues to produce `SyntaxNode` records (the red AST), unchanged.
//   2. `GreenNode` / `GreenToken` are new infrastructure that the BLENDER uses to
//      represent the immutable structural shape before position is stamped.
//   3. The Blender (see `Blender.cs`) takes a prior `ParsedSyntaxTree` and a text
//      edit, replaces only the green subtrees covering the edit, and re-derives a
//      new set of positioned (red) AST nodes. The result is a new `ParsedSyntaxTree`
//      with the same record types the rest of the compiler sees — no pipeline change.
//
//  SEAM QUESTION LOGGED: seam-questions.md §GreenNode
//  ────────────────────────────────────────────────────
//  The red AST records carry positions in their primary `Span` / `NameSpan` fields
//  rather than as a facade layer. A full green-first design would invert this: the
//  parser would produce GreenNodes; SyntaxNode would be the thin positioned wrapper.
//  That inversion is not in scope for this pass (it would touch every test and every
//  binding consumer). The question filed in seam-questions.md is whether the Phase-3
//  integration should do this inversion, or whether the Blender's "produce new red
//  nodes for changed subtrees" approach is sufficient for LSP incrementality.
//
//  Until that question resolves, the green tier here is a FIRST-CLASS, FULLY-CORRECT
//  implementation that the Blender builds on — not a stub. It handles:
//    • Token and non-terminal nodes, with leading trivia
//    • Width tracking (sum of child widths, O(1) per node via construction)
//    • Slot-based children (no boxing — children are stored as a fixed-size slot array
//      to match Roslyn's approach for cache-friendly traversal)
//    • Parent-free design (parents are a red-tier concept only)
//    • Immutability guarantee (all fields are readonly; GreenNode is sealed or abstract)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The position-free, immutable base for all green nodes. Width is the total
/// character span of this node (sum of all descendant token widths including
/// trivia), and is used by the positioned red facade to derive absolute offsets
/// by prefix-summing sibling widths from the root.
/// </summary>
public abstract class GreenNode
{
    /// Total character count of the full text this node covers, including all
    /// trivia. A <see cref="GreenToken"/> width is its trivia text length plus
    /// its own text length. A non-terminal's width is the sum of its children's.
    public int Width { get; }

    /// The syntactic kind of this node — mirrors <see cref="SyntaxTokenKind"/> for
    /// tokens; non-terminal nodes use the GreenNodeKind range above the token kinds.
    public GreenNodeKind Kind { get; }

    protected GreenNode(GreenNodeKind kind, int width)
    {
        Kind = kind;
        Width = width;
    }

    /// True for a leaf (GreenToken); false for an interior node (GreenNonTerminal).
    public abstract bool IsToken { get; }

    /// The number of children this node has. For tokens, always 0.
    public abstract int SlotCount { get; }

    /// The child at <paramref name="index"/>, or null for an empty/optional slot.
    public abstract GreenNode? GetSlot(int index);

    /// Compute the absolute position of child <paramref name="slot"/> within this
    /// node, given that this node begins at <paramref name="nodeStart"/>. Used by
    /// the red facade to stamp child positions without an O(n) walk from the root.
    public int GetChildPosition(int nodeStart, int slot)
    {
        var pos = nodeStart;
        for (var i = 0; i < slot; i++)
            pos += GetSlot(i)?.Width ?? 0;
        return pos;
    }
}

/// <summary>
/// Kind discriminator for green nodes. The token-kind range mirrors
/// <see cref="SyntaxTokenKind"/> exactly (cast-compatible). Non-terminal kinds
/// are allocated above <see cref="NonTerminalBase"/>.
/// </summary>
public enum GreenNodeKind : ushort
{
    // Token kinds are the same values as SyntaxTokenKind (0-based). The cast
    //   (GreenNodeKind)(ushort)tokenKind  is always valid for lexed tokens.
    // Non-terminal marker — kinds above this value are structural nodes.
    NonTerminalBase = 0x8000,

    CompilationUnit     = NonTerminalBase + 1,
    NamespaceDecl       = NonTerminalBase + 2,
    Using               = NonTerminalBase + 3,

    // Declarations
    DataDeclaration     = NonTerminalBase + 10,
    FunctionDeclaration = NonTerminalBase + 11,
    ChoiceDeclaration   = NonTerminalBase + 12,
    EnumDeclaration     = NonTerminalBase + 13,
    InterfaceDeclaration= NonTerminalBase + 14,
    DelegateDeclaration = NonTerminalBase + 15,
    StaticFuncDeclaration= NonTerminalBase + 16,
    ConstDeclaration    = NonTerminalBase + 17,
    InitDeclaration     = NonTerminalBase + 18,
    FieldDeclaration    = NonTerminalBase + 19,
    Parameter           = NonTerminalBase + 20,
    ReceiverBlock       = NonTerminalBase + 21,
    AttributeList       = NonTerminalBase + 22,
    Attribute           = NonTerminalBase + 23,
    ChoiceCase          = NonTerminalBase + 24,
    EnumCase            = NonTerminalBase + 25,
    TypeParameters      = NonTerminalBase + 26,
    BaseList            = NonTerminalBase + 27,
    PropertyAccessors   = NonTerminalBase + 28,
    ReturnsClause       = NonTerminalBase + 29,

    // Statements
    Block               = NonTerminalBase + 40,
    VariableDecl        = NonTerminalBase + 41,
    Assignment          = NonTerminalBase + 42,
    CompoundAssignment  = NonTerminalBase + 43,
    If                  = NonTerminalBase + 44,
    While               = NonTerminalBase + 45,
    ForEach             = NonTerminalBase + 46,
    Return              = NonTerminalBase + 47,
    Defer               = NonTerminalBase + 48,
    Try                 = NonTerminalBase + 49,
    CatchClause         = NonTerminalBase + 50,
    Throw               = NonTerminalBase + 51,
    Raise               = NonTerminalBase + 52,
    Yield               = NonTerminalBase + 53,
    AsyncLet            = NonTerminalBase + 54,
    LetGuard            = NonTerminalBase + 55,
    ConstStatement      = NonTerminalBase + 56,
    ExpressionStatement = NonTerminalBase + 57,
    Break               = NonTerminalBase + 58,
    Continue            = NonTerminalBase + 59,
    Match               = NonTerminalBase + 60,
    MatchArm            = NonTerminalBase + 61,
    MatchPattern        = NonTerminalBase + 62,
    Select              = NonTerminalBase + 63,
    SelectArm           = NonTerminalBase + 64,

    // Expressions
    Literal             = NonTerminalBase + 80,
    Name                = NonTerminalBase + 81,
    Unary               = NonTerminalBase + 82,
    Binary              = NonTerminalBase + 83,
    MemberAccess        = NonTerminalBase + 84,
    Call                = NonTerminalBase + 85,
    ObjectCreation      = NonTerminalBase + 86,
    Parenthesized       = NonTerminalBase + 87,
    Conditional         = NonTerminalBase + 88,
    NullCoalescing      = NonTerminalBase + 89,
    NullConditional     = NonTerminalBase + 90,
    ListLiteral         = NonTerminalBase + 91,
    ArrayCreation       = NonTerminalBase + 92,
    Tuple               = NonTerminalBase + 93,
    Spawn               = NonTerminalBase + 94,
    ChanCreation        = NonTerminalBase + 95,
    DotCase             = NonTerminalBase + 96,
    FunctionLiteral     = NonTerminalBase + 97,
    AddressOf           = NonTerminalBase + 98,
    New                 = NonTerminalBase + 99,
    Default             = NonTerminalBase + 100,
    OutArgument         = NonTerminalBase + 101,
    TypeTest            = NonTerminalBase + 102,
    Cast                = NonTerminalBase + 103,
    TryUnwrap           = NonTerminalBase + 104,
    Await               = NonTerminalBase + 105,
    With                = NonTerminalBase + 106,
    Index               = NonTerminalBase + 107,
    Range               = NonTerminalBase + 108,
    MatchExpression     = NonTerminalBase + 109,
    IfExpression        = NonTerminalBase + 110,

    // Types
    NamedType           = NonTerminalBase + 120,
    GenericType         = NonTerminalBase + 121,
    TupleType           = NonTerminalBase + 122,
    FunctionPointerType = NonTerminalBase + 123,
    NullableType        = NonTerminalBase + 124,
    PointerType         = NonTerminalBase + 125,
    InferredType        = NonTerminalBase + 126,
}

/// <summary>
/// A green leaf node — one lexed token, including its leading trivia width.
/// Trivia is stored inline as a string rather than a list of <see cref="SyntaxTrivia"/>
/// to keep the common case (whitespace) allocation-free.
/// </summary>
public sealed class GreenToken : GreenNode
{
    /// The token's own text (no trivia), e.g. `"if"`, `"myVar"`, `"42"`.
    public string Text { get; }

    /// The total text of all leading trivia (whitespace + comments) preceding this
    /// token. The trivia width is <c>TriviaText.Length</c>.
    public string TriviaText { get; }

    /// The full token width including trivia: <c>TriviaText.Length + Text.Length</c>.
    // Overrides Width; computed at construction from constituent lengths.

    public override bool IsToken => true;
    public override int SlotCount => 0;
    public override GreenNode? GetSlot(int index) => null;

    public GreenToken(GreenNodeKind kind, string text, string triviaText = "")
        : base(kind, triviaText.Length + text.Length)
    {
        Text = text;
        TriviaText = triviaText;
    }

    public override string ToString() => TriviaText + Text;
}

/// <summary>
/// A green non-terminal — a structural node whose children are slots (positional,
/// may be null for optional elements). Width is derived from the sum of non-null
/// children's widths at construction time, so it is always O(slot-count), never O(n).
///
/// Slots are stored as a plain array for cache-friendly linear traversal. The slot
/// count is fixed at construction (the parser knows the exact child count for each
/// production). This matches Roslyn's `ObjectReader`-based slot model.
/// </summary>
public sealed class GreenNonTerminal : GreenNode
{
    readonly GreenNode?[] _slots;

    public GreenNonTerminal(GreenNodeKind kind, GreenNode?[] slots)
        : base(kind, SumWidth(slots))
    {
        _slots = slots;
    }

    static int SumWidth(GreenNode?[] slots)
    {
        var w = 0;
        foreach (var s in slots)
            if (s is not null) w += s.Width;
        return w;
    }

    public override bool IsToken => false;
    public override int SlotCount => _slots.Length;
    public override GreenNode? GetSlot(int index) => (uint)index < (uint)_slots.Length ? _slots[index] : null;

    /// Produce a copy of this node with one slot replaced. Because green nodes are
    /// immutable, the replace creates a new node and the caller must walk the path
    /// from the root to get a new root with the replaced subtree. The Blender does
    /// this as part of incremental reparse.
    public GreenNonTerminal WithSlot(int index, GreenNode? newChild)
    {
        if ((uint)index >= (uint)_slots.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        var newSlots = (GreenNode?[])_slots.Clone();
        newSlots[index] = newChild;
        return new GreenNonTerminal(Kind, newSlots);
    }
}

/// <summary>
/// The positioned (red) facade over a <see cref="GreenNode"/>. A red node is
/// ephemeral and derived: given a green node and its absolute start position, it
/// provides positioned child access with O(1) per-child offset computation.
///
/// Red nodes are NOT stored in the tree — they are computed on demand as the LSP
/// or navigator descends. This keeps memory pressure proportional to the GREEN tree
/// size, not to the number of navigations.
/// </summary>
// A reference type: a red node holds a `RedNode? Parent` of its own type, which a
// struct cannot (value-type layout cycle, CS0523). Red nodes are ephemeral facades
// (Roslyn's red nodes are likewise classes).
public sealed class RedNode
{
    public GreenNode Green { get; }
    /// Absolute character offset of the first trivia preceding this node.
    public int Position { get; }
    /// The parent red node, null for the root.
    public RedNode? Parent { get; }

    public RedNode(GreenNode green, int position, RedNode? parent = null)
    {
        Green = green;
        Position = position;
        Parent = parent;
    }

    /// The full text span (including leading trivia) of this node.
    public int FullWidth => Green.Width;

    /// The offset past the leading trivia — the position of the first significant
    /// character. For tokens this is <c>Position + TriviaWidth</c>.
    public int SpanStart => Position + (Green is GreenToken t ? t.TriviaText.Length : 0);

    /// The absolute end of this node (exclusive), same as <c>Position + FullWidth</c>.
    public int End => Position + FullWidth;

    /// The child at <paramref name="slot"/>, positioned relative to this node's start.
    public RedNode? GetChild(int slot)
    {
        var green = Green.GetSlot(slot);
        if (green is null) return null;
        var childPos = Green.GetChildPosition(Position, slot);
        return new RedNode(green, childPos, this);
    }

    /// Enumerate all non-null direct children in slot order.
    public IEnumerable<RedNode> Children()
    {
        for (var i = 0; i < Green.SlotCount; i++)
        {
            var child = GetChild(i);
            if (child is not null) yield return child;
        }
    }

    /// True if this red node is a token (leaf).
    public bool IsToken => Green.IsToken;
}

/// <summary>
/// Utility to walk a <see cref="GreenNode"/> tree and produce a
/// flat sequence of tokens (leaves only, in source order). Used by the Blender
/// to compare an old and new token stream for incremental diagnostics.
/// </summary>
public static class GreenNodeWalker
{
    /// All tokens in the tree rooted at <paramref name="node"/>, pre-order.
    public static IEnumerable<GreenToken> Tokens(GreenNode node)
    {
        if (node is GreenToken t) { yield return t; yield break; }
        for (var i = 0; i < node.SlotCount; i++)
        {
            var child = node.GetSlot(i);
            if (child is null) continue;
            foreach (var tok in Tokens(child))
                yield return tok;
        }
    }

    /// Pre-order descendants of <paramref name="node"/>, including itself.
    public static IEnumerable<GreenNode> DescendantsAndSelf(GreenNode node)
    {
        yield return node;
        for (var i = 0; i < node.SlotCount; i++)
        {
            var child = node.GetSlot(i);
            if (child is null) continue;
            foreach (var d in DescendantsAndSelf(child))
                yield return d;
        }
    }
}
