using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Esharp.ILEmit;

/// <summary>
/// Post-pass IL optimizations. Applied after method emission, before assembly write.
/// </summary>
public static class ILOptimizer
{
    /// <summary>
    /// Replace long-form opcodes with short-form equivalents where possible.
    /// ldc.i4 → ldc.i4.N / ldc.i4.s, ldloc/stloc → .N / .s, ldarg → .N / .s, br/brfalse/brtrue → .s
    /// </summary>
    public static void ShortenOpcodes(MethodBody body)
    {
        var il = body.GetILProcessor();
        var instructions = body.Instructions;

        for (var i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];

            // ldc.i4 → compact forms
            if (instr.OpCode == OpCodes.Ldc_I4 && instr.Operand is int val)
            {
                var replacement = val switch
                {
                    -1 => il.Create(OpCodes.Ldc_I4_M1),
                    0 => il.Create(OpCodes.Ldc_I4_0),
                    1 => il.Create(OpCodes.Ldc_I4_1),
                    2 => il.Create(OpCodes.Ldc_I4_2),
                    3 => il.Create(OpCodes.Ldc_I4_3),
                    4 => il.Create(OpCodes.Ldc_I4_4),
                    5 => il.Create(OpCodes.Ldc_I4_5),
                    6 => il.Create(OpCodes.Ldc_I4_6),
                    7 => il.Create(OpCodes.Ldc_I4_7),
                    8 => il.Create(OpCodes.Ldc_I4_8),
                    >= -128 and <= 127 => il.Create(OpCodes.Ldc_I4_S, (sbyte)val),
                    _ => null,
                };
                if (replacement is not null)
                    Replace(il, instr, replacement);
            }
            // ldloc V → ldloc.N / ldloc.s
            else if (instr.OpCode == OpCodes.Ldloc && instr.Operand is VariableDefinition ldlocVar)
            {
                var idx = ldlocVar.Index;
                var replacement = idx switch
                {
                    0 => il.Create(OpCodes.Ldloc_0),
                    1 => il.Create(OpCodes.Ldloc_1),
                    2 => il.Create(OpCodes.Ldloc_2),
                    3 => il.Create(OpCodes.Ldloc_3),
                    < 256 => il.Create(OpCodes.Ldloc_S, ldlocVar),
                    _ => null,
                };
                if (replacement is not null)
                    Replace(il, instr, replacement);
            }
            // stloc V → stloc.N / stloc.s
            else if (instr.OpCode == OpCodes.Stloc && instr.Operand is VariableDefinition stlocVar)
            {
                var idx = stlocVar.Index;
                var replacement = idx switch
                {
                    0 => il.Create(OpCodes.Stloc_0),
                    1 => il.Create(OpCodes.Stloc_1),
                    2 => il.Create(OpCodes.Stloc_2),
                    3 => il.Create(OpCodes.Stloc_3),
                    < 256 => il.Create(OpCodes.Stloc_S, stlocVar),
                    _ => null,
                };
                if (replacement is not null)
                    Replace(il, instr, replacement);
            }
            // ldloca V → ldloca.s
            else if (instr.OpCode == OpCodes.Ldloca && instr.Operand is VariableDefinition ldlocaVar && ldlocaVar.Index < 256)
            {
                Replace(il, instr, il.Create(OpCodes.Ldloca_S, ldlocaVar));
            }
            // ldarg P → ldarg.N / ldarg.s
            else if (instr.OpCode == OpCodes.Ldarg && instr.Operand is ParameterDefinition ldargParam)
            {
                var idx = ldargParam.Index;
                var replacement = idx switch
                {
                    0 => il.Create(OpCodes.Ldarg_0),
                    1 => il.Create(OpCodes.Ldarg_1),
                    2 => il.Create(OpCodes.Ldarg_2),
                    3 => il.Create(OpCodes.Ldarg_3),
                    < 256 => il.Create(OpCodes.Ldarg_S, ldargParam),
                    _ => null,
                };
                if (replacement is not null)
                    Replace(il, instr, replacement);
            }
            // starg P → starg.s
            else if (instr.OpCode == OpCodes.Starg && instr.Operand is ParameterDefinition stargParam && stargParam.Index < 256)
            {
                Replace(il, instr, il.Create(OpCodes.Starg_S, stargParam));
            }
            // ldarga P → ldarga.s
            else if (instr.OpCode == OpCodes.Ldarga && instr.Operand is ParameterDefinition ldargaParam && ldargaParam.Index < 256)
            {
                Replace(il, instr, il.Create(OpCodes.Ldarga_S, ldargaParam));
            }
        }

        // Second pass: shorten branch instructions where offset fits in sbyte.
        // Must be done after opcode shortening since instruction sizes changed.
        ShortenBranches(body);
    }

    static void ShortenBranches(MethodBody body)
    {
        var il = body.GetILProcessor();
        // Map long-form branch → short-form
        ReadOnlySpan<(OpCode from, OpCode to)> branchMap =
        [
            (OpCodes.Br, OpCodes.Br_S),
            (OpCodes.Brfalse, OpCodes.Brfalse_S),
            (OpCodes.Brtrue, OpCodes.Brtrue_S),
            (OpCodes.Beq, OpCodes.Beq_S),
            (OpCodes.Bge, OpCodes.Bge_S),
            (OpCodes.Bgt, OpCodes.Bgt_S),
            (OpCodes.Ble, OpCodes.Ble_S),
            (OpCodes.Blt, OpCodes.Blt_S),
            (OpCodes.Bne_Un, OpCodes.Bne_Un_S),
            (OpCodes.Bge_Un, OpCodes.Bge_Un_S),
            (OpCodes.Bgt_Un, OpCodes.Bgt_Un_S),
            (OpCodes.Ble_Un, OpCodes.Ble_Un_S),
            (OpCodes.Blt_Un, OpCodes.Blt_Un_S),
            (OpCodes.Leave, OpCodes.Leave_S),
        ];

        // Iterate until no more changes (shortening can cascade)
        bool changed;
        do
        {
            changed = false;
            ComputeOffsets(body);

            for (var i = 0; i < body.Instructions.Count; i++)
            {
                var instr = body.Instructions[i];
                if (instr.Operand is not Instruction target) continue;

                foreach (var (from, to) in branchMap)
                {
                    if (instr.OpCode != from) continue;

                    // Calculate offset: target offset minus (current offset + short instruction size)
                    // Short branch = 1 byte opcode + 1 byte operand = 2 bytes
                    var offset = target.Offset - (instr.Offset + 2);
                    if (offset is >= -128 and <= 127)
                    {
                        Replace(il, instr, il.Create(to, target));
                        changed = true;
                    }
                    break;
                }
            }
        } while (changed);
    }

    static void ComputeOffsets(MethodBody body)
    {
        var offset = 0;
        foreach (var instr in body.Instructions)
        {
            instr.Offset = offset;
            offset += instr.GetSize();
        }
    }

    /// <summary>Replace an instruction in-place, preserving branch targets that point to it.</summary>
    static void Replace(ILProcessor il, Instruction old, Instruction @new)
    {
        // Retarget any branches pointing at the old instruction
        foreach (var instr in il.Body.Instructions)
        {
            if (instr.Operand is Instruction target && target == old)
                instr.Operand = @new;
            else if (instr.Operand is Instruction[] targets)
            {
                for (var j = 0; j < targets.Length; j++)
                    if (targets[j] == old) targets[j] = @new;
            }
        }

        il.Replace(old, @new);
    }
}
