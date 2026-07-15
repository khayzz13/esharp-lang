using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.BoundTree;
using BoundFunctionPointerType = Esharp.BoundTree.FunctionPointerType;

namespace Esharp.Emit;

/// Handles function pointer and managed-pointer emission.
/// Extracted from ILMethodEmitter to keep pointer-specific IL logic isolated.
public static class ILPointerEmitter
{
    /// Build a Cecil FunctionPointerType from the bound type's param/return info.
    public static Mono.Cecil.FunctionPointerType BuildCecilFunctionPointerType(
        BoundFunctionPointerType fp, IMetadataResolver types)
    {
        var cecilFp = new Mono.Cecil.FunctionPointerType
        {
            ReturnType = types.Resolve(fp.ReturnType),
            HasThis = false,
            CallingConvention = MethodCallingConvention.Default,
        };
        foreach (var p in fp.ParameterTypes)
            cecilFp.Parameters.Add(new ParameterDefinition(types.Resolve(p)));
        return cecilFp;
    }

    /// Try to emit a call through a function pointer local. Returns true if the
    /// slot held a function pointer and the calli was emitted.
    /// `emitCallArg` receives the exact calli parameter type so pointer arguments
    /// can use the same wrapper-vs-managed-ref reconciliation as ordinary calls.
    public static bool TryEmitFunctionPointerCall(
        ILSlot slot, IReadOnlyList<BoundExpression> args,
        ILBuilder il, ModuleDefinition module,
        Action<BoundExpression, TypeReference> emitCallArg, out bool isVoid)
    {
        isVoid = false;
        if (slot.Type is not Mono.Cecil.FunctionPointerType fp)
            return false;

        // Push arguments first, then the function pointer, then calli.
        for (var i = 0; i < args.Count; i++)
            emitCallArg(args[i], fp.Parameters[i].ParameterType);
        slot.EmitLoad(il);

        var callSite = new CallSite(fp.ReturnType)
        {
            HasThis = false,
            CallingConvention = MethodCallingConvention.Default,
        };
        foreach (var p in fp.Parameters)
            callSite.Parameters.Add(new ParameterDefinition(p.ParameterType));

        il.CallIndirect(callSite, args.Count);
        isVoid = fp.ReturnType.FullName == "System.Void";
        return true;
    }
}
