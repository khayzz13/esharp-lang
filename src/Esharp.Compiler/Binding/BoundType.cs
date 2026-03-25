using Esharp.Compiler.Syntax;

namespace Esharp.Compiler.Binding;

public abstract record BoundType
{
    public abstract string EmitName { get; }
}

public sealed record PrimitiveType(string Name) : BoundType
{
    public override string EmitName => Name;
}

public sealed record DataType(string Name, IReadOnlyList<string> TypeParameters, DataDeclarationSyntax Decl) : BoundType
{
    public override string EmitName => TypeParameters.Count > 0
        ? $"{Name}<{string.Join(", ", TypeParameters)}>"
        : Name;
}

public sealed record ChoiceType(string Name, IReadOnlyList<string> TypeParameters, ChoiceDeclarationSyntax Decl) : BoundType
{
    public override string EmitName => TypeParameters.Count > 0
        ? $"{Name}<{string.Join(", ", TypeParameters)}>"
        : Name;
}

public sealed record EnumType(string Name, EnumDeclarationSyntax Decl) : BoundType
{
    public override string EmitName => Name;
}

public sealed record ResultType(BoundType OkType, BoundType ErrorType) : BoundType
{
    public override string EmitName => $"Result<{OkType.EmitName}, {ErrorType.EmitName}>";
}

public sealed record ChanType(BoundType ElementType) : BoundType
{
    public override string EmitName => $"Chan<{ElementType.EmitName}>";
}

public sealed record ExternalType(string Name) : BoundType
{
    public override string EmitName => Name;
}

public sealed record VoidType : BoundType
{
    public override string EmitName => "void";
}

public sealed record NullType : BoundType
{
    public override string EmitName => "null";
}

public sealed record FunctionPointerType(IReadOnlyList<BoundType> ParameterTypes, BoundType ReturnType) : BoundType
{
    public override string EmitName =>
        $"delegate*<{string.Join(", ", ParameterTypes.Select(p => p.EmitName))}, {ReturnType.EmitName}>";
}
