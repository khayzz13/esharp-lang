namespace Esharp.Symbols;

// ============================================================================
// Public typed sub-interfaces of ISymbol.
//
// ISymbol is the top-level versioned surface; these sub-interfaces are the
// dispatch layer for tooling (LSP, formatter, E#-written tools) that needs to
// act on a specific symbol kind without casting to the compiler-internal
// concrete types (TypeSymbol, MethodSymbol, …).
//
// Design rules:
//   1. All properties here are read-only; the compiler-internal types carry
//      the mutable state the binder writes.
//   2. Each interface is "honestly right-sized" — it exposes only what tooling
//      genuinely needs at the public surface. Compiler-internal state (BoundType
//      back-links, ICSharpTypeHandle, DataClassification, etc.) stays off.
//   3. The concrete types implement these interfaces in addition to ISymbol.
//      The SemanticModel returns ISymbol; callers pattern-match on these to
//      retrieve typed facts.
//   4. SymbolKind on ISymbol is always authoritative for switch dispatch;
//      these interfaces are the strongly-typed face of the same information.
// ============================================================================

/// The public fact-set for a type symbol: its kind, arity, whether it is
/// generic, and the resolved member lists that tooling renders in hover docs
/// and completion lists. The concrete implementation is TypeSymbol.
public interface ITypeSymbol : ISymbol
{
    /// The E# type kind (struct, class, union, …). Distinct from SymbolKind,
    /// which is always SymbolKind.Type for an ITypeSymbol.
    SymbolTypeKind TypeKind { get; }

    /// The number of generic type parameters. Zero for non-generic types.
    int Arity { get; }

    /// True when Arity > 0.
    bool IsGeneric => Arity > 0;

    /// The base class, or null for struct/union/enum/interface and any class
    /// that does not extend another user-declared base.
    ITypeSymbol? BaseType { get; }

    /// The interfaces this type declares conformance to (the `: InterfaceName`
    /// list). Does not include transitively inherited interfaces.
    IReadOnlyList<string> DeclaredInterfaces { get; }

    /// The declared methods and promoted free functions, in declaration order.
    IReadOnlyList<IMethodSymbol> Methods { get; }

    /// The declared fields (data fields, event fields), in declaration order.
    IReadOnlyList<IFieldSymbol> Fields { get; }

    /// Union/enum cases, in declaration order. Empty for struct/class.
    IReadOnlyList<ICaseSymbol> Cases { get; }
}

/// The public kind enumeration for a type symbol — finer than SymbolKind.Type.
/// Named SymbolTypeKind (not TypeKind) to avoid a name clash with the
/// TypeSymbol.TypeKind property (which is TypeSymbolKind, an internal enum).
///
/// Mirrors TypeSymbolKind but is part of the public versioned surface; the
/// internal TypeSymbolKind can evolve (NamespaceHost, ExternalCSharp, etc.)
/// without affecting this.
public enum SymbolTypeKind
{
    Struct,
    Class,
    Union,
    RefUnion,
    Enum,
    Interface,
    StaticFunc,
    Delegate,
    /// A built-in primitive (int, string, bool, …) or an unresolved external type.
    External,
    /// A type parameter (the `T` in `struct Pair<T>`), when surfaced as a symbol.
    TypeParameter,
}

/// The public fact-set for a method or free function symbol. The concrete
/// implementation is MethodSymbol.
public interface IMethodSymbol : ISymbol
{
    /// The number of source-level parameters (the "declared arity"). Does not
    /// include the synthesized state-machine parameters or async builder
    /// parameters the lowering pass adds.
    int ParameterCount { get; }

    /// True when the method is declared on a static host (or is a namespace
    /// free function); false for instance methods and promoted functions.
    bool IsStatic { get; }

    /// True when the method body contains await / async let, making it async.
    bool IsAsync { get; }

    /// The declared type parameters, by name. Empty for non-generic methods.
    IReadOnlyList<string> TypeParameters { get; }

    /// The declared parameters, in declaration order. Each parameter is an
    /// IParameterSymbol carrying its name, declared type display, and mutability.
    IReadOnlyList<IParameterSymbol> Parameters { get; }

    /// Display form of the return type, as the compiler has resolved it.
    /// "void" for void-returning functions; "?" when unresolved.
    string ReturnTypeDisplay { get; }

    /// The receiver kind — None for free functions and static-func members,
    /// Value/Pointer/ReadonlyValue for methods that carry a receiver block.
    ReceiverKind ReceiverKind { get; }
}

/// A parameter of a method or free function: its name, type display, and
/// mutability (whether it was declared as an 'out' / '*T' by-ref parameter).
/// Parameters are exposed through IMethodSymbol.Parameters on the public surface.
public interface IParameterSymbol : ISymbol
{
    /// Display form of the declared type.
    string TypeDisplay { get; }

    /// True when the parameter is declared 'out' (an out-parameter that must be
    /// set before the function returns).
    bool IsOut { get; }

    /// True when the parameter is declared as a by-ref pointer ('*T').
    bool IsByRef { get; }
}

/// The public fact-set for a field (data field or event field). The concrete
/// implementation is FieldSymbol.
public interface IFieldSymbol : ISymbol
{
    /// Display form of the field's resolved type.
    string TypeDisplay { get; }

    /// True when the field was declared with 'var' (mutable); false for 'let'
    /// (write-once / immutable) fields.
    bool IsMutable { get; }

    /// True when the field is an event (declared with 'event').
    bool IsEvent { get; }

    /// True when the field is embedded (anonymous field — a bare type name that
    /// promotes its members into the containing type).
    bool IsEmbedded { get; }

    /// True for a property boundary; false for direct field storage.
    bool IsProperty { get; }

    /// True when the property supplies a durable location protocol.
    bool HasDurableLocation { get; }

    /// True when the property supplies a scoped mutation protocol.
    bool HasScopedMutation { get; }

    /// True when the property has an authored setter policy.
    bool HasCustomSetter { get; }

    /// True when access is represented by generated CLR accessor methods.
    bool HasGeneratedAccessors { get; }
}

/// The public fact-set for a local binding or parameter declared inside a
/// function body. The concrete implementation is LocalSymbol.
///
/// Note: parameters are also reported as IParameterSymbol via
/// IMethodSymbol.Parameters; they appear as ILocalSymbol in the scope-visible
/// list that LookupSymbolsInScope returns (since from a local-resolution
/// standpoint, a parameter is just a name in scope).
public interface ILocalSymbol : ISymbol
{
    /// Display form of the declared (or inferred) type.
    string TypeDisplay { get; }

    /// True when declared with 'var'; false for 'let' or a parameter.
    bool IsMutable { get; }

    /// True when this local is a function parameter rather than a body binding.
    bool IsParameter { get; }

    /// True when the declaration selected compiler-managed borrowable storage.
    bool IsAddressable { get; }
}

/// The public fact-set for a compile-time constant ('const'). The concrete
/// implementation is ConstSymbol.
public interface IConstSymbol : ISymbol
{
    /// The folded literal value, as a string render for hover/display.
    string FoldedValueDisplay { get; }
}

/// The public fact-set for a union/enum case ('choice case'). The concrete
/// implementation is CaseSymbol.
public interface ICaseSymbol : ISymbol
{
    /// The payload fields, in declaration order. Empty for enum cases and
    /// payload-less union cases.
    IReadOnlyList<IFieldSymbol> Payloads { get; }

    /// The enum case's underlying integer value; null for union cases.
    int? EnumValue { get; }
}

/// The public fact-set for a namespace. Namespace symbols are synthesized by
/// the SymbolTable (as TypeSymbol with TypeSymbolKind.NamespaceHost) but
/// exposed to tooling through this interface, distinct from ITypeSymbol.
///
/// Tooling distinguishes a namespace from a type by ISymbol.Kind ==
/// SymbolKind.Namespace — no casting needed.
public interface INamespaceSymbol : ISymbol
{
    /// All type symbols declared in this namespace, across all source files.
    IReadOnlyList<ITypeSymbol> DeclaredTypes { get; }

    /// All free functions declared directly in this namespace
    /// (not in a static or a type body).
    IReadOnlyList<IMethodSymbol> FreeFunctions { get; }
}
