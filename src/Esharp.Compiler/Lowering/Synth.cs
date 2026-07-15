using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// The shared bound-node synthesis vocabulary for lowering passes. Every pass builds the
/// same handful of CORE shapes — a <c>default(T)</c>, a temp + reference, a nil-test, a
/// <c>HasValue</c>/<c>Value</c> projection, a <c>string.Concat</c> segment — and historically
/// each pass re-spelled them, which is exactly where the divergent bugs lived (one pass built
/// <c>default(T)</c> as an empty object-creation, another compared a value-type
/// <c>Nullable&lt;T&gt;</c> against a reference <c>null</c>). This type is the single source of
/// truth for those shapes, so a fix lands once.
///
/// <para>Everything here is a pure factory over <see cref="BoundExpression"/> /
/// <see cref="BoundStatement"/> — no traversal, no state. Traversal lives in
/// <see cref="BoundTreeRewriter"/> (Base A) / <see cref="SpillingBoundTreeRewriter"/> (Base B);
/// temp-name allocation lives on the spilling base (<see cref="SpillingBoundTreeRewriter.FreshTemp"/>)
/// or a pass-local counter. This class only mints nodes.</para>
/// </summary>
public static class Synth
{
    // ── Cached primitive types ───────────────────────────────────────────────────
    // BoundType records compare by value, so sharing one instance is purely an allocation
    // win — never an identity hazard.

    public static readonly PrimitiveType Bool   = new("bool");
    public static readonly PrimitiveType Int    = new("int");
    public static readonly PrimitiveType Float  = new("float");
    public static readonly PrimitiveType String = new("string");
    public static readonly PrimitiveType Void   = new("void");

    /// `object` — the type the null literal carries for a *reference-identity* nil test, so
    /// CodeGen takes the `ldnull; cgt.un` path rather than routing `!= null` through a typed
    /// value-equality overload (e.g. `String::op_Equality`, which would not even push null).
    public static readonly PrimitiveType Object = new("object");

    // ── Literals ─────────────────────────────────────────────────────────────────

    public static BoundLiteralExpression IntLit(int v)     => new(v, v.ToString(), Int);
    public static BoundLiteralExpression BoolLit(bool v)   => new(v, v ? "true" : "false", Bool);
    public static BoundLiteralExpression StrLit(string s)  => new(s, s, String);

    /// The reference-identity null: typed `object` (see <see cref="Object"/>).
    public static BoundLiteralExpression NullObj           => new(null, "null", Object);

    /// <summary>
    /// The zero value of <paramref name="type"/> — the one correct spelling for "default(T)".
    /// Value type → <c>initobj</c>; reference → <c>ldnull</c>; type parameter → <c>initobj</c>
    /// on the reified argument. CodeGen owns the per-shape emission; lowering must never
    /// approximate it with an empty object-creation (wrong for refs and type params).
    /// </summary>
    public static BoundDefaultExpression Default(BoundType type) => new(type);

    // ── References, members, calls ───────────────────────────────────────────────

    public static BoundNameExpression Name(string name, BoundType type) => new(name, type);

    public static BoundMemberAccessExpression Member(BoundExpression target, string member, BoundType type)
        => new(target, member, type);

    public static BoundCallExpression Call(BoundExpression target, IReadOnlyList<BoundExpression> args, BoundType result)
        => new(target, args, result);

    /// A static-host call written as a bare type name receiver: <c>Host.Method(args)</c>.
    public static BoundCallExpression StaticCall(string host, string method, BoundType hostType,
                                                 IReadOnlyList<BoundExpression> args, BoundType result)
        => new(new BoundMemberAccessExpression(Name(host, hostType), method, result), args, result);

    public static BoundUnaryExpression Not(BoundExpression operand)
        => new(SyntaxTokenKind.Bang, operand, Bool);

    // ── Statements ───────────────────────────────────────────────────────────────

    public static BoundVariableDeclaration Let(string name, BoundType type, BoundExpression init)
        => new(Mutable: false, name, type, init);

    public static BoundVariableDeclaration Var(string name, BoundType type, BoundExpression init)
        => new(Mutable: true, name, type, init);

    public static BoundAssignment Assign(BoundExpression target, BoundExpression value)
        => new(target, value);

    public static BoundIfStatement If(BoundExpression cond, BoundStatement then, BoundStatement? @else = null)
        => new(cond, then, @else);

    public static BoundBlockStatement Block(IReadOnlyList<BoundStatement> stmts) => new(stmts);

    public static BoundBlockStatement Block(params BoundStatement[] stmts) => new(stmts);

    /// Wrap a statement as a block iff it is not already one (no spurious nesting).
    public static BoundBlockStatement AsBlock(BoundStatement s)
        => s as BoundBlockStatement ?? new BoundBlockStatement([s]) { Span = s.Span };

    // ── Nullable vocabulary (the bug-prone seam, centralized) ────────────────────
    //
    // A `T?` is either a value-type Nullable<T> (BoundType is NullableType) or a reference
    // nullable (the underlying reference type with an annotation). The two test and unwrap
    // completely differently, and conflating them is the classic reference-compare-against-a-
    // value-type bug. These four helpers are the only sanctioned spelling.

    /// True when <paramref name="type"/> is a value-type <c>Nullable&lt;T&gt;</c> (vs a
    /// reference nullable).
    public static bool IsValueNullable(BoundType type) => type is NullableType nt && nt.Inner switch
    {
        PrimitiveType p => p.Name is not ("string" or "object" or "void"),
        EnumType or TupleType or ResultType => true,
        ChoiceType c => !c.IsRef,
        DataType d => d.Classification == DataClassification.Struct,
        _ => false,
    };

    /// A boolean "has a value" test over a nullable of either flavor:
    /// value → <c>x.HasValue</c>; reference → <c>x != (object)null</c>.
    public static BoundExpression IsPresent(BoundExpression nullable) => IsValueNullable(nullable.Type)
        ? Member(nullable, "HasValue", Bool)
        : new BoundBinaryExpression(nullable, SyntaxTokenKind.BangEquals, NullObj, Bool);

    /// A boolean "is nil" test — the negation of <see cref="IsPresent"/>:
    /// value → <c>!x.HasValue</c>; reference → <c>x == (object)null</c>.
    public static BoundExpression IsNil(BoundExpression nullable) => IsValueNullable(nullable.Type)
        ? Not(Member(nullable, "HasValue", Bool))
        : new BoundBinaryExpression(nullable, SyntaxTokenKind.EqualsEquals, NullObj, Bool);

    /// The underlying value of a present nullable: value → <c>x.Value</c> (typed by the inner
    /// type); reference → the receiver itself (a reference nullable already *is* the value).
    public static BoundExpression Unwrap(BoundExpression nullable) => IsValueNullable(nullable.Type)
        && nullable.Type is NullableType nt
        ? Member(nullable, "Value", nt.Inner)
        : nullable;

    /// Lift a bare value into a value-nullable slot when required: <c>T → Nullable&lt;T&gt;</c>
    /// via the present-case wrap. Identity for a reference target.
    public static BoundExpression WrapInto(BoundExpression value, BoundType target) => target is NullableType nt
        ? BoundConversion.WrapNullable(value, nt)
        : value;
}
