using System.Collections.Concurrent;

namespace Esharp.Diagnostics;

// ============================================================================
// DiagnosticCatalog — the global registry of DiagnosticDescriptors keyed by Id.
//
// Design:
//   • Descriptors are registered at module startup (static ctors of the compiler
//     areas that own them). Registration is idempotent — registering the same Id
//     twice with the same descriptor is a no-op; registering with a different
//     descriptor throws to catch accidental Id reuse at startup.
//
//   • The catalog is global (ConcurrentDictionary) and lock-free on the read path
//     (the common case during binding). Write (Register) happens only at startup
//     before any binding thread is active, so the cost is irrelevant.
//
//   • Ad-hoc helpers (AdHocError / AdHocWarning / …) are provided for call sites
//     in transition — they create a transient descriptor that is NOT added to the
//     catalog, preserving the catalog's role as the exhaustive list of named rules.
//     Over time all ad-hoc uses should be migrated to named descriptors; the
//     "ES0001 ad-hoc" pattern is a migration breadcrumb, not a long-term design.
//
// ES#### scheme (from the spec):
//   ES1xxx  — type system (const folding, value-type cycles, pointer rules, interface conformance,
//               conversion, smart-cast stability)
//   ES2xxx  — semantic / naming / flow (naming conventions, namespace resolution, match semantics,
//               definite return, call conventions, constructor rules, field coverage, concurrency)
//   ES3xxx  — codegen / struct rules (struct init, async-let shape, unresolved extern, emit failures)
//   ES9xxx  — workspace / infra (project model, internal errors)
// ============================================================================
public static class DiagnosticCatalog
{
    static readonly ConcurrentDictionary<string, DiagnosticDescriptor> _catalog = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ registration

    /// Register a descriptor by its Id. Idempotent for the same instance;
    /// throws <see cref="InvalidOperationException"/> on Id collision with a
    /// different descriptor (catches accidental Id reuse at startup).
    public static DiagnosticDescriptor Register(DiagnosticDescriptor descriptor)
    {
        var existing = _catalog.GetOrAdd(descriptor.Id, descriptor);
        if (!ReferenceEquals(existing, descriptor) && existing != descriptor)
            throw new InvalidOperationException(
                $"DiagnosticCatalog: Id '{descriptor.Id}' is already registered with a different descriptor. " +
                $"Existing title: '{existing.Title}', new title: '{descriptor.Title}'.");
        return existing;
    }

    // ------------------------------------------------------------------ lookup

    /// Look up a descriptor by Id, or null if not registered.
    public static DiagnosticDescriptor? TryGet(string id) =>
        _catalog.TryGetValue(id, out var d) ? d : null;

    /// All currently registered descriptors, in arbitrary order.
    public static IEnumerable<DiagnosticDescriptor> All => _catalog.Values;

    // ------------------------------------------------------------------ GetOrAdd (transitional)

    /// Get by Id or create and cache an ad-hoc descriptor with the given message and severity.
    /// Used by the transitional Diagnostic constructors that receive a bare string code.
    /// Prefer named descriptors registered at startup over this path.
    internal static DiagnosticDescriptor GetOrAdd(string id, string message, DiagnosticSeverity severity) =>
        _catalog.GetOrAdd(id, _ => new DiagnosticDescriptor(
            id, message, message, severity, "General"));

    // ------------------------------------------------------------------ ad-hoc helpers (transitional)
    //
    // These produce a transient descriptor NOT stored in the catalog. Used by DiagnosticBag's
    // span+message fast-path overloads for call sites not yet migrated to named descriptors.
    // They carry Id = "ES0001" / "ES0002" / "ES0003" / "ES0004" as a migration breadcrumb —
    // a search for these Ids turns up every site that still owes a named descriptor.

    internal static DiagnosticDescriptor AdHocError(string message) =>
        new("ES0001", message, message, DiagnosticSeverity.Error, "General");

    internal static DiagnosticDescriptor AdHocWarning(string message) =>
        new("ES0002", message, message, DiagnosticSeverity.Warning, "General");

    internal static DiagnosticDescriptor AdHocInfo(string message) =>
        new("ES0003", message, message, DiagnosticSeverity.Info, "General");

    internal static DiagnosticDescriptor AdHocHidden(string message) =>
        new("ES0004", message, message, DiagnosticSeverity.Hidden, "General");
}

// ============================================================================
// DiagnosticDescriptors — the canonical named descriptor definitions,
// populated from the spec's real ES#### codes.
//
// Each compiler area owns a region. Static readonly fields — registered in the
// static ctor so they land in the catalog before any compilation starts.
//
// Organized by spec area. Each entry cites the spec section it originates from.
// ============================================================================
public static partial class DiagnosticDescriptors
{
    // ------------------------------------------------------------------ registration helper

    /// Shorthand: creates and registers a descriptor in one call.
    public static DiagnosticDescriptor Register(DiagnosticDescriptor d) =>
        DiagnosticCatalog.Register(d);

    // ================================================================
    // ES1xxx — Type system
    // Const folding, value-type structural rules, pointer rules, interface
    // conformance, conversion model, smart-cast stability.
    // ================================================================

    /// const initializer does not fold to a compile-time literal.
    /// Spec: programs.md §Initialization / declarations.md §const-and-derive
    public static readonly DiagnosticDescriptor ConstNotFoldable = Register(new(
        "ES1011",
        "const initializer not foldable",
        "Initializer of '{0}' cannot be folded to a compile-time constant. " +
        "A const must be a literal, nil, a dot-case, or an ok/error over such values.",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES1011"));

    /// A struct field whose type contains the enclosing struct by value (recursive struct).
    /// Spec: declarations.md §struct / generics.md §Recursion / type-system.md §value-types
    public static readonly DiagnosticDescriptor RecursiveValueType = Register(new(
        "ES2002",
        "Recursive value type",
        "Field '{0}' in '{1}' contains the enclosing type '{1}' by value, which is ill-formed. " +
        "Break the cycle with a pointer (*{1}).",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES2002"));

    /// *Class is ill-formed: a class is already a reference.
    /// Spec: declarations.md §struct / type-system.md §pointers / pointers.md / expressions.md §construction
    public static readonly DiagnosticDescriptor PointerToClass = Register(new(
        "ES2003",
        "Pointer to class",
        "'{0}' is a class (a reference type) and cannot be used as a pointer target. " +
        "A class is already a reference; write '{0}' or '{0}?' directly.",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES2003"));

    /// A managed pointer alias reaches a durable sink after pointer realization.
    /// This is a source-located containment barrier: callers receive an actionable
    /// lifetime diagnostic rather than malformed IL or an eventual runtime ICE.
    public static readonly DiagnosticDescriptor UnrealizedDurablePointerAlias = Register(new(
        "ES2030",
        "Managed pointer escapes its lifetime",
        "Pointer alias '{0}' crosses a durable lifetime boundary but remains a managed borrow. " +
        "The compiler cannot emit that borrow safely; use a durable address at the boundary.",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES2030"));

    /// 'new' on a non-struct type.
    /// Spec: expressions.md §construction
    public static readonly DiagnosticDescriptor NewOnNonStruct = Register(new(
        "ES2144",
        "'new' on non-struct type",
        "'new {0}' is only valid for value struct types; '{0}' is not a struct. " +
        "For a class use a composite literal '{0} {{ … }}' or a factory.",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES2144"));

    // ================================================================
    // ES2xxx — Semantic / naming / flow
    //
    // This is the largest family. Sub-groups:
    //   ES21xx  — names, resolution, namespace
    //   ES212x  — inheritance
    //   ES213x  — methods and functions
    //   ES214x  — functions (definite return, events, raise)
    //   ES215x  — namespace resolution (cross-ns, ambiguity, missing import)
    //   ES216x  — naming conventions
    //   ES217x  — match, narrowing, smart-cast
    //   ES218x  — call conventions, default args, constructors
    //   ES219x  — struct/class field coverage
    // ================================================================

    // -- ES212x: inheritance diagnostics --

    /// `: base(…)` or `: this(…)` targets no matching init overload.
    /// Spec: declarations.md §Constructors / inheritance guide
    public static readonly DiagnosticDescriptor NoMatchingInitDelegate = Register(new(
        "ES2128",
        "No matching init to delegate to",
        "The delegation call ': {0}(…)' with arity {1} matches no declared init in {2}.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2128"));

    // -- ES213x: method / function rules --

    /// Closure inside a task func captures a var from the surrounding scope.
    /// Spec: functions.md §Lambdas / concurrency.md §spawn
    public static readonly DiagnosticDescriptor TaskClosureCaptureVar = Register(new(
        "ES2130",
        "task func closure captures mutable variable",
        "A lambda inside a 'task func' body captures '{0}', which is a mutable 'var'. " +
        "Shared mutable state across a concurrency boundary is a data race. " +
        "Thread the value through a channel parameter instead.",
        DiagnosticSeverity.Error,
        "Concurrency",
        "https://esharp-lang.vercel.app/diagnostics/ES2130"));

    /// yield used outside an IAsyncEnumerable<T> function.
    /// Spec: concurrency.md §Async streams
    public static readonly DiagnosticDescriptor YieldOutsideAsyncStream = Register(new(
        "ES2131",
        "'yield' outside async stream",
        "'yield' may only appear inside a function declared '-> IAsyncEnumerable<T>'.",
        DiagnosticSeverity.Error,
        "Concurrency",
        "https://esharp-lang.vercel.app/diagnostics/ES2131"));

    /// Receiver over a closed generic (e.g. func (h: Holder<int>)).
    /// Spec: functions.md §methods
    public static readonly DiagnosticDescriptor ReceiverOnClosedGeneric = Register(new(
        "ES2132",
        "Receiver on closed generic",
        "A method receiver must use the type's open parameters. '{0}' is a closed instantiation; " +
        "write 'func ({1}: {2}<T>)' and make the method generic.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2132"));

    // -- ES214x: definite return, events, raise --

    /// Function does not return on every path.
    /// Spec: statements.md §return / pattern-matching.md §match-and-definite-return
    public static readonly DiagnosticDescriptor MissingReturn = Register(new(
        "ES2140",
        "Missing return",
        "'{0}' does not return on every path. Add a 'return' or make the match exhaustive.",
        DiagnosticSeverity.Error,
        "FlowAnalysis",
        "https://esharp-lang.vercel.app/diagnostics/ES2140",
        Fixit: "AddMissingReturn"));

    /// Event declared on a value struct.
    /// Spec: delegates-events.md §Events
    public static readonly DiagnosticDescriptor EventOnValueStruct = Register(new(
        "ES2141",
        "Event on value struct",
        "'{0}' is a value struct; events may only be declared on 'class' or 'interface'.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2141"));

    /// raise or free-call spelling used for a method; event name is not declared on the enclosing type.
    /// Spec: statements.md §raise / functions.md §methods / delegates-events.md §Events
    public static readonly DiagnosticDescriptor InvalidRaiseOrMethodCall = Register(new(
        "ES2142",
        "Invalid raise or method call",
        "'{0}' is not declared on the enclosing type, or a method is being called with free-call syntax. " +
        "Use the receiver spelling 'receiver.{0}(…)', or declare the event on the enclosing class.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2142",
        Fixit: "UseMethodSyntax"));

    // -- ES215x: namespace resolution --

    /// Bare cross-namespace reference to a type with no import or qualifier.
    /// Spec: names-and-resolution.md §Resolution
    public static readonly DiagnosticDescriptor UnimportedType = Register(new(
        "ES2150",
        "Unimported type",
        "'{0}' is declared in namespace '{1}' but that namespace is not imported. " +
        "Add 'using \"{1}\"' or qualify the name as '{1}.{0}'.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2150",
        Fixit: "AddUsing"));

    /// Ambiguous type name: visible from two or more in-scope namespaces.
    /// Spec: names-and-resolution.md §Ambiguity
    public static readonly DiagnosticDescriptor AmbiguousType = Register(new(
        "ES2151",
        "Ambiguous type name",
        "'{0}' is declared in more than one in-scope namespace ({1}). " +
        "Qualify the name or add a type alias.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2151",
        Fixit: "AddTypeAlias"));

    /// Bare cross-namespace reference to a free function with no import or qualifier.
    /// Also: same-name + same-arity redeclaration (generic arity keying).
    /// Spec: names-and-resolution.md §Resolution / generics.md §Arity-keying
    public static readonly DiagnosticDescriptor UnimportedFunctionOrDuplicate = Register(new(
        "ES2152",
        "Unimported function or duplicate declaration",
        "'{0}' is either declared in a namespace that is not imported, or it is redeclared " +
        "with the same name and arity. Import the namespace or qualify, and ensure names are unique per arity.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2152",
        Fixit: "AddUsing"));

    /// Type structurally satisfies an interface it does not name.
    /// Spec: type-system.md §Enumerations-and-interfaces / declarations.md §interface
    public static readonly DiagnosticDescriptor ImplicitInterfaceConformance = Register(new(
        "ES2153",
        "Implicit interface conformance",
        "'{0}' satisfies the contract of '{1}' structurally but does not name it after ':'. " +
        "E# uses nominal conformance; add ': {1}' to the declaration.",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES2153",
        Fixit: "AddInterfaceConformance"));

    // -- ES216x: naming conventions --

    /// Type name is not PascalCase.
    /// Spec: declarations.md §types / index.md §naming
    public static readonly DiagnosticDescriptor TypeNameNotPascalCase = Register(new(
        "ES2160",
        "Type name must be PascalCase",
        "Type name '{0}' must be PascalCase (e.g. '{1}'). " +
        "Types (struct, class, union, ref union, enum, interface, static func, delegate func) " +
        "must begin with an uppercase letter.",
        DiagnosticSeverity.Error,
        "Style",
        "https://esharp-lang.vercel.app/diagnostics/ES2160",
        Fixit: "RenameToPascalCase"));

    /// Free function name is not camelCase.
    /// Spec: declarations.md §functions / index.md §naming
    public static readonly DiagnosticDescriptor FreeFunctionNotCamelCase = Register(new(
        "ES2161",
        "Free function name must be camelCase",
        "Free function '{0}' must be camelCase (e.g. '{1}'). " +
        "Free functions must begin with a lowercase letter. " +
        "(Methods on types follow the type's convention.)",
        DiagnosticSeverity.Error,
        "Style",
        "https://esharp-lang.vercel.app/diagnostics/ES2161",
        Fixit: "RenameToCamelCase"));

    // -- ES217x: match, narrowing, smart-cast --

    /// union/enum matched by type pattern instead of .case.
    /// Spec: pattern-matching.md §The-scrutinee
    public static readonly DiagnosticDescriptor ClosedTypeMatchedByType = Register(new(
        "ES2172",
        "union/enum matched by type pattern",
        "'{0}' is a {1} (a closed type) and must be matched by '.case' patterns, not by a type pattern. " +
        "Type patterns are for open hierarchies (abstract class, interface, object).",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2172"));

    /// Smart-cast relies on a narrow the compiler cannot prove stable.
    /// Spec: expressions.md §Type-narrowing / type-system.md §Conversions
    public static readonly DiagnosticDescriptor UnstableSmartCast = Register(new(
        "ES2173",
        "Unstable smart-cast",
        "The narrow of '{0}' to '{1}' cannot be proven stable here. Smart-cast applies only to " +
        "'let' locals, parameters, and 'let'-field paths with no intervening call. " +
        "Use 'as' (safe, yields T?) or 'as!' (asserting, throws on miss).",
        DiagnosticSeverity.Error,
        "FlowAnalysis",
        "https://esharp-lang.vercel.app/diagnostics/ES2173"));

    // -- ES218x: call conventions, defaults, constructors --

    /// Default argument value is not a constant shape.
    /// Spec: functions.md §Default-and-named-arguments
    public static readonly DiagnosticDescriptor DefaultNotConstant = Register(new(
        "ES2180",
        "Default argument is not a constant shape",
        "The default for '{0}' must fold to a literal, be 'nil', or be a composite-literal / " +
        "dot-case / ok/error construction over such constants. The provided expression cannot fold.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2180"));

    /// Positional argument follows a named argument at the call site.
    /// Spec: functions.md §Default-and-named-arguments
    public static readonly DiagnosticDescriptor PositionalAfterNamed = Register(new(
        "ES2181",
        "Positional argument after named argument",
        "Positional arguments must come before named arguments. " +
        "Move the positional argument '{0}' before the named arguments, or give it a name.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2181"));

    /// Named argument names no parameter in the callee's signature.
    /// Spec: functions.md §Default-and-named-arguments
    public static readonly DiagnosticDescriptor UnknownNamedArgument = Register(new(
        "ES2182",
        "Named argument names no parameter",
        "'{0}' is not a parameter of '{1}'. Check the spelling or remove the name.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2182"));

    /// Too few required arguments, or too many arguments.
    /// Spec: functions.md §Default-and-named-arguments
    public static readonly DiagnosticDescriptor ArgumentCountMismatch = Register(new(
        "ES2183",
        "Argument count mismatch",
        "'{0}' expects {1} required argument(s) (and up to {2} with defaults), but {3} were supplied.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2183"));

    /// A parameter is filled twice (positional + named, or named twice).
    /// Spec: functions.md §Default-and-named-arguments
    public static readonly DiagnosticDescriptor DuplicateArgument = Register(new(
        "ES2184",
        "Parameter filled twice",
        "Parameter '{0}' of '{1}' is supplied more than once (positional and named, or named twice).",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2184"));

    /// Two init blocks with the same arity on the same class.
    /// Spec: declarations.md §Constructors
    public static readonly DiagnosticDescriptor DuplicateInitArity = Register(new(
        "ES2185",
        "Duplicate init arity",
        "'{0}' already has an 'init' with {1} parameter(s). Overloads are resolved by arity; " +
        "give this init a different arity, or merge with a default parameter.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2185"));

    /// A secondary init on a headered class does not delegate with `: this(...)`.
    /// Spec: declarations.md §Primary-constructor-capture
    public static readonly DiagnosticDescriptor SecondaryInitMustDelegate = Register(new(
        "ES2186",
        "Secondary init must delegate",
        "'{0}' has a capture header, so every secondary 'init' must delegate to the primary " +
        "with ': this(…)'. Add a delegation call.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2186",
        Fixit: "AddThisDelegation"));

    /// init delegation cycle.
    /// Spec: declarations.md §Constructors
    public static readonly DiagnosticDescriptor InitDelegationCycle = Register(new(
        "ES2187",
        "init delegation cycle",
        "The 'init' delegation chain in '{0}' forms a cycle. An init may not directly or " +
        "indirectly delegate back to itself.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2187"));

    /// A capture-header parameter name shadows an explicit field.
    /// Spec: declarations.md §Primary-constructor-capture
    public static readonly DiagnosticDescriptor HeaderParamShadowsField = Register(new(
        "ES2188",
        "Header parameter shadows field",
        "Capture-header parameter '{0}' in '{1}' has the same name as a declared field. " +
        "Rename either the parameter or the field.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2188"));

    // -- ES219x: field coverage --

    /// A composite literal omits a required field.
    /// Spec: declarations.md §struct / expressions.md §construction
    public static readonly DiagnosticDescriptor RequiredFieldNotSet = Register(new(
        "ES2189",
        "Required field not set",
        "Required field '{0}' of '{1}' is not set in this composite literal. " +
        "Every 'required' field must be supplied.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2189",
        Fixit: "AddRequiredField"));

    /// A composite literal over a headered class (headered class must be constructed through its primary init).
    /// Spec: declarations.md §Primary-constructor-capture / expressions.md §construction
    public static readonly DiagnosticDescriptor CompositeLiteralOnHeaderedClass = Register(new(
        "ES2190",
        "Composite literal on headered class",
        "'{0}' has a capture header and cannot be constructed with a composite literal. " +
        "Call its primary constructor with positional arguments: '{0}(…)'.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2190"));

    /// Stored 'let' property on a value struct (no constructor to set it).
    /// Spec: declarations.md §Properties / clr-mapping.md
    public static readonly DiagnosticDescriptor StoredLetPropertyOnStruct = Register(new(
        "ES2193",
        "Stored let property on value struct",
        "A stored 'let x {{ }}' property is not valid on a value struct '{0}': there is no 'init' " +
        "to write through it. Use a computed property ('let x => expr'), " +
        "'required let x {{ }}' (set by composite literal), or 'var x {{ }}'.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2193"));

    /// A direct field is not a property implementation. The declaration keyword owns
    /// the member's ABI; conformance never changes a field into a property implicitly.
    public static readonly DiagnosticDescriptor FieldCannotImplementInterfaceProperty = Register(new(
        "ES2226",
        "Field cannot implement interface property",
        "'{0}.{1}' is a field, but interface '{2}' requires property '{1}'. " +
        "Declare it with 'let' or 'var' so the source declaration owns the property contract.",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES2226"));

    /// `enum C: T { … }` where `T` is not one of the CLR integral primitives. An enum's
    /// underlying type must be byte/sbyte/short/ushort/int/uint/long/ulong.
    public static readonly DiagnosticDescriptor EnumUnderlyingTypeInvalid = Register(new(
        "ES2127",
        "Invalid enum underlying type",
        "Enum '{0}' cannot be backed by '{1}'. The underlying type must be an integral primitive: " +
        "byte, sbyte, short, ushort, int, uint, long, or ulong.",
        DiagnosticSeverity.Error,
        "TypeSystem",
        "https://esharp-lang.vercel.app/diagnostics/ES2127"));

    /// A property accessor may only narrow the property's visibility, never widen it.
    /// `pub var X { priv set }` is legal; `priv var X { pub set }` is not.
    public static readonly DiagnosticDescriptor AccessorCannotWidenVisibility = Register(new(
        "ES2229",
        "Accessor cannot widen property visibility",
        "The '{0}' accessor of '{1}' declares visibility '{2}', which is wider than the property's '{3}'. " +
        "A per-accessor modifier may only narrow the property's visibility.",
        DiagnosticSeverity.Error,
        "Semantic",
        "https://esharp-lang.vercel.app/diagnostics/ES2229"));

    public static readonly DiagnosticDescriptor VarArrowRequiresWriteBehavior = Register(new(
        "ES2227",
        "Var computed shorthand has no write behavior",
        "'var {0}: T => expression' cannot promise writes without a setter. Use 'let', or " +
        "declare 'var {0}: T {{ get => expression  set(value) => effect }}'.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2227"));

    public static readonly DiagnosticDescriptor CustomVarGetterRequiresWriteBehavior = Register(new(
        "ES2228",
        "Custom var getter requires write behavior",
        "Property 'var {0}' has a custom getter but no authored write behavior. " +
        "Add 'set(value) => effect' or 'mut', or declare the property with 'let'.",
        DiagnosticSeverity.Error,
        "Binding",
        "https://esharp-lang.vercel.app/diagnostics/ES2228"));

    public static readonly DiagnosticDescriptor ByRefLikeField = Register(new(
        "ES2230", "By-ref-like value stored in a field",
        "By-ref-like type '{0}' cannot be stored in field '{1}'. E# types are heap-capable; keep the value in a local or parameter instead.",
        DiagnosticSeverity.Error, "TypeSystem", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#by-ref-like-safety"));

    public static readonly DiagnosticDescriptor ByRefLikeReturn = Register(new(
        "ES2231", "By-ref-like return requires lifetime analysis",
        "Function '{0}' cannot return by-ref-like type '{1}' until E# has return-lifetime analysis.",
        DiagnosticSeverity.Error, "TypeSystem", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#by-ref-like-safety"));

    public static readonly DiagnosticDescriptor ByRefLikeAsyncState = Register(new(
        "ES2232", "By-ref-like value crosses async suspension",
        "Async function '{0}' cannot carry by-ref-like {1} across suspension.",
        DiagnosticSeverity.Error, "Flow", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#by-ref-like-safety"));

    public static readonly DiagnosticDescriptor ByRefLikeCapture = Register(new(
        "ES2233", "By-ref-like value captured by a function literal",
        "By-ref-like value '{0}: {1}' cannot be captured by a function literal.",
        DiagnosticSeverity.Error, "Flow", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#by-ref-like-safety"));

    public static readonly DiagnosticDescriptor ByRefLikeBoxing = Register(new(
        "ES2234", "By-ref-like value cannot be boxed",
        "By-ref-like type '{0}' cannot be boxed as '{1}'.",
        DiagnosticSeverity.Error, "TypeSystem", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#by-ref-like-safety"));

    public static readonly DiagnosticDescriptor InvalidNumericConversion = Register(new(
        "ES2235", "Invalid numeric conversion", "{0}",
        DiagnosticSeverity.Error, "TypeSystem", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#contextual-numeric-literals-and-decimal"));

    public static readonly DiagnosticDescriptor NumericValueOutOfRange = Register(new(
        "ES2236", "Numeric value out of range", "Numeric literal '{0}' is out of range for {1}.",
        DiagnosticSeverity.Error, "TypeSystem", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#contextual-numeric-literals-and-decimal"));

    public static readonly DiagnosticDescriptor InvalidCompilerDirective = Register(new(
        "ES2237", "Invalid compiler directive", "{0}",
        DiagnosticSeverity.Error, "Parsing", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#floating-point-contract-and-contraction"));

    public static readonly DiagnosticDescriptor ConflictingCompilerDirective = Register(new(
        "ES2238", "Conflicting compiler directive", "Duplicate or conflicting '@floatMode' directive.",
        DiagnosticSeverity.Error, "Binding", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#floating-point-contract-and-contraction"));

    // ================================================================
    // ES3xxx — Codegen / struct rules
    // ================================================================

    /// async let initializer is not a call expression.
    /// Spec: concurrency.md §async-let
    public static readonly DiagnosticDescriptor AsyncLetNonCall = Register(new(
        "ES3005",
        "async let initializer must be a call",
        "'async let {0}' initializer must be a call to an async or awaitable function. " +
        "Non-call initializers are rejected; wrap in 'Task.Run(() => …)' to genuinely run concurrently.",
        DiagnosticSeverity.Error,
        "Codegen",
        "https://esharp-lang.vercel.app/diagnostics/ES3005"));

    /// init block on a value struct.
    /// Spec: declarations.md §struct / type-system.md §value-types / programs.md §Initialization
    public static readonly DiagnosticDescriptor InitOnStruct = Register(new(
        "ES3012",
        "init block on value struct",
        "'init' is not valid on the value struct '{0}'. " +
        "Construct a struct with a composite literal '{0} {{ … }}', " +
        "the positional form 'struct {0}(a, b)', or a factory function.",
        DiagnosticSeverity.Error,
        "Codegen",
        "https://esharp-lang.vercel.app/diagnostics/ES3012"));

    // ================================================================
    // ES8xxx — opt-in allocation and copy diagnostics
    // ================================================================

    public static readonly DiagnosticDescriptor LargeValueParameterCopy = Register(new(
        "ES8001", "Large value parameter copy",
        "Passing '{0}: {1}' by value copies {2} bytes; use 'readonly *{1}' and pass '&value' when aliasing is intended.",
        DiagnosticSeverity.Warning, "Performance", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#allocation-and-copy-diagnostics"));
    public static readonly DiagnosticDescriptor LargeValueReceiverCopy = Register(new(
        "ES8002", "Large value receiver copy",
        "Value receiver '{0}' snapshots {1} bytes; use a readonly receiver or 'readonly *{2}' when aliasing is intended.",
        DiagnosticSeverity.Warning, "Performance", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#allocation-and-copy-diagnostics"));
    public static readonly DiagnosticDescriptor LargeValueReturnCopy = Register(new(
        "ES8003", "Large value return copy",
        "Returning '{0}' copies {1} bytes; keep the value return when snapshot semantics are required.",
        DiagnosticSeverity.Warning, "Performance", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#allocation-and-copy-diagnostics"));
    public static readonly DiagnosticDescriptor BoxingAllocation = Register(new(
        "ES8004", "Boxing allocation",
        "Boxing '{0}' as '{1}' allocates an object.",
        DiagnosticSeverity.Warning, "Performance", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#allocation-and-copy-diagnostics"));
    public static readonly DiagnosticDescriptor ClosureAllocation = Register(new(
        "ES8005", "Closure allocation",
        "Capturing function literal allocates a display class for: {0}.",
        DiagnosticSeverity.Warning, "Performance", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#allocation-and-copy-diagnostics"));
    public static readonly DiagnosticDescriptor EnumeratorAllocation = Register(new(
        "ES8006", "Generic enumeration allocation",
        "Enumerating '{0}' uses the generic IEnumerable path and may allocate an enumerator; arrays use direct indexed lowering.",
        DiagnosticSeverity.Warning, "Performance", "https://esharp-lang.vercel.app/spec/numerics-and-performance/#allocation-and-copy-diagnostics"));
    public static readonly DiagnosticDescriptor PointerPromotionAllocation = Register(new(
        "ES8007", "Pointer promotion allocation",
        "{0}", DiagnosticSeverity.Warning, "Performance",
        "https://esharp-lang.vercel.app/spec/numerics-and-performance/#allocation-and-copy-diagnostics"));

    // ================================================================
    // ES9xxx — Workspace / infra
    // ================================================================

    /// Internal compiler error — unexpected exception.
    public static readonly DiagnosticDescriptor InternalError = Register(new(
        "ES9001",
        "Internal compiler error",
        "An unexpected error occurred in the compiler: {0}. This is a compiler bug — please file an issue.",
        DiagnosticSeverity.Error,
        "Workspace",
        "https://esharp-lang.vercel.app/diagnostics/ES9001"));

    /// Internal compiler error — a masked resolution failure. An entity (type, method,
    /// property, field, …) failed to resolve at a site while an entity of the SAME KIND
    /// and NAME is known to the symbol table. The failure is therefore not a user error
    /// (the name is not undefined) but a scope/context bug at the resolution site, hidden
    /// behind an ordinary "not found" message. Surfacing it as an ICE keeps a masked
    /// compiler bug self-identifying instead of sending the author hunting for a typo.
    public static readonly DiagnosticDescriptor MaskedResolutionIce = Register(new(
        "ES9501",
        "Internal compiler error (masked resolution)",
        "{0} '{1}' failed to resolve in {2} when a {0} '{1}' is known to exist. " +
        "This is a compiler bug (ICE) — the entity exists but a scope/context error hid it at this site.",
        DiagnosticSeverity.Error,
        "Workspace",
        "https://esharp-lang.vercel.app/diagnostics/ES9501"));

    /// Project model: referenced source file not found on disk.
    public static readonly DiagnosticDescriptor MissingSourceFile = Register(new(
        "ES9010",
        "Source file not found",
        "Source file '{0}' listed in the project could not be found on disk.",
        DiagnosticSeverity.Error,
        "Workspace",
        "https://esharp-lang.vercel.app/diagnostics/ES9010"));
}
