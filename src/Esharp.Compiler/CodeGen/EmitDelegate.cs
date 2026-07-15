using Mono.Cecil;
using Esharp.BoundTree;

namespace Esharp.CodeGen;

public static partial class CodeGenerator
{
    // A nominal delegate type is a sealed class deriving from System.MulticastDelegate
    // with two runtime-provided (bodyless) methods: a `.ctor(object, native int)` and a
    // virtual `Invoke(params) -> R`. The runtime fills both bodies — `MethodImplAttributes.Runtime`
    // is the signal. This is the exact shape the C# compiler emits for `delegate R D(...)`,
    // so the type bridges assemblies and binds to C# call sites with no shim.
    //
    // Emission is split in two so type-reference cycles between delegates and data types
    // resolve regardless of source order:
    //   * EmitDelegateShell registers the (member-less) TypeDefinition early, so a data
    //     field typed by the delegate resolves.
    //   * EmitDelegateMembers fills .ctor + Invoke after all data/enum/choice types are
    //     registered, so an Invoke signature that mentions a declared type resolves too.

    static TypeDefinition EmitDelegateShell(ModuleDefinition module, ILTypeResolver types, BoundDelegateDeclaration del, string ns)
    {
        var delType = new TypeDefinition(
            ns,
            del.Name,
            (del.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Sealed,
            module.ImportReference(typeof(System.MulticastDelegate)));

        module.Types.Add(delType);
        types.Register(del.Name, delType, ns);
        return delType;
    }

    static void EmitDelegateMembers(ModuleDefinition module, ILTypeResolver types, BoundDelegateDeclaration del, TypeDefinition delType)
    {
        // .ctor(object target, native int method) — runtime-provided, rtspecialname.
        var ctor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void)
        {
            ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
        };
        ctor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, module.TypeSystem.Object));
        ctor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, module.TypeSystem.IntPtr));
        delType.Methods.Add(ctor);

        // Invoke(params) -> R — runtime-provided, virtual newslot. This signature IS the
        // delegate's identity; by-ref / out intent on parameters is carried through faithfully.
        var invoke = new MethodDefinition(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            types.Resolve(del.ReturnType))
        {
            ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
        };
        foreach (var param in del.Parameters)
        {
            var pt = types.Resolve(param.Type);
            if (param.ByRef || param.ReadOnlyByRef || param.IsOut) pt = new Mono.Cecil.ByReferenceType(pt);
            var pd = new ParameterDefinition(param.Name, ParameterAttributes.None, pt);
            if (param.ReadOnlyByRef) pd.Attributes |= ParameterAttributes.In;
            if (param.IsOut) pd.Attributes |= ParameterAttributes.Out;
            invoke.Parameters.Add(pd);
        }
        delType.Methods.Add(invoke);
    }
}
