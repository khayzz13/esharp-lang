namespace Esharp.BoundTree;

/// Display rendering for BoundType instances — the canonical E# spelling of a type.
/// Replaces the scattered `TypeResolver.TypeDisplayName` calls with a single, dependency-free
/// helper inside the BoundTree module. The IL backend never keys off this string; it is
/// display-only (hover text, diagnostics, TypeInfo projections).
public static class BoundTypeDisplay
{
    public static string Name(BoundType t) => t switch
    {
        PrimitiveType p             => p.Name,
        VoidType                    => "void",
        NullType                    => "null",
        InferredType                => "var",
        DataType d                  => d.EmitName,
        ChoiceType c                => c.EmitName,
        EnumType e                  => e.EmitName,
        InterfaceType i             => i.EmitName,
        StaticFuncType s            => s.EmitName,
        NamedDelegateType nd        => nd.EmitName,
        ResultType r                => $"Result<{Name(r.OkType)}, {Name(r.ErrorType)}>",
        ChanType ch                 => $"Chan<{Name(ch.ElementType)}>",
        TupleType tt                => $"({string.Join(", ", tt.ElementTypes.Select(Name))})",
        NullableType n              => $"{Name(n.Inner)}?",
        ArrayBoundType a            => $"{Name(a.ElementType)}[]",
        ByRefBoundType b            => $"ref {Name(b.Inner)}",
        HeapPointerBoundType hp     => $"*{Name(hp.Inner)}",
        ExternalType ex             => ex.EmitName,
        ExternalCSharpType cs       => cs.EmitName,
        FunctionPointerType fp      => $"&({string.Join(", ", fp.ParameterTypes.Select(Name))} -> {Name(fp.ReturnType)})",
        _                           => t.EmitName,
    };
}
