using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// Lowers the two null-flow operators into CORE BoundTree nodes, honestly for BOTH a
/// value-type <c>Nullable&lt;T&gt;</c> and a reference nullable:
///
///   <c>a ?? b</c>
///     value:     var __a = a; T __r; if (__a.HasValue) __r = __a.Value; else __r = b;  → __r
///     reference: var __a = a; R __r; if (__a != null)   __r = __a;       else __r = b;  → __r
///
///   <c>a?.member</c>
///     value:     var __a = a; R __r = nil; if (__a.HasValue) __r = wrap(__a.Value.member);  → __r
///     reference: var __a = a; R __r = nil; if (__a != null)   __r = __a.member;             → __r
///
/// The right of <c>??</c> and the member access of <c>?.</c> are conditionally evaluated, so
/// their setup is hoisted INSIDE the nil-test branch — never before the whole statement. The
/// pass derives from <see cref="SpillingBoundTreeRewriter"/>, inheriting total descent (so a
/// <c>??</c>/<c>?.</c> buried in a lambda body, a constructor argument, or an init body is
/// lowered) plus the short-circuit-safe hoist machinery.
///
/// A value-type <c>Nullable&lt;T&gt;</c> is tested with <c>get_HasValue</c> and unwrapped with
/// <c>get_Value</c> — never a reference compare against <c>null</c>, which on a value-type
/// <c>Nullable&lt;T&gt;</c> is always false (the bug this pass previously shipped). The bound
/// tree distinguishes the two: a value nullable is a <see cref="NullableType"/>; a reference
/// nullable is the underlying reference type with an annotation.
/// </summary>
public sealed class NullFlowLowering : IBoundTreePass
{
    public static readonly NullFlowLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new NullFlowRewriter());
}

sealed class NullFlowRewriter : SpillingBoundTreeRewriter
{
    static readonly PrimitiveType BoolT = new("bool");

    // a ?? b
    protected override BoundExpression RewriteNullCoalescingExpression(BoundNullCoalescingExpression node)
    {
        // A literal nil has no effects and can never select the left branch. More
        // importantly, its bound NullType resolves to object in IL, so materializing
        // the generic coalesce temporary would attempt an object → concrete-reference
        // assignment on an unreachable branch. Evaluate only the selected right side.
        if (node.Left is BoundLiteralExpression { Value: null } || node.Left.Type is NullType)
            return RewriteExpression(node.Right);

        var left  = RewriteExpression(node.Left);          // unconditional
        var (right, rightHoists) = RewriteCapturing(node.Right);  // only when left is nil

        var leftType   = node.Left.Type;
        var resultType = node.Type;
        var valueNullable = Synth.IsValueNullable(leftType);

        // let __a = left
        var aName = FreshTemp("coal_a");
        var aRef  = new BoundNameExpression(aName, leftType);
        Hoist(new BoundVariableDeclaration(Mutable: false, aName, leftType, left));

        // var __r = default(R)
        var rName = FreshTemp("coal_r");
        var rRef  = new BoundNameExpression(rName, resultType);
        Hoist(new BoundVariableDeclaration(Mutable: true, rName, resultType, new BoundDefaultExpression(resultType)));

        // present test. The null literal is typed `object`, not the source type, so codegen
        // takes the reference-identity path (`ldnull; cgt.un`) rather than a value-equality
        // overload (`String::op_Equality`, which would not push the null operand).
        BoundExpression present = valueNullable
            ? new BoundMemberAccessExpression(aRef, "HasValue", BoolT)
            : new BoundBinaryExpression(aRef, SyntaxTokenKind.BangEquals,
                new BoundLiteralExpression(null, "null", new ExternalType("object")), BoolT);

        // present value coerced to R: unwrap a value-nullable when R is the bare value type.
        BoundExpression presentValue =
            valueNullable && resultType is not NullableType && leftType is NullableType nt
                ? new BoundMemberAccessExpression(aRef, "Value", nt.Inner)
                : aRef;

        var thenStmts = new List<BoundStatement> { new BoundAssignment(rRef, presentValue) };
        var elseStmts = new List<BoundStatement>(rightHoists) { new BoundAssignment(rRef, right) };
        Hoist(new BoundIfStatement(present,
            new BoundBlockStatement(thenStmts), new BoundBlockStatement(elseStmts)));

        return rRef;
    }

    // a?.member
    protected override BoundExpression RewriteNullConditionalAccessExpression(BoundNullConditionalAccessExpression node)
    {
        var src     = RewriteExpression(node.Target);   // unconditional (chains nest here)
        var srcType = node.Target.Type;
        var resultType = node.Type;                     // always nullable
        var valueNullable = Synth.IsValueNullable(srcType);

        // let __a = src
        var aName = FreshTemp("nc_a");
        var aRef  = new BoundNameExpression(aName, srcType);
        Hoist(new BoundVariableDeclaration(Mutable: false, aName, srcType, src));

        // var __r = nil
        var rName = FreshTemp("nc_r");
        var rRef  = new BoundNameExpression(rName, resultType);
        Hoist(new BoundVariableDeclaration(Mutable: true, rName, resultType, new BoundDefaultExpression(resultType)));

        // present test + the non-null receiver to access the member through
        BoundExpression present;
        BoundExpression receiver;
        if (valueNullable && srcType is NullableType nt)
        {
            present  = new BoundMemberAccessExpression(aRef, "HasValue", BoolT);
            receiver = new BoundMemberAccessExpression(aRef, "Value", nt.Inner);
        }
        else
        {
            // object-typed null → reference-identity test, not a value-equality overload.
            present  = new BoundBinaryExpression(aRef, SyntaxTokenKind.BangEquals,
                new BoundLiteralExpression(null, "null", new ExternalType("object")), BoolT);
            receiver = aRef;
        }

        // member access yields its raw (non-nullable) type; wrap into R when R is a value
        // nullable (a value-typed member), identity when R is a reference member.
        var memberRaw = resultType is NullableType rnt ? rnt.Inner : resultType;
        BoundExpression member = new BoundMemberAccessExpression(receiver, node.MemberName, memberRaw);
        BoundExpression stored  = resultType is NullableType rntWrap
            ? BoundConversion.WrapNullable(member, rntWrap)
            : member;

        Hoist(new BoundIfStatement(present,
            new BoundBlockStatement([new BoundAssignment(rRef, stored)]), Else: null));

        return rRef;
    }
}
