using System.Reflection.Metadata;
using CecilOpCode = Mono.Cecil.Cil.OpCode;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace Esharp.Emit;

/// <summary>
/// The knowledge table for the neutral opcode currency
/// (<see cref="System.Reflection.Metadata.ILOpCode"/>).
///
/// Mirrors Roslyn's <c>ILOpCodeExtensions</c> (ported, not guessed — see
/// <c>other/roslyn/src/Compilers/Core/Portable/CodeGen/ILOpCodeExtensions.cs</c>)
/// for the stack-behavior, control-transfer and short-form-folding facts, and
/// is the single translation point from the currency to <c>Mono.Cecil</c>'s
/// concrete <see cref="Mono.Cecil.Cil.OpCode"/> values.
///
/// <para><see cref="ILBuilder"/> consults this for every verb: it adjusts the
/// tracked stack via <see cref="NetStackBehavior"/>, picks short forms via
/// <see cref="FoldLoadInt"/>/<see cref="FoldLoadArg"/>/<see cref="FoldLoadLocal"/>/
/// <see cref="FoldStoreLocal"/>, and refuses raw control transfer
/// (<see cref="IsControlTransfer"/>) outside the branch verbs.</para>
/// </summary>
internal static class ILOpCodeFacts
{
    // ── Control-flow classification ─────────────────────────────────────────
    // IsBranch / IsConditionalBranch operand encodings come straight from the
    // BCL's own ILOpCodeExtensions (System.Reflection.Metadata): IsBranch(),
    // GetBranchOperandSize(), GetShortBranch(), GetLongBranch(). We reuse those
    // rather than re-table them. The semantic predicates below are Roslyn's.

    /// <summary>True for the branch family (br/brtrue/brfalse/beq/bge/…, short and long).</summary>
    public static bool IsBranch(this ILOpCode op) => ILOpCodeExtensions.IsBranch(op);

    /// <summary>
    /// Roslyn: opcodes that transfer control and must terminate a basic block —
    /// every branch, plus ret/throw/rethrow/endfilter/endfinally/switch/jmp.
    /// The <see cref="ILBuilder"/> only lets these through the dedicated verbs.
    /// </summary>
    public static bool IsControlTransfer(this ILOpCode op)
    {
        if (op.IsBranch())
            return true;

        switch (op)
        {
            case ILOpCode.Ret:
            case ILOpCode.Throw:
            case ILOpCode.Rethrow:
            case ILOpCode.Endfilter:
            case ILOpCode.Endfinally:
            case ILOpCode.Switch:
            case ILOpCode.Jmp:
                return true;
        }
        return false;
    }

    /// <summary>Roslyn: the conditional-branch subset (everything but br/br.s/leave/switch).</summary>
    public static bool IsConditionalBranch(this ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Brtrue:
            case ILOpCode.Brtrue_s:
            case ILOpCode.Brfalse:
            case ILOpCode.Brfalse_s:
            case ILOpCode.Beq:
            case ILOpCode.Beq_s:
            case ILOpCode.Bne_un:
            case ILOpCode.Bne_un_s:
            case ILOpCode.Bge:
            case ILOpCode.Bge_s:
            case ILOpCode.Bge_un:
            case ILOpCode.Bge_un_s:
            case ILOpCode.Bgt:
            case ILOpCode.Bgt_s:
            case ILOpCode.Bgt_un:
            case ILOpCode.Bgt_un_s:
            case ILOpCode.Ble:
            case ILOpCode.Ble_s:
            case ILOpCode.Ble_un:
            case ILOpCode.Ble_un_s:
            case ILOpCode.Blt:
            case ILOpCode.Blt_s:
            case ILOpCode.Blt_un:
            case ILOpCode.Blt_un_s:
                return true;
        }
        return false;
    }

    /// <summary>
    /// Roslyn: opcodes whose stack effect depends on the call signature
    /// (call/calli/callvirt/newobj/ret). The <see cref="NetStackBehavior"/>
    /// table is undefined for these — <see cref="ILBuilder"/> supplies the
    /// signature-derived adjustment explicitly.
    /// </summary>
    public static bool HasVariableStackBehavior(this ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Call:
            case ILOpCode.Calli:
            case ILOpCode.Callvirt:
            case ILOpCode.Newobj:
            case ILOpCode.Ret:
                return true;
        }
        return false;
    }

    /// <summary>
    /// ECMA-335 §12.4.2.8.1: instructions after which control may fall through.
    /// Only unconditional branches, ret/jmp/throw/rethrow/leave(.s)/endfinally do not.
    /// </summary>
    public static bool CanFallThrough(this ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Br:
            case ILOpCode.Br_s:
            case ILOpCode.Ret:
            case ILOpCode.Jmp:
            case ILOpCode.Throw:
            case ILOpCode.Endfinally:
            case ILOpCode.Leave:
            case ILOpCode.Leave_s:
            case ILOpCode.Rethrow:
                return false;
        }
        return true;
    }

    /// <summary>Net stack delta (push − pop). Invalid for <see cref="HasVariableStackBehavior"/> opcodes.</summary>
    public static int NetStackBehavior(this ILOpCode op) => StackPushCount(op) - StackPopCount(op);

    // ── Stack pop/push tables — ported verbatim from Roslyn ILOpCodeExtensions ──

    public static int StackPopCount(this ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Nop:
            case ILOpCode.Break:
            case ILOpCode.Ldarg_0:
            case ILOpCode.Ldarg_1:
            case ILOpCode.Ldarg_2:
            case ILOpCode.Ldarg_3:
            case ILOpCode.Ldloc_0:
            case ILOpCode.Ldloc_1:
            case ILOpCode.Ldloc_2:
            case ILOpCode.Ldloc_3:
                return 0;
            case ILOpCode.Stloc_0:
            case ILOpCode.Stloc_1:
            case ILOpCode.Stloc_2:
            case ILOpCode.Stloc_3:
                return 1;
            case ILOpCode.Ldarg_s:
            case ILOpCode.Ldarga_s:
                return 0;
            case ILOpCode.Starg_s:
                return 1;
            case ILOpCode.Ldloc_s:
            case ILOpCode.Ldloca_s:
                return 0;
            case ILOpCode.Stloc_s:
                return 1;
            case ILOpCode.Ldnull:
            case ILOpCode.Ldc_i4_m1:
            case ILOpCode.Ldc_i4_0:
            case ILOpCode.Ldc_i4_1:
            case ILOpCode.Ldc_i4_2:
            case ILOpCode.Ldc_i4_3:
            case ILOpCode.Ldc_i4_4:
            case ILOpCode.Ldc_i4_5:
            case ILOpCode.Ldc_i4_6:
            case ILOpCode.Ldc_i4_7:
            case ILOpCode.Ldc_i4_8:
            case ILOpCode.Ldc_i4_s:
            case ILOpCode.Ldc_i4:
            case ILOpCode.Ldc_i8:
            case ILOpCode.Ldc_r4:
            case ILOpCode.Ldc_r8:
                return 0;
            case ILOpCode.Dup:
            case ILOpCode.Pop:
                return 1;
            case ILOpCode.Jmp:
                return 0;
            case ILOpCode.Call:
            case ILOpCode.Calli:
            case ILOpCode.Ret:
                return -1; // Variable
            case ILOpCode.Br_s:
                return 0;
            case ILOpCode.Brfalse_s:
            case ILOpCode.Brtrue_s:
                return 1;
            case ILOpCode.Beq_s:
            case ILOpCode.Bge_s:
            case ILOpCode.Bgt_s:
            case ILOpCode.Ble_s:
            case ILOpCode.Blt_s:
            case ILOpCode.Bne_un_s:
            case ILOpCode.Bge_un_s:
            case ILOpCode.Bgt_un_s:
            case ILOpCode.Ble_un_s:
            case ILOpCode.Blt_un_s:
                return 2;
            case ILOpCode.Br:
                return 0;
            case ILOpCode.Brfalse:
            case ILOpCode.Brtrue:
                return 1;
            case ILOpCode.Beq:
            case ILOpCode.Bge:
            case ILOpCode.Bgt:
            case ILOpCode.Ble:
            case ILOpCode.Blt:
            case ILOpCode.Bne_un:
            case ILOpCode.Bge_un:
            case ILOpCode.Bgt_un:
            case ILOpCode.Ble_un:
            case ILOpCode.Blt_un:
                return 2;
            case ILOpCode.Switch:
            case ILOpCode.Ldind_i1:
            case ILOpCode.Ldind_u1:
            case ILOpCode.Ldind_i2:
            case ILOpCode.Ldind_u2:
            case ILOpCode.Ldind_i4:
            case ILOpCode.Ldind_u4:
            case ILOpCode.Ldind_i8:
            case ILOpCode.Ldind_i:
            case ILOpCode.Ldind_r4:
            case ILOpCode.Ldind_r8:
            case ILOpCode.Ldind_ref:
                return 1;
            case ILOpCode.Stind_ref:
            case ILOpCode.Stind_i1:
            case ILOpCode.Stind_i2:
            case ILOpCode.Stind_i4:
            case ILOpCode.Stind_i8:
            case ILOpCode.Stind_r4:
            case ILOpCode.Stind_r8:
            case ILOpCode.Add:
            case ILOpCode.Sub:
            case ILOpCode.Mul:
            case ILOpCode.Div:
            case ILOpCode.Div_un:
            case ILOpCode.Rem:
            case ILOpCode.Rem_un:
            case ILOpCode.And:
            case ILOpCode.Or:
            case ILOpCode.Xor:
            case ILOpCode.Shl:
            case ILOpCode.Shr:
            case ILOpCode.Shr_un:
                return 2;
            case ILOpCode.Neg:
            case ILOpCode.Not:
            case ILOpCode.Conv_i1:
            case ILOpCode.Conv_i2:
            case ILOpCode.Conv_i4:
            case ILOpCode.Conv_i8:
            case ILOpCode.Conv_r4:
            case ILOpCode.Conv_r8:
            case ILOpCode.Conv_u4:
            case ILOpCode.Conv_u8:
                return 1;
            case ILOpCode.Callvirt:
                return -1; // Variable
            case ILOpCode.Cpobj:
                return 2;
            case ILOpCode.Ldobj:
                return 1;
            case ILOpCode.Ldstr:
                return 0;
            case ILOpCode.Newobj:
                return -1; // Variable
            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
            case ILOpCode.Conv_r_un:
            case ILOpCode.Unbox:
            case ILOpCode.Throw:
            case ILOpCode.Ldfld:
            case ILOpCode.Ldflda:
                return 1;
            case ILOpCode.Stfld:
                return 2;
            case ILOpCode.Ldsfld:
            case ILOpCode.Ldsflda:
                return 0;
            case ILOpCode.Stsfld:
                return 1;
            case ILOpCode.Stobj:
                return 2;
            case ILOpCode.Conv_ovf_i1_un:
            case ILOpCode.Conv_ovf_i2_un:
            case ILOpCode.Conv_ovf_i4_un:
            case ILOpCode.Conv_ovf_i8_un:
            case ILOpCode.Conv_ovf_u1_un:
            case ILOpCode.Conv_ovf_u2_un:
            case ILOpCode.Conv_ovf_u4_un:
            case ILOpCode.Conv_ovf_u8_un:
            case ILOpCode.Conv_ovf_i_un:
            case ILOpCode.Conv_ovf_u_un:
            case ILOpCode.Box:
            case ILOpCode.Newarr:
            case ILOpCode.Ldlen:
                return 1;
            case ILOpCode.Ldelema:
            case ILOpCode.Ldelem_i1:
            case ILOpCode.Ldelem_u1:
            case ILOpCode.Ldelem_i2:
            case ILOpCode.Ldelem_u2:
            case ILOpCode.Ldelem_i4:
            case ILOpCode.Ldelem_u4:
            case ILOpCode.Ldelem_i8:
            case ILOpCode.Ldelem_i:
            case ILOpCode.Ldelem_r4:
            case ILOpCode.Ldelem_r8:
            case ILOpCode.Ldelem_ref:
                return 2;
            case ILOpCode.Stelem_i:
            case ILOpCode.Stelem_i1:
            case ILOpCode.Stelem_i2:
            case ILOpCode.Stelem_i4:
            case ILOpCode.Stelem_i8:
            case ILOpCode.Stelem_r4:
            case ILOpCode.Stelem_r8:
            case ILOpCode.Stelem_ref:
                return 3;
            case ILOpCode.Ldelem:
                return 2;
            case ILOpCode.Stelem:
                return 3;
            case ILOpCode.Unbox_any:
            case ILOpCode.Conv_ovf_i1:
            case ILOpCode.Conv_ovf_u1:
            case ILOpCode.Conv_ovf_i2:
            case ILOpCode.Conv_ovf_u2:
            case ILOpCode.Conv_ovf_i4:
            case ILOpCode.Conv_ovf_u4:
            case ILOpCode.Conv_ovf_i8:
            case ILOpCode.Conv_ovf_u8:
            case ILOpCode.Refanyval:
            case ILOpCode.Ckfinite:
            case ILOpCode.Mkrefany:
                return 1;
            case ILOpCode.Ldtoken:
                return 0;
            case ILOpCode.Conv_u2:
            case ILOpCode.Conv_u1:
            case ILOpCode.Conv_i:
            case ILOpCode.Conv_ovf_i:
            case ILOpCode.Conv_ovf_u:
                return 1;
            case ILOpCode.Add_ovf:
            case ILOpCode.Add_ovf_un:
            case ILOpCode.Mul_ovf:
            case ILOpCode.Mul_ovf_un:
            case ILOpCode.Sub_ovf:
            case ILOpCode.Sub_ovf_un:
                return 2;
            case ILOpCode.Endfinally:
            case ILOpCode.Leave:
            case ILOpCode.Leave_s:
                return 0;
            case ILOpCode.Stind_i:
                return 2;
            case ILOpCode.Conv_u:
                return 1;
            case ILOpCode.Arglist:
                return 0;
            case ILOpCode.Ceq:
            case ILOpCode.Cgt:
            case ILOpCode.Cgt_un:
            case ILOpCode.Clt:
            case ILOpCode.Clt_un:
                return 2;
            case ILOpCode.Ldftn:
                return 0;
            case ILOpCode.Ldvirtftn:
                return 1;
            case ILOpCode.Ldarg:
            case ILOpCode.Ldarga:
                return 0;
            case ILOpCode.Starg:
                return 1;
            case ILOpCode.Ldloc:
            case ILOpCode.Ldloca:
                return 0;
            case ILOpCode.Stloc:
            case ILOpCode.Localloc:
            case ILOpCode.Endfilter:
                return 1;
            case ILOpCode.Unaligned:
            case ILOpCode.Volatile:
            case ILOpCode.Tail:
                return 0;
            case ILOpCode.Initobj:
                return 1;
            case ILOpCode.Constrained:
                return 0;
            case ILOpCode.Cpblk:
            case ILOpCode.Initblk:
                return 3;
            case ILOpCode.Rethrow:
            case ILOpCode.Sizeof:
                return 0;
            case ILOpCode.Refanytype:
                return 1;
            case ILOpCode.Readonly:
                return 0;
        }

        throw new ArgumentOutOfRangeException(nameof(op), op, "unhandled opcode in StackPopCount");
    }

    public static int StackPushCount(this ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Nop:
            case ILOpCode.Break:
                return 0;
            case ILOpCode.Ldarg_0:
            case ILOpCode.Ldarg_1:
            case ILOpCode.Ldarg_2:
            case ILOpCode.Ldarg_3:
            case ILOpCode.Ldloc_0:
            case ILOpCode.Ldloc_1:
            case ILOpCode.Ldloc_2:
            case ILOpCode.Ldloc_3:
                return 1;
            case ILOpCode.Stloc_0:
            case ILOpCode.Stloc_1:
            case ILOpCode.Stloc_2:
            case ILOpCode.Stloc_3:
                return 0;
            case ILOpCode.Ldarg_s:
            case ILOpCode.Ldarga_s:
                return 1;
            case ILOpCode.Starg_s:
                return 0;
            case ILOpCode.Ldloc_s:
            case ILOpCode.Ldloca_s:
                return 1;
            case ILOpCode.Stloc_s:
                return 0;
            case ILOpCode.Ldnull:
            case ILOpCode.Ldc_i4_m1:
            case ILOpCode.Ldc_i4_0:
            case ILOpCode.Ldc_i4_1:
            case ILOpCode.Ldc_i4_2:
            case ILOpCode.Ldc_i4_3:
            case ILOpCode.Ldc_i4_4:
            case ILOpCode.Ldc_i4_5:
            case ILOpCode.Ldc_i4_6:
            case ILOpCode.Ldc_i4_7:
            case ILOpCode.Ldc_i4_8:
            case ILOpCode.Ldc_i4_s:
            case ILOpCode.Ldc_i4:
            case ILOpCode.Ldc_i8:
            case ILOpCode.Ldc_r4:
            case ILOpCode.Ldc_r8:
                return 1;
            case ILOpCode.Dup:
                return 2;
            case ILOpCode.Pop:
            case ILOpCode.Jmp:
                return 0;
            case ILOpCode.Call:
            case ILOpCode.Calli:
                return -1; // Variable
            case ILOpCode.Ret:
            case ILOpCode.Br_s:
            case ILOpCode.Brfalse_s:
            case ILOpCode.Brtrue_s:
            case ILOpCode.Beq_s:
            case ILOpCode.Bge_s:
            case ILOpCode.Bgt_s:
            case ILOpCode.Ble_s:
            case ILOpCode.Blt_s:
            case ILOpCode.Bne_un_s:
            case ILOpCode.Bge_un_s:
            case ILOpCode.Bgt_un_s:
            case ILOpCode.Ble_un_s:
            case ILOpCode.Blt_un_s:
            case ILOpCode.Br:
            case ILOpCode.Brfalse:
            case ILOpCode.Brtrue:
            case ILOpCode.Beq:
            case ILOpCode.Bge:
            case ILOpCode.Bgt:
            case ILOpCode.Ble:
            case ILOpCode.Blt:
            case ILOpCode.Bne_un:
            case ILOpCode.Bge_un:
            case ILOpCode.Bgt_un:
            case ILOpCode.Ble_un:
            case ILOpCode.Blt_un:
            case ILOpCode.Switch:
                return 0;
            case ILOpCode.Ldind_i1:
            case ILOpCode.Ldind_u1:
            case ILOpCode.Ldind_i2:
            case ILOpCode.Ldind_u2:
            case ILOpCode.Ldind_i4:
            case ILOpCode.Ldind_u4:
            case ILOpCode.Ldind_i8:
            case ILOpCode.Ldind_i:
            case ILOpCode.Ldind_r4:
            case ILOpCode.Ldind_r8:
            case ILOpCode.Ldind_ref:
                return 1;
            case ILOpCode.Stind_ref:
            case ILOpCode.Stind_i1:
            case ILOpCode.Stind_i2:
            case ILOpCode.Stind_i4:
            case ILOpCode.Stind_i8:
            case ILOpCode.Stind_r4:
            case ILOpCode.Stind_r8:
                return 0;
            case ILOpCode.Add:
            case ILOpCode.Sub:
            case ILOpCode.Mul:
            case ILOpCode.Div:
            case ILOpCode.Div_un:
            case ILOpCode.Rem:
            case ILOpCode.Rem_un:
            case ILOpCode.And:
            case ILOpCode.Or:
            case ILOpCode.Xor:
            case ILOpCode.Shl:
            case ILOpCode.Shr:
            case ILOpCode.Shr_un:
            case ILOpCode.Neg:
            case ILOpCode.Not:
            case ILOpCode.Conv_i1:
            case ILOpCode.Conv_i2:
            case ILOpCode.Conv_i4:
            case ILOpCode.Conv_i8:
            case ILOpCode.Conv_r4:
            case ILOpCode.Conv_r8:
            case ILOpCode.Conv_u4:
            case ILOpCode.Conv_u8:
                return 1;
            case ILOpCode.Callvirt:
                return -1; // Variable
            case ILOpCode.Cpobj:
                return 0;
            case ILOpCode.Ldobj:
            case ILOpCode.Ldstr:
            case ILOpCode.Newobj:
            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
            case ILOpCode.Conv_r_un:
            case ILOpCode.Unbox:
                return 1;
            case ILOpCode.Throw:
                return 0;
            case ILOpCode.Ldfld:
            case ILOpCode.Ldflda:
                return 1;
            case ILOpCode.Stfld:
                return 0;
            case ILOpCode.Ldsfld:
            case ILOpCode.Ldsflda:
                return 1;
            case ILOpCode.Stsfld:
            case ILOpCode.Stobj:
                return 0;
            case ILOpCode.Conv_ovf_i1_un:
            case ILOpCode.Conv_ovf_i2_un:
            case ILOpCode.Conv_ovf_i4_un:
            case ILOpCode.Conv_ovf_i8_un:
            case ILOpCode.Conv_ovf_u1_un:
            case ILOpCode.Conv_ovf_u2_un:
            case ILOpCode.Conv_ovf_u4_un:
            case ILOpCode.Conv_ovf_u8_un:
            case ILOpCode.Conv_ovf_i_un:
            case ILOpCode.Conv_ovf_u_un:
            case ILOpCode.Box:
            case ILOpCode.Newarr:
            case ILOpCode.Ldlen:
            case ILOpCode.Ldelema:
            case ILOpCode.Ldelem_i1:
            case ILOpCode.Ldelem_u1:
            case ILOpCode.Ldelem_i2:
            case ILOpCode.Ldelem_u2:
            case ILOpCode.Ldelem_i4:
            case ILOpCode.Ldelem_u4:
            case ILOpCode.Ldelem_i8:
            case ILOpCode.Ldelem_i:
            case ILOpCode.Ldelem_r4:
            case ILOpCode.Ldelem_r8:
            case ILOpCode.Ldelem_ref:
                return 1;
            case ILOpCode.Stelem_i:
            case ILOpCode.Stelem_i1:
            case ILOpCode.Stelem_i2:
            case ILOpCode.Stelem_i4:
            case ILOpCode.Stelem_i8:
            case ILOpCode.Stelem_r4:
            case ILOpCode.Stelem_r8:
            case ILOpCode.Stelem_ref:
                return 0;
            case ILOpCode.Ldelem:
                return 1;
            case ILOpCode.Stelem:
                return 0;
            case ILOpCode.Unbox_any:
            case ILOpCode.Conv_ovf_i1:
            case ILOpCode.Conv_ovf_u1:
            case ILOpCode.Conv_ovf_i2:
            case ILOpCode.Conv_ovf_u2:
            case ILOpCode.Conv_ovf_i4:
            case ILOpCode.Conv_ovf_u4:
            case ILOpCode.Conv_ovf_i8:
            case ILOpCode.Conv_ovf_u8:
            case ILOpCode.Refanyval:
            case ILOpCode.Ckfinite:
            case ILOpCode.Mkrefany:
            case ILOpCode.Ldtoken:
            case ILOpCode.Conv_u2:
            case ILOpCode.Conv_u1:
            case ILOpCode.Conv_i:
            case ILOpCode.Conv_ovf_i:
            case ILOpCode.Conv_ovf_u:
            case ILOpCode.Add_ovf:
            case ILOpCode.Add_ovf_un:
            case ILOpCode.Mul_ovf:
            case ILOpCode.Mul_ovf_un:
            case ILOpCode.Sub_ovf:
            case ILOpCode.Sub_ovf_un:
                return 1;
            case ILOpCode.Endfinally:
            case ILOpCode.Leave:
            case ILOpCode.Leave_s:
            case ILOpCode.Stind_i:
                return 0;
            case ILOpCode.Conv_u:
            case ILOpCode.Arglist:
            case ILOpCode.Ceq:
            case ILOpCode.Cgt:
            case ILOpCode.Cgt_un:
            case ILOpCode.Clt:
            case ILOpCode.Clt_un:
            case ILOpCode.Ldftn:
            case ILOpCode.Ldvirtftn:
            case ILOpCode.Ldarg:
            case ILOpCode.Ldarga:
                return 1;
            case ILOpCode.Starg:
                return 0;
            case ILOpCode.Ldloc:
            case ILOpCode.Ldloca:
                return 1;
            case ILOpCode.Stloc:
                return 0;
            case ILOpCode.Localloc:
                return 1;
            case ILOpCode.Endfilter:
            case ILOpCode.Unaligned:
            case ILOpCode.Volatile:
            case ILOpCode.Tail:
            case ILOpCode.Initobj:
            case ILOpCode.Constrained:
            case ILOpCode.Cpblk:
            case ILOpCode.Initblk:
            case ILOpCode.Rethrow:
                return 0;
            case ILOpCode.Sizeof:
            case ILOpCode.Refanytype:
                return 1;
            case ILOpCode.Readonly:
                return 0;
        }

        throw new ArgumentOutOfRangeException(nameof(op), op, "unhandled opcode in StackPushCount");
    }

    // ── Short-form folding ──────────────────────────────────────────────────
    // The verbs own operand-encoding choice. Cecil shortens *branch* forms at
    // write time (Body.OptimizeMacros / ILOptimizer), so we only fold the
    // operand-bearing load/store/const families whose form is a function of the
    // operand value, exactly as Roslyn's ILBuilderEmit does.

    /// <summary>ldc.i4 → the narrowest constant-load form (m1/0..8, then .s, then full).</summary>
    public static ILOpCode FoldLoadInt(int value)
    {
        switch (value)
        {
            case -1: return ILOpCode.Ldc_i4_m1;
            case 0: return ILOpCode.Ldc_i4_0;
            case 1: return ILOpCode.Ldc_i4_1;
            case 2: return ILOpCode.Ldc_i4_2;
            case 3: return ILOpCode.Ldc_i4_3;
            case 4: return ILOpCode.Ldc_i4_4;
            case 5: return ILOpCode.Ldc_i4_5;
            case 6: return ILOpCode.Ldc_i4_6;
            case 7: return ILOpCode.Ldc_i4_7;
            case 8: return ILOpCode.Ldc_i4_8;
        }
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
            return ILOpCode.Ldc_i4_s;
        return ILOpCode.Ldc_i4;
    }

    /// <summary>ldarg → ldarg.0..3 / ldarg.s / ldarg by index.</summary>
    public static ILOpCode FoldLoadArg(int index) => index switch
    {
        0 => ILOpCode.Ldarg_0,
        1 => ILOpCode.Ldarg_1,
        2 => ILOpCode.Ldarg_2,
        3 => ILOpCode.Ldarg_3,
        <= 0xff => ILOpCode.Ldarg_s,
        _ => ILOpCode.Ldarg,
    };

    /// <summary>ldarga → ldarga.s / ldarga.</summary>
    public static ILOpCode FoldLoadArgAddress(int index) =>
        index <= 0xff ? ILOpCode.Ldarga_s : ILOpCode.Ldarga;

    /// <summary>starg → starg.s / starg.</summary>
    public static ILOpCode FoldStoreArg(int index) =>
        index <= 0xff ? ILOpCode.Starg_s : ILOpCode.Starg;

    /// <summary>ldloc → ldloc.0..3 / ldloc.s / ldloc.</summary>
    public static ILOpCode FoldLoadLocal(int index) => index switch
    {
        0 => ILOpCode.Ldloc_0,
        1 => ILOpCode.Ldloc_1,
        2 => ILOpCode.Ldloc_2,
        3 => ILOpCode.Ldloc_3,
        <= 0xff => ILOpCode.Ldloc_s,
        _ => ILOpCode.Ldloc,
    };

    /// <summary>ldloca → ldloca.s / ldloca.</summary>
    public static ILOpCode FoldLoadLocalAddress(int index) =>
        index <= 0xff ? ILOpCode.Ldloca_s : ILOpCode.Ldloca;

    /// <summary>stloc → stloc.0..3 / stloc.s / stloc.</summary>
    public static ILOpCode FoldStoreLocal(int index) => index switch
    {
        0 => ILOpCode.Stloc_0,
        1 => ILOpCode.Stloc_1,
        2 => ILOpCode.Stloc_2,
        3 => ILOpCode.Stloc_3,
        <= 0xff => ILOpCode.Stloc_s,
        _ => ILOpCode.Stloc,
    };

    // ── The single translation point to Mono.Cecil ──────────────────────────
    // Every currency opcode the emitter can produce maps here, and ONLY here.
    // Keep exhaustive over the set our codegen emits; the default throws so a
    // missing mapping is loud rather than silent.

    public static CecilOpCode ToCecil(this ILOpCode op) => op switch
    {
        ILOpCode.Nop => CecilOpCodes.Nop,
        ILOpCode.Break => CecilOpCodes.Break,

        // ── args ──
        ILOpCode.Ldarg_0 => CecilOpCodes.Ldarg_0,
        ILOpCode.Ldarg_1 => CecilOpCodes.Ldarg_1,
        ILOpCode.Ldarg_2 => CecilOpCodes.Ldarg_2,
        ILOpCode.Ldarg_3 => CecilOpCodes.Ldarg_3,
        ILOpCode.Ldarg_s => CecilOpCodes.Ldarg_S,
        ILOpCode.Ldarg => CecilOpCodes.Ldarg,
        ILOpCode.Ldarga_s => CecilOpCodes.Ldarga_S,
        ILOpCode.Ldarga => CecilOpCodes.Ldarga,
        ILOpCode.Starg_s => CecilOpCodes.Starg_S,
        ILOpCode.Starg => CecilOpCodes.Starg,

        // ── locals ──
        ILOpCode.Ldloc_0 => CecilOpCodes.Ldloc_0,
        ILOpCode.Ldloc_1 => CecilOpCodes.Ldloc_1,
        ILOpCode.Ldloc_2 => CecilOpCodes.Ldloc_2,
        ILOpCode.Ldloc_3 => CecilOpCodes.Ldloc_3,
        ILOpCode.Ldloc_s => CecilOpCodes.Ldloc_S,
        ILOpCode.Ldloc => CecilOpCodes.Ldloc,
        ILOpCode.Ldloca_s => CecilOpCodes.Ldloca_S,
        ILOpCode.Ldloca => CecilOpCodes.Ldloca,
        ILOpCode.Stloc_0 => CecilOpCodes.Stloc_0,
        ILOpCode.Stloc_1 => CecilOpCodes.Stloc_1,
        ILOpCode.Stloc_2 => CecilOpCodes.Stloc_2,
        ILOpCode.Stloc_3 => CecilOpCodes.Stloc_3,
        ILOpCode.Stloc_s => CecilOpCodes.Stloc_S,
        ILOpCode.Stloc => CecilOpCodes.Stloc,

        // ── constants ──
        ILOpCode.Ldnull => CecilOpCodes.Ldnull,
        ILOpCode.Ldc_i4_m1 => CecilOpCodes.Ldc_I4_M1,
        ILOpCode.Ldc_i4_0 => CecilOpCodes.Ldc_I4_0,
        ILOpCode.Ldc_i4_1 => CecilOpCodes.Ldc_I4_1,
        ILOpCode.Ldc_i4_2 => CecilOpCodes.Ldc_I4_2,
        ILOpCode.Ldc_i4_3 => CecilOpCodes.Ldc_I4_3,
        ILOpCode.Ldc_i4_4 => CecilOpCodes.Ldc_I4_4,
        ILOpCode.Ldc_i4_5 => CecilOpCodes.Ldc_I4_5,
        ILOpCode.Ldc_i4_6 => CecilOpCodes.Ldc_I4_6,
        ILOpCode.Ldc_i4_7 => CecilOpCodes.Ldc_I4_7,
        ILOpCode.Ldc_i4_8 => CecilOpCodes.Ldc_I4_8,
        ILOpCode.Ldc_i4_s => CecilOpCodes.Ldc_I4_S,
        ILOpCode.Ldc_i4 => CecilOpCodes.Ldc_I4,
        ILOpCode.Ldc_i8 => CecilOpCodes.Ldc_I8,
        ILOpCode.Ldc_r4 => CecilOpCodes.Ldc_R4,
        ILOpCode.Ldc_r8 => CecilOpCodes.Ldc_R8,
        ILOpCode.Ldstr => CecilOpCodes.Ldstr,

        // ── stack ──
        ILOpCode.Dup => CecilOpCodes.Dup,
        ILOpCode.Pop => CecilOpCodes.Pop,

        // ── calls / construction ──
        ILOpCode.Jmp => CecilOpCodes.Jmp,
        ILOpCode.Call => CecilOpCodes.Call,
        ILOpCode.Calli => CecilOpCodes.Calli,
        ILOpCode.Callvirt => CecilOpCodes.Callvirt,
        ILOpCode.Ret => CecilOpCodes.Ret,
        ILOpCode.Newobj => CecilOpCodes.Newobj,
        ILOpCode.Ldftn => CecilOpCodes.Ldftn,
        ILOpCode.Ldvirtftn => CecilOpCodes.Ldvirtftn,

        // ── branches (short + long; Cecil shortens at write time) ──
        ILOpCode.Br_s => CecilOpCodes.Br_S,
        ILOpCode.Br => CecilOpCodes.Br,
        ILOpCode.Brfalse_s => CecilOpCodes.Brfalse_S,
        ILOpCode.Brfalse => CecilOpCodes.Brfalse,
        ILOpCode.Brtrue_s => CecilOpCodes.Brtrue_S,
        ILOpCode.Brtrue => CecilOpCodes.Brtrue,
        ILOpCode.Beq_s => CecilOpCodes.Beq_S,
        ILOpCode.Beq => CecilOpCodes.Beq,
        ILOpCode.Bne_un_s => CecilOpCodes.Bne_Un_S,
        ILOpCode.Bne_un => CecilOpCodes.Bne_Un,
        ILOpCode.Bge_s => CecilOpCodes.Bge_S,
        ILOpCode.Bge => CecilOpCodes.Bge,
        ILOpCode.Bge_un_s => CecilOpCodes.Bge_Un_S,
        ILOpCode.Bge_un => CecilOpCodes.Bge_Un,
        ILOpCode.Bgt_s => CecilOpCodes.Bgt_S,
        ILOpCode.Bgt => CecilOpCodes.Bgt,
        ILOpCode.Bgt_un_s => CecilOpCodes.Bgt_Un_S,
        ILOpCode.Bgt_un => CecilOpCodes.Bgt_Un,
        ILOpCode.Ble_s => CecilOpCodes.Ble_S,
        ILOpCode.Ble => CecilOpCodes.Ble,
        ILOpCode.Ble_un_s => CecilOpCodes.Ble_Un_S,
        ILOpCode.Ble_un => CecilOpCodes.Ble_Un,
        ILOpCode.Blt_s => CecilOpCodes.Blt_S,
        ILOpCode.Blt => CecilOpCodes.Blt,
        ILOpCode.Blt_un_s => CecilOpCodes.Blt_Un_S,
        ILOpCode.Blt_un => CecilOpCodes.Blt_Un,
        ILOpCode.Switch => CecilOpCodes.Switch,
        ILOpCode.Leave_s => CecilOpCodes.Leave_S,
        ILOpCode.Leave => CecilOpCodes.Leave,
        ILOpCode.Endfinally => CecilOpCodes.Endfinally,
        ILOpCode.Endfilter => CecilOpCodes.Endfilter,
        ILOpCode.Throw => CecilOpCodes.Throw,
        ILOpCode.Rethrow => CecilOpCodes.Rethrow,

        // ── indirect load/store ──
        ILOpCode.Ldind_i1 => CecilOpCodes.Ldind_I1,
        ILOpCode.Ldind_u1 => CecilOpCodes.Ldind_U1,
        ILOpCode.Ldind_i2 => CecilOpCodes.Ldind_I2,
        ILOpCode.Ldind_u2 => CecilOpCodes.Ldind_U2,
        ILOpCode.Ldind_i4 => CecilOpCodes.Ldind_I4,
        ILOpCode.Ldind_u4 => CecilOpCodes.Ldind_U4,
        ILOpCode.Ldind_i8 => CecilOpCodes.Ldind_I8,
        ILOpCode.Ldind_i => CecilOpCodes.Ldind_I,
        ILOpCode.Ldind_r4 => CecilOpCodes.Ldind_R4,
        ILOpCode.Ldind_r8 => CecilOpCodes.Ldind_R8,
        ILOpCode.Ldind_ref => CecilOpCodes.Ldind_Ref,
        ILOpCode.Stind_ref => CecilOpCodes.Stind_Ref,
        ILOpCode.Stind_i1 => CecilOpCodes.Stind_I1,
        ILOpCode.Stind_i2 => CecilOpCodes.Stind_I2,
        ILOpCode.Stind_i4 => CecilOpCodes.Stind_I4,
        ILOpCode.Stind_i8 => CecilOpCodes.Stind_I8,
        ILOpCode.Stind_r4 => CecilOpCodes.Stind_R4,
        ILOpCode.Stind_r8 => CecilOpCodes.Stind_R8,
        ILOpCode.Stind_i => CecilOpCodes.Stind_I,

        // ── arithmetic / logic ──
        ILOpCode.Add => CecilOpCodes.Add,
        ILOpCode.Sub => CecilOpCodes.Sub,
        ILOpCode.Mul => CecilOpCodes.Mul,
        ILOpCode.Div => CecilOpCodes.Div,
        ILOpCode.Div_un => CecilOpCodes.Div_Un,
        ILOpCode.Rem => CecilOpCodes.Rem,
        ILOpCode.Rem_un => CecilOpCodes.Rem_Un,
        ILOpCode.And => CecilOpCodes.And,
        ILOpCode.Or => CecilOpCodes.Or,
        ILOpCode.Xor => CecilOpCodes.Xor,
        ILOpCode.Shl => CecilOpCodes.Shl,
        ILOpCode.Shr => CecilOpCodes.Shr,
        ILOpCode.Shr_un => CecilOpCodes.Shr_Un,
        ILOpCode.Neg => CecilOpCodes.Neg,
        ILOpCode.Not => CecilOpCodes.Not,
        ILOpCode.Add_ovf => CecilOpCodes.Add_Ovf,
        ILOpCode.Add_ovf_un => CecilOpCodes.Add_Ovf_Un,
        ILOpCode.Mul_ovf => CecilOpCodes.Mul_Ovf,
        ILOpCode.Mul_ovf_un => CecilOpCodes.Mul_Ovf_Un,
        ILOpCode.Sub_ovf => CecilOpCodes.Sub_Ovf,
        ILOpCode.Sub_ovf_un => CecilOpCodes.Sub_Ovf_Un,

        // ── compare ──
        ILOpCode.Ceq => CecilOpCodes.Ceq,
        ILOpCode.Cgt => CecilOpCodes.Cgt,
        ILOpCode.Cgt_un => CecilOpCodes.Cgt_Un,
        ILOpCode.Clt => CecilOpCodes.Clt,
        ILOpCode.Clt_un => CecilOpCodes.Clt_Un,

        // ── conversions ──
        ILOpCode.Conv_i1 => CecilOpCodes.Conv_I1,
        ILOpCode.Conv_i2 => CecilOpCodes.Conv_I2,
        ILOpCode.Conv_i4 => CecilOpCodes.Conv_I4,
        ILOpCode.Conv_i8 => CecilOpCodes.Conv_I8,
        ILOpCode.Conv_r4 => CecilOpCodes.Conv_R4,
        ILOpCode.Conv_r8 => CecilOpCodes.Conv_R8,
        ILOpCode.Conv_u4 => CecilOpCodes.Conv_U4,
        ILOpCode.Conv_u8 => CecilOpCodes.Conv_U8,
        ILOpCode.Conv_r_un => CecilOpCodes.Conv_R_Un,
        ILOpCode.Conv_u2 => CecilOpCodes.Conv_U2,
        ILOpCode.Conv_u1 => CecilOpCodes.Conv_U1,
        ILOpCode.Conv_i => CecilOpCodes.Conv_I,
        ILOpCode.Conv_u => CecilOpCodes.Conv_U,
        ILOpCode.Conv_ovf_i1 => CecilOpCodes.Conv_Ovf_I1,
        ILOpCode.Conv_ovf_u1 => CecilOpCodes.Conv_Ovf_U1,
        ILOpCode.Conv_ovf_i2 => CecilOpCodes.Conv_Ovf_I2,
        ILOpCode.Conv_ovf_u2 => CecilOpCodes.Conv_Ovf_U2,
        ILOpCode.Conv_ovf_i4 => CecilOpCodes.Conv_Ovf_I4,
        ILOpCode.Conv_ovf_u4 => CecilOpCodes.Conv_Ovf_U4,
        ILOpCode.Conv_ovf_i8 => CecilOpCodes.Conv_Ovf_I8,
        ILOpCode.Conv_ovf_u8 => CecilOpCodes.Conv_Ovf_U8,
        ILOpCode.Conv_ovf_i => CecilOpCodes.Conv_Ovf_I,
        ILOpCode.Conv_ovf_u => CecilOpCodes.Conv_Ovf_U,
        ILOpCode.Conv_ovf_i1_un => CecilOpCodes.Conv_Ovf_I1_Un,
        ILOpCode.Conv_ovf_i2_un => CecilOpCodes.Conv_Ovf_I2_Un,
        ILOpCode.Conv_ovf_i4_un => CecilOpCodes.Conv_Ovf_I4_Un,
        ILOpCode.Conv_ovf_i8_un => CecilOpCodes.Conv_Ovf_I8_Un,
        ILOpCode.Conv_ovf_u1_un => CecilOpCodes.Conv_Ovf_U1_Un,
        ILOpCode.Conv_ovf_u2_un => CecilOpCodes.Conv_Ovf_U2_Un,
        ILOpCode.Conv_ovf_u4_un => CecilOpCodes.Conv_Ovf_U4_Un,
        ILOpCode.Conv_ovf_u8_un => CecilOpCodes.Conv_Ovf_U8_Un,
        ILOpCode.Conv_ovf_i_un => CecilOpCodes.Conv_Ovf_I_Un,
        ILOpCode.Conv_ovf_u_un => CecilOpCodes.Conv_Ovf_U_Un,
        ILOpCode.Ckfinite => CecilOpCodes.Ckfinite,

        // ── object model ──
        ILOpCode.Cpobj => CecilOpCodes.Cpobj,
        ILOpCode.Ldobj => CecilOpCodes.Ldobj,
        ILOpCode.Stobj => CecilOpCodes.Stobj,
        ILOpCode.Castclass => CecilOpCodes.Castclass,
        ILOpCode.Isinst => CecilOpCodes.Isinst,
        ILOpCode.Box => CecilOpCodes.Box,
        ILOpCode.Unbox => CecilOpCodes.Unbox,
        ILOpCode.Unbox_any => CecilOpCodes.Unbox_Any,
        ILOpCode.Ldfld => CecilOpCodes.Ldfld,
        ILOpCode.Ldflda => CecilOpCodes.Ldflda,
        ILOpCode.Stfld => CecilOpCodes.Stfld,
        ILOpCode.Ldsfld => CecilOpCodes.Ldsfld,
        ILOpCode.Ldsflda => CecilOpCodes.Ldsflda,
        ILOpCode.Stsfld => CecilOpCodes.Stsfld,
        ILOpCode.Initobj => CecilOpCodes.Initobj,
        ILOpCode.Constrained => CecilOpCodes.Constrained,
        ILOpCode.Ldtoken => CecilOpCodes.Ldtoken,
        ILOpCode.Sizeof => CecilOpCodes.Sizeof,
        ILOpCode.Mkrefany => CecilOpCodes.Mkrefany,
        ILOpCode.Refanyval => CecilOpCodes.Refanyval,
        ILOpCode.Refanytype => CecilOpCodes.Refanytype,
        ILOpCode.Arglist => CecilOpCodes.Arglist,

        // ── arrays ──
        ILOpCode.Newarr => CecilOpCodes.Newarr,
        ILOpCode.Ldlen => CecilOpCodes.Ldlen,
        ILOpCode.Ldelema => CecilOpCodes.Ldelema,
        ILOpCode.Ldelem_i1 => CecilOpCodes.Ldelem_I1,
        ILOpCode.Ldelem_u1 => CecilOpCodes.Ldelem_U1,
        ILOpCode.Ldelem_i2 => CecilOpCodes.Ldelem_I2,
        ILOpCode.Ldelem_u2 => CecilOpCodes.Ldelem_U2,
        ILOpCode.Ldelem_i4 => CecilOpCodes.Ldelem_I4,
        ILOpCode.Ldelem_u4 => CecilOpCodes.Ldelem_U4,
        ILOpCode.Ldelem_i8 => CecilOpCodes.Ldelem_I8,
        ILOpCode.Ldelem_i => CecilOpCodes.Ldelem_I,
        ILOpCode.Ldelem_r4 => CecilOpCodes.Ldelem_R4,
        ILOpCode.Ldelem_r8 => CecilOpCodes.Ldelem_R8,
        ILOpCode.Ldelem_ref => CecilOpCodes.Ldelem_Ref,
        ILOpCode.Ldelem => CecilOpCodes.Ldelem_Any,
        ILOpCode.Stelem_i => CecilOpCodes.Stelem_I,
        ILOpCode.Stelem_i1 => CecilOpCodes.Stelem_I1,
        ILOpCode.Stelem_i2 => CecilOpCodes.Stelem_I2,
        ILOpCode.Stelem_i4 => CecilOpCodes.Stelem_I4,
        ILOpCode.Stelem_i8 => CecilOpCodes.Stelem_I8,
        ILOpCode.Stelem_r4 => CecilOpCodes.Stelem_R4,
        ILOpCode.Stelem_r8 => CecilOpCodes.Stelem_R8,
        ILOpCode.Stelem_ref => CecilOpCodes.Stelem_Ref,
        ILOpCode.Stelem => CecilOpCodes.Stelem_Any,

        // ── memory / prefixes ──
        ILOpCode.Localloc => CecilOpCodes.Localloc,
        ILOpCode.Cpblk => CecilOpCodes.Cpblk,
        ILOpCode.Initblk => CecilOpCodes.Initblk,
        ILOpCode.Unaligned => CecilOpCodes.Unaligned,
        ILOpCode.Volatile => CecilOpCodes.Volatile,
        ILOpCode.Tail => CecilOpCodes.Tail,
        ILOpCode.Readonly => CecilOpCodes.Readonly,

        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "no Mono.Cecil mapping for opcode"),
    };
}
