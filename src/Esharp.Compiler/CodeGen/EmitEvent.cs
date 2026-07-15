using Mono.Cecil;
using Mono.Cecil.Cil;
using ILOpCode = System.Reflection.Metadata.ILOpCode;
using Esharp.Emit;
using Esharp.BoundTree;

namespace Esharp.CodeGen;

public static partial class CodeGenerator
{
    // A field-style event on a `class` emits exactly what C# emits for
    // `public event Action OnReady;`:
    //   * a private backing field of the delegate type, same name as the event,
    //   * public `add_<E>` / `remove_<E>` accessors using the lock-free
    //     Delegate.Combine/Remove + Interlocked.CompareExchange loop (thread-safe),
    //   * an `EventDefinition` wiring the two accessors.
    // The accessor visibility tracks the event's; the backing field is always private
    // (subscription crosses the boundary through the accessors). Returns the backing
    // field so `raise` can read it directly.
    static FieldDefinition EmitEventMember(ModuleDefinition module, ILTypeResolver types, TypeDefinition typeDef, BoundField ev)
    {
        var delegateType = types.Resolve(ev.Type);

        var backing = new FieldDefinition(ev.Name, FieldAttributes.Private, delegateType);
        typeDef.Fields.Add(backing);

        var accessorVis = ev.IsPublic ? MethodAttributes.Public : MethodAttributes.Assembly;
        var accessorAttrs = accessorVis | MethodAttributes.HideBySig | MethodAttributes.SpecialName
            | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot;

        var addMethod = EmitEventAccessor(module, types, typeDef, backing, delegateType, isAdd: true, accessorAttrs);
        var removeMethod = EmitEventAccessor(module, types, typeDef, backing, delegateType, isAdd: false, accessorAttrs);

        var eventDef = new EventDefinition(ev.Name, EventAttributes.None, delegateType)
        {
            AddMethod = addMethod,
            RemoveMethod = removeMethod,
        };
        typeDef.Events.Add(eventDef);
        return backing;
    }

    // The lock-free combine/remove loop C# emits. For `add`:
    //   var t0 = field;
    //   do { var t1 = t0; var t2 = (T)Delegate.Combine(t1, value);
    //        t0 = Interlocked.CompareExchange<T>(ref field, t2, t1); } while (t0 != t1);
    // `remove` is identical with Delegate.Remove.
    static MethodDefinition EmitEventAccessor(
        ModuleDefinition module, ILTypeResolver types, TypeDefinition typeDef,
        FieldDefinition backing, TypeReference delegateType, bool isAdd, MethodAttributes attrs)
    {
        var name = (isAdd ? "add_" : "remove_") + backing.Name;
        var method = new MethodDefinition(name, attrs, module.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, delegateType));
        method.Body.InitLocals = true;

        var v0 = new VariableDefinition(delegateType); // current
        var v1 = new VariableDefinition(delegateType); // snapshot
        var v2 = new VariableDefinition(delegateType); // combined/removed
        method.Body.Variables.Add(v0);
        method.Body.Variables.Add(v1);
        method.Body.Variables.Add(v2);

        var delegateRef = module.ImportReference(typeof(System.Delegate));
        var combineOrRemove = module.ImportReference(
            typeof(System.Delegate).GetMethod(isAdd ? "Combine" : "Remove",
                new[] { typeof(System.Delegate), typeof(System.Delegate) })!);

        // Interlocked.CompareExchange<T>(ref T, T, T) — closed over the delegate type.
        var ceOpen = typeof(System.Threading.Interlocked).GetMethods()
            .First(m => m.Name == "CompareExchange" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
        var ceClosed = new GenericInstanceMethod(module.ImportReference(ceOpen));
        ceClosed.GenericArguments.Add(delegateType);

        var il = new ILBuilder(method);
        var loop = il.DefineLabel("ceLoop");

        il.LoadArgByIndex(0);
        il.LoadField(backing);
        il.StoreLocal(v0);

        il.MarkLabel(loop); // loop: ldloc v0
        il.LoadLocal(v0);
        il.StoreLocal(v1);
        il.LoadLocal(v1);
        il.LoadArgByIndex(1);
        il.Call(combineOrRemove);
        il.CastClass(delegateType);
        il.StoreLocal(v2);
        il.LoadArgByIndex(0);
        il.LoadFieldAddress(backing);
        il.LoadLocal(v2);
        il.LoadLocal(v1);
        il.Call(ceClosed);
        il.StoreLocal(v0);
        il.LoadLocal(v0);
        il.LoadLocal(v1);
        il.BranchRelational(ILOpCode.Bne_un, loop);
        il.Return();

        typeDef.Methods.Add(method);
        return method;
    }

    // An interface event: abstract `add_<E>` / `remove_<E>` (no body, no backing field)
    // plus the `EventDefinition`. Implementors supply the accessors.
    static void EmitInterfaceEventMember(ModuleDefinition module, ILTypeResolver types, TypeDefinition ifaceDef, BoundField ev)
    {
        var delegateType = types.Resolve(ev.Type);
        var attrs = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
            | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot;

        MethodDefinition Accessor(string prefix)
        {
            var m = new MethodDefinition(prefix + ev.Name, attrs, module.TypeSystem.Void);
            m.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, delegateType));
            ifaceDef.Methods.Add(m);
            return m;
        }

        var add = Accessor("add_");
        var remove = Accessor("remove_");
        ifaceDef.Events.Add(new EventDefinition(ev.Name, EventAttributes.None, delegateType)
        {
            AddMethod = add,
            RemoveMethod = remove,
        });
    }
}
