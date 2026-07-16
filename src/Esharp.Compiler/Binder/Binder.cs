using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;
using Esharp.FlowAnalysis;

namespace Esharp.Binder;

/// The binder facade and composition root — the binding-side `Parser`. It owns the
/// declaration passes (RegisterTypes → RegisterSignatures → BindUnit), the shared
/// `BindContext`, and wires the domain binders (expressions, statements, match,
/// declarations) and the `TypeResolver` that recurse into each other through it.
/// The public surface — `Bind`, `RegisterTypes`, `RegisterSignatures`, `BindUnit`,
/// `Diagnostics`, `Data` — is all any caller needs.
public sealed class Binder
{
    // Durable cross-file registries live on CompilationData; ALL transient
    // binding state (scope, current namespace/function, payload views, import
    // context) lives on the one shared BindContext the units and the
    // TypeResolver read. The Binder is the composition root: it owns the
    // declaration passes (RegisterTypes / RegisterSignatures / BindUnit) and
    // wires the domain binders that recurse into each other through it.
    readonly CompilationData _data;

    internal BindContext Ctx { get; }
    internal TypeResolver Types { get; }
    internal ExpressionBinder Expressions { get; }
    internal StatementBinder Statements { get; }
    internal MatchBinder Match { get; }
    internal DeclarationBinder Declarations { get; }
    internal NarrowingAnalyzer Narrowing { get; }

    public Binder() : this(new CompilationData()) { }

    public Binder(CompilationData data)
    {
        _data = data;
        Ctx = new BindContext(data);
        Types = new TypeResolver(Ctx);
        Expressions = new ExpressionBinder(this);
        Statements = new StatementBinder(this);
        Match = new MatchBinder(this);
        Declarations = new DeclarationBinder(this);
        Narrowing = new NarrowingAnalyzer(this);
        // Load the E#-authored stdlib into the AppDomain up front so reflective name
        // resolution sees `Esharp.Stdlib.*` for the whole compilation — in particular the
        // `Result.Ok`/`Result.Error` static factory class (`Esharp.Stdlib.Result`), whose
        // absence at bind time would type the factory call as `var` and drop the trailing
        // `.Value`/`.Error` accessor. Idempotent and process-cached; a no-op (seed-only
        // staging) when the stdlib dll isn't on disk.
        StdlibProbe.EnsureLoaded();
    }

    public CompilationData Data => _data;

    /// CLR-style arity-qualified registry key. Arity 0 → the bare name (static
    /// funcs, enums, non-generic data/choice/interface, delegates, and the C#
    /// adapters Workspace/ILEmit write/read by bare name all stay byte-for-byte
    /// unchanged); a generic type → `Name`N` (mirrors `ILEmitter.MetadataTypeName`
    /// and the CLR itself). This is what lets a generic `data Result`2` and an
    /// arity-0 `static Result` coexist under one registry — each use site
    /// resolves by the arity it demands.
    internal static string RegKey(string name, int arity) => arity > 0 ? $"{name}`{arity}" : name;

    public IReadOnlyList<Diagnostic> Diagnostics => _data.Diagnostics.Diagnostics;

    /// <summary>Single-file convenience: registers types then binds.</summary>
    public BoundCompilationUnit Bind(CompilationUnitSyntax syntax)
    {
        RegisterTypes(syntax);
        RegisterSignatures(syntax);
        FinalizeOperatorDeclarations();
        RegisterNamespaceStates(syntax);
        // The convenience path still represents a one-file compilation, so it
        // performs the same post-bind pointer realization as CompilationPipeline.
        // Multi-file compilation deliberately defers this until every unit is
        // bound; otherwise one file can decide `ref T` while a sibling emits the
        // callee as `__Ptr_T`.
        return PointerEscapeAnalysis.Run([BindUnit(syntax)], _data.Diagnostics)[0];
    }

    public void FinalizeOperatorDeclarations()
    {
        foreach (var owner in _data.Symbols.AllOfKind(TypeSymbolKind.Struct, TypeSymbolKind.Class))
        {
            var operators = owner.Members.Where(m => m.OperatorKind is not null).ToList();
            if (operators.Count == 0) continue;
            static string Signature(MethodSymbol m) => string.Join("|", m.OperatorParameterTypes.Select(BoundTypeDisplay.Name));

            foreach (var duplicate in operators.GroupBy(m => (m.OperatorKind, Sig: Signature(m))).Where(g => g.Count() > 1))
                foreach (var method in duplicate.Skip(1))
                    _data.Diagnostics.Report(method.Decl?.NameSpan ?? owner.Span,
                        $"ES2278: duplicate operator '{method.Name}' with operand signature ({string.Join(", ", method.OperatorParameterTypes.Select(BoundTypeDisplay.Name))}).");

            foreach (var method in operators)
            {
                var pair = method.OperatorKind switch
                {
                    SyntaxTokenKind.EqualsEquals => SyntaxTokenKind.BangEquals,
                    SyntaxTokenKind.BangEquals => SyntaxTokenKind.EqualsEquals,
                    SyntaxTokenKind.Less => SyntaxTokenKind.Greater,
                    SyntaxTokenKind.Greater => SyntaxTokenKind.Less,
                    SyntaxTokenKind.LessEquals => SyntaxTokenKind.GreaterEquals,
                    SyntaxTokenKind.GreaterEquals => SyntaxTokenKind.LessEquals,
                    _ => (SyntaxTokenKind?)null,
                };
                if (pair is not null && !operators.Any(candidate => candidate.OperatorKind == pair && Signature(candidate) == Signature(method)))
                    _data.Diagnostics.Report(method.Decl?.NameSpan ?? owner.Span,
                        $"ES2279: operator '{method.Name}' requires its matching '{OperatorGlyph(pair.Value)}' declaration with the same operand types.");
            }

            if (owner.Decl is DataDeclarationSyntax { DeriveTraits: { } traits }
                && traits.Contains("equality", StringComparer.Ordinal)
                && operators.Any(m => m.OperatorKind is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals))
                _data.Diagnostics.Report(owner.Span,
                    "ES2280: 'derive equality' conflicts with explicit ==/!= operator functions; choose one equality definition.");
        }
    }

    // The integral primitive names a CLR enum may be backed by. `int` is the default.
    static readonly HashSet<string> EnumUnderlyingPrimitives =
        new() { "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong" };

    // The primitive name for `enum C: T { … }`, defaulting to `int`. A non-integral or
    // otherwise unrecognized annotation is reported and falls back to `int`.
    string ResolveEnumUnderlying(EnumDeclarationSyntax decl)
    {
        if (decl.UnderlyingType is null) return "int";
        if (decl.UnderlyingType is NamedTypeSyntax { Name: var name } && EnumUnderlyingPrimitives.Contains(name))
            return name;
        _data.Diagnostics.Report(decl.NameSpan, DiagnosticDescriptors.EnumUnderlyingTypeInvalid,
            decl.Name, (decl.UnderlyingType as NamedTypeSyntax)?.Name ?? decl.UnderlyingType.ToString());
        return "int";
    }

    internal static string OperatorGlyph(SyntaxTokenKind op) => op switch
    {
        SyntaxTokenKind.Plus => "+", SyntaxTokenKind.Minus => "-", SyntaxTokenKind.Bang => "!", SyntaxTokenKind.Tilde => "~",
        SyntaxTokenKind.Star => "*", SyntaxTokenKind.Slash => "/", SyntaxTokenKind.Percent => "%",
        SyntaxTokenKind.Ampersand => "&", SyntaxTokenKind.Pipe => "|", SyntaxTokenKind.Caret => "^",
        SyntaxTokenKind.ShiftLeft => "<<", SyntaxTokenKind.ShiftRight => ">>", SyntaxTokenKind.UnsignedShiftRight => ">>>",
        SyntaxTokenKind.EqualsEquals => "==", SyntaxTokenKind.BangEquals => "!=",
        SyntaxTokenKind.Less => "<", SyntaxTokenKind.LessEquals => "<=", SyntaxTokenKind.Greater => ">", SyntaxTokenKind.GreaterEquals => ">=",
        _ => op.ToString(),
    };

    /// Register namespace-host let/var state after all call signatures exist but
    /// before any body binds. This is the namespace analogue of signature binding:
    /// every file sees the complete host-field scope.
    public void RegisterNamespaceStates(CompilationUnitSyntax syntax)
    {
        Ctx.CurrentNamespace = syntax.NamespaceName ?? "Main";
        SetCurrentUsings(syntax.Imports);
        Ctx.Scope = BinderScope.Root();
        if (!_data.NamespaceStateScopes.TryGetValue(Ctx.CurrentNamespace, out var known))
            _data.NamespaceStateScopes[Ctx.CurrentNamespace] = known = new(StringComparer.Ordinal);
        foreach (var (name, state) in known)
            Ctx.Scope.Declare(name, state.Type, state.Mutable);

        foreach (var declaration in syntax.Members.OfType<NamespaceStateDeclarationSyntax>())
        {
            if (known.ContainsKey(declaration.Name))
            {
                _data.Diagnostics.Report(declaration.Span,
                    $"ES2209: namespace '{Ctx.CurrentNamespace}' already declares state named '{declaration.Name}'.");
                continue;
            }
            var bound = Declarations.BindNamespaceState(declaration);
            _data.NamespaceStateDeclarations[declaration] = bound;
            known[declaration.Name] = (bound.Field.Type, bound.Field.Mutable);
            Ctx.Scope.Declare(declaration.Name, bound.Field.Type, bound.Field.Mutable);
            var host = _data.Symbols.Host(Ctx.CurrentNamespace);
            var stateSymbol = new FieldSymbol
            {
                Name = declaration.Name,
                DeclaringType = host,
                Bound = bound.Field.Type,
                IsPublic = declaration.Visibility == Syntax.Visibility.Public,
                Visibility = declaration.Visibility,
                DeclaredMutable = declaration.Mutable,
                Mutable = bound.Field.IsProperty ? bound.Field.PropHasSet : bound.Field.Mutable,
                IsProperty = bound.Field.IsProperty,
                HasDurablePropertyLocation = bound.Field.IsProperty && !bound.Field.IsComputedProperty
                    && (bound.Field.PropHasExplicitLoca
                        || bound.Field.PropHasMut && bound.Field.ScopedMut is null
                        || !bound.Field.PropHasMut && !bound.Field.PropHasCustomSetter),
                HasScopedPropertyLocation = bound.Field.ScopedMut is not null,
                HasCustomPropertySetter = bound.Field.PropHasCustomSetter,
                Span = declaration.NameSpan,
            };
            host.AddField(stateSymbol);
            _data.Sink.OnFieldResolved(stateSymbol, declaration.NameSpan,
                Semantics.SymbolOccurrence.Declaration);
            if (bound.Field.IsProperty && !bound.Field.IsComputedProperty)
            {
                if (!_data.NamespaceInitWritableProperties.TryGetValue(Ctx.CurrentNamespace, out var writable))
                    _data.NamespaceInitWritableProperties[Ctx.CurrentNamespace] = writable = new(StringComparer.Ordinal);
                writable.Add(bound.Field.Name);
            }
        }
    }

    /// <summary>Pass 1: register all type and function declarations into shared registries.
    /// Call this for every file before calling BindUnit on any file.</summary>
    public void RegisterTypes(CompilationUnitSyntax syntax)
    {
        var ns = syntax.NamespaceName ?? "Main";
        _data.Symbols.Host(ns); // interning the host also registers the namespace
        foreach (var member in syntax.Members)
        {
            switch (member)
            {
                case DataDeclarationSyntax data:
                    var dataArity = data.TypeParameters.Count;
                    var dataSym = _data.Symbols.GetOrAdd(data.Name, dataArity,
                        data.IsRef ? TypeSymbolKind.Class : TypeSymbolKind.Struct, ns,
                        // Seed the CLR form: class is a class by definition; value
                        // data starts struct and may be revised by the promotion pass.
                        data.IsRef ? DataClassification.Class : DataClassification.Struct,
                        decl: data);
                    // A static facet may have been registered from another file
                    // first. The same identity now gains its instance facet; it is
                    // still one CLR type, but ordinary receivers select this side.
                    dataSym.TypeKind = data.IsRef ? TypeSymbolKind.Class : TypeSymbolKind.Struct;
                    dataSym.Decl = data;
                    dataSym.BoundView = new DataType(data.Name, data.TypeParameters, data, Namespace: ns) { Symbol = dataSym };
                    RegisterTypeNamespace(data.Name, ns, data.Span);
                    _data.Sink.OnTypeResolved(dataSym, data.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    RegisterNestedTypes(data.NestedTypes, dataSym);
                    break;
                case ChoiceDeclarationSyntax choice:
                    var choiceSym = _data.Symbols.GetOrAdd(choice.Name, choice.TypeParameters.Count,
                        choice.IsRef ? TypeSymbolKind.RefUnion : TypeSymbolKind.Union, ns, decl: choice);
                    choiceSym.BoundView = new ChoiceType(choice.Name, choice.TypeParameters, choice, choice.IsRef) { Symbol = choiceSym };
                    RegisterTypeNamespace(choice.Name, ns, choice.Span);
                    _data.Sink.OnTypeResolved(choiceSym, choice.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var enumSym = _data.Symbols.GetOrAdd(enumDecl.Name, 0, TypeSymbolKind.Enum, ns, decl: enumDecl);
                    enumSym.BoundView = new EnumType(enumDecl.Name, enumDecl) { Symbol = enumSym };
                    RegisterTypeNamespace(enumDecl.Name, ns, enumDecl.Span);
                    _data.Sink.OnTypeResolved(enumSym, enumDecl.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case InterfaceDeclarationSyntax proto:
                    var protoSym = _data.Symbols.GetOrAdd(proto.Name, proto.TypeParameters.Count, TypeSymbolKind.Interface, ns, decl: proto);
                    protoSym.BoundView = new InterfaceType(proto.Name, proto) { Symbol = protoSym };
                    RegisterTypeNamespace(proto.Name, ns, proto.Span);
                    _data.Sink.OnTypeResolved(protoSym, proto.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case DelegateDeclarationSyntax del:
                    var delSym = _data.Symbols.GetOrAdd(del.Name, 0, TypeSymbolKind.Delegate, ns, decl: del);
                    delSym.BoundView = new NamedDelegateType(del.Name, ns, del) { Symbol = delSym };
                    RegisterTypeNamespace(del.Name, ns, del.Span);
                    _data.Sink.OnTypeResolved(delSym, del.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case StaticFuncDeclarationSyntax sfn:
                    // Static facets are declaration-backed. A same-name, same-arity
                    // instance declaration shares this symbol/CLR type; a standalone
                    // facet remains an abstract/sealed static CLR type.
                    var sfnArity = sfn.GenericParameters.Count;
                    var sfnTypeSym = _data.Symbols.TryGet(sfn.Name, sfnArity, ns)
                        ?? _data.Symbols.GetOrAdd(sfn.Name, sfnArity, TypeSymbolKind.StaticFunc, ns, decl: sfn);
                    _data.Symbols.RegisterStaticFacet(sfnTypeSym, sfn);
                    if (!sfnTypeSym.HasInstanceFacet)
                    {
                        sfnTypeSym.TypeKind = TypeSymbolKind.StaticFunc;
                        sfnTypeSym.BoundView = new StaticFuncType(sfn.Name, sfn, sfn.GenericParameters) { Symbol = sfnTypeSym };
                    }
                    RegisterTypeNamespace(sfn.Name, ns, sfn.Span);
                    _data.Sink.OnTypeResolved(sfnTypeSym, sfn.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    RegisterNestedTypes(sfn.NestedTypes, sfnTypeSym);
                    // Member symbols (with resolved signatures + async facts) are
                    // created in RegisterSignatures, after every type is registered.
                    break;
                case FunctionDeclarationSyntax:
                    // Function symbols are created in RegisterSignatures (Pass 1b),
                    // after every type from every file is registered.
                    break;
            }
        }
    }

    /// Intern the symbols for NESTED type declarations under <paramref name="declaring"/>,
    /// recursively. Mirrors the top-level RegisterTypes switch but keys each symbol by the
    /// declaring chain (so a nested `Inner` never collides with a top-level one) and records
    /// DeclaringType. The simple name is indexed too, so a bare reference inside the
    /// enclosing scope resolves. Run in RegisterTypes so every nested type is interned
    /// before any signature/body binds.
    void RegisterNestedTypes(IReadOnlyList<MemberSyntax>? nested, TypeSymbol declaring)
    {
        if (nested is null) return;
        var ns = declaring.Namespace;
        foreach (var member in nested)
        {
            switch (member)
            {
                case DataDeclarationSyntax data:
                    var dataSym = _data.Symbols.GetOrAddNested(data.Name, data.TypeParameters.Count,
                        data.IsRef ? TypeSymbolKind.Class : TypeSymbolKind.Struct, declaring,
                        data.IsRef ? DataClassification.Class : DataClassification.Struct, decl: data);
                    dataSym.BoundView = new DataType(data.Name, data.TypeParameters, data, Namespace: ns) { Symbol = dataSym };
                    RegisterTypeNamespace(data.Name, ns ?? "Main", data.Span);
                    _data.Sink.OnTypeResolved(dataSym, data.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    RegisterNestedTypes(data.NestedTypes, dataSym);
                    break;
                case ChoiceDeclarationSyntax choice:
                    var choiceSym = _data.Symbols.GetOrAddNested(choice.Name, choice.TypeParameters.Count,
                        choice.IsRef ? TypeSymbolKind.RefUnion : TypeSymbolKind.Union, declaring, decl: choice);
                    choiceSym.BoundView = new ChoiceType(choice.Name, choice.TypeParameters, choice, choice.IsRef) { Symbol = choiceSym };
                    RegisterTypeNamespace(choice.Name, ns ?? "Main", choice.Span);
                    _data.Sink.OnTypeResolved(choiceSym, choice.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var enumSym = _data.Symbols.GetOrAddNested(enumDecl.Name, 0, TypeSymbolKind.Enum, declaring, decl: enumDecl);
                    enumSym.BoundView = new EnumType(enumDecl.Name, enumDecl) { Symbol = enumSym };
                    RegisterTypeNamespace(enumDecl.Name, ns ?? "Main", enumDecl.Span);
                    _data.Sink.OnTypeResolved(enumSym, enumDecl.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case InterfaceDeclarationSyntax proto:
                    var protoSym = _data.Symbols.GetOrAddNested(proto.Name, proto.TypeParameters.Count, TypeSymbolKind.Interface, declaring, decl: proto);
                    protoSym.BoundView = new InterfaceType(proto.Name, proto) { Symbol = protoSym };
                    RegisterTypeNamespace(proto.Name, ns ?? "Main", proto.Span);
                    _data.Sink.OnTypeResolved(protoSym, proto.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case DelegateDeclarationSyntax del:
                    var delSym = _data.Symbols.GetOrAddNested(del.Name, 0, TypeSymbolKind.Delegate, declaring, decl: del);
                    delSym.BoundView = new NamedDelegateType(del.Name, ns, del) { Symbol = delSym };
                    RegisterTypeNamespace(del.Name, ns ?? "Main", del.Span);
                    _data.Sink.OnTypeResolved(delSym, del.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    break;
                case StaticFuncDeclarationSyntax sfn:
                    var sfnArity = sfn.GenericParameters.Count;
                    var sfnSym = _data.Symbols.TryGetNested(sfn.Name, sfnArity, declaring)
                        ?? _data.Symbols.GetOrAddNested(sfn.Name, sfnArity, TypeSymbolKind.StaticFunc, declaring, decl: sfn);
                    _data.Symbols.RegisterStaticFacet(sfnSym, sfn);
                    if (!sfnSym.HasInstanceFacet)
                        sfnSym.BoundView = new StaticFuncType(sfn.Name, sfn, sfn.GenericParameters) { Symbol = sfnSym };
                    RegisterTypeNamespace(sfn.Name, ns ?? "Main", sfn.Span);
                    _data.Sink.OnTypeResolved(sfnSym, sfn.NameSpan, Semantics.SymbolOccurrence.Declaration);
                    RegisterNestedTypes(sfn.NestedTypes, sfnSym);
                    break;
            }
        }
    }

    /// The CLR metadata simple-name segment for a type — `Name` or `` Name`arity ``.
    /// The nested-parent key both legs agree on (binder sets DeclaringTypeKey to the
    /// parent's; the emitter registers each shell under the same).
    public static string NestedParentKey(string name, int arity) => arity > 0 ? $"{name}`{arity}" : name;

    /// Pass 4 for NESTED types: bind each nested declaration and append it to the
    /// unit's bound members with DeclaringTypeKey set to the enclosing type's key, so
    /// the emitter re-parents it into the enclosing Cecil type. Recurses for deeper
    /// nesting. (Nested-type instance methods are not yet collected — a nested `data`
    /// binds with an empty method set; its fields/cases are fully supported.)
    void BindNestedTypes(IReadOnlyList<MemberSyntax>? nested, TypeSymbol? declaring, List<BoundMember> output, string? namespaceName)
    {
        if (nested is null || declaring is null) return;
        var parentKey = NestedParentKey(declaring.Name, declaring.Arity);
        foreach (var member in nested)
        {
            switch (member)
            {
                case DataDeclarationSyntax data:
                    var dsym = _data.Symbols.TryGetNested(data.Name, data.TypeParameters.Count, declaring);
                    Types.CurrentEnclosingType = dsym;
                    output.Add(Declarations.BindData(data, [], null, dsym) with { DeclaringTypeKey = parentKey, Visibility = data.Visibility });
                    Types.CurrentEnclosingType = null;
                    BindNestedTypes(data.NestedTypes, dsym, output, namespaceName);
                    break;
                case ChoiceDeclarationSyntax choice:
                    output.Add(Declarations.BindChoice(choice) with { DeclaringTypeKey = parentKey, Visibility = choice.Visibility });
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var resolvedCases = new List<BoundEnumCase>();
                    int nextEnumValue = 0;
                    foreach (var c in enumDecl.Cases)
                    {
                        int value = c.ExplicitValue ?? nextEnumValue;
                        resolvedCases.Add(new BoundEnumCase(c.Name, value));
                        nextEnumValue = value + 1;
                    }
                    output.Add(new BoundEnumDeclaration(enumDecl.IsPublic, enumDecl.Name, resolvedCases, parentKey, ResolveEnumUnderlying(enumDecl)) { Visibility = enumDecl.Visibility });
                    break;
                case InterfaceDeclarationSyntax proto:
                    output.Add(Declarations.BindProtocol(proto) with { DeclaringTypeKey = parentKey, Visibility = proto.Visibility });
                    break;
                case DelegateDeclarationSyntax del:
                    output.Add(Declarations.BindDelegate(del, namespaceName) with { DeclaringTypeKey = parentKey, Visibility = del.Visibility });
                    break;
                case StaticFuncDeclarationSyntax sfn:
                    var ssym = _data.Symbols.TryGetNested(sfn.Name, 0, declaring);
                    Types.CurrentEnclosingType = ssym;
                    output.Add(Declarations.BindStaticFunc(sfn) with { DeclaringTypeKey = parentKey, Visibility = sfn.Visibility });
                    Types.CurrentEnclosingType = null;
                    BindNestedTypes(sfn.NestedTypes, ssym, output, namespaceName);
                    break;
            }
        }
    }

    /// Record a type's declaring namespace, tracking every namespace a simple name
    /// appears in so the resolver can flag a cross-namespace collision.
    void RegisterTypeNamespace(string name, string ns, SourceSpan span)
    {
        // Type names must be PascalCase. The parser uses an uppercase initial to tell
        // a composite literal / qualified construction (`Foo { ... }`, `NS.Foo { ... }`)
        // apart from a value's member-access-then-block (`r.Error { ... }` is a match
        // body, not an initializer). A camelCase / snake_case type name would silently
        // never construct via that path and could collide with a local binding — reject
        // it at the declaration instead of letting it misparse downstream.
        if (name.Length > 0 && char.IsLower(name[0]))
            _data.Diagnostics.Report(span,
                $"ES2160: type name '{name}' must be PascalCase (start with an uppercase letter) — " +
                $"lower-case type names collide with value/local syntax and cannot be constructed with `{name} {{ ... }}`.");

        // The namespace index itself is fed by SymbolTable.GetOrAdd at interning.
    }

    /// True when a receiver `data`/`class` type named <paramref name="receiverTypeName"/>
    /// is declared in the namespace currently being bound — i.e. a first-param-`T` function in
    /// this unit promotes onto it. Promotion is namespace-local (spec: Functions →
    /// instance-method promotion): a function only attaches to a type declared in its own
    /// namespace. A receiver type from another namespace (or an unknown one) does not promote,
    /// so the function stays a free static member of this namespace's host class.
    bool PromotesInCurrentNamespace(string receiverTypeName) =>
        // Any-declaration check (not last-write-wins): the type is local when SOME
        // declaration of this simple name lives in the current namespace.
        _data.Symbols.NamespacesOf(receiverTypeName).Contains(Ctx.CurrentNamespace);

    /// Receiverless free functions must be camelCase (start with a lower-case
    /// letter). This is the dual of the PascalCase type rule (ES2160): the value/type
    /// distinction is carried in the surface case, so a bare uppercase name is always
    /// a type. A function with a *receiver* (first parameter is a `data` type, value
    /// or `*T`) is a method — it is called via `.` or dispatched through an interface
    /// slot, never as a bare name, so it is exempt and MAY be PascalCase to match a
    /// .NET interface member (`Dispose`, `CompareTo`, …). `static Name` declares
    /// a type and is checked as Pascal elsewhere. Run from RegisterSignatures, after
    /// every type is registered, so the receiver test sees a complete DataDecls.
    void CheckFunctionNameCasing(FunctionDeclarationSyntax fn)
    {
        if (fn.OperatorKind is not null) return;
        if (fn.Name.Length == 0 || !char.IsUpper(fn.Name[0])) return;
        if (HasDataReceiver(fn)) return; // method: name may match an interface member
        _data.Diagnostics.Report(fn.Span,
            $"ES2161: function name '{fn.Name}' must be camelCase (start with a lower-case letter) — " +
            $"Pascal-case names are reserved for types, so a bare `{fn.Name}` would read as a type. " +
            $"(Methods with a receiver are exempt — they may match a .NET interface member.)");
    }

    void ValidateOperatorDeclaration(FunctionDeclarationSyntax fn, TypeSymbol? owner, int operandOffset)
    {
        if (fn.OperatorKind is not { } op) return;
        var operands = fn.Parameters.Skip(operandOffset).ToList();
        if (owner is null || !owner.HasStaticFacet
            || owner.TypeKind is not (TypeSymbolKind.Struct or TypeSymbolKind.Class))
            _data.Diagnostics.Report(fn.NameSpan,
                $"ES2270: operator function '{fn.Name}' must belong to a companion static facet for an instance class or struct.");
        if (fn.IsTypeBodyMethod || (fn.Receiver is not null && fn.Receiver.IsStaticFacet == false))
            _data.Diagnostics.Report(fn.NameSpan,
                $"ES2270: operator function '{fn.Name}' cannot be an instance method; place it in 'static T {{ ... }}' or attach it with a 'static T' receiver.");
        if (fn.TypeParameters.Count != 0)
            _data.Diagnostics.Report(fn.NameSpan, "ES2271: operator functions cannot declare method type parameters; use the owning type's parameters.");
        if (fn.IsTaskFunc || DeclarationBinder.FunctionBodyHasAwait(fn) || DeclarationBinder.FunctionBodyHasYield(fn))
            _data.Diagnostics.Report(fn.NameSpan, "ES2271: operator functions must be synchronous and cannot await or yield.");
        if (!fn.HasExplicitReturnType || Types.ResolveHeapPointerAware(fn.ReturnType) is VoidType)
            _data.Diagnostics.Report(fn.NameSpan, "ES2272: operator functions require an explicit non-void return type.");

        var unaryOnly = op is SyntaxTokenKind.Bang or SyntaxTokenKind.Tilde;
        var binaryOnly = op is not (SyntaxTokenKind.Plus or SyntaxTokenKind.Minus or SyntaxTokenKind.Bang or SyntaxTokenKind.Tilde);
        var validArity = unaryOnly ? 1 : binaryOnly ? 2 : operands.Count is 1 or 2 ? operands.Count : -1;
        if (validArity < 0 || operands.Count != validArity)
            _data.Diagnostics.Report(fn.NameSpan,
                $"ES2273: operator '{fn.Name}' requires {(unaryOnly ? "one" : binaryOnly ? "two" : "one or two")} explicit operand(s).");

        foreach (var p in operands)
            if (p.IsOut || p.DefaultValue is not null || p.Type is PointerTypeSyntax)
                _data.Diagnostics.Report(p.NameSpan,
                    "ES2274: operator operands cannot be out, by-reference, pointer, or optional parameters.");

        if (owner is not null && operands.Count > 0)
        {
            var ownsOperand = operands.Select(p => Types.ResolveHeapPointerAware(p.Type)).Any(t => t switch
            {
                DataType d => ReferenceEquals(d.Symbol, owner) || (d.Name == owner.Name && d.TypeParameters.Count == owner.Arity),
                _ => false,
            });
            if (!ownsOperand)
                _data.Diagnostics.Report(fn.NameSpan,
                    $"ES2275: at least one operand of '{fn.Name}' must have the owning type '{owner.Name}'.");
        }

        if (op is SyntaxTokenKind.ShiftLeft or SyntaxTokenKind.ShiftRight or SyntaxTokenKind.UnsignedShiftRight
            && operands.Count == 2
            && Types.ResolveHeapPointerAware(operands[1].Type) is not PrimitiveType { Name: "int" })
            _data.Diagnostics.Report(operands[1].NameSpan, "ES2276: a shift operator's right operand must be int.");
        if (op is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals or
            SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals
            && Types.ResolveHeapPointerAware(fn.ReturnType) is not PrimitiveType { Name: "bool" })
            _data.Diagnostics.Report(fn.NameSpan, "ES2277: equality and ordering operators must return bool.");
    }

    /// True when a function's first parameter is a user `data` type (value or `*T`),
    /// making it a promoted method rather than a receiverless free function.
    // The camelCase rule applies only to FREE functions. A method is exempt — it is
    // dispatched via `.` or an interface slot, never as a bare name, so it may be
    // PascalCase to match a .NET member. A function is a method when it has a Go-style
    // receiver block (`func (c: T) m()`) OR its first parameter is a data type — the
    // latter covers inline type-body methods (the parser prepends a `self: Owner`
    // parameter, surfaced here) and is harmless for free functions.
    bool HasDataReceiver(FunctionDeclarationSyntax fn) =>
        fn.Receiver is not null
        || (fn.Parameters.Count > 0
            && _data.Symbols.DataDecl(TypeResolver.TypeSyntaxLeafName(fn.Parameters[0].Type)) is not null);

    /// <summary>Pass 1b: resolve every function's call-site return type, run after
    /// RegisterTypes has completed for *all* units. Resolving here (rather than
    /// inline during registration) means a function whose return type names a type
    /// declared later in the file — or in another file — resolves it correctly
    /// instead of erasing the type argument to `object`
    /// (e.g. `-> Result&lt;int, DbError&gt;` keeps `DbError`).</summary>
    public void RegisterSignatures(CompilationUnitSyntax syntax)
    {
        Ctx.CurrentNamespace = syntax.NamespaceName ?? "Main";
        SetCurrentUsings(syntax.Imports);

        // Inline type-body methods are parser-hoisted with a placeholder
        // `self: TypeName`. Keep their declaration owner by reference so
        // same-name arities (`Spawned` and `Spawned<T>`) do not recover the
        // receiver through a bare-name lookup.
        var typeBodyOwners = new Dictionary<FunctionDeclarationSyntax, TypeSymbol>(ReferenceEqualityComparer.Instance);
        foreach (var data in syntax.Members.OfType<DataDeclarationSyntax>())
        {
            var owner = _data.Symbols.TryGet(data.Name, data.TypeParameters.Count, Ctx.CurrentNamespace);
            if (owner is null) continue;
            foreach (var method in data.Methods ?? [])
                typeBodyOwners[method] = owner;
        }
        foreach (var member in syntax.Members)
        {
            switch (member)
            {
                case DataDeclarationSyntax data:
                    PopulateDataSymbol(data);
                    PopulateNestedSignatures(data.NestedTypes, _data.Symbols.TryGet(data.Name, data.TypeParameters.Count, Ctx.CurrentNamespace));
                    break;
                case ChoiceDeclarationSyntax choice:
                    PopulateChoiceSymbol(choice);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    PopulateEnumSymbol(enumDecl);
                    break;
                case InterfaceDeclarationSyntax proto:
                    PopulateInterfaceSymbol(proto);
                    break;
                case StaticFuncDeclarationSyntax sfn:
                    BindStaticFuncMemberSignatures(sfn, _data.Symbols.TryGet(sfn.Name, sfn.GenericParameters.Count, Ctx.CurrentNamespace));
                    PopulateNestedSignatures(sfn.NestedTypes, _data.Symbols.TryGet(sfn.Name, sfn.GenericParameters.Count, Ctx.CurrentNamespace));
                    break;
                case FunctionDeclarationSyntax func:
                    {
                        CheckFunctionNameCasing(func);
                        if (func.OperatorKind is not null && func.Receiver?.IsStaticFacet != true)
                            ValidateOperatorDeclaration(func, null, 0);
                        var effRet = func.ReturnType;
                        if (!func.HasExplicitReturnType && func.Parameters.Count > 0)
                        {
                            // Look up DefaultReturns on the first-param's data type, if any.
                            var firstName = TypeResolver.TypeSyntaxLeafName(func.Parameters[0].Type);
                            DataDeclarationSyntax? owner = null;
                            foreach (var m in syntax.Members)
                                if (m is DataDeclarationSyntax d && d.Name == firstName) { owner = d; break; }
                            owner ??= _data.Symbols.DataDecl(firstName);
                            if (owner?.DefaultReturns is not null)
                                effRet = owner.DefaultReturns.Type;
                        }
                        BoundType callerReturn = Types.ResolveHeapPointerAware(effRet);
                        // `task func` wraps the call-site return type in the E# stdlib
                        // handle: void → Spawned, T → Spawned<T>. The body itself still
                        // returns its unwrapped T directly.
                        if (func.IsTaskFunc)
                            callerReturn = callerReturn is VoidType
                                ? new ExternalType("Spawned")
                                : new ExternalType("Spawned", [callerReturn]);
                        // Attach the promoted-method symbol to its receiver type's spine
                        // now (signature time), before any body binds and asks about it.
                        // Symbol spine: EVERY function has a symbol. A promoted
                        // function's symbol lives on its receiver (just attached);
                        // anything else is a free function on the namespace host.
                        if (RegisterPromotedMethodSymbol(func, callerReturn,
                                typeBodyOwners.GetValueOrDefault(func)) is { } promotedSym)
                        {
                            // Static-facet methods are deliberately reachable only
                            // through `Foo.method()`, never as a bare free function.
                            if (promotedSym.ReceiverKind != ReceiverKind.Static)
                                _data.Symbols.AddFunction(func.Name, promotedSym);
                        }
                        else
                        {
                            var host = _data.Symbols.Host(Ctx.CurrentNamespace);
                            var freeIsAsync = DeclarationBinder.FunctionBodyHasAwait(func);
                            var freeSym = new MethodSymbol
                            {
                                Name = func.Name,
                                DeclaredArity = func.Parameters.Count,
                                IsStatic = true,
                                DeclaringType = host,
                                Decl = func,
                                ReturnType = callerReturn,
                                IsAsync = freeIsAsync,
                                HasExplicitAsyncWrapperReturn = freeIsAsync && DeclarationBinder.HasExplicitAsyncWrapperReturn(func),
                                DeclaredParameters = func.Parameters.Select(p => MapTypeRef(Types.ResolveHeapPointerAware(p.Type))).ToList(),
                                DeclaredReturn = MapTypeRef(callerReturn),
                                TypeParameters = func.TypeParameters,
                                OperatorKind = func.OperatorKind,
                                OperatorParameterTypes = func.Parameters.Select(p => Types.ResolveHeapPointerAware(p.Type)).ToList(),
                            };
                            _data.Symbols.AddFunction(func.Name, freeSym);
                            host.AddMember(freeSym);
                            _data.Sink.OnMethodResolved(freeSym, func.NameSpan, Semantics.SymbolOccurrence.Declaration);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Passes 2-4: bind functions, determine instance methods, bind types.
    /// All types must be registered (via RegisterTypes) before calling this.</summary>
    public BoundCompilationUnit BindUnit(CompilationUnitSyntax syntax)
    {
        Ctx.CurrentNamespace = syntax.NamespaceName ?? "Main";
        SetCurrentUsings(syntax.Imports);
        Ctx.Scope = BinderScope.Root();
        if (_data.NamespaceStateScopes.TryGetValue(Ctx.CurrentNamespace, out var namespaceState))
            foreach (var (name, state) in namespaceState)
                Ctx.Scope.Declare(name, state.Type, state.Mutable);

        // (Field types were pre-bound at signature time — PopulateDataSymbol resolved
        // every data declaration's fields onto its TypeSymbol, per (ns, name, arity),
        // so object creation in Pass 2 sees embedded fields without a per-unit
        // pre-pass and same-name/different-arity declarations never collide.)

        // Member-emission seam: synthesize members from each data type's read-only
        // projection BEFORE function binding, so a synthesized method is a real
        // member symbol on its receiver — visible to call resolution (`b.answer()`)
        // and member-access authority in Pass 2, and joined into the instance-method
        // set at Pass 4 so interface satisfaction and the emitters see it like a
        // promoted method. Hygienic by construction: the seam returns members for
        // the one type it was handed.
        // Method ownership is nominal: `Spawned` and `Spawned<T>` are distinct
        // declarations even though their source base name is the same. Keep every
        // per-type worklist arity-qualified so their methods never get merged.
        static string TypeWorkKey(string name, int arity) => RegKey(name, arity);
        var synthesizedByType = new Dictionary<string, List<BoundFunctionDeclaration>>(StringComparer.Ordinal);
        if (_data.MemberSynthesizer is { } synth)
        {
            foreach (var data in syntax.Members.OfType<DataDeclarationSyntax>())
            {
                if (_data.Symbols.TryGet(data.Name, data.TypeParameters.Count, Ctx.CurrentNamespace) is not { } dataSym)
                    continue;
                var synthesized = synth.SynthesizeMembers(Symbols.TypeInfo.From(dataSym));
                if (synthesized.Count == 0) continue;
                synthesizedByType[TypeWorkKey(data.Name, data.TypeParameters.Count)] = [.. synthesized];
                foreach (var m in synthesized)
                    RegisterSynthesizedMember(dataSym, m);
            }
        }

        // Pass 1.9: fold namespace consts before ANY body binds. Consts fold to
        // literals that depend only on earlier consts/literals — never on function
        // or method bodies — so binding them first lets BindNameOrConstFold splice
        // the literal directly at every use site (free function, inline `class`
        // method, `static func`). Without this, a const referenced from a body bound
        // earlier than Pass 4 stays a bare name and reaches the emitter as
        // `IL: undefined variable` (inline methods have no module-static fallback).
        // Bound here, the result is cached and reused at its Pass 4 output slot.
        var boundConsts = new Dictionary<ConstDeclarationSyntax, BoundConstDeclaration>(ReferenceEqualityComparer.Instance);
        foreach (var cds in syntax.Members.OfType<ConstDeclarationSyntax>())
            boundConsts[cds] = Declarations.BindNamespaceConst(cds);

        // In-body methods of a HEADERED class bind under a capture context: bare
        // header-param names resolve as `self.<param>` reads and mark the param
        // captured. The hoisted method nodes are the same references stored in the
        // declaration's Methods list, so reference identity recovers the owner.
        var headerOwner = new Dictionary<FunctionDeclarationSyntax, DataDeclarationSyntax>(ReferenceEqualityComparer.Instance);
        foreach (var hd in syntax.Members.OfType<DataDeclarationSyntax>())
            if (hd is { HeaderParameters.Count: > 0, Methods.Count: > 0 })
                foreach (var hm in hd.Methods)
                    headerOwner[hm] = hd;

        // Pass 2: bind all functions first (needed for instance method promotion)
        var allBoundFunctions = new List<BoundFunctionDeclaration>();
        foreach (var func in syntax.Members.OfType<FunctionDeclarationSyntax>())
            allBoundFunctions.Add(Declarations.BindFunction(func, captureOwner: headerOwner.GetValueOrDefault(func)));

        // Pass 3: determine instance methods per data type
        var instanceMethodsByType = new Dictionary<string, List<BoundFunctionDeclaration>>(StringComparer.Ordinal);
        // *T receiver methods — tracked for interface satisfaction but still emitted as static
        var pointerMethodsByType = new Dictionary<string, List<BoundFunctionDeclaration>>(StringComparer.Ordinal);
        var staticFunctions = new List<BoundFunctionDeclaration>();
        foreach (var bf in allBoundFunctions)
        {
            // Attachment is now EXPLICIT: a function becomes a method only via a Go-style
            // receiver block (`func (c: T) m()`) OR by being a type-body inline method
            // (`class Foo { func bar() {} }`, hoisted with a synthesized `self`). A bare
            // first-param free function is NOT promoted — Go's model. Inline methods keep
            // their old categorization (value/instance set by their `self` first param).
            var isMethod = bf.ReceiverKind != Esharp.Symbols.ReceiverKind.None || bf.IsTypeBodyMethod;
            if (!isMethod || bf.Parameters.Count == 0)
            {
                staticFunctions.Add(bf);
                continue;
            }

            var receiverType = bf.Parameters[0].Type;

            // Pointer receiver `func (c: *T) m()`: in `*T`'s method set only. It EMITS as a
            // static host `m(*T, …)` — the receiver is the leading `*T` parameter, subject to
            // the same escape analysis as any pointer param. That is the only representation
            // under which a body treating the receiver as a first-class `*T` (`current = head`,
            // nil-compare, `.next` walk) verifies; an instance method's `ref this` cannot be
            // assigned into a `__Ptr_T` local. The `*T` wrapper's interface forwarder
            // (ILHeapPointer.WireProtocol) delegates to this static; the method-call spelling
            // `p.m()` lowers to it at emit; the free-call spelling `m(p)` is rejected at bind
            // (ES2142). So a pointer-receiver method is in BOTH `pointerMethodsByType` (method
            // set / interface satisfaction) and `staticFunctions` (the emitted host). Namespace-
            // local: a method attaches only to a receiver type declared in its own namespace.
            if (bf.ReceiverKind == Esharp.Symbols.ReceiverKind.Pointer
                && receiverType is HeapPointerBoundType { Inner: DataType hpDt })
            {
                if (PromotesInCurrentNamespace(hpDt.Name))
                {
                    var pointerKey = TypeWorkKey(hpDt.Name, hpDt.TypeParameters.Count);
                    if (!pointerMethodsByType.TryGetValue(pointerKey, out var plist))
                        pointerMethodsByType[pointerKey] = plist = new List<BoundFunctionDeclaration>();
                    plist.Add(bf);
                }
                staticFunctions.Add(bf); // emitted as the static host (also the cross-namespace fallback)
                continue;
            }

            // Value / readonly-value receiver `func (c: T) m()` / `readonly func (c: T) m()`:
            // in both `T`'s and `*T`'s method sets. Emitted as a genuine instance method —
            // value → snapshot copy (struct) / reference (class), readonly → `in this`.
            var typeName = receiverType is DataType dt ? dt.Name
                : receiverType is ExternalType et && _data.Symbols.DataDecl(et.Name) is not null ? et.Name
                : null;
            var typeKey = receiverType switch
            {
                DataType dataReceiver => TypeWorkKey(dataReceiver.Name, dataReceiver.TypeParameters.Count),
                ExternalType externalReceiver when _data.Symbols.DataDecl(externalReceiver.Name) is { } decl
                    => TypeWorkKey(externalReceiver.Name, decl.TypeParameters.Count),
                _ => null,
            };
            if (typeName is not null && typeKey is not null && PromotesInCurrentNamespace(typeName))
            {
                if (!instanceMethodsByType.TryGetValue(typeKey, out var list))
                    instanceMethodsByType[typeKey] = list = new List<BoundFunctionDeclaration>();
                list.Add(bf);
                continue;
            }
            staticFunctions.Add(bf); // cross-namespace receiver: stays a free function
        }

        // Pass 3.5: synthesize forwarder methods for embedded-field method promotion.
        // For each data type with embedded fields, walk the inner type's instance
        // method set and append a forwarder `M(self: Outer, args...) -> R = self.<Field>.M(args)`
        // when the outer doesn't already declare a method by the same name. The
        // forwarder lets SatisfiesInterface's normal path catch promoted methods
        // and the IL emitter emit a real method body that the CLR dispatches to
        // via interface satisfaction.
        foreach (var dataDecl in syntax.Members.OfType<DataDeclarationSyntax>())
        {
            if (_data.Symbols.TryGet(dataDecl.Name, dataDecl.TypeParameters.Count, Ctx.CurrentNamespace) is not { } dataSym) continue;
            foreach (var ef in dataSym.Fields.Where(f => f.IsEmbedded))
            {
                if (ef.Bound is not { } efBound) continue;
                var innerType = efBound is HeapPointerBoundType hp ? hp.Inner : efBound;
                string? innerName = innerType switch
                {
                    DataType idt => idt.Name,
                    ExternalType iet when _data.Symbols.DataDecl(iet.Name) is not null => iet.Name,
                    _ => null,
                };
                if (innerName is null) continue;
                var innerKey = innerType switch
                {
                    DataType idt => TypeWorkKey(idt.Name, idt.TypeParameters.Count),
                    ExternalType iet when _data.Symbols.DataDecl(iet.Name) is { } decl
                        => TypeWorkKey(iet.Name, decl.TypeParameters.Count),
                    _ => null,
                };
                if (innerKey is null) continue;
                var ownKey = TypeWorkKey(dataDecl.Name, dataDecl.TypeParameters.Count);

                if (!instanceMethodsByType.TryGetValue(ownKey, out var ownMethods))
                {
                    ownMethods = new List<BoundFunctionDeclaration>();
                    instanceMethodsByType[ownKey] = ownMethods;
                }

                // Value-receiver methods on the inner type: forwarder calls
                // self.<field>.<method>(args). For pointer-embed (*Inner field) the
                // bound shape ends up routing through the pointer wrapper at emit;
                // for value-embed the call is the direct instance method.
                if (instanceMethodsByType.TryGetValue(innerKey, out var innerInstance))
                {
                    foreach (var innerMethod in innerInstance)
                    {
                        if (ownMethods.Any(m => m.Name == innerMethod.Name && m.Parameters.Count == innerMethod.Parameters.Count))
                            continue;
                        ownMethods.Add(Declarations.SynthesizeEmbeddedForwarder(dataDecl.Name, ef, innerType, innerMethod));
                    }
                }

                // Pointer-receiver methods on the inner type: only reachable when the
                // field is itself a *T (so self.<field> is already a *Inner and the
                // forwarded call matches the pointer-receiver signature). Without
                // this, a *Outer that should satisfy a protocol through its embedded
                // *Inner has no method to dispatch into on __Ptr_Outer's delegate.
                if (efBound is HeapPointerBoundType && pointerMethodsByType.TryGetValue(innerKey, out var innerPointer))
                {
                    foreach (var innerMethod in innerPointer)
                    {
                        if (ownMethods.Any(m => m.Name == innerMethod.Name && m.Parameters.Count == innerMethod.Parameters.Count))
                            continue;
                        ownMethods.Add(Declarations.SynthesizeEmbeddedForwarder(dataDecl.Name, ef, innerType, innerMethod));
                    }
                }

                // Also enroll the data type in the pointer-method registry so
                // BindData's "*T satisfies protocol" check (Go model) fires. Without
                // this entry, a data type with NO directly-declared pointer-receiver
                // methods is skipped — even though its embedded *Inner gives it a
                // non-empty *T method set via the forwarders we just synthesized.
                // The list itself can stay empty; presence is what BindData reads.
                if (efBound is HeapPointerBoundType
                    && !pointerMethodsByType.ContainsKey(ownKey))
                {
                    pointerMethodsByType[ownKey] = new List<BoundFunctionDeclaration>();
                }
            }
        }

        // Pass 4: bind data (with instance methods + implicit interface satisfaction)
        var localDataNames = new HashSet<string>(StringComparer.Ordinal);
        var boundMembers = new List<BoundMember>();
        foreach (var member in syntax.Members)
        {
            switch (member)
            {
                case InterfaceDeclarationSyntax proto:
                    boundMembers.Add(Declarations.BindProtocol(proto));
                    break;
                case DelegateDeclarationSyntax del:
                    boundMembers.Add(Declarations.BindDelegate(del, syntax.NamespaceName));
                    break;
                case DataDeclarationSyntax data:
                    localDataNames.Add(data.Name);
                    // Collect instance methods from all files for this data type
                    var dataKey = TypeWorkKey(data.Name, data.TypeParameters.Count);
                    var methods = instanceMethodsByType.GetValueOrDefault(dataKey) ?? [];
                    var ptrMethods = pointerMethodsByType.GetValueOrDefault(dataKey) ?? [];
                    // Inject the members synthesized before Pass 2: they join the
                    // instance-method set so interface satisfaction and the emitters
                    // treat them exactly like a promoted method.
                    if (synthesizedByType.TryGetValue(dataKey, out var synthMembers))
                    {
                        if (methods is not List<BoundFunctionDeclaration> ml)
                            methods = ml = [.. methods];
                        ml.AddRange(synthMembers);
                    }
                    var dataSym = _data.Symbols.TryGet(data.Name, data.TypeParameters.Count, Ctx.CurrentNamespace);
                    Types.CurrentEnclosingType = dataSym;
                    boundMembers.Add(Declarations.BindData(data, methods, ptrMethods, dataSym));
                    Types.CurrentEnclosingType = null;
                    BindNestedTypes(data.NestedTypes, dataSym, boundMembers, syntax.NamespaceName);
                    break;
                case ChoiceDeclarationSyntax choice:
                    boundMembers.Add(Declarations.BindChoice(choice));
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var resolvedCases = new List<BoundEnumCase>();
                    int nextEnumValue = 0;
                    foreach (var c in enumDecl.Cases)
                    {
                        int value = c.ExplicitValue ?? nextEnumValue;
                        resolvedCases.Add(new BoundEnumCase(c.Name, value));
                        nextEnumValue = value + 1;
                    }
                    boundMembers.Add(new BoundEnumDeclaration(enumDecl.IsPublic, enumDecl.Name, resolvedCases, null, ResolveEnumUnderlying(enumDecl)));
                    break;
                case StaticFuncDeclarationSyntax sfn:
                    var sfnSym = _data.Symbols.TryGet(sfn.Name, sfn.GenericParameters.Count, Ctx.CurrentNamespace);
                    Types.CurrentEnclosingType = sfnSym;
                    boundMembers.Add(Declarations.BindStaticFunc(sfn));
                    Types.CurrentEnclosingType = null;
                    BindNestedTypes(sfn.NestedTypes, sfnSym, boundMembers, syntax.NamespaceName);
                    break;
                case ConstDeclarationSyntax cds:
                    // Already folded in Pass 1.9 — reuse the bound result so the
                    // const isn't re-folded (and re-reported) at its output slot.
                    boundMembers.Add(boundConsts[cds]);
                    break;
                case NamespaceInitDeclarationSyntax init:
                    if (!_data.NamespaceInitializers.Add(Ctx.CurrentNamespace))
                    {
                        _data.Diagnostics.Report(init.Span,
                            $"ES2206: namespace '{Ctx.CurrentNamespace}' already declares an 'init' block.");
                        break;
                    }
                    boundMembers.Add(Declarations.BindNamespaceInit(init));
                    break;
                case NamespaceStateDeclarationSyntax state:
                    if (_data.NamespaceStateDeclarations.TryGetValue(state, out var boundState))
                        boundMembers.Add(boundState);
                    break;
            }
        }

        // Cross-file instance methods: emit partial struct blocks for data types from other files
        foreach (var (typeName, methods) in instanceMethodsByType)
        {
            if (localDataNames.Contains(typeName)) continue; // already handled above
            if (_data.Symbols.DataDecl(typeName) is not { } remoteData) continue;

            // Create a partial data declaration with no fields, just the cross-file instance methods
            var crossClassification = Types.ClassifyData(typeName);
            var declaringModule = _data.Symbols.FindType(typeName, TypeSymbolKind.Struct, TypeSymbolKind.Class)?.Namespace;
            boundMembers.Add(new BoundDataDeclaration(
                false, false, typeName, remoteData.TypeParameters, [], [], methods, crossClassification, [], null, declaringModule));
        }

        // Add remaining static functions
        foreach (var sf in staticFunctions)
            boundMembers.Add(sf);

        // Pass 5: Validate the `data` value-semantic contract, then let the compiler
        // pick CLR struct vs class. Recursive fields (direct self-reference or the
        // declaring type buried in a generic arg) are an error — the user must use
        // `*T` to break the cycle. After that, non-recursive `data` may still be
        // silently promoted to class based on size / ref-field / collection / pass
        // heuristics; the value-semantic surface is preserved via the generated
        // interop facade + value-equality.
        Declarations.ValidateDataContract(boundMembers);

        var boundImports = syntax.Imports.Select(i => new BoundUsing(i.IsStatic, i.Path, i.Alias)).ToList();

        // Close the unit for the semantic model: the file, its namespace, and the
        // internal namespaces it imports — the scope frame LookupSymbolsInScope
        // layers namespace-visible names onto. The file comes off any node's span.
        var unitFile = syntax.Members.Select(m => m.Span.File)
            .Concat(syntax.Imports.Select(i => i.Span.File))
            .FirstOrDefault(f => !string.IsNullOrEmpty(f)) ?? "";
        var importPaths = syntax.Imports.Where(i => !i.IsStatic && i.Alias is null).Select(i => i.Path).ToList();
        _data.Sink.OnUnitBound(unitFile, Ctx.CurrentNamespace, importPaths);

        return new BoundCompilationUnit(syntax.NamespaceName, boundImports, boundMembers);
    }

    void SetCurrentUsings(IReadOnlyList<UsingSyntax> imports)
    {
        Ctx.NamespaceImports.Clear();
        Ctx.StaticImports.Clear();
        Ctx.TypeAliases.Clear();
        foreach (var import in imports)
        {
            if (import.Alias is not null)
            {
                Ctx.TypeAliases[import.Alias] = import.Path;
            }
            else if (import.IsStatic)
            {
                var lastDot = import.Path.LastIndexOf('.');
                var shortName = lastDot >= 0 ? import.Path[(lastDot + 1)..] : import.Path;
                Ctx.StaticImports[shortName] = import.Path;
            }
            else
            {
                Ctx.NamespaceImports.Add(import.Path);
            }
        }
    }

    // === Symbol-spine structure population (signature time) ===
    // Fields, cases, conformances, and the base-class link land on the interned
    // TypeSymbols here — after every type is registered (so cross-file/namespace
    // references resolve), before any body binds (so resolution reads the spine).
    // Idempotent per symbol: re-registering a unit (workspace rebuild into a
    // shared CompilationData) must not duplicate members.

    void PopulateDataSymbol(DataDeclarationSyntax data, TypeSymbol? preresolved = null)
    {
        var sym = preresolved ?? _data.Symbols.TryGet(data.Name, data.TypeParameters.Count, Ctx.CurrentNamespace);
        if (sym is null || sym.Fields.Count > 0 || sym.Interfaces.Count > 0) return;
        // Field/interface types resolve in THIS type's scope, so a field typed by a
        // sibling/own nested type (`kind: Kind` inside a type nested with `Kind`)
        // resolves by bare name.
        var prevEnclosing = Types.CurrentEnclosingType;
        Types.CurrentEnclosingType = sym;
        try {
        foreach (var f in data.Fields)
        {
            var fieldType = Types.ResolveType(f.Type);
            // Mirror the bound layer's `*T` field shape: a pointer field on a value
            // `data` or primitive is the heap wrapper. (Silent here — BindData owns
            // the ES2003 diagnostic for `*refData`.)
            if (f.Type is PointerTypeSyntax
                && fieldType is PrimitiveType or DataType
                && !(fieldType is DataType fd && Types.ClassifyData(fd.Name) == DataClassification.Class))
                fieldType = new HeapPointerBoundType(fieldType);
            var fieldSym = new FieldSymbol
            {
                Name = f.Name, DeclaringType = sym, Type = MapTypeRef(fieldType), Bound = fieldType,
                IsPublic = f.IsPublic ?? data.IsPublic,
                Visibility = f.IsPublic switch
                {
                    true => Syntax.Visibility.Public,
                    false => Syntax.Visibility.Private,
                    null => data.IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal,
                },
                DeclaredMutable = f.Mutable,
                Mutable = f.Mutable,
                IsEmbedded = f.IsEmbedded, IsEvent = f.IsEvent, Span = f.Span,
                IsProperty = f.Property is not null,
                HasDurablePropertyLocation = f.Property is { ComputedGetter: null } property
                    && (property.LocaStorageName is not null
                        || property.MutStorageName is not null
                        || property.ScopedMutBody is null && property.SetterBody is null),
                HasScopedPropertyLocation = f.Property?.ScopedMutBody is not null,
                HasCustomPropertySetter = f.Property?.SetterBody is not null,
            };
            sym.AddField(fieldSym);
            _data.Sink.OnFieldResolved(fieldSym, f.NameSpan, Semantics.SymbolOccurrence.Declaration);
        }
        foreach (var ifaceSyntax in data.Interfaces)
        {
            sym.AddInterface(MapTypeRef(Types.ResolveType(ifaceSyntax)));
            // The first entry naming an open/abstract class is the base class.
            if (sym.BaseType is null)
            {
                var ifaceName = TypeResolver.InterfaceName(ifaceSyntax);
                if (_data.Symbols.DataDecl(ifaceName) is { } bd && bd.IsRef
                    && bd.Modifier is ClassModifier.Open or ClassModifier.Abstract)
                {
                    sym.BaseType = _data.Symbols.TryGet(
                        ifaceName, bd.TypeParameters.Count,
                        _data.Symbols.NamespacesOf(ifaceName).FirstOrDefault());
                    // Record the reverse edge so the base's closed leaf set is queryable
                    // for type-pattern exhaustiveness.
                    sym.BaseType?.AddDerived(sym);
                }
            }
        }
        } finally { Types.CurrentEnclosingType = prevEnclosing; }
    }

    void PopulateChoiceSymbol(ChoiceDeclarationSyntax choice, TypeSymbol? preresolved = null)
    {
        var sym = preresolved ?? _data.Symbols.TryGet(choice.Name, choice.TypeParameters.Count, Ctx.CurrentNamespace);
        if (sym is null || sym.Cases.Count > 0) return;
        foreach (var c in choice.Cases)
        {
            var payloads = c.Payloads.Select(pl =>
            {
                var plType = Types.ResolveType(pl.Type);
                return new FieldSymbol
                {
                    Name = pl.Name, DeclaringType = sym,
                    Type = MapTypeRef(plType), Bound = plType, Span = pl.Span,
                };
            }).ToList();
            var caseSym = new CaseSymbol { Name = c.Name, DeclaringType = sym, Payloads = payloads, Span = c.NameSpan };
            sym.AddCase(caseSym);
            _data.Sink.OnCaseResolved(caseSym, c.NameSpan, Semantics.SymbolOccurrence.Declaration);
        }
    }

    void PopulateEnumSymbol(EnumDeclarationSyntax enumDecl, TypeSymbol? preresolved = null)
    {
        var sym = preresolved ?? _data.Symbols.TryGet(enumDecl.Name, 0, Ctx.CurrentNamespace);
        if (sym is null || sym.Cases.Count > 0) return;
        var next = 0;
        foreach (var c in enumDecl.Cases)
        {
            var value = c.ExplicitValue ?? next;
            var caseSym = new CaseSymbol { Name = c.Name, DeclaringType = sym, Value = value, Span = c.NameSpan };
            sym.AddCase(caseSym);
            _data.Sink.OnCaseResolved(caseSym, c.NameSpan, Semantics.SymbolOccurrence.Declaration);
            next = value + 1;
        }
    }

    void PopulateInterfaceSymbol(InterfaceDeclarationSyntax proto, TypeSymbol? preresolved = null)
    {
        var sym = preresolved ?? _data.Symbols.TryGet(proto.Name, proto.TypeParameters.Count, Ctx.CurrentNamespace);
        if (sym is null || sym.Members.Count > 0 || sym.Fields.Count > 0) return;
        foreach (var m in proto.Methods)
        {
            sym.AddMember(new MethodSymbol
            {
                Name = m.Name,
                DeclaredArity = m.Parameters.Count,
                DeclaringType = sym,
                DeclaredParameters = m.Parameters.Select(p => MapTypeRef(Types.ResolveType(p.Type))).ToList(),
                DeclaredReturn = MapTypeRef(Types.ResolveType(m.ReturnType)),
            });
        }
        if (proto.Properties is { } properties)
            foreach (var property in properties)
            {
                var propertyType = Types.ResolveType(property.Type);
                var field = new FieldSymbol
                {
                    Name = property.Name,
                    DeclaringType = sym,
                    Type = MapTypeRef(propertyType),
                    Bound = propertyType,
                    IsPublic = true,
                    Visibility = Syntax.Visibility.Public,
                    DeclaredMutable = property.HasSet,
                    Mutable = property.HasSet,
                    IsProperty = true,
                    HasDurablePropertyLocation = property.HasLoca,
                    Span = property.NameSpan,
                };
                sym.AddField(field);
                _data.Sink.OnFieldResolved(field, property.NameSpan,
                    Semantics.SymbolOccurrence.Declaration);
            }
    }

    /// Bind the member signatures of a `static func` host onto <paramref name="host"/>.
    /// Factored so both a top-level `static func` and a nested one share the path.
    void BindStaticFuncMemberSignatures(StaticFuncDeclarationSyntax sfn, TypeSymbol? host)
    {
        // Member parameter/return types resolve in the host's scope, so a parameter
        // typed by a nested type (`Select(arms: Arm[])`) resolves by bare name.
        var prevEnclosing = Types.CurrentEnclosingType;
        Types.CurrentEnclosingType = host;
        try {
        foreach (var fn in sfn.Functions)
        {
            ValidateOperatorDeclaration(fn, host, 0);
            // Members without an explicit return type pick up the static-func's
            // `returns Type` clause, if present.
            var effRet = fn.HasExplicitReturnType ? fn.ReturnType
                       : (sfn.DefaultReturns?.Type ?? fn.ReturnType);
            var sfnRet = Types.ResolveHeapPointerAware(effRet);
            var sfnIsAsync = DeclarationBinder.FunctionBodyHasAwait(fn);
            var sfnSym = new MethodSymbol
            {
                Name = fn.Name,
                DeclaredArity = fn.Parameters.Count,
                IsStatic = true,
                DeclaringType = host,
                Decl = fn,
                ReturnType = sfnRet,
                IsAsync = sfnIsAsync,
                HasExplicitAsyncWrapperReturn = sfnIsAsync && DeclarationBinder.HasExplicitAsyncWrapperReturn(fn),
                DeclaredParameters = fn.Parameters.Select(p => MapTypeRef(Types.ResolveHeapPointerAware(p.Type))).ToList(),
                DeclaredReturn = MapTypeRef(sfnRet),
                TypeParameters = fn.TypeParameters,
                OperatorKind = fn.OperatorKind,
                OperatorParameterTypes = fn.Parameters.Select(p => Types.ResolveHeapPointerAware(p.Type)).ToList(),
            };
            _data.Symbols.AddFunction($"{sfn.Name}.{fn.Name}", sfnSym);
            host?.AddMember(sfnSym);
            _data.Sink.OnMethodResolved(sfnSym, fn.NameSpan, Semantics.SymbolOccurrence.Declaration);
        }
        } finally { Types.CurrentEnclosingType = prevEnclosing; }
    }

    /// Populate the field/case/member signatures of NESTED type declarations under
    /// <paramref name="declaring"/>, recursively. Each nested symbol is resolved by
    /// the declaring chain (TryGetNested) and handed to the same Populate* core a
    /// top-level type uses, so a nested type's members resolve identically.
    void PopulateNestedSignatures(IReadOnlyList<MemberSyntax>? nested, TypeSymbol? declaring)
    {
        if (nested is null || declaring is null) return;
        foreach (var member in nested)
        {
            switch (member)
            {
                case DataDeclarationSyntax data:
                    var dsym = _data.Symbols.TryGetNested(data.Name, data.TypeParameters.Count, declaring);
                    PopulateDataSymbol(data, dsym);
                    PopulateNestedSignatures(data.NestedTypes, dsym);
                    break;
                case ChoiceDeclarationSyntax choice:
                    PopulateChoiceSymbol(choice, _data.Symbols.TryGetNested(choice.Name, choice.TypeParameters.Count, declaring));
                    break;
                case EnumDeclarationSyntax enumDecl:
                    PopulateEnumSymbol(enumDecl, _data.Symbols.TryGetNested(enumDecl.Name, 0, declaring));
                    break;
                case InterfaceDeclarationSyntax proto:
                    PopulateInterfaceSymbol(proto, _data.Symbols.TryGetNested(proto.Name, proto.TypeParameters.Count, declaring));
                    break;
                case StaticFuncDeclarationSyntax sfn:
                    var ssym = _data.Symbols.TryGetNested(sfn.Name, 0, declaring);
                    BindStaticFuncMemberSignatures(sfn, ssym);
                    PopulateNestedSignatures(sfn.NestedTypes, ssym);
                    break;
            }
        }
    }

    // === Symbol-spine population (promotion registration) ===
    // This is where the interned TypeSymbols stop being identity-only and gain
    // their resolved method set: promoted free functions become MethodSymbols
    // attached to their receiver's TypeSymbol.Members at signature time.

    /// Build the symbol-layer MethodSymbol for a free function that promotes onto a
    /// receiver type, and attach it to that receiver's TypeSymbol. Runs from
    /// RegisterSignatures (after every type is interned, before any body binds), so
    /// the promotion decision is computed exactly once and lives on the symbol.
    /// Register a synthesized member as a value-receiver method on its receiver —
    /// the symbol the member-access authority and call resolution see, so a
    /// `b.answer()` to a synthesized `answer(self: Box)` resolves like a promoted
    /// method. The first parameter is the receiver (`self`); DeclaredArity is the
    /// source parameter count.
    void RegisterSynthesizedMember(TypeSymbol receiver, BoundFunctionDeclaration member)
    {
        var method = new MethodSymbol
        {
            Name = member.Name,
            DeclaredArity = member.Parameters.Count,
            IsStatic = false,
            ReceiverKind = ReceiverKind.Value,
            DeclaringType = receiver,
            ReturnType = member.ReturnType,
            TypeParameters = member.TypeParameters,
        };
        receiver.AddMember(method);
        _data.Symbols.AddPromoted(member.Name, method);
        _data.Symbols.AddFunction(member.Name, method);
    }

    MethodSymbol? RegisterPromotedMethodSymbol(FunctionDeclarationSyntax func, BoundType callerReturn,
        TypeSymbol? typeBodyOwner = null)
    {
        // Attachment is explicit: a method is a receiver-block function OR a type-body
        // inline method (hoisted with `self`). Either way the receiver is the leading
        // parameter (Parameters[0]), so arity and the declared-parameter list are the
        // natural list — no off-by-one against the bound layer, emit, or call resolution.
        if ((func.Receiver is null && !func.IsTypeBodyMethod) || func.Parameters.Count == 0) return null;

        ReceiverKind kind;
        TypeSymbol receiverSym;
        if (typeBodyOwner is not null)
        {
            // The owner map is authoritative for an inline method: the parser's
            // synthetic receiver deliberately omits generic arguments.
            kind = ReceiverKind.Value;
            receiverSym = typeBodyOwner;
        }
        else
        {
            var recvType = Types.ResolveHeapPointerAware(func.Parameters[0].Type);
            var classified = func.Receiver?.IsStaticFacet == true
                ? (ReceiverKind.Static, TypeResolver.TypeSyntaxLeafName(func.Receiver.Type))
                : ClassifyReceiver(recvType, func.Receiver?.IsReadonly ?? false);
            if (classified is null)
                return null;
            var (resolvedKind, receiverName) = classified.Value;
            // Namespace-local: the method attaches only to a receiver type declared in its
            // own namespace (the single gate, shared with Pass 3).
            if (!PromotesInCurrentNamespace(receiverName)) return null;
            var receiverArity = func.Receiver?.Type is GenericTypeSyntax generic ? generic.Args.Count : 0;
            var resolvedReceiver = resolvedKind == ReceiverKind.Static
                ? _data.Symbols.TryGet(receiverName, receiverArity, Ctx.CurrentNamespace)
                : ReceiverSymbol(receiverName);
            if (resolvedReceiver is null || (resolvedKind == ReceiverKind.Static && !resolvedReceiver.HasStaticFacet)) return null;
            kind = resolvedKind;
            receiverSym = resolvedReceiver;
        }

        // Idempotent per DECLARATION, not per name: overloads attach onto their own
        // receivers; only a re-registration of the same decl returns the existing symbol.
        if (receiverSym.Members.FirstOrDefault(m => ReferenceEquals(m.Decl, func)) is { } already)
            return already;

        var method = new MethodSymbol
        {
            Name = func.Name,
            DeclaredArity = kind == ReceiverKind.Static ? func.Parameters.Count - 1 : func.Parameters.Count,
            IsStatic = kind == ReceiverKind.Static,
            ReceiverKind = kind,
            DeclaringType = receiverSym,
            Decl = func,
            ReturnType = callerReturn,
            IsAsync = DeclarationBinder.FunctionBodyHasAwait(func),
            HasExplicitAsyncWrapperReturn = DeclarationBinder.FunctionBodyHasAwait(func) && DeclarationBinder.HasExplicitAsyncWrapperReturn(func),
            DeclaredParameters = (kind == ReceiverKind.Static ? func.Parameters.Skip(1) : func.Parameters)
                .Select(p => MapTypeRef(Types.ResolveHeapPointerAware(p.Type))).ToList(),
            DeclaredReturn = MapTypeRef(callerReturn),
            TypeParameters = func.TypeParameters,
            OperatorKind = func.OperatorKind,
            OperatorParameterTypes = (kind == ReceiverKind.Static ? func.Parameters.Skip(1) : func.Parameters)
                .Select(p => Types.ResolveHeapPointerAware(p.Type)).ToList(),
        };
        ValidateOperatorDeclaration(func, receiverSym, kind == ReceiverKind.Static ? 1 : 0);
        receiverSym.AddMember(method);
        if (kind == ReceiverKind.Static)
            _data.Symbols.AddFunction($"{receiverSym.Name}.{func.Name}", method);
        else
            _data.Symbols.AddPromoted(func.Name, method);
        _data.Sink.OnMethodResolved(method, func.NameSpan, Semantics.SymbolOccurrence.Declaration);
        return method;
    }

    /// Classify a receiver-block type into its method-set kind: `*T` → pointer (in `*T`'s
    /// set only); `readonly` → readonly value (`in this`, in both sets); a plain user
    /// `data`/`class` (directly or via an as-yet-unresolved ExternalType naming a local
    /// type) → value (in both sets). Returns null if the receiver isn't a local type.
    (ReceiverKind Kind, string ReceiverName)? ClassifyReceiver(BoundType receiverType, bool isReadonly) =>
        receiverType switch
        {
            HeapPointerBoundType { Inner: DataType hpDt } => (ReceiverKind.Pointer, hpDt.Name),
            DataType dt => (isReadonly ? ReceiverKind.ReadonlyValue : ReceiverKind.Value, dt.Name),
            StaticFuncType sf => (ReceiverKind.Static, sf.Name),
            ExternalType et when _data.Symbols.DataDecl(et.Name) is not null
                => (isReadonly ? ReceiverKind.ReadonlyValue : ReceiverKind.Value, et.Name),
            _ => null,
        };

    /// The interned TypeSymbol for a user-declared receiver type by its bare name.
    TypeSymbol? ReceiverSymbol(string receiverName)
    {
        if (_data.Symbols.DataDecl(receiverName) is not { } decl) return null;
        // Promotion is namespace-local, so the receiver symbol is the one
        // declared in the CURRENT namespace (the gate already verified locality).
        return _data.Symbols.TryGet(receiverName, decl.TypeParameters.Count, Ctx.CurrentNamespace);
    }

    /// Bound type → structured TypeRef. Leaves are symbols (CoreTypes for primitives
    /// and the runtime generics, the user SymbolTable for declared types, the
    /// CoreTypes external holding pen for everything else), never strings. This is
    /// the binder→backend currency the reorg routes identity through; step 4
    /// widens its consumers, but the mapping itself lives here.
    TypeRef MapTypeRef(BoundType type)
    {
        var core = _data.Core;
        switch (type)
        {
            case InferredType:
                return TypeRef.InferencePending;
            case VoidType:
                return new TypeRef(core.Void);
            case PrimitiveType p:
                return new TypeRef(core.Primitive(p.Name) ?? core.External(p.Name));
            case NullableType n:
                return MapTypeRef(n.Inner) with { Modifier = TypeRefModifier.Nullable };
            case HeapPointerBoundType hp:
                return MapTypeRef(hp.Inner) with { Modifier = TypeRefModifier.HeapPointer };
            case ByRefBoundType br:
                return MapTypeRef(br.Inner) with { Modifier = TypeRefModifier.ByRef };
            case ResultType r:
                return new TypeRef(core.Result, [MapTypeRef(r.OkType), MapTypeRef(r.ErrorType)]);
            case ChanType c:
                return new TypeRef(core.Chan, [MapTypeRef(c.ElementType)]);
            case TupleType t:
                return new TypeRef(core.Tuple(t.ElementTypes.Count), t.ElementTypes.Select(MapTypeRef).ToList());
            case DataType d:
                return new TypeRef(
                    UserSymbol(d.Name, d.TypeParameters.Count, d.Namespace) ?? core.External(d.Name, d.TypeArgs.Count),
                    d.TypeArgs.Select(MapTypeRef).ToList());
            case ChoiceType ch:
                return new TypeRef(
                    UserSymbol(ch.Name, ch.TypeParameters.Count, null) ?? core.External(ch.Name, ch.TypeArgs.Count),
                    ch.TypeArgs.Select(MapTypeRef).ToList());
            case InterfaceType i:
                return new TypeRef(
                    UserSymbol(i.Name, i.Decl.TypeParameters.Count, null) ?? core.External(i.Name, i.TypeArgs.Count),
                    i.TypeArgs.Select(MapTypeRef).ToList());
            case EnumType e:
                return new TypeRef(UserSymbol(e.Name, 0, null) ?? core.External(e.Name));
            case NamedDelegateType nd:
                return new TypeRef(UserSymbol(nd.Name, 0, nd.Namespace) ?? core.External(nd.Name));
            case ExternalCSharpType ecs:
                return new TypeRef(core.External(ecs.Handle.Name, ecs.TypeArgs.Count), ecs.TypeArgs.Select(MapTypeRef).ToList());
            case ExternalType ext:
                // Preserve external generic structure in the symbol spine. Dropping
                // these arguments turned `IEnumerable<int>` conformance into bare
                // `IEnumerable`, erasing element types before lowering or codegen had
                // a chance to consume them.
                return new TypeRef(
                    core.External(ext.Name, ext.TypeArgs.Count),
                    ext.TypeArgs.Select(MapTypeRef).ToList());
            default:
                // Function pointers, null, static-func types, etc. — never appear as a
                // promoted-method parameter/return today; a stable external leaf keyed
                // by the E# display name keeps the TypeRef total without inventing
                // structure we don't have yet.
                return new TypeRef(core.External(TypeResolver.TypeDisplayName(type)));
        }
    }

    /// Look up a user-declared type symbol by (name, arity), preferring its declaring
    /// namespace, then falling back to the bare (name, arity) across namespaces.
    TypeSymbol? UserSymbol(string name, int arity, string? ns) =>
        _data.Symbols.TryGet(name, arity, ns) ?? _data.Symbols.TryGetByName(name, arity);
}
