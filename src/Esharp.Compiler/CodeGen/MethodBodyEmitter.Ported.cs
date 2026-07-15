using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;

namespace Esharp.CodeGen;

// Helpers the round-2 CodeGen draft referenced but never carried across from the legacy
// ILEmit emitter. The Cecil-reference builders port verbatim; the ref-choice binding store
// is retargeted onto the ILBuilder verb API (the only behavioural change from the original).
public partial class MethodBodyEmitter
{
    // The Func<…>/Action<…> delegate type matching a lowered function-literal signature,
    // returned both as the open runtime Type and the closed Cecil reference.
    internal (Type OpenType, TypeReference ClosedRef) ResolveFuncActionDelegate(TypeReference retType, List<TypeReference> paramTypes)
    {
        var module = _types.Module;
        Type delegateType = retType.FullName == "System.Void"
            ? paramTypes.Count switch { 0 => typeof(Action), 1 => typeof(Action<>), 2 => typeof(Action<,>), 3 => typeof(Action<,,>), _ => typeof(Action) }
            : paramTypes.Count switch { 0 => typeof(Func<>), 1 => typeof(Func<,>), 2 => typeof(Func<,,>), 3 => typeof(Func<,,,>), _ => typeof(Func<>) };

        TypeReference delegateRef;
        if (delegateType.IsGenericTypeDefinition)
        {
            var closed = new GenericInstanceType(module.ImportReference(delegateType));
            foreach (var pt in paramTypes) closed.GenericArguments.Add(pt);
            if (retType.FullName != "System.Void") closed.GenericArguments.Add(retType);
            delegateRef = module.ImportReference(closed);
        }
        else
        {
            delegateRef = module.ImportReference(delegateType);
        }
        return (delegateType, delegateRef);
    }

    // A member of the open Result`2 re-hosted on a closed Result<T,E> instance so the
    // operand token carries the reified type arguments.
    MethodReference ResultMemberOnClosed(TypeReference closedResult, string methodName)
    {
        var openType = ILTypeResolver.ResultOpenType();
        var openMethod = openType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
                      ?? openType.GetProperty(methodName.StartsWith("get_") ? methodName[4..] : methodName)?.GetGetMethod();
        if (openMethod is null)
            throw new InvalidOperationException($"Result<,> has no member '{methodName}'");
        var openRef = _types.Module.ImportReference(openMethod);
        var closedRef = new MethodReference(openRef.Name, openRef.ReturnType, closedResult)
        {
            HasThis = openRef.HasThis,
            ExplicitThis = openRef.ExplicitThis,
            CallingConvention = openRef.CallingConvention,
        };
        foreach (var p in openRef.Parameters)
            closedRef.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        foreach (var gp in openRef.GenericParameters)
            closedRef.GenericParameters.Add(new GenericParameter(gp.Name, closedRef));
        return closedRef;
    }

    // The closed generic instance of a reified ref-union variant subclass (`Box_full`1<int>`),
    // or the open variant when it is non-generic.
    TypeReference ClosedRefVariant(TypeReference variantType, ChoiceType refCt)
    {
        if (variantType is TypeDefinition { HasGenericParameters: true } && refCt.TypeArgs.Count > 0)
        {
            var closed = new GenericInstanceType(variantType);
            foreach (var arg in refCt.TypeArgs) closed.GenericArguments.Add(_types.Resolve(arg));
            return closed;
        }
        return variantType;
    }

    // Bind a ref-union case's payload fields (or the case-view itself) by casting the subject
    // to the variant and reading each field. On a closed generic variant the field is hosted
    // on that instance so the load reads the reified payload type.
    void EmitRefChoiceBindings(IReadOnlyList<BoundMatchBinding> bindings, VariableDefinition subjectLocal, TypeReference variantType)
    {
        var closedHost = variantType as GenericInstanceType;
        foreach (var binding in bindings)
        {
            var isCaseView = string.IsNullOrEmpty(binding.PayloadFieldName);
            var slotType = isCaseView ? variantType : _types.Resolve(binding.Type);
            DeclareLocal(binding.Name, slotType);
            var slot = TryResolveSlot(binding.Name)!;
            FieldReference? payloadField = null;
            if (!isCaseView
                && variantType.Resolve().Fields.FirstOrDefault(f => f.Name == binding.PayloadFieldName) is { } fd)
            {
                payloadField = _types.Module.ImportReference(fd);
                if (closedHost is not null)
                    payloadField = RebindFieldToDeclaring(payloadField, closedHost);
            }
            slot.EmitStore(_il, () =>
            {
                _il.LoadLocal(subjectLocal);
                _il.CastClass(variantType);
                if (payloadField is not null)
                    _il.LoadField(payloadField);
            });
        }
    }
}

// Metadata-name helpers shared by the resolver and the emitter (formerly statics on the
// legacy ILEmitter facade).
internal static class EmitNaming
{
    // The CLR metadata name of a type by arity (`Foo` / `Foo`1`).
    internal static string MetadataTypeName(string name, int arity) => arity > 0 ? name + "`" + arity : name;

    // The compiler-generated backing-field name for an auto-property.
    internal static string PropertyBackingFieldName(string propertyName) => $"<{propertyName}>k__BackingField";
}
