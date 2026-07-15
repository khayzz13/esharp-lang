using Esharp.Syntax;

namespace Esharp.Binder;

/// Compile-time constant folding over bound expressions — shared by `const`
/// statements (StatementBinder) and namespace-level `const` declarations
/// (DeclarationBinder). Handles literals, parenthesized expressions, unary
/// minus / bang, and binary arithmetic / comparison on numeric, string, and bool
/// operands. Returns null when the expression isn't fully foldable; the caller
/// decides whether that's an error.
public static class ConstantFolder
{
    public static BoundLiteralExpression? Fold(BoundExpression expr)
    {
        switch (expr)
        {
            case BoundLiteralExpression lit: return lit;
            case BoundUnaryExpression u:
                var inner = Fold(u.Operand);
                if (inner is null) return null;
                switch (u.Op)
                {
                    case SyntaxTokenKind.Minus:
                        return inner.Value switch
                        {
                            int i => new BoundLiteralExpression(-i, (-i).ToString(), inner.Type),
                            long l => new BoundLiteralExpression(-l, (-l).ToString(), inner.Type),
                            float f => new BoundLiteralExpression(-f, (-f).ToString(), inner.Type),
                            double d => new BoundLiteralExpression(-d, (-d).ToString(), inner.Type),
                            _ => null,
                        };
                    case SyntaxTokenKind.Bang or SyntaxTokenKind.NotKeyword:
                        return inner.Value is bool b ? new BoundLiteralExpression(!b, (!b).ToString(), inner.Type) : null;
                }
                return null;
            case BoundBinaryExpression bin:
                var lFold = Fold(bin.Left);
                var rFold = Fold(bin.Right);
                if (lFold is null || rFold is null) return null;
                return FoldBinary(bin.Op, lFold, rFold);
        }
        return null;
    }

    /// Whether an expression is a CONSTANT SHAPE — evaluable at any call site with
    /// no environment and no side effects. The contract for parameter defaults
    /// (ES2180): a foldable literal expression, `nil`, `default(T)`, or a
    /// composite-literal / choice-case / Result construction built from constant
    /// shapes. Deliberately excludes names, calls, and anything heap-observable
    /// shared between calls — a default materializes fresh at each omitted slot.
    public static bool IsConstantShape(BoundExpression expr) => expr switch
    {
        _ when Fold(expr) is not null => true,
        BoundLiteralExpression => true, // nil / non-numeric literals the folder declines
        BoundDefaultExpression => true,
        BoundObjectCreationExpression oc => oc.Fields.All(f => IsConstantShape(f.Value)),
        BoundDotCaseExpression dc => dc.Arguments.All(IsConstantShape),
        BoundResultCallExpression rc => IsConstantShape(rc.Argument),
        _ => false,
    };

    static BoundLiteralExpression? FoldBinary(SyntaxTokenKind op, BoundLiteralExpression l, BoundLiteralExpression r)
    {
        BoundLiteralExpression Lit(object v, BoundType t) => new(v, v.ToString() ?? "", t);

        // String concat via `+`.
        if (op == SyntaxTokenKind.Plus && l.Value is string ls && r.Value is string rs)
            return Lit(ls + rs, new PrimitiveType("string"));

        // Numeric arithmetic. Promote int/long/float/double per operand width.
        if (l.Value is int li && r.Value is int ri)
            return op switch
            {
                SyntaxTokenKind.Plus => Lit(li + ri, new PrimitiveType("int")),
                SyntaxTokenKind.Minus => Lit(li - ri, new PrimitiveType("int")),
                SyntaxTokenKind.Star => Lit(li * ri, new PrimitiveType("int")),
                SyntaxTokenKind.Slash when ri != 0 => Lit(li / ri, new PrimitiveType("int")),
                SyntaxTokenKind.Percent when ri != 0 => Lit(li % ri, new PrimitiveType("int")),
                SyntaxTokenKind.EqualsEquals => Lit(li == ri, new PrimitiveType("bool")),
                SyntaxTokenKind.BangEquals => Lit(li != ri, new PrimitiveType("bool")),
                SyntaxTokenKind.Less => Lit(li < ri, new PrimitiveType("bool")),
                SyntaxTokenKind.LessEquals => Lit(li <= ri, new PrimitiveType("bool")),
                SyntaxTokenKind.Greater => Lit(li > ri, new PrimitiveType("bool")),
                SyntaxTokenKind.GreaterEquals => Lit(li >= ri, new PrimitiveType("bool")),
                _ => null,
            };
        if (l.Value is double ld && r.Value is double rd)
            return op switch
            {
                SyntaxTokenKind.Plus => Lit(ld + rd, new PrimitiveType("double")),
                SyntaxTokenKind.Minus => Lit(ld - rd, new PrimitiveType("double")),
                SyntaxTokenKind.Star => Lit(ld * rd, new PrimitiveType("double")),
                SyntaxTokenKind.Slash when rd != 0 => Lit(ld / rd, new PrimitiveType("double")),
                _ => null,
            };
        if (l.Value is bool lb && r.Value is bool rb)
            return op switch
            {
                SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword => Lit(lb && rb, new PrimitiveType("bool")),
                SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword => Lit(lb || rb, new PrimitiveType("bool")),
                SyntaxTokenKind.EqualsEquals => Lit(lb == rb, new PrimitiveType("bool")),
                SyntaxTokenKind.BangEquals => Lit(lb != rb, new PrimitiveType("bool")),
                _ => null,
            };
        return null;
    }
}
