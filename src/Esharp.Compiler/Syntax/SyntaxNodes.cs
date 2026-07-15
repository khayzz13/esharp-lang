namespace Esharp.Syntax;

public abstract record SyntaxNode
{
    public SourceSpan Span { get; init; }

    /// The span of the node's identifier token — the NAME of a declaration
    /// (`data ▸Point◂ { … }`), the member of an access (`list.▸Add◂`), the bound
    /// name of a pattern. The parser stamps it where a name token is consumed;
    /// the semantic sink reports occurrences against it so tooling (hover, rename,
    /// semantic tokens) lands on the identifier, never the whole node. Falls back
    /// to <see cref="Span"/> when no name token was stamped (synthesized nodes).
    public SourceSpan NameSpan
    {
        get => _nameSpan.IsValid ? _nameSpan : Span;
        init => _nameSpan = value;
    }
    SourceSpan _nameSpan;
}

public abstract record MemberSyntax : SyntaxNode;

public abstract record StatementSyntax : SyntaxNode;

public abstract record ExpressionSyntax : SyntaxNode;

// Error-recovery placeholders. The parser produces these (with a diagnostic
// already reported) where no expression/member could be parsed, so the tree
// stays structurally complete for later phases and tooling. Diagnostics gate
// emission — the emitters never see them.
public sealed record ErrorExpressionSyntax : ExpressionSyntax;
public sealed record ErrorMemberSyntax : MemberSyntax;

// Alias (`using Baz = "Full.Type"`, C#-style) is non-null only for the alias form;
// `static` and namespace imports leave it null.
public sealed record UsingSyntax(bool IsStatic, string Path, string? Alias = null) : SyntaxNode;

public sealed record CompilationUnitSyntax(string? NamespaceName, IReadOnlyList<UsingSyntax> Imports, IReadOnlyList<MemberSyntax> Members) : SyntaxNode;

public sealed record FieldSyntax(string Name, TypeSyntax Type, bool? IsPublic = null, bool Mutable = true, ExpressionSyntax? DefaultValue = null, bool IsEmbedded = false, bool IsEvent = false, bool IsRequired = false, PropertyAccessorsSyntax? Property = null) : SyntaxNode;

// Property accessor set on a member. A member is a property iff this is non-null.
// `let` and `var` select this representation even without `{}`; `ComputedGetter`
// carries the `let x: T => expr` form, while stored forms use generated backing
// storage. A default expression initializes that storage rather than changing the
// declaration back into a field.
public sealed record PropertyAccessorsSyntax(
    bool HasGet,
    bool HasSet,
    bool HasInit,
    ExpressionSyntax? ComputedGetter = null,
    string? SetterParam = null,
    ExpressionSyntax? SetterBody = null,
    string? LocaStorageName = null,
    string? MutStorageName = null,
    BlockStatementSyntax? ScopedMutBody = null) : SyntaxNode;

// `name: Type [= default]` — the default must be a constant shape (ES2180) and is
// materialized at every call site that omits the argument.
public sealed record ParameterSyntax(string Name, TypeSyntax Type, bool IsOut = false, ExpressionSyntax? DefaultValue = null) : SyntaxNode;

public sealed record ChoiceCaseSyntax(string Name, IReadOnlyList<FieldSyntax> Payloads) : SyntaxNode;

public sealed record AttributeSyntax(string Name, string? Arguments) : SyntaxNode;

// The visibility of an emitted constructor: Default follows the type's pub/internal,
// `priv init` narrows to private, `protected init` to family (base-only construction).
public enum InitVisibility { Default, Private, Protected }

// init(args) [: this(args) | : base(args)] { body } — a class constructor.
// `ThisArguments` delegates to a sibling init (the dual of `: base`); a single
// init carries at most one of the two.
public sealed record InitDeclarationSyntax(
    IReadOnlyList<ParameterSyntax> Parameters,
    BlockStatementSyntax Body,
    IReadOnlyList<ExpressionSyntax>? BaseArguments = null,
    IReadOnlyList<ExpressionSyntax>? ThisArguments = null,
    InitVisibility Visibility = InitVisibility.Default) : SyntaxNode;

// Only parsed at the direct level of a class `init` body.  DeclarationParser
// hoists it into the enclosing type's real field list and rewrites the statement
// to `self.name = initializer`, keeping field identity available before binding.
public sealed record InitFieldDeclarationStatementSyntax(
    bool? IsPublic, string Name, TypeSyntax Type, ExpressionSyntax Initializer) : StatementSyntax;

// `yield &working` within a property `mut { ... }` accessor.  This is not an
// async-stream element: scoped-mut lowering owns its lend/resume lifetime.
public sealed record MutYieldStatementSyntax(ExpressionSyntax Location) : StatementSyntax;

// Namespace `init { ... }` — a synchronous, once-only initializer for the
// namespace host class. This is deliberately distinct from class `init(...)`.
public sealed record NamespaceInitDeclarationSyntax(BlockStatementSyntax Body) : MemberSyntax;

// Namespace host storage: bare `name: T`, or property-shaped `let` / `var`.
public sealed record NamespaceStateDeclarationSyntax(
    bool IsPublic, bool Mutable, string Name, TypeSyntax Type, ExpressionSyntax? Initializer,
    PropertyAccessorsSyntax? Property = null) : MemberSyntax
{
    public Visibility Visibility { get; init; } = IsPublic ? Visibility.Public : Visibility.Internal;
}

// `returns Type` clause inside a class or static body — default return
// type for nested `func` declarations that omit `-> Type`.
public sealed record ReturnsClauseSyntax(TypeSyntax Type) : SyntaxNode;

public enum ClassModifier { Sealed, Abstract, Open }

/// Declared visibility of a type. `pub` → Public; `internal` (or the absence of a
/// modifier at top level) → Internal; `priv` → Private. At top level Internal and
/// Private both emit as CLR not-public (the CLR has no private top-level type); on a
/// NESTED type all three are distinct — NestedPublic / NestedAssembly / NestedPrivate,
/// where the no-modifier default tightens to Private (the C# nested default).
public enum Visibility { Public, Internal, Private }

public enum FunctionModifier { None, Virtual, Abstract, InheritColon }

// Go-style method receiver: `func (c: *Circle) scale(...)`. Binds the function to a
// type at namespace scope, lifting the receiver out of the parameter list so the
// receiver kind — value, `*T` pointer, or `readonly` (in-this) — is visible at the
// declaration. The receiver is NOT carried in `Parameters`; the binder synthesizes
// it as the leading `self` parameter so the downstream instance-method machinery
// (Parameters[0]-as-receiver, `selfParamName`, the `.Skip(1)` emit) is reused.
//   `func (c: *T) m()`        → IsReadonly=false, Type is a PointerTypeSyntax  (mutates, ref this)
//   `func (c: T) m()`         → IsReadonly=false, Type is a value type        (snapshot copy on a struct)
//   `readonly func (c: T) m()`→ IsReadonly=true                               (in this, no copy/mutation)
// `func (x: static Foo) f()` selects Foo's static facet.  It is deliberately
// receiver-only syntax: a static facet is a member surface, never a value type.
public sealed record ReceiverSyntax(string Name, TypeSyntax Type, bool IsReadonly = false, bool IsStaticFacet = false) : SyntaxNode;

public sealed record DataDeclarationSyntax(
    bool IsRef,
    bool IsPublic,
    bool IsReadonly,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<TypeSyntax> Interfaces,
    IReadOnlyList<FieldSyntax> Fields,
    IReadOnlyList<AttributeSyntax> Attributes,
    IReadOnlyList<InitDeclarationSyntax>? Inits = null,
    IReadOnlyList<FunctionDeclarationSyntax>? Methods = null,
    ReturnsClauseSyntax? DefaultReturns = null,
    ClassModifier Modifier = ClassModifier.Sealed,
    // `#derive equality, debug` traits, kept distinct from the base/interface list
    // (they used to ride `Interfaces` as `#`-prefixed strings).
    IReadOnlyList<string>? DeriveTraits = null,
    // `class Foo(a: T, b: U = d)` — the primary-constructor capture header. The
    // params are NOT fields: one referenced in an in-body method becomes a
    // synthesized private `let` capture field; one used only in `init { }` /
    // field defaults stays a ctor-local. `data` never carries a header (its
    // positional form synthesizes real fields instead).
    IReadOnlyList<ParameterSyntax>? HeaderParameters = null,
    // True for the positional `data Vec2(x: int, y: int)` form: the fields were
    // synthesized from the parameter list (in order) — the type destructures via
    // a synthesized `Deconstruct` and `let (a, b) = v`.
    bool IsPositional = false,
    // Nested type declarations in the body — `class Outer { struct Inner { ... } }`.
    // Each is an ordinary type declaration whose CLR identity is nested under this
    // type (metadata `Outer/Inner`); registered with declaring-type context so the
    // emitter places it in the parent's NestedTypes.
    IReadOnlyList<MemberSyntax>? NestedTypes = null) : MemberSyntax
{
    /// Declared visibility; `IsPublic` is the legacy boolean face (== Public).
    public Visibility Visibility { get; init; } = IsPublic ? Visibility.Public : Visibility.Internal;
}

// static Foo { let X = ..., var Y = ..., returns T, func bar() ... }
public sealed record StaticFuncDeclarationSyntax(
    bool IsPublic,
    string Name,
    IReadOnlyList<FieldSyntax> Fields,
    IReadOnlyList<FunctionDeclarationSyntax> Functions,
    ReturnsClauseSyntax? DefaultReturns = null,
    // Nested type declarations in the body — `static Host { enum Kind { ... } }`.
    IReadOnlyList<MemberSyntax>? NestedTypes = null,
    IReadOnlyList<string>? TypeParameters = null) : MemberSyntax
{
    public Visibility Visibility { get; init; } = IsPublic ? Visibility.Public : Visibility.Internal;
    public IReadOnlyList<string> GenericParameters => TypeParameters ?? [];
}

// protocol IGreeter { func greet(name: string) -> string }
public sealed record InterfaceMethodSyntax(string Name, IReadOnlyList<ParameterSyntax> Parameters, TypeSyntax ReturnType) : SyntaxNode;
// Interface property requirement: `let area: T { get }` / `var name: T { get set }` /
// `required let id: T { get init }`. The accessor words inside the block — not the
// `let`/`var` keyword — fix the minimum set: `{ get }` requires a getter, `{ get set }`
// a getter and a settable setter, `{ get init }` a getter and an init-only setter (one
// carrying `modreq(IsExternalInit)`), and `loca` requires durable property identity.
// An implementer may offer more (a `var x { }`
// satisfies a `let x { get }`); a plain field satisfies it through synthesized accessors.
public sealed record InterfacePropertySyntax(
    string Name, TypeSyntax Type, bool HasGet, bool HasSet, bool HasInit, bool HasLoca = false) : SyntaxNode;
public sealed record InterfaceDeclarationSyntax(bool IsPublic, string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<InterfaceMethodSyntax> Methods, IReadOnlyList<FieldSyntax>? Events = null, IReadOnlyList<InterfacePropertySyntax>? Properties = null) : MemberSyntax
{
    public Visibility Visibility { get; init; } = IsPublic ? Visibility.Public : Visibility.Internal;
}

// const NAME[: Type] = literalExpr  — compile-time constant, no storage.
// Lives at namespace level OR inside a class body OR inside a static func
// body. Inside a function body it's a statement (see ConstStatementSyntax).
public sealed record ConstDeclarationSyntax(bool IsPublic, string Name, TypeSyntax? Type, ExpressionSyntax Value) : MemberSyntax;

// #derive equality, debug
public sealed record DeriveDirectiveSyntax(IReadOnlyList<string> Traits) : SyntaxNode;

public sealed record ChoiceDeclarationSyntax(bool IsRef, bool IsPublic, string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<ChoiceCaseSyntax> Cases) : MemberSyntax
{
    public Visibility Visibility { get; init; } = IsPublic ? Visibility.Public : Visibility.Internal;
}

public sealed record FunctionDeclarationSyntax(
    bool IsPublic,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<ParameterSyntax> Parameters,
    TypeSyntax ReturnType,
    BlockStatementSyntax Body,
    IReadOnlyList<AttributeSyntax> Attributes,
    bool HasExplicitReturnType = true,
    FunctionModifier Modifier = FunctionModifier.None,
    bool IsTaskFunc = false,
    // Explicit interface implementation: `func IFace.method(...)`. Names the single
    // interface slot this member fills; emitted private/final/virtual with one MethodImpl.
    TypeSyntax? ExplicitInterface = null,
    // Go-style method receiver block — `func (c: *Circle) scale(...)`. Non-null marks
    // this as an attached method (vs a plain free function). The receiver is bound as
    // the synthesized leading parameter; this never coexists with ExplicitInterface
    // (one is namespace-scope attachment, the other type-body interface impl).
    ReceiverSyntax? Receiver = null,
    // True for a method declared INSIDE a type body (`class Foo { func bar() {} }`).
    // The parser hoists it to namespace scope with a synthesized `self` first parameter;
    // this flag keeps it a method (attached, byref `self`, no value-snapshot) under the
    // explicit-attachment model, distinct from a bare free function with a data first param.
    bool IsTypeBodyMethod = false) : MemberSyntax;

// delegate func Name(params) -> R  — a nominal CLR delegate type: a sealed class
// deriving from System.MulticastDelegate, defined by its Invoke signature.
// Signature-only (no body), top-level member sibling to data/enum/choice/interface.
// Nominally distinct from any same-shaped Func/Action (a Func<int,int> is not a BinOp).
public sealed record DelegateDeclarationSyntax(
    bool IsPublic,
    string Name,
    IReadOnlyList<ParameterSyntax> Parameters,
    TypeSyntax ReturnType) : MemberSyntax
{
    public Visibility Visibility { get; init; } = IsPublic ? Visibility.Public : Visibility.Internal;
}

public sealed record BlockStatementSyntax(IReadOnlyList<StatementSyntax> Statements) : StatementSyntax;

/// <summary>
/// The storage/borrowing representation requested by a local declaration.  This is
/// deliberately independent from mutability: `name: T = value` is mutable but a
/// plain value binding, while `var` is mutable compiler-managed location storage.
/// </summary>
public enum LocalRepresentation
{
    Default,
    BareTypedValue,
    ReadonlyLocation,
    MutableLocation,
}

public sealed record VariableDeclarationStatementSyntax(
    bool Mutable,
    string Name,
    TypeSyntax? ExplicitType,
    ExpressionSyntax Initializer,
    LocalRepresentation Representation = LocalRepresentation.Default) : StatementSyntax;

public sealed record IfStatementSyntax(
    ExpressionSyntax Condition,
    StatementSyntax ThenStatement,
    StatementSyntax? ElseStatement) : StatementSyntax;

public sealed record ReturnStatementSyntax(ExpressionSyntax? Expression) : StatementSyntax;
// raise EventName(args) — fire a field-style event. Lowers to a null-safe
// capture-then-invoke of the event's backing delegate.
public sealed record RaiseStatementSyntax(string EventName, IReadOnlyList<ExpressionSyntax> Arguments) : StatementSyntax;
public sealed record YieldStatementSyntax(ExpressionSyntax Value) : StatementSyntax;
public sealed record BreakStatementSyntax() : StatementSyntax;
public sealed record ContinueStatementSyntax() : StatementSyntax;
// Function-body compile-time constant. Binds a name to a literal value that
// the binder inlines at every reference site — no IL slot is allocated, the
// scope just sees `name` as the literal.
public sealed record ConstStatementSyntax(string Name, TypeSyntax? Type, ExpressionSyntax Value) : StatementSyntax;

public sealed record ExpressionStatementSyntax(ExpressionSyntax Expression) : StatementSyntax;

public sealed record AssignmentStatementSyntax(ExpressionSyntax Target, ExpressionSyntax Expression) : StatementSyntax;
public sealed record CompoundAssignmentStatementSyntax(ExpressionSyntax Target, SyntaxTokenKind Operator, ExpressionSyntax Value) : StatementSyntax;

public sealed record WhileStatementSyntax(ExpressionSyntax Condition, StatementSyntax Body) : StatementSyntax;

public sealed record ForEachStatementSyntax(string Identifier, ExpressionSyntax Collection, StatementSyntax Body, IReadOnlyList<string>? DestructuredNames = null, IReadOnlyList<SourceSpan>? DestructuredNameSpans = null, bool IsAwait = false) : StatementSyntax;

/// A literal. For string literals, <see cref="Value"/> is the decoded template (escapes
/// applied, raw strings indent-normalized) and <see cref="SuppressInterpolation"/> is set
/// for bare `"""` raw strings, whose braces stay verbatim (no holes).
public sealed record LiteralExpressionSyntax(object? Value, string Text) : ExpressionSyntax
{
    public bool SuppressInterpolation { get; init; }
}

public sealed record NameExpressionSyntax(string Name) : ExpressionSyntax;

public sealed record UnaryExpressionSyntax(SyntaxTokenKind OperatorKind, ExpressionSyntax Operand) : ExpressionSyntax;

public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxTokenKind OperatorKind,
    ExpressionSyntax Right) : ExpressionSyntax;

public sealed record MemberAccessExpressionSyntax(ExpressionSyntax Target, string MemberName) : ExpressionSyntax;

// operand is T  /  operand is not T  — a runtime type test yielding bool (and, in a
// guard region, smart-casting `operand` to T). `Negated` is the `is not` form.
public sealed record TypeTestExpressionSyntax(ExpressionSyntax Operand, TypeSyntax Type, bool Negated) : ExpressionSyntax;

// operand as T  (safe cast → T?, never throws)  /  operand as! T  (asserting cast → T,
// throws on miss). `Asserting` is the `as!` form.
public sealed record CastExpressionSyntax(ExpressionSyntax Operand, TypeSyntax Type, bool Asserting) : ExpressionSyntax;

// `ArgumentNames[i]` is the label of a named argument (`f(x, port: 9090)`), null for a
// positional one; the whole list is null when every argument is positional. Names are
// resolved away in BindCall — the bound call carries arguments in parameter order.
public sealed record CallExpressionSyntax(ExpressionSyntax Target, IReadOnlyList<ExpressionSyntax> Arguments, IReadOnlyList<TypeSyntax>? TypeArguments = null, IReadOnlyList<string?>? ArgumentNames = null) : ExpressionSyntax;

public sealed record ObjectInitializerFieldSyntax(string Name, ExpressionSyntax Value) : SyntaxNode;

public sealed record ObjectCreationExpressionSyntax(
    TypeSyntax Type,
    IReadOnlyList<ObjectInitializerFieldSyntax> Fields) : ExpressionSyntax;

public sealed record ParenthesizedExpressionSyntax(ExpressionSyntax Expression) : ExpressionSyntax;

// cond ? then : else
public sealed record ConditionalExpressionSyntax(ExpressionSyntax Condition, ExpressionSyntax Consequence, ExpressionSyntax Alternative) : ExpressionSyntax;

// left ?? right
public sealed record NullCoalescingExpressionSyntax(ExpressionSyntax Left, ExpressionSyntax Right) : ExpressionSyntax;

// target?.member
public sealed record NullConditionalAccessExpressionSyntax(ExpressionSyntax Target, string MemberName) : ExpressionSyntax;

// [e1, e2, ...]
public sealed record ListLiteralExpressionSyntax(IReadOnlyList<ExpressionSyntax> Elements) : ExpressionSyntax;

// T[](n) — fixed-size array creation (newarr). Element type + size expression.
public sealed record ArrayCreationExpressionSyntax(TypeSyntax ElementType, ExpressionSyntax Size) : ExpressionSyntax;

// (e1, e2, ...)
public sealed record TupleExpressionSyntax(IReadOnlyList<ExpressionSyntax> Elements) : ExpressionSyntax;

public sealed record SpawnExpressionSyntax(BlockStatementSyntax Body) : ExpressionSyntax;

// let x = expr else { body }
public sealed record LetGuardStatementSyntax(string Name, TypeSyntax? ExplicitType, ExpressionSyntax Initializer, BlockStatementSyntax ElseBody) : StatementSyntax;

// match (expr: TypeName) { arms }  or  match expr { arms }
//
// A pattern is one of: a `.case(bindings)` case pattern, a `literal`, `default`, the
// `nil` arm (`IsNil`), or a type pattern `(name: T)` — `TypeBindingName` + `TypeBindingType`,
// which `is T` + binds the narrowed value (§type-narrowing-and-downcasting).
public sealed record MatchPatternSyntax(
    string? CaseName,
    IReadOnlyList<string>? BindingNames,
    bool IsDefault,
    ExpressionSyntax? LiteralValue = null,
    string? TypeBindingName = null,
    TypeSyntax? TypeBindingType = null,
    bool IsNil = false,
    // The identifier spans of `BindingNames`, parallel by index — the occurrence
    // spans case-payload bindings declare at (`NameSpan` carries the case name /
    // type-pattern binding instead).
    IReadOnlyList<SourceSpan>? BindingNameSpans = null) : SyntaxNode;

// An arm is `Pattern [if Guard] (Body | => ExprBody)`. Exactly one of `Body` (a block
// arm) or `ExprBody` (a `=>` expression arm) is non-null; `Guard` refines the arm.
public sealed record MatchArmSyntax(
    MatchPatternSyntax Pattern,
    BlockStatementSyntax? Body,
    ExpressionSyntax? Guard = null,
    ExpressionSyntax? ExprBody = null) : SyntaxNode;
public sealed record MatchStatementSyntax(ExpressionSyntax Subject, TypeSyntax? SubjectType, IReadOnlyList<MatchArmSyntax> Arms) : StatementSyntax;

// match expr { arms } in expression position
public sealed record MatchExpressionSyntax(ExpressionSyntax Subject, TypeSyntax? SubjectType, IReadOnlyList<MatchArmSyntax> Arms) : ExpressionSyntax;

// `if cond { … } else if … { … } else { … }` in EXPRESSION position — the value is the
// taken branch's value, and a branch body's value is its trailing expression. An `else` is
// required (the value must be total). Statement-position `if` stays IfStatementSyntax; this
// node is produced only where an expression is expected.
public sealed record IfExpressionSyntax(
    IReadOnlyList<IfExpressionBranchSyntax> Branches,
    BlockStatementSyntax? ElseBody) : ExpressionSyntax;
public sealed record IfExpressionBranchSyntax(ExpressionSyntax Condition, BlockStatementSyntax Body) : SyntaxNode;

// enum Direction { north, south, east, west }
// enum Priority { low = 1, medium = 5, high = 10 }
public sealed record EnumCaseSyntax(string Name, int? ExplicitValue) : SyntaxNode;
public sealed record EnumDeclarationSyntax(bool IsPublic, string Name, IReadOnlyList<EnumCaseSyntax> Cases) : MemberSyntax
{
    public Visibility Visibility { get; init; } = IsPublic ? Visibility.Public : Visibility.Internal;
}

// chan<AuditEvent>(256)
public sealed record ChanCreationExpressionSyntax(TypeSyntax ElementType, ExpressionSyntax? Capacity) : ExpressionSyntax;

// .invalidCredentials  or  .accountLocked(args...)
public sealed record DotCaseExpressionSyntax(string CaseName, IReadOnlyList<ExpressionSyntax> Arguments) : ExpressionSyntax;

// defer { body }
public sealed record DeferStatementSyntax(BlockStatementSyntax Body) : StatementSyntax;

// func(a: int, b: int) -> bool { return a < b }
public sealed record FunctionLiteralExpressionSyntax(
    IReadOnlyList<ParameterSyntax> Parameters,
    TypeSyntax ReturnType,
    BlockStatementSyntax Body) : ExpressionSyntax;

// &expr — address-of: function pointer (&funcName) or variable address (&varName, &s.field)
public sealed record AddressOfExpressionSyntax(ExpressionSyntax Target) : ExpressionSyntax;

// new T { ... } / new T(args) — the sole fresh-heap-allocation form: construct a
// value `data` and place it on the heap, yielding a `*T`. Distinct from `&`
// (address-of something that already exists); `new` allocates something that does
// not. Target is the construction expression (object literal or positional call).
public sealed record NewExpressionSyntax(ExpressionSyntax Target) : ExpressionSyntax;

// default(T) — zero-initialized value of the given type. A bare `default` (no parens)
// carries a null Type and is target-typed at bind time from the expected type (parameter
// default, return, annotated `let`, assignment target).
public sealed record DefaultExpressionSyntax(TypeSyntax? Type) : ExpressionSyntax;

// out var name — declares a local at the call site, passed by address
// out name      — references an existing local, passed by address
// DeclaresLocal is true for `out var` form.
public sealed record OutArgumentExpressionSyntax(string Name, bool DeclaresLocal) : ExpressionSyntax;

// try { body } catch (T e) { catch_body } catch { ... }
public sealed record TryStatementSyntax(BlockStatementSyntax Body, IReadOnlyList<CatchClauseSyntax> Catches) : StatementSyntax;
// `catch (e: T) if cond { … }` — the optional `Guard` is a CLR exception filter:
// the clause matches only when the type matches AND the guard holds, otherwise control
// falls to the next clause.
public sealed record CatchClauseSyntax(TypeSyntax? ExceptionType, string? BindingName, BlockStatementSyntax Body, ExpressionSyntax? Guard = null) : SyntaxNode;

// throw expr — raise an exception
public sealed record ThrowStatementSyntax(ExpressionSyntax? Expression) : StatementSyntax;

// items[expr]
public sealed record IndexExpressionSyntax(ExpressionSyntax Target, ExpressionSyntax Index) : ExpressionSyntax;

// items[start..end], items[..end], items[start..]   -- Target = the indexable.
// Standalone integer range `start..end` (e.g. `for i in 0..n`) leaves Target null.
public sealed record RangeExpressionSyntax(ExpressionSyntax? Target, ExpressionSyntax? Start, ExpressionSyntax? End) : ExpressionSyntax;

// expr?  — error propagation / result unwrap
public sealed record TryUnwrapExpressionSyntax(ExpressionSyntax Inner) : ExpressionSyntax;

// expr with { field: value, ... } — non-destructive struct update
public sealed record WithExpressionSyntax(ExpressionSyntax Target, IReadOnlyList<ObjectInitializerFieldSyntax> Fields) : ExpressionSyntax;

// await expr — async suspension
public sealed record AwaitExpressionSyntax(ExpressionSyntax Inner) : ExpressionSyntax;

// async let name = expr — structured concurrent binding
public sealed record AsyncLetStatementSyntax(string Name, TypeSyntax? ExplicitType, ExpressionSyntax Initializer) : StatementSyntax;

// select { .recv(binding, channel) { body } ... }
// Member order mirrors Esharp.Stdlib.ChanSelect.Kind — the IL emitter maps
// arm kinds onto the runtime enum's values.
public enum SelectArmKind { Recv, Send, Timeout, Default }
public sealed record SelectArmSyntax(SelectArmKind Kind, string? Binding, ExpressionSyntax? Channel, ExpressionSyntax? Value, BlockStatementSyntax Body) : SyntaxNode;
public sealed record SelectStatementSyntax(IReadOnlyList<SelectArmSyntax> Arms) : StatementSyntax;
