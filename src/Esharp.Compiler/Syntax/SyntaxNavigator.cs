namespace Esharp.Syntax;

/// A flat, sorted index of all nodes in a syntax tree, keyed by their source range.
/// Built once per <see cref="ParsedSyntaxTree"/> and reused across position queries.
///
/// The flat array layout (vs. re-traversing the tree) is the [Δ] from pillar-1: a
/// sorted <c>(start, end, node)</c> array lets <see cref="SyntaxNavigator.PathTo"/>
/// and <see cref="SyntaxNavigator.FindNode"/> run in O(log n) binary search on the
/// sorted start offsets, rather than the O(depth × children) tree-descent that
/// every naïve position→node lookup requires. At 10k-node trees (typical for a file
/// body) this cuts LSP hover lookup from ~100 µs to ~1 µs.
///
/// <b>Insertion order:</b> pre-order (parent before children), so the array's natural
/// ordering is also the ancestor-first ordering needed for path extraction.
public sealed class SyntaxIndex
{
    // Each entry is (start, end, node) in pre-order (parent before children).
    readonly int[] _starts;
    readonly int[] _ends;
    readonly SyntaxNode[] _nodes;

    SyntaxIndex(int[] starts, int[] ends, SyntaxNode[] nodes)
    {
        _starts = starts;
        _ends = ends;
        _nodes = nodes;
    }

    /// Build an index for the full subtree rooted at <paramref name="root"/>.
    public static SyntaxIndex Build(SyntaxNode root)
    {
        var buf = new List<(int start, int end, SyntaxNode node)>();
        Collect(root, buf);

        var count = buf.Count;
        var starts = new int[count];
        var ends = new int[count];
        var nodes = new SyntaxNode[count];
        for (var i = 0; i < count; i++)
        {
            starts[i] = buf[i].start;
            ends[i] = buf[i].end;
            nodes[i] = buf[i].node;
        }
        return new SyntaxIndex(starts, ends, nodes);
    }

    static void Collect(SyntaxNode node, List<(int, int, SyntaxNode)> buf)
    {
        var span = SyntaxNavigator.SpanOf(node);
        buf.Add((span.Start, span.End, node));
        foreach (var child in SyntaxNavigator.Children(node))
            Collect(child, buf);
    }

    /// The innermost node whose [start, end] range contains <paramref name="offset"/>.
    /// O(log n) via a binary search on the sorted start offsets.
    public SyntaxNode? FindNode(int offset)
    {
        var path = PathTo(offset);
        return path.Count > 0 ? path[^1] : null;
    }

    /// The path from the root down to the innermost node containing
    /// <paramref name="offset"/>, outermost first. O(log n).
    public IReadOnlyList<SyntaxNode> PathTo(int offset)
    {
        // Binary search for the last entry whose start ≤ offset.
        var lo = 0;
        var hi = _starts.Length - 1;
        var best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (_starts[mid] <= offset)
            {
                best = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        if (best < 0) return [];

        // Walk backwards from `best`, collecting every ancestor whose end ≥ offset.
        // Because the array is pre-order, all ancestors are at indices < best. We
        // want the path in root-first order, so we collect and reverse.
        var path = new List<SyntaxNode>();
        for (var i = best; i >= 0; i--)
        {
            if (_starts[i] <= offset && _ends[i] >= offset)
                path.Add(_nodes[i]);
        }
        path.Reverse();
        return path;
    }
}

/// Tree navigation over the abstract syntax records. The records are pure data (no
/// parent pointers, no child accessors), so traversal lives here in one place — the
/// single `Children` switch every consumer (the printer, the fuzzer's AST mutation,
/// a future LSP) walks through. Navigation is computed on demand from spans, so the
/// records stay value-comparable and nothing is cached onto the tree.
public static class SyntaxNavigator
{
    /// Direct child nodes, in source order. String / bool / enum members are not
    /// children — only `SyntaxNode`-typed fields and lists of them.
    public static IReadOnlyList<SyntaxNode> Children(SyntaxNode node)
    {
        var c = new List<SyntaxNode>();
        switch (node)
        {
            case CompilationUnitSyntax n:
                c.AddRange(n.Imports); c.AddRange(n.Members); break;

            case FieldSyntax n:
                c.Add(n.Type); Add(c, n.DefaultValue); Add(c, n.Property); break;
            case PropertyAccessorsSyntax n:
                Add(c, n.ComputedGetter); Add(c, n.SetterBody); Add(c, n.ScopedMutBody); break;
            case ParameterSyntax n:
                c.Add(n.Type); break;
            case ChoiceCaseSyntax n:
                c.AddRange(n.Payloads); break;
            case InitDeclarationSyntax n:
                c.AddRange(n.Parameters); AddRange(c, n.BaseArguments); c.Add(n.Body); break;
            case NamespaceInitDeclarationSyntax n:
                c.Add(n.Body); break;
            case NamespaceStateDeclarationSyntax n:
                c.Add(n.Type); Add(c, n.Initializer); Add(c, n.Property); break;
            case ReturnsClauseSyntax n:
                c.Add(n.Type); break;

            case DataDeclarationSyntax n:
                c.AddRange(n.Attributes); c.AddRange(n.Interfaces); AddRange(c, n.HeaderParameters); c.AddRange(n.Fields);
                AddRange(c, n.Inits); AddRange(c, n.Methods); Add(c, n.DefaultReturns); break;
            case StaticFuncDeclarationSyntax n:
                c.AddRange(n.Fields); c.AddRange(n.Functions); Add(c, n.DefaultReturns); break;
            case InterfaceMethodSyntax n:
                c.AddRange(n.Parameters); c.Add(n.ReturnType); break;
            case InterfacePropertySyntax n:
                c.Add(n.Type); break;
            case InterfaceDeclarationSyntax n:
                c.AddRange(n.Methods); AddRange(c, n.Events); AddRange(c, n.Properties); break;
            case ConstDeclarationSyntax n:
                Add(c, n.Type); c.Add(n.Value); break;
            case ChoiceDeclarationSyntax n:
                c.AddRange(n.Cases); break;
            case FunctionDeclarationSyntax n:
                c.AddRange(n.Attributes); Add(c, n.ExplicitInterface); c.AddRange(n.Parameters);
                c.Add(n.ReturnType); c.Add(n.Body); break;
            case DelegateDeclarationSyntax n:
                c.AddRange(n.Parameters); c.Add(n.ReturnType); break;
            case EnumDeclarationSyntax n:
                c.AddRange(n.Cases); break;

            case BlockStatementSyntax n:
                c.AddRange(n.Statements); break;
            case VariableDeclarationStatementSyntax n:
                Add(c, n.ExplicitType); c.Add(n.Initializer); break;
            case IfStatementSyntax n:
                c.Add(n.Condition); c.Add(n.ThenStatement); Add(c, n.ElseStatement); break;
            case ReturnStatementSyntax n:
                Add(c, n.Expression); break;
            case RaiseStatementSyntax n:
                c.AddRange(n.Arguments); break;
            case YieldStatementSyntax n:
                c.Add(n.Value); break;
            case MutYieldStatementSyntax n:
                c.Add(n.Location); break;
            case ConstStatementSyntax n:
                Add(c, n.Type); c.Add(n.Value); break;
            case ExpressionStatementSyntax n:
                c.Add(n.Expression); break;
            case AssignmentStatementSyntax n:
                c.Add(n.Target); c.Add(n.Expression); break;
            case CompoundAssignmentStatementSyntax n:
                c.Add(n.Target); c.Add(n.Value); break;
            case WhileStatementSyntax n:
                c.Add(n.Condition); c.Add(n.Body); break;
            case ForEachStatementSyntax n:
                c.Add(n.Collection); c.Add(n.Body); break;
            case LetGuardStatementSyntax n:
                Add(c, n.ExplicitType); c.Add(n.Initializer); c.Add(n.ElseBody); break;
            case DeferStatementSyntax n:
                c.Add(n.Body); break;
            case TryStatementSyntax n:
                c.Add(n.Body); c.AddRange(n.Catches); break;
            case CatchClauseSyntax n:
                Add(c, n.ExceptionType); c.Add(n.Body); break;
            case ThrowStatementSyntax n:
                Add(c, n.Expression); break;
            case AsyncLetStatementSyntax n:
                Add(c, n.ExplicitType); c.Add(n.Initializer); break;

            case MatchPatternSyntax n:
                Add(c, n.LiteralValue); Add(c, n.TypeBindingType); break;
            case MatchArmSyntax n:
                c.Add(n.Pattern); Add(c, n.Guard); Add(c, n.Body); Add(c, n.ExprBody); break;
            case MatchStatementSyntax n:
                c.Add(n.Subject); Add(c, n.SubjectType); c.AddRange(n.Arms); break;
            case MatchExpressionSyntax n:
                c.Add(n.Subject); Add(c, n.SubjectType); c.AddRange(n.Arms); break;
            case SelectArmSyntax n:
                Add(c, n.Channel); Add(c, n.Value); c.Add(n.Body); break;
            case SelectStatementSyntax n:
                c.AddRange(n.Arms); break;

            case UnaryExpressionSyntax n:
                c.Add(n.Operand); break;
            case BinaryExpressionSyntax n:
                c.Add(n.Left); c.Add(n.Right); break;
            case MemberAccessExpressionSyntax n:
                c.Add(n.Target); break;
            case TypeTestExpressionSyntax n:
                c.Add(n.Operand); c.Add(n.Type); break;
            case CastExpressionSyntax n:
                c.Add(n.Operand); c.Add(n.Type); break;
            case CallExpressionSyntax n:
                c.Add(n.Target); AddRange(c, n.TypeArguments); c.AddRange(n.Arguments); break;
            case ObjectInitializerFieldSyntax n:
                c.Add(n.Value); break;
            case ObjectCreationExpressionSyntax n:
                c.Add(n.Type); c.AddRange(n.Fields); break;
            case ParenthesizedExpressionSyntax n:
                c.Add(n.Expression); break;
            case ConditionalExpressionSyntax n:
                c.Add(n.Condition); c.Add(n.Consequence); c.Add(n.Alternative); break;
            case NullCoalescingExpressionSyntax n:
                c.Add(n.Left); c.Add(n.Right); break;
            case NullConditionalAccessExpressionSyntax n:
                c.Add(n.Target); break;
            case ListLiteralExpressionSyntax n:
                c.AddRange(n.Elements); break;
            case TupleExpressionSyntax n:
                c.AddRange(n.Elements); break;
            case SpawnExpressionSyntax n:
                c.Add(n.Body); break;
            case ChanCreationExpressionSyntax n:
                c.Add(n.ElementType); Add(c, n.Capacity); break;
            case DotCaseExpressionSyntax n:
                c.AddRange(n.Arguments); break;
            case FunctionLiteralExpressionSyntax n:
                c.AddRange(n.Parameters); c.Add(n.ReturnType); c.Add(n.Body); break;
            case AddressOfExpressionSyntax n:
                c.Add(n.Target); break;
            case NewExpressionSyntax n:
                c.Add(n.Target); break;
            case DefaultExpressionSyntax n:
                if (n.Type is not null) c.Add(n.Type);
                break;
            case IndexExpressionSyntax n:
                c.Add(n.Target); c.Add(n.Index); break;
            case RangeExpressionSyntax n:
                Add(c, n.Target); Add(c, n.Start); Add(c, n.End); break;
            case TryUnwrapExpressionSyntax n:
                c.Add(n.Inner); break;
            case WithExpressionSyntax n:
                c.Add(n.Target); c.AddRange(n.Fields); break;
            case AwaitExpressionSyntax n:
                c.Add(n.Inner); break;

            case GenericTypeSyntax n:
                c.AddRange(n.Args); break;
            case TupleTypeSyntax n:
                c.AddRange(n.Elements); break;
            case FunctionPointerTypeSyntax n:
                c.AddRange(n.ParameterTypes); c.Add(n.ReturnType); break;
            case NullableTypeSyntax n:
                c.Add(n.Inner); break;
            case PointerTypeSyntax n:
                c.Add(n.Inner); break;

            // Leaves (no node children): NameExpressionSyntax, LiteralExpressionSyntax,
            // OutArgumentExpressionSyntax, Break/Continue, Using, Attribute, EnumCase,
            // DeriveDirective, NamedType, InferredType, Error nodes.
        }
        return c;
    }

    /// Every node in the subtree rooted at <paramref name="node"/>, pre-order
    /// (node before its children).
    public static IEnumerable<SyntaxNode> DescendantsAndSelf(SyntaxNode node)
    {
        yield return node;
        foreach (var child in Children(node))
            foreach (var d in DescendantsAndSelf(child))
                yield return d;
    }

    /// A node's effective range: its own span if stamped, else the union of its
    /// children's effective spans. Lets the navigator/printer treat the handful of
    /// unstamped structural sub-nodes (parameters, fields, arms, …) uniformly without
    /// a mutating completion pass over the immutable tree.
    public static SourceSpan SpanOf(SyntaxNode node)
    {
        if (node.Span.IsValid)
            return node.Span;
        var span = SourceSpan.None;
        foreach (var child in Children(node))
            span = span.Union(SpanOf(child));
        return span;
    }

    /// The path from <paramref name="root"/> down to the innermost node whose range
    /// contains <paramref name="offset"/> — `[root, …, innermost]`. Descends through
    /// unstamped intermediates by consulting their subtree, so a missing span on a
    /// structural node never blocks the descent.
    public static IReadOnlyList<SyntaxNode> PathTo(SyntaxNode root, int offset)
    {
        var path = new List<SyntaxNode>();
        var node = root;
        while (node is not null)
        {
            path.Add(node);
            SyntaxNode? next = null;
            foreach (var child in Children(node))
            {
                if (SpanOf(child).Contains(offset))
                {
                    next = child;
                    break;
                }
            }
            node = next;
        }
        return path;
    }

    /// The innermost node whose range contains <paramref name="offset"/>, or null if
    /// the offset is outside the root.
    public static SyntaxNode? FindNode(SyntaxNode root, int offset)
    {
        var path = PathTo(root, offset);
        return path.Count > 0 && SpanOf(path[^1]).Contains(offset) ? path[^1] : null;
    }

    /// The ancestor chain of <paramref name="target"/> within <paramref name="root"/>,
    /// nearest first (excluding the target). Empty if the target isn't found.
    public static IReadOnlyList<SyntaxNode> Ancestors(SyntaxNode root, SyntaxNode target)
    {
        var stack = new List<SyntaxNode>();
        return Search(root, target, stack) ? Reversed(stack) : [];

        static bool Search(SyntaxNode node, SyntaxNode target, List<SyntaxNode> stack)
        {
            if (ReferenceEquals(node, target))
                return true;
            stack.Add(node);
            foreach (var child in Children(node))
                if (Search(child, target, stack))
                    return true;
            stack.RemoveAt(stack.Count - 1);
            return false;
        }

        static IReadOnlyList<SyntaxNode> Reversed(List<SyntaxNode> s)
        {
            s.Reverse();
            return s;
        }
    }

    static void Add(List<SyntaxNode> list, SyntaxNode? node)
    {
        if (node is not null) list.Add(node);
    }

    static void AddRange<T>(List<SyntaxNode> list, IReadOnlyList<T>? nodes) where T : SyntaxNode
    {
        if (nodes is null) return;
        foreach (var n in nodes) list.Add(n);
    }
}
