using Mono.Cecil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;

namespace Esharp.CodeGen;

public static partial class CodeGenerator
{

    static void EmitProtocolInterface(ModuleDefinition module, ILTypeResolver types, BoundInterfaceDeclaration proto, string ns)
    {
        var ifaceDef = new TypeDefinition(
            ns,
            MetadataTypeName(proto.Name, proto.TypeParameters.Count),
            (proto.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Interface | TypeAttributes.Abstract,
            null);

        // Generic interface: `interface IBox<T>` emits as `IBox\`1` with a generic
        // parameter so method signatures (and downstream conformance) can reference T.
        var pushedGenericContext = false;
        if (proto.TypeParameters.Count > 0)
        {
            foreach (var tp in proto.TypeParameters)
                ifaceDef.GenericParameters.Add(new GenericParameter(tp, ifaceDef));
            types.PushGenericContext(ifaceDef.GenericParameters);
            pushedGenericContext = true;
        }

        foreach (var m in proto.Methods)
        {
            var retType = types.Resolve(m.ReturnType);
            var methodDef = new MethodDefinition(
                m.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig |
                MethodAttributes.NewSlot | MethodAttributes.Abstract,
                retType);

            // Skip the first parameter if it's 'self' — becomes 'this' in interface methods
            var paramsToEmit = m.Parameters.Count > 0 && m.Parameters[0].Name == "self"
                ? m.Parameters.Skip(1)
                : m.Parameters;
            foreach (var p in paramsToEmit)
                methodDef.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, types.Resolve(p.Type)));

            ifaceDef.Methods.Add(methodDef);
        }

        if (proto.Events is { Count: > 0 })
            foreach (var ev in proto.Events)
                EmitInterfaceEventMember(module, types, ifaceDef, ev);

        if (proto.Properties is { Count: > 0 })
            foreach (var p in proto.Properties)
                EmitInterfacePropertyMember(module, types, ifaceDef, p);

        if (pushedGenericContext) types.PopGenericContext();

        module.Types.Add(ifaceDef);
        types.Register(proto.Name, ifaceDef);
    }

    static void EmitEnum(ModuleDefinition module, ILTypeResolver types, BoundEnumDeclaration e, string ns)
    {
        var enumType = new TypeDefinition(
            ns,
            e.Name,
            (e.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Sealed,
            module.ImportReference(typeof(Enum)));

        var underlyingClr = EnumUnderlyingClrType(e.UnderlyingType);

        // Enums need a special "value__" field, typed to the enum's underlying type.
        enumType.Fields.Add(new FieldDefinition(
            "value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            module.ImportReference(underlyingClr)));

        for (var i = 0; i < e.Cases.Count; i++)
        {
            var caseField = new FieldDefinition(
                e.Cases[i].Name,
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal,
                enumType)
            {
                // The literal constant must be boxed as the underlying integral type, so
                // the metadata matches `value__` (a byte-backed enum stores a byte).
                Constant = System.Convert.ChangeType(e.Cases[i].Value, underlyingClr)
            };
            enumType.Fields.Add(caseField);
        }

        module.Types.Add(enumType);
        types.Register(e.Name, enumType);
    }

    static Type EnumUnderlyingClrType(string primitive) => primitive switch
    {
        "byte" => typeof(byte),
        "sbyte" => typeof(sbyte),
        "short" => typeof(short),
        "ushort" => typeof(ushort),
        "int" => typeof(int),
        "uint" => typeof(uint),
        "long" => typeof(long),
        "ulong" => typeof(ulong),
        _ => typeof(int),
    };
}
