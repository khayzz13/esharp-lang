using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// The type-annotation grammar: recursive descent producing a structured
/// `TypeSyntax` tree (generic args, tuples, function pointers, pointers, nullable),
/// so no phase downstream re-parses a type from a name string.
sealed class TypeParser : ParserUnit
{
    public TypeParser(Parser parser) : base(parser) { }

    // The whole-annotation entry point used by every declaration / statement /
    // expression site: recursive descent producing a structured `TypeSyntax` tree.
    public TypeSyntax ParseTypeUntil(params SyntaxTokenKind[] terminators) =>
        ParseType(terminators.ToHashSet());

    public TypeSyntax ParseType(HashSet<SyntaxTokenKind> terminators)
    {
        P.EnterRecursion();
        try { return ParseTypeCore(terminators); }
        finally { P.ExitRecursion(); }
    }

    TypeSyntax ParseTypeCore(HashSet<SyntaxTokenKind> terminators)
    {
        var start = SpanHere();

        // readonly *T  (read-only by-ref / `in T`)
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "readonly" && Peek(1).Kind == SyntaxTokenKind.Star)
        {
            NextToken(); // readonly
            NextToken(); // *
            return new PointerTypeSyntax(ParseType(terminators), ReadOnly: true) { Span = SpanFrom(start) };
        }

        // *T  (mutable pointer / by-ref). Pointers compose with a trailing `?` via the
        // recursive call: `*T?` parses as `*(T?)`.
        if (Current.Kind == SyntaxTokenKind.Star)
        {
            NextToken();
            return new PointerTypeSyntax(ParseType(terminators), ReadOnly: false) { Span = SpanFrom(start) };
        }

        // &( ... ) — function pointer
        if (Current.Kind == SyntaxTokenKind.Ampersand && Peek(1).Kind == SyntaxTokenKind.OpenParen)
            return ParseFunctionPointerType(terminators, start);

        // ( ... ) — tuple
        if (Current.Kind == SyntaxTokenKind.OpenParen)
            return ParseTupleType(terminators, start);

        // var — inferred type sentinel (an explicit `: var` annotation; the common
        // sites synthesize InferredTypeSyntax directly rather than parsing it).
        if (Current.Kind == SyntaxTokenKind.VarKeyword)
        {
            NextToken();
            return InferredTypeSyntax.Instance with { Span = SpanFrom(start) };
        }

        // chan<T> — the `chan` keyword as a generic base name
        if (Current.Kind == SyntaxTokenKind.ChanKeyword)
        {
            NextToken(); // chan
            Match(SyntaxTokenKind.Less, "Expected '<' after 'chan'.");
            var elem = ParseType(new HashSet<SyntaxTokenKind> { SyntaxTokenKind.Greater });
            Cursor.ConsumeTypeGreater("Expected '>' after chan element type.");
            return MaybeNullable(new GenericTypeSyntax("chan", [elem]), terminators, start);
        }

        // Bare name (optionally dotted) with an optional generic argument list.
        if (Current.Kind == SyntaxTokenKind.Identifier)
        {
            var name = NextToken().Text;
            while (Current.Kind == SyntaxTokenKind.Dot && Peek(1).Kind == SyntaxTokenKind.Identifier)
            {
                NextToken(); // .
                name += "." + NextToken().Text;
            }
            TypeSyntax node = Current.Kind == SyntaxTokenKind.Less
                ? new GenericTypeSyntax(name, ParseTypeArguments())
                : new NamedTypeSyntax(name);
            return MaybeNullable(node, terminators, start);
        }

        Report(Current.Line, Current.Column, "Expected type name.");
        // Forward progress: a garbage token that is not this position's terminator
        // must be consumed, or the list-position loops (type args, tuple elements,
        // fn-pointer params) spin forever on it.
        if (!terminators.Contains(Current.Kind) && Current.Kind != SyntaxTokenKind.EndOfFile)
            NextToken();
        return new NamedTypeSyntax("object") { Span = SpanFrom(start) };
    }

    /// Parse a `<T, U, ...>` type-argument list. Public so the expression parser's
    /// speculative generic-construction / generic-method-args lookaheads can hand a
    /// confirmed angle group to the real grammar instead of re-deriving structure.
    public List<TypeSyntax> ParseTypeArguments()
    {
        Match(SyntaxTokenKind.Less);
        var args = new List<TypeSyntax>();
        var argTerminators = new HashSet<SyntaxTokenKind> { SyntaxTokenKind.Comma, SyntaxTokenKind.Greater };
        while (!IsTypeGreater(Current.Kind) && Current.Kind != SyntaxTokenKind.EndOfFile)
        {
            args.Add(ParseType(argTerminators));
            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
        }
        Cursor.ConsumeTypeGreater("Expected '>' to close type argument list.");
        return args;
    }

    static bool IsTypeGreater(SyntaxTokenKind kind) => kind is SyntaxTokenKind.Greater
        or SyntaxTokenKind.ShiftRight or SyntaxTokenKind.UnsignedShiftRight;

    TypeSyntax ParseFunctionPointerType(HashSet<SyntaxTokenKind> terminators, SourceSpan start)
    {
        NextToken(); // &
        Match(SyntaxTokenKind.OpenParen, "Expected '(' in function pointer type.");

        var paramTypes = new List<TypeSyntax>();
        TypeSyntax returnType = new NamedTypeSyntax("void");

        if (FunctionPointerHasArrow())
        {
            var paramTerminators = new HashSet<SyntaxTokenKind> { SyntaxTokenKind.Comma, SyntaxTokenKind.Arrow, SyntaxTokenKind.CloseParen };
            while (Current.Kind is not (SyntaxTokenKind.Arrow or SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                paramTypes.Add(ParseType(paramTerminators));
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Arrow, "Expected '->' in function pointer type.");
            returnType = ParseType(new HashSet<SyntaxTokenKind> { SyntaxTokenKind.CloseParen });
        }
        else
        {
            // No arrow → void return: &(int, int)
            var paramTerminators = new HashSet<SyntaxTokenKind> { SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen };
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                paramTypes.Add(ParseType(paramTerminators));
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
        }

        Match(SyntaxTokenKind.CloseParen, "Expected ')' to close function pointer type.");
        return MaybeNullable(new FunctionPointerTypeSyntax(paramTypes, returnType), terminators, start);
    }

    // From the token after `(`, report whether a top-level `->` precedes the matching
    // `)` — i.e. the function pointer has an explicit return type rather than the
    // void-return `&(int, int)` form. Nested `(...)` / `<...>` are skipped by depth.
    bool FunctionPointerHasArrow()
    {
        var depth = 0;
        for (var i = 0; ; i++)
        {
            switch (Peek(i).Kind)
            {
                case SyntaxTokenKind.EndOfFile: return false;
                case SyntaxTokenKind.OpenParen:
                case SyntaxTokenKind.Less:
                    depth++; break;
                case SyntaxTokenKind.CloseParen:
                    if (depth == 0) return false;
                    depth--; break;
                case SyntaxTokenKind.Greater:
                    if (depth > 0) depth--; break;
                case SyntaxTokenKind.ShiftRight:
                    depth = Math.Max(0, depth - 2); break;
                case SyntaxTokenKind.UnsignedShiftRight:
                    depth = Math.Max(0, depth - 3); break;
                case SyntaxTokenKind.Arrow:
                    if (depth == 0) return true; break;
            }
        }
    }

    TypeSyntax ParseTupleType(HashSet<SyntaxTokenKind> terminators, SourceSpan start)
    {
        Match(SyntaxTokenKind.OpenParen);
        var elements = new List<TypeSyntax>();
        var elementNames = new List<string?>();
        var elementTerminators = new HashSet<SyntaxTokenKind> { SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen };
        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            // Optional per-element label: `(name: T, other: U)`. A bare `identifier :`
            // pair before the element type names it; an unlabeled element keeps a null.
            string? label = null;
            if (Current.Kind == SyntaxTokenKind.Identifier && Peek(1).Kind == SyntaxTokenKind.Colon)
            {
                label = Current.Text;
                NextToken();  // identifier
                NextToken();  // colon
            }
            elementNames.Add(label);
            elements.Add(ParseType(elementTerminators));
            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
        }
        Match(SyntaxTokenKind.CloseParen, "Expected ')' to close tuple type.");
        var names = elementNames.Any(n => n is not null) ? elementNames : null;
        return MaybeNullable(new TupleTypeSyntax(elements, names), terminators, start);
    }

    // A trailing `?` makes the just-parsed type nullable, unless `?` is a terminator
    // for this position. Stamps both the wrapper and the inner node with the span.
    TypeSyntax MaybeNullable(TypeSyntax node, HashSet<SyntaxTokenKind> terminators, SourceSpan start)
    {
        node = node with { Span = SpanFrom(start) };
        // Array suffix(es): a trailing `[]` after a type is `T[]` (and `T[][]` nests).
        // In type position a `[` always begins an array suffix — only the empty `[]`
        // pair is an array; anything else is a malformed type, left to the caller.
        while (Current.Kind == SyntaxTokenKind.OpenBracket && Peek(1).Kind == SyntaxTokenKind.CloseBracket)
        {
            NextToken(); // [
            NextToken(); // ]
            node = new ArrayTypeSyntax(node) { Span = SpanFrom(start) };
        }
        if (Current.Kind == SyntaxTokenKind.Question && !terminators.Contains(SyntaxTokenKind.Question))
        {
            NextToken();
            return new NullableTypeSyntax(node) { Span = SpanFrom(start) };
        }
        return node;
    }
}
