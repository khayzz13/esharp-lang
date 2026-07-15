using System.Reflection;
using Mono.Cecil;
using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Emit;

/// <summary>
/// The Cecil-reference / token layer — our binding with <see cref="Mono.Cecil"/>.
/// This is the Cecil-emission half of the old <c>ILTypeResolver</c> (its
/// reflection-discovery half is S3's <c>Esharp.Metadata</c>). It maps the bound
/// model to concrete Cecil refs and owns the import/dedup cache:
/// <list type="bullet">
///   <item><c>BoundType</c> → <see cref="TypeReference"/> (generics, by-ref, pointer, array);</item>
///   <item><c>MethodSymbol</c> → <see cref="MethodReference"/> (generic instances, varargs);</item>
///   <item><c>FieldSymbol</c> → <see cref="FieldReference"/>;</item>
///   <item>the token import/dedup cache, so a given runtime member imports once.</item>
/// </list>
///
/// <para>The seam CodeGen depends on is <see cref="IMetadataResolver"/> (defined
/// here, in <c>Esharp.Emit</c>): CodeGen takes an <see cref="IMetadataResolver"/>,
/// not a concrete resolver, so it no longer imports the assembly-load machinery
/// of <c>Esharp.ILEmit</c>. This resolves round-1 seam <b>C2-S1</b>. The existing
/// <c>ILTypeResolver</c> implements <see cref="IMetadataResolver"/> verbatim
/// (its public surface already matches); S3's metadata-backed resolver implements
/// the same interface.</para>
///
/// <para><see cref="MetadataBinder"/> also exposes the genuinely Cecil-only
/// construction primitives (generic instantiation, array/by-ref wrapping, the
/// import cache) as concrete, stateless helpers that any resolver implementation
/// can reuse instead of re-deriving the Cecil shapes.</para>
/// </summary>
public sealed class MetadataBinder
{
    readonly ModuleDefinition _module;

    // Token dedup: a runtime member maps to exactly one imported ref. Cecil's
    // ImportReference already caches by handle in most cases, but the binder
    // keeps an explicit cache so symbol→ref is stable across emit passes and so
    // the dedup policy lives in one place.
    readonly Dictionary<MethodSymbol, MethodReference> _methodRefs = new();
    readonly Dictionary<FieldSymbol, FieldReference> _fieldRefs = new();
    readonly Dictionary<Type, TypeReference> _runtimeTypeCache = new();

    public MetadataBinder(ModuleDefinition module) => _module = module;

    public ModuleDefinition Module => _module;

    // ── runtime-type import (cached) ─────────────────────────────────────────

    /// <summary>Import a runtime <see cref="Type"/>, deduped through the cache.</summary>
    public TypeReference ImportReference(Type type)
    {
        if (_runtimeTypeCache.TryGetValue(type, out var cached))
            return cached;
        var imported = _module.ImportReference(type);
        _runtimeTypeCache[type] = imported;
        return imported;
    }

    public MethodReference ImportReference(MethodReference method) => _module.ImportReference(method);
    public FieldReference ImportReference(FieldReference field) => _module.ImportReference(field);

    // ── symbol → ref binding (dedup) ─────────────────────────────────────────

    /// <summary>Bind a source <see cref="MethodSymbol"/> to its emitted reference.</summary>
    public void RegisterMethod(MethodSymbol symbol, MethodReference method) => _methodRefs[symbol] = method;

    public MethodReference? MethodForSymbol(MethodSymbol symbol) =>
        _methodRefs.TryGetValue(symbol, out var m) ? m : null;

    public void RegisterField(FieldSymbol symbol, FieldReference field) => _fieldRefs[symbol] = field;

    public FieldReference? FieldForSymbol(FieldSymbol symbol) =>
        _fieldRefs.TryGetValue(symbol, out var f) ? f : null;

    // ── Cecil construction primitives (stateless Cecil mechanics) ────────────
    // These are the genuinely Cecil-only shapes the resolver re-derives by hand
    // today. Centralizing them keeps the Cecil model in one module.

    /// <summary>Close an open generic type over the given closed arguments
    /// (<c>List`1</c> + <c>[int32]</c> → <c>List`1&lt;int32&gt;</c>), imported into the module.</summary>
    public TypeReference CloseGeneric(TypeReference openType, IReadOnlyList<TypeReference> typeArgs)
    {
        var closed = new GenericInstanceType(openType);
        foreach (var a in typeArgs)
            closed.GenericArguments.Add(a);
        return _module.ImportReference(closed);
    }

    /// <summary><c>T[]</c> — a single-dimension zero-based vector of the element type.</summary>
    public static ArrayType ArrayOf(TypeReference elementType) => new(elementType);

    /// <summary><c>T&amp;</c> — a managed pointer to the element type.</summary>
    public static ByReferenceType ByRefOf(TypeReference elementType) => new(elementType);

    /// <summary><c>T*</c> — an unmanaged pointer to the element type.</summary>
    public static PointerType PointerTo(TypeReference elementType) => new(elementType);

    /// <summary>Close a generic <em>method</em> over its type arguments
    /// (<c>M&lt;T&gt;</c> + <c>[int32]</c> → <c>M&lt;int32&gt;</c>).</summary>
    public MethodReference CloseGenericMethod(MethodReference openMethod, IReadOnlyList<TypeReference> typeArgs)
    {
        var inst = new GenericInstanceMethod(openMethod);
        foreach (var a in typeArgs)
            inst.GenericArguments.Add(a);
        return inst;
    }

    /// <summary>Re-scope a member reference (method or field) onto a closed generic
    /// declaring type, so the operand targets the instantiated slot. Cecil requires
    /// a fresh reference whose <c>DeclaringType</c> is the closed type but which
    /// keeps the open member's name/signature element types (the generic parameters
    /// resolve at the closed site).</summary>
    public MethodReference OnClosedType(MethodReference openMember, GenericInstanceType closedType)
    {
        var bound = new MethodReference(openMember.Name, openMember.ReturnType, closedType)
        {
            HasThis = openMember.HasThis,
            ExplicitThis = openMember.ExplicitThis,
            CallingConvention = openMember.CallingConvention,
        };
        foreach (var p in openMember.Parameters)
            bound.Parameters.Add(new ParameterDefinition(p.ParameterType));
        foreach (var gp in openMember.GenericParameters)
            bound.GenericParameters.Add(new GenericParameter(gp.Name, bound));
        return bound;
    }

    public FieldReference OnClosedType(FieldReference openField, GenericInstanceType closedType) =>
        new(openField.Name, openField.FieldType, closedType);
}

/// <summary>
/// The Cecil-emission contract CodeGen depends on. Defined in <c>Esharp.Emit</c>
/// so CodeGen references the abstraction, never a concrete resolver assembly.
///
/// <para>Two surfaces, deliberately on one interface because the emitter holds a
/// single resolver: the <b>Cecil-reference</b> methods (owned conceptually by
/// <see cref="MetadataBinder"/>) and the <b>reflection-discovery</b> methods
/// (owned by S3's <c>Esharp.Metadata</c>). The split is in *who implements them*,
/// not in the call surface CodeGen sees.</para>
///
/// <para>Round-1 seam <b>C2-S1</b> resolves here: <c>ILTypeResolver</c> already
/// exposes this exact public surface, so it satisfies the interface as-is; S3's
/// metadata resolver implements the same contract.</para>
/// </summary>
public interface IMetadataResolver
{
    // ── Cecil-reference surface (MetadataBinder's domain) ────────────────────
    ModuleDefinition Module { get; }
    TypeReference Resolve(BoundType type);
    TypeReference ResolveGenericArgument(BoundType type);
    bool IsValueType(BoundType type);
    TypeDefinition? TryResolveRegistered(string name);
    MethodReference? FindConstructor(TypeDefinition typeDef);
    MethodReference? MethodForSymbol(MethodSymbol symbol);
    void RegisterMethod(MethodSymbol symbol, MethodReference method);
    TypeReference ImportReference(Type type);

    // ── synthetic-id counters (emission bookkeeping) ─────────────────────────
    int NextClosureId();
    int NextSyntheticLocalId();

    // ── reflection-discovery surface (S3 Esharp.Metadata's domain) ───────────
    Type? BoundTypeToRuntime(BoundType type);
    Type? TryResolveRuntimeType(string name);
    MethodReference? ResolveExternalMethod(Type declaringType, string methodName, int argCount,
        Type[]? argTypes = null, bool silent = false, Type[]? explicitTypeArgs = null);
    (MethodReference method, int fixedCount, Type elementType)? ResolveParamsMethod(
        Type declaringType, string methodName, Type[] argTypes);
    MethodReference? ResolveImportedStaticMethod(string methodName, int argCount,
        Type[]? argTypes = null, Type[]? explicitTypeArgs = null);
    MethodReference? ResolveExtensionMethod(Type receiverType, string methodName, int argCount,
        Type[]? argTypes = null, Type[]? explicitTypeArgs = null);
    MethodInfo? FindBestExtensionMethod(Type receiverType, string methodName, int argCount,
        int explicitTypeArgCount, bool[] isLambdaArg);
}
