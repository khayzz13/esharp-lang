using Esharp.Syntax;

namespace Esharp;

internal static class OperatorFacts
{
    public static string? MetadataName(SyntaxTokenKind op, int arity) => (op, arity) switch
    {
        (SyntaxTokenKind.Plus, 1) => "op_UnaryPlus",
        (SyntaxTokenKind.Minus, 1) => "op_UnaryNegation",
        (SyntaxTokenKind.Bang, 1) => "op_LogicalNot",
        (SyntaxTokenKind.Tilde, 1) => "op_OnesComplement",
        (SyntaxTokenKind.Plus, 2) => "op_Addition",
        (SyntaxTokenKind.Minus, 2) => "op_Subtraction",
        (SyntaxTokenKind.Star, 2) => "op_Multiply",
        (SyntaxTokenKind.Slash, 2) => "op_Division",
        (SyntaxTokenKind.Percent, 2) => "op_Modulus",
        (SyntaxTokenKind.Ampersand, 2) => "op_BitwiseAnd",
        (SyntaxTokenKind.Pipe, 2) => "op_BitwiseOr",
        (SyntaxTokenKind.Caret, 2) => "op_ExclusiveOr",
        (SyntaxTokenKind.ShiftLeft, 2) => "op_LeftShift",
        (SyntaxTokenKind.ShiftRight, 2) => "op_RightShift",
        (SyntaxTokenKind.UnsignedShiftRight, 2) => "op_UnsignedRightShift",
        (SyntaxTokenKind.EqualsEquals, 2) => "op_Equality",
        (SyntaxTokenKind.BangEquals, 2) => "op_Inequality",
        (SyntaxTokenKind.Less, 2) => "op_LessThan",
        (SyntaxTokenKind.Greater, 2) => "op_GreaterThan",
        (SyntaxTokenKind.LessEquals, 2) => "op_LessThanOrEqual",
        (SyntaxTokenKind.GreaterEquals, 2) => "op_GreaterThanOrEqual",
        _ => null,
    };

    public static bool IsComparison(SyntaxTokenKind op) => op is
        SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals or
        SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or
        SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals;
}
