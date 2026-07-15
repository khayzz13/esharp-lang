using System.Reflection;
using Mono.Cecil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
// Disambiguate from Mono.Cecil.IMetadataResolver — this implements Esharp.Emit's.
using IMetadataResolver = Esharp.Emit.IMetadataResolver;

namespace Esharp.CodeGen;

// Implements Esharp.Emit.IMetadataResolver — the Cecil-emission contract CodeGen
// depends on (round-2 seam C2-S1). The emit-time Cecil resolver lives WITH the emitter
// (Esharp.CodeGen), not in Esharp.Metadata: the binder depends on Esharp.Metadata for
// bind-time external-symbol discovery, so that module must stay Cecil-free. The
// reflection-discovery internals here are slated to migrate onto Esharp.Metadata's
// MetadataReader (a deferred deepening); the Cecil-reference half is mirrored by
// Esharp.Emit.MetadataBinder.
public sealed class ILTypeResolver : IMetadataResolver
{
    readonly ModuleDefinition _module;
    readonly DiagnosticBag _diagnostics;
    readonly Dictionary<string, TypeDefinition> _definedTypes = new(StringComparer.Ordinal);
    // The reference-identity bridge from the binder's spine to emitted Cecil
    // methods: every user-declared free function and static-func member registers
    // its MethodSymbol → MethodDefinition here at definition time, so a call's
    // BoundCallExpression.ResolvedMethod recovers the EXACT target method —
    // namespace-correct by construction, never a first-match-wins module name walk.
    readonly Dictionary<Esharp.Symbols.MethodSymbol, MethodReference> _methodsBySymbol =
        new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, DataClassification> _classifications = new(StringComparer.Ordinal);
    readonly Dictionary<string, Type> _runtimeTypeCache = new(StringComparer.Ordinal);
    // Active import scope for bare external-name resolution. During emission this is
    // narrowed per declaring compilation unit (SetUnitImports) so file A's `using`s
    // never resolve file B's bare names — mirroring the binder, which is per-unit. A
    // List (not a HashSet) keeps tier-2 search order deterministic and in import
    // order. The union across all units is retained as the restore default.
    readonly List<string> _importedNamespaces = new();
    readonly Dictionary<string, string> _importedStaticTypes = new(StringComparer.Ordinal);
    readonly List<string> _unionNamespaces = new();
    readonly Dictionary<string, string> _unionStaticTypes = new(StringComparer.Ordinal);

    // Resolution policy is compilation-local. A process-wide mutable gate lets one project
    // with ImplicitUsings=disable poison concurrent/default compilations in the same host.
    readonly bool _searchCommonNamespaces;
    static IReadOnlyList<string> CommonNamespaces => Esharp.Compiler.UsingEnvironment.Instance.CommonNamespaces;
    IEnumerable<string> AutoSearchNamespaces => _searchCommonNamespaces
        ? Esharp.Compiler.UsingEnvironment.Instance.ExternalNamespaces.Concat(CommonNamespaces)
        : Esharp.Compiler.UsingEnvironment.Instance.ExternalNamespaces;
    // Stack of generic parameter scopes. Each scope maps formal param name → GenericParameter.
    // Pushed by EmitDataStruct when emitting a generic type's fields/methods; used by
    // Resolve() to translate `T`/`A`/`B` references to the right Cecil GenericParameter.
    readonly Stack<Dictionary<string, GenericParameter>> _genericContext = new();

    // Monotonic id source for synthesized closure/display/lambda types
    // (`<>c__Display_N`, `<>c__Lambda_N`). It lives here — on the per-emit resolver —
    // rather than as a process-static counter so the numbering restarts at 0 for each
    // assembly emit. A static counter kept climbing across compilations in the same
    // process, so the same source emitted twice produced different synthesized type
    // names and thus structurally different assemblies — breaking the same-source →
    // same-assembly invariant every differential oracle and reproducible build relies on.
    int _closureIdCounter;
    public int NextClosureId() => _closureIdCounter++;

    // Same rationale for synthesized local names (`<foreach_enum>_N`, `<range_end>_N`):
    // a per-emit counter, not a process-static one, so repeated emits of one source are
    // byte-identical (these names reach the output only via debug symbols, but the
    // determinism invariant must hold regardless of whether PDBs are written).
    int _syntheticLocalIdCounter;
    public int NextSyntheticLocalId() => _syntheticLocalIdCounter++;

    // Optional handle to the shared type registry the binder populated. Used
    // to look up cross-language adapters (ExternalCSharpType) by name during
    // emit — e.g. when a data declaration lists `: ICSharpInterface` and the
    // emitter needs a TypeReference for it.
    readonly Esharp.Symbols.SymbolTable? _externalSymbols;

    public ILTypeResolver(ModuleDefinition module, DiagnosticBag diagnostics, IReadOnlyList<BoundUsing>? imports = null, IReadOnlyList<string>? referencePaths = null, Esharp.Symbols.SymbolTable? externalSymbols = null, bool implicitUsings = true)
    {
        _externalSymbols = externalSymbols;
        _module = module;
        _diagnostics = diagnostics;
        _searchCommonNamespaces = implicitUsings;

        if (imports is not null)
            ApplyImports(imports);
        // Snapshot the full union so SetUnitImports(null) restores it after a
        // unit-scoped emission completes.
        _unionNamespaces.AddRange(_importedNamespaces);
        foreach (var kv in _importedStaticTypes) _unionStaticTypes[kv.Key] = kv.Value;

        if (referencePaths is { Count: > 0 })
        {
            // Load reference assemblies into the AppDomain so SearchAssemblies and
            // ResolveOpenGenericType find them via AppDomain.CurrentDomain.GetAssemblies().
            foreach (var path in referencePaths)
            {
                // Ref assemblies (metadata-only stubs in packs/.../ref/) poison the fusion
                // loader if attempted first — go straight to the implementation assembly.
                if (IsRefAssemblyPath(path))
                {
                    var implPath = TryResolveImplementationAssembly(path);
                    if (implPath is not null)
                        TryLoadAssembly(implPath);
                    else
                        TryLoadAssembly(path); // fallback: maybe it's loadable after all
                }
                else
                {
                    TryLoadAssembly(path);
                }
            }

            // Add reference directories to Cecil's resolver so module.ImportReference
            // can resolve assembly references from the loaded types.
            if (module.AssemblyResolver is DefaultAssemblyResolver resolver)
            {
                var dirs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in referencePaths)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (dir is not null && dirs.Add(dir))
                        resolver.AddSearchDirectory(dir);
                }
            }
        }

        RegisterStdlibWithResolver(module);
    }

    /// The E#-authored stdlib (`Esharp.Stdlib.dll`) is bound by metadata name
    /// (`ResultOpenType` probe-loads it via `Assembly.LoadFrom`), NOT passed as a
    /// referencePath — so its directory is never added to Cecil's assembly resolver.
    /// Without it, `_module.ImportReference` of a CLOSED generic method on
    /// `Esharp.Stdlib.Result`2` (the promoted combinators — `Map`/`UnwrapOr`/…) cannot
    /// resolve the declaring assembly and silently ERASES the receiver's type
    /// arguments to `object` (ILVerify: `Found Func`2<!0,!!0>, Expected …<…,object>`).
    /// Registering the stdlib's directory here lets combinator calls on the flipped
    /// `Result` resolve on the closed instance exactly as for any referenced assembly.
    /// No-op when the C# seed is active (`ESHARP_USE_RUNTIME_RESULT=1`), since the seed
    /// is already a normal compiler reference.
    void RegisterStdlibWithResolver(ModuleDefinition module)
    {
        if (!ResultIsStdlib()) return;
        if (module.AssemblyResolver is not DefaultAssemblyResolver resolver) return;
        foreach (var path in StdlibCandidatePaths())
        {
            if (!File.Exists(path)) continue;
            TryLoadAssembly(path);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) resolver.AddSearchDirectory(dir);
            return;
        }
    }

    // Build the active import scope from a using list. Aliases are resolved entirely
    // in the binder (the bound tree carries the full type path), so `import.Path` for
    // an alias is a TYPE, not a namespace — never add it to the namespace search.
    void ApplyImports(IReadOnlyList<BoundUsing> imports)
    {
        foreach (var import in imports)
        {
            if (import.Alias is not null) continue;
            if (import.IsStatic)
            {
                var lastDot = import.Path.LastIndexOf('.');
                var shortName = lastDot >= 0 ? import.Path[(lastDot + 1)..] : import.Path;
                _importedStaticTypes[shortName] = import.Path;
            }
            else if (!_importedNamespaces.Contains(import.Path))
            {
                _importedNamespaces.Add(import.Path);
            }
        }
    }

    // The import list currently applied (null = the all-units union). Used to skip the
    // cache-clear + rebuild when the scope is unchanged — the single-file case sets the
    // same reference for every function, so this keeps resolution cached there.
    IReadOnlyList<BoundUsing>? _activeImports;
    bool _activeIsUnion = true;

    /// Narrow bare external-name resolution to one compilation unit's `using`s for the
    /// duration of that unit's member emission; `null` restores the union of all units.
    /// Without this, files with different imports compiled into one assembly cross-
    /// pollinate — e.g. `Esharp.BoundTree.Binder` and Roslyn's internal
    /// `Microsoft.CodeAnalysis.CSharp.Binder` both matching a bare `Binder`, resolving
    /// to whichever the search hits first. The bare-name resolution cache
    /// (_runtimeTypeCache) is keyed by simple name, so it MUST be invalidated whenever
    /// the scope changes or a name resolved under one file's imports would be served to
    /// the next file.
    public void SetUnitImports(IReadOnlyList<BoundUsing>? imports)
    {
        if (imports is null)
        {
            if (_activeIsUnion) return;
            _importedNamespaces.Clear();
            _importedStaticTypes.Clear();
            _importedNamespaces.AddRange(_unionNamespaces);
            foreach (var kv in _unionStaticTypes) _importedStaticTypes[kv.Key] = kv.Value;
            _runtimeTypeCache.Clear();
            _activeImports = null;
            _activeIsUnion = true;
            return;
        }

        if (!_activeIsUnion && ReferenceEquals(imports, _activeImports)) return;
        _importedNamespaces.Clear();
        _importedStaticTypes.Clear();
        ApplyImports(imports);
        _runtimeTypeCache.Clear();
        _activeImports = imports;
        _activeIsUnion = false;
    }

    // Simple type names declared in more than one namespace. Their `_definedTypes`
    // entries are keyed `Ns.Name` so `A.Widget` and `B.Widget` coexist; every other
    // type keeps its bare key, so the common path is unchanged.
    readonly HashSet<string> _collidingTypeNames = new(StringComparer.Ordinal);
    public void SetCollidingTypeNames(IEnumerable<string> names)
    {
        _collidingTypeNames.Clear();
        foreach (var n in names) _collidingTypeNames.Add(n);
    }

    string TypeKey(string name, string? ns) =>
        ns is not null && _collidingTypeNames.Contains(name) ? $"{ns}.{name}" : name;

    public void Register(string name, TypeDefinition type, string? ns = null, int arity = 0)
    {
        var key = TypeKey(name, ns);
        // The bare key is the arity-0 type when one exists. A later generic
        // declaration (`Spawned<T>`) must not replace a same-name non-generic
        // declaration (`Spawned`): unparameterized uses resolve through this key,
        // while constructed uses resolve through the metadata arity key below.
        // If no arity-0 declaration exists, retain the first generic as the legacy
        // bare fallback used by open-generic lookup.
        if (arity == 0 || !_definedTypes.ContainsKey(key))
            _definedTypes[key] = type;
        // A generic type also registers under its CLR metadata identity (`Name`arity`),
        // so a generic `Pair`1` and a same-name `Pair`2` coexist: the bound-DataType
        // resolve path keys by the arity it demands instead of last-write-wins on `Pair`.
        if (arity > 0) _definedTypes[EmitNaming.MetadataTypeName(key, arity)] = type;
    }

    public TypeDefinition? TryResolveRegistered(string name) =>
        _definedTypes.TryGetValue(name, out var t) ? t : null;

    /// Register the Cecil method emitted for a spine MethodSymbol (free function
    /// or static-func member), keyed by reference identity. Called once per
    /// user-declared function at definition time.
    public void RegisterMethod(Esharp.Symbols.MethodSymbol symbol, MethodReference method) =>
        _methodsBySymbol[symbol] = method;

    /// The Cecil method a call's resolved spine symbol targets, or null when the
    /// symbol names something not emitted as a plain static method (the caller
    /// then falls back to its receiver-based / reflection resolution).
    public MethodReference? MethodForSymbol(Esharp.Symbols.MethodSymbol symbol) =>
        _methodsBySymbol.TryGetValue(symbol, out var m) ? m : null;

    /// When `name` resolves to a cross-language `ExternalCSharpType` adapter,
    /// return its underlying handle so callers can walk the C# type's member
    /// surface (e.g. to synthesize MethodReferences for method-impl bridges).
    public ICSharpTypeHandle? TryGetExternalCSharpHandle(string name)
    {
        if (_externalSymbols?.ResolveBound(name, 0) is ExternalCSharpType cs)
            return cs.Handle;
        return null;
    }

    /// Resolve a type by name, falling back to the binder's symbol table for
    /// cross-language adapters when nothing is registered locally. Returns null
    /// only when the name is unknown in both universes.
    public TypeReference? TryResolveByName(string name)
    {
        if (_definedTypes.TryGetValue(name, out var local)) return local;
        if (_externalSymbols?.ResolveBound(name, 0) is { } external)
            return Resolve(external);
        return null;
    }

    /// Resolve a structured conformance entry from a `:` list (`IBox<T>`,
    /// `IEnumerable<T>`, `IDisposable`) to a closed TypeReference. The bound type
    /// already carries its arguments — a bare type-parameter argument arrives as a
    /// flat external whose name binds to the enclosing type's own GenericParameter
    /// through the normal Resolve dispatch. Returns null — not object — when the
    /// base interface is unknown, so a bad conformance is a diagnostic rather than
    /// a silent `object` implementation.
    public TypeReference? ResolveConformance(BoundType iface)
    {
        switch (iface)
        {
            // E#-declared interface (open or closed generic) — registered locally.
            case InterfaceType:
                return Resolve(iface);
            case ExternalType { TypeArgs.Count: 0 } flat:
                // E#/cross-language registries first, then the BCL (IDisposable,
                // IEnumerable, IAsyncDisposable).
                return TryResolveByName(flat.Name)
                    ?? (TryResolveRuntimeType(flat.Name) is { } rt ? _module.ImportReference(rt) : null);
            case ExternalType gen:
            {
                // Open base: an E#-registered generic interface (IBox`1) or a BCL one
                // (IEnumerable`1, IEquatable`1).
                TypeReference? openRef = TryResolveRegistered(gen.Name);
                if (openRef is null && ResolveOpenGenericType(gen.Name, gen.TypeArgs.Count) is { } bclOpen)
                    openRef = _module.ImportReference(bclOpen);
                if (openRef is null) return null;

                var closed = new GenericInstanceType(openRef);
                foreach (var arg in gen.TypeArgs)
                    closed.GenericArguments.Add(ResolveGenericArgument(arg));
                return _module.ImportReference(closed);
            }
            default:
                return Resolve(iface);
        }
    }

    public void RegisterClassification(string name, DataClassification classification) =>
        _classifications[name] = classification;

    public void PushGenericContext(IEnumerable<GenericParameter> parameters)
    {
        var scope = new Dictionary<string, GenericParameter>(StringComparer.Ordinal);
        foreach (var p in parameters)
            scope[p.Name] = p;
        _genericContext.Push(scope);
    }

    public void PopGenericContext() => _genericContext.Pop();

    GenericParameter? LookupGenericParameter(string name)
    {
        foreach (var scope in _genericContext)
            if (scope.TryGetValue(name, out var p))
                return p;
        return null;
    }

    public bool IsValueType(BoundType type) => type switch
    {
        DataType d => _classifications.TryGetValue(d.Name, out var c) ? c == DataClassification.Struct : true,
        ChoiceType => true,
        EnumType => true,
        ResultType => true, // Result<T,E> is a readonly struct
        TupleType => true,  // emits as System.ValueTuple<…>, a value type
        NullableType n => IsValueType(n.Inner), // Nullable<T> is itself a value type
        ArrayBoundType => false, // T[] is a reference type
        InterfaceType => false, // Protocols emit as interfaces (reference types)
        PrimitiveType p => p.Name is not "string",
        // A module-local synthesized type referenced via ExternalType (the async/iterator
        // state-machine STRUCT, a synth display CLASS) carries no DataDeclarationSyntax, so
        // lowering types it as ExternalType rather than DataType. Honor its registered
        // classification first — only a genuinely external (BCL/referenced) name falls to
        // reflection. Without this, a synth struct's name doesn't resolve via reflection,
        // reads as a reference type, and its composite-literal construction emits `newobj`
        // where the CLR wants `initobj`+stores (ILVerify: Found=ref object, Expected=value SM).
        ExternalType ext when _classifications.TryGetValue(ext.Name, out var ec) =>
            ec == DataClassification.Struct,
        ExternalType { TypeArgs.Count: 0 } ext =>
            TryResolveRuntimeType(ext.Name)?.IsValueType == true,
        // Async awaiter placeholder (`__Awaiter<awaitable>`): resolve to the concrete
        // awaiter struct (TaskAwaiter<T> / ValueTaskAwaiter<T> / their void shapes) and
        // honor its value-ness — the same resolution Resolve/BoundTypeToRuntime perform.
        // Without this case the placeholder name falls to the generic branch below,
        // ResolveOpenGenericType("__Awaiter", 1) fails, and the awaiter reads as a
        // reference type — so the awaiter's `get_IsCompleted` / `GetResult` receiver is
        // loaded by value (ldfld) where the CLR wants the field address (ldflda),
        // failing ILVerify with `Found=value awaiter, Expected=address`.
        ExternalType { Name: BackendPlaceholders.Awaiter, TypeArgs.Count: 1 }
            => BoundTypeToRuntime(type)?.IsValueType ?? true,
        // A constructed external generic's value-ness follows its OPEN definition
        // (List<> ref, ValueTuple<,> / Nullable<> value) — the args don't change it.
        ExternalType gen => ResolveOpenGenericType(gen.Name, gen.TypeArgs.Count)?.IsValueType == true,
        _ => false,
    };

    public MethodReference? FindConstructor(TypeDefinition typeDef) =>
        typeDef.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 0);

    /// Resolve a type for use **as a generic argument**. A managed pointer (`ref T`,
    /// `ByReferenceType`) is illegal in a generic-argument position in the CLR, so a `*T`
    /// here must materialize as the heap wrapper `__Ptr_T`, never `T&`. This is the single
    /// invariant that unifies the two `*T` representations (Gap A): a stored/field/collection
    /// `*T` is always the wrapper, and escape analysis may downgrade a *parameter* `*T` to a
    /// managed pointer — but the moment that `*T` flows into a generic instantiation
    /// (`List<*T>`, `Func<*T, …>`, a closed user generic), the wrapper is mandatory.
    public TypeReference ResolveGenericArgument(BoundType type)
    {
        if (type is ByRefBoundType br)
            return ILHeapPointer.ResolveWrapperType(_module, Resolve(br.Inner));
        var resolved = Resolve(type);
        if (resolved is ByReferenceType brt)
            return ILHeapPointer.ResolveWrapperType(_module, brt.ElementType);
        return resolved;
    }

    public TypeReference Resolve(BoundType type)
    {
        switch (type)
        {
            case PrimitiveType p:
                // Inside a generic type body, a "primitive"-looking name may actually be a type parameter
                // (e.g. `first: A` binds as PrimitiveType("A") rather than ExternalType because the binder
                // sees A in its _scope via the type parameter declaration in BindFunction). Check the
                // generic context first before falling back to real primitive resolution.
                if (LookupGenericParameter(p.Name) is { } gp1) return gp1;
                return ResolvePrimitive(p.Name);
            case DataType d:
                var dKeyBare = TypeKey(d.Name, d.Namespace);
                // Prefer the arity-qualified key (`Pair`1` vs `Pair`2`) so two same-name
                // generic arities resolve to the right open type; fall back to the bare
                // key (non-generic types, and the single-arity back-compat path).
                var dKey = d.TypeParameters.Count > 0
                    ? EmitNaming.MetadataTypeName(dKeyBare, d.TypeParameters.Count)
                    : dKeyBare;
                // Generic instantiation: close the open type over its arguments.
                if (d.TypeArgs.Count > 0)
                {
                    if (_definedTypes.TryGetValue(dKey, out var openType) || _definedTypes.TryGetValue(dKeyBare, out openType))
                    {
                        var closed = new GenericInstanceType(openType);
                        foreach (var arg in d.TypeArgs)
                            closed.GenericArguments.Add(Resolve(arg));
                        return _module.ImportReference(closed);
                    }
                    _diagnostics.Report("", 0, 0, $"IL: unresolved generic data type '{d.Name}'");
                    return _module.ImportReference(typeof(object));
                }
                if (_definedTypes.TryGetValue(dKey, out var dt) || _definedTypes.TryGetValue(dKeyBare, out dt)) return dt;
                _diagnostics.Report("", 0, 0, $"IL: unresolved data type '{d.Name}'");
                return _module.ImportReference(typeof(object));
            case ChoiceType c:
                // Generic instantiation: close the reified choice struct over its
                // type arguments (`Option<int>` → `Option`1<int32>`).
                if (c.TypeArgs.Count > 0)
                {
                    if (_definedTypes.TryGetValue(c.Name, out var openChoice))
                    {
                        var closed = new GenericInstanceType(openChoice);
                        foreach (var arg in c.TypeArgs)
                            closed.GenericArguments.Add(Resolve(arg));
                        return _module.ImportReference(closed);
                    }
                    _diagnostics.Report("", 0, 0, $"IL: unresolved generic choice type '{c.Name}'");
                    return _module.ImportReference(typeof(object));
                }
                if (_definedTypes.TryGetValue(c.Name, out var ct)) return ct;
                _diagnostics.Report("", 0, 0, $"IL: unresolved choice type '{c.Name}'");
                return _module.ImportReference(typeof(object));
            case EnumType e:
                if (_definedTypes.TryGetValue(e.Name, out var et)) return et;
                _diagnostics.Report("", 0, 0, $"IL: unresolved enum type '{e.Name}'");
                return _module.ImportReference(typeof(int));
            case InterfaceType p:
                // Generic interface instantiation: close the open interface over its args
                // (`IBox<int>` → `IBox\`1<int32>`) so dispatch targets the closed slot.
                if (p.TypeArgs.Count > 0 && _definedTypes.TryGetValue(p.Name, out var openIp))
                {
                    var closedIp = new GenericInstanceType(openIp);
                    foreach (var arg in p.TypeArgs)
                        closedIp.GenericArguments.Add(Resolve(arg));
                    return _module.ImportReference(closedIp);
                }
                if (_definedTypes.TryGetValue(p.Name, out var pt)) return pt;
                _diagnostics.Report("", 0, 0, $"IL: unresolved protocol type '{p.Name}'");
                return _module.ImportReference(typeof(object));
            case NamedDelegateType nd:
                if (_definedTypes.TryGetValue(TypeKey(nd.Name, nd.Namespace), out var ndt)) return ndt;
                _diagnostics.Report("", 0, 0, $"IL: unresolved delegate type '{nd.Name}'");
                return _module.ImportReference(typeof(object));
            case NullableType n: return ResolveNullable(n);
            case VoidType: return _module.ImportReference(typeof(void));
            case ResultType r:
            {
                var okRef = Resolve(r.OkType);
                var errRef = Resolve(r.ErrorType);
                var openResult = _module.ImportReference(ResultOpenType());
                var closed = new GenericInstanceType(openResult);
                closed.GenericArguments.Add(okRef);
                closed.GenericArguments.Add(errRef);
                return _module.ImportReference(closed);
            }
            case ChanType chan:
            {
                // Chan<T> → the active Chan`1 (stdlib if present, else the C# seed).
                var elementRef = Resolve(chan.ElementType);
                var openChan = _module.ImportReference(ChanOpenType());
                var closed = new GenericInstanceType(openChan);
                closed.GenericArguments.Add(elementRef);
                return _module.ImportReference(closed);
            }
            case TupleType tt:
            {
                var elemRefs = tt.ElementTypes.Select(Resolve).ToArray();
                var openType = tt.ElementTypes.Count switch
                {
                    2 => _module.ImportReference(typeof(ValueTuple<,>)),
                    3 => _module.ImportReference(typeof(ValueTuple<,,>)),
                    4 => _module.ImportReference(typeof(ValueTuple<,,,>)),
                    5 => _module.ImportReference(typeof(ValueTuple<,,,,>)),
                    6 => _module.ImportReference(typeof(ValueTuple<,,,,,>)),
                    7 => _module.ImportReference(typeof(ValueTuple<,,,,,,>)),
                    _ => _module.ImportReference(typeof(ValueTuple<,>)),
                };
                var closed = new GenericInstanceType(openType);
                foreach (var e in elemRefs) closed.GenericArguments.Add(e);
                return _module.ImportReference(closed);
            }
            case ArrayBoundType arr:
                return new Mono.Cecil.ArrayType(Resolve(arr.ElementType));
            case ByRefBoundType br:
                return new Mono.Cecil.ByReferenceType(Resolve(br.Inner));
            case HeapPointerBoundType hp:
                return ILHeapPointer.ResolveWrapperType(_module, Resolve(hp.Inner));
            case Esharp.BoundTree.FunctionPointerType fp:
                return ILPointerEmitter.BuildCecilFunctionPointerType(fp, this);
            case ExternalCSharpType cs:
                // Type sourced from a sibling .cs file in the same project. The C# half
                // emits to its own intermediate assembly identity (handle.AssemblyName);
                // E# IL emits a TypeRef scoped to that identity. After ILRepack fuses
                // both halves into one DLL, those refs collapse to intra-assembly
                // TypeDef references in the merged output.
                return BuildCSharpTypeReference(cs);
            case InferredType:
                // The inference hole — nothing better is known; `object` is the
                // erasure, exactly what the old `ExternalType("var")` produced.
                return _module.ImportReference(typeof(object));
            case ExternalType ext:
                // Type parameter references can arrive here as ExternalType (`A`/`B`) from the binder.
                if (LookupGenericParameter(ext.Name) is { } gp2) return gp2;
                // Async awaiter placeholder (`__Awaiter<awaitable>`): resolve the concrete
                // awaiter struct from the awaitable's GetAwaiter() return (Task<int> →
                // TaskAwaiter<int>), the same computation a direct async emitter performs.
                if (ext.Name == BackendPlaceholders.Awaiter && ext.TypeArgs.Count == 1)
                    return ResolveAwaiterType(ext.TypeArgs[0]);
                // Well-known runtime types get direct shortcuts before falling back to external search
                // Structured external generic (`List<int>`, `Func<*Box, bool>`): close the
                // open base over the BOUND arguments — each resolves through the normal
                // generic-argument path (pointers → wrapper, nullable, nested generics,
                // type params), so no type syntax is ever re-parsed out of a name.
                if (ext.TypeArgs.Count > 0)
                    return ResolveExternalGeneric(ext);
                return ResolveExternal(ext.Name);
            default: return _module.ImportReference(typeof(object));
        }
    }

    TypeReference ResolveNullable(NullableType n)
    {
        var inner = Resolve(n.Inner);
        // Reference types are already nullable — no wrapping needed
        if (!IsValueType(n.Inner)) return inner;
        // Value types → Nullable<T>
        var nullableOpen = _module.ImportReference(typeof(Nullable<>));
        return _module.ImportReference(new GenericInstanceType(nullableOpen) { GenericArguments = { inner } });
    }

    TypeReference ResolvePrimitive(string name) => name switch
    {
        "int" => _module.ImportReference(typeof(int)),
        "string" => _module.ImportReference(typeof(string)),
        "bool" => _module.ImportReference(typeof(bool)),
        "double" => _module.ImportReference(typeof(double)),
        "float" => _module.ImportReference(typeof(float)),
        "long" => _module.ImportReference(typeof(long)),
        "byte" => _module.ImportReference(typeof(byte)),
        "char" => _module.ImportReference(typeof(char)),
        "short" => _module.ImportReference(typeof(short)),
        "ushort" => _module.ImportReference(typeof(ushort)),
        "sbyte" => _module.ImportReference(typeof(sbyte)),
        "uint" => _module.ImportReference(typeof(uint)),
        "ulong" => _module.ImportReference(typeof(ulong)),
        "void" => _module.ImportReference(typeof(void)),
        _ => _module.ImportReference(typeof(object)),
    };

    TypeReference ResolveExternal(string name)
    {
        if (name is "var" or "object")
            return _module.ImportReference(typeof(object));
        if (_definedTypes.TryGetValue(name, out var localType))
            return localType;
        if (TryResolveRuntimeType(name) is { } t)
            return _module.ImportReference(t);
        _diagnostics.Warn("", 0, 0, $"IL: unresolved external type '{name}', falling back to object");
        return _module.ImportReference(typeof(object));
    }

    /// Close an external generic over its STRUCTURED bound arguments. Each argument
    /// resolves through ResolveGenericArgument (pointer args → the `__Ptr_T` wrapper,
    /// nullable / tuple / nested generics / type params via the normal Resolve
    /// dispatch). The base resolves: user-registered open type first, then the
    /// `Result` strangler, then the open BCL generic. This replaces the deleted
    /// string subsystem (ResolveGenericExternal + the hand-written type parsers) —
    /// the parser already parsed this once; nobody re-parses it here.
    TypeReference ResolveExternalGeneric(ExternalType ext)
    {
        var cecilArgs = ext.TypeArgs.Select(ResolveGenericArgument).ToList();

        // User-defined generic base (`Box<int>`): close the registered TypeDefinition.
        if (_definedTypes.TryGetValue(ext.Name, out var userOpen) && userOpen.HasGenericParameters)
        {
            var userInst = new GenericInstanceType(userOpen);
            foreach (var arg in cecilArgs) userInst.GenericArguments.Add(arg);
            return _module.ImportReference(userInst);
        }

        // `Result<T,E>` reaching here as an external (e.g. the element of
        // `List<Result<int,string>>`): close over the strangler-fig open type so the
        // external view remains the E# stdlib's `Esharp.Stdlib.Result`2`.
        if (ext.Name == "Result" && cecilArgs.Count == 2)
        {
            var resultInst = new GenericInstanceType(_module.ImportReference(ResultOpenType()));
            foreach (var arg in cecilArgs) resultInst.GenericArguments.Add(arg);
            return _module.ImportReference(resultInst);
        }

        var openType = ResolveOpenGenericType(ext.Name, cecilArgs.Count);
        if (openType is null)
        {
            _diagnostics.Warn("", 0, 0, $"IL: unresolved generic base type '{ext.Name}' with arity {cecilArgs.Count}");
            return _module.ImportReference(typeof(object));
        }

        var genericInst = new GenericInstanceType(_module.ImportReference(openType));
        foreach (var arg in cecilArgs)
            genericInst.GenericArguments.Add(arg);
        return _module.ImportReference(genericInst);
    }

    /// <summary>Resolve an open generic runtime type by base name and arity.</summary>
    Type? ResolveOpenGenericType(string baseName, int arity)
    {
        // Compiler-synthesized generic BCL builders — seeded unconditionally (the
        // arity-0 forms are in ResolveRuntimeTypeCore; same rationale: not user-facing,
        // outside the common-namespace search, must resolve even under implicitUsings:false).
        var wellKnown = (baseName, arity) switch
        {
            ("AsyncValueTaskMethodBuilder", 1) => typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<>),
            ("AsyncTaskMethodBuilder", 1)      => typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder<>),
            _ => (Type?)null,
        };
        if (wellKnown is not null) return wellKnown;

        var mangledName = $"{baseName}`{arity}";

        foreach (var ns in _importedNamespaces)
        {
            var imported = Type.GetType($"{ns}.{mangledName}");
            if (imported is not null) return imported;
        }
        if (_searchCommonNamespaces)
            foreach (var ns in CommonNamespaces)
            {
                var t = Type.GetType($"{ns}.{mangledName}");
                if (t is not null) return t;
            }
        var direct = Type.GetType(mangledName);
        if (direct is not null) return direct;

        // Search loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var ns in _importedNamespaces)
            {
                var imported = asm.GetType($"{ns}.{mangledName}");
                if (imported is not null) return imported;
            }
            if (_searchCommonNamespaces)
                foreach (var ns in CommonNamespaces)
                {
                    var t = asm.GetType($"{ns}.{mangledName}");
                    if (t is not null) return t;
                }
            var anyMatch = asm.GetType(mangledName);
            if (anyMatch is not null) return anyMatch;
        }

        // The backing assembly may not be loaded yet (see TryForceLoadType).
        return TryForceLoadType(mangledName);
    }

    /// <summary>Try to resolve a type name as a primitive, returning both Cecil and runtime representations.</summary>
    (TypeReference cecilRef, Type runtimeType)? TryResolvePrimitiveByName(string name) => name switch
    {
        "int" => (_module.ImportReference(typeof(int)), typeof(int)),
        "string" => (_module.ImportReference(typeof(string)), typeof(string)),
        "bool" => (_module.ImportReference(typeof(bool)), typeof(bool)),
        "double" => (_module.ImportReference(typeof(double)), typeof(double)),
        "float" => (_module.ImportReference(typeof(float)), typeof(float)),
        "long" => (_module.ImportReference(typeof(long)), typeof(long)),
        "byte" => (_module.ImportReference(typeof(byte)), typeof(byte)),
        "char" => (_module.ImportReference(typeof(char)), typeof(char)),
        "short" => (_module.ImportReference(typeof(short)), typeof(short)),
        "ushort" => (_module.ImportReference(typeof(ushort)), typeof(ushort)),
        "sbyte" => (_module.ImportReference(typeof(sbyte)), typeof(sbyte)),
        "uint" => (_module.ImportReference(typeof(uint)), typeof(uint)),
        "ulong" => (_module.ImportReference(typeof(ulong)), typeof(ulong)),
        _ => null,
    };

    /// <summary>Resolve a .NET type by name via reflection. Caches results.</summary>
    public Type? TryResolveRuntimeType(string name)
    {
        if (_importedStaticTypes.TryGetValue(name, out var staticImport))
            name = staticImport;

        if (_runtimeTypeCache.TryGetValue(name, out var cached))
            return cached;

        var type = ResolveRuntimeTypeCore(name);
        if (type is not null)
            _runtimeTypeCache[name] = type;
        return type;
    }

    Type? ResolveRuntimeTypeCore(string name) => name switch
    {
        // Primitive keyword names — let `int.TryParse`, `string.Join`, etc. work directly.
        "int" => typeof(int),
        "long" => typeof(long),
        "short" => typeof(short),
        "byte" => typeof(byte),
        "sbyte" => typeof(sbyte),
        "uint" => typeof(uint),
        "ulong" => typeof(ulong),
        "ushort" => typeof(ushort),
        "float" => typeof(float),
        "double" => typeof(double),
        "decimal" => typeof(decimal),
        "bool" => typeof(bool),
        "char" => typeof(char),
        "string" => typeof(string),
        "object" => typeof(object),
        // Common BCL types
        "Console" => typeof(Console),
        "Math" => typeof(Math),
        "MathF" => typeof(MathF),
        "Environment" => typeof(Environment),
        "Guid" => typeof(Guid),
        "DateTime" => typeof(DateTime),
        "DateTimeOffset" => typeof(DateTimeOffset),
        "TimeSpan" => typeof(TimeSpan),
        "Convert" => typeof(Convert),
        "File" => typeof(System.IO.File),
        "Path" => typeof(System.IO.Path),
        "Directory" => typeof(System.IO.Directory),
        "Task" => typeof(System.Threading.Tasks.Task),
        // Bare (non-generic) ValueTask must resolve exactly like bare Task — an
        // explicit entry, not a reliance on the fuzzy common-namespace search (which
        // is off when implicit usings are disabled, silently erasing `ValueTask` to
        // `object` and sending a `await someValueTask` down the wrong Task awaiter path).
        "ValueTask" => typeof(System.Threading.Tasks.ValueTask),
        "CancellationToken" => typeof(CancellationToken),
        "CancellationTokenSource" => typeof(CancellationTokenSource),
        // E# stdlib types are loaded by metadata name because the stdlib is compiled
        // by this compiler (a C# project reference would form a build cycle).
        "ChanSelect" => ProbeStdlibType("Esharp.Stdlib.ChanSelect"),
        "ChanSelect.Arm" => ProbeStdlibType("Esharp.Stdlib.ChanSelect+Arm"),
        "ChanSelect.Kind" => ProbeStdlibType("Esharp.Stdlib.ChanSelect+Kind"),
        // Async state-machine builders (System.Runtime.CompilerServices). These are
        // COMPILER-SYNTHESIZED references the async lowering emits by bare name — they
        // are not user-facing types, live outside the curated common-namespace search,
        // and must resolve UNCONDITIONALLY (even under implicitUsings:false, which gates
        // that search off). Seeded here like Task; the generic arity-1 forms are seeded
        // in ResolveOpenGenericType so both `AsyncValueTaskMethodBuilder` (void) and
        // `AsyncValueTaskMethodBuilder<T>` resolve.
        "AsyncValueTaskMethodBuilder" => typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder),
        "AsyncTaskMethodBuilder" => typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder),
        "AsyncVoidMethodBuilder" => typeof(System.Runtime.CompilerServices.AsyncVoidMethodBuilder),
        // The async state-machine contract interfaces (System.Runtime.CompilerServices),
        // compiler-synthesized by AsyncLowering: the SM struct declares `IAsyncStateMachine`.
        // These live outside the curated common-namespace search, so without an explicit seed
        // `ResolveConformance("IAsyncStateMachine")` returns null and the struct silently drops
        // its interface — which makes `builder.Start<SM>` fail the `where TStateMachine :
        // IAsyncStateMachine` constraint at JIT/runtime (ILVerify does not catch generic
        // constraints, so the assembly passes the verify gate then throws at invocation).
        "IAsyncStateMachine" => typeof(System.Runtime.CompilerServices.IAsyncStateMachine),
        "INotifyCompletion" => typeof(System.Runtime.CompilerServices.INotifyCompletion),
        "ICriticalNotifyCompletion" => typeof(System.Runtime.CompilerServices.ICriticalNotifyCompletion),
        // Fallback: search loaded assemblies
        _ => Type.GetType($"System.{name}") ?? Type.GetType(name) ?? SearchAssemblies(name),
    };

    /// <summary>
    /// Pre-loads reference assemblies into the AppDomain, handling ref-assembly → impl-assembly
    /// resolution. Call before binding so the binder's reflection-based type lookup works.
    /// </summary>
    public static void PreloadReferenceAssemblies(IReadOnlyList<string> referencePaths)
    {
        foreach (var path in referencePaths)
        {
            if (IsRefAssemblyPath(path))
            {
                var implPath = TryResolveImplementationAssembly(path);
                if (implPath is not null)
                    TryLoadAssembly(implPath);
                else
                    TryLoadAssembly(path);
            }
            else
            {
                TryLoadAssembly(path);
            }
        }
    }

    internal static bool IsRefAssemblyPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/ref/net") || normalized.Contains("/packs/") && normalized.Contains(".Ref/");
    }

    internal static bool TryLoadAssembly(string path)
    {
        try
        {
            Assembly.LoadFrom(path);
            return true;
        }
        catch
        {
            // "Assembly with same name is already loaded" — already in the AppDomain, fine.
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// When a ref assembly (metadata-only stub from a targeting pack) can't be loaded for
    /// execution, try to find the real implementation assembly in the shared framework.
    /// Ref path pattern:  .../packs/{Name}.Ref/{ver}/ref/net10.0/Foo.dll
    /// Impl path pattern: .../shared/{Name}/{ver}/Foo.dll
    /// </summary>
    internal static string? TryResolveImplementationAssembly(string refPath)
    {
        var fileName = Path.GetFileName(refPath);

        // Try mapping packs/.../ref/ → shared/.../
        // e.g. .../packs/Microsoft.AspNetCore.App.Ref/10.0.3/ref/net10.0/Foo.dll
        //   → .../shared/Microsoft.AspNetCore.App/10.0.3/Foo.dll
        var normalized = refPath.Replace('\\', '/');
        var packsIdx = normalized.IndexOf("/packs/", StringComparison.Ordinal);
        if (packsIdx >= 0)
        {
            var dotnetRoot = normalized[..packsIdx];
            // Extract pack name and version from the ref path
            var afterPacks = normalized[(packsIdx + "/packs/".Length)..];
            var segments = afterPacks.Split('/');
            // segments: [PackName.Ref, version, ref, net10.0, Foo.dll]
            if (segments.Length >= 5 && segments[2] == "ref")
            {
                var packRef = segments[0];
                var version = segments[1];
                var packName = packRef.EndsWith(".Ref", StringComparison.Ordinal)
                    ? packRef[..^4]
                    : packRef;
                var implPath = $"{dotnetRoot}/shared/{packName}/{version}/{fileName}";
                if (File.Exists(implPath))
                    return implPath;

                // Version might differ between ref pack and shared framework — try any version
                var sharedDir = $"{dotnetRoot}/shared/{packName}";
                if (Directory.Exists(sharedDir))
                {
                    foreach (var versionDir in Directory.GetDirectories(sharedDir))
                    {
                        var candidate = Path.Combine(versionDir, fileName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
        }

        // Fallback: search next to the runtime assemblies
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir is not null)
        {
            var candidate = Path.Combine(runtimeDir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    Type? SearchAssemblies(string name)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        // The search is tiered across ALL assemblies per tier, not assembly-by-
        // assembly — otherwise the first assembly that happens to carry a common-
        // namespace match wins before a later assembly's explicitly-imported match
        // is ever tried (e.g. CoreLib's `System.Reflection.Binder` shadowing an
        // imported `Esharp.BoundTree.Binder`).

        // Tier 1: exact / already-qualified name.
        foreach (var asm in assemblies)
            if (asm.GetType(name) is { } exact) return exact;

        // Tier 2: explicit `using` imports win over the implicit standard set.
        foreach (var ns in _importedNamespaces)
            foreach (var asm in assemblies)
                if (asm.GetType($"{ns}.{name}") is { } imported) return imported;

        // Tier 3: the curated common namespaces — skipped when the common search is off.
        if (_searchCommonNamespaces)
            foreach (var ns in CommonNamespaces)
                foreach (var asm in assemblies)
                    if (asm.GetType($"{ns}.{name}") is { } common) return common;

        // Tier 4: the backing assembly may not be loaded yet (the CLR loads referenced
        // assemblies lazily; a hosted compile without explicit reference paths sees only
        // what's already touched). Force-load an imported namespace's assembly and retry.
        return TryForceLoadType(name);
    }

    /// Force-load the assembly backing an imported namespace and look up the type there.
    /// A non-CoreLib BCL type (System.Threading.Channels.Channel, System.Text.Json.*) is
    /// unresolvable until its assembly is touched. The split-assembly convention is that
    /// the assembly name matches a namespace prefix, so probe each imported namespace from
    /// most- to least-specific. Loaded assemblies are then visible to the normal search.
    Type? TryForceLoadType(string typeName)
    {
        // Explicit `using` imports first, then the implicit standard namespaces. The
        // implicit tier matters: a common namespace whose backing framework assembly
        // is not yet loaded (e.g. System.Threading.Channels.dll) would otherwise never
        // resolve — the loaded-assembly search can't see it, and nothing force-LOADS it.
        // That left `Channel`/`Channel<T>` resolvable only by luck of load order. Here
        // we Assembly.Load the namespace-as-assembly-name (the .NET convention) so the
        // resolution is deterministic and `using`-free, matching how `Task` resolves.
        foreach (var ns in _importedNamespaces)
            if (Esharp.Compiler.UsingEnvironment.ForceLoadType(ns, typeName) is { } t) return t;
        foreach (var ns in AutoSearchNamespaces)
            if (Esharp.Compiler.UsingEnvironment.ForceLoadType(ns, typeName) is { } t) return t;
        return null;
    }

    /// <summary>Map a BoundType to the runtime System.Type for reflection-based method lookup.</summary>
    public Type? BoundTypeToRuntime(BoundType type) => type switch
    {
        PrimitiveType p => p.Name switch
        {
            "int" => typeof(int),
            "string" => typeof(string),
            "bool" => typeof(bool),
            "double" => typeof(double),
            "float" => typeof(float),
            "long" => typeof(long),
            "byte" => typeof(byte),
            "char" => typeof(char),
            "short" => typeof(short),
            "uint" => typeof(uint),
            "ulong" => typeof(ulong),
            _ => typeof(object),
        },
        ExternalType ext => ResolveExternalRuntime(ext),
        VoidType => typeof(void),
        ResultType r => MakeResultRuntimeType(r),
        ChanType chan => MakeChanRuntimeType(chan),
        TupleType tt => MakeTupleRuntimeType(tt),
        ArrayBoundType arr => (BoundTypeToRuntime(arr.ElementType) ?? typeof(object)).MakeArrayType(),
        // `T?`: value types become `Nullable<T>`; reference types are already nullable
        // and stay their underlying runtime type.
        NullableType n => BoundTypeToRuntime(n.Inner) is { IsValueType: true } innerRt
            ? typeof(Nullable<>).MakeGenericType(innerRt)
            : BoundTypeToRuntime(n.Inner),
        // User-defined types have no System.Type at compile time — return null so
        // callers fall through to module-local resolution paths.
        DataType or ChoiceType or EnumType or InterfaceType or HeapPointerBoundType => null,
        _ => typeof(object),
    };

    Type MakeResultRuntimeType(ResultType r)
    {
        var okRuntime = BoundTypeToRuntime(r.OkType) ?? typeof(object);
        var errRuntime = BoundTypeToRuntime(r.ErrorType) ?? typeof(object);
        return ResultOpenType().MakeGenericType(okRuntime, errRuntime);
    }

    // ── E#-authored stdlib binding ────────────────────────────────────────────
    // The language builtin `Result` binds against the E#-compiled stdlib
    // (`Esharp.Stdlib.dll`, type `Esharp.Stdlib.Result`2`) resolved by metadata
    // name off an assembly loaded from disk — not `typeof`, because the stdlib is
    // compiled by this compiler.
    static readonly object _stdlibProbeLock = new();
    static bool _stdlibProbed;
    static Type? _stdlibResultOpen;
    static bool _stdlibChanProbed;
    static Type? _stdlibChanOpen;
    static bool _stdlibChanOpsProbed;
    static Type? _stdlibChanOps;

    /// The open `Result`2` definition the lowering targets: `Esharp.Stdlib.Result`2`
    /// in the E# standard library.
    internal static Type ResultOpenType()
    {
        if (!_stdlibProbed)
        {
            lock (_stdlibProbeLock)
            {
                if (!_stdlibProbed)
                {
                    _stdlibResultOpen = ProbeStdlibType("Esharp.Stdlib.Result`2");
                    _stdlibProbed = true;
                }
            }
        }
        return _stdlibResultOpen ?? throw new InvalidOperationException(
            "Esharp.Stdlib.Result`2 is required. Build/copy Esharp.Stdlib.dll beside the compiler or set ESHARP_STDLIB.");
    }

    internal static bool ResultIsStdlib() => true;

    /// The open `Chan`1` the `chan<T>` intrinsic lowers against: `Esharp.Stdlib.Chan`1`
    /// in the E# standard library.
    internal static Type ChanOpenType()
    {
        if (!_stdlibChanProbed)
            lock (_stdlibProbeLock)
                if (!_stdlibChanProbed) { _stdlibChanOpen = ProbeStdlibType("Esharp.Stdlib.Chan`1"); _stdlibChanProbed = true; }
        return _stdlibChanOpen ?? throw new InvalidOperationException(
            "Esharp.Stdlib.Chan`1 is required. Build/copy Esharp.Stdlib.dll beside the compiler or set ESHARP_STDLIB.");
    }

    /// The stdlib's `ChanOps` static-dispatch class.
    internal static Type ChanOpsType()
    {
        if (!_stdlibChanOpsProbed)
            lock (_stdlibProbeLock)
                if (!_stdlibChanOpsProbed) { _stdlibChanOps = ProbeStdlibType("Esharp.Stdlib.ChanOps"); _stdlibChanOpsProbed = true; }
        return _stdlibChanOps ?? throw new InvalidOperationException(
            "Esharp.Stdlib.ChanOps is required. Build/copy Esharp.Stdlib.dll beside the compiler or set ESHARP_STDLIB.");
    }

    // Stdlib location + load is owned by the compiler layer (`Esharp.Compiler.StdlibProbe`)
    // so the binder's reflective resolution and this emitter's metadata-name resolution
    // share ONE source of truth for where `Esharp.Stdlib.dll` lives.
    static Type? ProbeStdlibType(string metadataName) => Esharp.Compiler.StdlibProbe.ProbeType(metadataName);

    static IEnumerable<string> StdlibCandidatePaths() => Esharp.Compiler.StdlibProbe.CandidatePaths();

    Type MakeChanRuntimeType(ChanType chan)
    {
        var elemRuntime = BoundTypeToRuntime(chan.ElementType) ?? typeof(object);
        return ChanOpenType().MakeGenericType(elemRuntime);
    }

    Type MakeTupleRuntimeType(TupleType tt)
    {
        var runtimeTypes = tt.ElementTypes.Select(t => BoundTypeToRuntime(t) ?? typeof(object)).ToArray();
        var openType = runtimeTypes.Length switch
        {
            2 => typeof(ValueTuple<,>),
            3 => typeof(ValueTuple<,,>),
            4 => typeof(ValueTuple<,,,>),
            5 => typeof(ValueTuple<,,,,>),
            6 => typeof(ValueTuple<,,,,,>),
            7 => typeof(ValueTuple<,,,,,,>),
            _ => typeof(ValueTuple<,>),
        };
        return openType.MakeGenericType(runtimeTypes);
    }

    /// <summary>Runtime-Type view of a structured external. A flat name resolves
    /// directly; a constructed generic closes the open base over the arguments'
    /// runtime forms (an argument with no runtime form — a user type — degrades to
    /// `object`, the correct reflection view).</summary>
    Type ResolveExternalRuntime(ExternalType ext)
    {
        // Async awaiter placeholder — resolve to the concrete awaiter struct so method
        // lookups on the awaiter (GetResult / IsCompleted) bind against the real type.
        if (ext.Name == BackendPlaceholders.Awaiter && ext.TypeArgs.Count == 1)
            return AwaiterRuntimeType(ext.TypeArgs[0]) ?? typeof(System.Runtime.CompilerServices.TaskAwaiter);

        if (ext.TypeArgs.Count == 0)
            return TryResolveRuntimeType(ext.Name) ?? typeof(object);

        // `Result<T,E>` resolves to the strangler-fig open type (`ResultOpenType()`),
        // identical to the Cecil path — so the runtime-Type view of `Result<…>` (e.g.
        // the element of `List<Result<int,string>>`) never diverges to the seed.
        var openType = ext.Name == "Result" && ext.TypeArgs.Count == 2
            ? ResultOpenType()
            : ResolveOpenGenericType(ext.Name, ext.TypeArgs.Count);
        if (openType is null) return typeof(object);

        var runtimeArgs = new Type[ext.TypeArgs.Count];
        for (var i = 0; i < runtimeArgs.Length; i++)
            runtimeArgs[i] = BoundTypeToRuntime(ext.TypeArgs[i]) ?? typeof(object);

        try { return openType.MakeGenericType(runtimeArgs); }
        catch { return typeof(object); }
    }

    /// The awaiter struct for an awaitable — its `GetAwaiter()` return type. For a closed
    /// awaitable (`Task&lt;int&gt;` / `ValueTask&lt;int&gt;`) this is the closed awaiter
    /// (`TaskAwaiter&lt;int&gt;` / `ValueTaskAwaiter&lt;int&gt;`); for the void shapes the
    /// non-generic awaiter. Null when the awaitable has no resolvable runtime type (a user
    /// awaitable backed by a compile-time-only type), letting the caller fall back.
    Type? AwaiterRuntimeType(BoundType awaitable)
    {
        var awaitableRt = BoundTypeToRuntime(awaitable);
        var getAwaiter = awaitableRt?.GetMethod(
            "GetAwaiter", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        return getAwaiter?.ReturnType;
    }

    /// <summary>
    /// Resolve the concrete awaiter type while retaining Cecil generic parameters.
    /// Reflection is useful for discovering an awaiter's runtime shape, but a method
    /// type parameter has no runtime <see cref="Type"/> while its body is emitted:
    /// mapping <c>Task&lt;T&gt;</c> through reflection therefore produced
    /// <c>TaskAwaiter&lt;object&gt;</c>.  Build the BCL task/value-task awaiters directly
    /// from the resolved Cecil awaitable so <c>T</c> remains the exact generic
    /// parameter owned by the emitted method/state machine.
    /// </summary>
    TypeReference ResolveAwaiterType(BoundType awaitable)
    {
        var awaitableRef = Resolve(awaitable);
        var element = awaitableRef.GetElementType();

        if (awaitableRef is GenericInstanceType genericAwaitable &&
            genericAwaitable.GenericArguments.Count == 1)
        {
            var awaiterOpen = element.FullName switch
            {
                "System.Threading.Tasks.Task`1" => typeof(System.Runtime.CompilerServices.TaskAwaiter<>),
                "System.Threading.Tasks.ValueTask`1" => typeof(System.Runtime.CompilerServices.ValueTaskAwaiter<>),
                _ => null,
            };

            if (awaiterOpen is not null)
            {
                var closed = new GenericInstanceType(_module.ImportReference(awaiterOpen));
                closed.GenericArguments.Add(genericAwaitable.GenericArguments[0]);
                return _module.ImportReference(closed);
            }
        }

        var voidAwaiter = element.FullName switch
        {
            "System.Threading.Tasks.Task" => typeof(System.Runtime.CompilerServices.TaskAwaiter),
            "System.Threading.Tasks.ValueTask" => typeof(System.Runtime.CompilerServices.ValueTaskAwaiter),
            _ => null,
        };
        if (voidAwaiter is not null)
            return _module.ImportReference(voidAwaiter);

        return _module.ImportReference(
            AwaiterRuntimeType(awaitable) ?? typeof(System.Runtime.CompilerServices.TaskAwaiter));
    }

    /// <summary>
    /// Attempts to find a params overload. Returns the method + fixedCount (number of args
    /// before the params tail) if one matches the provided arg types, or null.
    /// </summary>
    public (MethodReference method, int fixedCount, Type elementType)? ResolveParamsMethod(
        Type declaringType, string methodName, Type[] argTypes)
    {
        var candidates = declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == methodName);
        foreach (var m in candidates)
        {
            var ps = m.GetParameters();
            if (ps.Length == 0) continue;
            var last = ps[ps.Length - 1];
            if (!last.IsDefined(typeof(ParamArrayAttribute), false)) continue;
            if (!last.ParameterType.IsArray) continue;

            var fixedCount = ps.Length - 1;
            if (argTypes.Length < fixedCount) continue;

            // The fixed portion must match (or be assignable). Keep it simple: accept any for now.
            // TODO: stricter matching for overload disambiguation.
            var elementType = last.ParameterType.GetElementType()!;
            return (_module.ImportReference(m), fixedCount, elementType);
        }
        return null;
    }

    /// <summary>Find a method on a runtime type and import it into the Cecil module.</summary>
    // Is `argType` acceptable for a parameter typed `paramType`? Covers identity /
    // reference conversions (List<string> → IEnumerable<string>), by-ref element
    // assignability, open generic parameters (inference fills them later), and a
    // lenient value↔value rule (numeric/struct coercions the emitter handles). An
    // unknown arg (`object`, from an un-mappable bound type) never disqualifies.
    static bool ArgAssignable(Type paramType, Type argType)
    {
        if (argType == typeof(object)) return true;
        if (paramType.IsGenericParameter) return true;
        // A *constructed* parameter that mentions an open generic param (`IEnumerable<T>`,
        // `Func<T,U>`, `T[]`) — distinct from a bare `T`, handled above. Inference fills
        // the params later, but applicability still demands structural compatibility: a
        // scalar `int` is NOT an `IEnumerable<T>`. Without this, xUnit's
        // `Equal<T>(IEnumerable<T>, IEnumerable<T>)` ties with `Equal<T>(T, T)` for two
        // `int` args and the tiebreak picks the collection overload — `Found=Int32,
        // Expected=ref IEnumerable<object>` at verify time.
        if (paramType.ContainsGenericParameters)
            return OpenGenericArgApplicable(paramType, argType);
        if (paramType.IsAssignableFrom(argType)) return true;
        if (paramType.IsByRef && paramType.GetElementType()!.IsAssignableFrom(argType)) return true;
        // Numeric / char widening between primitives (int→long, char→int, …). Scoped
        // to IsPrimitive so an arbitrary struct (e.g. char↛ReadOnlySpan<char>) is NOT
        // treated as applicable — that would shadow the real char overload.
        if (paramType.IsPrimitive && argType.IsPrimitive) return true;
        return false;
    }

    // How specifically does the open-generic parameter `param` close onto `arg`? Returns
    // the count of CONCRETE generic-definition levels that match (an open type-parameter
    // contributes nothing — it swallows whatever the arg has there), or -1 if the shapes
    // are incompatible. `Func<Task<TResult>>` vs `Func<Task<int>>` → 2 (Func, Task; then
    // TResult↦int); `Func<TResult>` vs `Func<Task<int>>` → 1 (Func; TResult↦Task<int>).
    // The higher specificity is C#'s better-conversion winner, so a result-preserving
    // overload is chosen over one whose bare `T` absorbs the wrapper.
    static int StructuralSpecificity(Type param, Type arg)
    {
        if (param.IsGenericParameter) return 0;          // open `T` binds anything, no specificity
        if (param == arg) return 1;
        if (param.IsArray && arg.IsArray)
        {
            var inner = StructuralSpecificity(param.GetElementType()!, arg.GetElementType()!);
            return inner < 0 ? -1 : inner + 1;
        }
        if (param.IsGenericType && arg.IsGenericType
            && param.GetGenericTypeDefinition() == arg.GetGenericTypeDefinition())
        {
            var pa = param.GetGenericArguments();
            var aa = arg.GetGenericArguments();
            if (pa.Length != aa.Length) return -1;
            var total = 1;   // this concrete level matched
            for (var i = 0; i < pa.Length; i++)
            {
                var inner = StructuralSpecificity(pa[i], aa[i]);
                if (inner < 0) return -1;
                total += inner;
            }
            return total;
        }
        return -1;
    }

    // Is `argType` structurally compatible with a constructed open-generic parameter
    // (`IEnumerable<T>`, `Func<T,U>`, `T[]`)? Bare `T` accepts anything; an array/by-ref
    // unwraps; a constructed generic requires the arg (or one of its interfaces/bases) to
    // instantiate the same definition. `Nullable<T>` accepts any value (T → T?). An
    // unknown (`object`) arg never disqualifies.
    static bool OpenGenericArgApplicable(Type paramType, Type argType)
    {
        if (argType == typeof(object)) return true;
        if (paramType.IsGenericParameter) return true;
        if (paramType.IsByRef)
            return OpenGenericArgApplicable(paramType.GetElementType()!, argType);
        if (paramType.IsArray)
            return argType.IsArray
                && OpenGenericArgApplicable(paramType.GetElementType()!, argType.GetElementType()!);
        if (paramType.IsGenericType)
        {
            var def = paramType.GetGenericTypeDefinition();
            if (def == typeof(Nullable<>)) return true; // T → T?
            foreach (var cand in SelfInterfacesAndBases(argType))
                if (cand.IsGenericType && cand.GetGenericTypeDefinition() == def) return true;
            return false;
        }
        return true; // a generic-mentioning shape we don't model — stay lenient
    }

    static IEnumerable<Type> SelfInterfacesAndBases(Type t)
    {
        for (var b = t; b is not null; b = b.BaseType)
            yield return b;
        foreach (var i in t.GetInterfaces())
            yield return i;
    }

    // True when an exact-arity overload accepts every supplied argument.
    static bool AllArgsApplicable(MethodInfo m, Type[] argTypes)
    {
        var ps = m.GetParameters();
        if (ps.Length != argTypes.Length) return false;
        for (var i = 0; i < ps.Length; i++)
            if (!ArgAssignable(ps[i].ParameterType, argTypes[i])) return false;
        return true;
    }

    /// Structurally unify an open parameter type against a concrete argument type,
    /// recording any inferred method type arguments by position. Descends constructed
    /// generics (`Func<TValue,TNew>` vs `Func<int,int>` pins `TNew`), arrays, and
    /// by-refs. First inference per slot wins (concurrent slots should agree).
    static void UnifyGenericArg(Type paramType, Type argType, Type?[] inferred)
    {
        if (paramType.IsGenericParameter)
        {
            var pos = paramType.GenericParameterPosition;
            if (pos >= 0 && pos < inferred.Length && inferred[pos] is null)
                inferred[pos] = argType;
            return;
        }
        if (paramType.IsByRef && argType.IsByRef)
        {
            UnifyGenericArg(paramType.GetElementType()!, argType.GetElementType()!, inferred);
            return;
        }
        if (paramType.IsArray && argType.IsArray)
        {
            UnifyGenericArg(paramType.GetElementType()!, argType.GetElementType()!, inferred);
            return;
        }
        if (paramType.IsGenericType && argType.IsGenericType
            && paramType.GetGenericTypeDefinition() == argType.GetGenericTypeDefinition())
        {
            var pa = paramType.GetGenericArguments();
            var aa = argType.GetGenericArguments();
            for (var i = 0; i < pa.Length && i < aa.Length; i++)
                UnifyGenericArg(pa[i], aa[i], inferred);
        }
    }

    public MethodReference? ResolveExternalMethod(Type declaringType, string methodName, int argCount, Type[]? argTypes = null, bool silent = false, Type[]? explicitTypeArgs = null)
    {
        MethodInfo? method = null;

        // An exact-arity overload is only acceptable if every argument is actually
        // assignable to the matching parameter. `Type.GetMethod(name, flags, types)`
        // can return a `params T[]` overload for an arg that isn't a `T[]` (e.g.
        // `string.Join(char, string[])` for a `List<string>` arg) — that overload is
        // NOT applicable; discard it and let scoring pick the real match (the
        // `IEnumerable<string>` overload), or `ResolveParamsMethod` pack the tail.
        if (argTypes is not null)
        {
            var exact = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, argTypes);
            // Only take this fast path on a TRULY exact parameter match. The reflection
            // binder also returns an overload the args reach by covariance (a
            // `Func<Task<int>>` arg binds `Run(Func<Task>)`), which would preempt the
            // scoring below that knows to prefer a generic overload closing exactly onto
            // the arg (`Run<int>(Func<Task<int>>)`). Let those fall through to scoring.
            if (exact is not null && AllArgsApplicable(exact, argTypes)
                && exact.GetParameters().Zip(argTypes, (p, a) => p.ParameterType == a).All(x => x))
                method = exact;
        }

        // Fallback: match by name + param count, optionally filtered by explicit type-arg arity.
        // When argTypes are available, prefer overloads whose parameter types are assignable from the args.
        if (method is null)
        {
            var candidates = declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == methodName && ParameterCountMatches(m, argCount));
            if (explicitTypeArgs is not null)
                candidates = candidates.Where(m => m.IsGenericMethodDefinition && m.GetGenericArguments().Length == explicitTypeArgs.Length);

            if (argTypes is { Length: > 0 })
            {
                // Prefer overloads where every arg is applicable to its parameter; only
                // fall back to the full set (partial matches) when none fully apply.
                var pool = candidates.Where(m => AllArgsApplicable(m, argTypes)).ToList();
                if (pool.Count == 0) pool = candidates.ToList();

                int ExactScore(MethodInfo m)
                {
                    var ps = m.GetParameters();
                    var score = 0;
                    for (var i = 0; i < argTypes.Length && i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        // A concrete exact match scores highest. A generic-def parameter scores
                        // by how SPECIFICALLY its open shape closes onto the arg — C#'s
                        // better-conversion. `Func<Task<TResult>>` (depth-2 concrete: Func·Task)
                        // beats `Func<TResult>` (depth-1: Func, with `TResult` swallowing the
                        // whole `Task<int>`) for a `Func<Task<int>>` arg, so
                        // `Task.Run(func() -> Task<int> {…})` binds `Run<int>` returning
                        // `Task<int>`, not `Run<Task<int>>` returning `Task<Task<int>>`. Both
                        // beat a non-generic overload reached only by covariance (score 0).
                        if (pt == argTypes[i])
                            score += 1000;
                        else if (pt.ContainsGenericParameters)
                            score += System.Math.Max(0, StructuralSpecificity(pt, argTypes[i]));
                    }
                    return score;
                }
                method = pool
                    .OrderByDescending(ExactScore)
                    .ThenBy(m => m.IsGenericMethodDefinition ? 1 : 0)   // prefer a concrete overload over a generic def
                    .ThenBy(m => m.GetParameters().Length)
                    .ThenBy(m => RequiredParameterCount(m))
                    .FirstOrDefault();
            }
            else
            {
                method = candidates
                    .OrderBy(m => m.GetParameters().Length)
                    .ThenBy(m => RequiredParameterCount(m))
                    .FirstOrDefault();
            }
        }

        // Interfaces don't surface inherited members through GetMethods — a method on a
        // BASE interface (e.g. `DisposeAsync` on `IAsyncDisposable`, inherited by
        // `IAsyncEnumerator<T>`) is found by walking the implemented interfaces.
        if (method is null && declaringType.IsInterface)
        {
            foreach (var baseIface in declaringType.GetInterfaces())
                if (ResolveExternalMethod(baseIface, methodName, argCount, argTypes, silent: true, explicitTypeArgs) is { } fromBase)
                    return fromBase;
        }

        if (method is null)
        {
            if (!silent)
                _diagnostics.Report("", 0, 0, $"IL: method '{methodName}' not found on '{declaringType.Name}' (argCount={argCount})");
            return null;
        }

        // Explicit type arguments override inference.
        if (method.IsGenericMethodDefinition && explicitTypeArgs is not null)
        {
            try { method = method.MakeGenericMethod(explicitTypeArgs); }
            catch { return null; }
            return ImportWithOptionalDefaults(method);
        }

        // For generic method definitions, close with the provided arg types
        if (method.IsGenericMethodDefinition && argTypes is not null)
        {
            // Infer generic type args from the concrete argument types
            var typeParams = method.GetGenericArguments();
            var paramInfos = method.GetParameters();
            var inferredArgs = new Type[typeParams.Length];

            // Unify each parameter type against its argument type, descending into
            // constructed generics so a method type param NESTED in the parameter is
            // pinned — `Map<TNew>(Func<TValue,TNew>)` infers `TNew` from a `Func<int,int>`
            // arg (not just the bare-`T`-param case). Without this `TNew` defaults to
            // `object` and a closed `Result<int,string>.Map` erases to `…<object>`.
            for (var i = 0; i < paramInfos.Length && i < argTypes.Length; i++)
                UnifyGenericArg(paramInfos[i].ParameterType, argTypes[i], inferredArgs);

            // Fill any unresolved with object
            for (var i = 0; i < inferredArgs.Length; i++)
                inferredArgs[i] ??= typeof(object);

            method = method.MakeGenericMethod(inferredArgs);
        }

        return ImportWithOptionalDefaults(method);
    }

    /// Import a reflection method and copy its C# optional-parameter default values onto
    /// the imported parameter definitions. Cecil's <c>ImportReference(MethodInfo)</c> does
    /// not carry defaults (they live in the Constant metadata table, not in a
    /// MethodReference), so without this an omitted trailing optional at an E# call site is
    /// filled with <c>default(T)</c> instead of the callee's declared default (e.g. a
    /// `bool flag = true` parameter silently became `false`).
    MethodReference ImportWithOptionalDefaults(MethodInfo method)
    {
        var mref = _module.ImportReference(method);
        var ps = method.GetParameters();
        for (var i = 0; i < ps.Length && i < mref.Parameters.Count; i++)
        {
            if (!ps[i].HasDefaultValue) continue;
            var pd = mref.Parameters[i];
            pd.IsOptional = true;
            // RawDefaultValue is the underlying constant (the int behind an enum default,
            // a char's code point, …) — exactly what EmitOptionalArgument loads.
            // DefaultValue would hand back a boxed enum instance the literal switch misses.
            pd.Constant = ps[i].RawDefaultValue;
        }
        return mref;
    }

    /// <summary>
    /// Resolve an extension method where the receiver becomes the first argument.
    /// Searches loaded assemblies for public static methods on static classes marked with
    /// [Extension], where the first parameter type is assignable from the receiver.
    /// Generic type parameters are inferred from the receiver + remaining args.
    /// </summary>
    public MethodReference? ResolveExtensionMethod(Type receiverType, string methodName, int argCount, Type[]? argTypes = null, Type[]? explicitTypeArgs = null)
    {
        var match = FindOpenExtensionMethod(receiverType, methodName, argCount);
        return match is null ? null : ImportExtension(match, receiverType, argTypes, explicitTypeArgs);
    }

    /// The OPEN extension-method <see cref="MethodInfo"/> matching a receiver + name +
    /// arg count (cached), or null. Exposed so the emitter can close a generic extension
    /// over CECIL type arguments when a module-only type arg (`AddHostedService&lt;Svc&gt;`,
    /// `Svc` a user type) would otherwise erase to `object` through reflection's
    /// MakeGenericMethod and violate the type parameter's constraint.
    public MethodInfo? FindOpenExtensionMethod(Type receiverType, string methodName, int argCount)
    {
        var key = (receiverType, methodName, argCount);
        if (_extensionCache.TryGetValue(key, out var cached))
            return cached;

        MethodInfo? match = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] asmTypes;
            try { asmTypes = asm.GetTypes(); }
            catch { continue; }

            foreach (var type in asmTypes)
            {
                if (!type.IsSealed || !type.IsAbstract) continue; // static class = sealed + abstract
                if (!type.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;

                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != methodName) continue;
                    if (!m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != argCount + 1) continue; // +1 for receiver

                    var firstParam = ps[0].ParameterType;
                    if (IsExtensionReceiverMatch(firstParam, receiverType))
                    {
                        match = m;
                        break;
                    }
                }
                if (match is not null) break;
            }
            if (match is not null) break;
        }

        _extensionCache[key] = match;
        return match;
    }

    readonly Dictionary<(Type, string, int), MethodInfo?> _extensionCache = new();

    /// Best OPEN extension overload for a call shape, scored by explicit type-arg arity
    /// and per-argument SHAPE — a lambda arg wants a delegate parameter, a value arg
    /// wants a non-delegate / non-`Type` parameter. The DI family is the motivating case:
    /// `AddSingleton` alone has ~10 overloads (`(Type)`, `<T>()`, `<T,U>()`, `<T>(T)`,
    /// `<T>(Func<IServiceProvider,T>)`, …), and a first-match scan picks wrong ones
    /// (`<IngestService>()` → the 2-arity `<T,U>`; `(seedBatch())` → the `(Type)` form).
    /// Returns null when nothing scores positively (the caller falls back).
    public MethodInfo? FindBestExtensionMethod(Type receiverType, string methodName, int argCount,
        int explicitTypeArgCount, bool[] isLambdaArg)
    {
        MethodInfo? best = null;
        var bestScore = int.MinValue;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] asmTypes;
            try { asmTypes = asm.GetTypes(); }
            catch { continue; }
            foreach (var type in asmTypes)
            {
                if (!type.IsSealed || !type.IsAbstract) continue;
                if (!type.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != methodName) continue;
                    if (!m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != argCount + 1) continue;
                    if (!IsExtensionReceiverMatch(ps[0].ParameterType, receiverType)) continue;
                    // Explicit type args pin the arity exactly (`<IngestService>` ⇒ 1 param).
                    if (explicitTypeArgCount > 0 && m.GetGenericArguments().Length != explicitTypeArgCount) continue;

                    var score = 0;
                    for (var i = 0; i < argCount; i++)
                    {
                        var pt = ps[i + 1].ParameterType;
                        var isDelegate = typeof(Delegate).IsAssignableFrom(pt) || pt == typeof(Delegate);
                        if (isLambdaArg[i])
                            score += isDelegate ? 2 : -3;            // lambda ⇒ wants a delegate param
                        else
                        {
                            if (isDelegate) score -= 3;              // value arg into a delegate param: wrong
                            if (pt == typeof(Type)) score -= 3;      // value arg into a `Type` param: wrong
                            else score += 1;
                        }
                    }
                    // A bare type-parameter value parameter (`<T>(T instance)`) is the natural
                    // home for a value arg with no explicit type args — nudge it ahead of
                    // `(Type)`-shaped siblings so an instance registers as itself.
                    if (explicitTypeArgCount == 0 && m.IsGenericMethodDefinition) score += 1;

                    // A matched explicit-arity generic overload is a strong base signal even
                    // with zero value args to shape-score (`AddSingleton<IngestService>()`).
                    if (explicitTypeArgCount > 0 && m.GetGenericArguments().Length == explicitTypeArgCount)
                        score += 2;

                    if (score > bestScore) { bestScore = score; best = m; }
                }
            }
        }
        // `best` already passed the receiver / param-count / arity filters; the score only
        // disambiguates among valid candidates. Return the best (caller still requires it to
        // be a generic definition and gates on a module type actually being involved).
        return best;
    }

    static bool IsExtensionReceiverMatch(Type parameterType, Type receiverType)
    {
        if (parameterType.IsAssignableFrom(receiverType)) return true;
        // Open generic like IEnumerable<T> — check if a closed form matches
        if (parameterType.IsGenericType && parameterType.ContainsGenericParameters)
        {
            var openParamType = parameterType.GetGenericTypeDefinition();
            foreach (var iface in receiverType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openParamType)
                    return true;
            }
            if (receiverType.IsGenericType && receiverType.GetGenericTypeDefinition() == openParamType)
                return true;
        }
        return false;
    }

    MethodReference ImportExtension(MethodInfo method, Type receiverType, Type[]? argTypes, Type[]? explicitTypeArgs = null)
    {
        if (method.IsGenericMethodDefinition)
        {
            var typeParams = method.GetGenericArguments();

            // Explicit call-site type args (`xs.OfType<MethodDeclarationSyntax>()`) are
            // authoritative and the ONLY source for a type parameter that appears in no
            // parameter — `Enumerable.OfType<TResult>(this IEnumerable)` takes the bare
            // non-generic IEnumerable, so TResult is uninferable and would otherwise
            // default to object. Use them when the arity matches.
            if (explicitTypeArgs is { Length: > 0 } && explicitTypeArgs.Length == typeParams.Length)
                return _module.ImportReference(method.MakeGenericMethod(explicitTypeArgs));

            // Otherwise infer each type parameter from the receiver + argument types.
            var fullArgs = new Type[(argTypes?.Length ?? 0) + 1];
            fullArgs[0] = receiverType;
            if (argTypes is not null) Array.Copy(argTypes, 0, fullArgs, 1, argTypes.Length);

            var paramInfos = method.GetParameters();
            var inferred = new Type[typeParams.Length];

            for (var i = 0; i < paramInfos.Length && i < fullArgs.Length; i++)
            {
                InferGenericArgFromParameter(paramInfos[i].ParameterType, fullArgs[i], inferred);
            }

            // Any parameter the inference couldn't pin (and the caller didn't supply
            // explicitly) falls back to object — but if explicit args were given, prefer
            // them slot-by-slot first.
            for (var i = 0; i < inferred.Length; i++)
            {
                if (inferred[i] is not null) continue;
                inferred[i] = explicitTypeArgs is { } ex && i < ex.Length ? ex[i] : typeof(object);
            }

            method = method.MakeGenericMethod(inferred);
        }
        return _module.ImportReference(method);
    }

    /// <summary>
    /// Extract generic type arguments by matching a parameter type pattern against a concrete type.
    /// Handles direct matches (T → Foo), generic instances (IEnumerable&lt;T&gt; → List&lt;Foo&gt;), and nested generics.
    /// </summary>
    static void InferGenericArgFromParameter(Type paramType, Type argType, Type[] inferred)
    {
        if (paramType.IsGenericParameter)
        {
            var pos = paramType.GenericParameterPosition;
            if (pos < inferred.Length && inferred[pos] is null)
                inferred[pos] = argType;
            return;
        }

        if (!paramType.IsGenericType || !paramType.ContainsGenericParameters) return;

        // paramType is something like IEnumerable<T>; argType may be List<Foo> or Foo[] etc.
        // Walk the interfaces/base-chain of argType to find a matching closed generic.
        var openParam = paramType.GetGenericTypeDefinition();
        Type? matchedClosed = null;

        if (argType.IsGenericType && argType.GetGenericTypeDefinition() == openParam)
            matchedClosed = argType;
        else
        {
            foreach (var iface in argType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openParam)
                {
                    matchedClosed = iface;
                    break;
                }
            }
        }

        if (matchedClosed is null) return;

        var paramArgs = paramType.GetGenericArguments();
        var concreteArgs = matchedClosed.GetGenericArguments();
        for (var i = 0; i < paramArgs.Length && i < concreteArgs.Length; i++)
            InferGenericArgFromParameter(paramArgs[i], concreteArgs[i], inferred);
    }

    /// <summary>Find a static method on a runtime type.</summary>
    public MethodReference? ResolveStaticMethod(Type declaringType, string methodName, int argCount, Type[]? argTypes = null)
    {
        MethodInfo? method = null;

        if (argTypes is not null)
            method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, argTypes);

        method ??= declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName && ParameterCountMatches(m, argCount))
            .OrderBy(m => m.GetParameters().Length)
            .ThenBy(m => RequiredParameterCount(m))
            .FirstOrDefault();

        return method is not null ? _module.ImportReference(method) : null;
    }

    public MethodReference? ResolveImportedStaticMethod(string methodName, int argCount, Type[]? argTypes = null, Type[]? explicitTypeArgs = null)
    {
        foreach (var staticTypePath in _importedStaticTypes.Values)
        {
            var runtimeType = TryResolveRuntimeType(staticTypePath);
            if (runtimeType is null)
                continue;

            var methodRef = ResolveExternalMethod(runtimeType, methodName, argCount, argTypes, silent: true, explicitTypeArgs: explicitTypeArgs);
            if (methodRef is not null)
                return methodRef;
        }
        return null;
    }

    static bool ParameterCountMatches(MethodInfo method, int argCount)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == argCount)
            return true;
        if (parameters.Length < argCount)
            return false;
        return RequiredParameterCount(method) <= argCount;
    }

    static int RequiredParameterCount(MethodInfo method) =>
        method.GetParameters().Count(p => !p.IsOptional && !p.IsDefined(typeof(ParamArrayAttribute), false));

    /// <summary>Get the runtime Type for a value on the IL stack given its BoundType.</summary>
    public Type? GetStackType(BoundType type) => BoundTypeToRuntime(type);

    public TypeReference ImportReference(Type type) => _module.ImportReference(type);
    public ModuleDefinition Module => _module;

    // Cache of AssemblyNameReference instances keyed by C# half assembly name.
    // Cecil treats two distinct AssemblyNameReference objects as separate scopes
    // even when they carry the same name, so we hand out one instance per name.
    readonly Dictionary<string, AssemblyNameReference> _csharpAsmRefs = new(StringComparer.Ordinal);

    // Cache TypeReferences by handle so distinct lookups of the same C# type
    // share one MetadataToken in the emitted module — Cecil writes one TypeRef
    // row per unique TypeReference instance.
    readonly Dictionary<string, TypeReference> _csharpTypeRefs = new(StringComparer.Ordinal);

    TypeReference BuildCSharpTypeReference(ExternalCSharpType cs)
    {
        var handle = cs.Handle;
        var fullName = string.IsNullOrEmpty(handle.Namespace)
            ? handle.Name
            : $"{handle.Namespace}.{handle.Name}";
        if (!_csharpTypeRefs.TryGetValue(fullName, out var typeRef))
        {
            if (!_csharpAsmRefs.TryGetValue(handle.AssemblyName, out var asmRef))
            {
                asmRef = new AssemblyNameReference(handle.AssemblyName, new Version(0, 0, 0, 0));
                _module.AssemblyReferences.Add(asmRef);
                _csharpAsmRefs[handle.AssemblyName] = asmRef;
            }
            // Construct the TypeReference scoped to the C# half assembly. We do
            // NOT call _module.ImportReference here because Cecil's import path
            // tries to resolve the assembly to verify metadata, and the C# half
            // PE isn't on disk yet at E# IL emit time. The TypeRef goes directly
            // into the emitted metadata; ILRepack later rewrites the asm scope
            // to point at the merged module's own TypeDefs.
            typeRef = new TypeReference(
                handle.Namespace,
                handle.Name,
                _module,
                asmRef,
                valueType: !handle.IsRefType && !handle.IsInterface);
            _csharpTypeRefs[fullName] = typeRef;
        }
        if (cs.TypeArgs.Count > 0)
        {
            var inst = new GenericInstanceType(typeRef);
            foreach (var arg in cs.TypeArgs)
                inst.GenericArguments.Add(Resolve(arg));
            return inst;
        }
        return typeRef;
    }
}
