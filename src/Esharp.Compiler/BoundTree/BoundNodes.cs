// ============================================================================
// Esharp.BoundTree — the typed IR all compiler phases pass.
//
// CORE nodes survive lowering and reach CodeGen.
// FEATURE nodes must be eliminated by a Lowering pass before CodeGen.
// CodeGen opens with an assertion: zero FEATURE nodes present.
//
// See contract-spine.md §1 for the full CORE/FEATURE table and lowering owners.
// ============================================================================

using Esharp.Syntax;
using Esharp.Symbols;

namespace Esharp.BoundTree;

public enum DataClassification { Struct, Class }

// === Dispatch root ===

/// Common ancestor for BoundMember, BoundStatement, and BoundExpression.
/// Allows BoundTreeVisitor.VisitNode(BoundNode) to dispatch over the full tree.
/// Must be a record: the derived bound nodes are records, and a record may only
/// inherit from object or another record (CS8864).
public abstract record BoundNode;

// === Top-level ===

public sealed partial record BoundUsing(bool IsStatic, string Path, string? Alias = null);
public sealed partial record BoundCompilationUnit(string? NamespaceName, IReadOnlyList<BoundUsing> Imports, IReadOnlyList<BoundMember> Members) : BoundNode;

// === Members ===

public abstract record BoundMember : BoundNode
{
    public SourceSpan Span { get; init; }
}

// A class constructor. `ThisArguments` delegates to the sibling init at
// `DelegatesTo` (resolved by the binder; -1 = none); a delegating ctor runs no
// field defaults and calls no base ctor — the delegate target does both.
public sealed partial record BoundInitDeclaration(
    IReadOnlyList<BoundParameter> Parameters,
    BoundBlockStatement Body,
    IReadOnlyList<BoundExpression>? BaseArguments = null,
    IReadOnlyList<BoundExpression>? ThisArguments = null,
    int DelegatesTo = -1,
    Esharp.Syntax.InitVisibility Visibility = Esharp.Syntax.InitVisibility.Default,
    // True for the synthesized primary ctor of a headered class: its Parameters
    // are the capture header, its Body is the `init { }` epilogue (or empty), and
    // emission stores each captured param into its synthesized field right after
    // the base call, before field defaults run.
    bool IsPrimary = false) : BoundMember;

// Synchronous initializer for one namespace host. CodeGen emits the body in the
// host's `.cctor`; normal lowering still traverses it like every other body.
public sealed partial record BoundNamespaceInitDeclaration(BoundBlockStatement Body) : BoundMember;
public sealed partial record BoundNamespaceStateDeclaration(
    BoundField Field,
    BoundExpression? ComputedGetter = null,
    string? SetterParam = null,
    BoundExpression? SetterBody = null) : BoundMember;

public enum InheritanceRole { None, Virtual, Abstract, Fulfill, Override, PassThrough, ReAbstract }

public sealed partial record BoundDataDeclaration(
    bool IsPublic, bool IsReadonly, string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<string> DeriveTraits, IReadOnlyList<BoundField> Fields,
    IReadOnlyList<BoundFunctionDeclaration> InstanceMethods,
    DataClassification Classification, IReadOnlyList<string> Attributes,
    IReadOnlyList<BoundInitDeclaration>? Inits = null, string? DeclaringNamespace = null,
    // Conformances the `*T` wrapper (not the value type) implements — structured,
    // symbol-linked. The Go pointer-receiver case: only the pointer method set
    // satisfies the protocol.
    IReadOnlyList<BoundType>? PointerInterfaceTypes = null, bool IsUserRef = false,
    ClassModifier Modifier = ClassModifier.Sealed, string? BaseClass = null,
    // The value type's conformances, structured and symbol-linked. Identity flows
    // through the BoundType (and its TypeSymbol) — the IL emitter resolves CLR
    // interfaces from THESE, the transpiler renders the `: IFoo` clause off their
    // display name. No string conformance list survives on the bound node.
    IReadOnlyList<BoundType>? InterfaceTypes = null,
    // Header params captured on use by an in-body method (header order). Each has
    // a synthesized private `let` BoundField of the same name appended to Fields;
    // the primary ctor stores the matching argument into it.
    IReadOnlyList<string>? CapturedHeaderParams = null,
    // Positional `data Vec2(x, y)`: fields are the positional params in order;
    // the emitter synthesizes `void Deconstruct(out T1, ...)` and the binder
    // rewrites `let (a, b) = v` ItemN reads to the positional fields.
    bool IsPositionalData = false,
    // NESTED type: the emit key (MetadataTypeName) of the enclosing type. When set,
    // the emitter builds this type's shell normally then re-parents it into the
    // enclosing type's Cecil NestedTypes with nested visibility. Null for top-level.
    string? DeclaringTypeKey = null) : BoundMember
{
    /// Declared visibility — drives nested-type emission (NestedPublic / NestedAssembly
    /// / NestedPrivate). `IsPublic` stays the top-level public-vs-not face.
    public Syntax.Visibility Visibility { get; init; } = IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal;
}

public sealed partial record BoundInterfaceDeclaration(bool IsPublic, string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<BoundInterfaceMethod> Methods, IReadOnlyList<BoundField>? Events = null, string? DeclaringTypeKey = null, IReadOnlyList<BoundInterfaceProperty>? Properties = null) : BoundMember
{
    public Syntax.Visibility Visibility { get; init; } = IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal;
}

// A nominal delegate type declaration. The emitter turns this into a sealed
// MulticastDelegate subclass: a bodyless runtime `.ctor(object, native int)` and a
// runtime `Invoke(Parameters) -> ReturnType`. Parameters carry by-ref / out intent
// exactly like a function's so the Invoke signature is CLR-faithful.
public sealed partial record BoundDelegateDeclaration(
    bool IsPublic, string Name, string? Namespace,
    IReadOnlyList<BoundParameter> Parameters, BoundType ReturnType,
    string? DeclaringTypeKey = null) : BoundMember
{
    public Syntax.Visibility Visibility { get; init; } = IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal;
}

// Top-level / type-scoped compile-time constant. Emitted as a CLR `literal`
// field on the namespace module class (when declared at namespace level), on
// the type (when declared inside a `class`), or on the static-func host
// (when declared inside a `static func` block). Every E#-side reference is
// already folded to a literal by the binder, so the IL field is purely for
// C# interop.
public sealed partial record BoundConstDeclaration(bool IsPublic, string Name, BoundType Type, BoundExpression Value) : BoundMember;
public sealed partial record BoundInterfaceMethod(string Name, IReadOnlyList<BoundParameter> Parameters, BoundType ReturnType);
// An interface property requirement (`let x: T { get }` / `var x: T { get set }` /
// `required let x: T { get init }`) — the minimum accessor/location set an implementer
// must expose; emitted as abstract get_/set_/getloca_ slots (the init setter carries
// modreq IsExternalInit).
public sealed partial record BoundInterfaceProperty(
    string Name, BoundType Type, bool HasGet, bool HasSet, bool HasInit, bool HasLoca = false);
public sealed partial record BoundChoiceDeclaration(bool IsPublic, bool IsRef, string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<BoundChoiceCase> Cases, string? DeclaringTypeKey = null) : BoundMember
{
    public Syntax.Visibility Visibility { get; init; } = IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal;
}
public sealed partial record BoundEnumCase(string Name, int Value);
public sealed partial record BoundEnumDeclaration(bool IsPublic, string Name, IReadOnlyList<BoundEnumCase> Cases, string? DeclaringTypeKey = null) : BoundMember
{
    public Syntax.Visibility Visibility { get; init; } = IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal;
}
// The CLR shape an async function's emitted state machine takes. Async stays
// uncolored — `await`/`yield` are the only signals — but the declared return type
// selects the wrapper: ValueTask<T> (default), Task/Task<T>, async `void` (explicit
// `-> void`, for event handlers), or IAsyncEnumerable<T> (an async stream, `yield`).
// In every case `ReturnType` is the unwrapped result/element type.
public enum AsyncReturnShape { ValueTask, Task, Void, AsyncEnumerable }

public sealed partial record BoundFunctionDeclaration(
    bool IsPublic, string Name, IReadOnlyList<string> TypeParameters,
    IReadOnlyList<BoundParameter> Parameters, BoundType ReturnType,
    BoundBlockStatement Body, IReadOnlyList<string> Attributes,
    bool HasAwait = false,
    InheritanceRole InheritanceRole = InheritanceRole.None,
    bool IsTaskFunc = false,
    AsyncReturnShape AsyncShape = AsyncReturnShape.ValueTask,
    string? ExplicitInterface = null,
    // ExplicitInterface resolved to a BoundType at bind time — the IL emitter's
    // override-target resolution consumes this, never the name string.
    BoundType? ExplicitInterfaceType = null,
    // Go-style receiver attachment. When not None, Parameters[0] is the synthesized
    // receiver (`self`): Value → snapshot copy (struct) / reference (class); Pointer →
    // mutating `ref this`; ReadonlyValue → `in this`, no copy/mutation. Drives both the
    // method-set sort (value/readonly ∈ T and *T; pointer ∈ *T only) and the emit of
    // the receiver as `this`.
    Esharp.Symbols.ReceiverKind ReceiverKind = Esharp.Symbols.ReceiverKind.None,
    // True for a type-body inline method (`class Foo { func bar() {} }`) hoisted with a
    // synthesized `self`. Keeps the old method semantics — attached, byref `self`, no
    // value-snapshot — distinct from an explicit receiver block.
    bool IsTypeBodyMethod = false) : BoundMember
{
    /// The interned spine symbol for this function (free / static-func member /
    /// promoted). The reference-identity bridge the IL emitter keys its
    /// definition map on, so a call's `ResolvedMethod` recovers this same Cecil
    /// method without a name walk. Null for synthesized forwarders without a
    /// source declaration.
    public Esharp.Symbols.MethodSymbol? Symbol { get; init; }

    /// The compiler-generated state-machine type for an async entry method. This
    /// is lowering metadata, not source syntax: CodeGen uses it to emit the CLR
    /// AsyncStateMachineAttribute with the exact generated type token.
    public BoundType? AsyncStateMachineType { get; init; }
}

public sealed partial record BoundStaticFuncDeclaration(
    bool IsPublic, string Name,
    IReadOnlyList<BoundField> Fields,
    IReadOnlyList<BoundFunctionDeclaration> Functions,
    string? DeclaringTypeKey = null) : BoundMember
{
    public Syntax.Visibility Visibility { get; init; } = IsPublic ? Syntax.Visibility.Public : Syntax.Visibility.Internal;
}

// === Statements ===

public abstract record BoundStatement : BoundNode
{
    public SourceSpan Span { get; set; }
}

// ---- CORE statements (survive lowering, reach CodeGen) ----
public sealed partial record BoundBlockStatement(IReadOnlyList<BoundStatement> Statements) : BoundStatement;
public sealed partial record BoundVariableDeclaration(bool Mutable, string Name, BoundType DeclaredType, BoundExpression Initializer) : BoundStatement
{
    // A `let x = uncoloredAsync()` in a synchronous function is source-typed as
    // the eventual value, but its declaration initially stores the ValueTask<T>.
    // SyncFutureLowering replaces it with that future slot and injects the blocking
    // GetAwaiter().GetResult() join at x's first use.
    public bool IsSyncFuture { get; init; }
    /// The interned local symbol this declaration introduces — the SAME instance
    /// <c>BinderScope.DeclareLocal</c> registered and reported to the semantic sink.
    /// Carried so CodeGen recovers the slot's identity (mutability, by-ref / out,
    /// declaring function) from the spine instead of re-deriving it from <c>Name</c>,
    /// and so rename / find-references resolves declaration↔use by reference identity.
    /// Null only for synthesized declarations with no spine identity.
    public Esharp.Symbols.LocalSymbol? Local { get; init; }
}
public sealed partial record BoundAssignment(BoundExpression Target, BoundExpression Value) : BoundStatement;
public sealed partial record BoundIfStatement(BoundExpression Condition, BoundStatement Then, BoundStatement? Else) : BoundStatement;
public sealed partial record BoundWhileStatement(BoundExpression Condition, BoundStatement Body) : BoundStatement;
public sealed partial record BoundReturnStatement(BoundExpression? Expression) : BoundStatement;
public sealed partial record BoundBreakStatement() : BoundStatement;
public sealed partial record BoundContinueStatement() : BoundStatement;
public sealed partial record BoundExpressionStatement(BoundExpression Expression) : BoundStatement;

// A call borrowing a scoped `mut` property. CodeGen emits the declaring
// property's generated begin/lease/resume protocol, keeping private setup and
// cleanup inside the declaring class while the borrower sees only `ref lease.Value`.
public sealed partial record BoundScopedMutCall(
    BoundExpression Receiver,
    BoundCallExpression Call,
    int ArgumentIndex,
    Esharp.Symbols.FieldSymbol Property,
    string? ResultLocal = null) : BoundStatement;

// goto/label [CORE] — unstructured control flow. State-machine dispatch and resume jumps
// are intra-region branches; an async return originating inside a nested protected region
// sets ExitsProtectedRegion so CodeGen emits `leave` and runs every intervening finally.
// `BoundLabelStatement` marks the branch target. The structured nodes (if/while) remain
// the surface forms; these are a lowering-only primitive.
public sealed partial record BoundLabelStatement(string Name) : BoundStatement;
public sealed partial record BoundGotoStatement(string Target) : BoundStatement
{
    public bool ExitsProtectedRegion { get; init; }
}

// try { body } catch (T e) { ... }  [CORE]
public sealed partial record BoundTryStatement(BoundBlockStatement Body, IReadOnlyList<BoundCatchClause> Catches) : BoundStatement;
// IsFinally: when true, this clause is emitted as a .finally handler (DeferLowering synthesizes these).
// ExceptionType and Guard must be null when IsFinally is true.
public sealed partial record BoundCatchClause(BoundType? ExceptionType, string? BindingName, BoundBlockStatement Body, BoundExpression? Guard = null, bool IsFinally = false);

// throw expr — raise exception, or bare throw in catch (rethrow)  [CORE]
public sealed partial record BoundThrowStatement(BoundExpression? Expression) : BoundStatement;

// Function-body `const NAME = literal`. Recorded so emitters can choose to
// no-op (binder already inlined the value at use sites). Carries the literal
// for tooling / debug-info consumers.  [CORE — trivially no-op'd]
public sealed partial record BoundConstStatement(string Name, BoundType Type, BoundExpression Value) : BoundStatement;

// ---- FEATURE statements (must be eliminated by Lowering before CodeGen) ----

// `raise EventName(args)` on the current `self`. EventType is the event's delegate
// type (carries the Invoke shape). Lowered by EventLowering → null-safe capture-then-invoke.
// [FEATURE → EventLowering]
public sealed partial record BoundRaiseStatement(string EventName, BoundType EventType, IReadOnlyList<BoundExpression> Arguments) : BoundStatement;

// let x = expr else { body }  [FEATURE → LetGuardLowering]
public sealed partial record BoundLetGuard(string Name, BoundType DeclaredType, BoundExpression Initializer, BoundBlockStatement ElseBody) : BoundStatement;

// for x in collection { body }            [FEATURE → ForEachLowering → GetEnumerator/MoveNext while]
// await for x in src { body }  (IsAwait)  [FEATURE → AsyncForeachLowering → GetAsyncEnumerator/MoveNextAsync]
// IsAwait selects the enumerator protocol; the binder sets it from the `await for` form. It is a
// scalar (not a walked child), so the generated rewriter/visitor are unaffected and preserve it.
// ElementType is resolved by binding from the enumerable contract and is preserved through
// lowering; recomputing it from an erased external enumerator loses value-type elements.
public sealed partial record BoundForEachStatement(string Identifier, BoundExpression Collection, BoundStatement Body, BoundType ElementType, IReadOnlyList<string>? DestructuredNames = null, bool IsAwait = false) : BoundStatement;

// match { arms }  [FEATURE → MatchLowering]
public sealed partial record BoundMatchStatement(BoundExpression Subject, BoundType SubjectType, IReadOnlyList<BoundMatchArm> Arms) : BoundStatement
{
    /// Set by flow analysis (MatchExhaustiveness) — true when the arms cover the closed
    /// case set (union / enum / closed hierarchy) with no gap. Read by Reachability for
    /// definite-return: an exhaustive match whose every arm terminates is itself a
    /// terminator. Settable (not a ctor param), like Span — written by a post-bind pass.
    public bool IsExhaustive { get; set; }
}

// defer { body }  [FEATURE → DeferLowering → BoundTryStatement(.finally)]
public sealed partial record BoundDeferStatement(BoundBlockStatement Body) : BoundStatement;

// async let name = expr  [FEATURE → AsyncLetLowering → Task.Run + await-at-use]
public sealed partial record BoundAsyncLetStatement(string Name, BoundType DeclaredType, BoundExpression Initializer) : BoundStatement;

// `yield value` in an IAsyncEnumerable<T> function [FEATURE → AsyncStreamLowering]
public sealed partial record BoundYieldStatement(BoundExpression Value) : BoundStatement;

// select { arms }  [FEATURE → ConcurrencyLowering]
public sealed partial record BoundSelectArm(SelectArmKind Kind, string? Binding, BoundType? BindingType, BoundExpression? Channel, BoundExpression? Value, BoundBlockStatement Body);
public sealed partial record BoundSelectStatement(IReadOnlyList<BoundSelectArm> Arms, IReadOnlyList<BoundCapturedVariable> CapturedVariables) : BoundStatement;

// +=, -=, *=, /=  [FEATURE → AssignmentLowering → BoundAssignment(BoundBinaryExpression)]
public sealed partial record BoundCompoundAssignment(BoundExpression Target, SyntaxTokenKind Op, BoundExpression Value) : BoundStatement
{
    /// True only when binding proved that `Target += Handler` / `-=` names an event.
    /// AssignmentLowering consumes this semantic fact before ordinary compound
    /// assignment lowering can turn it into invalid delegate arithmetic.
    public bool IsEventSubscription { get; init; }
}

// === Expressions ===

public abstract record BoundExpression(BoundType Type) : BoundNode
{
    /// Source range of the syntax this expression bound from. Stamped centrally by
    /// the binder's BindExpression dispatch (never per-constructor), so every bound
    /// expression a diagnostic or the semantic model needs to locate carries one.
    /// Synthesized nodes (lowering, forwarders) keep the invalid default — they have
    /// no source. The stamp never overwrites a valid span: folded constants are
    /// shared instances and keep their declaration-site span.
    public SourceSpan Span { get; set; }
}

// ---- CORE expressions (survive lowering, reach CodeGen) ----

// Bound face of ErrorExpressionSyntax — the parser already reported the
// diagnostic, so binding it is silent. Diagnostics gate emission; the
// emitters never see one.
public sealed partial record BoundErrorExpression() : BoundExpression(InferredType.Instance);

public sealed partial record BoundLiteralExpression(object? Value, string Text, BoundType Type) : BoundExpression(Type);
public sealed partial record BoundNameExpression(string Name, BoundType Type) : BoundExpression(Type)
{
    /// Who this name resolved to — the interned <c>LocalSymbol</c> (a local / parameter
    /// read), <c>FieldSymbol</c> (an implicit-self field), <c>TypeSymbol</c> (a type
    /// reference in static-call / construction position), <c>ConstSymbol</c>, or the
    /// namespace host. The SAME instance the binder hands the semantic sink at this
    /// occurrence. Carried so downstream dispatches on the resolved kind instead of
    /// re-disambiguating the string (the <c>TryResolveSlot</c> / <c>LooksLikeTypeName</c>
    /// re-resolution it replaces). Null for names with no spine identity (alias paths,
    /// flattened namespace paths, external-only references).
    public Esharp.Symbols.ISymbol? Symbol { get; init; }
}
public sealed partial record BoundUnaryExpression(SyntaxTokenKind Op, BoundExpression Operand, BoundType Type) : BoundExpression(Type);
public sealed partial record BoundBinaryExpression(BoundExpression Left, SyntaxTokenKind Op, BoundExpression Right, BoundType Type) : BoundExpression(Type);
public sealed partial record BoundMemberAccessExpression(BoundExpression Target, string MemberName, BoundType Type) : BoundExpression(Type)
{
    /// This member is the value projection of a raised durable property location.
    /// Loads and stores must use the property's location companion and dereference
    /// it, never route through ordinary get/set accessors.
    public bool IsPropertyLocationProjection { get; init; }

    /// The resolved member — the interned <c>FieldSymbol</c> (a field / event) or the
    /// <c>MethodSymbol</c> of a property accessor / method group. The SAME instance
    /// reported to the semantic sink. Carried so CodeGen emits the member directly from
    /// its declaring <c>TypeSymbol</c> instead of re-resolving <c>MemberName</c> against
    /// the target's type. Null for external / BCL members (the reflection path) and
    /// unresolved access.
    public Esharp.Symbols.ISymbol? Member { get; init; }
}

// BoundConversion and ConversionKind are defined in BoundConversion.cs.
// That file is the authoritative home for the unified cast/box/narrow CORE node.

// operand is [not] T — a runtime type test (`isinst` + bool), `Negated` for `is not`.
// [CORE — kept as a boolean test primitive; never produces a cast value]
public sealed partial record BoundTypeTestExpression(BoundExpression Operand, BoundType TargetType, bool Negated) : BoundExpression(new PrimitiveType("bool"));
public sealed partial record BoundCallExpression(BoundExpression Target, IReadOnlyList<BoundExpression> Arguments, BoundType Type, IReadOnlyList<BoundType>? ExplicitTypeArguments = null) : BoundExpression(Type)
{
    /// The resolved target for a call to a user-declared function (free,
    /// static-func member, or promoted). Reference-identical to the function's
    /// `BoundFunctionDeclaration.Symbol`, so the IL emitter recovers the exact
    /// Cecil method from its definition map — namespace-correct by construction,
    /// no first-match-wins module walk. Null for BCL / external / delegate /
    /// constructor calls, which keep the reflection path.
    public Esharp.Symbols.MethodSymbol? ResolvedMethod { get; init; }
}
// ObjectCreation, Index, Array, Tuple, Default, AddressOf, MethodGroupConversion,
// OutArgument are all CORE — they reach CodeGen directly.
public sealed partial record BoundObjectCreationExpression(BoundType ObjectType, IReadOnlyList<BoundFieldInit> Fields) : BoundExpression(ObjectType)
{
    /// Positional constructor arguments, for types built through an `init(args)` ctor
    /// rather than field-initializer construction — e.g. `Chan<T>(capacity)`. When
    /// non-empty, CodeGen matches a constructor by argument count and emits these
    /// before `newobj`; <see cref="Fields"/> then applies any trailing object-initializer
    /// stores. Field-init construction leaves this empty (parameterless ctor path).
    /// An init-property (not a primary-ctor field) so existing 2-arg construction sites
    /// are unaffected and `with` preserves it.
    public IReadOnlyList<BoundExpression> ConstructorArguments { get; init; } = [];
}
public sealed partial record BoundIndexExpression(BoundExpression Target, BoundExpression Index, BoundType Type) : BoundExpression(Type);
public sealed partial record BoundArrayCreationExpression(BoundType ElementType, BoundExpression Size, BoundType Type) : BoundExpression(Type);

// (e1, e2, ...) — tuple literal  [CORE]
public sealed partial record BoundTupleLiteralExpression(IReadOnlyList<BoundExpression> Elements, BoundType Type) : BoundExpression(Type);

// default(T) — zero-initialized value of the given type  [CORE]
public sealed partial record BoundDefaultExpression(BoundType Type) : BoundExpression(Type);

// out var name / out name — argument passed by address.  [CORE]
// For `out var`, the binder also inserts a BoundVariableDeclaration into the enclosing scope.
public sealed partial record BoundOutArgumentExpression(string Name, BoundType SlotType, bool DeclaresLocal) : BoundExpression(SlotType);

// &funcName — function pointer  [CORE]
public sealed partial record BoundAddressOfExpression(string FunctionName, IReadOnlyList<BoundType> ParameterTypes, BoundType ReturnType)
    : BoundExpression(new FunctionPointerType(ParameterTypes, ReturnType));

// A method group (a bare `func` name) converted to a delegate instance.  [CORE]
// Delegate creation from a named method. `Receiver` is the instance the delegate binds to:
// null for a static target (emits `ldnull / ldftn <method> / newobj`); for a closure trampoline
// it is the display-class instance (emits `<receiver> / ldftn <method> / newobj`).
public sealed partial record BoundMethodGroupConversion(string FunctionName, int ParameterCount, BoundType DelegateType, BoundExpression? Receiver = null)
    : BoundExpression(DelegateType);

// ---- FEATURE expressions (must be eliminated by Lowering before CodeGen) ----

// A string-interpolation part: either literal text (Expr null) or a bound hole expression.
// Holes are full expressions bound through the normal pipeline.
public sealed partial record BoundInterpolationPart(string? Literal, BoundExpression? Expr);
// [FEATURE → StringLowering → string.Concat/Format]
public sealed partial record BoundInterpolatedStringExpression(IReadOnlyList<BoundInterpolationPart> Parts, BoundType Type) : BoundExpression(Type);

// cond ? then : else  [FEATURE → ExpressionFormLowering → temp local + branches]
public sealed partial record BoundConditionalExpression(BoundExpression Condition, BoundExpression Consequence, BoundExpression Alternative, BoundType Type) : BoundExpression(Type);

// left ?? right  [FEATURE → NullFlowLowering → null-test + branch]
public sealed partial record BoundNullCoalescingExpression(BoundExpression Left, BoundExpression Right, BoundType Type) : BoundExpression(Type);

// target?.member  [FEATURE → NullFlowLowering → null-test + branch]
public sealed partial record BoundNullConditionalAccessExpression(BoundExpression Target, string MemberName, BoundType Type) : BoundExpression(Type);

// [e1, e2, ...]  [FEATURE → kept as BoundCallExpression(List.Add) by StringLowering; lowering phase TBD]
public sealed partial record BoundListLiteralExpression(IReadOnlyList<BoundExpression> Elements, BoundType ElementType, BoundType Type) : BoundExpression(Type);

// Target is null for the standalone integer-range form `start..end`.
// [FEATURE → ForEachLowering; the int range form → while loop]
public sealed partial record BoundRangeExpression(BoundExpression? Target, BoundExpression? Start, BoundExpression? End, BoundType Type) : BoundExpression(Type);

// spawn { ... }  [FEATURE → ConcurrencyLowering → E# stdlib Spawned]
public sealed partial record BoundSpawnExpression(BoundBlockStatement Body, IReadOnlyList<BoundCapturedVariable> CapturedVariables) : BoundExpression(new ExternalType("Spawned"));

// chan<T>(n)  [FEATURE → ConcurrencyLowering → Chan ctor]
public sealed partial record BoundChanCreationExpression(BoundType ElementType, BoundExpression? Capacity) : BoundExpression(new ChanType(ElementType));

// .Case(args) / `.case` in expression position  [FEATURE → MatchLowering → factory call]
public sealed partial record BoundDotCaseExpression(string CaseName, string ResolvedTypeName, IReadOnlyList<BoundExpression> Arguments, BoundType Type) : BoundExpression(Type);

// expr?  — try-unwrap  [FEATURE → ResultLowering → if-IsOk / early-return]
public sealed partial record BoundTryUnwrapExpression(BoundExpression Inner, BoundType UnwrappedType, string TempName) : BoundExpression(UnwrappedType);

// expr with { f: v }  [FEATURE → WithLowering → copy + field stores]
public sealed partial record BoundWithExpression(BoundExpression Target, IReadOnlyList<BoundFieldInit> Fields, BoundType Type) : BoundExpression(Type);

// ok(v) / error(v)  [FEATURE → ResultLowering → initobj + field stores on Result`2]
public sealed partial record BoundResultCallExpression(bool IsOk, BoundExpression Argument, BoundType OkType, BoundType ErrorType) : BoundExpression(new ResultType(OkType, ErrorType));

// func(a: int) -> bool { ... } / (x) => e  — anonymous function literal
// [FEATURE → ClosureConversion → display-class symbol + ctor + field stores]
public sealed partial record BoundCapturedVariable(string Name, BoundType Type, bool Mutable);
/// IsFunctionPointer = true when the binder resolved the literal against a
/// FunctionPointerType expected slot AND the literal had no captures. The IL
/// emitter consumes this flag to choose between `ldftn` only (fnptr) and
/// `ldnull/ldftn/newobj <Delegate>` (delegate). Without captures, both shapes
/// are verifiable; the expected type drives the pick. With captures, fnptr is
/// impossible (no env), so the binder leaves IsFunctionPointer false even when
/// the expected slot is fnptr — that case lands as a type-mismatch diagnostic.
/// `ReturnType` is the literal's DECLARED/inferred type, kept as the awaitable WRAPPER
/// when async (`Task<int>`) so the materialized delegate's signature stays `Func<Task<int>>`.
/// When `IsAsync`, the body awaits at this lambda's own level and lowers to its own CLR
/// state machine (AsyncLowering), exactly as a top-level async function does.
public sealed partial record BoundFunctionLiteralExpression(
    IReadOnlyList<BoundParameter> Parameters,
    BoundType ReturnType,
    BoundBlockStatement Body,
    IReadOnlyList<BoundCapturedVariable> CapturedVariables,
    bool IsFunctionPointer = false,
    bool IsAsync = false,
    AsyncReturnShape AsyncShape = AsyncReturnShape.ValueTask
) : BoundExpression(InferredType.Instance);

// await expr  [FEATURE → AsyncLowering → state-machine struct symbol + builder by AsyncReturnShape]
public sealed partial record BoundAwaitExpression(BoundExpression Inner, BoundType ResultType) : BoundExpression(ResultType);

// match expression — match in expression position, arms yield values
// [FEATURE → MatchLowering / ExpressionFormLowering]
public sealed partial record BoundMatchExpressionArm(BoundMatchPattern Pattern, BoundExpression Value, BoundExpression? Guard = null);
public sealed partial record BoundMatchExpression(BoundExpression Subject, BoundType SubjectType, IReadOnlyList<BoundMatchExpressionArm> Arms) : BoundExpression(Arms.Count > 0 ? Arms[0].Value.Type : InferredType.Instance)
{
    /// Set by flow analysis (MatchExhaustiveness). See BoundMatchStatement.IsExhaustive.
    /// NOTE: BoundMatchStatement and BoundMatchExpression are SIBLINGS (one is a
    /// BoundStatement, the other a BoundExpression) — there is no shared match base, and
    /// the scrutinee field on both is `SubjectType` (not `ScrutineeType`). Consumers walk
    /// each kind explicitly; `x is BoundMatchExpression` is never true for a statement.
    public bool IsExhaustive { get; set; }
}

// if expression — `if`/`else if`/`else` in expression position.
// [FEATURE → ExpressionFormLowering → temp local + branches]
public sealed partial record BoundIfExpressionBranch(BoundExpression Condition, IReadOnlyList<BoundStatement> Body, BoundExpression? Value);
public sealed partial record BoundIfExpression(IReadOnlyList<BoundIfExpressionBranch> Branches, IReadOnlyList<BoundStatement> ElseBody, BoundExpression? ElseValue, BoundType Type) : BoundExpression(Type);

// NOTE: BoundParenthesizedExpression is REMOVED — it carries no IL.
// The binder unwraps it in place; it never reaches the bound tree post-bind.

// === Supporting (shared by CORE and FEATURE nodes) ===

public sealed partial record BoundField(
    string Name, BoundType Type, bool IsPublic = true, bool Mutable = true,
    BoundExpression? DefaultValue = null, bool IsEmbedded = false, bool IsEvent = false,
    bool IsRequired = false, bool IsProperty = false, bool IsComputedProperty = false,
    bool PropHasSet = false, bool PropHasInit = false,
    Esharp.Syntax.Visibility Vis = Esharp.Syntax.Visibility.Public,
    bool PropHasExplicitLoca = false,
    string? PropLocaStorageName = null,
    bool PropHasMut = false,
    bool PropMutWritable = false,
    bool PropHasCustomSetter = false,
    BoundScopedMutAccessor? ScopedMut = null)
{
    // Three-state field visibility: `pub` → Public, `priv` → Private, bare → internal.
    // `IsPublic` stays the public-vs-not face; `Vis` distinguishes `priv` (CLR private)
    // from bare-internal at emit.
}

// The block form of `mut`.  Setup runs before the property lends the yielded
// location; Resume is a real finally-body and therefore runs on normal return,
// mutation failure, and exception unwinding.  It is carried on the property
// declaration rather than encoded as a ref-return method because a scoped lend
// must never outlive its cleanup region.
public sealed record BoundScopedMutAccessor(
    BoundBlockStatement Setup,
    BoundExpression YieldTarget,
    BoundBlockStatement Resume,
    bool IsWritable);

// `DefaultValue` is the declaration-bound default expression (constant shape, ES2180).
// Call sites that omit the argument re-materialize it; a literal default is also stamped
// onto the emitted parameter (`[Optional]` + `.param` constant) for C# callers.
public sealed partial record BoundParameter(string Name, BoundType Type, bool ByRef, bool ReadOnlyByRef = false, bool IsOut = false, BoundExpression? DefaultValue = null);
public sealed partial record BoundChoiceCase(string Name, IReadOnlyList<BoundField> Payloads);
public sealed partial record BoundFieldInit(string Name, BoundExpression Value);
public sealed partial record BoundMatchArm(BoundMatchPattern Pattern, BoundBlockStatement Body, BoundExpression? Guard = null);
// Symbol preserves the arm-local binding identity through lowering. Multiple match
// arms may use the same source spelling with different payload types.
public sealed partial record BoundMatchBinding(string Name, BoundType Type, string PayloadFieldName,
    Esharp.Symbols.LocalSymbol? Symbol = null);
// A pattern is a `.case` (CaseName + Bindings), a literal (LiteralValue), `default`
// (IsDefault), the `nil` arm (IsNil), or a type pattern (TypeBindingName bound to
// NarrowedType — `isinst NarrowedType` then store the cast value).
public sealed partial record BoundMatchPattern(
    string? CaseName, IReadOnlyList<BoundMatchBinding>? Bindings, bool IsDefault,
    BoundExpression? LiteralValue = null,
    bool IsNil = false,
    string? TypeBindingName = null,
    BoundType? NarrowedType = null);
