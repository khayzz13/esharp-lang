using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.Binder;

/// The type-facts service of the binder: source type references resolve to
/// BoundTypes here, runtime (reflection) views of externals resolve here,
/// namespace-scoping rules (ES2150/ES2151) are enforced here, member-TYPE
/// resolution (ResolveMemberType) lives here, and the `data` CLR-form
/// classification (struct vs class) is computed here. Reads the per-unit import
/// context and current namespace off the shared BindContext — no mirrored state.
public sealed class TypeResolver
{
    readonly CompilationData _data;
    readonly BindContext _ctx;
    readonly List<string> _currentNamespaceImports;
    readonly Dictionary<string, string> _currentStaticImports;
    readonly Dictionary<string, string> _currentTypeAliases;
    readonly bool _searchCommonNamespaces;
    IEnumerable<string> AutoSearchNamespaces => _searchCommonNamespaces
        ? UsingEnvironment.Instance.ExternalNamespaces.Concat(UsingEnvironment.Instance.CommonNamespaces)
        : UsingEnvironment.Instance.ExternalNamespaces;

    /// The namespace of the unit currently being resolved — the shared context's,
    /// not a mirror.
    public string CurrentNamespace => _ctx.CurrentNamespace;

    /// The enclosing type whose scope governs bare nested-name resolution (or null at
    /// namespace scope). Mirrors the shared context so the binder can set it while
    /// populating/binding a type's members.
    internal TypeSymbol? CurrentEnclosingType
    {
        get => _ctx.CurrentEnclosingType;
        set => _ctx.CurrentEnclosingType = value;
    }

    public TypeResolver(BindContext ctx)
    {
        _ctx = ctx;
        _data = ctx.Data;
        _currentNamespaceImports = ctx.NamespaceImports;
        _currentStaticImports = ctx.StaticImports;
        _currentTypeAliases = ctx.TypeAliases;
        _searchCommonNamespaces = _data.EnableImplicitUsings;
    }


    internal static bool PointerInnerMatches(BoundType a, BoundType b) => (a, b) switch
    {
        (DataType da, DataType db) => da.Name == db.Name,
        (ChoiceType ca, ChoiceType cb) => ca.Name == cb.Name,
        (EnumType ea, EnumType eb) => ea.Name == eb.Name,
        (PrimitiveType pa, PrimitiveType pb) => pa.Name == pb.Name,
        (ExternalType xa, ExternalType xb) => xa.Name == xb.Name,
        _ => false,
    };

    /// The E# *source* spelling of a type — what diagnostics show the user
    /// (`*T`, `Result<int, DbError>`, `List<Node>`). The semantic-layer counterpart
    /// to the emit layers' `EmitName`: pointers read `*T` (not the CLR `ref T`),
    /// generics render their arguments inline. Diagnostics speak this; never the
    /// emit spelling.
    internal static string TypeDisplayName(BoundType t) => t switch
    {
        DataType d => Generic(d.Name, d.TypeArgs),
        ChoiceType c => Generic(c.Name, c.TypeArgs),
        EnumType e => e.Name,
        PrimitiveType p => p.Name,
        ExternalType x => Generic(x.Name, x.TypeArgs),
        ExternalCSharpType xc => Generic(xc.Handle.Name, xc.TypeArgs),
        InterfaceType pr => Generic(pr.Name, pr.TypeArgs),
        NamedDelegateType nd => nd.Name,
        StaticFuncType s => s.Name,
        ResultType r => $"Result<{TypeDisplayName(r.OkType)}, {TypeDisplayName(r.ErrorType)}>",
        ChanType ch => $"Chan<{TypeDisplayName(ch.ElementType)}>",
        TupleType tu => $"({string.Join(", ", tu.ElementTypes.Select(TypeDisplayName))})",
        NullableType n => TypeDisplayName(n.Inner) + "?",
        HeapPointerBoundType hp => "*" + TypeDisplayName(hp.Inner),
        ByRefBoundType br => "ref " + TypeDisplayName(br.Inner),
        VoidType => "void",
        NullType => "nil",
        InferredType => "var",
        _ => t.ToString() ?? "?",
    };

    static string Generic(string name, IReadOnlyList<BoundType> args) =>
        args.Count > 0 ? $"{name}<{string.Join(", ", args.Select(TypeDisplayName))}>" : name;

    internal static bool LooksLikeTypeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Starts with upper-case letter, or contains generic brackets.
        return char.IsUpper(name[0]) || name.Contains('<');
    }

    internal static BoundType MapRuntimeTypeToBoundType(Type t)
    {
        // A closed stdlib `Result`2 maps to the intrinsic ResultType, so a reflected
        // return (e.g. `Result.Ok`/`.Error`) retains the builtin `.Value`/`?`/`match`
        // surface rather than becoming an opaque external type.
        if (t.IsGenericType && !t.IsGenericTypeDefinition
            && t.GetGenericTypeDefinition().FullName == "Esharp.Stdlib.Result`2")
        {
            var ra = t.GetGenericArguments();
            return new ResultType(MapRuntimeTypeToBoundType(ra[0]), MapRuntimeTypeToBoundType(ra[1]));
        }
        if (t == typeof(int)) return new PrimitiveType("int");
        if (t == typeof(long)) return new PrimitiveType("long");
        if (t == typeof(double)) return new PrimitiveType("double");
        if (t == typeof(float)) return new PrimitiveType("float");
        if (t == typeof(bool)) return new PrimitiveType("bool");
        if (t == typeof(string)) return new PrimitiveType("string");
        if (t == typeof(byte)) return new PrimitiveType("byte");
        if (t == typeof(short)) return new PrimitiveType("short");
        if (t == typeof(char)) return new PrimitiveType("char");
        // The unsigned / wide numeric primitives — a BCL method returning `uint`
        // (`BinaryPrimitives.ReadUInt32LittleEndian`), `ushort`, `sbyte`, `decimal`, …
        // must map to the E# primitive so `int(value)` conversions, signedness-aware
        // operators, and contextual numeric binding all see a numeric primitive rather
        // than an opaque `UInt32` external type.
        if (t == typeof(uint)) return new PrimitiveType("uint");
        if (t == typeof(ulong)) return new PrimitiveType("ulong");
        if (t == typeof(ushort)) return new PrimitiveType("ushort");
        if (t == typeof(sbyte)) return new PrimitiveType("sbyte");
        if (t == typeof(decimal)) return new PrimitiveType("decimal");
        if (t == typeof(nint)) return new PrimitiveType("nint");
        if (t == typeof(nuint)) return new PrimitiveType("nuint");
        // A reflected single-dimension array return (`MemoryStream.ToArray()` →
        // `byte[]`) is a first-class E# array, not an opaque external type — so it
        // indexes, iterates, and implicit-converts to a span like any `T[]`. Higher-rank
        // arrays fall through to the external fallback (E# arrays are single-dimension).
        if (t.IsArray && t.GetArrayRank() == 1)
            return new ArrayBoundType(MapRuntimeTypeToBoundType(t.GetElementType()!));
        // A reflected constructed generic maps STRUCTURED — base name + recursive
        // args — never rendered to a `Name<...>` string for someone to re-parse.
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return new ExternalType(
                RuntimeGenericBaseName(t),
                t.GetGenericArguments().Select(MapRuntimeTypeToBoundType).ToList());
        return new ExternalType(RuntimeTypeToEsharpName(t));
    }

    /// The base name for a constructed-generic `ExternalType`. A SIMPLE name when the
    /// type's namespace is in the searchable common set (`List`, `Task`, `Dictionary`,
    /// the `Esharp.Stdlib` types — all round-trip via the namespace search), a
    /// NAMESPACE-QUALIFIED name otherwise (`System.Runtime.CompilerServices.TaskAwaiter`,
    /// `ConfiguredTaskAwaitable`) so `FindOpenGenericByName` / `ResolveOpenGenericType`
    /// resolve it through `Type.GetType(qualified`N)` instead of erasing it to `object`.
    /// Mirrors the non-generic `RuntimeTypeToEsharpName` fallback, which already qualifies.
    internal static string RuntimeGenericBaseName(Type t)
    {
        // A nested type of a generic (`List<int>.Enumerator`) is itself IsGenericType —
        // it inherits the enclosing arguments — yet its own name carries no arity
        // backtick, so IndexOf returns -1. Take the whole name in that case rather than
        // slicing to a negative length.
        var tick = t.Name.IndexOf('`');
        var simple = tick >= 0 ? t.Name[..tick] : t.Name;
        var ns = t.Namespace;
        if (ns is null) return simple;
        return ns is "System" or "System.Collections.Generic"
               || UsingEnvironment.Instance.CommonNamespaces.Contains(ns)
            ? simple
            : $"{ns}.{simple}";
    }

    /// <summary>
    /// Produces an E#-style type name that <see cref="ResolveExternalRuntimeTypeByName"/> can
    /// round-trip back to a runtime Type. Handles generic constructed types
    /// (e.g. <c>List&lt;int&gt;</c>) and falls back to the full namespace-qualified name
    /// when the short name alone wouldn't resolve.
    /// </summary>
    static string RuntimeTypeToEsharpName(Type t)
    {
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var baseName = t.Name[..t.Name.IndexOf('`')];
            var args = string.Join(", ", t.GetGenericArguments().Select(RuntimeTypeToEsharpName));
            return $"{baseName}<{args}>";
        }

        // Short name works for System namespace and common types.
        // For anything else, use the full name so FindTypeByName can resolve it via
        // Type.GetType(fullName) even when the namespace isn't imported.
        if (t.Namespace is "System" or "System.Collections.Generic")
            return t.Name;

        return t.FullName ?? t.Name;
    }

    /// Map a (possibly open) runtime type to a BoundType, substituting generic-METHOD
    /// type parameters (`TService`) with caller-supplied BOUND types from `map`.
    /// Used to close a generic external extension's parameter types over explicit
    /// call-site type args when reflection's MakeGenericMethod can't be used (a module
    /// type arg has no runtime Type): `Func<IServiceProvider, TService>` +
    /// {TService→BatchProcessor} ⇒ `Func<IServiceProvider, BatchProcessor>` — structured
    /// all the way down, nothing rendered to a name and re-parsed.
    internal static BoundType MapRuntimeTypeWithBoundArgs(Type t, Dictionary<string, BoundType> map)
    {
        if (t.IsByRef) t = t.GetElementType()!;
        if (t.IsGenericParameter)
            return map.TryGetValue(t.Name, out var s) ? s : new ExternalType(t.Name);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return new ExternalType(
                RuntimeGenericBaseName(t),
                t.GetGenericArguments().Select(a => MapRuntimeTypeWithBoundArgs(a, map)).ToList());
        return MapRuntimeTypeToBoundType(t);
    }

    /// The simple name of a collection field's element type (`List<Pt>` → "Pt"),
    /// read off the structured type args. Used by the struct/class promotion
    /// heuristics, which key on bare type names — a pointer (`List<*Pt>`) or closed
    /// generic element never matches a data name, so only bare elements report.
    internal static string? ExtractCollectionElementType(BoundType type)
    {
        if (type is not ExternalType { Name: "List" or "IList" or "IEnumerable" or "ICollection", TypeArgs: [var elem] })
            return null;
        return elem switch
        {
            DataType { TypeArgs.Count: 0 } d => d.Name,
            ChoiceType { TypeArgs.Count: 0 } c => c.Name,
            ExternalType { TypeArgs.Count: 0 } e => e.Name,
            EnumType en => en.Name,
            PrimitiveType p => p.Name,
            _ => null,
        };
    }

    // === Type resolution ===

    // Resolve a type that appears as a *generic type argument*. A `*T` argument is
    // the heap-pointer form (`__Ptr_T` wrapper), never a managed `ref T` — the CLR
    // forbids by-ref generic arguments, and anything stored in a `List<*T>` /
    // `Pair<*T, …>` is the escaping wrapper. (Other nested ref positions — function-
    // pointer parameters, tuple elements — keep the ByRefBoundType form instead;
    // see ResolveNestedRef.)
    internal BoundType ResolveGenericArg(TypeSyntax arg) =>
        arg is PointerTypeSyntax p
            ? new HeapPointerBoundType(ResolveType(p.Inner))
            : ResolveType(arg);

    // A nested ref position (function-pointer parameter / return, tuple element,
    // Result/Chan argument) where a leading `*` is the managed by-ref form, NOT the
    // top-level flag that the binding sites strip. ResolveType itself drops a
    // top-level pointer to its pointee, so these positions re-introduce the ByRef.
    BoundType ResolveNestedRef(TypeSyntax t) =>
        t is PointerTypeSyntax p ? new ByRefBoundType(ResolveType(p.Inner)) : ResolveType(t);

    /// Resolve a type named by an expression-carried STRING (a constructor target
    /// `List<int>`, a dot-case choice name, a rendered conformance name) — the
    /// expression and bound-conformance layers still thread some type names as
    /// strings (a deferred concern). Parses the name through the real grammar, so no
    /// ad-hoc string splitting is reintroduced, then resolves structurally.
    internal BoundType ResolveTypeName(string name, SourceSpan reportAt = default)
    {
        // The mini-parse's spans point at the synthetic name buffer, not real
        // source — override diagnostic locations with the caller's span for the
        // duration of this resolution. Dies with the bound-layer de-stringing.
        var prev = _nameReportOverride;
        _nameReportOverride = reportAt;
        try
        {
            return ResolveType(new Parsing.Parser(name).Types.ParseTypeUntil(SyntaxTokenKind.EndOfFile));
        }
        finally
        {
            _nameReportOverride = prev;
        }
    }

    SourceSpan _nameReportOverride;

    /// The span namespace-scoping diagnostics report at: the syntax's own range,
    /// unless a string-keyed resolution (ResolveTypeName) supplied the real use site.
    SourceSpan ReportSpan(SourceSpan syntaxSpan) =>
        _nameReportOverride.IsValid ? _nameReportOverride : syntaxSpan;

    /// A bare type name (no `<...>`) that names a GENERIC user type resolves to its
    /// open registered definition. The arity comes from the bare-keyed decl tables
    /// (DataDecls / ChoiceDecls / InterfaceDecls), then keys into the arity-qualified
    /// registry. Returns false for non-generic names (handled by the direct lookup)
    /// and unknown names (handled by the external/primitive fallthrough).
    internal bool TryResolveOpenGenericByName(string name, out BoundType result)
    {
        var arity = _data.Symbols.FindType(name,
                        TypeSymbolKind.Struct, TypeSymbolKind.Class,
                        TypeSymbolKind.Union, TypeSymbolKind.RefUnion,
                        TypeSymbolKind.Interface)?.Arity ?? 0;
        if (arity > 0 && _data.Symbols.ResolveBound(name, arity) is { } t)
        {
            result = t;
            return true;
        }
        result = null!;
        return false;
    }

    /// Source type syntax → BoundType, a structural dispatch over the parsed type
    /// tree. A top-level pointer resolves to its pointee (the binding sites read the
    /// by-ref flags off the syntax via RefFlags); nested ref positions re-introduce
    /// the ByRef via ResolveNestedRef / ResolveGenericArg.
    internal BoundType ResolveType(TypeSyntax syntax)
    {
        var resolved = syntax switch
        {
            InferredTypeSyntax => InferredType.Instance,
            PointerTypeSyntax p => ResolveType(p.Inner),
            NullableTypeSyntax n => new NullableType(ResolveType(n.Inner)),
            FunctionPointerTypeSyntax f => new FunctionPointerType(
                f.ParameterTypes.Select(ResolveNestedRef).ToList(), ResolveNestedRef(f.ReturnType)),
            TupleTypeSyntax t => new TupleType(t.Elements.Select(ResolveNestedRef).ToList()) { ElementNames = t.ElementNames },
            ArrayTypeSyntax a => new ArrayBoundType(ResolveType(a.ElementType)),
            GenericTypeSyntax g => ResolveGeneric(g),
            NamedTypeSyntax n => ResolveNamed(n.Name, ReportSpan(n.Span)),
            _ => new ExternalType("object"),
        };
        // A type annotation that resolves to a nominal user type is a type USE —
        // reported so go-to-def / find-refs reach the declaration from a signature,
        // field, or local annotation. Only the named/generic leaves carry a symbol;
        // wrappers (`*T`, `T?`) recurse and report their inner.
        if (syntax is NamedTypeSyntax or GenericTypeSyntax)
        {
            if (TypeSymbolOf(resolved) is { } sym)
                _data.Sink.OnTypeResolved(sym, syntax.Span, Semantics.SymbolOccurrence.Use);
            // External / primitive annotations (`-> string`, `s: StringBuilder`)
            // report the interned external symbol — hover on a return type names
            // the type, never the enclosing function.
            else if (_data.Sink is not Semantics.NullSemanticSink
                && resolved is ExternalType or PrimitiveType)
            {
                var rt = resolved is ExternalType ext
                    ? ResolveExternalToRuntime(ext)
                    : ResolveExternalRuntimeTypeByName(((PrimitiveType)resolved).Name);
                if (rt is not null)
                    _data.Sink.OnTypeResolved(_data.Externals.ForType(rt), syntax.Span, Semantics.SymbolOccurrence.Use);
            }
        }
        return resolved;
    }

    static Esharp.Symbols.TypeSymbol? TypeSymbolOf(BoundType t) => t switch
    {
        DataType d => d.Symbol,
        ChoiceType c => c.Symbol,
        EnumType e => e.Symbol,
        InterfaceType i => i.Symbol,
        NamedDelegateType nd => nd.Symbol,
        StaticFuncType s => s.Symbol,
        _ => null,
    };

    /// A generic instantiation `Name<Args>`. A registered base (user / C#-seed type)
    /// takes precedence over the runtime intrinsics, so a `data Result<T,E>` or seed
    /// `Result`2` wins over the builtin; otherwise the intrinsic `Result`/`chan`
    /// forms fire, and finally an external constructed generic (`Dictionary<…>`).
    BoundType ResolveGeneric(GenericTypeSyntax g)
    {
        // Namespace-qualified base (`A.Widget<int>`): strip a known-namespace prefix.
        var baseName = g.Name;
        var qualified = false;
        var firstDot = baseName.IndexOf('.');
        if (firstDot > 0 && _data.Symbols.IsKnownNamespace(baseName[..firstDot]))
        {
            baseName = baseName[(firstDot + 1)..];
            qualified = true;
        }

        // User generic instantiation: the base is registered (arity-keyed `Name`N`
        // first, then the bare name for C#-adapters / arity-0 fallbacks).
        if (_data.Symbols.ResolveBound(baseName, g.Args.Count) is { } genericBase)
        {
            CheckTypeNamespaceScope(baseName, qualified, ReportSpan(g.Span));
            return CloseGenericBase(genericBase, g.Args.Select(ResolveGenericArg).ToList());
        }

        // Runtime intrinsics.
        if (baseName == "Result" && g.Args.Count == 2)
            return new ResultType(ResolveNestedRef(g.Args[0]), ResolveNestedRef(g.Args[1]));
        if (baseName is "Chan" or "chan" && g.Args.Count == 1)
            return new ChanType(ResolveNestedRef(g.Args[0]));

        // External constructed generic — structured base name + recursively-resolved
        // args. Downstream phases (and the IL backend) walk TypeArgs, never a string.
        return new ExternalType(baseName, g.Args.Select(ResolveGenericArg).ToList());
    }

    /// Instantiate a generic base over ALREADY-BOUND type arguments — the
    /// substitution-side mirror of ResolveGeneric, for callers (generic-param
    /// substitution) whose arguments are BoundType, not syntax. Registry base wins
    /// (so `Wrap<U>` closes to the user `DataType`, never an ExternalType shell),
    /// then the Result/Chan intrinsics, then an external constructed generic.
    internal BoundType InstantiateGeneric(string name, IReadOnlyList<BoundType> args)
    {
        var baseName = name;
        var firstDot = baseName.IndexOf('.');
        if (firstDot > 0 && _data.Symbols.IsKnownNamespace(baseName[..firstDot]))
            baseName = baseName[(firstDot + 1)..];

        if (_data.Symbols.ResolveBound(baseName, args.Count) is { } genericBase)
            return CloseGenericBase(genericBase, args);

        if (baseName == "Result" && args.Count == 2)
            return new ResultType(args[0], args[1]);
        if (baseName is "Chan" or "chan" && args.Count == 1)
            return new ChanType(args[0]);

        return new ExternalType(baseName, args);
    }

    /// Close a registry-resolved generic base over bound arguments, preserving the
    /// base's kind (data / choice / interface). A non-generic registry hit passes
    /// through unchanged (C#-adapter / arity-0 fallback keys).
    static BoundType CloseGenericBase(BoundType genericBase, IReadOnlyList<BoundType> boundArgs) => genericBase switch
    {
        DataType dt when dt.TypeParameters.Count > 0
            => dt with { TypeArguments = boundArgs.ToList() },
        ChoiceType ct when ct.TypeParameters.Count > 0 => ct with { TypeArguments = boundArgs.ToList() },
        InterfaceType it when it.Decl.TypeParameters.Count > 0 => it with { TypeArguments = boundArgs.ToList() },
        _ => genericBase,
    };

    /// A bare (possibly dotted) type name. Alias substitution, namespace-qualifier
    /// resolution, the symbol table's bound views, open-generic-by-name, primitives, and the
    /// external/reflection fallback — operating on the leaf name only.
    BoundType ResolveNamed(string rawName, SourceSpan span)
    {
        var name = rawName;
        // C#-style type alias (`using Baz = "Full.Type"`): substitute to the aliased path.
        if (_currentTypeAliases.TryGetValue(name, out var aliasTarget))
            name = aliasTarget;

        // Qualified `NS.Type`: a dotted name whose prefix is a known E# namespace is an
        // explicit cross-namespace reference — resolve its namespace-specific entry
        // first (so `A.Widget` reaches A's type even when `Widget` also exists in B),
        // else strip the prefix and resolve the remainder (always in scope).
        var qualified = false;
        var firstDot = name.IndexOf('.');
        if (firstDot > 0 && _data.Symbols.IsKnownNamespace(name[..firstDot]))
        {
            if (_data.Symbols.ResolveBound(name, 0) is { } qreg)
                return qreg;
            name = name[(firstDot + 1)..];
            qualified = true;
        }

        // Qualified nested type `Outer.Inner` (or `Ns.Outer.Inner`, `A.B.Inner`): a
        // dotted name whose leading run names a type, with nested segments after. The
        // C# way to reach a nested type from outside its enclosing type.
        if (name.IndexOf('.') > 0 && _data.Symbols.ResolveNestedQualified(name, 0)?.BoundView is { } nestedQ)
            return nestedQ;

        if (_data.Symbols.ResolveBound(name, 0) is { } registered)
        {
            CheckTypeNamespaceScope(name, qualified, span);
            return registered;
        }

        // Bare nested type within the enclosing type's scope — a sibling/own nested
        // name visible inside `Outer` (and deeper) but not globally. Honors C# scoping:
        // outside the enclosing type the bare name does NOT resolve here.
        if (!qualified && CurrentEnclosingType is { } enclosing
            && _data.Symbols.ResolveNestedInScope(name, 0, enclosing)?.BoundView is { } nestedScoped)
            return nestedScoped;

        // A bare reference to a generic type's NAME resolves to its OPEN definition
        // (generics are keyed `Name`N`, so the bare lookup above misses them).
        if (TryResolveOpenGenericByName(name, out var openGeneric))
        {
            CheckTypeNamespaceScope(name, qualified, span);
            return openGeneric;
        }

        // Primitives, else external (reflection-resolved) type.
        BoundType resolved = name switch
        {
            "void" => new VoidType(),
            "int" or "string" or "bool" or "float" or "double" or "byte" or "char" or "long" or "short"
                or "uint" or "ulong" or "ushort" or "sbyte" or "nint" or "nuint" or "decimal" => new PrimitiveType(name),
            "Guid" or "DateTimeOffset" or "DateTime" or "TimeSpan" => new PrimitiveType(name),
            _ => new ExternalType(name),
        };

        // A bare external name resolving to distinct CLR types across imported
        // namespaces is ambiguous (ES2151). Skipped for qualified refs and primitives.
        if (!qualified && resolved is ExternalType)
            CheckExternalTypeAmbiguity(name, span);

        return resolved;
    }

    /// The declaration symbol behind a bound `data` use — the carrier of fields,
    /// members, and the base chain. The symbol link threaded at construction is
    /// authoritative; otherwise resolve by (name, arity[, namespace]), with a
    /// last-resort simple-name probe for DataTypes synthesized without type args.
    internal TypeSymbol? SymbolOf(DataType dt)
    {
        if (dt.Symbol is { } linked) return linked;
        var arity = dt.TypeArgs.Count;
        if (dt.Namespace is { } ns && _data.Symbols.TryGet(dt.Name, arity, ns) is { } nsSym)
            return nsSym;
        return _data.Symbols.ResolveSymbol(dt.Name, arity)
            ?? _data.Symbols.FindType(dt.Name, TypeSymbolKind.Struct, TypeSymbolKind.Class);
    }


    /// A namespace is "in scope" for bare-name resolution when it is the unit's own
    /// namespace or pulled in by a `using "NS"` (`_currentNamespaceImports` holds
    /// both internal-namespace and external-BCL using paths; only matching internal
    /// names matter here).
    internal bool IsNamespaceInScope(string ns)
        => ns == CurrentNamespace || _currentNamespaceImports.Contains(ns);

    /// Enforce C#-like namespace scoping on a bare user-type reference. A qualified
    /// `NS.Type` is always allowed (the namespace was named explicitly). A bare name
    /// errors when its declaring namespace is neither the current one nor imported
    /// (ES2150), or when it is visible from two in-scope namespaces at once (ES2151,
    /// ambiguous — the user must qualify).
    void CheckTypeNamespaceScope(string simpleName, bool qualified, SourceSpan span)
    {
        if (qualified) return;
        var declNamespaces = _data.Symbols.NamespacesOf(simpleName);
        if (declNamespaces.Count == 0)
        {
            // Not a user-declared type — check for an ambiguous external (BCL /
            // referenced-assembly) name across imported namespaces.
            CheckExternalTypeAmbiguity(simpleName, span);
            return;
        }

        if (declNamespaces.Count > 1)
        {
            var inScope = declNamespaces.Where(IsNamespaceInScope).ToList();
            if (inScope.Count > 1)
            {
                _data.Diagnostics.Report(span,
                    $"ES2151: type '{simpleName}' is ambiguous — declared in {string.Join(" and ", inScope.Select(n => $"namespace '{n}'"))}. " +
                    $"Qualify the reference (e.g. '{inScope[0]}.{simpleName}').");
                return;
            }
        }

        if (!declNamespaces.Any(IsNamespaceInScope))
        {
            var declNs = declNamespaces[0];
            _data.Diagnostics.Report(span,
                $"ES2150: type '{simpleName}' is in namespace '{declNs}' — add `using \"{declNs}\"` or qualify as `{declNs}.{simpleName}`.");
        }
    }

    // A bare external (reflection-resolved) type name that resolves to DISTINCT CLR
    // types in more than one imported namespace is ambiguous — the same trap ES2151
    // catches for user types. Example: `Binder` is both System.Reflection.Binder and
    // Esharp.BoundTree.Binder when both namespaces are `using`-imported. Force
    // qualification rather than silently binding whichever the search hits first.
    internal void CheckExternalTypeAmbiguity(string simpleName, SourceSpan span = default)
    {
        if (simpleName.IndexOf('<') >= 0 || simpleName.IndexOf('.') >= 0) return; // generic / already-qualified
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var namespaces = new List<string>();
        var distinctTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in _currentNamespaceImports)
        {
            foreach (var asm in assemblies)
            {
                var t = asm.GetType($"{ns}.{simpleName}");
                if (t is not null)
                {
                    if (distinctTypes.Add(t.FullName ?? t.Name))
                        namespaces.Add(ns);
                    break;
                }
            }
        }
        if (distinctTypes.Count > 1)
            _data.Diagnostics.Report(span,
                $"ES2151: type '{simpleName}' is ambiguous — found in {string.Join(" and ", namespaces.Select(n => $"namespace '{n}'"))}. " +
                $"Qualify the reference (e.g. '{namespaces[0]}.{simpleName}').");
    }

    // Resolve an ExternalType name to a runtime Type.
    // Used only for member type inference — pragmatic, best-effort.
    /// Runtime Type of a structured external — close the open generic over the
    /// arguments' runtime types. An argument with no runtime form (a user `data`, a
    /// type parameter) degrades to `object`, matching the old string path: the
    /// runtime view is for BCL member reflection, where `List<Json>` legitimately
    /// reflects as `List<object>`. Null only when the open type itself is unknown.
    internal Type? ResolveExternalToRuntime(ExternalType ext)
    {
        if (ext.TypeArgs.Count == 0) return ResolveExternalRuntimeTypeByName(ext.Name);
        var open = FindOpenGenericByName(ext.Name, ext.TypeArgs.Count);
        if (open is null) return null;
        var args = new Type[ext.TypeArgs.Count];
        for (var i = 0; i < args.Length; i++)
        {
            // Nullable wrapping mirrors the old string path: a value-type `T?` arg is
            // Nullable<T>; a reference-type `T?` stays T.
            var a = ext.TypeArgs[i];
            var rt = a is NullableType na
                ? ResolveBoundTypeToRuntime(na.Inner) is { } inner
                    ? inner.IsValueType ? typeof(Nullable<>).MakeGenericType(inner) : inner
                    : null
                : ResolveBoundTypeToRuntime(a);
            args[i] = rt ?? typeof(object);
        }
        try { return open.MakeGenericType(args); }
        catch { return null; }
    }

    internal Type? ResolveExternalRuntimeTypeByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        if (_currentStaticImports.TryGetValue(name, out var staticImport))
            name = staticImport;

        // Nullable suffix: a value type `int?` → `Nullable<int>`; a reference type
        // `string?` is just `string` at runtime (the `?` is an annotation). This keeps
        // a `Func<string, string?>` generic arg from erasing to `object`.
        if (name.Length > 1 && name.EndsWith('?'))
        {
            var inner = ResolveExternalRuntimeTypeByName(name[..^1].Trim());
            if (inner is null) return null;
            return inner.IsValueType ? typeof(Nullable<>).MakeGenericType(inner) : inner;
        }

        // Constructed generics no longer arrive here as a name string — the
        // structured ResolveExternalToRuntime closes them over their TypeArgs. This
        // path resolves bare/primitive names (member/flat-name reflection lookups).
        return name switch
        {
            "int" => typeof(int),
            "long" => typeof(long),
            "double" => typeof(double),
            "float" => typeof(float),
            "bool" => typeof(bool),
            "string" => typeof(string),
            "byte" => typeof(byte),
            "short" => typeof(short),
            "char" => typeof(char),
            "decimal" => typeof(decimal),
            "sbyte" => typeof(sbyte),
            "uint" => typeof(uint),
            "ulong" => typeof(ulong),
            "ushort" => typeof(ushort),
            "nint" => typeof(nint),
            "nuint" => typeof(nuint),
            // The CLR keyword aliases the binder carries as bare `ExternalType` names —
            // `Type.GetType("object")` / `("void")` return null (the types are `Object`
            // / `Void`), so without these a generic arg of `object` (`List<object>`)
            // resolves to null, spuriously diverting member-return inference onto the
            // symbolic-substitution path. Map them like any other primitive.
            "object" => typeof(object),
            "void" => typeof(void),
            _ => FindTypeByName(name),
        };
    }

    // The reflection type-name parser THROWS ("The given assembly name was invalid")
    // rather than returning null when a name contains a character it reads as
    // assembly-qualified syntax (`,`, `[`, a bad backtick arity). A name reaching type
    // resolution is arbitrary source — a value name, a mangled tuple/generic — so a
    // parse failure must mean "not a type here", never a compiler crash.
    static Type? SafeGetType(string name)
    {
        try { return Type.GetType(name); }
        catch (Exception e) when (e is ArgumentException or System.IO.FileLoadException or System.IO.FileNotFoundException or TypeLoadException or BadImageFormatException) { return null; }
    }

    static Type? SafeAsmGetType(System.Reflection.Assembly asm, string name)
    {
        try { return asm.GetType(name); }
        catch (Exception e) when (e is ArgumentException or System.IO.FileLoadException or System.IO.FileNotFoundException or TypeLoadException or BadImageFormatException) { return null; }
    }

    internal Type? FindTypeByName(string name)
    {
        // Already-qualified / assembly-qualified name (e.g. "System.Text.Json.JsonElement").
        if (SafeGetType(name) is { } qualified) return qualified;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        // Tier 1: exact (top-level) name in any loaded assembly.
        foreach (var asm in assemblies)
            if (SafeAsmGetType(asm, name) is { } exact) return exact;

        // Tier 2: explicit `using` imports win over the implicit standard set, and
        // each tier is searched across ALL assemblies — an assembly-by-assembly loop
        // would let a common-namespace match in CoreLib shadow an imported match in
        // a later assembly. (Cross-import ambiguity is reported as ES2151.)
        foreach (var ns in _currentNamespaceImports)
            foreach (var asm in assemblies)
                if (SafeAsmGetType(asm, $"{ns}.{name}") is { } imported) return imported;

        // Tier 3: the curated common namespaces — off when the common search is disabled.
        if (_searchCommonNamespaces)
            foreach (var ns in UsingEnvironment.Instance.CommonNamespaces)
                foreach (var asm in assemblies)
                    if (SafeAsmGetType(asm, $"{ns}.{name}") is { } common) return common;

        // Tier 4: force-load the backing assembly. A common namespace whose assembly is
        // not yet in the AppDomain (System.Threading.Channels.dll &c.) is invisible to the
        // loaded-assembly tiers above; force-load it from the explicit imports, then the
        // auto-search set, exactly as the emitter does — so binding and emission agree.
        foreach (var ns in _currentNamespaceImports)
            if (UsingEnvironment.ForceLoadType(ns, name) is { } t) return t;
        foreach (var ns in AutoSearchNamespaces)
            if (UsingEnvironment.ForceLoadType(ns, name) is { } t) return t;

        return null;
    }

    internal Type? FindOpenGenericByName(string baseName, int arity)
    {
        var mangled = $"{baseName}`{arity}";
        if (SafeGetType(mangled) is { } qualified) return qualified;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var asm in assemblies)
            if (SafeAsmGetType(asm, mangled) is { } exact) return exact;

        foreach (var ns in _currentNamespaceImports)
            foreach (var asm in assemblies)
                if (SafeAsmGetType(asm, $"{ns}.{mangled}") is { } imported) return imported;

        if (_searchCommonNamespaces)
            foreach (var ns in UsingEnvironment.Instance.CommonNamespaces)
                foreach (var asm in assemblies)
                    if (SafeAsmGetType(asm, $"{ns}.{mangled}") is { } common) return common;

        // Tier 4: force-load the backing assembly for an unqualified open generic
        // (Channel`1, ChannelWriter`1, …) the loaded-assembly tiers can't see.
        foreach (var ns in _currentNamespaceImports)
            if (UsingEnvironment.ForceLoadType(ns, mangled) is { } t) return t;
        foreach (var ns in AutoSearchNamespaces)
            if (UsingEnvironment.ForceLoadType(ns, mangled) is { } t) return t;

        return null;
    }

    /// Best-effort runtime (reflection) view of a bound type — primitives and
    /// structured externals only; user types have no runtime Type at compile time.
    internal Type? ResolveBoundTypeToRuntime(BoundType t) => t switch
    {
        PrimitiveType p => p.Name switch
        {
            "int" => typeof(int),
            "long" => typeof(long),
            "double" => typeof(double),
            "float" => typeof(float),
            "bool" => typeof(bool),
            "string" => typeof(string),
            "byte" => typeof(byte),
            "short" => typeof(short),
            "char" => typeof(char),
            "decimal" => typeof(decimal),
            "sbyte" => typeof(sbyte),
            "uint" => typeof(uint),
            "ulong" => typeof(ulong),
            "ushort" => typeof(ushort),
            "nint" => typeof(nint),
            "nuint" => typeof(nuint),
            "Guid" => typeof(Guid),
            "DateTime" => typeof(DateTime),
            "DateTimeOffset" => typeof(DateTimeOffset),
            "TimeSpan" => typeof(TimeSpan),
            _ => null,
        },
        ExternalType ext => ResolveExternalToRuntime(ext),
        // A single-dimension array is a real runtime `T[]`, so it closes a generic
        // argument (`Dictionary<string, byte[]>`) as `byte[]` instead of erasing to
        // `object` — which otherwise mistypes indexer/`TryGetValue` results and the
        // out-var they bind.
        ArrayBoundType arr => ResolveBoundTypeToRuntime(arr.ElementType) is { } el ? el.MakeArrayType() : null,
        _ => null,
    };


    // === Top-level pointer / by-ref annotation facts ===

    /// A top-level `*T` / `readonly *T` annotation carries its pointer-ness in these
    /// flags (ResolveType drops the leading pointer to the pointee). `BoundParameter`
    /// and the field/promotion machinery read it through here.
    internal static (bool ByRef, bool ReadOnlyByRef) RefFlags(TypeSyntax syntax) =>
        syntax is PointerTypeSyntax p ? (true, p.ReadOnly) : (false, false);

    /// The base name of a type annotation (no generic args, no pointer/nullable
    /// wrapping) — used for receiver-type detection and error messages, matching the
    /// old flag-stripped `.Name`.
    internal static string TypeSyntaxLeafName(TypeSyntax t) => t switch
    {
        NamedTypeSyntax n => n.Name,
        GenericTypeSyntax g => g.Name,
        PointerTypeSyntax p => TypeSyntaxLeafName(p.Inner),
        NullableTypeSyntax nu => TypeSyntaxLeafName(nu.Inner),
        InferredTypeSyntax => "var",
        _ => "?",
    };

    /// The flat declared name of a base/interface entry — the key the binder's
    /// name-keyed satisfaction routing and base-class chain walk compare against
    /// (DataDecl lookups, ES2120/2128 checks). The bound node carries conformance
    /// as structured types now; this stays a binder-internal comparison helper.
    /// Matches the flat name the syntax carries: no spaces, generic args inline.
    internal static string InterfaceName(TypeSyntax t) => t switch
    {
        NamedTypeSyntax n => n.Name,
        GenericTypeSyntax g => $"{g.Name}<{string.Join(",", g.Args.Select(InterfaceName))}>",
        NullableTypeSyntax nu => InterfaceName(nu.Inner) + "?",
        PointerTypeSyntax p => "*" + InterfaceName(p.Inner),
        _ => "?",
    };

    /// Resolves a type reference, converting `*T` to the `__Ptr_T` wrapper
    /// (`HeapPointerBoundType`). Used for return types and explicitly-typed local
    /// `*T` bindings — positions that escape the frame, so they take the
    /// first-class, nullable wrapper representation for both value `data` and
    /// primitives. `*refData` is an error. `readonly *T` keeps the managed `in T`
    /// form. Parameters are handled separately (they may downgrade to a managed
    /// pointer when provably non-escaping).
    internal BoundType ResolveHeapPointerAware(TypeSyntax syntax)
    {
        if (syntax is PointerTypeSyntax p)
        {
            var inner = ResolveType(p.Inner);
            if (!p.ReadOnly && !StarClassError(inner, syntax.Span) && inner is DataType or PrimitiveType)
                return new HeapPointerBoundType(inner);
            return inner;
        }
        return ResolveType(syntax);
    }

    /// `*T` where T is a `class` is illegal — a `class` is already a CLR
    /// reference with identity, so a pointer to it is meaningless. Reports ES2003
    /// and returns true so callers leave the bare reference type in place (the
    /// `*` is simply dropped, no cascade). Returns false for value `data` and
    /// primitives, which are the legitimate `*T` pointees.
    internal bool StarClassError(BoundType inner, SourceSpan span)
    {
        if (inner is DataType dt && ClassifyData(dt.Name) == DataClassification.Class)
        {
            _data.Diagnostics.Report(span,
                $"ES2003: '*{dt.Name}' is illegal — '{dt.Name}' is already a reference type (class); drop the '*'.");
            return true;
        }
        return false;
    }

    // === `data` CLR-form classification (struct vs class) ===

    internal static readonly Dictionary<string, int> PrimitiveSizes = new(StringComparer.Ordinal)
    {
        ["int"] = 4, ["float"] = 4, ["double"] = 8, ["bool"] = 1,
        ["byte"] = 1, ["char"] = 2, ["long"] = 8, ["short"] = 2,
        ["uint"] = 4, ["ulong"] = 8, ["ushort"] = 2, ["sbyte"] = 1,
        ["decimal"] = 16, ["nint"] = 8, ["nuint"] = 8, ["Guid"] = 16, ["DateTime"] = 8,
        ["DateTimeOffset"] = 16, ["TimeSpan"] = 8,
    };

    internal DataClassification ClassifyData(string name)
    {
        // The CLR form lives on the symbol: seeded at registration (class →
        // class, data → struct) and revised once by the promotion pass. An
        // unknown name is treated as a class (reference semantics) — the safe
        // default for externals.
        var sym = _data.Symbols.FindType(name, TypeSymbolKind.Struct, TypeSymbolKind.Class);
        return sym?.Classification ?? DataClassification.Class;
    }

    internal (bool isValue, int size) FieldMetrics(BoundType type) => type switch
    {
        PrimitiveType p => PrimitiveSizes.TryGetValue(p.Name, out var s) ? (true, s) : (false, 0),
        DataType d => ClassifyData(d.Name) == DataClassification.Struct ? (true, DataSize(d.Name)) : (false, 0),
        HeapPointerBoundType => (false, 0), // *T is a reference (wrapper class), 8 bytes
        ChoiceType => (true, 8),
        EnumType => (true, 4),
        _ => (false, 0),
    };

    internal int DataSize(string name)
    {
        if (_data.Symbols.DataDecl(name) is not { } decl) return 0;
        var total = 0;
        foreach (var field in decl.Fields)
        {
            var (_, size) = FieldMetrics(ResolveType(field.Type));
            total += size;
        }
        return total;
    }

    // === Member-TYPE resolution ===

    internal BoundType ResolveMemberType(BoundType targetType, string memberName)
    {
        // Auto-deref through pointer values and managed property/ref locals. A
        // durable `&property` local is still semantically a location to T, so
        // member lookup occurs on T rather than on the CLR byref carrier.
        if (targetType is HeapPointerBoundType hp)
            return ResolveMemberType(hp.Inner, memberName);
        if (targetType is ByRefBoundType br)
            return ResolveMemberType(br.Inner, memberName);

        // Tuple field access: .Item1, .Item2, ...
        if (targetType is TupleType tt && memberName.StartsWith("Item")
            && int.TryParse(memberName.AsSpan(4), out var idx) && idx >= 1 && idx <= tt.ElementTypes.Count)
        {
            return tt.ElementTypes[idx - 1];
        }

        // Named tuple element access: `t.name` → the type of the element labeled `name`.
        if (targetType is TupleType named && named.ElementNames is { } labels)
        {
            var labelIdx = labels.ToList().IndexOf(memberName);
            if (labelIdx >= 0) return named.ElementTypes[labelIdx];
        }

        // Result<T,E> intrinsic members: `.Value` → T, `.Error` → E,
        // `.IsOk`/`.IsError` → bool. Without this, `r.Value.age` types `r.Value`
        // as `var` and the trailing `.age` field access fails to resolve.
        if (targetType is ResultType resultTarget)
        {
            return memberName switch
            {
                "Value" => resultTarget.OkType,
                "Error" => resultTarget.ErrorType,
                "IsOk" or "IsError" => new PrimitiveType("bool"),
                _ => InferredType.Instance,
            };
        }

        // `T[]` members: `.Length` is an int; an indexed element is the element type.
        if (targetType is ArrayBoundType arrType)
        {
            return memberName switch
            {
                "Length" => new PrimitiveType("int"),
                _ => InferredType.Instance,
            };
        }

        if (targetType is DataType dt)
        {
            // Walk the inheritance chain on the symbol spine — own fields first,
            // then base class fields. The symbol carries (ns, name, arity)
            // identity, so same-name declarations of different arity (Pair<A> vs
            // Pair<A,B>) and cross-namespace collisions resolve to the right
            // declaration by construction.
            var cursorSym = SymbolOf(dt);
            while (cursorSym is not null)
            {
                var bf = cursorSym.Fields.FirstOrDefault(f => f.Name == memberName);
                if (bf?.Bound is { } bound)
                    return SubstituteGenericParams(bound, dt);

                // Promoted access through embedded fields
                foreach (var embed in cursorSym.Fields.Where(f => f.IsEmbedded))
                {
                    if (embed.Bound is not { } embedBound) continue;
                    var innerType = embedBound is HeapPointerBoundType hp2 ? hp2.Inner : embedBound;
                    var promoted = ResolveMemberType(innerType, memberName);
                    if (promoted is not InferredType)
                        return promoted;
                }
                // Syntax fallback for a field whose type has not been resolved
                // onto the symbol (partial symbols from broken declarations).
                if (cursorSym.Decl is DataDeclarationSyntax dataDecl
                    && dataDecl.Fields.FirstOrDefault(f => f.Name == memberName) is { } field)
                    return ResolveHeapPointerAware(field.Type);
                cursorSym = cursorSym.BaseType;
            }

            // Inherited BCL-base members. An E# class extending a framework type
            // (`class ModelFileException : Exception`) exposes that base's public
            // instance members — `ex.Message`, `ex.InnerException`. The symbol walk above
            // only covers E# base classes; a BCL base has no E# symbol, so reflect it here
            // (codegen already walks the emitted Cecil base chain to the getter).
            if (SymbolOf(dt)?.Decl is DataDeclarationSyntax classDecl)
                foreach (var baseSyntax in classDecl.Interfaces)
                {
                    var runtimeBase = ResolveExternalRuntimeTypeByName(TypeSyntaxLeafName(baseSyntax));
                    if (runtimeBase is null || runtimeBase.IsInterface) continue;
                    if (ResolveBclInstanceMemberType(runtimeBase, memberName) is { } inherited)
                        return inherited;
                }
        }

        // Substitute the data type's open generic parameters with the closed
        // type arguments when resolving a field's declared type. Without this,
        // `p.first` on `p: Pair<string,int>` reports type `A` (the open param),
        // and downstream emit decisions (string-concat vs add, boxing, etc.)
        // see the wrong type. Walks recursive shapes too — `List<T>` reaches
        // `List<int>` when T=int via the outer substitution.
        BoundType SubstituteGenericParams(BoundType raw, DataType decl)
        {
            if (decl.TypeArgs.Count == 0) return raw;
            if (SymbolOf(decl)?.Decl is not DataDeclarationSyntax declSyntax)
                return raw;
            var paramNames = declSyntax.TypeParameters;
            if (paramNames.Count != decl.TypeArgs.Count) return raw;
            var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
            for (var i = 0; i < paramNames.Count; i++)
                map[paramNames[i]] = decl.TypeArgs[i];
            return Substitute(raw, map);
        }

        BoundType Substitute(BoundType t, Dictionary<string, BoundType> map) => t switch
        {
            PrimitiveType prim when map.TryGetValue(prim.Name, out var arg) => arg,
            ExternalType { TypeArgs.Count: 0 } ext when map.TryGetValue(ext.Name, out var arg2) => arg2,
            ExternalType ext when ext.TypeArgs.Count > 0
                => ext with { TypeArguments = ext.TypeArgs.Select(a => Substitute(a, map)).ToList() },
            DataType inner when inner.TypeArgs.Count > 0
                => inner with { TypeArguments = inner.TypeArgs.Select(a => Substitute(a, map)).ToList() },
            HeapPointerBoundType hp2 => new HeapPointerBoundType(Substitute(hp2.Inner, map)),
            _ => t,
        };

        // Protocol member resolution: look up method return type, or a property
        // requirement's type, from the protocol declaration.
        if (targetType is InterfaceType pt && _data.Symbols.InterfaceDecl(pt.Name) is { } protoDecl)
        {
            var method = protoDecl.Methods.FirstOrDefault(m => m.Name == memberName);
            if (method is not null)
                return ResolveType(method.ReturnType);
            if (protoDecl.Properties?.FirstOrDefault(p => p.Name == memberName) is { } prop)
                return ResolveType(prop.Type);
        }

        // static member resolution: Foo.bar / Foo.X — look up function or field
        // on the static-func body's declarations. An arity-0 data type may own the
        // host too (`class TaskScope` + `static TaskScope`), which emits one
        // CLR type with both instance and static members.
        var staticHostName = targetType switch
        {
            StaticFuncType sft => sft.Name,
            DataType dataHost => dataHost.Name,
            _ => null,
        };
        if (staticHostName is not null && _data.Symbols.StaticFuncDecl(staticHostName) is { } sfDecl)
        {
            var fn = sfDecl.Functions.FirstOrDefault(f => f.Name == memberName);
            if (fn is not null)
            {
                // If the function omitted its return type and the static has a
                // `returns Type` clause, that clause supplies the effective type.
                var rt = fn.HasExplicitReturnType ? fn.ReturnType
                       : (sfDecl.DefaultReturns?.Type ?? fn.ReturnType);
                return ResolveType(rt);
            }
            var ff = sfDecl.Fields.FirstOrDefault(f => f.Name == memberName);
            if (ff is not null)
                return ResolveType(ff.Type);
        }

        // C#-defined type from a sibling .cs file in the same project. The handle's
        // Members surface includes fields, properties, and methods; field/property
        // lookups return the member's declared type. Method lookup returns the
        // method's return type so member-access call sites get the right signature.
        if (targetType is ExternalCSharpType cs)
        {
            foreach (var member in cs.Handle.Members)
            {
                if (member.Name != memberName) continue;
                return member.ReturnType;
            }
            // Walk base class chain — C# fields can be inherited.
            var baseHandle = cs.Handle.BaseType;
            while (baseHandle is not null)
            {
                foreach (var member in baseHandle.Members)
                {
                    if (member.Name == memberName)
                        return member.ReturnType;
                }
                baseHandle = baseHandle.BaseType;
            }
        }

        // BCL property reflection: `dict.Count` on `Dictionary<K,V>` → int.
        // Resolve the target's runtime Type, look up the property by name, and map back.
        if (targetType is ExternalType ext)
        {
            // Ref-choice case subclass: ExternalType("Expr_literal") refers to a
            // compiler-synthesized subclass of `Expr`. Reflection won't find it
            // (the type is only materialized in the emitted assembly), so resolve
            // via the parent choice declaration's payloads.
            var caseField = ResolveRefChoiceCaseField(ext.Name, memberName);
            if (caseField is not null)
                return caseField;

            // Symbolic generic member resolution. When a type argument cannot close to a
            // runtime Type — the enclosing type's own generic parameter (`Channel<T>`
            // inside `Chan<T>`), or a user `data` — resolving the member through
            // `ResolveExternalToRuntime` erases that arg to `object` (`Channel<object>`),
            // and the call site emits unverifiable IL (`Found 'T', Expected 'object'`).
            // Resolve the member on the OPEN definition instead and substitute its
            // parameters BY POSITION with `ext`'s bound args, so `Channel<T>.Writer`
            // resolves to `ChannelWriter<T>` with `T` preserved — the basis for an E#
            // generic type wrapping a BCL generic.
            if (ext.TypeArgs.Count > 0 && ext.TypeArgs.Any(a => ResolveBoundTypeToRuntime(a) is null)
                && ResolveExternalMemberSymbolic(ext, memberName) is { } symbolic)
                return symbolic;

            var runtime = ResolveExternalToRuntime(ext);
            if (runtime is not null && ResolveRuntimeMemberType(runtime, memberName) is { } extMember)
                return extMember;
        }

        // BCL members on primitive targets: `s.Length` on string → int, etc.
        // `string`/`int`/`char`/... are `PrimitiveType`, not `ExternalType`, so
        // they don't reach the branch above; route them through the same
        // reflection lookup against their CLR runtime type.
        if (targetType is PrimitiveType primTarget)
        {
            var runtime = ResolveExternalRuntimeTypeByName(primTarget.Name);
            if (runtime is not null && ResolveRuntimeMemberType(runtime, memberName) is { } primMember)
                return primMember;
        }

        return InferredType.Instance;
    }

    // Resolve a property/field type on a runtime type, walking inherited
    // interfaces. Type.GetProperty/GetField on an interface does NOT surface
    // members declared on its base interfaces — e.g. `IReadOnlyList<int>.Count`
    // lives on IReadOnlyCollection<T> — so without this the member is left typed
    // `var`/object, which breaks overload resolution and value-type boxing at the
    // use site (a direct `WriteLine(list.Count)`, an `object` parameter, etc.).
    // Concrete classes already walk their base *class* chain via GetProperty.
    // Resolve `memberName` on the OPEN definition of `ext` (e.g. `Channel`1`), then
    // rewrite the member's type — which references the OWNING type's generic parameters
    // by position — with `ext`'s bound arguments. Keeps a generic parameter / user type
    // symbolic instead of erasing it through the runtime reflection round-trip.
    // A public instance member's type on a BCL type, walking its base classes
    // (`FlattenHierarchy`): a property/field type, or a zero-arg method's return type.
    // Null when the type has no such member.
    static BoundType? ResolveBclInstanceMemberType(Type type, string memberName)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
        if (type.GetProperty(memberName, flags) is { } p) return MapRuntimeTypeToBoundType(p.PropertyType);
        if (type.GetField(memberName, flags) is { } f) return MapRuntimeTypeToBoundType(f.FieldType);
        var m = type.GetMethods(flags).FirstOrDefault(x => x.Name == memberName && x.GetParameters().Length == 0);
        if (m is not null && m.ReturnType != typeof(void)) return MapRuntimeTypeToBoundType(m.ReturnType);
        return null;
    }

    BoundType? ResolveExternalMemberSymbolic(ExternalType ext, string memberName)
    {
        var open = FindOpenGenericByName(ext.Name, ext.TypeArgs.Count);
        if (open is null) return null;
        foreach (var t in MemberLookupTypes(open))
        {
            var memberRuntime = t.GetProperty(memberName)?.PropertyType
                             ?? t.GetField(memberName)?.FieldType;
            if (memberRuntime is not null)
                return MapRuntimeWithSubstitution(memberRuntime, ext.TypeArgs);
        }
        return null;
    }

    // Map a runtime Type that may reference the owning type's generic parameters into a
    // BoundType, substituting parameter position i → `args[i]`. Recurses through nested
    // generic instances (`ChannelReader<T>`, `IEnumerable<KeyValuePair<K,V>>`).
    internal static BoundType MapRuntimeWithSubstitution(Type t, IReadOnlyList<BoundType> args)
    {
        if (t.IsGenericParameter)
            return t.GenericParameterPosition < args.Count
                ? args[t.GenericParameterPosition]
                : MapRuntimeTypeToBoundType(t);
        // `T[]` / `KeyValuePair<K,V>[]` — substitute the element, then re-wrap as the
        // name-based array external the rest of the pipeline uses. Without this the open
        // element survives into an unresolvable `ExternalType("T[]")`; recursing keeps a
        // BCL element (`object[]`) on the same footing as the non-symbolic path.
        if (t.IsArray)
            return new ExternalType(MapRuntimeWithSubstitution(t.GetElementType()!, args).EmitName + "[]");
        if (t.IsGenericType && !t.IsGenericTypeDefinition && t.Name.Contains('`'))
            return new ExternalType(
                RuntimeGenericBaseName(t),
                t.GetGenericArguments().Select(a => MapRuntimeWithSubstitution(a, args)).ToList());
        return MapRuntimeTypeToBoundType(t);
    }

    BoundType? ResolveRuntimeMemberType(Type runtime, string memberName)
    {
        foreach (var t in MemberLookupTypes(runtime))
        {
            var prop = t.GetProperty(memberName);
            if (prop is not null) return MapRuntimeTypeToBoundType(prop.PropertyType);
            var field = t.GetField(memberName);
            if (field is not null) return MapRuntimeTypeToBoundType(field.FieldType);
        }
        return null;
    }

    static IEnumerable<Type> MemberLookupTypes(Type t)
        => t.IsInterface
            ? new[] { t }.Concat(t.GetInterfaces())
            : new[] { t };

    // Ref-choice subclasses are named `Choice_Case`. The binder doesn't register
    // them as standalone types, so a member access like `lit.value` (where lit is
    // typed Expr_literal) needs to be resolved by looking up the case payload in
    // the parent choice declaration.
    BoundType? ResolveRefChoiceCaseField(string caseTypeName, string memberName)
    {
        foreach (var choiceSym in _data.Symbols.AllOfKind(TypeSymbolKind.RefUnion))
        {
            if (choiceSym.Decl is not ChoiceDeclarationSyntax choiceDecl) continue;
            var choiceName = choiceSym.Name;
            var prefix = choiceName + "_";
            if (!caseTypeName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var caseName = caseTypeName[prefix.Length..];
            var caseDecl = choiceDecl.Cases.FirstOrDefault(c => c.Name == caseName);
            if (caseDecl is null) continue;
            var payload = caseDecl.Payloads.FirstOrDefault(p => p.Name == memberName);
            if (payload is null) return null;
            return ResolveType(payload.Type);
        }
        return null;
    }

    /// True when `name` is a choice-case type — `Choice_Case` for a choice declared in
    /// this compilation. These subclasses are synthesized at emit time, not registered
    /// as standalone types, so reflection can't see them; a composite literal
    /// (`Expr_add { … }`) is nonetheless valid and the emitter resolves it. Callers use
    /// this to avoid mistaking an in-compilation case type for an unknown external type.
    public bool IsChoiceCaseType(string name)
    {
        foreach (var choiceSym in _data.Symbols.AllOfKind(TypeSymbolKind.RefUnion)
                     .Concat(_data.Symbols.AllOfKind(TypeSymbolKind.Union)))
        {
            if (choiceSym.Decl is not ChoiceDeclarationSyntax choiceDecl) continue;
            var prefix = choiceSym.Name + "_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var caseName = name[prefix.Length..];
            if (choiceDecl.Cases.Any(c => c.Name == caseName)) return true;
        }
        return false;
    }

    /// The implicit standard-namespace search set for bare external names
    /// (tier 3 of FindTypeByName) — off when implicit usings are disabled.
}
