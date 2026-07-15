namespace Esharp.BoundTree;

// A language-neutral handle to a type that lives outside E# (today, always a C#
// type from a sibling `.cs` file in the same project). Implementations live in
// the Workspace layer, wrapping Roslyn's INamedTypeSymbol — this interface is
// the seam that keeps the compiler core free of the Roslyn package dependency.
//
// The handle reports the metadata-level shape (names, modifiers, member
// signatures, base/interface chain). The binder consumes it during name
// resolution and inheritance checks; the IL emitter consumes it to build Cecil
// TypeReferences scoped to the right assembly identity. Everything the binder
// and emitter need is on this surface — they never look at the underlying
// language-specific symbol.
public interface ICSharpTypeHandle
{
    string Name { get; }
    string Namespace { get; }
    string FullMetadataName { get; }
    string AssemblyName { get; }

    bool IsRefType { get; }
    bool IsInterface { get; }
    bool IsEnum { get; }
    bool IsSealed { get; }
    bool IsAbstract { get; }
    bool IsStatic { get; }

    int GenericParameterCount { get; }
    ICSharpTypeHandle? BaseType { get; }
    IReadOnlyList<ICSharpTypeHandle> Interfaces { get; }
    IReadOnlyList<ICSharpMemberHandle> Members { get; }
}

public interface ICSharpMemberHandle
{
    string Name { get; }
    CSharpMemberKind Kind { get; }
    bool IsStatic { get; }
    bool IsVirtual { get; }
    bool IsAbstract { get; }
    bool IsPublic { get; }

    // Property ABI. Defaults keep field/method adapters source-compatible while
    // allowing the mixed-language seam to preserve ref-returning properties.
    bool HasGetter => false;
    bool HasSetter => false;
    bool ReturnsByRef => false;
    bool ReturnsByRefReadonly => false;

    BoundType ReturnType { get; }
    IReadOnlyList<ICSharpParameterHandle> Parameters { get; }

    // For const/enum fields: the literal value at declaration. Null for
    // non-literal members. Lets the IL emitter inline `enum.Red` as `ldc.i4 0`
    // instead of `ldsfld`, matching how C# itself emits enum member loads.
    object? ConstantValue { get; }
}

public interface ICSharpParameterHandle
{
    string Name { get; }
    BoundType Type { get; }

    // Optional-parameter metadata, so an E# call site may omit a trailing optional and
    // have the emitter fill the declared default. `DefaultValue` is the constant the
    // parameter declares (an enum default arrives as its underlying CLR primitive); null
    // means either no explicit default or an explicit `default(T)`.
    bool IsOptional { get; }
    object? DefaultValue { get; }
}

public enum CSharpMemberKind
{
    Method,
    Field,
    Property,
    Constructor,
}
