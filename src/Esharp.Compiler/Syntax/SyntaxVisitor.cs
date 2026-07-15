namespace Esharp.Syntax;

/// Visit one node. Override `Visit` to dispatch on node type (a `switch`), or
/// subclass `SyntaxWalker` to recurse automatically. Kept deliberately small — the
/// records are pure data and traversal is centralized in `SyntaxNavigator`, so a
/// generated double-dispatch hierarchy would be ceremony without payoff.
public abstract class SyntaxVisitor
{
    public abstract void Visit(SyntaxNode node);
}

/// A visitor that walks the whole tree depth-first. Override `DefaultVisit` to run
/// logic at every node (the fuzzer's AST-collection, a span audit, a future
/// rewriter's read pass); the base recurses through `SyntaxNavigator.Children`.
public abstract class SyntaxWalker : SyntaxVisitor
{
    public override void Visit(SyntaxNode node)
    {
        DefaultVisit(node);
        foreach (var child in SyntaxNavigator.Children(node))
            Visit(child);
    }

    protected virtual void DefaultVisit(SyntaxNode node) { }
}
