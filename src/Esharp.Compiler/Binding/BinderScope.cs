namespace Esharp.Compiler.Binding;

public sealed class BinderScope
{
    readonly BinderScope? _parent;
    readonly Dictionary<string, BoundType> _variables = new(StringComparer.Ordinal);

    BinderScope(BinderScope? parent) => _parent = parent;

    public static BinderScope Root() => new(null);
    public BinderScope Child() => new(this);

    public void Declare(string name, BoundType type) => _variables[name] = type;

    public BoundType? Lookup(string name)
    {
        if (_variables.TryGetValue(name, out var type)) return type;
        return _parent?.Lookup(name);
    }
}
