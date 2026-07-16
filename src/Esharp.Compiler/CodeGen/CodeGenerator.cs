using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
// BoundProgram + CompilationData (the lowered-program I/O the backend reads) live in
// the bind+flow+lower assembly; CodeGen is its downstream reader.
using Esharp.Binder;
using Esharp.Compilation;

namespace Esharp.CodeGen;

// Explicit output kind for the produced assembly. Replaces the older
// infer-by-name-of-`main` heuristic, which was dishonest when the entry
// point lived on a different half of a mixed compilation (e.g. a `.cs`
// Program.Main).
public enum ILOutputKind { Library, Console }

public static partial class CodeGenerator
{
    // Hosts may compile projects concurrently. Serialise the complete write + verify
    // transaction for a destination: otherwise one compilation can replace the PE
    // after another wrote it but before ILVerify opened it, attributing foreign IL
    // failures to the wrong source (and occasionally loading invalid partial output).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> OutputLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // -----------------------------------------------------------------
    // Primary entry — consumes a LOWERED BoundProgram (post LoweringPipeline).
    //
    // THIS IS THE ARCHITECTURE. The assertion below is load-bearing:
    //   "If any FEATURE node survives into CodeGen, the lowering pipeline
    //    failed to meet its contract." We crash loudly so nothing silently
    //    emits malformed IL for a construct that should have been lowered.
    //
    // After lowering:
    //   - No BoundMatchStatement / BoundMatchExpression
    //   - No BoundDeferStatement
    //   - No BoundForEachStatement
    //   - No BoundTryUnwrapExpression (?)
    //   - No BoundResultCallExpression (ok/error)
    //   - No BoundNullCoalescingExpression / BoundNullConditionalAccessExpression
    //   - No BoundCompoundAssignment
    //   - No BoundLetGuard
    //   - No BoundWithExpression
    //   - No BoundInterpolatedStringExpression
    //   - No BoundRaiseStatement
    //   - No BoundFunctionLiteralExpression / BoundCapturedVariable
    //   - No BoundAwaitExpression / async function bodies (HasAwait == false on all)
    //   - No async-stream yield nodes
    //   - No BoundAsyncLetStatement
    //   - No BoundSpawnExpression / BoundSelectStatement / BoundChanCreationExpression
    //   - No BoundIfExpression / BoundConditionalExpression (expression form)
    //   - Property accessors are synthesized
    //   - Union layout is decided; BoundChoiceDeclaration is a CORE shell
    // -----------------------------------------------------------------

    /// <summary>
    /// The primary CodeGen entry. Consumes a fully-lowered <see cref="BoundProgram"/>
    /// (post <c>LoweringPipeline.Lower</c>) and emits a Cecil <see cref="AssemblyDefinition"/>.
    /// Asserts no FEATURE node survives — crashes loudly if the lowering contract is violated.
    /// </summary>
    public static (AssemblyDefinition Assembly, IReadOnlyList<Diagnostic> Diagnostics) Generate(
        BoundProgram program,
        string assemblyName,
        bool debugSymbols = false,
        IReadOnlyList<string>? referencePaths = null,
        string? internalsVisibleTo = null,
        Esharp.Symbols.SymbolTable? externalSymbols = null,
        ILOutputKind outputKind = ILOutputKind.Library,
        bool implicitUsings = true,
        OptimizationLevel optimization = OptimizationLevel.Debug)
    {
        // ---- THE ASSERTION. Every FEATURE node class that LoweringPipeline must eliminate. ----
        AssertCoreOnly(program.Units);

        return Emit(
            program.Units,
            assemblyName,
            debugSymbols,
            referencePaths,
            internalsVisibleTo,
            externalSymbols,
            outputKind,
            implicitUsings,
            optimization);
    }

    /// <summary>
    /// Walk the entire bound tree and throw <see cref="FeatureNodeInCodeGenException"/>
    /// for every FEATURE node that survives lowering. This is the architectural gate —
    /// an un-lowered feature node reaching here is a pipeline bug, not a user error.
    /// </summary>
    internal static void AssertCoreOnly(IReadOnlyList<BoundCompilationUnit> units)
    {
        var collector = new FeatureNodeCollector();
        foreach (var unit in units)
            collector.VisitCompilationUnit(unit);
        if (collector.Violations.Count > 0)
        {
            var msg = string.Join("\n  ", collector.Violations.Select(v => $"[{v.Kind}] {v.Description}"));
            throw new FeatureNodeInCodeGenException(
                $"LoweringPipeline contract violated — {collector.Violations.Count} FEATURE node(s) survived to CodeGen:\n  {msg}\n" +
                "Each of these should have been eliminated by the corresponding lowering pass.");
        }
    }

    // NOTE: The old public Emit(BoundCompilationUnit) and Emit(IReadOnlyList<BoundCompilationUnit>)
    // entry points have been removed (round-2 seam C2-S3). Generate(BoundProgram) is the ONLY
    // public entry; it runs AssertCoreOnly before delegating to the private implementation below.
    // EmitToFile routes through the same private implementation.

    static (AssemblyDefinition Assembly, IReadOnlyList<Diagnostic> Diagnostics) Emit(
        IReadOnlyList<BoundCompilationUnit> units,
        string assemblyName,
        bool debugSymbols,
        IReadOnlyList<string>? referencePaths,
        string? internalsVisibleTo,
        Esharp.Symbols.SymbolTable? externalSymbols = null,
        ILOutputKind outputKind = ILOutputKind.Library,
        bool implicitUsings = true,
        OptimizationLevel optimization = OptimizationLevel.Debug)
    {
        ILHeapPointer.ResetCache();
        var diagnostics = new DiagnosticBag();
        var documentCache = debugSymbols ? new Dictionary<string, Mono.Cecil.Cil.Document>(StringComparer.Ordinal) : null;

        // Multiple namespaces compile into one assembly (C#-like). Each member is
        // emitted under its declaring unit's namespace, and each namespace gets its
        // own host static class. `ns` is the default for members with no namespace.
        var nsOf = new Dictionary<BoundMember, string>(ReferenceEqualityComparer.Instance);
        foreach (var u in units)
        {
            var uns = string.IsNullOrWhiteSpace(u.NamespaceName) ? "Main" : u.NamespaceName!;
            foreach (var m in u.Members)
                nsOf[m] = uns;
        }
        string NsOf(BoundMember m) => nsOf.TryGetValue(m, out var n) ? n : "Main";
        var ns = units.Select(u => u.NamespaceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Main";

        var allMembers = units.SelectMany(u => u.Members).ToList();

        // Per-unit import scope for emit-time external resolution: each function is
        // resolved under its OWN file's `using`s (via types.SetUnitImports below), so
        // a bare name in file A never binds through file B's imports. The binder is
        // already per-unit; the resolver is built from the union of all imports (line
        // below) and would otherwise lose that scoping — letting cross-file name
        // collisions resolve nondeterministically.
        var importsOf = new Dictionary<BoundMember, IReadOnlyList<BoundUsing>>(ReferenceEqualityComparer.Instance);
        foreach (var u in units)
            foreach (var m in u.Members)
            {
                importsOf[m] = u.Imports;
                if (m is BoundDataDeclaration dd)
                    foreach (var im in dd.InstanceMethods) importsOf[im] = u.Imports;
                else if (m is BoundStaticFuncDeclaration sf)
                    foreach (var f in sf.Functions) importsOf[f] = u.Imports;
            }
        IReadOnlyList<BoundUsing>? ImportsFor(BoundMember m) => importsOf.TryGetValue(m, out var i) ? i : null;

        // Data type names declared in more than one namespace coexist as distinct CLR
        // types (`A.Widget`, `B.Widget`). Their emit-side keys (field maps, resolver
        // registration) are qualified `Ns.Name`; every other type keeps its bare key,
        // so the single-name common path is untouched.
        var collidingNames = allMembers
            .OfType<BoundDataDeclaration>()
            .Where(d => d.DeclaringNamespace is null) // exclude synthesized cross-file partials (promoted methods)
            .GroupBy(d => d.Name)
            .Where(g => g.Select(NsOf).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        string EmitKey(string name, string nsName) => collidingNames.Contains(name) ? $"{nsName}.{name}" : name;
        // Install the facade reflection importer so every BCL type is scoped to the
        // reference assembly a C# consumer compiles against (System.Runtime, ...), rather
        // than the runtime implementation corelib (System.Private.CoreLib) reflection
        // resolves to. Import-time scoping means every reference is born consumable — no
        // post-emit metadata rewrite — so the emitted assembly is a first-class C#
        // reference (CS0012 otherwise). See FacadeReflectionImporter.
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(assemblyName, new Version(1, 0)),
            assemblyName,
            new ModuleParameters
            {
                Kind = outputKind == ILOutputKind.Console ? ModuleKind.Console : ModuleKind.Dll,
                ReflectionImporterProvider = new FacadeReflectionImporterProvider(referencePaths),
            });

        var module = assembly.MainModule;
        EmitDebuggableAttribute(assembly, module, optimization);
        var imports = units.SelectMany(u => u.Imports).Distinct().ToList();
        var types = new ILTypeResolver(module, diagnostics, imports, referencePaths, externalSymbols, implicitUsings);
        types.SetCollidingTypeNames(collidingNames);

        // Mixed-language seam — when a sibling assembly (the .cs half emitted by
        // Roslyn) needs to see this half's internals before ILRepack fuses them,
        // emit an [InternalsVisibleTo] attribute pointing at the sibling. After
        // fusion both halves share one assembly identity and the attribute is
        // moot; before fusion it lets Roslyn's emit consume this half's internals.
        if (!string.IsNullOrEmpty(internalsVisibleTo))
        {
            var ivtCtor = module.ImportReference(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute)
                .GetConstructor(new[] { typeof(string) })!);
            var ivtAttr = new CustomAttribute(ivtCtor);
            ivtAttr.ConstructorArguments.Add(new CustomAttributeArgument(
                module.TypeSystem.String, internalsVisibleTo));
            assembly.CustomAttributes.Add(ivtAttr);
        }

        // Pass 1a: emit protocols and enums first so data types can reference them
        // for interface implementations regardless of source order.
        var emittedProtocols = new HashSet<string>(StringComparer.Ordinal);
        var emittedEnums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in allMembers)
        {
            if (member is BoundInterfaceDeclaration proto)
            {
                if (!emittedProtocols.Add(proto.Name))
                {
                    diagnostics.Report("", 0, 0, $"IL: duplicate protocol '{proto.Name}' across input files");
                    continue;
                }
                EmitProtocolInterface(module, types, proto, NsOf(proto));
            }
            else if (member is BoundEnumDeclaration e)
            {
                if (!emittedEnums.Add(e.Name))
                {
                    diagnostics.Report("", 0, 0, $"IL: duplicate enum '{e.Name}' across input files");
                    continue;
                }
                EmitEnum(module, types, e, NsOf(e));
            }
        }

        // Pass 1a': register delegate type shells (MulticastDelegate subclasses) so a
        // data field or function parameter typed by a `delegate func` resolves. Their
        // .ctor + Invoke members are filled after data/choice types register (pass 1b'),
        // so an Invoke signature mentioning a declared type also resolves.
        var delegateDefs = new List<(BoundDelegateDeclaration del, TypeDefinition type)>();
        var emittedDelegates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in allMembers)
        {
            if (member is BoundDelegateDeclaration del)
            {
                if (!emittedDelegates.Add(del.Name))
                {
                    diagnostics.Report("", 0, 0, $"IL: duplicate delegate '{del.Name}' across input files");
                    continue;
                }
                delegateDefs.Add((del, EmitDelegateShell(module, types, del, NsOf(del))));
            }
        }

        // Pass 1b: emit data structs/classes and choices in two phases so type
        // references between user types resolve regardless of declaration order
        // (forward references). Phase 1 registers every type SHELL (name, namespace,
        // generic parameters); phase 2 resolves fields / payloads / constructors
        // against the now-complete type table. Without the split, a field whose type
        // is declared later in the file resolves to `object` (silent erasure) or a
        // hard `unresolved type` error — protocols already register in pass 1a, so
        // this covers the data↔data, data↔choice, and choice↔choice cycles.
        var structFieldMaps = new Dictionary<string, Dictionary<string, FieldDefinition>>(StringComparer.Ordinal);
        var choiceFieldMaps = new Dictionary<string, Dictionary<string, FieldDefinition>>(StringComparer.Ordinal);

        var dataShells = new List<(BoundDataDeclaration decl, string key, TypeDefinition typeDef)>();
        var structChoiceShells = new List<(BoundChoiceDeclaration decl, TypeDefinition typeDef, FieldDefinition tagField)>();
        var refChoiceShells = new List<(BoundChoiceDeclaration decl, MethodDefinition baseCtor, Dictionary<string, TypeDefinition> subs)>();
        var seenDataKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenChoiceNames = new HashSet<string>(StringComparer.Ordinal);

        // Phase 1 — register every data/choice shell.
        foreach (var member in allMembers)
        {
            if (member is BoundDataDeclaration data)
            {
                // Cross-file partial: a synthesized declaration with no fields/init that carries
                // instance methods defined in another file. Pass 1c will merge its methods into
                // the primary TypeDefinition — skip type creation here.
                if (data.DeclaringNamespace is not null) continue;

                // Key by the CLR metadata identity (`Name`arity`), so a generic `Pair`1`
                // and a same-name `Pair`2` are distinct types — they collide only on a
                // genuine same-(name, arity) redeclaration. (Arity-0 → the bare name, so
                // the non-generic common path is byte-for-byte unchanged.)
                var dataKey = MetadataTypeName(EmitKey(data.Name, NsOf(data)), data.TypeParameters.Count);
                if (!seenDataKeys.Add(dataKey))
                {
                    diagnostics.Report("", 0, 0, $"IL: duplicate data type '{dataKey}' across input files");
                    continue;
                }
                dataShells.Add((data, dataKey, DeclareDataShell(module, types, data, NsOf(data))));
            }
            else if (member is BoundChoiceDeclaration choice)
            {
                if (!seenChoiceNames.Add(choice.Name))
                {
                    diagnostics.Report("", 0, 0, $"IL: duplicate choice type '{choice.Name}' across input files");
                    continue;
                }
                if (choice.IsRef)
                {
                    var (_, baseCtor, subs) = DeclareRefChoiceShell(module, types, choice, NsOf(choice));
                    refChoiceShells.Add((choice, baseCtor, subs));
                }
                else
                {
                    var (typeDef, _, tagField) = DeclareChoiceStructShell(module, types, choice, NsOf(choice));
                    structChoiceShells.Add((choice, typeDef, tagField));
                }
            }
        }

        // Phase 2 — populate members now that every shell is registered. Base classes
        // must be populated before their subclasses: a subclass init's `: base(args)`
        // binds against the base's already-emitted `.ctor` set, so order data by
        // inheritance depth (bases first) regardless of source order. OrderBy is
        // stable, so same-depth types keep source order.
        var dataDeclByName = new Dictionary<string, BoundDataDeclaration>(StringComparer.Ordinal);
        foreach (var s in dataShells) dataDeclByName[s.decl.Name] = s.decl;
        int InheritanceDepth(BoundDataDeclaration d)
        {
            var depth = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = d;
            while (cur.BaseClass is { } baseName && seen.Add(cur.Name)
                   && dataDeclByName.TryGetValue(baseName, out var baseDecl))
            {
                depth++;
                cur = baseDecl;
            }
            return depth;
        }
        var deferredCtorBodies = new List<(BoundDataDeclaration data, Action emit)>();
        foreach (var (data, dataKey, typeDef) in dataShells.OrderBy(s => InheritanceDepth(s.decl)))
        {
            // Scope field-type AND init-body resolution to THIS data's file imports — a
            // field/init referencing a type from a non-implicit `using` (e.g.
            // `Channel<T>` under `using "System.Threading.Channels"`) would otherwise
            // erase to `object`, since the resolver's default search omits it.
            types.SetUnitImports(ImportsFor(data));
            var (fields, ctorBodies) = PopulateDataMembers(module, types, diagnostics, data, typeDef, NsOf(data));
            structFieldMaps[dataKey] = fields;
            if (ctorBodies is not null) deferredCtorBodies.Add((data, ctorBodies));
        }
        types.SetUnitImports(null);
        foreach (var (choice, typeDef, tagField) in structChoiceShells)
            choiceFieldMaps[choice.Name] = PopulateChoiceStructMembers(module, types, choice, typeDef, tagField);
        foreach (var (choice, baseCtor, subs) in refChoiceShells)
            choiceFieldMaps[choice.Name] = PopulateRefChoiceMembers(module, types, choice, baseCtor, subs);

        // Pass 1b': fill delegate .ctor + Invoke now that every declared type is registered.
        foreach (var (del, delType) in delegateDefs)
            EmitDelegateMembers(module, types, del, delType);

        // Pass 1b: pre-register static function signatures so instance methods can call them
        // A static-facet receiver is source-attached to an explicit `static Foo`
        // surface; it must not leak onto the namespace host as a free function.
        var staticFacetFunctions = allMembers.OfType<BoundFunctionDeclaration>()
            .Where(f => f.ReceiverKind == Esharp.Symbols.ReceiverKind.Static)
            .ToList();
        var staticFunctions = allMembers.OfType<BoundFunctionDeclaration>()
            .Where(f => f.ReceiverKind != Esharp.Symbols.ReceiverKind.Static)
            .ToList();
        var namespaceConsts = allMembers.OfType<BoundConstDeclaration>().ToList();
        var namespaceStates = allMembers.OfType<BoundNamespaceStateDeclaration>().ToList();
        var namespaceInitializers = allMembers.OfType<BoundNamespaceInitDeclaration>().ToList();
        var namespacesWithInitializers = namespaceInitializers.Select(NsOf).ToHashSet(StringComparer.Ordinal);
        var methodDefs = new List<(BoundFunctionDeclaration func, MethodDefinition method)>();
        var namespaceAccessorBodies = new List<(BoundNamespaceStateDeclaration State,
            MethodDefinition Method, FieldDefinition? Backing)>();

        // One host static class per namespace, named `NS` in CLR namespace `NS`
        // (full name `NS.NS`). Free functions and namespace `const`s land on the
        // host class for their own namespace, so a cross-namespace qualified call
        // (`MathNs.add(...)`) resolves against `MathNs.MathNs::add`.
        var hostClasses = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        TypeDefinition HostClassFor(string namespaceName)
        {
            if (hostClasses.TryGetValue(namespaceName, out var existing)) return existing;
            var hasPublicMembers = allMembers.Any(m => NsOf(m) == namespaceName && m switch {
                BoundDataDeclaration d => d.IsPublic,
                BoundChoiceDeclaration c => c.IsPublic,
                BoundEnumDeclaration e => e.IsPublic,
                BoundInterfaceDeclaration p => p.IsPublic,
                BoundFunctionDeclaration f => f.IsPublic,
                BoundConstDeclaration k => k.IsPublic,
                BoundNamespaceStateDeclaration s => s.Field.Vis == Esharp.Syntax.Visibility.Public,
                _ => false
            });
            var moduleTypeAttrs = (hasPublicMembers ? TypeAttributes.Public : TypeAttributes.NotPublic)
                | TypeAttributes.Abstract | TypeAttributes.Sealed;
            // A namespace `init` is a meaningful CLR `.cctor`; do not mark that
            // host BeforeFieldInit or the runtime may run it early.
            if (!namespacesWithInitializers.Contains(namespaceName))
                moduleTypeAttrs |= TypeAttributes.BeforeFieldInit;
            // The module class lives in the namespace and is named after its last
            // segment (a CLR type name can't contain '.'): `namespace A.B.C` →
            // type `A.B.C.C`. Single-segment namespaces are unchanged (`Test.Test`).
            var lastSegment = namespaceName[(namespaceName.LastIndexOf('.') + 1)..];
            var hc = new TypeDefinition(namespaceName, lastSegment, moduleTypeAttrs, module.ImportReference(typeof(object)));
            module.Types.Add(hc);
            hostClasses[namespaceName] = hc;
            return hc;
        }

        TypeDefinition? moduleClass = null;
        if (staticFunctions.Count > 0 || namespaceConsts.Count > 0 || namespaceStates.Count > 0 || namespaceInitializers.Count > 0)
        {
            moduleClass = HostClassFor(ns);

            // Namespace-level `const` decls emit as CLR `literal` fields on the
            // host class. E# call sites already inlined the value via binder
            // folding — the field exists for C# interop. Same FieldAttributes
            // pattern as static-func `let X = literal`.
            foreach (var k in namespaceConsts)
            {
                var fieldType = types.Resolve(k.Type);
                var decimalValue = (k.Value as BoundLiteralExpression)?.Value as decimal?;
                var attrs = (k.IsPublic ? FieldAttributes.Public : FieldAttributes.Assembly)
                    | FieldAttributes.Static
                    | (decimalValue is not null
                        ? FieldAttributes.InitOnly
                        : FieldAttributes.Literal | FieldAttributes.HasDefault);
                var fdef = new FieldDefinition(k.Name, attrs, fieldType)
                {
                    Constant = decimalValue is null ? (k.Value as BoundLiteralExpression)?.Value : null,
                };
                if (decimalValue is { } value)
                    AddDecimalConstantAttribute(fdef, value);
                HostClassFor(NsOf(k)).Fields.Add(fdef);
            }

            // Bare typed namespace declarations are host fields. Namespace
            // `let`/`var` are properties in both their stored and computed forms.
            foreach (var state in namespaceStates)
            {
                var field = state.Field;
                var host = HostClassFor(NsOf(state));
                var fieldType = types.Resolve(field.Type);
                if (!field.IsProperty)
                {
                    var attrs = field.Vis switch
                    {
                        Esharp.Syntax.Visibility.Public => FieldAttributes.Public,
                        Esharp.Syntax.Visibility.Private => FieldAttributes.Private,
                        _ => FieldAttributes.Assembly,
                    } | FieldAttributes.Static;
                    if (!field.Mutable) attrs |= FieldAttributes.InitOnly;
                    host.Fields.Add(new FieldDefinition(field.Name, attrs, fieldType));
                    continue;
                }

                FieldDefinition? backing = null;
                if (!field.IsComputedProperty)
                {
                    var backingAttrs = FieldAttributes.Private | FieldAttributes.Static;
                    if (!field.PropHasSet) backingAttrs |= FieldAttributes.InitOnly;
                    backing = new FieldDefinition(PropertyBackingFieldName(field.Name), backingAttrs, fieldType);
                    host.Fields.Add(backing);
                }

                var accessorAccess = field.Vis switch
                {
                    Esharp.Syntax.Visibility.Public => MethodAttributes.Public,
                    Esharp.Syntax.Visibility.Private => MethodAttributes.Private,
                    _ => MethodAttributes.Assembly,
                };
                var accessorAttrs = accessorAccess | MethodAttributes.Static
                    | MethodAttributes.HideBySig | MethodAttributes.SpecialName;

                MethodDefinition getter;
                if (field.IsComputedProperty)
                {
                    getter = new MethodDefinition("get_" + field.Name, accessorAttrs, fieldType);
                    getter.Body.InitLocals = true;
                    host.Methods.Add(getter);
                    namespaceAccessorBodies.Add((state, getter, null));
                }
                else
                {
                    getter = new MethodDefinition("get_" + field.Name, accessorAttrs, fieldType);
                    getter.Body.InitLocals = true;
                    var gil = new ILBuilder(getter);
                    gil.LoadStaticField(backing!);
                    gil.Return();
                    host.Methods.Add(getter);
                }

                MethodDefinition? setter = null;
                if (field.PropHasSet || field.PropHasInit)
                {
                    TypeReference setterReturn = field.PropHasInit
                        ? new RequiredModifierType(module.ImportReference(
                            typeof(System.Runtime.CompilerServices.IsExternalInit)), module.TypeSystem.Void)
                        : module.TypeSystem.Void;
                    setter = new MethodDefinition("set_" + field.Name, accessorAttrs, setterReturn);
                    setter.Parameters.Add(new ParameterDefinition(state.SetterParam ?? "value",
                        ParameterAttributes.None, fieldType));
                    setter.Body.InitLocals = true;
                    host.Methods.Add(setter);
                    if (state.SetterBody is not null)
                    {
                        namespaceAccessorBodies.Add((state, setter, backing));
                    }
                    else
                    {
                        var sil = new ILBuilder(setter);
                        sil.LoadArgByIndex(0);
                        sil.StoreStaticField(backing!);
                        sil.Return();
                    }
                }

                // Stored namespace properties have the same implicit stable
                // location identity as stored instance properties. This remains
                // a ref-return protocol on the static namespace host; it is not
                // an E# pointer-valued member.
                if (backing is not null && (!field.PropHasMut || field.PropHasExplicitLoca))
                {
                    var locationStorage = host.Fields.FirstOrDefault(candidate =>
                        candidate.IsStatic && candidate.Name == field.PropLocaStorageName) ?? backing;
                    var loca = new MethodDefinition("getloca_" + field.Name,
                        accessorAttrs, new ByReferenceType(locationStorage.FieldType));
                    loca.Body.InitLocals = true;
                    var lil = new ILBuilder(loca);
                    lil.LoadStaticFieldAddress(locationStorage);
                    lil.Return();
                    host.Methods.Add(loca);
                }

                var property = new PropertyDefinition(field.Name, PropertyAttributes.None, fieldType)
                {
                    HasThis = false,
                    GetMethod = getter,
                    SetMethod = setter,
                };
                host.Properties.Add(property);
                EmitPropertyCapability(module, property, field);
            }

            foreach (var func in staticFunctions)
            {
                types.SetUnitImports(ImportsFor(func)); // scope signature resolution to this func's file
                // task func: synthesize an inner method `__taskfunc_body_<name>` with the
                // user-declared signature, and a public spawn method `<name>` returning
                // E# stdlib Spawned / Spawned<T>. Pass 2 emits the inner body and a spawn wrapper for the
                // public method.
                if (func.IsTaskFunc)
                {
                    var innerName = $"__taskfunc_body_{func.Name}";
                    var innerReturnType = types.Resolve(func.ReturnType);
                    var innerMethod = new MethodDefinition(
                        innerName,
                        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                        innerReturnType);
                    foreach (var param in func.Parameters)
                    {
                        var pt = types.Resolve(param.Type);
                        if (param.ByRef || param.ReadOnlyByRef) pt = new Mono.Cecil.ByReferenceType(pt);
                        var pd = new ParameterDefinition(param.Name, ParameterAttributes.None, pt);
                        if (param.ReadOnlyByRef) pd.Attributes |= ParameterAttributes.In;
                        if (param.IsOut) pd.Attributes |= ParameterAttributes.Out;
                        ApplyDefaultValueFacts(pd, param);
                        innerMethod.Parameters.Add(pd);
                    }
                    innerMethod.Body.InitLocals = true;
                    HostClassFor(NsOf(func)).Methods.Add(innerMethod);
                    methodDefs.Add((func, innerMethod));

                    // Public spawn wrapper.
                    TypeReference wrapperReturn;
                    wrapperReturn = func.ReturnType is VoidType
                        ? types.Resolve(new ExternalType("Spawned"))
                        : types.Resolve(new ExternalType("Spawned", [func.ReturnType]));
                    var wrapperAttrs = (func.IsPublic ? MethodAttributes.Public : MethodAttributes.Assembly)
                        | MethodAttributes.Static | MethodAttributes.HideBySig;
                    var wrapper = new MethodDefinition(func.Name, wrapperAttrs, wrapperReturn);
                    foreach (var param in func.Parameters)
                    {
                        var pt = types.Resolve(param.Type);
                        if (param.ByRef || param.ReadOnlyByRef) pt = new Mono.Cecil.ByReferenceType(pt);
                        var pd = new ParameterDefinition(param.Name, ParameterAttributes.None, pt);
                        if (param.ReadOnlyByRef) pd.Attributes |= ParameterAttributes.In;
                        if (param.IsOut) pd.Attributes |= ParameterAttributes.Out;
                        ApplyDefaultValueFacts(pd, param);
                        wrapper.Parameters.Add(pd);
                    }
                    HostClassFor(NsOf(func)).Methods.Add(wrapper);
                    EmitTaskFuncWrapperBody(module, types, wrapper, innerMethod, innerReturnType,
                        func.ReturnType is VoidType, func.Parameters.Select(p => types.Resolve(p.Type)).ToList());
                    continue;
                }

                var funcMethodAttrs = (func.IsPublic ? MethodAttributes.Public : MethodAttributes.Assembly)
                    | MethodAttributes.Static;
                // Return-type resolution happens after generic parameters land on
                // the method so a `T` return resolves to the method's own
                // GenericParameter, not a placeholder `object`. Same for params.
                var method = new MethodDefinition(
                    func.Name,
                    funcMethodAttrs,
                    module.TypeSystem.Void); // patched below once T-context is set

                bool hasGenerics = func.TypeParameters.Count > 0;
                if (hasGenerics)
                {
                    foreach (var tp in func.TypeParameters)
                        method.GenericParameters.Add(new GenericParameter(tp, method));
                    ApplyGenericConstraints(method.GenericParameters, func.GenericConstraints, module);
                    types.PushGenericContext(method.GenericParameters);
                }

                method.ReturnType = types.Resolve(func.ReturnType);

                foreach (var param in func.Parameters)
                {
                    var paramType = types.Resolve(param.Type);
                    if (param.ByRef || param.ReadOnlyByRef) paramType = new Mono.Cecil.ByReferenceType(paramType);
                    var paramDef = new ParameterDefinition(param.Name, ParameterAttributes.None, paramType);
                    if (param.ReadOnlyByRef)
                        paramDef.Attributes |= ParameterAttributes.In;
                    if (param.IsOut) paramDef.Attributes |= ParameterAttributes.Out;
                    ApplyDefaultValueFacts(paramDef, param);
                    method.Parameters.Add(paramDef);
                }

                if (hasGenerics)
                    types.PopGenericContext();

                method.Body.InitLocals = true;
                if (func.Attributes.Count > 0)
                    EmitClrAttributes(module, types, method, func.Attributes);
                EmitAsyncStateMachineAttribute(module, types, method, func);
                HostClassFor(NsOf(func)).Methods.Add(method);
                methodDefs.Add((func, method));
                if (func.Symbol is { } freeSym) types.RegisterMethod(freeSym, method);
            }
            types.SetUnitImports(null); // restore the union for subsequent passes
        }

        // Pass 1b.1: emit each `static Foo { ... }` as a TOP-LEVEL static class
        // in the namespace (sibling to the module class, NOT nested inside it).
        // Members:
        //   let X = literal  → public const  (CLR literal field)
        //   let X = expr     → public static readonly
        //   var X = expr     → public static
        //   func bar(...)    → public static method
        var staticFuncDecls = allMembers.OfType<BoundStaticFuncDeclaration>().ToList();
        var staticFuncBodies = new List<(BoundFunctionDeclaration func, MethodDefinition method, TypeDefinition declaringType)>();
        foreach (var sfn in staticFuncDecls)
        {
            // `class Foo` plus `static Foo` is one CLR Foo: the latter
            // contributes static members to the former. Standalone static funcs
            // still emit their own abstract/sealed host class.
            var companion = allMembers.OfType<BoundDataDeclaration>().FirstOrDefault(d =>
                d.Name == sfn.Name && d.TypeParameters.Count == sfn.TypeParameters.Count && NsOf(d) == NsOf(sfn));
            TypeDefinition sfType;
            if (companion is not null)
            {
                sfType = types.Resolve(new DataType(companion.Name, companion.TypeParameters,
                    new Esharp.Syntax.DataDeclarationSyntax(false, companion.IsPublic, false,
                        companion.Name, [], [], [], []), companion.Classification)).Resolve()
                    ?? throw new InvalidOperationException($"IL: failed to resolve merged static-func host '{sfn.Name}'");
            }
            else
            {
                var sfAttrs = (sfn.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic)
                    | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
                sfType = new TypeDefinition(NsOf(sfn), sfn.Name, sfAttrs, module.ImportReference(typeof(object)));
                foreach (var tp in sfn.TypeParameters)
                    sfType.GenericParameters.Add(new GenericParameter(tp, sfType));
                module.Types.Add(sfType);
            }

            if (sfType.GenericParameters.Count > 0)
                types.PushGenericContext(sfType.GenericParameters);

            // Emit fields. Const for `let X = <constant>`, plain static for `var X`,
            // static readonly for `let X` initialized by a non-foldable expression
            // (initialization handled by a synthesized `.cctor`).
            var nonConstInits = new List<(FieldDefinition field, BoundExpression init)>();
            foreach (var f in sfn.Fields)
            {
                var fieldType = types.Resolve(f.Type);
                var fieldAttrs = FieldAttributes.Public | FieldAttributes.Static;
                if (!f.Mutable)
                    fieldAttrs |= FieldAttributes.InitOnly;
                var fdef = new FieldDefinition(f.Name, fieldAttrs, fieldType);

                object? constValue = ExtractConstantValue(f.DefaultValue);
                if (!f.Mutable && constValue is not null)
                {
                    fdef.Attributes = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;
                    fdef.Constant = constValue;
                }
                else if (f.DefaultValue is not null)
                {
                    nonConstInits.Add((fdef, f.DefaultValue));
                }
                sfType.Fields.Add(fdef);
            }

            // Emit method signatures so call resolution can find them in Pass 1c+.
            foreach (var fn in sfn.Functions)
            {
                var methodAttrs = (fn.IsPublic ? MethodAttributes.Public : MethodAttributes.Assembly)
                    | MethodAttributes.Static | MethodAttributes.HideBySig;
                if (fn.OperatorKind is not null) methodAttrs |= MethodAttributes.SpecialName;
                // Return type is resolved AFTER generic parameters land on the method,
                // so a `T` return/param binds to the method's own GenericParameter
                // rather than erasing to `object` (mirrors the module-class path).
                var methodName = fn.OperatorKind is { } operatorKind
                    ? Esharp.OperatorFacts.MetadataName(operatorKind, fn.Parameters.Count) ?? fn.Name
                    : fn.Name;
                var method = new MethodDefinition(methodName, methodAttrs, module.TypeSystem.Void);
                bool hasGenerics = fn.TypeParameters.Count > 0;
                if (hasGenerics)
                {
                    foreach (var tp in fn.TypeParameters)
                        method.GenericParameters.Add(new GenericParameter(tp, method));
                    ApplyGenericConstraints(method.GenericParameters, fn.GenericConstraints, module);
                    types.PushGenericContext(method.GenericParameters);
                }
                method.ReturnType = types.Resolve(fn.ReturnType);
                foreach (var param in fn.Parameters)
                {
                    var paramType = types.Resolve(param.Type);
                    if (param.ByRef || param.ReadOnlyByRef) paramType = new Mono.Cecil.ByReferenceType(paramType);
                    var pdef = new ParameterDefinition(param.Name, ParameterAttributes.None, paramType);
                    if (param.ReadOnlyByRef) pdef.Attributes |= ParameterAttributes.In;
                    if (param.IsOut) pdef.Attributes |= ParameterAttributes.Out;
                    ApplyDefaultValueFacts(pdef, param);
                    method.Parameters.Add(pdef);
                }
                if (hasGenerics) types.PopGenericContext();
                method.Body.InitLocals = true;
                if (fn.Attributes.Count > 0) EmitClrAttributes(module, types, method, fn.Attributes);
                EmitAsyncStateMachineAttribute(module, types, method, fn);
                sfType.Methods.Add(method);
                staticFuncBodies.Add((fn, method, sfType));
                if (fn.Symbol is { } sfSym) types.RegisterMethod(sfSym, method);
            }

            // The static `.cctor` is emitted AFTER the facet's method definitions land on
            // the type, so a field initializer that calls a sibling facet function
            // (`let Table = buildTable()`) resolves that call to a real MethodDefinition.
            // Emitted before the methods, its FindMethod index would miss them.
            if (nonConstInits.Count > 0)
            {
                var cctor = new MethodDefinition(
                    ".cctor",
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
                        | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    module.ImportReference(typeof(void)));
                cctor.Body.InitLocals = true;
                // Emit each initializer through the full expression emitter so a
                // `static readonly` field can be any runtime value — an external
                // construction (`Dictionary<…>(0)`), a call, etc. — not just a literal.
                var cctorEmitter = new MethodBodyEmitter(cctor, types, diagnostics, new Dictionary<string, FieldDefinition>(StringComparer.Ordinal));
                var cil = new ILBuilder(cctor);
                foreach (var (fdef, init) in nonConstInits)
                {
                    cctorEmitter.EmitFieldInitializer(fdef, init);
                }
                cil.Return();
                sfType.Methods.Add(cctor);
            }
            if (sfType.GenericParameters.Count > 0)
                types.PopGenericContext();
        }

        // Receiver-attached static methods are declared outside the facet body,
        // but emit directly onto the already-declared `static Foo` CLR type. Their
        // synthetic source receiver is an alias for the static surface and is not
        // a CLR parameter.
        foreach (var fn in staticFacetFunctions)
        {
            var receiver = fn.Parameters.FirstOrDefault();
            var ownerInfo = receiver.Type switch
            {
                StaticFuncType sf => (Name: sf.Name, Arity: sf.TypeParameters?.Count ?? sf.TypeArgs.Count),
                _ => ((string Name, int Arity)?)null,
            };
            if (ownerInfo is null) continue;
            var ownerName = ownerInfo.Value.Name;
            var metadataOwnerName = MetadataTypeName(ownerName, ownerInfo.Value.Arity);
            var owner = module.Types.FirstOrDefault(t => t.Namespace == NsOf(fn) && t.Name == metadataOwnerName);
            if (owner is null)
            {
                diagnostics.Report("", 0, 0,
                    $"IL: static receiver method '{fn.Name}' has no emitted static facet '{ownerName}'.");
                continue;
            }
            types.SetUnitImports(ImportsFor(fn));
            var attrs = (fn.IsPublic ? MethodAttributes.Public : MethodAttributes.Assembly)
                | MethodAttributes.Static | MethodAttributes.HideBySig;
            if (fn.OperatorKind is not null) attrs |= MethodAttributes.SpecialName;
            var operatorArity = fn.Parameters.Count - 1;
            var methodName = fn.OperatorKind is { } operatorKind
                ? Esharp.OperatorFacts.MetadataName(operatorKind, operatorArity) ?? fn.Name
                : fn.Name;
            var method = new MethodDefinition(methodName, attrs, module.TypeSystem.Void);
            if (owner.GenericParameters.Count > 0)
                types.PushGenericContext(owner.GenericParameters);
            if (fn.TypeParameters.Count > 0)
            {
                foreach (var tp in fn.TypeParameters)
                    method.GenericParameters.Add(new GenericParameter(tp, method));
                ApplyGenericConstraints(method.GenericParameters, fn.GenericConstraints, module);
                types.PushGenericContext(method.GenericParameters);
            }
            method.ReturnType = types.Resolve(fn.ReturnType);
            foreach (var param in fn.Parameters.Skip(1))
            {
                var type = types.Resolve(param.Type);
                if (param.ByRef || param.ReadOnlyByRef) type = new Mono.Cecil.ByReferenceType(type);
                var def = new ParameterDefinition(param.Name, ParameterAttributes.None, type);
                if (param.ReadOnlyByRef) def.Attributes |= ParameterAttributes.In;
                if (param.IsOut) def.Attributes |= ParameterAttributes.Out;
                ApplyDefaultValueFacts(def, param);
                method.Parameters.Add(def);
            }
            if (fn.TypeParameters.Count > 0) types.PopGenericContext();
            if (owner.GenericParameters.Count > 0) types.PopGenericContext();
            method.Body.InitLocals = true;
            owner.Methods.Add(method);
            staticFuncBodies.Add((fn, method, owner));
            if (fn.Symbol is { } symbol) types.RegisterMethod(symbol, method);
        }

        // Pass 1c: declare instance-method signatures on EVERY data type (static methods
        // already registered), collecting bodies for pass 1c'. Bodies are emitted only
        // after all signatures — across all types and files — are on their typeDefs, so a
        // method body can call a value-receiver method promoted onto another type
        // (`item.summary()` where `summary` lives in a different file's `data WorkItem`)
        // regardless of which type/file the emitter visits first.
        var allPendingBodies = new List<(BoundFunctionDeclaration Im, MethodDefinition Method, TypeDefinition TypeDef)>();
        foreach (var member in allMembers)
        {
            // Enter for any type with instance methods OR stored/computed properties OR a
            // declared interface — properties need EmitPropertyDefinitions (synthesized
            // get_/set_ accessors and interface-slot wiring), even when the type declares
            // no ordinary user methods.
            if (member is BoundDataDeclaration data && (data.InstanceMethods.Count > 0 || data.Fields.Any(f => f.IsProperty) || ConformanceEntries(data).Count > 0))
            {
                var typeDef = types.Resolve(new DataType(data.Name, data.TypeParameters,
                    new Esharp.Syntax.DataDeclarationSyntax(false, false, false, data.Name, [], [], [], []),
                    data.Classification)).Resolve();
                if (typeDef is null)
                {
                    diagnostics.Report("", 0, 0, $"IL: failed to resolve type definition for '{data.Name}'");
                    continue;
                }

                // Map from method name → interface method reference(s) implemented by this data type.
                // Used to emit explicit MethodImpl overrides so the CLR wires virtual dispatch
                // correctly — both for E# interfaces (registered MethodDefinitions in this module)
                // and for C# interfaces (synthesized MethodReferences scoped to the C# half so the
                // method-impl token survives the fusion step).
                // A generic data type's method signatures/bodies reference the type's own
                // parameters (`func get() -> T`). Push the type's generic context so `T`
                // resolves to the type's GenericParameter, not the `object` fallback.
                var pushedTypeGenericContext = false;
                if (typeDef.HasGenericParameters)
                {
                    types.PushGenericContext(typeDef.GenericParameters);
                    pushedTypeGenericContext = true;
                }

                var ifaceMethodsByName = new Dictionary<string, List<MethodReference>>(StringComparer.Ordinal);
                foreach (var ifaceBound in ConformanceEntries(data))
                {
                    // For a generic interface (`IBox<T>`), the registry is keyed by the bare
                    // name (`IBox`); the override targets must be hosted on the CLOSED
                    // self-instantiation (`IBox<!0>::get`) so the CLR wires dispatch.
                    var (baseName, isGenericIface) = ifaceBound switch
                    {
                        InterfaceType it => (it.Name, it.TypeArgs.Count > 0),
                        ExternalType ex => (ex.Name, ex.TypeArgs.Count > 0),
                        _ => (ifaceBound.EmitName, false),
                    };
                    var ifaceType = types.TryResolveRegistered(baseName);
                    if (ifaceType is not null)
                    {
                        var closed = isGenericIface
                            ? types.ResolveConformance(ifaceBound) as GenericInstanceType
                            : null;
                        foreach (var m in ifaceType.Methods)
                        {
                            MethodReference mref = m;
                            if (closed is not null)
                            {
                                var rehomed = new MethodReference(m.Name, m.ReturnType, closed) { HasThis = m.HasThis };
                                foreach (var p in m.Parameters)
                                    rehomed.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                                mref = rehomed;
                            }
                            if (!ifaceMethodsByName.TryGetValue(m.Name, out var list))
                            {
                                list = new List<MethodReference>();
                                ifaceMethodsByName[m.Name] = list;
                            }
                            list.Add(mref);
                        }
                        continue;
                    }

                    // Cross-language: C#-defined interface listed on an E# data type. The
                    // declaration is in the C# half assembly — we look up the handle and
                    // synthesize MethodReferences scoped to that half's assembly identity.
                    // ILRepack rewrites the scope on these refs during fusion.
                    if (types.TryGetExternalCSharpHandle(baseName) is { } csHandle)
                    {
                        var declaringRef = types.Resolve(new ExternalCSharpType(csHandle));
                        foreach (var csMember in csHandle.Members)
                        {
                            if (csMember.Kind != CSharpMemberKind.Method) continue;
                            var returnRef = types.Resolve(csMember.ReturnType);
                            var methodRef = new MethodReference(csMember.Name, returnRef, declaringRef)
                            {
                                HasThis = !csMember.IsStatic,
                            };
                            foreach (var p in csMember.Parameters)
                                methodRef.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, types.Resolve(p.Type)));
                            if (!ifaceMethodsByName.TryGetValue(csMember.Name, out var list))
                            {
                                list = new List<MethodReference>();
                                ifaceMethodsByName[csMember.Name] = list;
                            }
                            list.Add(methodRef);
                        }
                        continue;
                    }

                    // BCL interface (IEnumerable<T>, IComparable<T>, IDisposable,
                    // IAsyncDisposable). Resolve the closed interface and pull its slot
                    // methods so the matching type method is wired virtual/newslot + an
                    // Overrides — without this the type carries the InterfaceImplementation
                    // but no impl, and the CLR rejects it at load ("does not have an
                    // implementation"). For a generic interface the slots are re-homed onto
                    // the closed instance (`IEnumerable`1<!0>::GetEnumerator`) so generic
                    // substitution lines up with the type's own GetEnumerator.
                    var bclRef = types.ResolveConformance(ifaceBound);
                    if (bclRef?.Resolve() is { } bclDef)
                    {
                        var bclClosed = bclRef as GenericInstanceType;
                        foreach (var m in bclDef.Methods)
                        {
                            if (!m.IsVirtual && !m.IsAbstract) continue; // only real slots
                            MethodReference mref;
                            if (bclClosed is not null)
                            {
                                // Import the open slot first (Cecil uses the method's own
                                // generic context, so `!0` in the signature resolves), then
                                // re-home onto the closed instance — the imported signature
                                // types stay valid because `!0` binds via the new declaring
                                // instance. Importing the bare `!0` directly would NRE
                                // (no context), so import-then-rehome, not rehome-then-import.
                                var imp = module.ImportReference(m);
                                var rehomed = new MethodReference(imp.Name, imp.ReturnType, bclClosed) { HasThis = imp.HasThis };
                                foreach (var p in imp.Parameters)
                                    rehomed.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                                mref = rehomed;
                            }
                            else mref = module.ImportReference(m);
                            if (!ifaceMethodsByName.TryGetValue(m.Name, out var list))
                            {
                                list = new List<MethodReference>();
                                ifaceMethodsByName[m.Name] = list;
                            }
                            list.Add(mref);
                        }
                    }
                }

                foreach (var im in data.InstanceMethods)
                {
                    types.SetUnitImports(ImportsFor(im)); // scope signature + body to this method's file
                    // PassThrough: method's contract is inherited from the base
                    // class as-is — don't emit a new MethodDef on the subclass.
                    if (im.InheritanceRole == InheritanceRole.PassThrough)
                        continue;

                    // Explicit interface member (`func IFace.method(...)`): emit a private,
                    // final, virtual, newslot method named `IFace.method` with a single
                    // MethodImpl onto that one interface's slot — the C# explicit-impl
                    // pattern. Lets two same-named methods (Chan's dual GetEnumerator) fill
                    // distinct interface slots. Resolve the override target up-front (closed
                    // generic interface re-homed onto its instance).
                    var isExplicit = im.ExplicitInterface is not null;
                    MethodReference? explicitOverride = null;
                    if (isExplicit)
                    {
                        var explicitBound = im.ExplicitInterfaceType
                            ?? throw new InvalidOperationException(
                                $"'{im.Name}': ExplicitInterface '{im.ExplicitInterface}' bound without a structured type.");
                        var ifaceRefE = types.ResolveConformance(explicitBound);
                        var baseNameE = explicitBound switch
                        {
                            Esharp.BoundTree.InterfaceType it => it.Name,
                            ExternalType ex => ex.Name,
                            _ => im.ExplicitInterface!,
                        };
                        var ifaceDefE = types.TryResolveRegistered(baseNameE) ?? ifaceRefE?.Resolve();
                        var slot = ifaceDefE?.Methods.FirstOrDefault(
                            m => m.Name == im.Name && m.Parameters.Count == im.Parameters.Count - 1);
                        if (slot is not null)
                        {
                            if (ifaceRefE is GenericInstanceType gitE)
                            {
                                var rh = new MethodReference(slot.Name, slot.ReturnType, gitE) { HasThis = slot.HasThis };
                                foreach (var p in slot.Parameters)
                                    rh.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                                explicitOverride = module.ImportReference(rh);
                            }
                            else explicitOverride = module.ImportReference(slot); // BCL slot — import into this module
                        }
                    }

                    var methodAttrs = isExplicit
                        ? MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual
                          | MethodAttributes.NewSlot | MethodAttributes.HideBySig
                        : MethodAttributes.Public | MethodAttributes.HideBySig;
                    var isInterfaceImpl = !isExplicit && ifaceMethodsByName.ContainsKey(im.Name);
                    if (isInterfaceImpl)
                    {
                        methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
                        // A value type is sealed: a non-final virtual method on a struct
                        // fails to load ("does not have an implementation"). C# marks every
                        // implicit interface implementation `virtual final`; for a struct it
                        // is mandatory. (A class method declared overridable picks up its
                        // virtual/abstract role below and is intentionally not finalized here.)
                        if (typeDef.IsValueType
                            && im.InheritanceRole is not (InheritanceRole.Virtual or InheritanceRole.Abstract))
                            methodAttrs |= MethodAttributes.Final;
                    }
                    if (!isExplicit)
                        switch (im.InheritanceRole)
                        {
                            case InheritanceRole.Virtual:
                                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
                                break;
                            case InheritanceRole.Abstract:
                                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Abstract;
                                break;
                            case InheritanceRole.Fulfill:
                            case InheritanceRole.Override:
                                // ReuseSlot — child rebinds the parent's vtable slot.
                                methodAttrs |= MethodAttributes.Virtual; // ReuseSlot is the default when NewSlot isn't set
                                break;
                            case InheritanceRole.ReAbstract:
                                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.Abstract;
                                break;
                        }
                    var method = new MethodDefinition(
                        isExplicit ? $"{im.ExplicitInterface}.{im.Name}" : im.Name,
                        methodAttrs,
                        module.TypeSystem.Void); // return type patched once the method's generic context is set

                    // A promoted combinator can declare type parameters BEYOND the receiver
                    // type's — `func Map<TValue, TError, TNew>(r: Result<TValue, TError>, …)`
                    // becomes the instance method `Result`2::Map<TNew>`. The parameters that
                    // name the receiver's own type arguments are the type's (already pushed,
                    // matched by name); the remainder are method-level. Declare and push them
                    // so the return type and parameter types resolve to the method's
                    // GenericParameter rather than the `object` fallback.
                    var methodTypeParams = im.TypeParameters
                        .Where(tp => !data.TypeParameters.Contains(tp, StringComparer.Ordinal))
                        .ToList();
                    var pushedMethodGenericContext = false;
                    if (methodTypeParams.Count > 0)
                    {
                        foreach (var tp in methodTypeParams)
                            method.GenericParameters.Add(new GenericParameter(tp, method));
                        // Constraints align with the method's own params — apply only when
                        // none were filtered out to the declaring type (the common case).
                        if (methodTypeParams.Count == im.TypeParameters.Count)
                            ApplyGenericConstraints(method.GenericParameters, im.GenericConstraints, module);
                        types.PushGenericContext(method.GenericParameters);
                        pushedMethodGenericContext = true;
                    }

                    method.ReturnType = types.Resolve(im.ReturnType);

                    foreach (var param in im.Parameters.Skip(1))
                    {
                        var paramType = types.Resolve(param.Type);
                        if (param.ByRef || param.ReadOnlyByRef) paramType = new Mono.Cecil.ByReferenceType(paramType);
                        var paramDef = new ParameterDefinition(param.Name, ParameterAttributes.None, paramType);
                        if (param.ReadOnlyByRef)
                        {
                            paramDef.Attributes |= ParameterAttributes.In;
                            paramDef.CustomAttributes.Add(new CustomAttribute(
                                module.ImportReference(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute).GetConstructor(Type.EmptyTypes)!)));
                        }
                        if (param.IsOut) paramDef.Attributes |= ParameterAttributes.Out;
                        ApplyDefaultValueFacts(paramDef, param);
                        method.Parameters.Add(paramDef);
                    }

                    if (pushedMethodGenericContext)
                        types.PopGenericContext();

                    typeDef.Methods.Add(method);

                    // A `readonly func (c: T) m()` receiver borrows `in this`: on a struct,
                    // mark the method `[IsReadOnly]` so the CLR (and C# consumers) treat
                    // `this` as a read-only ref — no defensive copy, no field mutation. The
                    // no-write guarantee itself is enforced at bind time. (No-op on a class:
                    // a class never copies; the readonly contract is purely bind-enforced.)
                    if (im.ReceiverKind == Esharp.Symbols.ReceiverKind.ReadonlyValue && typeDef.IsValueType)
                        method.CustomAttributes.Add(new CustomAttribute(
                            module.ImportReference(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute).GetConstructor(Type.EmptyTypes)!)));

                    // Method-level CLR attributes (`[Fact]`, `[Obsolete]`, …) pass
                    // through onto the MethodDef — same rule as type-level attributes.
                    if (im.Attributes.Count > 0)
                        EmitClrAttributes(module, types, method, im.Attributes);
                    EmitAsyncStateMachineAttribute(module, types, method, im);

                    // Wire explicit interface override so CLR dispatch finds this method
                    // from any matching interface slot (handles name collisions across interfaces).
                    if (isExplicit)
                    {
                        if (explicitOverride is not null)
                            method.Overrides.Add(explicitOverride);
                        else
                            diagnostics.Report("", 0, 0,
                                $"IL: explicit interface member '{im.ExplicitInterface}.{im.Name}' found no matching slot on '{im.ExplicitInterface}'");
                    }
                    else if (isInterfaceImpl)
                    {
                        foreach (var ifaceMethod in ifaceMethodsByName[im.Name])
                        {
                            // Match by arity AND return-type compatibility: a generic
                            // `GetEnumerator() -> IEnumerator<T>` claims only the generic
                            // `IEnumerable<T>` slot, not the non-generic `IEnumerable`
                            // slot (whose `IEnumerator` return is left to an explicit member).
                            if (ifaceMethod.Parameters.Count == method.Parameters.Count
                                && ReturnTypesCompatible(ifaceMethod.ReturnType, method.ReturnType))
                                method.Overrides.Add(ifaceMethod);
                        }
                    }

                    // abstract method has no body — Cecil leaves Body as null and
                    // the IL is omitted from the assembly.
                    if (im.InheritanceRole is InheritanceRole.Abstract or InheritanceRole.ReAbstract)
                        continue;

                    method.Body.InitLocals = true;
                    allPendingBodies.Add((im, method, typeDef)); // body emitted in pass 1c' below
                }

                // Synthesize forwarders for transitive non-generic base interfaces
                // (e.g. the non-generic IEnumerable behind a declared IEnumerable<T>) so
                // the type is loadable even when the user lists only the generic interface.
                EmitTransitiveInterfaceBridges(module, types, typeDef, data);

                // Attach PropertyDefinitions over the synthesized get_/set_ accessors
                // (the accessor methods were emitted by the instance-method pass above),
                // and synthesize accessors for a plain field that satisfies an interface
                // property requirement — both wired into the interface slot via ifaceMethodsByName.
                EmitPropertyDefinitions(module, typeDef, data, ifaceMethodsByName);

                if (pushedTypeGenericContext) types.PopGenericContext();
            }
        }

        // Constructor bodies, deferred from the Pass 1b populate sweep: an init body
        // (or a field default's initializer) may construct ANY declared type — forward
        // references included — so it emits only now, when every type's .ctor and every
        // method signature exists. Same split as instance methods (Pass 1c / 1c').
        foreach (var (ctorData, emitCtorBodies) in deferredCtorBodies)
        {
            types.SetUnitImports(ImportsFor(ctorData)); // scope body to this type's file
            emitCtorBodies();
        }
        types.SetUnitImports(null);

        // Pass 1c': emit every instance-method body now that all signatures (across all
        // types and files) are present. A generic type's body resolves its own `T` via
        // the type's generic context, re-pushed per body since pass 1c popped it.
        //
        // POST-LOWERING INVARIANT: im.HasAwait is ALWAYS false here. AsyncLowering (C1)
        // rewrites every async function body into a synchronous state-machine struct stub.
        // The generated state-machine's MoveNext method is a plain BoundFunctionDeclaration
        // with HasAwait == false. If HasAwait is true here, the lowering pipeline failed —
        // the assertion in Generate() would already have caught it, but be explicit.
        foreach (var (im, method, typeDef) in allPendingBodies)
        {
            types.SetUnitImports(ImportsFor(im)); // scope body to this method's file
            var pushedCtx = typeDef.HasGenericParameters;
            if (pushedCtx) types.PushGenericContext(typeDef.GenericParameters);
            // Signature emission pushed method-owned generic parameters long
            // enough to resolve the signature, then popped them. Re-push for the
            // body as well: otherwise `T` in a generic instance method body is
            // resolved as object (notably `Task.Run<T>` and Func<Task<T>>).
            var pushedMethodCtx = method.HasGenericParameters;
            if (pushedMethodCtx) types.PushGenericContext(method.GenericParameters);
            if (im.HasAwait)
                throw new FeatureNodeInCodeGenException(
                    $"HasAwait==true on instance method '{im.Name}' of '{typeDef.FullName}' reached CodeGen. " +
                    "AsyncLowering must have failed to convert this function body to a state machine.");
            EmitFunctionBody(module, types, diagnostics, method, im, structFieldMaps, isSelfMethod: true, documentCache: documentCache, optimization: optimization);
            if (pushedMethodCtx) types.PopGenericContext();
            if (pushedCtx) types.PopGenericContext();
        }

        // Pass 2: emit static function bodies (signatures already registered in Pass 1b)
        // Static-func bodies live in top-level static classes; emit regardless of moduleClass.
        foreach (var (sfFunc, sfMethod, sfDeclType) in staticFuncBodies)
        {
            types.SetUnitImports(ImportsFor(sfFunc)); // scope body to this func's file
            var pushedMethodCtx = sfMethod.HasGenericParameters;
            if (pushedMethodCtx) types.PushGenericContext(sfMethod.GenericParameters);
            if (sfFunc.HasAwait)
                throw new FeatureNodeInCodeGenException(
                    $"HasAwait==true on static-func '{sfFunc.Name}' in '{sfDeclType.FullName}' reached CodeGen. " +
                    "AsyncLowering must have failed.");
            EmitFunctionBody(module, types, diagnostics, sfMethod, sfFunc, structFieldMaps, documentCache: documentCache, optimization: optimization);
            if (pushedMethodCtx) types.PopGenericContext();
        }
        if (staticFunctions.Count > 0)
        {
            foreach (var (func, method) in methodDefs)
            {
                types.SetUnitImports(ImportsFor(func)); // scope body to this func's file
                if (func.HasAwait)
                    throw new FeatureNodeInCodeGenException(
                        $"HasAwait==true on free function '{func.Name}' reached CodeGen. " +
                        "AsyncLowering must have failed.");
                EmitFunctionBody(module, types, diagnostics, method, func, structFieldMaps, documentCache: documentCache, optimization: optimization);
            }
        }

        // Set the entry point for a CLI-shaped `main`. Console programs may
        // use either `main()` or `main(args: string[])`; the latter is the
        // CLR's normal command-line entry contract and receives only the
        // user arguments (never the executable path). Libraries keep `main`
        // as an ordinary callable method.
        // POST-LOWERING: async main is already lowered. The state-machine stub's
        // entry wrapper was synthesized by AsyncLowering with name "<Main>" — the
        // .esproj pipeline sets it as the entry point. If a bare "main" still
        // exists here it is a synchronous main (HasAwait already asserted false).
        if (outputKind == ILOutputKind.Console)
        {
            var mainFunc = methodDefs.FirstOrDefault(m =>
                m.func.Name == "main" && IsCliMainSignature(m.method));
            var staticMain = staticFuncBodies.FirstOrDefault(m =>
                m.func.Name == "main" && IsCliMainSignature(m.method));
            if (mainFunc.method is not null)
            {
                // Synchronous main — set as entry point directly.
                assembly.EntryPoint = mainFunc.method;
            }
            else if (staticMain.method is not null)
            {
                // A declared static facet is already a CLR static class. Its
                // `main` therefore needs no synthetic object wrapper.
                assembly.EntryPoint = staticMain.method;
            }
            else
            {
                // Class-style program: no top-level `func main`, but a `main` method on
                // a `class Program`. The program is an object instead of a bare
                // function — still entered through `main`. Synthesize a static `<Main>`
                // that constructs the Program (newobj) and calls its `main`, and make it
                // the entry point — so the E# author needs no hand-written launcher
                // (in E#, a `class` constructs as `Program()`, no `new`).
                var programType = module.Types.FirstOrDefault(t =>
                    (t.Name == "Program")
                    && t.Methods.Any(mm => mm.Name == "main" && !mm.IsStatic && IsCliMainSignature(mm))
                    && t.Methods.Any(mm => mm.IsConstructor && mm.Parameters.Count == 0))
                    ?? module.Types.FirstOrDefault(t =>
                    t.Methods.Any(mm => mm.Name == "main" && !mm.IsStatic && IsCliMainSignature(mm))
                    && t.Methods.Any(mm => mm.IsConstructor && mm.Parameters.Count == 0));

                    if (programType is not null)
                    {
                        var classMain = programType.Methods.First(mm =>
                            mm.Name == "main" && !mm.IsStatic && IsCliMainSignature(mm));
                        var ctor = programType.Methods.First(mm => mm.IsConstructor && mm.Parameters.Count == 0);
                        var takesArgs = classMain.Parameters.Count == 1;

                        var wrapper = new MethodDefinition(
                            "<Main>",
                            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                            classMain.ReturnType);
                        if (takesArgs)
                            wrapper.Parameters.Add(new ParameterDefinition(
                                "args", ParameterAttributes.None, new ArrayType(module.TypeSystem.String)));
                        wrapper.Body.InitLocals = true;
                        var il = new ILBuilder(wrapper);
                        il.NewObj(ctor); // new Program()
                        if (takesArgs)
                            il.LoadArg(wrapper.Parameters[0]);
                        il.CallVirt(classMain); // .main()

                        // Async class-main: unwrap the awaitable so the CLR entry is a plain
                        // synchronous shell (Program().main().GetAwaiter().GetResult()).
                        var retName = classMain.ReturnType.FullName;
                        if (retName.StartsWith("System.Threading.Tasks.ValueTask") || retName.StartsWith("System.Threading.Tasks.Task"))
                        {
                            var awaitableLocal = new VariableDefinition(classMain.ReturnType);
                            wrapper.Body.Variables.Add(awaitableLocal);
                            il.StoreLocal(awaitableLocal);
                            il.LoadLocalAddress(awaitableLocal);
                            var getAwaiter = classMain.ReturnType.Resolve().Methods.First(mm => mm.Name == "GetAwaiter" && mm.Parameters.Count == 0);
                            var getAwaiterRef = module.ImportReference(getAwaiter);
                            getAwaiterRef.DeclaringType = classMain.ReturnType;
                            il.Call(getAwaiterRef);
                            var awaiterType = getAwaiterRef.ReturnType;
                            // When the awaitable is a closed generic (`ValueTask<int>` / `Task<int>`),
                            // GetAwaiter returns a CLOSED awaiter (`ValueTaskAwaiter<int>`), but Cecil
                            // leaves the imported return type open (`!0`). Substitute the awaitable's
                            // type argument so the awaiter local is the concrete instance, not `!0`.
                            if (classMain.ReturnType is GenericInstanceType retGit
                                && awaiterType.Resolve() is { HasGenericParameters: true } awaiterDef)
                            {
                                var closedAwaiter = new GenericInstanceType(module.ImportReference(awaiterDef));
                                foreach (var ta in retGit.GenericArguments)
                                    closedAwaiter.GenericArguments.Add(ta);
                                awaiterType = module.ImportReference(closedAwaiter);
                            }
                            var awaiterLocal = new VariableDefinition(awaiterType);
                            wrapper.Body.Variables.Add(awaiterLocal);
                            il.StoreLocal(awaiterLocal);
                            il.LoadLocalAddress(awaiterLocal);
                            var getResult = awaiterType.Resolve().Methods.First(mm => mm.Name == "GetResult" && mm.Parameters.Count == 0);
                            var getResultRef = module.ImportReference(getResult);
                            getResultRef.DeclaringType = awaiterType;
                            il.Call(getResultRef);
                            // GetResult on a closed awaiter returns the concrete result (`int` for
                            // `ValueTask<int>`), but Cecil leaves the imported return open (`!0`). Use
                            // the awaitable's own type argument — or void for a bare Task/ValueTask —
                            // so the wrapper's signature matches the value actually returned.
                            wrapper.ReturnType = classMain.ReturnType is GenericInstanceType resultGit
                                ? resultGit.GenericArguments[0]
                                : module.ImportReference(typeof(void));
                        }

                        il.Return();
                        ILOptimizer.ShortenOpcodes(wrapper.Body);
                        programType.Methods.Add(wrapper);
                        assembly.EntryPoint = wrapper;
                    }
                }
        }

        // Computed getters and custom setters use the ordinary bound-expression
        // emitter after all callable signatures exist.
        foreach (var (state, method, backing) in namespaceAccessorBodies)
        {
            types.SetUnitImports(ImportsFor(state));
            if (method.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                var body = new BoundFunctionDeclaration(false, method.Name, [], [], state.Field.Type,
                    new BoundBlockStatement([new BoundReturnStatement(state.ComputedGetter)]), []);
                EmitFunctionBody(module, types, diagnostics, method, body, structFieldMaps,
                    documentCache: documentCache);
            }
            else
            {
                if (state.Field.IsComputedProperty)
                {
                    var parameter = new BoundParameter(state.SetterParam ?? "value", state.Field.Type,
                        ByRef: false, ReadOnlyByRef: false, IsOut: false, DefaultValue: null);
                    var body = new BoundFunctionDeclaration(false, method.Name, [], [parameter],
                        new PrimitiveType("void"),
                        new BoundBlockStatement([new BoundExpressionStatement(state.SetterBody!)]), []);
                    EmitFunctionBody(module, types, diagnostics, method, body, structFieldMaps,
                        documentCache: documentCache);
                }
                else
                {
                    var emitter = new MethodBodyEmitter(method, types, diagnostics);
                    emitter.EmitFieldInitializer(backing!, state.SetterBody!);
                    new ILBuilder(method).Return();
                }
            }
        }

        // Emit one host .cctor per namespace. State assignments are deliberately
        // first, followed by the explicit namespace init body.
        var initializedNamespaces = namespaceStates.Where(s => s.Field.DefaultValue is not null).Select(NsOf)
            .Concat(namespaceInitializers.Select(NsOf))
            .Concat(namespaceConsts.Where(k => (k.Value as BoundLiteralExpression)?.Value is decimal).Select(NsOf))
            .Distinct(StringComparer.Ordinal);
        foreach (var namespaceName in initializedNamespaces)
        {
            var host = HostClassFor(namespaceName);
            if (host.Methods.Any(m => m.IsConstructor && m.IsStatic))
            {
                diagnostics.Report("", 0, 0,
                    $"IL: namespace host '{host.FullName}' already has a static initializer");
                continue;
            }

            var cctor = new MethodDefinition(
                ".cctor",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            cctor.Body.InitLocals = true;
            host.Methods.Add(cctor);

            var statements = new List<BoundStatement>();
            foreach (var state in namespaceStates.Where(s => NsOf(s) == namespaceName
                && s.Field.DefaultValue is not null))
            {
                statements.Add(new BoundAssignment(
                    new BoundNameExpression(state.Field.Name, state.Field.Type), state.Field.DefaultValue!));
            }
            foreach (var constant in namespaceConsts.Where(k => NsOf(k) == namespaceName
                         && (k.Value as BoundLiteralExpression)?.Value is decimal))
            {
                statements.Add(new BoundAssignment(
                    new BoundNameExpression(constant.Name, constant.Type), constant.Value));
            }
            var init = namespaceInitializers.SingleOrDefault(i => NsOf(i) == namespaceName);
            if (init is not null) statements.AddRange(init.Body.Statements);

            // State may be contributed by several files with independent imports.
            // Expressions are already bound; use the compilation import union here.
            types.SetUnitImports(null);
            var body = new BoundFunctionDeclaration(
                IsPublic: false,
                Name: ".cctor",
                TypeParameters: [],
                Parameters: [],
                ReturnType: new VoidType(),
                Body: new BoundBlockStatement(statements),
                Attributes: []);
            EmitFunctionBody(module, types, diagnostics, cctor, body, structFieldMaps,
                documentCache: documentCache);
        }

        types.SetUnitImports(null); // restore the union before scope-independent passes

        // State machines use top-level shells while their members and references are
        // emitted; now move each shell under the method's actual CLR owner.
        ReparentAsyncStateMachines(module,
            methodDefs.Select(m => (m.func, m.method))
                .Concat(staticFuncBodies.Select(m => (m.func, m.method)))
                .Concat(allPendingBodies.Select(m => (m.Im, m.Method))),
            diagnostics);

        // Pass 3: wire *T interface satisfaction on wrapper classes.
        // Must happen after all static methods are emitted so delegate methods
        // can reference them.
        foreach (var member in allMembers)
        {
            if (member is BoundDataDeclaration data && data.PointerInterfaceTypes is { Count: > 0 })
            {
                var innerTypeRef = types.TryResolveRegistered(data.Name);
                if (innerTypeRef is null) continue;
                foreach (var proto in data.PointerInterfaceTypes)
                    ILHeapPointer.WireProtocol(module, types, innerTypeRef, proto, HostClassFor(NsOf(data)));
            }
        }

        // Nested types: every type was emitted as a top-level module type so its members
        // resolved by name; now move each whose bound decl carries a DeclaringTypeKey into
        // its enclosing type's Cecil NestedTypes (with nested visibility, empty namespace).
        ReparentNestedTypes(module, allMembers, diagnostics);

        // The compiler's own metadata verification — the layer beyond ILVerify. Catches
        // type-load / JIT-time defects in the metadata just emitted (a declared interface
        // with no loadable implementation, a generic instantiation that breaks a
        // constraint) that ILVerify's IL-type-safety pass does not see, turning a mystery
        // runtime TypeLoadException/VerificationException into a located compile diagnostic.
        MetadataVerifier.Verify(module, diagnostics);

        return (assembly, diagnostics.Diagnostics);
    }

    static void EmitDebuggableAttribute(AssemblyDefinition assembly, ModuleDefinition module,
        OptimizationLevel optimization)
    {
        // Keep Debug on the compiler's established runtime policy. In particular, even
        // DebuggingModes.Default changes JIT tracking behaviour and can perturb timing-
        // sensitive channel/select code. Portable PDBs provide Debug sequence points;
        // Release is the only mode that needs an explicit JIT-facing policy here.
        if (optimization == OptimizationLevel.Debug)
            return;

        var ctor = typeof(System.Diagnostics.DebuggableAttribute).GetConstructor(
            [typeof(System.Diagnostics.DebuggableAttribute.DebuggingModes)])!;
        var attribute = new CustomAttribute(module.ImportReference(ctor));
        var modes = System.Diagnostics.DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints;
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(
            module.ImportReference(typeof(System.Diagnostics.DebuggableAttribute.DebuggingModes)),
            (int)modes));
        assembly.CustomAttributes.Add(attribute);
    }

    // The CLR accepts exactly these entry signatures: parameterless, or one
    // `string[]` parameter. Keep the check on emitted signatures, rather than
    // syntax, so imported aliases and every binding path agree on the same ABI.
    static bool IsCliMainSignature(MethodDefinition method) =>
        method.Parameters.Count == 0
        || (method.Parameters.Count == 1
            && method.Parameters[0].ParameterType is ArrayType { ElementType.FullName: "System.String" });

    // EmitToFile: convenience wrappers for tests and CLI that write a .dll directly.
    // These bypass AssertCoreOnly by design — test drivers that call EmitToFile are
    // unit-testing lowering passes that may intentionally pass a partially-lowered tree.
    // Production pipelines must go through Generate(BoundProgram).
    public static IReadOnlyList<Diagnostic> EmitToFile(BoundCompilationUnit unit, string assemblyName, string outputPath, bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null, bool verify = false, bool implicitUsings = true)
        => EmitToFile([unit], assemblyName, outputPath, debugSymbols, referencePaths, verify, implicitUsings);

    public static IReadOnlyList<Diagnostic> EmitToFile(IReadOnlyList<BoundCompilationUnit> units, string assemblyName, string outputPath, bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null, bool verify = false, bool implicitUsings = true)
    {
        var (assembly, diagnostics) = Emit(units, assemblyName, debugSymbols, referencePaths,
            internalsVisibleTo: null, externalSymbols: null,
            outputKind: ILOutputKind.Library, implicitUsings: implicitUsings);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputLock = OutputLocks.GetOrAdd(fullOutputPath, static _ => new object());
        lock (outputLock)
        {
            using (assembly)
            {
                if (debugSymbols)
                    ILPdbWriter.WriteWithPdb(assembly, fullOutputPath);
                else
                    ILPdbWriter.WriteWithoutPdb(assembly, fullOutputPath);
            }

            // Verification is part of the same output transaction. Do not release the
            // path until the exact bytes just written have been inspected.
            if (verify && !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                var fatal = IlVerification.VerifyFatal(fullOutputPath, referencePaths is null ? null : referencePaths.Select(Path.GetDirectoryName).Where(d => d is not null)!);
                if (fatal.Count > 0)
                {
                    var combined = new List<Diagnostic>(diagnostics);
                    foreach (var f in fatal)
                        combined.Add(new Diagnostic("", 0, 0, DiagnosticSeverity.Error, "ES0900",
                            $"emitted IL failed verification at {f.Method}: {f.Code} — {f.Message}"));
                    return combined;
                }
            }
            return diagnostics;
        }
    }

    // Build MethodReference for EqualityComparer<fieldType>.get_Default().
    static void EmitTaskFuncWrapperBody(
        ModuleDefinition module,
        ILTypeResolver types,
        MethodDefinition wrapper,
        MethodDefinition inner,
        TypeReference innerReturnType,
        bool isVoid,
        IReadOnlyList<TypeReference> parameterTypes)
    {
        // The wrapper IL: `return SpawnedOps.Spawn(new Action(__inner))` or
        // `return SpawnedOps.Spawn<T>(new Func<T>(__inner))`. The delegate is constructed
        // with a null target (static method) + the inner method pointer.
        wrapper.Body.InitLocals = true;
        var il = new ILBuilder(wrapper);
        il.LoadNull();
        il.LoadFunctionPointer(inner);

        if (parameterTypes.Count > 2)
            throw new NotSupportedException("task func parameters currently support up to two arguments.");

        if (isVoid)
        {
            var actionType = parameterTypes.Count switch
            {
                0 => module.ImportReference(typeof(Action)),
                1 => CloseDelegate(module, typeof(Action<>), parameterTypes),
                2 => CloseDelegate(module, typeof(Action<,>), parameterTypes),
                _ => throw new InvalidOperationException("unsupported task-function action arity"),
            };
            il.NewObj(DelegateCtor(module, actionType));
            var spawnedOps = types.Resolve(new ExternalType("SpawnedOps")).Resolve()
                ?? throw new InvalidOperationException("Esharp.Stdlib.SpawnedOps must be available for task func lowering.");
            var spawnAction = module.ImportReference(spawnedOps.Methods.Single(m =>
                m.Name == "Spawn" && m.GenericParameters.Count == parameterTypes.Count && m.Parameters.Count == parameterTypes.Count + 1));
            if (parameterTypes.Count == 0)
                il.Call(spawnAction);
            else
            {
                var spawnInst = new GenericInstanceMethod(spawnAction);
                foreach (var parameterType in parameterTypes) spawnInst.GenericArguments.Add(parameterType);
                foreach (var parameter in wrapper.Parameters) il.LoadArg(parameter);
                il.Call(spawnInst);
            }
        }
        else
        {
            var funcOpen = parameterTypes.Count switch
            {
                0 => typeof(Func<>),
                1 => typeof(Func<,>),
                2 => typeof(Func<,,>),
                _ => throw new InvalidOperationException("unsupported task-function function arity"),
            };
            var funcClosed = CloseDelegate(module, funcOpen, [.. parameterTypes, innerReturnType]);
            il.NewObj(DelegateCtor(module, funcClosed));

            var spawnedOps = types.Resolve(new ExternalType("SpawnedOps")).Resolve()
                ?? throw new InvalidOperationException("Esharp.Stdlib.SpawnedOps must be available for task func lowering.");
            var spawnRef = module.ImportReference(spawnedOps.Methods.Single(m =>
                m.Name == "Spawn" && m.GenericParameters.Count == parameterTypes.Count + 1 && m.Parameters.Count == parameterTypes.Count + 1));
            var spawnInst = new GenericInstanceMethod(spawnRef);
            foreach (var parameterType in parameterTypes) spawnInst.GenericArguments.Add(parameterType);
            spawnInst.GenericArguments.Add(innerReturnType);
            foreach (var parameter in wrapper.Parameters) il.LoadArg(parameter);
            il.Call(spawnInst);
        }

        il.Return();
        ILOptimizer.ShortenOpcodes(wrapper.Body);
    }

    static GenericInstanceType CloseDelegate(ModuleDefinition module, Type openDelegate, IReadOnlyList<TypeReference> arguments)
    {
        var closed = new GenericInstanceType(module.ImportReference(openDelegate));
        foreach (var argument in arguments) closed.GenericArguments.Add(argument);
        return closed;
    }

    static MethodReference DelegateCtor(ModuleDefinition module, TypeReference delegateType)
    {
        var ctor = new MethodReference(".ctor", module.ImportReference(typeof(void)), delegateType)
        {
            HasThis = true,
            ExplicitThis = false,
        };
        ctor.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(object))));
        ctor.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(IntPtr))));
        return ctor;
    }

    // Constant folding for static-func `let` field initializers. Returns null if
    // the expression isn't reducible to a single CLR-literal-compatible value.
    static object? ExtractConstantValue(BoundExpression? expr)
    {
        switch (expr)
        {
            case BoundLiteralExpression lit when lit.Value is not null:
                return lit.Value;
            case BoundUnaryExpression u when u.Op == Esharp.Syntax.SyntaxTokenKind.Minus:
                var inner = ExtractConstantValue(u.Operand);
                return inner switch
                {
                    int i => -i,
                    long l => -l,
                    double d => -d,
                    float f => -f,
                    _ => null,
                };
            default:
                return null;
        }
    }

    static bool TryEmitConstantLoad(ILBuilder cil, BoundExpression init)
    {
        var v = ExtractConstantValue(init);
        switch (v)
        {
            case int i: cil.LoadInt(i); return true;
            case long l: cil.LoadLong(l); return true;
            case bool b: cil.LoadInt(b ? 1 : 0); return true;
            case string s: cil.LoadString(s); return true;
            case double d: cil.LoadDouble(d); return true;
            case float f: cil.LoadFloat(f); return true;
            default: return false;
        }
    }

    // The return type in metadata must be expressed in the *open* type's terms
    // (i.e., EqualityComparer<!0>), so Cecil writes a correct TypeSpec that
    // resolves at runtime to EqualityComparer<fieldType>.
    static MethodReference EqualityComparerGetDefault(ModuleDefinition module, TypeReference fieldType)
    {
        var openType = module.ImportReference(typeof(System.Collections.Generic.EqualityComparer<>));
        var closedType = new GenericInstanceType(openType);
        closedType.GenericArguments.Add(fieldType);

        // Reference the return type as EqualityComparer<!0> (open form over its own T parameter)
        var openTypeDef = openType.Resolve();
        var openReturnType = new GenericInstanceType(openType);
        openReturnType.GenericArguments.Add(openTypeDef.GenericParameters[0]);

        var getDefault = new MethodReference("get_Default", openReturnType, closedType)
        {
            HasThis = false,
        };
        return getDefault;
    }

    // Build MethodReference for EqualityComparer<fieldType>.Equals(T, T).
    static MethodReference EqualityComparerEquals(ModuleDefinition module, TypeReference fieldType)
    {
        var openType = module.ImportReference(typeof(System.Collections.Generic.EqualityComparer<>));
        var closedType = new GenericInstanceType(openType);
        closedType.GenericArguments.Add(fieldType);

        // Use the open type's first generic parameter (T) to express the signature,
        // but bind the declaring type to the closed GenericInstanceType.
        var openTypeDef = openType.Resolve();
        var tParam = openTypeDef.GenericParameters[0];

        var equalsMethod = new MethodReference("Equals", module.ImportReference(typeof(bool)), closedType)
        {
            HasThis = true,
        };
        equalsMethod.Parameters.Add(new ParameterDefinition(tParam));
        equalsMethod.Parameters.Add(new ParameterDefinition(tParam));
        return equalsMethod;
    }

    // Build MethodReference for EqualityComparer<fieldType>.GetHashCode(T).
    static MethodReference EqualityComparerGetHashCode(ModuleDefinition module, TypeReference fieldType)
    {
        var openType = module.ImportReference(typeof(System.Collections.Generic.EqualityComparer<>));
        var closedType = new GenericInstanceType(openType);
        closedType.GenericArguments.Add(fieldType);

        var openTypeDef = openType.Resolve();
        var tParam = openTypeDef.GenericParameters[0];

        var hashMethod = new MethodReference("GetHashCode", module.ImportReference(typeof(int)), closedType)
        {
            HasThis = true,
        };
        hashMethod.Parameters.Add(new ParameterDefinition(tParam));
        return hashMethod;
    }

    static Type? SearchAllAssembliesForAttribute(string attrTypeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Try common BCL attribute namespaces
            var t = asm.GetType($"System.{attrTypeName}")
                 ?? asm.GetType($"System.Runtime.CompilerServices.{attrTypeName}")
                 ?? asm.GetType($"System.Text.Json.Serialization.{attrTypeName}")
                 ?? asm.GetType($"System.Runtime.InteropServices.{attrTypeName}")
                 ?? asm.GetType(attrTypeName);
            if (t is not null) return t;
        }
        return null;
    }

    static (string name, string argsText) SplitAttribute(string attrText)
    {
        var paren = attrText.IndexOf('(');
        if (paren < 0) return (attrText.Trim(), "");
        var name = attrText[..paren].Trim();
        var body = attrText[(paren + 1)..].TrimEnd(')', ' ', '\t');
        return (name, body);
    }

    record AttrArg(string? Name, string Value, bool IsNamed);

    static int FindNamedArgEquals(string piece)
    {
        // Named arg form `Name = value` — `=` at depth 0, not inside a string, not `==`.
        var inString = false;
        for (var i = 0; i < piece.Length; i++)
        {
            var c = piece[i];
            if (c == '"') inString = !inString;
            if (inString) continue;
            if (c == '=' && (i + 1 >= piece.Length || piece[i + 1] != '=') && (i == 0 || piece[i - 1] != '=' && piece[i - 1] != '<' && piece[i - 1] != '>' && piece[i - 1] != '!'))
                return i;
        }
        return -1;
    }

    static void EmitFunctionBody(
        ModuleDefinition module, ILTypeResolver types, DiagnosticBag diagnostics, MethodDefinition method,
        BoundFunctionDeclaration func, Dictionary<string, Dictionary<string, FieldDefinition>> structFieldMaps,
        bool isSelfMethod = false, Dictionary<string, Mono.Cecil.Cil.Document>? documentCache = null,
        OptimizationLevel optimization = OptimizationLevel.Debug)
    {
        var allFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var param in func.Parameters)
        {
            // Match the arity-aware key structFieldMaps is written under (`Name`arity`);
            // a generic `data`'s self-field preload would otherwise miss its own map.
            var typeName = param.Type switch
            {
                DataType dt => MetadataTypeName(dt.Name, dt.TypeParameters.Count),
                // Synthesized async state machines use an ExternalType view. Keep
                // its arity in the field-map key just as a source DataType does;
                // otherwise a generic state machine misses the map where its
                // captured fields were emitted and MoveNext cannot bind them.
                ExternalType et => MetadataTypeName(et.Name, et.TypeArgs.Count),
                _ => null,
            };
            if (typeName is not null && structFieldMaps.TryGetValue(typeName, out var fields))
            {
                foreach (var (name, field) in fields)
                    allFields.TryAdd(name, field);
            }
        }

        // A value receiver on a STRUCT operates on a snapshot copy (Go value-receiver
        // semantics): bind the receiver name to a copy local rather than to `this`, so
        // body mutations don't write back through the managed `this` pointer. Pointer /
        // readonly receivers, and any receiver on a class, bind to `this` directly.
        var snapshotValueReceiver = isSelfMethod && func.Parameters.Count > 0
            && func.ReceiverKind == Esharp.Symbols.ReceiverKind.Value
            && method.DeclaringType is { IsValueType: true };
        var selfParamName = isSelfMethod && !snapshotValueReceiver && func.Parameters.Count > 0 ? func.Parameters[0].Name : null;

        // Field maps are needed for the ordinary bound-receiver path above, but a
        // synthesized instance method already has the authoritative receiver type
        // in Cecil.  Seed from it as well.  This matters for generic async MoveNext:
        // its synthetic `this` is an ExternalType, and resolving its map by display
        // name is needlessly fragile (the actual MethodDefinition knows every
        // captured parameter and spill field exactly).
        if (selfParamName is not null && method.DeclaringType is { } declaringType)
        {
            foreach (var field in declaringType.Fields)
                allFields.TryAdd(field.Name, field);
        }
        var emitter = new MethodBodyEmitter(method, types, diagnostics, allFields, selfParamName,
            contractFma: optimization == OptimizationLevel.Release && func.ContractFma);
        // Generic-method body: push the type-parameter context so resolutions
        // of `T` inside the body land on the method's own GenericParameter.
        bool pushedGenericContext = false;
        if (method.HasGenericParameters)
        {
            types.PushGenericContext(method.GenericParameters);
            pushedGenericContext = true;
        }
        // Emit the body under the ICE guard. A compiler exception anywhere in body
        // emission becomes a located ES9500 diagnostic (CodeGen phase + this method)
        // and the body is replaced with a throwing stub, so the TYPE still loads and
        // the rest of the assembly still emits — the diagnostic, not a downstream
        // TypeLoad/InvalidProgram far from the bug, is what surfaces. (Seed the value-
        // receiver snapshot AFTER the generic context is live so the copy local's type
        // and the `ldobj` token resolve against the method's instantiation.)
        using var _bodyWork = CompilerTrace.Work($"emitting body of {method.DeclaringType?.Name}::{method.Name}");
        var bodyEmitted = CompilerTrace.Guard(diagnostics, CompilerPhase.CodeGen, default, () =>
        {
            if (snapshotValueReceiver)
                emitter.SeedValueReceiverSnapshot(func.Parameters[0].Name, MethodBodyEmitter.ReceiverInstantiationRef(method.DeclaringType.Resolve()));
            emitter.PrepareCaptures(func.Body);
            emitter.PrepareHoistedPointers(func.Body);
            emitter.EmitBlock(func.Body);
            // If any protected region (try / defer / foreach) is in this method,
            // returns redirected through Leave + this epilogue.
            emitter.FinalizeMethodEpilogue();
            return true;
        }, fallback: false);
        if (pushedGenericContext)
            types.PopGenericContext();

        if (!bodyEmitted)
        {
            EmitIceStub(method);
            return;
        }

        // Ensure the method ends with a valid terminator. The binder's
        // definite-return pass guarantees every non-void function returns on all
        // paths, so any fallthrough reaching here is unreachable code (e.g. the
        // "no arm matched" edge of an exhaustive `match`) — but the verifier still
        // requires a typed `ret`. Emit a default value of the return type so the
        // unreachable filler verifies; for void, a bare `ret`.
        if (!ILBuilder.EndsInReturn(method))
        {
            var ilp = new ILBuilder(method);
            EmitDefaultReturnValue(ilp, method);
            ilp.Return();
        }

        ILOptimizer.ShortenOpcodes(method.Body);

        // Apply sequence points after optimization (Nop anchors survive ShortenOpcodes)
        if (documentCache is not null)
            ILPdbWriter.ApplySequencePoints(method, emitter.SequencePoints, documentCache);
    }

    // Replace a method's body with a throwing stub after an ICE was contained in its
    // emission (ES9500 already reported). A `throw new NotSupportedException(...)` body
    // is valid, verifiable IL of any return shape, so the type LOADS and the rest of the
    // assembly emits — the located diagnostic is what the user acts on, not a TypeLoad.
    static void EmitIceStub(MethodDefinition method)
    {
        var body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        var il = body.GetILProcessor();
        var module = method.Module;
        var ctor = module.ImportReference(
            typeof(NotSupportedException).GetConstructor([typeof(string)])!);
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldstr,
            $"E# compiler could not emit method '{method.Name}' (internal error — see ES9500)."));
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Newobj, ctor));
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Throw));
    }

    // Push a verifiable default value of `method.ReturnType` so an unreachable
    // trailing `ret` type-checks. Void emits nothing.
    static void EmitDefaultReturnValue(ILBuilder ilp, MethodDefinition method)
    {
        var rt = method.ReturnType;
        if (rt.MetadataType == MetadataType.Void)
            return;

        if (rt.IsGenericParameter || (rt.IsValueType && rt.MetadataType is not (
                MetadataType.Boolean or MetadataType.Char or
                MetadataType.SByte or MetadataType.Byte or
                MetadataType.Int16 or MetadataType.UInt16 or
                MetadataType.Int32 or MetadataType.UInt32 or
                MetadataType.Int64 or MetadataType.UInt64 or
                MetadataType.Single or MetadataType.Double)))
        {
            // struct / Nullable<T> / closed generic value type / unconstrained
            // generic parameter → initobj a fresh temp, then load it.
            var tmp = new VariableDefinition(rt);
            method.Body.Variables.Add(tmp);
            ilp.LoadLocalAddress(tmp);
            ilp.InitObj(rt);
            ilp.LoadLocal(tmp);
            return;
        }

        switch (rt.MetadataType)
        {
            case MetadataType.Boolean or MetadataType.Char or
                 MetadataType.SByte or MetadataType.Byte or
                 MetadataType.Int16 or MetadataType.UInt16 or
                 MetadataType.Int32 or MetadataType.UInt32:
                ilp.LoadInt(0);
                return;
            case MetadataType.Int64 or MetadataType.UInt64:
                ilp.LoadLong(0L);
                return;
            case MetadataType.Single:
                ilp.LoadFloat(0f);
                return;
            case MetadataType.Double:
                ilp.LoadDouble(0d);
                return;
            default:
                ilp.LoadNull();
                return;
        }
    }
}
