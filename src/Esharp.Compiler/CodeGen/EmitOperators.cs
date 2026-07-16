using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.CodeGen;

// ============================================================================
// EmitOperators — the primitive binary-operator → IL-opcode lowering.
//
// The one place a `SyntaxTokenKind` operator becomes its CIL opcode, and the one
// place operand SIGNEDNESS is honored. The CLR splits several arithmetic ops into a
// signed and an unsigned form that are NOT interchangeable:
//
//   >>   arithmetic `shr`  (sign-extends) vs logical `shr.un`
//   /    signed    `div`   vs `div.un`
//   %    signed    `rem`   vs `rem.un`
//   < >  signed    `clt`/`cgt` vs `clt.un`/`cgt.un`
//
// `+ - * & | ^ == <<` are representation-agnostic (two's complement) and take one
// opcode either way. The shifted value's type (for `>>`) or the common operand type
// (for `/ % < >`) decides which form to emit; `>>>` is always logical.
//
// Callers pass the governing operand type so this stays the single decision point —
// EmitBinary and the compound-assignment paths route every primitive op through here.
// ============================================================================
public partial class MethodBodyEmitter
{
    /// The unsigned CLR integral primitives — the ones whose arithmetic needs the
    /// `.un` opcode form. `char` is a `u2` and compares/shifts unsigned.
    static bool IsUnsignedIntegral(BoundType? t) =>
        t is PrimitiveType { Name: "byte" or "ushort" or "uint" or "ulong" or "nuint" or "char" };

    /// Emit the CIL opcode(s) for a primitive binary operator. `operandType` is the
    /// governing operand type — the shifted value for a shift, the common operand type
    /// for a division/remainder/comparison — and selects the signed vs unsigned opcode.
    /// A null `operandType` (a reference/null comparison, an error path) is treated as
    /// signed, which is correct for the `ceq`/null shapes that pass none.
    void EmitBinaryOp(SyntaxTokenKind op, BoundType? operandType = null)
    {
        var unsigned = IsUnsignedIntegral(operandType);
        switch (op)
        {
            case SyntaxTokenKind.Plus or SyntaxTokenKind.PlusEquals:
                _il.Add(); break;
            case SyntaxTokenKind.Minus or SyntaxTokenKind.MinusEquals:
                _il.Sub(); break;
            case SyntaxTokenKind.Star or SyntaxTokenKind.StarEquals:
                _il.Mul(); break;
            case SyntaxTokenKind.Slash or SyntaxTokenKind.SlashEquals:
                if (unsigned) _il.DivUn(); else _il.Div();
                break;
            case SyntaxTokenKind.Percent or SyntaxTokenKind.PercentEquals:
                if (unsigned) _il.RemUn(); else _il.Rem();
                break;
            case SyntaxTokenKind.Ampersand or SyntaxTokenKind.AmpersandEquals:
                _il.And(); break;
            case SyntaxTokenKind.Pipe or SyntaxTokenKind.PipeEquals:
                _il.Or(); break;
            case SyntaxTokenKind.Caret or SyntaxTokenKind.CaretEquals:
                _il.Xor(); break;
            case SyntaxTokenKind.ShiftLeft or SyntaxTokenKind.ShiftLeftEquals:
                _il.Shl(); break;
            case SyntaxTokenKind.ShiftRight or SyntaxTokenKind.ShiftRightEquals:
                // Arithmetic `shr` sign-extends; an unsigned value needs logical `shr.un`.
                if (unsigned) _il.ShrUn(); else _il.Shr();
                break;
            case SyntaxTokenKind.UnsignedShiftRight or SyntaxTokenKind.UnsignedShiftRightEquals:
                // `>>>` is logical regardless of the operand's declared signedness.
                _il.ShrUn(); break;
            case SyntaxTokenKind.EqualsEquals:
                _il.Ceq(); break;
            case SyntaxTokenKind.BangEquals:
                _il.Ceq();
                _il.LoadInt(0);
                _il.Ceq();
                break;
            case SyntaxTokenKind.Less:
                if (unsigned) _il.CltUn(); else _il.Clt();
                break;
            case SyntaxTokenKind.Greater:
                if (unsigned) _il.CgtUn(); else _il.Cgt();
                break;
            case SyntaxTokenKind.LessEquals:
                // a <= b  ⇔  !(a > b)
                if (unsigned) _il.CgtUn(); else _il.Cgt();
                _il.LoadInt(0);
                _il.Ceq();
                break;
            case SyntaxTokenKind.GreaterEquals:
                // a >= b  ⇔  !(a < b)
                if (unsigned) _il.CltUn(); else _il.Clt();
                _il.LoadInt(0);
                _il.Ceq();
                break;
            case SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword:
                _il.And(); break;
            case SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword:
                _il.Or(); break;
        }
    }
}
