using Esharp.Symbols;

namespace Esharp.Binder;

public sealed class BinderScope
{
    readonly BinderScope? _parent;
    readonly Dictionary<string, (BoundType type, bool mutable, LocalRepresentation representation)> _variables = new(StringComparer.Ordinal);
    // The interned local symbol per name, when one was declared (locals, params,
    // match bindings). The SAME instance is handed back at every use so the
    // semantic sink reports reference-identical occurrences — find-references is a
    // list lookup, not a name search. A name may live in _variables without a
    // symbol (type-parameter placeholders, synthesized scopes); those just have
    // no spine identity to report.
    readonly Dictionary<string, LocalSymbol> _localSymbols = new(StringComparer.Ordinal);
    // Compile-time constants declared in this scope. The binder folds reads
    // by name into the stored literal expression — no IL slot is allocated.
    readonly Dictionary<string, BoundLiteralExpression> _constants = new(StringComparer.Ordinal);

    BinderScope(BinderScope? parent) => _parent = parent;

    public static BinderScope Root() => new(null);
    public BinderScope Child() => new(this);

    public void Declare(string name, BoundType type, bool mutable = true,
        LocalRepresentation representation = LocalRepresentation.Default) => _variables[name] = (type, mutable, representation);

    /// Declare a name AND its interned local symbol — the spine identity the
    /// semantic model reports at this declaration and recovers at every use.
    public void DeclareLocal(LocalSymbol symbol, BoundType type) =>
        DeclareLocal(symbol, type, symbol.Mutable);

    public void DeclareLocal(LocalSymbol symbol, BoundType type, bool mutable)
    {
        _variables[symbol.Name] = (type, mutable, symbol.Representation);
        _localSymbols[symbol.Name] = symbol;
    }

    /// The interned local symbol for a name, walking out through enclosing scopes —
    /// the same instance the declaration registered. Null for names with no spine
    /// identity (placeholders) or names that aren't locals.
    public LocalSymbol? LookupLocal(string name)
    {
        if (_localSymbols.TryGetValue(name, out var sym)) return sym;
        return _parent?.LookupLocal(name);
    }

    public void DeclareConst(string name, BoundLiteralExpression literal)
    {
        _constants[name] = literal;
        _variables[name] = (literal.Type, false, LocalRepresentation.Default);
    }

    public BoundLiteralExpression? LookupConst(string name)
    {
        if (_constants.TryGetValue(name, out var lit)) return lit;
        return _parent?.LookupConst(name);
    }

    public BoundType? Lookup(string name)
    {
        if (_variables.TryGetValue(name, out var entry)) return entry.type;
        return _parent?.Lookup(name);
    }

    public bool? LookupMutable(string name)
    {
        if (_variables.TryGetValue(name, out var entry)) return entry.mutable;
        return _parent?.LookupMutable(name);
    }

    public LocalRepresentation? LookupRepresentation(string name)
    {
        if (_variables.TryGetValue(name, out var entry)) return entry.representation;
        return _parent?.LookupRepresentation(name);
    }

    /// <summary>Checks only the current scope (not parent). Used for closure capture detection.</summary>
    public bool ContainsLocal(string name) => _variables.ContainsKey(name);

    /// <summary>Returns all variable names declared in this scope only.</summary>
    public IEnumerable<string> LocalNames => _variables.Keys;
}
