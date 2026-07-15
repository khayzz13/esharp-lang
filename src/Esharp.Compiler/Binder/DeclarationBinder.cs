using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.Binder;

/// Declaration binding: `data` / `class` (fields, events, conformance,
/// inheritance roles, init), `choice`, interfaces, delegates, `static func`,
/// namespace consts, functions (async shape + task-func rules), the embedded-
/// forwarder synthesis, and the `data` value-contract validation + CLR-form
/// promotion pass.
internal sealed class DeclarationBinder : BinderUnit
{
    internal DeclarationBinder(Binder binder) : base(binder) { }


    internal BoundDataDeclaration BindData(DataDeclarationSyntax syntax, List<BoundFunctionDeclaration> instanceMethods, IReadOnlyList<BoundFunctionDeclaration>? pointerMethods = null, TypeSymbol? selfSymbol = null)
    {
        var prevScope = Scope;
        Scope = Scope.Child();
        foreach (var tp in syntax.TypeParameters)
            Scope.Declare(tp, new PrimitiveType(tp));

        // Primary-ctor capture header (`class Foo(a: T, ...)`). Params bind up
        // front and enter the data scope BEFORE fields so a field default may read
        // a header param — it runs inside the primary ctor, where each header
        // param is a real ctor argument.
        List<BoundParameter>? headerParams = null;
        if (syntax.HeaderParameters is { Count: > 0 } headerSyntax)
        {
            headerParams = new List<BoundParameter>(headerSyntax.Count);
            foreach (var hp in headerSyntax)
            {
                var hpType = ResolveType(hp.Type);
                var (hpByRef, hpReadOnly) = RefFlags(hp.Type);
                headerParams.Add(new BoundParameter(hp.Name, hpType, hpByRef, hpReadOnly, DefaultValue: BindParameterDefault(hp, hpType)));
                Scope.Declare(hp.Name, hpType);
                if (syntax.Fields.Any(f => f.Name == hp.Name))
                    Diagnostics.Report(hp.NameSpan.IsValid ? hp.NameSpan : syntax.Span,
                        $"ES2188: '{syntax.Name}': header parameter '{hp.Name}' conflicts with a field of the same name. Rename one — a captured header param synthesizes its own private field.");
            }
        }

        var fields = syntax.Fields.Select(f =>
        {
            var directMutWritable = f.Property?.MutStorageName is { } mutStorage
                && syntax.Fields.FirstOrDefault(candidate => candidate.Name == mutStorage)?.Mutable == true;
            var fieldType = ResolveType(f.Type);
            if (f.Type is PointerTypeSyntax && !Types.StarClassError(fieldType, f.Type.Span))
            {
                // A field is shared, heap-resident storage — a `*T` field always
                // escapes the frame, so it is always the `__Ptr_T` wrapper
                // (nullable, aliasing). True for value `data` *and* primitives
                // (`*int` fields were silently degraded to bare `int` before).
                fieldType = new HeapPointerBoundType(fieldType);
            }
            // A stored (non-computed) property on a value `data` is illegal unless it is
            // settable by composite literal — `let x {}` (get-only) has no ctor to set it,
            // so only `let x => …` (computed), `required let x {}` (get+init), and
            // `var x {}` (get+set) are allowed on `data`. ES2193.
            if (f.Property is { ComputedGetter: null } prop && !syntax.IsRef
                && !prop.HasSet && !prop.HasInit)
                Diagnostics.Report(f.Span.IsValid ? f.Span : syntax.Span,
                    $"ES2193: get-only property '{f.Name}' has no storage on value `data {syntax.Name}` — `data` has no constructor to set it. Use a computed property `let {f.Name}: {TypeDisplayName(fieldType)} => …`, or `required let {f.Name} {{ }}` (set by composite literal), or declare '{syntax.Name}' as `class`.");
            return new BoundField(
                f.Name, fieldType, f.IsPublic ?? syntax.IsPublic, f.Mutable,
                f.DefaultValue is not null ? Expressions.BindExpression(f.DefaultValue) : null,
                f.IsEmbedded, f.IsEvent, f.IsRequired,
                IsProperty: f.Property is not null,
                IsComputedProperty: f.Property?.ComputedGetter is not null,
                PropHasSet: (f.Property?.HasSet ?? false) || directMutWritable,
                PropHasInit: f.Property?.HasInit ?? false,
                // `priv` → a real CLR private; `pub` → public; bare → the declaration's default.
                Vis: f.IsPublic switch
                {
                    true => Syntax.Visibility.Public,
                    false => Syntax.Visibility.Private,
                    null => syntax.IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal,
                },
                PropHasExplicitLoca: f.Property?.LocaStorageName is not null || f.Property?.MutStorageName is not null,
                PropLocaStorageName: f.Property?.MutStorageName ?? f.Property?.LocaStorageName,
                PropHasMut: f.Property?.MutStorageName is not null || f.Property?.ScopedMutBody is not null,
                PropMutWritable: directMutWritable,
                PropHasCustomSetter: f.Property?.SetterBody is not null);
        }).ToList();

        // A scoped `mut` is bound in the property's real instance context.  It is
        // not lowered to a getter here: the yielded location and the resume body
        // stay separate so later lowering can place the actual borrow in a
        // try/finally region at its use site.
        var scopedMutSelf = ResolveType(new NamedTypeSyntax(syntax.Name));
        for (var i = 0; i < syntax.Fields.Count; i++)
        {
            var mutSyntax = syntax.Fields[i].Property?.ScopedMutBody;
            if (mutSyntax is null) continue;

            var previousScope = Scope;
            Scope = Scope.Child();
            Scope.Declare("self", scopedMutSelf);
            foreach (var knownField in fields)
                Scope.Declare(knownField.Name, knownField.Type, knownField.Mutable);
            var scopedMut = BindScopedMutAccessor(mutSyntax, fields[i].Type);
            Scope = previousScope;

            if (scopedMut is not null)
            {
                fields[i] = fields[i] with
                {
                    ScopedMut = scopedMut,
                    PropHasSet = fields[i].PropHasSet || scopedMut.IsWritable,
                    PropMutWritable = scopedMut.IsWritable,
                };
                if ((selfSymbol ?? Data.Symbols.TryGet(syntax.Name, syntax.TypeParameters.Count, Ctx.CurrentNamespace))?
                    .Fields.FirstOrDefault(f => f.Name == fields[i].Name) is { } fieldSymbol)
                    fieldSymbol.ScopedMut = scopedMut;
            }
        }
        // Refresh the symbol spine's field views with the fully-bound types (the
        // same FieldSymbol instances the sink saw at declaration — defaults stay
        // on the BoundDataDeclaration, the symbol carries the type facts).
        if ((selfSymbol ?? Data.Symbols.TryGet(syntax.Name, syntax.TypeParameters.Count, Ctx.CurrentNamespace)) is { } fieldSym)
            foreach (var bf in fields)
                if (fieldSym.Fields.FirstOrDefault(fs => fs.Name == bf.Name) is { } fs)
                {
                    fs.Bound = bf.Type;
                    fs.Mutable = bf.IsProperty ? bf.PropHasSet : bf.Mutable;
                    fs.IsProperty = bf.IsProperty;
                    fs.HasDurablePropertyLocation = bf.IsProperty && !bf.IsComputedProperty
                        && (bf.PropHasExplicitLoca
                            || bf.PropHasMut && bf.ScopedMut is null
                            || !bf.PropHasMut && !bf.PropHasCustomSetter);
                    fs.HasScopedPropertyLocation = bf.ScopedMut is not null;
                    fs.HasCustomPropertySetter = bf.PropHasCustomSetter;
                }

        // Events imply identity (like `init`) and are typed by a delegate. They are
        // legal only on `class`, and only over a delegate type.
        foreach (var ev in fields.Where(f => f.IsEvent))
        {
            var evFieldSpan = syntax.Fields.FirstOrDefault(x => x.Name == ev.Name)?.Span ?? default;
            var evSpan = evFieldSpan.IsValid ? evFieldSpan : syntax.Span;
            if (!syntax.IsRef)
                Diagnostics.Report(evSpan,
                    $"ES2140: event '{ev.Name}' is only allowed on a 'class' (events imply identity). Declare '{syntax.Name}' as 'class'.");
            if (Expressions.TryGetExpectedDelegateShape(ev.Type) is null)
                Diagnostics.Report(evSpan,
                    $"ES2141: event '{ev.Name}' must be typed by a delegate (Action, EventHandler<T>, a 'delegate func', …). '{TypeDisplayName(ev.Type)}' is not a delegate.");
        }

        // The base class (must be first, must resolve to a class with modifier
        // 'open' or 'abstract') and the interfaces; derive traits are a separate list.
        var interfaces = new List<string>();
        var deriveTraits = syntax.DeriveTraits is { } dt ? new List<string>(dt) : new List<string>();
        string? baseClass = null;
        for (var i = 0; i < syntax.Interfaces.Count; i++)
        {
            var iface = InterfaceName(syntax.Interfaces[i]);
            // First entry: may be a base class (class with open/abstract).
            if (baseClass is null && i == 0
                && Data.Symbols.DataDecl(iface) is { } maybeBase
                && maybeBase.IsRef
                && maybeBase.Modifier is ClassModifier.Open or ClassModifier.Abstract)
            {
                baseClass = iface;
                continue;
            }
            // First entry resolving to an EXTERNAL class (BCL / referenced assembly) is the
            // base class — `class TagAttribute : Attribute`. Reflect the runtime type: a
            // class that is not an interface or value type. A sealed external base can't be
            // extended (the CLR forbids it), reported here rather than as bad metadata.
            if (baseClass is null && i == 0
                && Data.Symbols.DataDecl(iface) is null
                && syntax.IsRef
                && ResolveExternalRuntimeTypeByName(iface) is { IsClass: true, IsInterface: false, IsValueType: false } extBase)
            {
                if (extBase.IsSealed)
                    Diagnostics.Report(syntax.Interfaces[i].Span,
                        $"ES2192: '{syntax.Name}' cannot inherit from sealed type '{iface}'.");
                else
                    baseClass = iface;
                continue;
            }
            // Non-first class-named entries are an error (base must come first).
            if (Data.Symbols.DataDecl(iface) is { } asData && asData.IsRef
                && asData.Modifier is ClassModifier.Open or ClassModifier.Abstract)
            {
                Diagnostics.Report(syntax.Interfaces[i].Span,
                    $"'{syntax.Name}': base class '{iface}' must be the first entry after ':'. At most one base class is allowed.");
                continue;
            }
            interfaces.Add(iface);
        }

        // If derive equality, add IEquatable<T>. For a generic type the argument
        // is the self-instantiation `T<A, B>` (closed over its own type params) —
        // `IEquatable<T>` with the open definition is invalid metadata.
        if (deriveTraits.Contains("equality"))
        {
            var selfName = syntax.TypeParameters.Count > 0
                ? $"{syntax.Name}<{string.Join(", ", syntax.TypeParameters)}>"
                : syntax.Name;
            interfaces.Add($"IEquatable<{selfName}>");
        }

        // Interface conformance is NOMINAL: a type implements an interface only
        // when it names it after ':'. There is no implicit/structural auto-add.
        // For each declared interface we route the CLR InterfaceImplementation to
        // the value type (struct) when its own method set satisfies the contract,
        // or to the `*T` wrapper class when only the pointer method set does (the
        // Go pointer-receiver case). Declared-but-unsatisfiable is a hard error.
        var pointerProtocols = new List<string>();
        var hasPointerEmbed = fields.Any(f => f.IsEmbedded && f.Type is HeapPointerBoundType);
        // Wrapper content: pointer-receiver methods OR a pointer-embedded field
        // (whose promoted methods join the combined set). Either makes a `__Ptr_T`.
        var hasWrapperContent = (pointerMethods is { Count: > 0 }) || hasPointerEmbed;
        // *T's method set = pointer-receiver methods + value-receiver methods (promoted from T).
        var starMethodSet = new List<BoundFunctionDeclaration>(pointerMethods ?? []);
        starMethodSet.AddRange(instanceMethods);

        var valueInterfaces = new List<string>();
        foreach (var iface in interfaces)
        {
            // External / BCL interfaces and derive-injected ones (e.g. IEquatable<T>)
            // aren't in InterfaceDecls — we can't structurally verify them, so trust
            // the declaration and place them on the struct (derive/interop emits the
            // members).
            if (Data.Symbols.InterfaceDecl(iface) is not { } declaredProto)
            {
                valueInterfaces.Add(iface);
                continue;
            }
            var valueSat = SatisfiesInterface(declaredProto, instanceMethods, fields);
            var starSat = hasWrapperContent && SatisfiesInterface(declaredProto, starMethodSet, fields);
            if (valueSat)
            {
                valueInterfaces.Add(iface);
                // The wrapper IS the runtime instance passed as the interface, so it
                // must implement the contract too when a wrapper exists.
                if (hasWrapperContent) pointerProtocols.Add(iface);
            }
            else if (starSat)
            {
                // Only the *T method set satisfies it (pointer receiver): the wrapper
                // implements it; the value type genuinely does not.
                pointerProtocols.Add(iface);
            }
            else
            {
                Diagnostics.Report(syntax.Span,
                    $"Type '{syntax.Name}' declares conformance to protocol '{iface}' but does not implement all required methods (checked both its value method set and its '*{syntax.Name}' method set).");
                valueInterfaces.Add(iface); // keep a target for downstream emission
            }
        }
        interfaces = valueInterfaces;

        // Migration aid (ES2153): a type that structurally matches an interface it
        // does not declare no longer implements it implicitly. Warn and point at the
        // explicit ': IFoo' to add. Structural coincidence is not conformance.
        foreach (var protoSym in Data.Symbols.AllOfKind(Esharp.Symbols.TypeSymbolKind.Interface))
        {
            if (protoSym.Decl is not InterfaceDeclarationSyntax protoDecl) continue;
            var protoName = protoSym.Name;
            if (protoDecl.Methods.Count == 0) continue; // marker interfaces match everything — no signal
            if (interfaces.Contains(protoName) || pointerProtocols.Contains(protoName)) continue;
            var valueSat = SatisfiesInterface(protoDecl, instanceMethods, fields);
            var starSat = hasWrapperContent && SatisfiesInterface(protoDecl, starMethodSet, fields);
            if (valueSat || starSat)
                Diagnostics.Warn(syntax.Span,
                    $"ES2153: '{syntax.Name}' structurally matches interface '{protoName}' but does not declare it. Add ': {protoName}' to conform (E# interface conformance is nominal).");
        }

        // Bind init constructors. A class may declare several, overloaded by
        // signature; `: this(args)` delegates to a sibling, `: base(args)` to a
        // base init. Overload identity is ARITY + PARAMETER NAMES (no conversion
        // ranking): two inits with the same arity are a hard error (ES2185).
        var boundInits = new List<BoundInitDeclaration>();
        BoundBlockStatement? primaryEpilogue = null;
        if (syntax.Inits is { Count: > 0 } initsSyntax)
        {
            foreach (var initSyntax in initsSyntax)
            {
                if (headerParams is not null)
                {
                    // Headered class: a zero-param `init { }` with no `: this`/`: base`
                    // is the PRIMARY's epilogue, not a constructor of its own. Any
                    // other init is secondary and must delegate into the primary
                    // chain — it has no header args of its own to capture from.
                    if (initSyntax.Parameters.Count == 0 && initSyntax.ThisArguments is null && initSyntax.BaseArguments is null)
                    {
                        if (primaryEpilogue is not null)
                            Diagnostics.Report(syntax.Span,
                                $"ES2185: '{syntax.Name}' declares two parameter-less 'init' blocks — a headered class has exactly one primary epilogue.");
                        var epilogueScope = Scope.Child();
                        foreach (var f in fields)
                            epilogueScope.Declare(f.Name, f.Type);
                        var prevEpilogueScope = Scope;
                        Scope = epilogueScope;
                        primaryEpilogue = Statements.BindBlock(initSyntax.Body);
                        Scope = prevEpilogueScope;
                        continue;
                    }
                    if (initSyntax.ThisArguments is null)
                    {
                        Diagnostics.Report(syntax.Span,
                            $"ES2186: '{syntax.Name}': a secondary 'init' on a headered class must delegate with ': this(...)' — only the primary constructor (the header) builds the object.");
                        continue;
                    }
                }

                var initScope = Scope.Child();
                foreach (var f in fields)
                    initScope.Declare(f.Name, f.Type);
                var initParams = new List<BoundParameter>();
                foreach (var p in initSyntax.Parameters)
                {
                    var pt = ResolveType(p.Type);
                    var (pByRef, pReadOnly) = RefFlags(p.Type);
                    initParams.Add(new BoundParameter(p.Name, pt, pByRef, pReadOnly, DefaultValue: BindParameterDefault(p, pt)));
                    initScope.Declare(p.Name, pt);
                }
                var prevScopeInit = Scope;
                Scope = initScope;
                var initBody = Statements.BindBlock(initSyntax.Body);

                // Bind `: base(args)` arguments against the base class's init list.
                IReadOnlyList<BoundExpression>? boundBaseArgs = null;
                if (initSyntax.BaseArguments is not null)
                {
                    if (baseClass is null)
                        Diagnostics.Report(syntax.Span,
                            $"'{syntax.Name}': 'init(...) : base(...)' requires a base class. Add a base type to the inheritance list (e.g. ': SomeOpenClass').");
                    boundBaseArgs = initSyntax.BaseArguments.Select(a => Expressions.BindExpression(a)).ToList();
                    if (baseClass is not null
                        && Data.Symbols.DataDecl(baseClass) is { } baseDecl
                        && baseDecl.Inits is { Count: > 0 } baseInits
                        && !baseInits.Any(bi => bi.Parameters.Count == boundBaseArgs.Count))
                    {
                        var expected = string.Join(" / ", baseInits.Select(bi => bi.Parameters.Count.ToString()).Distinct());
                        Diagnostics.Report(syntax.Span,
                            $"ES2128: '{syntax.Name}': ': base(...)' argument count {boundBaseArgs.Count} does not match any '{baseClass}.init' signature (expects {expected}).");
                    }
                }

                // Bind `: this(args)` — sibling delegation, resolved by arity below.
                IReadOnlyList<BoundExpression>? boundThisArgs = null;
                if (initSyntax.ThisArguments is not null)
                    boundThisArgs = initSyntax.ThisArguments.Select(a => Expressions.BindExpression(a)).ToList();

                Scope = prevScopeInit;
                boundInits.Add(new BoundInitDeclaration(initParams, initBody, boundBaseArgs, boundThisArgs, -1, initSyntax.Visibility));
            }

        }

        // Headered class: the primary ctor IS the header. Index 0, so `: this`
        // delegation and construction-site arity resolution see it like any init.
        if (headerParams is not null)
            boundInits.Insert(0, new BoundInitDeclaration(
                headerParams, primaryEpilogue ?? new BoundBlockStatement([]), null, null, -1,
                Esharp.Syntax.InitVisibility.Default, IsPrimary: true));

        if (boundInits.Count > 0)
        {
            // ES2185 — overloads are distinguished by arity (+ argument names at the
            // call); two inits with the same arity cannot be told apart.
            for (var i = 0; i < boundInits.Count; i++)
                for (var j = i + 1; j < boundInits.Count; j++)
                    if (boundInits[i].Parameters.Count == boundInits[j].Parameters.Count)
                        Diagnostics.Report(syntax.Span,
                            $"ES2185: '{syntax.Name}' declares two 'init' constructors with {boundInits[i].Parameters.Count} parameter(s) — overloads are resolved by arity and argument names, never by argument types. Differ the arity or merge with defaults.");

            // Resolve `: this(args)` delegation targets by arity, then reject cycles.
            for (var i = 0; i < boundInits.Count; i++)
            {
                if (boundInits[i].ThisArguments is not { } ta) continue;
                var targetIdx = -1;
                for (var j = 0; j < boundInits.Count; j++)
                {
                    // Self is a legal arity match — the cycle pass below turns it
                    // into ES2187 rather than a misleading "no sibling" ES2128.
                    if (boundInits[j].Parameters.Count != ta.Count) continue;
                    targetIdx = j;
                    break;
                }
                if (targetIdx < 0)
                    Diagnostics.Report(syntax.Span,
                        $"ES2128: '{syntax.Name}': ': this(...)' argument count {ta.Count} does not match any sibling 'init' signature.");
                else
                    boundInits[i] = boundInits[i] with { DelegatesTo = targetIdx };
            }
            for (var i = 0; i < boundInits.Count; i++)
            {
                var seen = new HashSet<int>();
                var cur = i;
                while (cur >= 0 && boundInits[cur].DelegatesTo >= 0)
                {
                    if (!seen.Add(cur))
                    {
                        Diagnostics.Report(syntax.Span,
                            $"ES2187: '{syntax.Name}': ': this(...)' delegation forms a cycle between 'init' constructors.");
                        break;
                    }
                    cur = boundInits[cur].DelegatesTo;
                }
            }
        }

        // Materialize capture fields: every header param some in-body method
        // referenced (recorded during Pass 2) becomes a synthesized private `let`
        // field of the same name; the primary ctor stores the argument into it.
        // Params used only in `init { }` / field defaults stay ctor-locals.
        IReadOnlyList<string>? capturedHeaderParams = null;
        if (headerParams is not null
            && Ctx.HeaderCaptured.TryGetValue(syntax.Name, out var capturedSet)
            && capturedSet.Count > 0)
        {
            var captured = headerParams.Where(p => capturedSet.Contains(p.Name)).ToList();
            capturedHeaderParams = captured.Select(p => p.Name).ToList();
            foreach (var p in captured)
                fields.Add(new BoundField(p.Name, p.Type, IsPublic: false, Mutable: false));
        }

        Scope = prevScope;

        var classification = ClassifyData(syntax.Name);
        // The registry is keyed by (name, arity): a generic `data Result`2` and an
        // arity-0 `static Result` coexist freely. ES2152 fires only on a genuine
        // SAME-(name, arity) clash (e.g. `data Foo` + `enum Foo`, both arity 0) — a
        // real redeclaration, where the cast below would otherwise throw.
        var declSym = selfSymbol ?? Data.Symbols.TryGet(syntax.Name, syntax.TypeParameters.Count, Ctx.CurrentNamespace);
        if (declSym is null || declSym.BoundView is not DataType)
        {
            Diagnostics.Report(syntax.Span,
                $"ES2152: '{syntax.Name}' is already declared as a non-`data` type of the same arity in this namespace — a `data` cannot share its name and arity. Rename one of them.");
        }
        else
        {
            declSym.Classification = classification;
            declSym.BoundView = ((DataType)declSym.BoundView) with { Classification = classification };
        }

        // Resolve `: func` inheritance roles by walking the base-class chain.
        // The parser tagged each `: func` method as InheritanceRole.Fulfill as a
        // placeholder; refine it here based on:
        //   parent form (virtual/abstract) × body presence × child class kind.
        var refinedMethods = ResolveInheritanceRoles(syntax, instanceMethods, baseClass);
        // ES2120 — plain `func` shadowing a virtual/abstract parent member.
        FlagShadowedNonOverrides(syntax, refinedMethods, baseClass);

        var attributes = syntax.Attributes.Select(a => a.Arguments is not null ? $"{a.Name}({a.Arguments})" : a.Name).ToList();
        // Conformance resolved to structured, symbol-linked types — the bound node
        // carries identity, not names. The name-keyed satisfaction routing above is
        // internal to binding; the emitter and transpiler consume only these.
        var interfaceTypes = interfaces.Select(ResolveTypeName).ToList();
        var pointerInterfaceTypes = pointerProtocols.Select(ResolveTypeName).ToList();
        // Pointer-receiver methods do NOT emit as instance methods on the type — they emit as
        // static hosts `m(*T, …)` (see Binder Pass 3), the only representation under which a
        // body that walks the receiver as a first-class `*T` verifies. They were kept in a
        // separate set so interface satisfaction can apply the Go method-set discipline
        // (pointer methods join `*T`'s set, not `T`'s) and so the `__Ptr_T` wrapper forwarder
        // can delegate to the static. Only value/readonly-receiver and inline methods emit as
        // genuine instance methods here.
        var emittedMethods = refinedMethods;
        return new BoundDataDeclaration(
            syntax.IsPublic, syntax.IsReadonly, syntax.Name, syntax.TypeParameters,
            deriveTraits, fields, emittedMethods, classification, attributes,
            boundInits.Count > 0 ? boundInits : null, DeclaringNamespace: null, PointerInterfaceTypes: pointerInterfaceTypes,
            IsUserRef: syntax.IsRef,
            Modifier: syntax.Modifier, BaseClass: baseClass, InterfaceTypes: interfaceTypes,
            CapturedHeaderParams: capturedHeaderParams, IsPositionalData: syntax.IsPositional)
        { Span = syntax.Span };
    }

    /// Walk the inheritance chain to refine `: func` placeholder roles and to
    /// validate parent linkage. Returns a new list with refined methods.
    List<BoundFunctionDeclaration> ResolveInheritanceRoles(
        DataDeclarationSyntax syntax,
        List<BoundFunctionDeclaration> instanceMethods,
        string? baseClass)
    {
        bool isAbstractChild = syntax.Modifier == ClassModifier.Abstract;
        var refined = new List<BoundFunctionDeclaration>(instanceMethods.Count);
        foreach (var m in instanceMethods)
        {
            if (m.InheritanceRole != InheritanceRole.Fulfill)
            {
                // Standalone virtual/abstract declarations: validate placement.
                if (m.InheritanceRole == InheritanceRole.Virtual
                    && syntax.Modifier == ClassModifier.Sealed)
                {
                    Diagnostics.Report(m.Span.IsValid ? m.Span : syntax.Span,
                        $"ES2126: '{syntax.Name}.{m.Name}': 'virtual func' is not allowed on a sealed class. Declare '{syntax.Name}' as 'open' or 'abstract'.");
                }
                refined.Add(m);
                continue;
            }
            // `: func` placeholder — locate a matching parent virtual/abstract.
            // Both bound child method and parent syntax include the implicit `self`
            // parameter, so compare counts directly.
            var (parent, parentClass) = FindMatchingParent(baseClass, m.Name, m.Parameters.Count);
            if (parent is null)
            {
                Diagnostics.Report(m.Span.IsValid ? m.Span : syntax.Span,
                    $"ES2122: '{syntax.Name}.{m.Name}': ':' marker has no matching virtual or abstract member on the inheritance chain.");
                refined.Add(m with { InheritanceRole = InheritanceRole.None });
                continue;
            }
            if (parent.InheritanceRole is not (InheritanceRole.Virtual or InheritanceRole.Abstract))
            {
                Diagnostics.Report(m.Span.IsValid ? m.Span : syntax.Span,
                    $"ES2123: '{syntax.Name}.{m.Name}': ':' marker references '{parentClass}.{m.Name}' which is neither 'virtual' nor 'abstract'.");
                refined.Add(m with { InheritanceRole = InheritanceRole.None });
                continue;
            }

            bool hasBody = m.Body.Statements.Count > 0;
            InheritanceRole resolved;
            if (parent.InheritanceRole == InheritanceRole.Abstract && hasBody && !isAbstractChild)
                resolved = InheritanceRole.Fulfill;
            else if (parent.InheritanceRole == InheritanceRole.Virtual && hasBody && !isAbstractChild)
                resolved = InheritanceRole.Override;
            else if (parent.InheritanceRole == InheritanceRole.Abstract && !hasBody && isAbstractChild)
                resolved = InheritanceRole.PassThrough;
            else if (parent.InheritanceRole == InheritanceRole.Virtual && !hasBody && isAbstractChild)
                resolved = InheritanceRole.ReAbstract;
            else
            {
                Diagnostics.Report(m.Span.IsValid ? m.Span : syntax.Span,
                    $"ES2121: '{syntax.Name}.{m.Name}': ':' marker without a body in a non-abstract subclass. Either supply a body, mark '{syntax.Name}' as abstract, or remove the ':'.");
                refined.Add(m with { InheritanceRole = InheritanceRole.None });
                continue;
            }
            refined.Add(m with { InheritanceRole = resolved });
        }
        return refined;
    }

    (BoundFunctionDeclaration? method, string? owner) FindMatchingParent(string? baseClass, string name, int argCount)
    {
        var cursor = baseClass;
        while (cursor is not null)
        {
            if (Data.Symbols.DataDecl(cursor) is { } bd && bd.Methods is not null)
            {
                foreach (var pm in bd.Methods)
                {
                    if (pm.Name != name) continue;
                    if (pm.Parameters.Count != argCount) continue;
                    var role = pm.Modifier switch
                    {
                        Esharp.Syntax.FunctionModifier.Virtual => InheritanceRole.Virtual,
                        Esharp.Syntax.FunctionModifier.Abstract => InheritanceRole.Abstract,
                        // `: func` on the parent is itself an override of a higher-up
                        // virtual/abstract — walk the chain to surface that role. Without
                        // this, three-level virtual chains (A virtual → B `:` → C `:`)
                        // fire ES2123 on C because B's local modifier is InheritColon
                        // and its real-but-derived role isn't reflected on the syntax.
                        Esharp.Syntax.FunctionModifier.InheritColon
                            => ResolveColonRole(cursor, name, argCount),
                        _ => InheritanceRole.None,
                    };
                    return (new BoundFunctionDeclaration(
                        pm.IsPublic, pm.Name, pm.TypeParameters,
                        Array.Empty<BoundParameter>(), new VoidType(),
                        new BoundBlockStatement(Array.Empty<BoundStatement>()),
                        Array.Empty<string>(),
                        false, role), cursor);
                }
            }
            cursor = Data.Symbols.DataDecl(cursor) is { } c ? FindBaseClassName(c) : null;
        }
        return (null, null);
    }

    InheritanceRole ResolveColonRole(string declaringClass, string name, int argCount)
    {
        if (Data.Symbols.DataDecl(declaringClass) is not { } decl) return InheritanceRole.None;
        var (ancestor, _) = FindMatchingParent(FindBaseClassName(decl), name, argCount);
        if (ancestor is null) return InheritanceRole.None;
        // If the ancestor is Virtual/Abstract, this `: func` is an Override —
        // still virtually dispatchable, so a child can override it again.
        return ancestor.InheritanceRole is InheritanceRole.Virtual or InheritanceRole.Abstract
            ? InheritanceRole.Virtual
            : InheritanceRole.None;
    }

    string? FindBaseClassName(DataDeclarationSyntax decl)
    {
        // First entry in Interfaces that resolves to a class with open/abstract.
        foreach (var ifaceSyntax in decl.Interfaces)
        {
            var iface = InterfaceName(ifaceSyntax);
            if (Data.Symbols.DataDecl(iface) is { } bd && bd.IsRef
                && bd.Modifier is ClassModifier.Open or ClassModifier.Abstract)
                return iface;
        }
        return null;
    }

    void FlagShadowedNonOverrides(
        DataDeclarationSyntax syntax,
        List<BoundFunctionDeclaration> methods,
        string? baseClass)
    {
        if (baseClass is null) return;
        foreach (var m in methods)
        {
            if (m.InheritanceRole != InheritanceRole.None) continue;
            var (parent, parentClass) = FindMatchingParent(baseClass, m.Name, m.Parameters.Count);
            if (parent is null) continue;
            if (parent.InheritanceRole is InheritanceRole.Virtual or InheritanceRole.Abstract)
            {
                Diagnostics.Report(m.Span.IsValid ? m.Span : syntax.Span,
                    $"ES2120: '{syntax.Name}.{m.Name}' has the same name as a virtual member on base type '{parentClass}'. Prefix with ':' to override, or rename.");
            }
        }
    }




    // === Contract validation + compiler-chosen representation for `data` ===
    //
    // `data` gives the user a value-semantic contract: copy-on-assign, no object
    // identity, no sharing through aliases, value-shaped equality. The CLR form
    // (struct vs class) is the compiler's choice; under the contract, that choice
    // is not user-observable from E# source. The generated interop facade gives
    // C# consumers a stable surface regardless of the chosen form.
    //
    // Recursion is the one case the user must be explicit about: a pure value
    // type cannot contain itself, so `data Node { next: Node }` (or any generic
    // wrapping of the same type, e.g. `List<Node>`) is an error. The fix is `*T`
    // — the heap-pointer handle that lets a value-shaped type take part in
    // recursive / shared flows.

    internal void ValidateDataContract(List<BoundMember> members)
    {
        var allData = members.OfType<BoundDataDeclaration>().ToList();
        if (allData.Count == 0) return;

        // --- Pass A: recursive-field error ---
        // Only checks value-semantic `data`; `class` is heap-native and may
        // legitimately hold itself by reference.
        var recursivelyBroken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var data in allData)
        {
            if (data.Classification == DataClassification.Class) continue;
            foreach (var field in data.Fields)
            {
                if (ContainsRecursiveReference(field.Type, data.Name))
                {
                    Diagnostics.Report(data.Span,
                        $"ES2002: '{data.Name}' has recursive field '{field.Name}' of type '{TypeDisplayName(field.Type)}' — " +
                        $"value-semantic `data` cannot contain itself. Use `*{data.Name}` to break the cycle " +
                        $"(e.g. `{field.Name}: *{data.Name}` or `{field.Name}: List<*{data.Name}>`).");
                    recursivelyBroken.Add(data.Name);
                }
            }
        }

        // --- Pass A.5: validate [Struct]/[Class] usage ---
        // Both attributes are explicit overrides of the compiler's struct/class
        // choice on `data`. They are not allowed on a `class` (already a CLR class
        // by definition) and are mutually exclusive on `data`.
        foreach (var data in allData)
        {
            var hasStructPin = data.Attributes.Contains("Struct");
            var hasClassPin = data.Attributes.Contains("Class");

            if (hasStructPin && hasClassPin)
                Diagnostics.Report(data.Span,
                    $"'{data.Name}': '[Struct]' and '[Class]' are mutually exclusive — pick one to pin the CLR form.");

            if (data.IsUserRef && (hasStructPin || hasClassPin))
            {
                var attr = hasStructPin ? "Struct" : "Class";
                Diagnostics.Report(data.Span,
                    $"'[{attr}]' is not allowed on 'class {data.Name}' — '[Struct]'/'[Class]' pin the CLR form of a value `data`. A class is always a CLR class.");
            }
        }

        // --- Pass B: compiler picks CLR form ---
        // Silent promotion when the heuristics say a class representation is
        // better. No warning; the value-semantic contract is preserved via the
        // facade + value equality. `[Struct]` forces struct, `[Class]`
        // forces class. Types that hit a Pass A error are skipped — their
        // size/shape is ill-defined.
        var structDecls = allData
            .Where(d => d.Classification == DataClassification.Struct && !recursivelyBroken.Contains(d.Name))
            .ToList();
        if (structDecls.Count == 0) return;

        var collectionElementTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var data in allData)
        {
            foreach (var field in data.Fields)
            {
                var elemType = TypeResolver.ExtractCollectionElementType(field.Type);
                if (elemType is not null) collectionElementTypes.Add(elemType);
            }
        }

        var passedByValueCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var func in members.OfType<BoundFunctionDeclaration>())
        {
            foreach (var param in func.Parameters)
            {
                if (param.Type is DataType pdt && !param.ByRef)
                    passedByValueCount[pdt.Name] = passedByValueCount.GetValueOrDefault(pdt.Name) + 1;
            }
        }

        // Autopromotion is disabled — a value type is always a CLR struct unless an
        // explicit [Class] pin promotes it. The size/usage heuristic below is retained
        // (it may return as an analyzer hint) but is gated off so it never fires.
        var autopromote = false;

        foreach (var data in structDecls)
        {
            // Explicit overrides win over the heuristic.
            if (data.Attributes.Contains("Class"))
            {
                PromoteDataToClass(members, data.Name);
                continue;
            }
            if (data.Attributes.Contains("Struct")) continue;
            // readonly data stays a struct — the [IsReadOnly] / defensive-copy
            // elision benefit only applies to value types.
            if (data.IsReadonly) continue;

            var estimatedSize = 0;
            var refFieldCount = 0;
            foreach (var field in data.Fields)
            {
                var (isValue, size) = Types.FieldMetrics(field.Type);
                if (isValue) estimatedSize += size;
                else { estimatedSize += 8; refFieldCount++; }
            }

            var storedInCollection = collectionElementTypes.Contains(data.Name);
            var passCount = passedByValueCount.GetValueOrDefault(data.Name);

            var promote = autopromote &&
                (estimatedSize > 64
                || refFieldCount >= 3
                || (storedInCollection && estimatedSize > 32)
                || (passCount >= 3 && estimatedSize > 32));

            if (!promote) continue;

            PromoteDataToClass(members, data.Name);
        }
    }

    void PromoteDataToClass(List<BoundMember> members, string name)
    {
        if (Data.Symbols.FindType(name, Esharp.Symbols.TypeSymbolKind.Struct, Esharp.Symbols.TypeSymbolKind.Class) is { } promotedSym)
            promotedSym.Classification = DataClassification.Class;
        for (var i = 0; i < members.Count; i++)
        {
            if (members[i] is BoundDataDeclaration d && d.Name == name)
            {
                members[i] = d with { Classification = DataClassification.Class };
                break;
            }
        }
        if (Data.Symbols.FindType(name, Esharp.Symbols.TypeSymbolKind.Struct) is { BoundView: DataType dtReg } promoViewSym)
            promoViewSym.BoundView = dtReg with { Classification = DataClassification.Class };
    }

    bool ContainsRecursiveReference(BoundType t, string declName) => t switch
    {
        DataType dt when dt.Name == declName => true,
        DataType dt => dt.TypeArgs.Any(a => ContainsRecursiveReference(a, declName)),
        HeapPointerBoundType => false, // *T breaks the cycle
        ByRefBoundType br => ContainsRecursiveReference(br.Inner, declName),
        NullableType n => ContainsRecursiveReference(n.Inner, declName),
        ResultType r => ContainsRecursiveReference(r.OkType, declName) || ContainsRecursiveReference(r.ErrorType, declName),
        ChanType c => ContainsRecursiveReference(c.ElementType, declName),
        TupleType tu => tu.ElementTypes.Any(e => ContainsRecursiveReference(e, declName)),
        // A structured external generic (`List<Node>`) recurses on its bound args;
        // a `*T` arg breaks the cycle exactly as the direct-field case does.
        ExternalType ext => ext.TypeArgs.Any(a =>
            a is not HeapPointerBoundType && ContainsRecursiveReference(a, declName)),
        _ => false,
    };


    BoundScopedMutAccessor? BindScopedMutAccessor(BlockStatementSyntax syntax, BoundType propertyType)
    {
        foreach (var node in SyntaxNavigator.DescendantsAndSelf(syntax))
        {
            if (node is AwaitExpressionSyntax or AsyncLetStatementSyntax)
                Diagnostics.Report(node.Span,
                    "ES2219: scoped `mut` cannot cross `await` — its yielded location is valid only inside the synchronous lend/resume region.");
            else if (node is ReturnStatementSyntax)
                Diagnostics.Report(node.Span,
                    "ES2220: `return` is not valid inside scoped `mut`; use the resume portion after `yield` to commit, validate, or throw.");
            else if (node is FunctionLiteralExpressionSyntax or SpawnExpressionSyntax)
                Diagnostics.Report(node.Span,
                    "ES2221: scoped `mut` cannot capture or spawn from its lend region. Move the work outside the accessor or use a durable `loca`/direct `mut` location.");
        }

        var setup = new List<BoundStatement>();
        var resume = new List<BoundStatement>();
        BoundExpression? yielded = null;
        var afterYield = false;

        foreach (var statement in syntax.Statements)
        {
            if (statement is MutYieldStatementSyntax lend)
            {
                if (yielded is not null)
                {
                    Diagnostics.Report(lend.Span,
                        "ES2223: a scoped `mut` accessor may contain exactly one `yield &location` lend point.");
                    continue;
                }

                var location = Expressions.BindExpression(lend.Location);
                if (location is not BoundAddressOfVariableExpression address)
                {
                    Diagnostics.Report(lend.Span,
                        "ES2224: scoped `mut` must yield an addressable local as `yield &name`.");
                    continue;
                }
                if (!SignatureTypeMatches(address.PointeeType, propertyType))
                {
                    Diagnostics.Report(lend.Span,
                        $"ES2225: scoped `mut` yields '{TypeDisplayName(address.PointeeType)}' but property storage is '{TypeDisplayName(propertyType)}'.");
                    continue;
                }
                if (address.Target is not BoundNameExpression)
                {
                    Diagnostics.Report(lend.Span,
                        "ES2224: scoped `mut` must lend a local working location (`yield &working`), not a field or computed expression.");
                    continue;
                }

                yielded = address.Target;
                afterYield = true;
                continue;
            }

            var bound = Statements.BindStatement(statement);
            if (afterYield) resume.Add(bound);
            else setup.Add(bound);
        }

        if (yielded is null)
        {
            Diagnostics.Report(syntax.Span,
                "ES2223: a scoped `mut` accessor must contain exactly one `yield &location` lend point.");
            return null;
        }

        return new BoundScopedMutAccessor(
            new BoundBlockStatement(setup) { Span = syntax.Span },
            yielded,
            new BoundBlockStatement(resume) { Span = syntax.Span },
            IsWritable: yielded is BoundNameExpression name && Scope.LookupMutable(name.Name) == true);
    }

    internal bool SatisfiesInterface(InterfaceDeclarationSyntax protocol, List<BoundFunctionDeclaration> instanceMethods, IReadOnlyList<BoundField> fields)
    {
        foreach (var method in protocol.Methods)
        {
            // Nominal conformance: match by name, arity (excluding the receiver/self
            // param), parameter types, AND return type — full signature equality,
            // not the old name+arity-only check. Name+arity alone falsely conformed
            // a `describe() -> int` to an interface wanting `describe() -> string`.
            var match = instanceMethods.FirstOrDefault(m =>
                m.Name == method.Name
                && m.Parameters.Count - 1 == method.Parameters.Count
                && InterfaceSignatureMatches(m, method));
            if (match is null) return false;
        }
        // Property requirements: satisfied by a property OR a plain field of the same
        // name and type with at least the required accessor capability. The setter kind
        // must match EXACTLY — a `{ get set }` requirement (plain setter) is not filled by
        // a get+init member and vice-versa, because the two emit distinct CLR slots (the
        // init setter carries modreq(IsExternalInit)). A get-only requirement is satisfied
        // by any form (every property/field exposes a getter), so a `var x { }` satisfies
        // a `let x { get }`.
        if (protocol.Properties is { } reqs)
            foreach (var req in reqs)
            {
                var f = fields.FirstOrDefault(x =>
                    !x.IsEmbedded && !x.IsEvent && x.Name == req.Name
                    && SignatureTypeMatches(ResolveType(req.Type), x.Type));
                if (f is null) return false;
                if (req.HasSet)
                {
                    // Freely-settable setter: a get+set property, or a mutable plain field.
                    var settable = f.IsProperty ? (f.PropHasSet && !f.IsComputedProperty) : f.Mutable;
                    if (!settable) return false;
                }
                if (req.HasInit)
                {
                    // Init-only setter: a get+init property, or a write-once (`let`) field
                    // whose synthesized accessor is emitted init-only.
                    var initable = f.IsProperty ? f.PropHasInit : !f.Mutable;
                    if (!initable) return false;
                }
                if (req.HasLoca)
                {
                    // A direct field can expose its location through synthesized
                    // interface accessors. A property must have a genuinely durable
                    // contract: implicit stored identity, explicit loca, or direct mut.
                    // A computed/custom-unacknowledged/scoped-only property cannot
                    // satisfy an interface that promises location escape.
                    var durable = !f.IsProperty
                        || !f.IsComputedProperty
                        && (f.PropHasExplicitLoca
                            || f.PropHasMut && f.ScopedMut is null
                            || !f.PropHasMut && !f.PropHasCustomSetter);
                    if (!durable) return false;
                }
            }
        return true;
    }

    // Exact signature comparison between a promoted instance method (whose first
    // parameter is the receiver/self) and an interface method. Types are compared
    // STRUCTURALLY: symbol reference identity decides nominal types when both sides
    // carry their interned symbol; name + type-argument shape otherwise (unresolved
    // externals, primitives, generic-parameter placeholders). Pointer forms
    // normalize (`ref T` ≡ `*T` — both lower to the same signature shape) and a
    // decl-site open generic compares its formal parameters against use-site args.
    bool InterfaceSignatureMatches(BoundFunctionDeclaration m, InterfaceMethodSyntax method)
    {
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var expected = ResolveType(method.Parameters[i].Type);
            var actual = m.Parameters[i + 1].Type;   // +1 skips the receiver/self
            if (!SignatureTypeMatches(expected, actual)) return false;
        }
        return SignatureTypeMatches(ResolveType(method.ReturnType), m.ReturnType);
    }

    internal static bool SignatureTypeMatches(BoundType a, BoundType b)
    {
        // Pointer spellings normalize: `ref T` and `*T` are one signature form.
        var aPtr = a switch { ByRefBoundType br => br.Inner, HeapPointerBoundType hp => hp.Inner, _ => null };
        var bPtr = b switch { ByRefBoundType br => br.Inner, HeapPointerBoundType hp => hp.Inner, _ => null };
        if (aPtr is not null || bPtr is not null)
            return aPtr is not null && bPtr is not null && SignatureTypeMatches(aPtr, bPtr);

        if (a is NullableType na || b is NullableType nb2)
            return a is NullableType na2 && b is NullableType nb
                && SignatureTypeMatches(na2.Inner, nb.Inner);

        // Nominal identity by interned symbol when both sides carry one — the
        // namespace-correct comparison strings could never make.
        var symA = SymbolLink(a);
        var symB = SymbolLink(b);
        if (symA is not null && symB is not null && !ReferenceEquals(symA, symB))
            return false;

        return (a, b) switch
        {
            (TupleType ta, TupleType tb) => ArgsMatch(ta.ElementTypes, tb.ElementTypes),
            (FunctionPointerType fa, FunctionPointerType fb) =>
                ArgsMatch(fa.ParameterTypes, fb.ParameterTypes) && SignatureTypeMatches(fa.ReturnType, fb.ReturnType),
            (VoidType, VoidType) => true,
            (NullType, NullType) => true,
            (InferredType, InferredType) => true,
            _ => SimpleName(a) is { } nameA && SimpleName(b) is { } nameB
                && nameA == nameB && ArgsMatch(EffectiveArgs(a), EffectiveArgs(b)),
        };

        static bool ArgsMatch(IReadOnlyList<BoundType> xs, IReadOnlyList<BoundType> ys)
        {
            if (xs.Count != ys.Count) return false;
            for (var i = 0; i < xs.Count; i++)
                if (!SignatureTypeMatches(xs[i], ys[i])) return false;
            return true;
        }

        static TypeSymbol? SymbolLink(BoundType t) => t switch
        {
            DataType d => d.Symbol,
            ChoiceType c => c.Symbol,
            EnumType e => e.Symbol,
            InterfaceType i => i.Symbol,
            NamedDelegateType nd => nd.Symbol,
            ExternalCSharpType x => x.Symbol,
            StaticFuncType s => s.Symbol,
            _ => null,
        };

        static string? SimpleName(BoundType t) => t switch
        {
            PrimitiveType p => p.Name,
            DataType d => d.Name,
            ChoiceType c => c.Name,
            EnumType e => e.Name,
            InterfaceType i => i.Name,
            ExternalType x => x.Name,
            ExternalCSharpType xc => xc.Handle.Name,
            NamedDelegateType nd => nd.Name,
            StaticFuncType s => s.Name,
            ResultType => "Result",
            ChanType => "Chan",
            _ => null,
        };

        // Structured type arguments; a decl-site open generic exposes its formal
        // parameters as placeholder primitives (mirrors how the binder scopes them).
        static IReadOnlyList<BoundType> EffectiveArgs(BoundType t) => t switch
        {
            DataType { TypeArgs.Count: > 0 } d => d.TypeArgs,
            DataType { TypeParameters.Count: > 0 } d => d.TypeParameters.Select(p => (BoundType)new PrimitiveType(p)).ToList(),
            ChoiceType { TypeArgs.Count: > 0 } c => c.TypeArgs,
            ChoiceType { TypeParameters.Count: > 0 } c => c.TypeParameters.Select(p => (BoundType)new PrimitiveType(p)).ToList(),
            InterfaceType i => i.TypeArgs,
            ExternalType x => x.TypeArgs,
            ExternalCSharpType xc => xc.TypeArgs,
            ResultType r => [r.OkType, r.ErrorType],
            ChanType ch => [ch.ElementType],
            _ => [],
        };
    }

    internal BoundInterfaceDeclaration BindProtocol(InterfaceDeclarationSyntax syntax)
    {
        // Generic interface: declare the type parameters in scope so the method
        // signatures resolve `T` to a generic-parameter placeholder (mirrors BindChoice).
        var prevScope = Scope;
        if (syntax.TypeParameters.Count > 0)
        {
            Scope = Scope.Child();
            foreach (var tp in syntax.TypeParameters)
                Scope.Declare(tp, new PrimitiveType(tp));
        }

        var methods = syntax.Methods.Select(m =>
        {
            var parameters = m.Parameters.Select(p =>
            {
                var (byRef, readOnly) = RefFlags(p.Type);
                return new BoundParameter(p.Name, ResolveType(p.Type), byRef, readOnly);
            }).ToList();
            return new BoundInterfaceMethod(m.Name, parameters, ResolveType(m.ReturnType));
        }).ToList();
        List<BoundField>? events = null;
        if (syntax.Events is { Count: > 0 })
        {
            events = new List<BoundField>();
            foreach (var e in syntax.Events)
            {
                var evType = ResolveType(e.Type);
                if (Expressions.TryGetExpectedDelegateShape(evType) is null)
                    Diagnostics.Report(e.Span.IsValid ? e.Span : syntax.Span,
                        $"ES2141: interface event '{e.Name}' must be typed by a delegate. '{TypeDisplayName(evType)}' is not a delegate.");
                events.Add(new BoundField(e.Name, evType, IsPublic: true, Mutable: false, IsEvent: true));
            }
        }

        List<BoundInterfaceProperty>? props = null;
        if (syntax.Properties is { Count: > 0 })
            props = syntax.Properties
                .Select(p => new BoundInterfaceProperty(
                    p.Name, ResolveType(p.Type), p.HasGet, p.HasSet, p.HasInit, p.HasLoca))
                .ToList();

        Scope = prevScope;
        return new BoundInterfaceDeclaration(syntax.IsPublic, syntax.Name, syntax.TypeParameters, methods, events, Properties: props);
    }

    internal BoundChoiceDeclaration BindChoice(ChoiceDeclarationSyntax syntax)
    {
        var prevScope = Scope;
        Scope = Scope.Child();
        foreach (var tp in syntax.TypeParameters)
            Scope.Declare(tp, new PrimitiveType(tp));

        var cases = syntax.Cases.Select(c =>
            new BoundChoiceCase(c.Name, c.Payloads.Select(p => new BoundField(p.Name, ResolveType(p.Type))).ToList())
        ).ToList();

        Scope = prevScope;
        return new BoundChoiceDeclaration(syntax.IsPublic, syntax.IsRef, syntax.Name, syntax.TypeParameters, cases);
    }

    internal BoundNamespaceInitDeclaration BindNamespaceInit(NamespaceInitDeclarationSyntax syntax)
    {
        var previousScope = Scope;
        var previousReturn = Ctx.CurrentReturnType;
        var previousAwait = Ctx.CurrentFunctionHasAwait;
        var previousYield = Ctx.CurrentFunctionAllowsYield;
        var previousInit = Ctx.InNamespaceInitializer;

        Scope = Scope.Child();
        Ctx.CurrentReturnType = new VoidType();
        Ctx.CurrentFunctionHasAwait = false;
        Ctx.CurrentFunctionAllowsYield = false;
        Ctx.InNamespaceInitializer = true;

        var body = Statements.BindBlock(syntax.Body);

        Scope = previousScope;
        Ctx.CurrentReturnType = previousReturn;
        Ctx.CurrentFunctionHasAwait = previousAwait;
        Ctx.CurrentFunctionAllowsYield = previousYield;
        Ctx.InNamespaceInitializer = previousInit;

        return new BoundNamespaceInitDeclaration(body) { Span = syntax.Span };
    }

    internal BoundNamespaceStateDeclaration BindNamespaceState(NamespaceStateDeclarationSyntax syntax)
    {
        BoundExpression? initializer = syntax.Initializer is null
            ? null
            : Expressions.BindExpression(syntax.Initializer);
        var type = syntax.Type is InferredTypeSyntax
            ? initializer?.Type ?? InferredType.Instance
            : ResolveType(syntax.Type);
        if (initializer is not null)
            initializer = Expressions.Coerce(initializer, type);

        BoundExpression? computedGetter = null;
        if (syntax.Property?.ComputedGetter is { } getter)
            computedGetter = Expressions.Coerce(Expressions.BindExpression(getter, type), type);

        BoundExpression? setterBody = null;
        if (syntax.Property?.SetterBody is { } setter)
        {
            var previous = Scope;
            Scope = Scope.Child();
            Scope.Declare(syntax.Property.SetterParam ?? "value", type, mutable: false);
            setterBody = Expressions.Coerce(Expressions.BindExpression(setter, type), type);
            Scope = previous;
        }

        var field = new BoundField(
            syntax.Name, type, syntax.IsPublic, syntax.Mutable, initializer,
            IsProperty: syntax.Property is not null,
            IsComputedProperty: syntax.Property?.ComputedGetter is not null,
            PropHasSet: syntax.Property?.HasSet ?? false,
            PropHasInit: syntax.Property?.HasInit ?? false,
            Vis: syntax.Visibility,
            PropHasExplicitLoca: syntax.Property?.LocaStorageName is not null || syntax.Property?.MutStorageName is not null,
            PropLocaStorageName: syntax.Property?.MutStorageName ?? syntax.Property?.LocaStorageName,
            PropHasMut: syntax.Property?.MutStorageName is not null || syntax.Property?.ScopedMutBody is not null,
            PropHasCustomSetter: syntax.Property?.SetterBody is not null);
        if (syntax.Property?.ScopedMutBody is not null)
            Diagnostics.Report(syntax.Property.ScopedMutBody.Span,
                "ES2214: scoped `mut` currently requires an instance property because its lend/resume protocol owns an instance receiver.");
        return new BoundNamespaceStateDeclaration(field, computedGetter,
            syntax.Property?.SetterParam, setterBody) { Span = syntax.Span };
    }

    internal BoundFunctionDeclaration BindFunction(FunctionDeclarationSyntax syntax, TypeSyntax? defaultReturnsOverride = null, DataDeclarationSyntax? captureOwner = null)
    {
        var prevScope = Scope;
        var prevReturn = Ctx.CurrentReturnType;
        var prevHasAwait = Ctx.CurrentFunctionHasAwait;
        var prevAllowsYield = Ctx.CurrentFunctionAllowsYield;
        var prevName = Ctx.CurrentFunctionName;
        var prevBlockSpan = Ctx.CurrentFunctionBlockSpan;
        Scope = Scope.Child();
        Ctx.CurrentFunctionHasAwait = false;
        Ctx.CurrentFunctionName = syntax.Name;
        // The enclosing scope range for locals/params declared in this body — the
        // span LookupSymbolsInScope tests a query position against.
        Ctx.CurrentFunctionBlockSpan = syntax.Body.Span;

        // Register type parameters in scope so T, U etc resolve
        foreach (var tp in syntax.TypeParameters)
            Scope.Declare(tp, new PrimitiveType(tp));

        // Inline type-body methods are parser-hoisted. Their own declaration may
        // have no type parameters, but an owner such as `class Spawned<T>` still
        // supplies T to every member signature and body. Recover that lexical
        // generic scope from the signature-time owner symbol before resolving any
        // return type or parameter; otherwise `Task<T>` degrades to Task<object>.
        if (syntax.IsTypeBodyMethod
            && Data.Symbols.FunctionForDecl(syntax)?.DeclaringType?.BoundView is DataType ownerGenericType)
        {
            foreach (var tp in ownerGenericType.TypeParameters)
                if (!syntax.TypeParameters.Contains(tp, StringComparer.Ordinal))
                    Scope.Declare(tp, new PrimitiveType(tp));
        }

        // If the function omitted an explicit return type and a default-returns
        // clause applies (from enclosing static or class), substitute.
        TypeSyntax effectiveReturn = syntax.ReturnType;
        if (!syntax.HasExplicitReturnType)
        {
            TypeSyntax? fallback = defaultReturnsOverride;
            if (fallback is null && syntax.Parameters.Count > 0)
            {
                if (Data.Symbols.DataDecl(TypeSyntaxLeafName(syntax.Parameters[0].Type)) is { } ownerData && ownerData.DefaultReturns is not null)
                    fallback = ownerData.DefaultReturns.Type;
            }
            if (fallback is not null) effectiveReturn = fallback;
        }
        var returnType = ResolveHeapPointerAware(effectiveReturn);
        var returnsAsyncStream = returnType is ExternalType { Name: "IAsyncEnumerable", TypeArgs.Count: 1 };
        var hasYield = FunctionBodyHasYield(syntax);
        Ctx.CurrentFunctionAllowsYield = returnsAsyncStream;
        // Uncolored async: when the body awaits, the declared return type selects the
        // state-machine shape. `-> Task<T>`/`-> Task` (or ValueTask form) unwraps to
        // its result type; an explicit `-> void` is async-void (event handlers). In
        // every case the body returns the unwrapped value, exactly like a bare `-> T`.
        var asyncShape = AsyncReturnShape.ValueTask;
        if (!syntax.IsTaskFunc && FunctionBodyHasAwait(syntax) && !returnsAsyncStream)
            (returnType, asyncShape) = ClassifyAsyncReturn(returnType, syntax.HasExplicitReturnType);
        Ctx.CurrentReturnType = returnType;

        var parameters = new List<BoundParameter>();

        // Go-style receiver: the parser prepended `func (c: *T) m()`'s receiver as the
        // leading parameter, so the foreach below binds it exactly like the rest and all
        // Parameters[0]-as-receiver machinery (call resolution, generic inference, the
        // `.Skip(1)` emit) is reused. Here we only classify the receiver kind (drives the
        // method-set sort + the emit of `this`) and validate the receiver type.
        var receiverKind = Esharp.Symbols.ReceiverKind.None;
        BoundType? staticReceiverType = null;
        if (syntax.Receiver is { } recv)
        {
            var recvType = ResolveHeapPointerAware(recv.Type);
            if (recv.IsStaticFacet || recvType is StaticFuncType)
            {
                var receiverName = TypeResolver.TypeSyntaxLeafName(recv.Type);
                var receiverArity = recv.Type is GenericTypeSyntax generic ? generic.Args.Count : 0;
                var facet = Data.Symbols.TryGet(receiverName, receiverArity, Ctx.CurrentNamespace);
                if (facet?.StaticFacet is null)
                {
                    Diagnostics.Report(recv.Type.Span,
                        $"ES2211: `static {receiverName}` cannot be used as a receiver because `{receiverName}` has no static facet. Declare `static {receiverName} {{ ... }}` or attach the method as an instance method without the `static` keyword.");
                }
                else
                {
                    staticReceiverType = new StaticFuncType(receiverName, facet.StaticFacet,
                        facet.StaticFacet.GenericParameters) { Symbol = facet };
                }
                if (recv.IsReadonly)
                    Diagnostics.Report(recv.Type.Span, "ES2212: a static receiver cannot be 'readonly' — a static facet has no value receiver.");
                if (recv.Type is PointerTypeSyntax || recvType is HeapPointerBoundType)
                    Diagnostics.Report(recv.Type.Span, "ES2212: a static receiver cannot be a pointer — a static facet has no addressable value.");
                receiverKind = Esharp.Symbols.ReceiverKind.Static;
            }
            if (recvType is HeapPointerBoundType { Inner: DataType { } innerData }
                && Data.Symbols.DataDecl(innerData.Name) is { IsRef: true })
                // `*class` is illegal — a class is already a reference.
                Diagnostics.Report(recv.Type.Span,
                    $"ES2131: '*{innerData.Name}' is not a valid receiver — '{innerData.Name}' is a class (already a reference). Use 'func ({recv.Name}: {innerData.Name}) {syntax.Name}(...)'.");
            // A *closed* generic receiver (`func (p: Pair<int, int>) m()`) on a non-generic
            // method can't attach — the CLR hosts the method on the open type `Pair<T0,T1>`,
            // which a concrete `Pair<int,int>` `this` doesn't match.
            if (recvType is DataType { TypeArgs.Count: > 0 } && syntax.TypeParameters.Count == 0)
                Diagnostics.Report(recv.Type.Span,
                    $"ES2132: receiver '{recv.Name}: {TypeDisplayName(recvType)}' is a closed generic — a method attaches to the open type. Make '{syntax.Name}' generic over the type's parameters (e.g. 'func ({recv.Name}: ...<A, B>) {syntax.Name}<A, B>(...)').");

            if (receiverKind != Esharp.Symbols.ReceiverKind.Static)
                receiverKind = recvType is HeapPointerBoundType ? Esharp.Symbols.ReceiverKind.Pointer
                    : recv.IsReadonly ? Esharp.Symbols.ReceiverKind.ReadonlyValue
                    : Esharp.Symbols.ReceiverKind.Value;
        }

        foreach (var p in syntax.Parameters)
        {
            var paramType = receiverKind == Esharp.Symbols.ReceiverKind.Static && parameters.Count == 0
                ? staticReceiverType ?? ResolveType(p.Type)
                : ResolveType(p.Type);
            // Type-body methods are hoisted with a synthetic leading `self`
            // parameter. For `class Ch<T>`, its syntax placeholder spells only
            // `Ch`; recover the symbol's canonical self-instantiation `Ch<T>`
            // before declaring the local or binding the body. Otherwise an async
            // state machine faithfully captures the wrong open receiver type.
            if (syntax.IsTypeBodyMethod && parameters.Count == 0 && p.Name == "self"
                && Data.Symbols.FunctionForDecl(syntax)?.DeclaringType?.BoundView is { } ownerType)
                paramType = ownerType;

            if (p.IsOut)
            {
                // `out T` → CLR `[Out] T&`: a true managed-pointer out-parameter
                // with implicit deref on use, distinct from `*T` (which wraps a
                // value `data` in `__Ptr_T`). The declared element type is T, so
                // the body sees `name` as a plain T lvalue backed by the byref.
                BindParameterDefault(p, paramType); // ES2180 — an out slot has no default
                parameters.Add(new BoundParameter(p.Name, paramType, ByRef: true, ReadOnlyByRef: false, IsOut: true));
                DeclareLocal(p.Name, paramType, mutable: true, p.NameSpan, isParameter: true);
                continue;
            }

            var (byRef, readOnlyByRef) = RefFlags(p.Type);

            if (byRef && Types.StarClassError(paramType, p.Type.Span))
            {
                // `*refData` is illegal — drop the `*`, bind as the bare reference.
                byRef = false;
                readOnlyByRef = false;
            }
            else if (byRef && !readOnlyByRef && paramType is DataType)
            {
                // A value-`data` by-ref parameter is the `__Ptr_T` wrapper — Go's
                // `*T`: nullable, aliasing, first-class. (The escape-analysis pass
                // may later downgrade a provably non-escaping one to a managed
                // pointer.) Primitive `*T` stays `ref T`; `readonly *T` stays `in T`.
                paramType = new HeapPointerBoundType(paramType);
                byRef = false;
            }

            // The readonly receiver (`readonly func (c: T) m()`) borrows `in this`: bind
            // it as a read-only-byref, immutable binding so body writes to it or its
            // fields are rejected (the no-copy, no-mutation contract).
            var isReadonlyReceiver = receiverKind == Esharp.Symbols.ReceiverKind.ReadonlyValue
                && syntax.Parameters.Count > 0 && ReferenceEquals(p, syntax.Parameters[0]);
            if (isReadonlyReceiver)
            {
                parameters.Add(new BoundParameter(p.Name, paramType, ByRef: false, ReadOnlyByRef: true, DefaultValue: BindParameterDefault(p, paramType)));
                DeclareLocal(p.Name, paramType, mutable: false, p.NameSpan, isParameter: true);
                continue;
            }

            parameters.Add(new BoundParameter(p.Name, paramType, byRef, readOnlyByRef, DefaultValue: BindParameterDefault(p, paramType)));
            DeclareLocal(p.Name, paramType, mutable: true, p.NameSpan, isParameter: true);
        }

        // Primary-ctor capture: an in-body method of a headered class binds with
        // the header params reachable as capture candidates. The shared Captured
        // set (one per class, across all its methods) feeds Pass 4's field
        // synthesis. Promoted free functions never get this context — they see
        // only their receiver parameter.
        var prevCapture = Ctx.HeaderCapture;
        if (captureOwner is { HeaderParameters: { Count: > 0 } ownerHeader } && parameters.Count > 0)
        {
            if (!Ctx.HeaderCaptured.TryGetValue(captureOwner.Name, out var capturedSet))
                Ctx.HeaderCaptured[captureOwner.Name] = capturedSet = new HashSet<string>(StringComparer.Ordinal);
            var headerMap = new Dictionary<string, BoundType>(StringComparer.Ordinal);
            foreach (var hp in ownerHeader)
                headerMap[hp.Name] = ResolveType(hp.Type);
            Ctx.HeaderCapture = new HeaderCaptureContext(captureOwner.Name, headerMap, capturedSet, parameters[0].Type);
        }

        var body = Statements.BindBlock(syntax.Body);
        Ctx.HeaderCapture = prevCapture;
        // Every stream yield becomes an awaited channel write in its synthesized
        // producer, even when the source has no explicit await.
        var hasAwait = Ctx.CurrentFunctionHasAwait || (returnsAsyncStream && hasYield);
        if (returnsAsyncStream && hasYield)
            asyncShape = AsyncReturnShape.AsyncEnumerable;

        // Lower `async let` into eager-started Task bindings + hoisted implicit
        // awaits at first use. After this pass the body contains no
        // BoundAsyncLetStatement nodes — only plain var decls + awaits — so both
        // emitters hit the normal statement / expression emission surface.
        body = AsyncLetLowering.Rewrite(body, Data.Symbols, Diagnostics);

        // A synchronous caller of an uncolored async function stores its started
        // ValueTask<T> and blocks only when the bound value is first consumed.
        // This deliberately stays outside the async state-machine path.
        if (!hasAwait)
            body = Esharp.Lowering.SyncFutureLowering.Rewrite(body);

        // Spill awaits out of expression positions so no `await` ever suspends with
        // a live operand on the state machine's evaluation stack (ES0900). Hoists
        // each `await` (and the sub-expressions evaluated before it) into temps,
        // leaving every await as a `let __spill_N = await …` the emitter can suspend
        // across cleanly. Only async bodies need it.
        if (hasAwait)
            body = Esharp.Lowering.AsyncSpill.AsyncSpillLowering.Rewrite(body);

        // ES2130: inside a `task func` body, function literals must not capture
        // a `var` from the surrounding (task-func) scope. Shared state must be
        // threaded explicitly via chan<T> parameters.
        if (syntax.IsTaskFunc)
            EnforceNoMutableCaptureInTaskFunc(body, syntax.Name);

        // Definite-return: a non-void concrete function must terminate on every
        // path. `abstract func` has no body to analyze.
        // A yielded async stream completes by closing its synthesized producer; source
        // fall-through is therefore the normal completion path, not a missing return.
        if (returnType is not VoidType
            && !(returnsAsyncStream && hasYield)
            && syntax.Modifier != Esharp.Syntax.FunctionModifier.Abstract)
            Match.CheckDefiniteReturn(body, syntax);

        Scope = prevScope;
        Ctx.CurrentReturnType = prevReturn;
        Ctx.CurrentFunctionHasAwait = prevHasAwait;
        Ctx.CurrentFunctionAllowsYield = prevAllowsYield;
        Ctx.CurrentFunctionName = prevName;
        Ctx.CurrentFunctionBlockSpan = prevBlockSpan;
        var attributes = syntax.Attributes.Select(a => a.Arguments is not null ? $"{a.Name}({a.Arguments})" : a.Name).ToList();
        // InheritColon is a marker — the chain-walk in BindData converts it to
        // a concrete role (Fulfill / Override / PassThrough / ReAbstract).
        var role = syntax.Modifier switch
        {
            Esharp.Syntax.FunctionModifier.Virtual => InheritanceRole.Virtual,
            Esharp.Syntax.FunctionModifier.Abstract => InheritanceRole.Abstract,
            Esharp.Syntax.FunctionModifier.InheritColon => InheritanceRole.Fulfill, // placeholder; refined in BindData
            _ => InheritanceRole.None,
        };
        return new BoundFunctionDeclaration(syntax.IsPublic, syntax.Name, syntax.TypeParameters, parameters, returnType, body, attributes, hasAwait, role, syntax.IsTaskFunc, asyncShape,
            syntax.ExplicitInterface is null ? null : InterfaceName(syntax.ExplicitInterface),
            ExplicitInterfaceType: syntax.ExplicitInterface is null ? null : ResolveType(syntax.ExplicitInterface),
            ReceiverKind: receiverKind,
            IsTypeBodyMethod: syntax.IsTypeBodyMethod)
        { Span = syntax.Span, Symbol = Data.Symbols.FunctionForDecl(syntax) };
    }

    // For an `await`-using function, the declared return type selects the async shape
    // and unwraps to its result type (the uncolored model: the body returns the
    // unwrapped value). A bare value type is the default (ValueTask wrap), returned
    // unchanged. An explicit `-> void` is async-void (event handlers); an omitted
    // return stays the awaitable ValueTask default.
    // The async return-shape rule lives in the AsyncBinder slice — one authority shared
    // by free functions (here), function literals (the lambda binder), and the emitter.
    static (BoundType result, AsyncReturnShape shape) ClassifyAsyncReturn(BoundType rt, bool explicitReturn)
        => AsyncBinder.ClassifyReturn(rt, explicitReturn);

    void EnforceNoMutableCaptureInTaskFunc(BoundStatement stmt, string taskFuncName)
    {
        foreach (var fl in WalkFunctionLiterals(stmt))
        {
            foreach (var cap in fl.CapturedVariables)
            {
                if (cap.Mutable)
                    Diagnostics.Report(fl.Span,
                        $"ES2130: 'var {cap.Name}' captured across 'task func {taskFuncName}' boundary — shared mutable state is a race. " +
                        $"Capture an immutable 'let' (an immutable capture is permitted), or thread shared state through a 'chan<T>'.");
            }
        }
    }

    IEnumerable<BoundFunctionLiteralExpression> WalkFunctionLiterals(BoundStatement stmt)
    {
        switch (stmt)
        {
            case BoundBlockStatement b:
                foreach (var s in b.Statements)
                    foreach (var l in WalkFunctionLiterals(s)) yield return l;
                break;
            case BoundIfStatement i:
                foreach (var l in WalkFunctionLiterals(i.Then)) yield return l;
                if (i.Else is not null)
                    foreach (var l in WalkFunctionLiterals(i.Else)) yield return l;
                break;
            case BoundWhileStatement w:
                foreach (var l in WalkFunctionLiterals(w.Body)) yield return l;
                break;
            case BoundForEachStatement f:
                foreach (var l in WalkFunctionLiterals(f.Body)) yield return l;
                break;
            case BoundVariableDeclaration vd:
                foreach (var l in WalkFunctionLiteralsExpr(vd.Initializer)) yield return l;
                break;
            case BoundReturnStatement r:
                if (r.Expression is not null)
                    foreach (var l in WalkFunctionLiteralsExpr(r.Expression)) yield return l;
                break;
            case BoundExpressionStatement e:
                foreach (var l in WalkFunctionLiteralsExpr(e.Expression)) yield return l;
                break;
            case BoundAssignment a:
                foreach (var l in WalkFunctionLiteralsExpr(a.Value)) yield return l;
                break;
        }
    }

    IEnumerable<BoundFunctionLiteralExpression> WalkFunctionLiteralsExpr(BoundExpression expr)
    {
        switch (expr)
        {
            case BoundFunctionLiteralExpression fl:
                yield return fl;
                break;
            case BoundCallExpression c:
                foreach (var a in c.Arguments)
                    foreach (var l in WalkFunctionLiteralsExpr(a)) yield return l;
                break;
            case BoundBinaryExpression b:
                foreach (var l in WalkFunctionLiteralsExpr(b.Left)) yield return l;
                foreach (var l in WalkFunctionLiteralsExpr(b.Right)) yield return l;
                break;
        }
    }

    internal BoundConstDeclaration BindNamespaceConst(ConstDeclarationSyntax syntax)
    {
        var explicitTypeHint = syntax.Type is not null ? ResolveHeapPointerAware(syntax.Type) : null;
        var bound = Expressions.BindExpression(syntax.Value, explicitTypeHint);
        var lit = ConstantFolder.Fold(bound);
        if (lit is null)
        {
            Diagnostics.Report(syntax.Span,
                $"ES1011: 'const {syntax.Name}' must fold to a literal value at compile time. Use a `static func` block + `let` for runtime values.");
            lit = new BoundLiteralExpression(0, "0", new PrimitiveType("int"));
        }
        var type = explicitTypeHint ?? lit.Type;
        // Register so any source-side reference to `Name` resolves directly to
        // the literal. The IL emitter still materializes a CLR `literal` field
        // on the module class so the value is reachable from C# / other halves.
        Data.Symbols.AddConstant(new Esharp.Symbols.ConstSymbol
        {
            Name = syntax.Name,
            DeclaringHost = Data.Symbols.Host(Ctx.CurrentNamespace),
            FoldedLiteral = lit,
            Span = syntax.Span,
        });
        return new BoundConstDeclaration(syntax.IsPublic, syntax.Name, type, lit);
    }

    internal BoundStaticFuncDeclaration BindStaticFunc(StaticFuncDeclarationSyntax syntax)
    {
        var fields = new List<BoundField>();
        foreach (var f in syntax.Fields)
        {
            var fieldType = ResolveType(f.Type);
            BoundExpression? defaultValue = null;
            if (f.DefaultValue is not null)
                defaultValue = Expressions.BindExpression(f.DefaultValue);
            fields.Add(new BoundField(f.Name, fieldType, f.IsPublic ?? syntax.IsPublic, f.Mutable, defaultValue));
        }

        // Push field names into a scope so bare `n` (vs `Counter.n`) inside any
        // inner `func` resolves to the static-func host's static field. Without
        // this, `n = n + 1` inside `bump()` binds against nothing and the IL
        // emitter can't write through to the static slot.
        var prevSfScope = Scope;
        Scope = Scope.Child();
        foreach (var f in fields)
            Scope.Declare(f.Name, f.Type);
        var prevSfContext = Ctx.CurrentStaticFuncName;
        Ctx.CurrentStaticFuncName = syntax.Name;

        var functions = new List<BoundFunctionDeclaration>();
        var overrideReturn = syntax.DefaultReturns?.Type;
        foreach (var fn in syntax.Functions)
            functions.Add(BindFunction(fn, overrideReturn));

        Scope = prevSfScope;
        Ctx.CurrentStaticFuncName = prevSfContext;

        return new BoundStaticFuncDeclaration(syntax.IsPublic, syntax.Name, fields, functions);
    }

    // Synthesize a forwarder method on `outerName` that calls `innerMethod` through
    // an embedded field. Used so embedded-method promotion satisfies protocols
    // via the normal SatisfiesInterface + interface-emit path. The forwarder body
    // is `return self.<embeddedField>.<methodName>(args...)` (or void without return).
    internal BoundFunctionDeclaration SynthesizeEmbeddedForwarder(string outerName, FieldSymbol embeddedField, BoundType innerType, BoundFunctionDeclaration innerMethod)
    {
        // Param list: self of the outer type, then innerMethod's params minus its self.
        var outerDecl = Data.Symbols.DataDecl(outerName)!;
        var outerType = (BoundType)new DataType(outerName, outerDecl.TypeParameters, outerDecl, ClassifyData(outerName));
        var newParams = new List<BoundParameter>
        {
            new("self", outerType, ByRef: false),
        };
        for (var i = 1; i < innerMethod.Parameters.Count; i++)
            newParams.Add(innerMethod.Parameters[i]);

        // Build `self.<embedded>.<method>(forwardArgs...)` as the call target.
        // The embedded-field access must carry the field's REAL type: for a
        // pointer embed (`*Inner`) that's the `*Inner` wrapper, not the bare
        // `Inner`. Typing it as the bare inner type makes the emitter treat the
        // `__Ptr_Inner` wrapper field as an `Inner` value and skip the deref —
        // a value-receiver call (`Inner::value()`) then reads the wrapper
        // reference as if it were the struct, returning garbage. With the pointer
        // type the emitter's auto-deref (wrapper → `.Value`) kicks in.
        BoundExpression selfExpr = new BoundNameExpression("self", outerType);
        BoundExpression embeddedAccess = new BoundMemberAccessExpression(selfExpr, embeddedField.Name, embeddedField.Bound!);
        BoundExpression methodAccess = new BoundMemberAccessExpression(embeddedAccess, innerMethod.Name, innerMethod.ReturnType);
        var forwardArgs = newParams.Skip(1)
            .Select(p => (BoundExpression)new BoundNameExpression(p.Name, p.Type))
            .ToList();
        var call = new BoundCallExpression(methodAccess, forwardArgs, innerMethod.ReturnType);

        BoundStatement bodyStmt = innerMethod.ReturnType is VoidType
            ? new BoundExpressionStatement(call)
            : new BoundReturnStatement(call);
        var body = new BoundBlockStatement(new List<BoundStatement> { bodyStmt });

        return new BoundFunctionDeclaration(
            IsPublic: true,
            Name: innerMethod.Name,
            TypeParameters: innerMethod.TypeParameters,
            Parameters: newParams,
            ReturnType: innerMethod.ReturnType,
            Body: body,
            Attributes: Array.Empty<string>(),
            HasAwait: innerMethod.HasAwait);
    }

    /// Bind a parameter's `= default` expression. The default must be a CONSTANT
    /// SHAPE (ES2180) — it is re-materialized at every call site that omits the
    /// argument, so it may not read state, call functions, or reference other
    /// parameters. `out` parameters cannot carry one.
    BoundExpression? BindParameterDefault(ParameterSyntax p, BoundType paramType)
    {
        if (p.DefaultValue is null) return null;
        if (p.IsOut)
        {
            Diagnostics.Report(p.DefaultValue.Span,
                $"ES2180: 'out' parameter '{p.Name}' cannot have a default value — an out slot is written by the callee.");
            return null;
        }
        var bound = Expressions.BindExpression(p.DefaultValue, paramType);
        if (!ConstantFolder.IsConstantShape(bound))
            Diagnostics.Report(p.DefaultValue.Span,
                $"ES2180: default value for parameter '{p.Name}' must be a compile-time constant shape — a literal, 'nil', or a composite/choice construction of such. It is materialized at every call site that omits the argument.");
        return bound;
    }

    /// Bind a `delegate func` into its CLR shape. Parameters carry by-ref / out
    /// intent identically to a function's so the emitted Invoke is metadata-faithful.
    internal BoundDelegateDeclaration BindDelegate(DelegateDeclarationSyntax syntax, string? namespaceName)
    {
        var parameters = new List<BoundParameter>();
        foreach (var p in syntax.Parameters)
        {
            var paramType = ResolveType(p.Type);
            if (p.IsOut)
            {
                parameters.Add(new BoundParameter(p.Name, paramType, ByRef: true, ReadOnlyByRef: false, IsOut: true));
                continue;
            }
            var (byRef, readOnlyByRef) = RefFlags(p.Type);
            if (byRef && !readOnlyByRef && paramType is DataType)
            {
                paramType = new HeapPointerBoundType(paramType);
                byRef = false;
            }
            parameters.Add(new BoundParameter(p.Name, paramType, byRef, readOnlyByRef));
        }
        var returnType = ResolveType(syntax.ReturnType);
        return new BoundDelegateDeclaration(syntax.IsPublic, syntax.Name, namespaceName, parameters, returnType);
    }

    // Walk a function body looking for `await` (which auto-promotes the function
    // to ValueTask<T>) or `async let` (which inserts an implicit await). Used at
    // pass-1 registration time before bodies are bound, so call sites that come
    // before their callee in source can still see the async-ness.
    // An async function whose declared return is an explicit awaitable/void wrapper —
    // Task / Task<T> / ValueTask / ValueTask<T> / async-void / IAsyncEnumerable<T>. A
    // bare call to one yields the wrapper VALUE (not auto-awaited); the default
    // uncolored async (omitted return, or a bare value/Result return) auto-awaits
    // in async callers and becomes a force-on-use future in sync callers.
    internal static bool HasExplicitAsyncWrapperReturn(FunctionDeclarationSyntax fn)
    {
        if (!fn.HasExplicitReturnType) return false;
        return fn.ReturnType switch
        {
            NamedTypeSyntax { Name: "Task" or "ValueTask" or "void" } => true,
            GenericTypeSyntax { Name: "Task" or "ValueTask" or "IAsyncEnumerable" } => true,
            _ => false,
        };
    }

    internal static bool FunctionBodyHasAwait(FunctionDeclarationSyntax func)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(func.Body);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case AwaitExpressionSyntax:
                case AsyncLetStatementSyntax:
                    return true;
                case BlockStatementSyntax b:
                    foreach (var s in b.Statements) stack.Push(s);
                    break;
                case VariableDeclarationStatementSyntax v:
                    if (v.Initializer is not null) stack.Push(v.Initializer);
                    break;
                case IfStatementSyntax ifs:
                    stack.Push(ifs.Condition);
                    stack.Push(ifs.ThenStatement);
                    if (ifs.ElseStatement is not null) stack.Push(ifs.ElseStatement);
                    break;
                case ReturnStatementSyntax r:
                    if (r.Expression is not null) stack.Push(r.Expression);
                    break;
                case ExpressionStatementSyntax es:
                    stack.Push(es.Expression);
                    break;
                case AssignmentStatementSyntax a:
                    stack.Push(a.Target); stack.Push(a.Expression); break;
                case CompoundAssignmentStatementSyntax ca:
                    stack.Push(ca.Target); stack.Push(ca.Value); break;
                case WhileStatementSyntax w:
                    stack.Push(w.Condition); stack.Push(w.Body); break;
                case ForEachStatementSyntax f:
                    stack.Push(f.Collection); stack.Push(f.Body); break;
                case MatchStatementSyntax m:
                    stack.Push(m.Subject);
                    foreach (var arm in m.Arms) PushArm(stack, arm);
                    break;
                case TryStatementSyntax ts:
                    stack.Push(ts.Body);
                    foreach (var c in ts.Catches) stack.Push(c.Body);
                    break;
                case ThrowStatementSyntax th:
                    if (th.Expression is not null) stack.Push(th.Expression);
                    break;
                case DeferStatementSyntax d:
                    stack.Push(d.Body);
                    break;
                case SpawnExpressionSyntax sp:
                    stack.Push(sp.Body);
                    break;
                case SelectStatementSyntax sel:
                    foreach (var arm in sel.Arms) stack.Push(arm.Body);
                    break;
                case BinaryExpressionSyntax bin:
                    stack.Push(bin.Left); stack.Push(bin.Right); break;
                case UnaryExpressionSyntax un:
                    stack.Push(un.Operand); break;
                case CallExpressionSyntax call:
                    stack.Push(call.Target);
                    foreach (var arg in call.Arguments) stack.Push(arg);
                    break;
                case MemberAccessExpressionSyntax ma:
                    stack.Push(ma.Target); break;
                case IndexExpressionSyntax ix:
                    stack.Push(ix.Target); stack.Push(ix.Index); break;
                case ConditionalExpressionSyntax cond:
                    stack.Push(cond.Condition); stack.Push(cond.Consequence); stack.Push(cond.Alternative); break;
                case ParenthesizedExpressionSyntax pe:
                    stack.Push(pe.Expression); break;
                case ObjectCreationExpressionSyntax oc:
                    foreach (var f in oc.Fields) if (f.Value is not null) stack.Push(f.Value);
                    break;
                case ListLiteralExpressionSyntax ll:
                    foreach (var e in ll.Elements) stack.Push(e);
                    break;
                case TupleExpressionSyntax tu:
                    foreach (var e in tu.Elements) stack.Push(e);
                    break;
                case AddressOfExpressionSyntax ao:
                    stack.Push(ao.Target); break;
                case NewExpressionSyntax ne:
                    stack.Push(ne.Target); break;
                case TryUnwrapExpressionSyntax tuw:
                    stack.Push(tuw.Inner); break;
                case MatchExpressionSyntax me:
                    stack.Push(me.Subject);
                    foreach (var arm in me.Arms) PushArm(stack, arm);
                    break;
                case FunctionLiteralExpressionSyntax fl:
                    stack.Push(fl.Body); break;
            }
        }
        return false;
    }

    // Yield in a nested lambda or spawn belongs to that independent callable and is
    // invalid there; it must not change the enclosing function's return shape.
    internal static bool FunctionBodyHasYield(FunctionDeclarationSyntax func)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(func.Body);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case YieldStatementSyntax:
                    return true;
                case SpawnExpressionSyntax:
                case FunctionLiteralExpressionSyntax:
                    continue;
            }
            foreach (var child in SyntaxNavigator.Children(node))
                stack.Push(child);
        }
        return false;
    }

    // Push a match arm's children (guard + block body or `=>` expression body) for the
    // await walk. Either `Body` (a block arm) or `ExprBody` (a `=>` arm) is non-null.
    static void PushArm(Stack<SyntaxNode> stack, MatchArmSyntax arm)
    {
        if (arm.Guard is not null) stack.Push(arm.Guard);
        if (arm.Body is not null) stack.Push(arm.Body);
        if (arm.ExprBody is not null) stack.Push(arm.ExprBody);
    }
}
