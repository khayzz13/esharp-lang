using System.Text;

namespace Esharp.Syntax;

/// Unparses a syntax tree back to E#. Two modes, both rooted in the same insight:
///
///  • <b>Byte-exact</b> — for a tree that came from a parse, the source is the truth.
///    `Print(ParsedSyntaxTree)` concatenates every token's leading trivia + text and
///    reproduces the source character for character (the round-trip oracle); a single
///    node prints by slicing `source[Start..End]`. Punctuation and comments the
///    abstract tree drops are recovered from the source/tokens, not re-synthesized.
///
///  • <b>Canonical</b> — for a node with no backing source (a `quote`-built or
///    fuzzer-mutated subtree), `PrintCanonical` re-emits idiomatic E# from structure
///    alone. Not byte-identical to any original, but valid and reparseable — the
///    formatter surface and the `quote` renderer.
///
/// `Print(node, source)` picks per-node: slice when the node maps to real source,
/// canonical otherwise — so an edited tree keeps every untouched region verbatim and
/// only re-formats what actually changed.
public static class SyntaxPrinter
{
    /// Byte-exact whole-tree reconstruction from the token stream. `print(parse(s)) == s`.
    public static string Print(ParsedSyntaxTree tree) => PrintTokens(tree.Tokens);

    /// Concatenate every token's leading trivia and text, in order. Complete because
    /// the lexer attributes every inter-token character (whitespace, comments) to a
    /// token's leading trivia, and the EOF token carries the file's trailing residue.
    public static string PrintTokens(IReadOnlyList<SyntaxToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            foreach (var trivia in t.Leading)
                sb.Append(trivia.Text);
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    /// Print one node: a verbatim source slice when it maps to real source, else a
    /// canonical re-emission. The edit-resilient path — untouched subtrees slice,
    /// synthesized ones format.
    public static string Print(SyntaxNode node, string? source = null)
    {
        if (source is not null)
        {
            var span = node.Span;
            if (span.IsValid && span.End > span.Start && span.End <= source.Length)
                return source[span.Start..span.End];
        }
        return PrintCanonical(node);
    }

    /// Re-emit idiomatic E# from structure alone (no source needed). Valid and
    /// reparseable; used for synthesized / mutated subtrees.
    public static string PrintCanonical(SyntaxNode node)
    {
        var p = new Canonical();
        p.Node(node);
        return p.ToString();
    }

    // The structural unparser. One method per node category; produces 4-space-indented
    // E# that parses back to an equivalent tree.
    sealed class Canonical
    {
        readonly StringBuilder _sb = new();
        int _indent;

        public override string ToString() => _sb.ToString();

        string Pad => new(' ', _indent * 4);

        public void Node(SyntaxNode node)
        {
            switch (node)
            {
                case TypeSyntax t: Type(t); break;
                case ExpressionSyntax e: Expr(e); break;
                case StatementSyntax s: Stmt(s); break;
                case MemberSyntax m: Member(m); break;
                case CompilationUnitSyntax u: Unit(u); break;
                default: _sb.Append(Fragment(node)); break;
            }
        }

        void Unit(CompilationUnitSyntax u)
        {
            if (u.NamespaceName is not null)
                _sb.Append("namespace ").Append(u.NamespaceName).Append('\n');
            foreach (var import in u.Imports)
            {
                _sb.Append("using ");
                if (import.IsStatic) _sb.Append("static ");
                if (import.Alias is not null) _sb.Append(import.Alias).Append(" = ");
                _sb.Append('"').Append(import.Path).Append("\"\n");
            }
            // The parser surfaces a `data`/`class` body's inline methods into the
            // top-level member list (the same `FunctionDeclarationSyntax` instances)
            // so the binder sees them as promoted free functions. The canonical text,
            // though, prints those methods inside their type — printing the surfaced
            // copies too would duplicate every type method, and because re-parsing the
            // printed text surfaces them again, the printer would never reach a
            // fixpoint. Skip members that a type declaration already owns by identity.
            var ownedMethods = new HashSet<MemberSyntax>(ReferenceEqualityComparer.Instance);
            foreach (var m in u.Members)
                if (m is DataDeclarationSyntax { Methods: { } methods })
                    foreach (var method in methods)
                        ownedMethods.Add(method);
            foreach (var m in u.Members)
            {
                if (ownedMethods.Contains(m)) continue;
                Member(m);
                _sb.Append('\n');
            }
        }

        // ---- Types ----
        void Type(TypeSyntax t)
        {
            switch (t)
            {
                case NamedTypeSyntax n: _sb.Append(n.Name); break;
                case GenericTypeSyntax g:
                    _sb.Append(g.Name).Append('<');
                    Join(g.Args, Type);
                    _sb.Append('>');
                    break;
                case TupleTypeSyntax tup:
                    _sb.Append('(');
                    Join(tup.Elements, Type);
                    _sb.Append(')');
                    break;
                case FunctionPointerTypeSyntax fp:
                    _sb.Append("&(");
                    Join(fp.ParameterTypes, Type);
                    if (fp.ReturnType is not NamedTypeSyntax { Name: "void" })
                    {
                        _sb.Append(" -> ");
                        Type(fp.ReturnType);
                    }
                    _sb.Append(')');
                    break;
                case NullableTypeSyntax nu: Type(nu.Inner); _sb.Append('?'); break;
                case PointerTypeSyntax pt:
                    _sb.Append(pt.ReadOnly ? "readonly *" : "*");
                    Type(pt.Inner);
                    break;
                case InferredTypeSyntax: _sb.Append("var"); break;
                default: _sb.Append("object"); break;
            }
        }

        // ---- Expressions ----
        void Expr(ExpressionSyntax e)
        {
            switch (e)
            {
                case LiteralExpressionSyntax lit: _sb.Append(lit.Text); break;
                case NameExpressionSyntax n: _sb.Append(n.Name); break;
                case UnaryExpressionSyntax u:
                    _sb.Append(OpText(u.OperatorKind));
                    if (u.OperatorKind is SyntaxTokenKind.NotKeyword) _sb.Append(' ');
                    Operand(u.Operand);
                    break;
                case BinaryExpressionSyntax b:
                    Operand(b.Left);
                    _sb.Append(' ').Append(OpText(b.OperatorKind)).Append(' ');
                    Operand(b.Right);
                    break;
                case MemberAccessExpressionSyntax m: Expr(m.Target); _sb.Append('.').Append(m.MemberName); break;
                case TypeTestExpressionSyntax tt:
                    Operand(tt.Operand); _sb.Append(tt.Negated ? " is not " : " is "); Type(tt.Type);
                    break;
                case CastExpressionSyntax ce:
                    Operand(ce.Operand); _sb.Append(ce.Asserting ? " as! " : " as "); Type(ce.Type);
                    break;
                case NullConditionalAccessExpressionSyntax m: Expr(m.Target); _sb.Append("?.").Append(m.MemberName); break;
                case CallExpressionSyntax call:
                    Expr(call.Target);
                    if (call.TypeArguments is { Count: > 0 } ta)
                    {
                        _sb.Append('<');
                        Join(ta, Type);
                        _sb.Append('>');
                    }
                    _sb.Append('(');
                    Join(call.Arguments, Expr);
                    _sb.Append(')');
                    break;
                case ObjectCreationExpressionSyntax oc:
                    Type(oc.Type);
                    _sb.Append(" { ");
                    Join(oc.Fields, f => { _sb.Append(f.Name).Append(": "); Expr(f.Value); });
                    _sb.Append(" }");
                    break;
                case WithExpressionSyntax w:
                    Expr(w.Target);
                    _sb.Append(" with { ");
                    Join(w.Fields, f => { _sb.Append(f.Name).Append(": "); Expr(f.Value); });
                    _sb.Append(" }");
                    break;
                case ParenthesizedExpressionSyntax pe: _sb.Append('('); Expr(pe.Expression); _sb.Append(')'); break;
                case ConditionalExpressionSyntax c:
                    Expr(c.Condition); _sb.Append(" ? "); Expr(c.Consequence);
                    _sb.Append(" : "); Expr(c.Alternative);
                    break;
                case NullCoalescingExpressionSyntax nc: Expr(nc.Left); _sb.Append(" ?? "); Expr(nc.Right); break;
                case ListLiteralExpressionSyntax l: _sb.Append('['); Join(l.Elements, Expr); _sb.Append(']'); break;
                case TupleExpressionSyntax tup: _sb.Append('('); Join(tup.Elements, Expr); _sb.Append(')'); break;
                case IndexExpressionSyntax ix: Expr(ix.Target); _sb.Append('['); Expr(ix.Index); _sb.Append(']'); break;
                case RangeExpressionSyntax r:
                    if (r.Target is not null) { Expr(r.Target); _sb.Append('['); }
                    if (r.Start is not null) Expr(r.Start);
                    _sb.Append("..");
                    if (r.End is not null) Expr(r.End);
                    if (r.Target is not null) _sb.Append(']');
                    break;
                case TryUnwrapExpressionSyntax tu: Expr(tu.Inner); _sb.Append('?'); break;
                case AwaitExpressionSyntax aw: _sb.Append("await "); Expr(aw.Inner); break;
                case AddressOfExpressionSyntax ao: _sb.Append('&'); Expr(ao.Target); break;
                case NewExpressionSyntax ne: _sb.Append("new "); Expr(ne.Target); break;
                case DefaultExpressionSyntax d:
                    if (d.Type is null) { _sb.Append("default"); }
                    else { _sb.Append("default("); Type(d.Type); _sb.Append(')'); }
                    break;
                case OutArgumentExpressionSyntax o: _sb.Append(o.DeclaresLocal ? "out var " : "out ").Append(o.Name); break;
                case DotCaseExpressionSyntax dc:
                    _sb.Append('.').Append(dc.CaseName);
                    if (dc.Arguments.Count > 0) { _sb.Append('('); Join(dc.Arguments, Expr); _sb.Append(')'); }
                    break;
                case ChanCreationExpressionSyntax ch:
                    _sb.Append("chan<"); Type(ch.ElementType); _sb.Append('>');
                    if (ch.Capacity is not null) { _sb.Append('('); Expr(ch.Capacity); _sb.Append(')'); }
                    break;
                case SpawnExpressionSyntax sp: _sb.Append("spawn "); Block(sp.Body); break;
                case FunctionLiteralExpressionSyntax fl:
                    _sb.Append("func(");
                    Join(fl.Parameters, Param);
                    _sb.Append(')');
                    if (fl.ReturnType is not NamedTypeSyntax { Name: "void" }) { _sb.Append(" -> "); Type(fl.ReturnType); }
                    _sb.Append(' ');
                    Block(fl.Body);
                    break;
                case MatchExpressionSyntax me: MatchBody("match ", me.Subject, me.SubjectType, me.Arms); break;
                case ErrorExpressionSyntax: _sb.Append("/*error*/"); break;
                default: _sb.Append("/*expr*/"); break;
            }
        }

        // ---- Statements ----
        void Stmt(StatementSyntax s)
        {
            switch (s)
            {
                case BlockStatementSyntax b: Block(b); break;
                case VariableDeclarationStatementSyntax v:
                    if (v.Representation == LocalRepresentation.BareTypedValue)
                        _sb.Append(v.Name);
                    else
                        _sb.Append(v.Mutable ? "var " : "let ").Append(v.Name);
                    if (v.ExplicitType is not null) { _sb.Append(": "); Type(v.ExplicitType); }
                    _sb.Append(" = "); Expr(v.Initializer);
                    break;
                case LetGuardStatementSyntax lg:
                    _sb.Append("let ").Append(lg.Name);
                    if (lg.ExplicitType is not null) { _sb.Append(": "); Type(lg.ExplicitType); }
                    _sb.Append(" = "); Expr(lg.Initializer); _sb.Append(" else "); Block(lg.ElseBody);
                    break;
                case ConstStatementSyntax cs:
                    _sb.Append("const ").Append(cs.Name);
                    if (cs.Type is not null) { _sb.Append(": "); Type(cs.Type); }
                    _sb.Append(" = "); Expr(cs.Value);
                    break;
                case ReturnStatementSyntax r:
                    _sb.Append("return");
                    if (r.Expression is not null) { _sb.Append(' '); Expr(r.Expression); }
                    break;
                case YieldStatementSyntax y: _sb.Append("yield "); Expr(y.Value); break;
                case MutYieldStatementSyntax y: _sb.Append("yield "); Expr(y.Location); break;
                case ThrowStatementSyntax t:
                    _sb.Append("throw");
                    if (t.Expression is not null) { _sb.Append(' '); Expr(t.Expression); }
                    break;
                case BreakStatementSyntax: _sb.Append("break"); break;
                case ContinueStatementSyntax: _sb.Append("continue"); break;
                case ExpressionStatementSyntax es: Expr(es.Expression); break;
                case AssignmentStatementSyntax a: Expr(a.Target); _sb.Append(" = "); Expr(a.Expression); break;
                case CompoundAssignmentStatementSyntax ca:
                    Expr(ca.Target); _sb.Append(' ').Append(OpText(ca.Operator)).Append(' '); Expr(ca.Value);
                    break;
                case IfStatementSyntax iff:
                    _sb.Append("if "); Expr(iff.Condition); _sb.Append(' '); StmtAsBlock(iff.ThenStatement);
                    if (iff.ElseStatement is not null) { _sb.Append(" else "); StmtAsBlock(iff.ElseStatement); }
                    break;
                case WhileStatementSyntax w: _sb.Append("while "); Expr(w.Condition); _sb.Append(' '); StmtAsBlock(w.Body); break;
                case ForEachStatementSyntax fe:
                    _sb.Append("for ").Append(fe.Identifier).Append(" in "); Expr(fe.Collection);
                    _sb.Append(' '); StmtAsBlock(fe.Body);
                    break;
                case DeferStatementSyntax d: _sb.Append("defer "); Block(d.Body); break;
                case RaiseStatementSyntax ra:
                    _sb.Append("raise ").Append(ra.EventName).Append('(');
                    Join(ra.Arguments, Expr); _sb.Append(')');
                    break;
                case AsyncLetStatementSyntax al:
                    _sb.Append("async let ").Append(al.Name);
                    if (al.ExplicitType is not null) { _sb.Append(": "); Type(al.ExplicitType); }
                    _sb.Append(" = "); Expr(al.Initializer);
                    break;
                case MatchStatementSyntax ms: MatchBody("match ", ms.Subject, ms.SubjectType, ms.Arms); break;
                case TryStatementSyntax tr: TryStmt(tr); break;
                case SelectStatementSyntax sel: SelectStmt(sel); break;
                default: _sb.Append("/*stmt*/"); break;
            }
        }

        // Defensively parenthesize a compound operand inside a binary/unary parent, so
        // a synthesized `(a + b) * c` tree never prints as `a + b * c`. Over-parenthesizes
        // (not minimal) but is always correct and stable under reparse — a parenthesized
        // node reprints as `(…)` and is not re-wrapped.
        void Operand(ExpressionSyntax e)
        {
            if (e is BinaryExpressionSyntax or ConditionalExpressionSyntax or NullCoalescingExpressionSyntax)
            {
                _sb.Append('(');
                Expr(e);
                _sb.Append(')');
            }
            else Expr(e);
        }

        void StmtAsBlock(StatementSyntax s)
        {
            if (s is BlockStatementSyntax b) Block(b);
            else Stmt(s);
        }

        void Block(BlockStatementSyntax b)
        {
            _sb.Append("{\n");
            _indent++;
            foreach (var s in b.Statements)
            {
                _sb.Append(Pad);
                Stmt(s);
                _sb.Append('\n');
            }
            _indent--;
            _sb.Append(Pad).Append('}');
        }

        void TryStmt(TryStatementSyntax tr)
        {
            _sb.Append("try "); Block(tr.Body);
            foreach (var cat in tr.Catches)
            {
                _sb.Append(" catch ");
                if (cat.ExceptionType is not null)
                {
                    _sb.Append('('); Type(cat.ExceptionType);
                    if (cat.BindingName is not null) _sb.Append(' ').Append(cat.BindingName);
                    _sb.Append(") ");
                }
                Block(cat.Body);
            }
        }

        void SelectStmt(SelectStatementSyntax sel)
        {
            _sb.Append("select {\n");
            _indent++;
            foreach (var arm in sel.Arms)
            {
                _sb.Append(Pad);
                switch (arm.Kind)
                {
                    case SelectArmKind.Recv:
                        _sb.Append(".recv(").Append(arm.Binding).Append(", "); Expr(arm.Channel!); _sb.Append(") ");
                        break;
                    case SelectArmKind.Send:
                        _sb.Append(".send("); Expr(arm.Value!); _sb.Append(", "); Expr(arm.Channel!); _sb.Append(") ");
                        break;
                    case SelectArmKind.Timeout:
                        _sb.Append(".timeout("); Expr(arm.Value!); _sb.Append(") ");
                        break;
                    default:
                        _sb.Append("default ");
                        break;
                }
                Block(arm.Body);
                _sb.Append('\n');
            }
            _indent--;
            _sb.Append(Pad).Append('}');
        }

        void MatchBody(string head, ExpressionSyntax subject, TypeSyntax? subjectType, IReadOnlyList<MatchArmSyntax> arms)
        {
            _sb.Append(head);
            if (subjectType is not null) { _sb.Append('('); Expr(subject); _sb.Append(": "); Type(subjectType); _sb.Append(')'); }
            else Expr(subject);
            _sb.Append(" {\n");
            _indent++;
            foreach (var arm in arms)
            {
                _sb.Append(Pad);
                Pattern(arm.Pattern);
                if (arm.Guard is not null) { _sb.Append(" if "); Expr(arm.Guard); }
                if (arm.ExprBody is not null) { _sb.Append(" => "); Expr(arm.ExprBody); }
                else if (arm.Body is not null) { _sb.Append(' '); Block(arm.Body); }
                _sb.Append('\n');
            }
            _indent--;
            _sb.Append(Pad).Append('}');
        }

        void Pattern(MatchPatternSyntax pat)
        {
            if (pat.IsDefault) { _sb.Append("default"); return; }
            if (pat.IsNil) { _sb.Append("nil"); return; }
            if (pat is { TypeBindingName: { } tbn, TypeBindingType: { } tbt })
            {
                _sb.Append('(').Append(tbn).Append(": "); Type(tbt); _sb.Append(')');
                return;
            }
            if (pat.LiteralValue is not null) { Expr(pat.LiteralValue); return; }
            _sb.Append('.').Append(pat.CaseName);
            if (pat.BindingNames is { Count: > 0 } bn)
                _sb.Append('(').Append(string.Join(", ", bn)).Append(')');
        }

        // ---- Members ----
        void Member(MemberSyntax m)
        {
            switch (m)
            {
                case FunctionDeclarationSyntax f: Function(f); break;
                case DataDeclarationSyntax d: Data(d); break;
                case ChoiceDeclarationSyntax ch: Choice(ch); break;
                case EnumDeclarationSyntax en: Enum(en); break;
                case InterfaceDeclarationSyntax i: Interface(i); break;
                case NamespaceInitDeclarationSyntax ni:
                    _sb.Append("init "); Block(ni.Body); break;
                case NamespaceStateDeclarationSyntax ns:
                    if (ns.IsPublic) _sb.Append("pub ");
                    if (ns.Property is not null)
                        _sb.Append(ns.Mutable ? "var " : "let ");
                    _sb.Append(ns.Name);
                    if (ns.Type is not InferredTypeSyntax) { _sb.Append(": "); Type(ns.Type); }
                    if (ns.Initializer is not null) { _sb.Append(" = "); Expr(ns.Initializer); }
                    else if (ns.Property is { ComputedGetter: { } getter, HasCustomGetter: false }) { _sb.Append(" => "); Expr(getter); }
                    else if (ns.Property is { } prop)
                    {
                        _sb.Append(" { ");
                        if (prop is { ComputedGetter: { } customGetter, HasCustomGetter: true })
                        {
                            _sb.Append("get => ");
                            Expr(customGetter);
                            _sb.Append(' ');
                        }
                        if (prop.SetterBody is { } setter)
                        {
                            _sb.Append("set(").Append(prop.SetterParam ?? "value").Append(") => ");
                            Expr(setter);
                        }
                        _sb.Append(" }");
                    }
                    break;
                case ConstDeclarationSyntax cd:
                    if (cd.IsPublic) _sb.Append("pub ");
                    _sb.Append("const ").Append(cd.Name);
                    if (cd.Type is not null) { _sb.Append(": "); Type(cd.Type); }
                    _sb.Append(" = "); Expr(cd.Value);
                    break;
                case DelegateDeclarationSyntax dg:
                    if (dg.IsPublic) _sb.Append("pub ");
                    _sb.Append("delegate func ").Append(dg.Name).Append('(');
                    Join(dg.Parameters, Param);
                    _sb.Append(") -> "); Type(dg.ReturnType);
                    break;
                case StaticFuncDeclarationSyntax sf: StaticFunc(sf); break;
                case ErrorMemberSyntax: _sb.Append("/*error*/"); break;
                default: _sb.Append("/*member*/"); break;
            }
        }

        // `dropReceiver` strips the leading synthetic `self` the parser prepends to a
        // type body's inline methods (DeclarationParser): the source form omits it and
        // the parser re-injects it, so printing it would both diverge from the source
        // and grow the parameter list by one `self` on every print/parse round-trip.
        void Directives(IReadOnlyList<CompilerDirectiveSyntax> directives)
        {
            foreach (var directive in directives)
            {
                _sb.Append('@').Append(directive.Name);
                if (directive.Arguments.Count > 0)
                {
                    _sb.Append('(');
                    for (var i = 0; i < directive.Arguments.Count; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        var argument = directive.Arguments[i];
                        _sb.Append(argument.Name).Append(": ").Append(argument.Value ? "true" : "false");
                    }
                    _sb.Append(')');
                }
                _sb.Append('\n').Append(Pad);
            }
        }

        void Function(FunctionDeclarationSyntax f, bool dropReceiver = false)
        {
            Directives(f.CompilerDirectives);
            foreach (var attr in f.Attributes) Attribute(attr);
            if (f.IsPublic) _sb.Append("pub ");
            switch (f.Modifier)
            {
                case FunctionModifier.Virtual: _sb.Append("virtual "); break;
                case FunctionModifier.Abstract: _sb.Append("abstract "); break;
                case FunctionModifier.InheritColon: _sb.Append(": "); break;
            }
            if (f.IsTaskFunc) _sb.Append("task ");
            _sb.Append("func ");
            if (f.ExplicitInterface is not null) { Type(f.ExplicitInterface); _sb.Append('.'); }
            _sb.Append(f.OperatorKind is { } op ? OpText(op) : f.Name);
            TypeParams(f.TypeParameters);
            _sb.Append('(');
            IReadOnlyList<ParameterSyntax> printedParams =
                dropReceiver && f.Parameters.Count > 0 ? f.Parameters.Skip(1).ToList() : f.Parameters;
            Join(printedParams, Param);
            _sb.Append(')');
            if (f.HasExplicitReturnType) { _sb.Append(" -> "); Type(f.ReturnType); }
            _sb.Append(' ');
            Block(f.Body);
        }

        void Data(DataDeclarationSyntax d)
        {
            Directives(d.CompilerDirectives);
            foreach (var attr in d.Attributes) Attribute(attr);
            if (d.DeriveTraits is { Count: > 0 } dt)
                _sb.Append("derive ").Append(string.Join(", ", dt)).Append('\n');
            if (d.IsPublic) _sb.Append("pub ");
            switch (d.Modifier)
            {
                case ClassModifier.Abstract: _sb.Append("abstract "); break;
                case ClassModifier.Open: _sb.Append("open "); break;
            }
            if (d.IsReadonly) _sb.Append("readonly ");
            _sb.Append(d.IsRef ? "class " : "struct ").Append(d.Name);
            TypeParams(d.TypeParameters);
            if (d.HeaderParameters is { Count: > 0 } header)
            {
                _sb.Append('(');
                Join(header, Param);
                _sb.Append(')');
            }
            if (d.Interfaces.Count > 0) { _sb.Append(" : "); Join(d.Interfaces, Type); }
            _sb.Append(" {\n");
            _indent++;
            foreach (var field in d.Fields) { _sb.Append(Pad); Field(field); _sb.Append('\n'); }
            if (d.Inits is not null)
                foreach (var ini in d.Inits) { _sb.Append(Pad); Init(ini); _sb.Append('\n'); }
            if (d.Methods is not null)
                foreach (var method in d.Methods) { _sb.Append(Pad); Function(method, dropReceiver: true); _sb.Append('\n'); }
            _indent--;
            _sb.Append("}");
        }

        void Choice(ChoiceDeclarationSyntax ch)
        {
            if (ch.IsPublic) _sb.Append("pub ");
            if (ch.IsRef) _sb.Append("ref ");
            _sb.Append("union ").Append(ch.Name);
            TypeParams(ch.TypeParameters);
            _sb.Append(" {\n");
            _indent++;
            foreach (var c in ch.Cases)
            {
                _sb.Append(Pad).Append(c.Name);
                if (c.Payloads.Count > 0) { _sb.Append('('); Join(c.Payloads, Field); _sb.Append(')'); }
                _sb.Append('\n');
            }
            _indent--;
            _sb.Append('}');
        }

        void Enum(EnumDeclarationSyntax en)
        {
            if (en.IsPublic) _sb.Append("pub ");
            _sb.Append("enum ").Append(en.Name).Append(" {\n");
            _indent++;
            foreach (var c in en.Cases)
            {
                _sb.Append(Pad).Append(c.Name);
                if (c.ExplicitValue is not null) _sb.Append(" = ").Append(c.ExplicitValue.Value);
                _sb.Append('\n');
            }
            _indent--;
            _sb.Append('}');
        }

        void Interface(InterfaceDeclarationSyntax i)
        {
            Directives(i.CompilerDirectives);
            if (i.IsPublic) _sb.Append("pub ");
            _sb.Append("interface ").Append(i.Name);
            TypeParams(i.TypeParameters);
            _sb.Append(" {\n");
            _indent++;
            foreach (var method in i.Methods)
            {
                _sb.Append(Pad).Append("func ").Append(method.Name).Append('(');
                Join(method.Parameters, Param);
                _sb.Append(") -> "); Type(method.ReturnType); _sb.Append('\n');
            }
            if (i.Events is { } events)
                foreach (var ev in events)
                {
                    _sb.Append(Pad).Append("event ").Append(ev.Name).Append(": ");
                    Type(ev.Type); _sb.Append('\n');
                }
            if (i.Properties is { } properties)
                foreach (var property in properties)
                {
                    _sb.Append(Pad).Append(property.HasSet ? "var " : "let ")
                        .Append(property.Name).Append(": ");
                    Type(property.Type);
                    _sb.Append(" {");
                    if (property.HasGet) _sb.Append(" get");
                    if (property.HasSet) _sb.Append(" set");
                    if (property.HasInit) _sb.Append(" init");
                    if (property.HasLoca) _sb.Append(" loca");
                    _sb.Append(" }\n");
                }
            _indent--;
            _sb.Append('}');
        }

        void StaticFunc(StaticFuncDeclarationSyntax sf)
        {
            if (sf.IsPublic) _sb.Append("pub ");
            _sb.Append("static ").Append(sf.Name);
            TypeParams(sf.GenericParameters);
            _sb.Append(" {\n");
            _indent++;
            foreach (var field in sf.Fields) { _sb.Append(Pad); Field(field); _sb.Append('\n'); }
            foreach (var fn in sf.Functions) { _sb.Append(Pad); Function(fn); _sb.Append('\n'); }
            _indent--;
            _sb.Append('}');
        }

        void Init(InitDeclarationSyntax init)
        {
            switch (init.Visibility)
            {
                case InitVisibility.Private: _sb.Append("priv "); break;
                case InitVisibility.Protected: _sb.Append("protected "); break;
            }
            _sb.Append("init(");
            Join(init.Parameters, Param);
            _sb.Append(')');
            if (init.ThisArguments is not null) { _sb.Append(" : this("); Join(init.ThisArguments, Expr); _sb.Append(')'); }
            else if (init.BaseArguments is not null) { _sb.Append(" : base("); Join(init.BaseArguments, Expr); _sb.Append(')'); }
            _sb.Append(' ');
            Block(init.Body);
        }

        void Field(FieldSyntax f)
        {
            if (f.IsPublic == true) _sb.Append("pub ");
            if (f.IsRequired) _sb.Append("required ");
            // Representation is source-significant: bare is a field, while
            // let/var are properties even when no accessor suffix is present.
            if (f.Property is not null)
                _sb.Append(f.Mutable ? "var " : "let ");
            _sb.Append(f.Name).Append(": ");
            Type(f.Type);
            if (f.DefaultValue is not null) { _sb.Append(" = "); Expr(f.DefaultValue); }
            else if (f.Property is { ComputedGetter: { } getter, HasCustomGetter: false }) { _sb.Append(" => "); Expr(getter); }
            else if (f.Property is { } prop && (prop.HasCustomGetter || prop.LocaStorageName is not null || prop.MutStorageName is not null || prop.ScopedMutBody is not null || prop.SetterBody is not null))
            {
                _sb.Append(" { ");
                if (prop is { ComputedGetter: { } customGetter, HasCustomGetter: true })
                {
                    _sb.Append("get => ");
                    Expr(customGetter);
                    _sb.Append(' ');
                }
                if (prop.LocaStorageName is { } loca) _sb.Append("loca => &self.").Append(loca).Append(' ');
                if (prop.MutStorageName is { } mut) _sb.Append("mut => &self.").Append(mut).Append(' ');
                if (prop.ScopedMutBody is { } scoped) { _sb.Append("mut "); Block(scoped); _sb.Append(' '); }
                if (prop.SetterBody is { } setter)
                {
                    _sb.Append("set(").Append(prop.SetterParam ?? "value").Append(") => ");
                    Expr(setter);
                }
                _sb.Append(" }");
            }
        }

        void Param(ParameterSyntax p)
        {
            if (p.IsOut) _sb.Append("out ");
            _sb.Append(p.Name).Append(": ");
            Type(p.Type);
            if (p.DefaultValue is not null) { _sb.Append(" = "); Expr(p.DefaultValue); }
        }

        void Attribute(AttributeSyntax attr)
        {
            _sb.Append('[').Append(attr.Name);
            if (attr.Arguments is not null) _sb.Append('(').Append(attr.Arguments).Append(')');
            _sb.Append("]\n").Append(Pad);
        }

        void TypeParams(IReadOnlyList<string> tps)
        {
            if (tps.Count == 0) return;
            _sb.Append('<').Append(string.Join(", ", tps)).Append('>');
        }

        // A node reached out of category context (rare) — best-effort fragment.
        string Fragment(SyntaxNode node) => node switch
        {
            ParameterSyntax p => $"{p.Name}: {PrintCanonical(p.Type)}",
            FieldSyntax f => $"{f.Name}: {PrintCanonical(f.Type)}",
            _ => "",
        };

        void Join<T>(IReadOnlyList<T> items, Action<T> each)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                each(items[i]);
            }
        }
    }

    /// Token-kind → source text for operators the canonical printer emits.
    static string OpText(SyntaxTokenKind kind) => kind switch
    {
        SyntaxTokenKind.Plus => "+",
        SyntaxTokenKind.Minus => "-",
        SyntaxTokenKind.Star => "*",
        SyntaxTokenKind.Slash => "/",
        SyntaxTokenKind.Percent => "%",
        SyntaxTokenKind.EqualsEquals => "==",
        SyntaxTokenKind.BangEquals => "!=",
        SyntaxTokenKind.Less => "<",
        SyntaxTokenKind.LessEquals => "<=",
        SyntaxTokenKind.Greater => ">",
        SyntaxTokenKind.GreaterEquals => ">=",
        SyntaxTokenKind.AmpAmp => "&&",
        SyntaxTokenKind.PipePipe => "||",
        SyntaxTokenKind.AndKeyword => "and",
        SyntaxTokenKind.OrKeyword => "or",
        SyntaxTokenKind.NotKeyword => "not",
        SyntaxTokenKind.Bang => "!",
        SyntaxTokenKind.Caret => "^",
        SyntaxTokenKind.DotDot => "..",
        SyntaxTokenKind.PlusEquals => "+=",
        SyntaxTokenKind.MinusEquals => "-=",
        SyntaxTokenKind.StarEquals => "*=",
        SyntaxTokenKind.SlashEquals => "/=",
        SyntaxTokenKind.Ampersand => "&",
        SyntaxTokenKind.Pipe => "|",
        SyntaxTokenKind.Tilde => "~",
        SyntaxTokenKind.ShiftLeft => "<<",
        SyntaxTokenKind.ShiftRight => ">>",
        SyntaxTokenKind.UnsignedShiftRight => ">>>",
        SyntaxTokenKind.PercentEquals => "%=",
        SyntaxTokenKind.AmpersandEquals => "&=",
        SyntaxTokenKind.PipeEquals => "|=",
        SyntaxTokenKind.CaretEquals => "^=",
        SyntaxTokenKind.ShiftLeftEquals => "<<=",
        SyntaxTokenKind.ShiftRightEquals => ">>=",
        SyntaxTokenKind.UnsignedShiftRightEquals => ">>>=",
        SyntaxTokenKind.At => "@",
        SyntaxTokenKind.QuestionQuestion => "??",
        _ => "?",
    };
}
