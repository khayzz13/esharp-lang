using System.Text;
using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// Member-level grammar: the `ParseMember` dispatch and every top-level
/// declaration form (data, choice, enum, interface, static func, const,
/// delegate, function). Attributes and `#derive` are parsed up front in
/// `ParseMemberCore` and handed to the declaration that owns them as arguments —
/// there is no parser-wide "pending attributes" state.
sealed class DeclarationParser : ParserUnit
{
    public DeclarationParser(Parser parser) : base(parser) { }

    /// The visibility parsed for the declaration currently being dispatched. Set by
    /// `ParseMemberCore` / `ParseNestedType` before the Parse*Declaration call; each
    /// Parse*Declaration captures it into a local at entry (before parsing its body,
    /// which may re-enter for a nested type) and stamps it on the produced syntax.
    Visibility _declVisibility = Visibility.Internal;

    /// Consume an optional `pub` / `internal` / `priv` modifier, returning the declared
    /// visibility (or <paramref name="dflt"/> when none is written). `internal` is a
    /// contextual keyword — recognized only here, an ordinary identifier elsewhere.
    Visibility ParseVisibilityDefault(Visibility dflt)
    {
        if (Current.Kind == SyntaxTokenKind.PubKeyword) { NextToken(); SkipSeparators(); return Visibility.Public; }
        if (Current.Kind == SyntaxTokenKind.PrivKeyword) { NextToken(); SkipSeparators(); return Visibility.Private; }
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "internal") { NextToken(); SkipSeparators(); return Visibility.Internal; }
        return dflt;
    }

    /// Visibility for a top-level declaration — default Internal (E#'s "internal unless
    /// `pub`" rule). A nested declaration defaults to Private instead.
    Visibility ParseVisibility() => ParseVisibilityDefault(Visibility.Internal);

    public MemberSyntax ParseMember()
    {
        // Every member declaration carries a range from its first token to its last.
        // Stamped centrally here (the single member choke point) so the symbol spine's
        // declarations — and through them the semantic sink / future LSP — always have
        // a real location; a core parse that stamped its own tighter span (the error
        // member) is honored.
        var declSpan = SpanHere();
        var member = ParseMemberCore();
        return member is { Span.IsValid: false } ? member with { Span = SpanFrom(declSpan) } : member;
    }

    /// Parse a `[Attribute(args)]` sequence — owned by the declaration that
    /// follows. Used at member scope (top-level declarations) and inside a
    /// `class` body (method-level attributes, e.g. xUnit's `[Fact]`).
    List<AttributeSyntax> ParseAttributeList()
    {
        var attributes = new List<AttributeSyntax>();
        while (Current.Kind == SyntaxTokenKind.OpenBracket)
        {
            NextToken(); // consume [
            var attrName = new StringBuilder();
            while (Current.Kind is not (SyntaxTokenKind.CloseBracket or SyntaxTokenKind.OpenParen or SyntaxTokenKind.EndOfFile))
                attrName.Append(NextToken().Text);

            string? args = null;
            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                NextToken(); // consume (
                var argBuilder = new StringBuilder();
                var depth = 1;
                while (depth > 0 && Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    if (Current.Kind == SyntaxTokenKind.OpenParen) depth++;
                    else if (Current.Kind == SyntaxTokenKind.CloseParen) { depth--; if (depth == 0) break; }
                    argBuilder.Append(NextToken().Text);
                }
                if (Current.Kind == SyntaxTokenKind.CloseParen) NextToken(); // consume )
                args = argBuilder.ToString().Trim();
            }

            Match(SyntaxTokenKind.CloseBracket, "Expected ']' to close attribute.");
            attributes.Add(new AttributeSyntax(attrName.ToString().Trim(), args));
            SkipSeparators();
        }
        return attributes;
    }

    /// A `[Attr]` list parsed in a data body where the following member cannot
    /// carry it — a located error rather than a silent drop.
    void ReportUnattachableAttributes(List<AttributeSyntax>? attributes, string memberForm)
    {
        if (attributes is not { Count: > 0 }) return;
        Report(Current.Line, Current.Column,
            $"ES1014: attributes are not supported on {memberForm} — attach them to a method, or move the member out of the attribute's way.");
    }

    MemberSyntax ParseMemberCore()
    {
        var attributes = ParseAttributeList();

        // Parse #derive directive if present — owned by the data declaration that follows.
        DeriveDirectiveSyntax? derive = null;
        if (Current.Kind == SyntaxTokenKind.DeriveKeyword)
        {
            NextToken();
            var traits = new List<string>();
            while (Current.Kind == SyntaxTokenKind.Identifier)
            {
                traits.Add(NextToken().Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            derive = new DeriveDirectiveSyntax(traits);
            SkipSeparators();
        }

        // Visibility modifier: pub / internal / priv. Recorded in `_declVisibility`
        // (captured per type at its construction site, re-entrancy-safe for nesting);
        // `isPublic` stays the boolean face the member forms below read.
        var hadVisibilityModifier = Current.Kind is SyntaxTokenKind.PubKeyword or SyntaxTokenKind.PrivKeyword
            || (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "internal");
        _declVisibility = ParseVisibility();
        var isPublic = _declVisibility == Visibility.Public;

        // Namespace `init { ... }` is contextual. Class constructors are parsed
        // only by the type-body parser, so the brace form is unambiguous here.
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "init"
            && Peek(1).Kind == SyntaxTokenKind.OpenBrace)
        {
            if (attributes.Count > 0 || hadVisibilityModifier)
                Report(Current.Line, Current.Column, "ES2205: namespace 'init' cannot carry attributes or visibility modifiers.");
            NextToken();
            return new NamespaceInitDeclarationSyntax(Statements.ParseBlockStatement());
        }

        // readonly contextual keyword: `readonly struct` (value type) or
        // `readonly func (c: T) m()` (read-only-receiver method — in this, no copy).
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "readonly")
        {
            if (Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            {
                NextToken(); // consume `readonly`
                return ParseFunctionDeclaration(isPublic, attributes: attributes, readonlyReceiver: true);
            }
            return ParseDataDeclaration(isPublic, ClassModifier.Sealed, attributes, derive);
        }

        // Class modifiers: abstract / open precede `class`. A class is otherwise
        // sealed — that is the default, so there is no `sealed` modifier to write.
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text is "abstract" or "open"
            && Peek(1).Kind == SyntaxTokenKind.ClassKeyword)
        {
            var modifier = Current.Text == "abstract" ? ClassModifier.Abstract : ClassModifier.Open;
            NextToken(); // consume modifier
            return ParseDataDeclaration(isPublic, modifier, attributes, derive);
        }

        // ref keyword: only `ref union` remains — the reference-class kind is `class`.
        if (Current.Kind == SyntaxTokenKind.RefKeyword)
        {
            if (Peek(1).Kind == SyntaxTokenKind.UnionKeyword)
                return ParseChoiceDeclaration(isPublic);
            Report(Current.Line, Current.Column, "Expected 'union' after 'ref'.");
            return ReportUnexpectedMember();
        }

        // `static Foo { ... }` declares an explicit static facet. `static func`
        // is intentionally no longer syntax, but we still parse its body after a
        // focused diagnostic so a migration mistake does not cascade.
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "static")
            return ParseStaticFuncDeclaration(isPublic);

        // task func name(...) ... — function-shaped spawn
        if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "task") && Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            return ParseFunctionDeclaration(isPublic, isTaskFunc: true, attributes: attributes);

        // delegate func Name(...) -> R — nominal delegate type. `delegate` is a
        // contextual keyword (only here, before `func`); it stays a valid identifier
        // elsewhere.
        if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "delegate") && Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            return ParseDelegateDeclaration(isPublic);

        if (Current.Kind is SyntaxTokenKind.LetKeyword or SyntaxTokenKind.VarKeyword)
            return ParseNamespaceStateDeclaration(isPublic);

        // A bare typed declaration on the namespace host is a real mutable
        // static field, matching a bare typed declaration in a class body.
        // `let`/`var` remain the property spellings.
        if (Current.Kind == SyntaxTokenKind.Identifier
            && Peek(1).Kind == SyntaxTokenKind.Colon)
            return ParseNamespaceFieldDeclaration(isPublic);

        return Current.Kind switch
        {
            SyntaxTokenKind.StructKeyword => ParseDataDeclaration(isPublic, ClassModifier.Sealed, attributes, derive),
            SyntaxTokenKind.ClassKeyword => ParseDataDeclaration(isPublic, ClassModifier.Sealed, attributes, derive),
            SyntaxTokenKind.UnionKeyword => ParseChoiceDeclaration(isPublic),
            SyntaxTokenKind.FuncKeyword => ParseFunctionDeclaration(isPublic, attributes: attributes),
            SyntaxTokenKind.EnumKeyword => ParseEnumDeclaration(isPublic),
            SyntaxTokenKind.InterfaceKeyword => ParseInterfaceDeclaration(isPublic),
            SyntaxTokenKind.ConstKeyword => ParseConstDeclaration(isPublic),
            _ => ReportUnexpectedMember(),
        };
    }

    NamespaceStateDeclarationSyntax ParseNamespaceStateDeclaration(bool isPublic)
    {
        var mutable = Current.Kind == SyntaxTokenKind.VarKeyword;
        NextToken();
        var name = Match(SyntaxTokenKind.Identifier, "Expected namespace state name.");
        TypeSyntax type = InferredTypeSyntax.Instance;
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            type = Types.ParseTypeUntil(SyntaxTokenKind.Equals, SyntaxTokenKind.FatArrow,
                SyntaxTokenKind.OpenBrace, SyntaxTokenKind.NewLine);
        }

        ExpressionSyntax? initializer = null;
        // Namespace let/var is always a property.  The simple `= value` form is
        // merely its stored form; it is not a static field spelling.
        PropertyAccessorsSyntax? property = new PropertyAccessorsSyntax(
            HasGet: true, HasSet: mutable, HasInit: !mutable);
        if (Current.Kind == SyntaxTokenKind.Equals)
        {
            NextToken();
            initializer = Expressions.ParseExpression();
        }
        else if (Current.Kind == SyntaxTokenKind.FatArrow)
        {
            if (mutable)
                Report(Current.Line, Current.Column, "A computed namespace property uses 'let', not 'var'.");
            if (type is InferredTypeSyntax)
                Report(Current.Line, Current.Column, "A computed namespace property requires an explicit result type.");
            NextToken();
            property = new PropertyAccessorsSyntax(true, false, false,
                ComputedGetter: Expressions.ParseExpression());
        }
        else if (Current.Kind == SyntaxTokenKind.OpenBrace)
        {
            if (type is InferredTypeSyntax)
                Report(Current.Line, Current.Column, "A stored namespace property requires an explicit type.");
            property = ParsePropertyAccessorBlock(mutable, required: false);
        }
        else
        {
            // `let name: T` / `var name: T` is a declaration of a stored
            // namespace property.  Its default CLR value is valid when no
            // initializer is supplied.
        }

        return new NamespaceStateDeclarationSyntax(isPublic, mutable, name.Text, type, initializer, property)
        { NameSpan = SpanOf(name), Visibility = _declVisibility };
    }

    NamespaceStateDeclarationSyntax ParseNamespaceFieldDeclaration(bool isPublic)
    {
        var name = Match(SyntaxTokenKind.Identifier, "Expected namespace field name.");
        Match(SyntaxTokenKind.Colon, "Expected ':' after namespace field name.");
        var type = Types.ParseTypeUntil(SyntaxTokenKind.Equals, SyntaxTokenKind.NewLine);
        ExpressionSyntax? initializer = null;
        if (Current.Kind == SyntaxTokenKind.Equals)
        {
            NextToken();
            initializer = Expressions.ParseExpression();
        }

        return new NamespaceStateDeclarationSyntax(
            isPublic, Mutable: true, name.Text, type, initializer, Property: null)
        { NameSpan = SpanOf(name), Visibility = _declVisibility };
    }

    /// True when the current token begins a NESTED type declaration inside a type
    /// or static-func body — a leading `derive`, or (past an optional `pub`/`priv`)
    /// a type keyword, `ref union`, `static func`, `delegate func`, `abstract`/`open
    /// class`, or `readonly struct`. Pure lookahead, consumes nothing. Distinguishes
    /// a nested type from a field/method member: contextual identifiers (`static`,
    /// `readonly`, …) only read as a type start when the type keyword follows, so a
    /// field named `static` (`static: int`) is unaffected.
    bool IsNestedTypeStart()
    {
        if (Current.Kind == SyntaxTokenKind.DeriveKeyword) return true;
        // Skip an optional visibility modifier — `pub` / `priv` keywords or the
        // contextual `internal` identifier — before the type keyword.
        var look = Current.Kind is SyntaxTokenKind.PubKeyword or SyntaxTokenKind.PrivKeyword
                   || (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "internal")
                   ? 1 : 0;
        var k = Peek(look).Kind;
        if (k is SyntaxTokenKind.StructKeyword or SyntaxTokenKind.ClassKeyword
              or SyntaxTokenKind.EnumKeyword or SyntaxTokenKind.UnionKeyword
              or SyntaxTokenKind.InterfaceKeyword)
            return true;
        if (k == SyntaxTokenKind.RefKeyword && Peek(look + 1).Kind == SyntaxTokenKind.UnionKeyword)
            return true;
        if (k == SyntaxTokenKind.Identifier)
        {
            var txt = Peek(look).Text;
            if (txt == "static" && (Peek(look + 1).Kind == SyntaxTokenKind.Identifier || Peek(look + 1).Kind == SyntaxTokenKind.FuncKeyword)) return true;
            if (txt == "delegate" && Peek(look + 1).Kind == SyntaxTokenKind.FuncKeyword) return true;
            if ((txt is "abstract" or "open") && Peek(look + 1).Kind == SyntaxTokenKind.ClassKeyword) return true;
            if (txt == "readonly" && Peek(look + 1).Kind == SyntaxTokenKind.StructKeyword) return true;
        }
        return false;
    }

    /// Parse a nested type declaration (attributes already consumed by the caller,
    /// passed through to the data form that accepts them). Mirrors the dispatch tail
    /// of `ParseMemberCore`, restricted to type-producing declarations. The result is
    /// an ordinary declaration syntax; the binder registers it with declaring-type
    /// context so its CLR identity nests under the enclosing type.
    MemberSyntax ParseNestedType(List<AttributeSyntax> attributes)
    {
        DeriveDirectiveSyntax? derive = null;
        if (Current.Kind == SyntaxTokenKind.DeriveKeyword)
        {
            NextToken();
            var traits = new List<string>();
            while (Current.Kind == SyntaxTokenKind.Identifier)
            {
                traits.Add(NextToken().Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            derive = new DeriveDirectiveSyntax(traits);
            SkipSeparators();
        }

        // Nested visibility — pub / internal / priv; default is Private (the C# nested
        // default), distinct from a top-level type's Internal default.
        _declVisibility = ParseVisibilityDefault(Visibility.Private);
        var isPublic = _declVisibility == Visibility.Public;

        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "readonly"
            && Peek(1).Kind == SyntaxTokenKind.StructKeyword)
            return ParseDataDeclaration(isPublic, ClassModifier.Sealed, attributes, derive);

        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text is "abstract" or "open"
            && Peek(1).Kind == SyntaxTokenKind.ClassKeyword)
        {
            var modifier = Current.Text == "abstract" ? ClassModifier.Abstract : ClassModifier.Open;
            NextToken();
            return ParseDataDeclaration(isPublic, modifier, attributes, derive);
        }

        if (Current.Kind == SyntaxTokenKind.RefKeyword && Peek(1).Kind == SyntaxTokenKind.UnionKeyword)
            return ParseChoiceDeclaration(isPublic);

        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "static")
            return ParseStaticFuncDeclaration(isPublic);

        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "delegate" && Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            return ParseDelegateDeclaration(isPublic);

        return Current.Kind switch
        {
            SyntaxTokenKind.StructKeyword => ParseDataDeclaration(isPublic, ClassModifier.Sealed, attributes, derive),
            SyntaxTokenKind.ClassKeyword => ParseDataDeclaration(isPublic, ClassModifier.Sealed, attributes, derive),
            SyntaxTokenKind.UnionKeyword => ParseChoiceDeclaration(isPublic),
            SyntaxTokenKind.EnumKeyword => ParseEnumDeclaration(isPublic),
            SyntaxTokenKind.InterfaceKeyword => ParseInterfaceDeclaration(isPublic),
            _ => ReportUnexpectedMember(),
        };
    }

    ConstDeclarationSyntax ParseConstDeclaration(bool isPublic)
    {
        Match(SyntaxTokenKind.ConstKeyword);
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected const name.");
        TypeSyntax? type = null;
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            type = Types.ParseTypeUntil(SyntaxTokenKind.Equals);
        }
        Match(SyntaxTokenKind.Equals, "Expected '=' in const declaration.");
        var value = Expressions.ParseExpression();
        return new ConstDeclarationSyntax(isPublic, nameTok.Text, type, value) { NameSpan = SpanOf(nameTok) };
    }

    StaticFuncDeclarationSyntax ParseStaticFuncDeclaration(bool isPublic)
    {
        // Capture before the body parse (which may re-enter for a nested type and
        // overwrite the shared field).
        var declVisibility = _declVisibility;
        // Consume contextual 'static' identifier.
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "static") NextToken();
        if (Current.Kind == SyntaxTokenKind.FuncKeyword)
        {
            Report(Current.Line, Current.Column,
                "ES2210: static declarations are written `static Foo { ... }`; remove the `func` keyword.");
            NextToken();
        }
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected static name.");
        var name = nameTok.Text;
        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' to open static body.");
        SkipSeparators();

        var fields = new List<FieldSyntax>();
        var functions = new List<FunctionDeclarationSyntax>();
        var nestedTypes = new List<MemberSyntax>();
        ReturnsClauseSyntax? returns = null;

        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPos = Current.Position;

            // Nested type declaration — `static Host { enum Kind { ... } struct Arm { ... } }`.
            // Checked before the field/func forms; handles its own visibility/derive.
            if (IsNestedTypeStart())
            {
                nestedTypes.Add(ParseNestedType([]));
                SkipSeparators();
                continue;
            }

            // `returns Type` clause
            if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "returns"))
            {
                NextToken();
                var t = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.CloseBrace);
                returns = new ReturnsClauseSyntax(t);
                SkipSeparators();
                continue;
            }

            // Visibility / mutability prefixes for fields
            bool fieldPublic = isPublic;
            if (Current.Kind == SyntaxTokenKind.PubKeyword) { fieldPublic = true; NextToken(); }
            else if (Current.Kind == SyntaxTokenKind.PrivKeyword) { fieldPublic = false; NextToken(); }

            if (Current.Kind == SyntaxTokenKind.FuncKeyword)
            {
                functions.Add(ParseFunctionDeclaration(fieldPublic));
                SkipSeparators();
                continue;
            }

            // task func inside static func
            if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "task") && Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            {
                functions.Add(ParseFunctionDeclaration(fieldPublic, isTaskFunc: true));
                SkipSeparators();
                continue;
            }

            if (Current.Kind is SyntaxTokenKind.LetKeyword or SyntaxTokenKind.VarKeyword)
            {
                var mutable = Current.Kind == SyntaxTokenKind.VarKeyword;
                NextToken();
                var fieldNameTok = Match(SyntaxTokenKind.Identifier, "Expected field name.");
                // The type annotation is optional per the grammar
                // (`"let" identifier [ ":" Type ] "=" Expr`): when omitted, the
                // field type is inferred from the initializer, exactly like the
                // `const` form below.
                TypeSyntax fieldType;
                if (Current.Kind == SyntaxTokenKind.Colon)
                {
                    NextToken();
                    fieldType = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.Equals, SyntaxTokenKind.CloseBrace);
                }
                else
                {
                    fieldType = InferredTypeSyntax.Instance;
                }
                ExpressionSyntax? defaultValue = null;
                if (Current.Kind == SyntaxTokenKind.Equals)
                {
                    NextToken();
                    defaultValue = Expressions.ParseExpression();
                }
                else if (fieldType is InferredTypeSyntax)
                {
                    // No annotation and no initializer — nothing to infer from.
                    Report(fieldNameTok.Line, fieldNameTok.Column,
                        $"Field '{fieldNameTok.Text}' needs a type annotation or an initializer to infer its type from.");
                }
                fields.Add(new FieldSyntax(fieldNameTok.Text, fieldType, fieldPublic, mutable, defaultValue) { NameSpan = SpanOf(fieldNameTok) });
                SkipSeparators();
                continue;
            }

            // `const NAME[: Type] = literal` inside static body. Treated
            // like `let NAME = literal` at this position — the existing static
            // func emitter folds constants into CLR `literal` fields. Optional
            // type annotation; otherwise type is inferred from the initializer.
            if (Current.Kind == SyntaxTokenKind.ConstKeyword)
            {
                NextToken();
                var constNameTok = Match(SyntaxTokenKind.Identifier, "Expected const name.");
                TypeSyntax? constType = null;
                if (Current.Kind == SyntaxTokenKind.Colon)
                {
                    NextToken();
                    constType = Types.ParseTypeUntil(SyntaxTokenKind.Equals);
                }
                Match(SyntaxTokenKind.Equals, "Expected '=' in const declaration.");
                var constValue = Expressions.ParseExpression();
                fields.Add(new FieldSyntax(constNameTok.Text, constType ?? InferredTypeSyntax.Instance, fieldPublic, Mutable: false, constValue) { NameSpan = SpanOf(constNameTok) });
                SkipSeparators();
                continue;
            }

            Report(Current.Line, Current.Column,
                $"ES1010: top-level statements are not allowed in a 'static' facet body. Use 'let' / 'var' for constants or 'func' for methods.");
            // Skip to next separator
            while (Current.Kind is not (SyntaxTokenKind.NewLine or SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
                NextToken();
            SkipSeparators();
            if (Current.Position == startPos && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close static body.");
        return new StaticFuncDeclarationSyntax(isPublic, name, fields, functions, returns,
            NestedTypes: nestedTypes.Count > 0 ? nestedTypes : null,
            TypeParameters: []) { NameSpan = SpanOf(nameTok), Visibility = declVisibility };
    }

    MemberSyntax ReportUnexpectedMember()
    {
        var span = SpanHere();
        Report(Current.Line, Current.Column, $"Unexpected member starting with '{Current.Text}'.");
        NextToken(); // consume the unexpected token to guarantee forward progress
        return new ErrorMemberSyntax { Span = SpanFrom(span) };
    }

    DataDeclarationSyntax ParseDataDeclaration(bool isPublic, ClassModifier modifier, List<AttributeSyntax> attributes, DeriveDirectiveSyntax? derive)
    {
        // Capture before the body parse, which may re-enter for a nested type and
        // overwrite the shared visibility field.
        var declVisibility = _declVisibility;
        var isReadonly = Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "readonly";
        if (isReadonly) NextToken();
        var isRef = Current.Kind == SyntaxTokenKind.ClassKeyword;
        if (isRef)
        {
            if (isReadonly)
                Report(Current.Line, Current.Column,
                    "'readonly' applies to 'data' only — a class has identity and mutable reference semantics.");
            NextToken(); // consume 'class'
        }
        else
        {
            Match(SyntaxTokenKind.StructKeyword);
        }
        var nameTok = Match(SyntaxTokenKind.Identifier, isRef ? "Expected class name." : "Expected data type name.");
        var name = nameTok.Text;

        // Optional type parameters: data Pair<A, B> { ... }
        var typeParameters = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Less)
        {
            NextToken();
            while (Current.Kind is not (SyntaxTokenKind.Greater or SyntaxTokenKind.EndOfFile))
            {
                typeParameters.Add(Match(SyntaxTokenKind.Identifier, "Expected type parameter name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Greater, "Expected '>' to close type parameter list.");
        }

        // Optional interface list: data Foo : IBar, IBaz { ... }
        var interfaces = new List<TypeSyntax>();
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            while (Current.Kind != SyntaxTokenKind.OpenBrace && Current.Kind != SyntaxTokenKind.NewLine && Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                // Parse the full (possibly generic) base/interface type so `: IEnumerable<T>, IDisposable`
                // and `Base<K, V>` carry their type arguments downstream as structure.
                interfaces.Add(Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.OpenBrace));
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
        }

        SkipSeparators();

        // Positional form. For `data` this synthesizes public fields + an init;
        // for `class` it is the primary-constructor capture header — the params are
        // not fields, and the declaration may continue with `: Base, IFoo` and a body.
        List<ParameterSyntax>? headerParams = null;
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken(); // consume (
            var positionalFields = new List<FieldSyntax>();
            var initParams = new List<ParameterSyntax>();
            SkipSeparators();
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                var startPosition = Current.Position;
                var pNameTok = Match(SyntaxTokenKind.Identifier, "Expected parameter name.");
                var pName = pNameTok.Text;
                Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
                var pType = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen, SyntaxTokenKind.Equals);
                ExpressionSyntax? pDefault = null;
                if (Current.Kind == SyntaxTokenKind.Equals)
                {
                    NextToken();
                    pDefault = Expressions.ParseExpression();
                }
                positionalFields.Add(new FieldSyntax(pName, pType, null, !isReadonly) { NameSpan = SpanOf(pNameTok) });
                initParams.Add(new ParameterSyntax(pName, pType, DefaultValue: pDefault) { NameSpan = SpanOf(pNameTok) });
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                SkipSeparators();

                if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                    NextToken();
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' to close positional parameters.");

            if (!isRef)
            {
                // Synthesize init body: self.field = param for each field
                var initStatements = new List<StatementSyntax>();
                foreach (var p in initParams)
                    initStatements.Add(new AssignmentStatementSyntax(
                        new MemberAccessExpressionSyntax(new NameExpressionSyntax("self"), p.Name),
                        new NameExpressionSyntax(p.Name)));
                var initBody = new BlockStatementSyntax(initStatements);
                var posInitDecl = new InitDeclarationSyntax(initParams, initBody);

                return new DataDeclarationSyntax(isRef, isPublic, isReadonly, name, typeParameters, interfaces, positionalFields, attributes, new[] { posInitDecl }, null, null, modifier, derive?.Traits, IsPositional: true) { NameSpan = SpanOf(nameTok), Visibility = declVisibility };
            }

            headerParams = initParams;

            // The natural spelling puts the base/interface list AFTER the header:
            // `class Foo(a: T) : Base, IBar { ... }`.
            if (interfaces.Count == 0 && Current.Kind == SyntaxTokenKind.Colon)
            {
                NextToken();
                while (Current.Kind != SyntaxTokenKind.OpenBrace && Current.Kind != SyntaxTokenKind.NewLine && Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    interfaces.Add(Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.OpenBrace));
                    if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                }
            }

            SkipSeparators();
            // A headered class may omit the body entirely.
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
                return new DataDeclarationSyntax(isRef, isPublic, isReadonly, name, typeParameters, interfaces, [], attributes, null, null, null, modifier, derive?.Traits, headerParams) { NameSpan = SpanOf(nameTok), Visibility = declVisibility };
        }

        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after data declaration.");
        SkipSeparators();

        var fields = new List<FieldSyntax>();
        var inlineMethods = new List<FunctionDeclarationSyntax>();
        var initDecls = new List<InitDeclarationSyntax>();
        var nestedTypes = new List<MemberSyntax>();
        ReturnsClauseSyntax? defaultReturns = null;
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;

            // Method-level attributes (`[Fact]`, `[Obsolete("...")]`) — parsed up
            // front and attached to the method form that follows. Attributes on
            // non-method members of a data body are not a supported form yet.
            List<AttributeSyntax>? memberAttributes = null;
            if (Current.Kind == SyntaxTokenKind.OpenBracket)
                memberAttributes = ParseAttributeList();

            // Nested type declaration — `class Outer { struct Inner { ... } }`. Checked
            // before the member forms below: a nested type's keyword (or `derive` /
            // contextual `static func` / `readonly struct`) is unambiguous against a
            // field/method, and the attributes parsed above flow to the nested type.
            if (IsNestedTypeStart())
            {
                nestedTypes.Add(ParseNestedType(memberAttributes ?? []));
                SkipSeparators();
                continue;
            }

            // `returns Type` clause: default return type for nested methods.
            if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "returns"))
            {
                ReportUnattachableAttributes(memberAttributes, "a returns clause");
                NextToken();
                var t = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.CloseBrace);
                defaultReturns = new ReturnsClauseSyntax(t);
                SkipSeparators();
                continue;
            }

            // virtual / abstract / : func — inheritance-participating method forms.
            if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "virtual") && Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            {
                NextToken(); // consume virtual
                var m = ParseFunctionDeclaration(isPublic, modifier: FunctionModifier.Virtual, attributes: memberAttributes);
                var selfParam = new ParameterSyntax("self", new NamedTypeSyntax(name));
                var ps = new List<ParameterSyntax> { selfParam };
                ps.AddRange(m.Parameters);
                inlineMethods.Add(m with { Parameters = ps, IsTypeBodyMethod = true });
                SkipSeparators();
                continue;
            }
            if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "abstract") && Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            {
                if (modifier != ClassModifier.Abstract)
                    Report(Current.Line, Current.Column,
                        $"ES2125: 'abstract func' is only allowed inside 'abstract class'. Mark '{name}' abstract or remove the abstract on this method.");
                NextToken(); // consume abstract
                var m = ParseFunctionDeclaration(isPublic, modifier: FunctionModifier.Abstract, attributes: memberAttributes);
                var selfParam = new ParameterSyntax("self", new NamedTypeSyntax(name));
                var ps = new List<ParameterSyntax> { selfParam };
                ps.AddRange(m.Parameters);
                inlineMethods.Add(m with { Parameters = ps, IsTypeBodyMethod = true });
                SkipSeparators();
                continue;
            }
            if (Current.Kind == SyntaxTokenKind.Colon && Peek(1).Kind == SyntaxTokenKind.FuncKeyword)
            {
                if (interfaces.Count == 0)
                    Report(Current.Line, Current.Column,
                        $"ES2124: ':' prefix is only valid inside a class declaring inheritance — '{name}' has no base type or interface list.");
                NextToken(); // consume :
                var m = ParseFunctionDeclaration(isPublic, modifier: FunctionModifier.InheritColon, attributes: memberAttributes);
                var selfParam = new ParameterSyntax("self", new NamedTypeSyntax(name));
                var ps = new List<ParameterSyntax> { selfParam };
                ps.AddRange(m.Parameters);
                inlineMethods.Add(m with { Parameters = ps, IsTypeBodyMethod = true });
                SkipSeparators();
                continue;
            }

            // Detect init constructor:
            //   [priv|protected] init(params) [: this(args) | : base(args)] { body }
            //   [priv|protected] init { body }            — the param-less form
            // `priv`/`protected` narrow the emitted .ctor's visibility (factory-
            // enforced / base-only construction).
            {
                var initVisibility = InitVisibility.Default;
                var visLook = 0;
                if (Current.Kind == SyntaxTokenKind.PrivKeyword) { initVisibility = InitVisibility.Private; visLook = 1; }
                else if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "protected") { initVisibility = InitVisibility.Protected; visLook = 1; }

                if (Peek(visLook).Kind == SyntaxTokenKind.Identifier && Peek(visLook).Text == "init"
                    && Peek(visLook + 1).Kind is SyntaxTokenKind.OpenParen or SyntaxTokenKind.OpenBrace)
                {
                    ReportUnattachableAttributes(memberAttributes, "an init constructor");
                    // `init` blocks are allowed only on `class`. On value-semantic
                    // `data`, use a composite literal `T { field: ... }` or a factory
                    // function `func make(...) -> T`.
                    if (!isRef)
                    {
                        var initTok = Current;
                        Report(initTok.Line, initTok.Column,
                            $"ES3012: `init` blocks are not allowed on `data {name}` — `data` is value-semantic, construct with a composite literal `{name} {{ ... }}` or a factory `func make(...) -> {name}`. If you need a constructor, declare the type as `class {name}`.");
                    }
                    if (visLook == 1) NextToken(); // consume priv / protected
                    NextToken(); // consume 'init'

                    var initParams = new List<ParameterSyntax>();
                    if (Current.Kind == SyntaxTokenKind.OpenParen)
                    {
                        NextToken(); // consume (
                        SkipSeparators();
                        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
                        {
                            var pStart = Current.Position;
                            var pNameTok = Match(SyntaxTokenKind.Identifier, "Expected parameter name.");
                            Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
                            var pType = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen, SyntaxTokenKind.Equals);
                            ExpressionSyntax? pDefault = null;
                            if (Current.Kind == SyntaxTokenKind.Equals)
                            {
                                NextToken();
                                pDefault = Expressions.ParseExpression();
                            }
                            initParams.Add(new ParameterSyntax(pNameTok.Text, pType, DefaultValue: pDefault) { NameSpan = SpanOf(pNameTok) });
                            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                            SkipSeparators();
                            if (Current.Position == pStart && Current.Kind != SyntaxTokenKind.EndOfFile)
                                NextToken();
                        }
                        Match(SyntaxTokenKind.CloseParen, "Expected ')' after init parameters.");
                    }

                    // Optional `: this(args)` sibling delegation or `: base(args)` base call.
                    IReadOnlyList<ExpressionSyntax>? baseArgs = null;
                    IReadOnlyList<ExpressionSyntax>? thisArgs = null;
                    if (Current.Kind == SyntaxTokenKind.Colon
                        && Peek(1).Kind == SyntaxTokenKind.Identifier && Peek(1).Text is "base" or "this")
                    {
                        NextToken(); // :
                        var kindTok = NextToken(); // base / this
                        Match(SyntaxTokenKind.OpenParen, $"Expected '(' after '{kindTok.Text}'.");
                        var args = new List<ExpressionSyntax>();
                        SkipSeparators();
                        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
                        {
                            args.Add(Expressions.ParseExpression());
                            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                            SkipSeparators();
                        }
                        Match(SyntaxTokenKind.CloseParen, $"Expected ')' after {kindTok.Text} arguments.");
                        if (kindTok.Text == "this") thisArgs = args;
                        else baseArgs = args;
                    }
                    SkipSeparators();
                    P.InClassInitBody = true;
                    BlockStatementSyntax initBody;
                    try { initBody = Statements.ParseBlockStatement(); }
                    finally { P.InClassInitBody = false; }
                    if (isRef)
                        initDecls.Add(new InitDeclarationSyntax(initParams, initBody, baseArgs, thisArgs, initVisibility));
                    SkipSeparators();
                    continue;
                }
            }

            // Detect inline method: [pub] func name(...)
            // Methods inherit pub from the enclosing type (same as fields).
            if (Current.Kind == SyntaxTokenKind.FuncKeyword ||
                (Current.Kind == SyntaxTokenKind.PubKeyword && Peek(1).Kind == SyntaxTokenKind.FuncKeyword))
            {
                var methodPublic = isPublic;
                if (Current.Kind == SyntaxTokenKind.PubKeyword)
                {
                    methodPublic = true;
                    NextToken();
                }
                var methodDecl = ParseFunctionDeclaration(methodPublic, attributes: memberAttributes);
                // Prepend implicit self parameter
                var selfParam = new ParameterSyntax("self", new NamedTypeSyntax(name));
                var allParams = new List<ParameterSyntax> { selfParam };
                allParams.AddRange(methodDecl.Parameters);
                inlineMethods.Add(methodDecl with { Parameters = allParams, IsTypeBodyMethod = true });
                SkipSeparators();
                continue;
            }

            // Everything below is a field-shaped member; attributes attach only to
            // the method forms above.
            ReportUnattachableAttributes(memberAttributes, "a field");

            // Field-style event: [pub|priv] event Name: T  — a delegate-typed event
            // member (mirrors `let x: T`). `event` is contextual (only here, before an
            // identifier-then-colon); it stays a valid identifier elsewhere. Detected
            // ahead of embedding so `event` isn't read as an embedded type name.
            {
                var evLook = 0;
                bool? evVis = null;
                if (Current.Kind == SyntaxTokenKind.PubKeyword) { evVis = true; evLook = 1; }
                else if (Current.Kind == SyntaxTokenKind.PrivKeyword) { evVis = false; evLook = 1; }
                if (Peek(evLook).Kind == SyntaxTokenKind.Identifier && Peek(evLook).Text == "event"
                    && Peek(evLook + 1).Kind == SyntaxTokenKind.Identifier
                    && Peek(evLook + 2).Kind == SyntaxTokenKind.Colon)
                {
                    if (evVis is not null) NextToken(); // pub/priv
                    NextToken();                        // contextual 'event'
                    var evNameTok = Match(SyntaxTokenKind.Identifier, "Expected event name.");
                    Match(SyntaxTokenKind.Colon, "Expected ':' after event name.");
                    var evType = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.CloseBrace, SyntaxTokenKind.Comma);
                    fields.Add(new FieldSyntax(evNameTok.Text, evType, evVis, Mutable: false, IsEvent: true) { NameSpan = SpanOf(evNameTok) });
                    SkipSeparators();
                    continue;
                }
            }

            // `required` field marker (contextual): [pub|priv] required [let|var] name: Type.
            // A composite literal must supply every required field (ES2189). Detected
            // before embedding so `required` isn't read as an embedded type name.
            var fieldRequired = false;
            {
                var reqLook = Current.Kind is SyntaxTokenKind.PubKeyword or SyntaxTokenKind.PrivKeyword ? 1 : 0;
                if (Peek(reqLook).Kind == SyntaxTokenKind.Identifier && Peek(reqLook).Text == "required"
                    && (Peek(reqLook + 1).Kind is SyntaxTokenKind.LetKeyword or SyntaxTokenKind.VarKeyword
                        || (Peek(reqLook + 1).Kind == SyntaxTokenKind.Identifier && Peek(reqLook + 2).Kind == SyntaxTokenKind.Colon)))
                    fieldRequired = true;
            }

            // Detect embedded field: type name alone (no name: type syntax).
            // Optional leading `pub` / `priv` carries the visibility through.
            bool? embedVisibility = null;
            int embedLookahead = 0;
            if (Current.Kind == SyntaxTokenKind.PubKeyword) { embedVisibility = true; embedLookahead = 1; }
            else if (Current.Kind == SyntaxTokenKind.PrivKeyword) { embedVisibility = false; embedLookahead = 1; }

            // Pointer embedding: [pub|priv] *Base
            if (!fieldRequired && Peek(embedLookahead).Kind == SyntaxTokenKind.Star
                && Peek(embedLookahead + 1).Kind == SyntaxTokenKind.Identifier
                && Peek(embedLookahead + 2).Kind != SyntaxTokenKind.Colon)
            {
                if (embedVisibility is not null) NextToken(); // consume pub/priv
                NextToken(); // consume *
                var embedNameTok = Match(SyntaxTokenKind.Identifier, "Expected embedded type name.");
                var embedName = embedNameTok.Text;
                fields.Add(new FieldSyntax(embedName, new PointerTypeSyntax(new NamedTypeSyntax(embedName), false), embedVisibility, false, null, IsEmbedded: true) { NameSpan = SpanOf(embedNameTok) });
                SkipSeparators();
                continue;
            }
            // Value embedding: [pub|priv] Base
            if (!fieldRequired && Peek(embedLookahead).Kind == SyntaxTokenKind.Identifier
                && Peek(embedLookahead + 1).Kind != SyntaxTokenKind.Colon
                && Peek(embedLookahead + 1).Kind != SyntaxTokenKind.OpenParen)
            {
                if (embedVisibility is not null) NextToken(); // consume pub/priv
                var embedNameTok = Match(SyntaxTokenKind.Identifier, "Expected embedded type name.");
                var embedName = embedNameTok.Text;
                fields.Add(new FieldSyntax(embedName, new NamedTypeSyntax(embedName), embedVisibility, false, null, IsEmbedded: true) { NameSpan = SpanOf(embedNameTok) });
                SkipSeparators();
                continue;
            }

            bool? fieldVisibility = null;
            if (Current.Kind == SyntaxTokenKind.PubKeyword) { fieldVisibility = true; NextToken(); }
            else if (Current.Kind == SyntaxTokenKind.PrivKeyword) { fieldVisibility = false; NextToken(); }
            if (fieldRequired) NextToken(); // consume contextual 'required'

            // `const NAME[: T] = literal` inside a data body. Parses as an
            // immutable field at this level — the field is emitted as a CLR
            // `literal` field and the binder inlines reads at the use site.
            if (Current.Kind == SyntaxTokenKind.ConstKeyword)
            {
                NextToken();
                var constNameTok = Match(SyntaxTokenKind.Identifier, "Expected const name.");
                TypeSyntax? constType = null;
                if (Current.Kind == SyntaxTokenKind.Colon)
                {
                    NextToken();
                    constType = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.Equals, SyntaxTokenKind.CloseBrace);
                }
                Match(SyntaxTokenKind.Equals, "Expected '=' in const declaration.");
                var constValue = Expressions.ParseExpression();
                fields.Add(new FieldSyntax(constNameTok.Text, constType ?? InferredTypeSyntax.Instance, fieldVisibility, Mutable: false, constValue) { NameSpan = SpanOf(constNameTok) });
                SkipSeparators();
                continue;
            }

            var fieldMutable = true;
            // A member let/var is a property declaration, even without `{}`.
            // Bare `name: T` remains the deliberately direct field spelling.
            var memberProperty = false;
            if (Current.Kind == SyntaxTokenKind.LetKeyword) { fieldMutable = false; memberProperty = true; NextToken(); }
            else if (Current.Kind == SyntaxTokenKind.VarKeyword) { fieldMutable = true; memberProperty = true; NextToken(); }

            if (isReadonly && fieldMutable && Current.Kind != SyntaxTokenKind.Identifier)
                fieldMutable = false;
            if (isReadonly) fieldMutable = false;

            var fieldNameTok = Match(SyntaxTokenKind.Identifier, "Expected field name.");
            Match(SyntaxTokenKind.Colon, "Expected ':' after field name.");
            var type = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.Comma, SyntaxTokenKind.CloseBrace, SyntaxTokenKind.Equals, SyntaxTokenKind.FatArrow, SyntaxTokenKind.OpenBrace);

            // `let`/`var` selects compiler-managed property representation.  Bare
            // members keep field representation unless they explicitly use `=>` or
            // an accessor block.  A stored property's `= value` initializes its
            // generated storage in constructors; it does not turn it into a field.
            PropertyAccessorsSyntax? property = memberProperty
                ? new PropertyAccessorsSyntax(HasGet: true, HasSet: fieldMutable, HasInit: !fieldMutable)
                : null;
            ExpressionSyntax? defaultValue = null;
            if (Current.Kind == SyntaxTokenKind.FatArrow)
            {
                // `let x: T => expr` — computed, get-only, recomputed; no backing field.
                // PropertyLowering (run at this declaration's close) synthesizes the
                // `get_<name>` accessor method from this expression.
                NextToken(); // consume '=>'
                var getterExpr = Expressions.ParseExpression();
                property = new PropertyAccessorsSyntax(HasGet: true, HasSet: false, HasInit: false, ComputedGetter: getterExpr);
            }
            else if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                // `{ }` / `{ get }` / `{ get set }` / `{ set(v) => expr }` — the accessor
                // set is keyword-derived (let→get · required let→get+init · var→get+set);
                // an explicit `set(v) =>` supplies a custom setter body over an auto getter.
                property = ParsePropertyAccessorBlock(fieldMutable, fieldRequired);
            }
            else if (Current.Kind == SyntaxTokenKind.Equals)
            {
                NextToken(); // consume '='
                defaultValue = Expressions.ParseExpression();
            }

            fields.Add(new FieldSyntax(fieldNameTok.Text, type, fieldVisibility, fieldMutable, defaultValue, IsRequired: fieldRequired, Property: property) { NameSpan = SpanOf(fieldNameTok) });
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close data declaration.");
        HoistInitFields(fields, initDecls, isPublic);
        var decl = new DataDeclarationSyntax(isRef, isPublic, isReadonly, name, typeParameters, interfaces, fields, attributes, initDecls.Count > 0 ? initDecls : null, inlineMethods.Count > 0 ? inlineMethods : null, defaultReturns, modifier, derive?.Traits, headerParams, NestedTypes: nestedTypes.Count > 0 ? nestedTypes : null) { NameSpan = SpanOf(nameTok), Visibility = declVisibility };
        // Synthesize property accessor methods (computed getter, custom setter) from the
        // declaration's property fields — see Lowering/PropertyLowering.cs.
        return PropertyLowering.Lower(decl);
    }

    void HoistInitFields(List<FieldSyntax> fields, List<InitDeclarationSyntax> inits, bool typeIsPublic)
    {
        if (inits.Count == 0) return;
        var rootSets = new List<Dictionary<string, InitFieldDeclarationStatementSyntax>>();
        for (var i = 0; i < inits.Count; i++)
        {
            var init = inits[i];
            var declared = new Dictionary<string, InitFieldDeclarationStatementSyntax>(StringComparer.Ordinal);
            foreach (var field in init.Body.Statements.OfType<InitFieldDeclarationStatementSyntax>())
            {
                if (!declared.TryAdd(field.Name, field))
                    Report(Current.Line, Current.Column,
                        $"ES2206: init-owned field '{field.Name}' is declared more than once in the same constructor.");
            }
            if (init.ThisArguments is null) rootSets.Add(declared);
            var rewritten = init.Body.Statements.Select(s => s is InitFieldDeclarationStatementSyntax f
                ? (StatementSyntax)new AssignmentStatementSyntax(
                    new MemberAccessExpressionSyntax(new NameExpressionSyntax("self"), f.Name), f.Initializer)
                    { Span = f.Span }
                : s).ToList();
            inits[i] = init with { Body = new BlockStatementSyntax(rewritten) { Span = init.Body.Span } };
        }
        if (rootSets.Count == 0) return;
        var canonical = rootSets[0];
        foreach (var other in rootSets.Skip(1))
            if (canonical.Count != other.Count
                || canonical.Keys.Except(other.Keys).Any()
                || canonical.Any(pair => other.TryGetValue(pair.Key, out var candidate)
                    && (!Equals(pair.Value.Type, candidate.Type) || pair.Value.IsPublic != candidate.IsPublic)))
                Report(Current.Line, Current.Column,
                    "ES2207: all nondelegating init constructors must declare the same init-owned fields with matching types and visibility.");
        foreach (var field in canonical.Values)
        {
            if (fields.Any(x => x.Name == field.Name))
            {
                Report(Current.Line, Current.Column,
                    $"ES2208: init-owned field '{field.Name}' conflicts with an existing member.");
                continue;
            }
            fields.Insert(0, new FieldSyntax(field.Name, field.Type, field.IsPublic, Mutable: true)
            { NameSpan = field.NameSpan, Span = field.Span });
        }
    }

    /// Parse a property accessor block `{ … }` on a `data`/`class` member. The base
    /// accessor set is keyword-derived (`let`→get · `required let`→get+init · `var`→
    /// get+set); an empty block takes that set auto-backed. A `set(v) => expr` supplies
    /// a custom setter body over the auto getter. Bare `get`/`set`/`init` tokens are
    /// tolerated (the explicit form), but the keyword already implies the set here.
    PropertyAccessorsSyntax ParsePropertyAccessorBlock(bool mutable, bool required)
    {
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' to open property accessors.");
        SkipSeparators();

        var hasGet = true;
        var hasSet = mutable;
        var hasInit = required && !mutable;
        string? setterParam = null;
        ExpressionSyntax? setterBody = null;
        string? locaStorageName = null;
        string? mutStorageName = null;
        BlockStatementSyntax? scopedMutBody = null;

        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var loopStart = Current.Position;
            if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "set"
                && Peek(1).Kind == SyntaxTokenKind.OpenParen)
            {
                // `set(v) => expr` — custom setter binding its value explicitly.
                NextToken(); // 'set'
                NextToken(); // '('
                setterParam = Match(SyntaxTokenKind.Identifier, "Expected setter value parameter name.").Text;
                Match(SyntaxTokenKind.CloseParen, "Expected ')' after setter parameter.");
                Match(SyntaxTokenKind.FatArrow, "Expected '=>' for a custom setter body.");
                setterBody = Expressions.ParseExpression();
                hasSet = true;
                SkipSeparators();
            }
            else if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text is "get" or "set" or "init")
            {
                // Bare accessor keyword (explicit form): record which accessors are present.
                if (Current.Text == "get") hasGet = true;
                else if (Current.Text == "set") hasSet = true;
                else hasInit = true;
                NextToken();
                SkipSeparators();
            }
            else if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text is "loca" or "mut")
            {
                var capability = Current.Text;
                NextToken();
                if (capability == "mut" && Current.Kind == SyntaxTokenKind.OpenBrace)
                {
                    if (scopedMutBody is not null || mutStorageName is not null)
                        Report(Current.Line, Current.Column,
                            "ES2202: a property may declare only one `mut` accessor.");
                    var previous = P.InScopedMutAccessorBody;
                    P.InScopedMutAccessorBody = true;
                    try { scopedMutBody = Statements.ParseBlockStatement(); }
                    finally { P.InScopedMutAccessorBody = previous; }
                }
                else
                {
                    Match(SyntaxTokenKind.FatArrow, $"Expected '=>' after `{capability}`.");
                    var location = Expressions.ParseExpression();
                    if (location is AddressOfExpressionSyntax
                        {
                            Target: MemberAccessExpressionSyntax
                            {
                                Target: NameExpressionSyntax { Name: "self" },
                                MemberName: var storage
                            }
                        })
                    {
                        if (capability == "loca") locaStorageName = storage;
                        else
                        {
                            if (scopedMutBody is not null || mutStorageName is not null)
                                Report(Current.Line, Current.Column,
                                    "ES2202: a property may declare only one `mut` accessor.");
                            mutStorageName = storage;
                        }
                    }
                    else
                        Report(Current.Line, Current.Column,
                            $"ES2201: `{capability}` must directly name stable storage as `&self.name`.");
                }
                SkipSeparators();
            }
            else
            {
                Report(Current.Line, Current.Column,
                    "ES2196: unexpected token in property accessor block — expected `get`, `set`, `init`, `loca => &self.name`, `mut => &self.name`, `mut { … yield &location … }`, `set(v) => …`, or `}`.");
                NextToken();
            }
            if (Current.Position == loopStart && Current.Kind != SyntaxTokenKind.EndOfFile) NextToken();
        }
        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close property accessors.");
        return new PropertyAccessorsSyntax(hasGet, hasSet, hasInit, ComputedGetter: null, setterParam, setterBody,
            locaStorageName, mutStorageName, scopedMutBody);
    }

    ChoiceDeclarationSyntax ParseChoiceDeclaration(bool isPublic = false)
    {
        var isRef = Current.Kind == SyntaxTokenKind.RefKeyword;
        if (isRef) NextToken();
        Match(SyntaxTokenKind.UnionKeyword);
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected choice type name.");
        var name = nameTok.Text;

        // Optional type parameters: choice Option<T> { ... }
        var typeParameters = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Less)
        {
            NextToken();
            while (Current.Kind is not (SyntaxTokenKind.Greater or SyntaxTokenKind.EndOfFile))
            {
                typeParameters.Add(Match(SyntaxTokenKind.Identifier, "Expected type parameter name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Greater, "Expected '>' to close type parameter list.");
        }

        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after choice declaration.");
        SkipSeparators();

        var cases = new List<ChoiceCaseSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var caseNameTok = Match(SyntaxTokenKind.Identifier, "Expected case name.");
            var payloads = new List<FieldSyntax>();

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                NextToken();
                SkipSeparators();
                while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
                {
                    var pStart = Current.Position;
                    var pNameTok = Match(SyntaxTokenKind.Identifier, "Expected payload name.");
                    Match(SyntaxTokenKind.Colon, "Expected ':' after payload name.");
                    var pType = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
                    payloads.Add(new FieldSyntax(pNameTok.Text, pType) { NameSpan = SpanOf(pNameTok) });
                    if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                    SkipSeparators();
                    if (Current.Position == pStart && Current.Kind != SyntaxTokenKind.EndOfFile)
                        NextToken();
                }
                Match(SyntaxTokenKind.CloseParen, "Expected ')' after payloads.");
            }

            cases.Add(new ChoiceCaseSyntax(caseNameTok.Text, payloads) { NameSpan = SpanOf(caseNameTok) });
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close choice declaration.");
        return new ChoiceDeclarationSyntax(isRef, isPublic, name, typeParameters, cases) { NameSpan = SpanOf(nameTok), Visibility = _declVisibility };
    }

    EnumDeclarationSyntax ParseEnumDeclaration(bool isPublic = false)
    {
        Match(SyntaxTokenKind.EnumKeyword);
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected enum type name.");
        var name = nameTok.Text;
        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after enum declaration.");
        SkipSeparators();

        var cases = new List<EnumCaseSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var caseNameTok = MatchEnumCaseName();
            int? explicitValue = null;
            if (Current.Kind == SyntaxTokenKind.Equals)
            {
                NextToken();
                bool negate = false;
                if (Current.Kind == SyntaxTokenKind.Minus) { negate = true; NextToken(); }
                var valueToken = Match(SyntaxTokenKind.NumberLiteral, "Expected integer value after '='.");
                if (int.TryParse(valueToken.Text, out var parsed))
                    explicitValue = negate ? -parsed : parsed;
            }
            cases.Add(new EnumCaseSyntax(caseNameTok.Text, explicitValue) { Span = SpanOf(caseNameTok), NameSpan = SpanOf(caseNameTok) });
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close enum declaration.");
        return new EnumDeclarationSyntax(isPublic, name, cases) { NameSpan = SpanOf(nameTok), Visibility = _declVisibility };
    }

    /// An enum case name. Case position is unambiguous, so a reserved word reads as a
    /// case name (`enum Kind { recv, send, default }`) — the contextual-keyword stance
    /// (`default`/`match`/`select` are valid names here, just as `let static = 1` is a
    /// valid binding). Any identifier-shaped token (letter/underscore start) is taken;
    /// punctuation/number still errors with the usual message.
    SyntaxToken MatchEnumCaseName()
    {
        var t = Current;
        if (t.Kind == SyntaxTokenKind.Identifier
            || (t.Text.Length > 0 && (char.IsLetter(t.Text[0]) || t.Text[0] == '_')))
        {
            NextToken();
            return t;
        }
        return Match(SyntaxTokenKind.Identifier, "Expected enum case name.");
    }

    InterfaceDeclarationSyntax ParseInterfaceDeclaration(bool isPublic = false)
    {
        Match(SyntaxTokenKind.InterfaceKeyword);
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected interface name.");
        var name = nameTok.Text;

        // Optional type parameters: interface IBox<T> { ... }
        var typeParameters = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Less)
        {
            NextToken();
            while (Current.Kind is not (SyntaxTokenKind.Greater or SyntaxTokenKind.EndOfFile))
            {
                typeParameters.Add(Match(SyntaxTokenKind.Identifier, "Expected type parameter name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Greater, "Expected '>' to close type parameter list.");
        }

        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after interface declaration.");
        SkipSeparators();

        var methods = new List<InterfaceMethodSyntax>();
        var events = new List<FieldSyntax>();
        var properties = new List<InterfacePropertySyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;

            // Field-style event in an interface: event Name: T  — an abstract
            // add/remove pair (no backing field). `event` is contextual.
            if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "event"
                && Peek(1).Kind == SyntaxTokenKind.Identifier && Peek(2).Kind == SyntaxTokenKind.Colon)
            {
                NextToken(); // contextual 'event'
                var evNameTok = Match(SyntaxTokenKind.Identifier, "Expected event name.");
                Match(SyntaxTokenKind.Colon, "Expected ':' after event name.");
                var evType = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.CloseBrace, SyntaxTokenKind.Comma);
                events.Add(new FieldSyntax(evNameTok.Text, evType, IsPublic: true, Mutable: false, IsEvent: true) { NameSpan = SpanOf(evNameTok) });
                SkipSeparators();
                continue;
            }

            // Property requirement: `[ let | var ] name: T { get [set] }`. The `let`/`var`
            // keyword is optional (per the grammar); a bare `name:` with no `func`/`event`
            // is the property form. The accessor words inside the block fix the minimum set.
            if (Current.Kind is SyntaxTokenKind.LetKeyword or SyntaxTokenKind.VarKeyword
                || (Current.Kind == SyntaxTokenKind.Identifier && Peek(1).Kind == SyntaxTokenKind.Colon))
            {
                if (Current.Kind is SyntaxTokenKind.LetKeyword or SyntaxTokenKind.VarKeyword)
                    NextToken(); // optional let/var — accessor words below decide the set
                var propNameTok = Match(SyntaxTokenKind.Identifier, "Expected property name.");
                Match(SyntaxTokenKind.Colon, "Expected ':' after property name.");
                var propType = Types.ParseTypeUntil(SyntaxTokenKind.OpenBrace);
                Match(SyntaxTokenKind.OpenBrace, "Expected '{' to open the interface property accessor set.");
                var hasGet = false;
                var hasSet = false;
                var hasInit = false;
                var hasLoca = false;
                while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
                {
                    if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "get") hasGet = true;
                    else if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "set") hasSet = true;
                    else if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "init") hasInit = true;
                    else if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "loca") hasLoca = true;
                    else Report(Current.Line, Current.Column,
                        "Interface property capabilities are 'get', 'set', 'init', and 'loca' — write `name: T { get }`, `name: T { get set }`, `name: T { get init }`, or add `loca` for durable identity.");
                    NextToken();
                    SkipSeparators();
                }
                Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close the interface property accessor set.");
                if (!hasGet && !hasSet && !hasInit)
                    Report(propNameTok.Line, propNameTok.Column,
                        $"Interface property '{propNameTok.Text}' must declare at least one accessor — `{{ get }}`, `{{ get set }}`, or `{{ get init }}`.");
                if (hasSet && hasInit)
                    Report(propNameTok.Line, propNameTok.Column,
                        $"Interface property '{propNameTok.Text}' cannot require both 'set' and 'init' — a setter is either freely settable or init-only.");
                properties.Add(new InterfacePropertySyntax(
                    propNameTok.Text, propType, hasGet, hasSet, hasInit, hasLoca) { NameSpan = SpanOf(propNameTok) });
                SkipSeparators();
                continue;
            }

            Match(SyntaxTokenKind.FuncKeyword, "Expected 'func' in interface method.");
            var methodNameTok = Match(SyntaxTokenKind.Identifier, "Expected method name.");
            Match(SyntaxTokenKind.OpenParen, "Expected '(' after method name.");

            var parameters = new List<ParameterSyntax>();
            SkipSeparators();
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                var paramNameTok = Match(SyntaxTokenKind.Identifier, "Expected parameter name.");
                Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
                var type = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
                parameters.Add(new ParameterSyntax(paramNameTok.Text, type) { NameSpan = SpanOf(paramNameTok) });
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                SkipSeparators();
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after parameters.");

            TypeSyntax returnType;
            if (Current.Kind == SyntaxTokenKind.Arrow)
            {
                NextToken();
                returnType = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.CloseBrace);
            }
            else
            {
                returnType = new NamedTypeSyntax("void");
            }

            methods.Add(new InterfaceMethodSyntax(methodNameTok.Text, parameters, returnType) { NameSpan = SpanOf(methodNameTok) });
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close interface.");
        return new InterfaceDeclarationSyntax(isPublic, name, typeParameters, methods, events.Count > 0 ? events : null, properties.Count > 0 ? properties : null) { NameSpan = SpanOf(nameTok), Visibility = _declVisibility };
    }

    // Current token is `<`. Peek past the balanced angle group and report whether a
    // `.` follows — i.e. this is `IFace<...>.method`, not a generic method `m<...>(`.
    bool GenericExplicitInterfaceAhead()
    {
        var depth = 0;
        for (var i = 0; ; i++)
        {
            var k = Peek(i).Kind;
            if (k == SyntaxTokenKind.EndOfFile) return false;
            if (k == SyntaxTokenKind.Less) depth++;
            else if (k == SyntaxTokenKind.Greater && --depth == 0)
                return Peek(i + 1).Kind == SyntaxTokenKind.Dot;
        }
    }

    public FunctionDeclarationSyntax ParseFunctionDeclaration(bool isPublic = false, bool isTaskFunc = false, FunctionModifier modifier = FunctionModifier.None, List<AttributeSyntax>? attributes = null, bool readonlyReceiver = false)
    {
        if ((Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "task"))
        {
            NextToken(); // consume task
            isTaskFunc = true;
        }
        Match(SyntaxTokenKind.FuncKeyword);

        // Go-style method receiver: `func (c: *Circle) scale(...)`. A `(` immediately
        // after `func` can only open a receiver block (no other declaration form has
        // one there), so this is zero-lookahead. The receiver lifts out of the
        // parameter list — the binder re-inserts it as the synthesized leading `self`.
        ReceiverSyntax? receiver = null;
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken(); // consume `(`
            var recvNameTok = Match(SyntaxTokenKind.Identifier, "Expected receiver name in 'func (name: Type)'.");
            Match(SyntaxTokenKind.Colon, "Expected ':' after the receiver name — a receiver is written 'name: Type', like a parameter.");
            var isStaticFacet = Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "static";
            if (isStaticFacet) NextToken();
            var recvType = Types.ParseTypeUntil(SyntaxTokenKind.CloseParen);
            Match(SyntaxTokenKind.CloseParen, "Expected ')' to close the receiver block.");
            receiver = new ReceiverSyntax(recvNameTok.Text, recvType, readonlyReceiver, isStaticFacet) { NameSpan = SpanOf(recvNameTok) };
        }
        else if (readonlyReceiver)
        {
            Report(Current.Line, Current.Column,
                "'readonly func' modifies a method receiver — write 'readonly func (c: T) name()'. A free function cannot be 'readonly'.");
        }

        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected function name.");
        var name = nameTok.Text;

        // Explicit interface implementation: `func IFace.method(...)`. The leading
        // identifier names the interface; the member name follows the dot. Routed to that
        // one interface's slot at emit (private/final/virtual + single MethodImpl).
        // A receiver method (`func (c: T) m()`) is attached at namespace scope by its
        // receiver, never by an interface qualifier — the two forms don't coexist, so
        // skip explicit-interface detection when a receiver block was parsed.
        TypeSyntax? explicitInterface = null;
        // Generic explicit interface: `func IBox<T>.get(...)`. Disambiguate from a
        // generic method `func get<T>(...)` by peeking past the balanced `<...>` for a
        // trailing dot; the angle group is then a closed interface's type arguments.
        if (receiver is null && Current.Kind == SyntaxTokenKind.Less && GenericExplicitInterfaceAhead())
        {
            explicitInterface = new GenericTypeSyntax(name, Types.ParseTypeArguments());
            Match(SyntaxTokenKind.Dot, "Expected '.' after generic explicit interface qualifier.");
            nameTok = Match(SyntaxTokenKind.Identifier, "Expected method name after explicit interface qualifier.");
            name = nameTok.Text;
        }
        else if (receiver is null && Current.Kind == SyntaxTokenKind.Dot)
        {
            NextToken(); // consume '.'
            explicitInterface = new NamedTypeSyntax(name);
            nameTok = Match(SyntaxTokenKind.Identifier, "Expected method name after explicit interface qualifier.");
            name = nameTok.Text;
        }

        // Parse optional type parameters <T, U, ...>
        var typeParameters = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Less)
        {
            NextToken();
            while (Current.Kind is not (SyntaxTokenKind.Greater or SyntaxTokenKind.EndOfFile))
            {
                typeParameters.Add(Match(SyntaxTokenKind.Identifier, "Expected type parameter name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Greater, "Expected '>' to close type parameter list.");
        }

        Match(SyntaxTokenKind.OpenParen, "Expected '(' after function name.");

        var parameters = new List<ParameterSyntax>();
        // The receiver IS the leading parameter (`func (c: *T) m(args)` ⇒ params c, args…)
        // — every downstream consumer that reads Parameters[0] as the receiver (call
        // resolution, generic inference, the emit `.Skip(1)`) works unchanged. `Receiver`
        // stays on the node as the "this is a method" + readonly marker.
        if (receiver is not null)
            parameters.Add(new ParameterSyntax(receiver.Name, receiver.Type) { NameSpan = receiver.NameSpan });
        SkipSeparators();
        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            // `out` parameter modifier — `out name: T` emits a CLR `[Out] T&`
            // (implicit deref on use), matching C# out-parameters. `out` is a
            // reserved keyword (Lexer), so no contextual disambiguation needed.
            var isOut = Current.Kind == SyntaxTokenKind.OutKeyword;
            if (isOut) NextToken();
            var parameterNameTok = Match(SyntaxTokenKind.Identifier, "Expected parameter name.");
            Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
            var type = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen, SyntaxTokenKind.Equals);
            // Optional default: `name: T = expr` (constant shape — validated in the binder).
            ExpressionSyntax? paramDefault = null;
            if (Current.Kind == SyntaxTokenKind.Equals)
            {
                NextToken();
                paramDefault = Expressions.ParseExpression();
            }
            parameters.Add(new ParameterSyntax(parameterNameTok.Text, type, isOut, paramDefault) { NameSpan = SpanOf(parameterNameTok) });

            if (Current.Kind == SyntaxTokenKind.Comma)
                NextToken();

            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseParen, "Expected ')' after parameter list.");

        TypeSyntax returnType;
        bool hasExplicitReturn;
        if (Current.Kind == SyntaxTokenKind.Arrow)
        {
            NextToken();
            returnType = Types.ParseTypeUntil(SyntaxTokenKind.OpenBrace, SyntaxTokenKind.NewLine, SyntaxTokenKind.Equals);
            hasExplicitReturn = true;
        }
        else
        {
            returnType = new NamedTypeSyntax("void");
            hasExplicitReturn = false;
        }

        var attrs = attributes ?? new List<AttributeSyntax>();
        SkipSeparators();
        // `abstract func name(...)` parent-side: no body, return type optional.
        if (modifier == FunctionModifier.Abstract &&
            (Current.Kind is SyntaxTokenKind.NewLine or SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile or SyntaxTokenKind.FuncKeyword or SyntaxTokenKind.Colon
             || (Current.Kind == SyntaxTokenKind.Identifier && Current.Text is "abstract" or "virtual")))
        {
            var emptyBody = new BlockStatementSyntax(new List<StatementSyntax>()) { Span = SpanHere() };
            return new FunctionDeclarationSyntax(isPublic, name, typeParameters, parameters, returnType, emptyBody, attrs, false, modifier, isTaskFunc, explicitInterface) { Span = SpanHere(), NameSpan = SpanOf(nameTok) };
        }
        BlockStatementSyntax body;
        if (Current.Kind == SyntaxTokenKind.Equals)
        {
            // Expression-bodied function: func f() -> T = expr. A newline after `=`
            // continues the expression onto the next line (`= ⏎ match … { … }`).
            NextToken(); // consume =
            SkipNewlines();
            var expr = Expressions.ParseExpression();
            var returnStmt = new ReturnStatementSyntax(expr) { Span = expr.Span };
            body = new BlockStatementSyntax([returnStmt]) { Span = returnStmt.Span };
        }
        else
        {
            body = Statements.ParseBlockStatement();
        }
        return new FunctionDeclarationSyntax(isPublic, name, typeParameters, parameters, returnType, body, attrs, hasExplicitReturn, modifier, isTaskFunc, explicitInterface, receiver) { NameSpan = SpanOf(nameTok) };
    }

    // delegate func Name(params) [-> R]  — a nominal delegate type declaration.
    // Signature only: parameter list (out-aware, same as functions) + optional
    // return type. No body, no type parameters (the approved spelling is non-generic).
    DelegateDeclarationSyntax ParseDelegateDeclaration(bool isPublic = false)
    {
        NextToken(); // consume contextual 'delegate'
        Match(SyntaxTokenKind.FuncKeyword, "Expected 'func' after 'delegate'.");
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected delegate name.");
        var name = nameTok.Text;

        Match(SyntaxTokenKind.OpenParen, "Expected '(' after delegate name.");
        var parameters = new List<ParameterSyntax>();
        SkipSeparators();
        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var isOut = Current.Kind == SyntaxTokenKind.OutKeyword;
            if (isOut) NextToken();
            var parameterNameTok = Match(SyntaxTokenKind.Identifier, "Expected parameter name.");
            Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
            var type = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
            parameters.Add(new ParameterSyntax(parameterNameTok.Text, type, isOut) { NameSpan = SpanOf(parameterNameTok) });
            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            SkipSeparators();
            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile) NextToken();
        }
        Match(SyntaxTokenKind.CloseParen, "Expected ')' after parameter list.");

        TypeSyntax returnType;
        if (Current.Kind == SyntaxTokenKind.Arrow)
        {
            NextToken();
            returnType = Types.ParseTypeUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.EndOfFile, SyntaxTokenKind.CloseBrace);
        }
        else
        {
            returnType = new NamedTypeSyntax("void");
        }

        return new DelegateDeclarationSyntax(isPublic, name, parameters, returnType) { Span = SpanHere(), NameSpan = SpanOf(nameTok), Visibility = _declVisibility };
    }
}
