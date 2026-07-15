using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Esharp.Emit;

/// A named binding in a method body. Hides whether the storage is a local,
/// parameter, display-class field, or async state machine field so the emitter
/// can operate on names through a single resolution surface.
///
/// Slots drive the typed <see cref="ILBuilder"/> verbs (never raw opcodes), so
/// their loads/stores participate in stack tracking like every other emission.
public abstract class ILSlot
{
    public abstract TypeReference Type { get; }
    public abstract void EmitLoad(ILBuilder il);
    public abstract void EmitStore(ILBuilder il, Action emitValue);
    public abstract void EmitAddress(ILBuilder il);
}

/// <summary>
/// A value slot backed by the compiler's durable <c>__Ptr_T</c> representation.
/// The backing carrier may be a normal method local or a field in an async state
/// machine; callers must not assume one or the other when preserving an explicit
/// address alias.
/// </summary>
public interface IWrapperBackedSlot
{
    TypeReference WrapperType { get; }
    void EmitWrapperLoad(ILBuilder il);
    void EmitWrapperStore(ILBuilder il, Action emitWrapper);
}

public sealed class LocalSlot : ILSlot
{
    public VariableDefinition Definition { get; }
    public LocalSlot(VariableDefinition def) => Definition = def;
    public override TypeReference Type => Definition.VariableType;
    public override void EmitLoad(ILBuilder il) => il.LoadLocal(Definition);
    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        emitValue();
        il.StoreLocal(Definition);
    }
    public override void EmitAddress(ILBuilder il) => il.LoadLocalAddress(Definition);
}

public sealed class ParameterSlot : ILSlot
{
    public ParameterDefinition Definition { get; }
    public ParameterSlot(ParameterDefinition def) => Definition = def;
    public override TypeReference Type => Definition.ParameterType;
    public override void EmitLoad(ILBuilder il) => il.LoadArg(Definition);
    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        emitValue();
        il.StoreArg(Definition);
    }
    public override void EmitAddress(ILBuilder il) => il.LoadArgAddress(Definition);
}

/// A parameter declared with by-ref semantics (E#'s `*T`). Loading produces
/// the value via Ldarg + Ldobj; storing pushes via Ldarg + value + Stobj;
/// address is the raw Ldarg (which is already a managed pointer).
public sealed class ByRefParameterSlot : ILSlot
{
    public ParameterDefinition Definition { get; }
    readonly TypeReference _elementType;
    public ByRefParameterSlot(ParameterDefinition def, TypeReference elementType)
    {
        Definition = def;
        _elementType = elementType;
    }
    public override TypeReference Type => _elementType;
    public override void EmitLoad(ILBuilder il)
    {
        il.LoadArg(Definition);
        il.LoadObject(_elementType);
    }
    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        il.LoadArg(Definition);
        emitValue();
        il.StoreObject(_elementType);
    }
    public override void EmitAddress(ILBuilder il) => il.LoadArg(Definition);
}

/// A parameter declared with read-only by-ref semantics (E#'s `readonly *T`).
/// Same as ByRefParameterSlot but EmitStore is unreachable — the binder prevents
/// assignment. The IL emitter adds [In] on the parameter for JIT optimization.
public sealed class ReadOnlyByRefParameterSlot : ILSlot
{
    public ParameterDefinition Definition { get; }
    readonly TypeReference _elementType;
    public ReadOnlyByRefParameterSlot(ParameterDefinition def, TypeReference elementType)
    {
        Definition = def;
        _elementType = elementType;
    }
    public override TypeReference Type => _elementType;
    public override void EmitLoad(ILBuilder il)
    {
        il.LoadArg(Definition);
        il.LoadObject(_elementType);
    }
    public override void EmitStore(ILBuilder il, Action emitValue) =>
        throw new InvalidOperationException("Cannot store through readonly *T parameter");
    public override void EmitAddress(ILBuilder il) => il.LoadArg(Definition);
}

/// <summary>
/// A source <c>*T</c> parameter whose escape contract selected the durable
/// <c>__Ptr_T</c> ABI. The CLR parameter is the wrapper reference, while source
/// reads, writes, and address formation retain the same pointee behavior as a
/// managed-ref parameter. The wrapper itself remains available to pointer-call
/// coercion through <see cref="IWrapperBackedSlot"/>.
/// </summary>
public sealed class WrapperBackedParameterSlot : ILSlot, IWrapperBackedSlot
{
    public ParameterDefinition Definition { get; }
    public FieldReference ValueField { get; }
    readonly TypeReference _elementType;

    public WrapperBackedParameterSlot(ParameterDefinition definition, FieldReference valueField,
        TypeReference elementType)
    {
        Definition = definition;
        ValueField = valueField;
        _elementType = elementType;
    }

    public override TypeReference Type => _elementType;
    public TypeReference WrapperType => Definition.ParameterType;

    public void EmitWrapperLoad(ILBuilder il) => il.LoadArg(Definition);

    public void EmitWrapperStore(ILBuilder il, Action emitWrapper)
    {
        emitWrapper();
        il.StoreArg(Definition);
    }

    public override void EmitLoad(ILBuilder il)
    {
        il.LoadArg(Definition);
        il.LoadField(ValueField);
    }

    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        il.LoadArg(Definition);
        emitValue();
        il.StoreField(ValueField);
    }

    public override void EmitAddress(ILBuilder il)
    {
        il.LoadArg(Definition);
        il.LoadFieldAddress(ValueField);
    }
}

/// A local variable holding a managed pointer (E#'s `var p: *T = &x`).
/// The VariableDefinition is typed as ByReferenceType(T). Loading dereferences
/// via Ldloc + Ldobj; storing pushes through Ldloc + value + Stobj; address is raw Ldloc.
public sealed class ByRefLocalSlot : ILSlot
{
    public VariableDefinition Definition { get; }
    readonly TypeReference _elementType;
    public ByRefLocalSlot(VariableDefinition def, TypeReference elementType)
    {
        Definition = def;
        _elementType = elementType;
    }
    public override TypeReference Type => _elementType;
    public override void EmitLoad(ILBuilder il)
    {
        il.LoadLocal(Definition);
        il.LoadObject(_elementType);
    }
    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        il.LoadLocal(Definition);
        emitValue();
        il.StoreObject(_elementType);
    }
    public override void EmitAddress(ILBuilder il) => il.LoadLocal(Definition);
}

/// A local whose address escapes (`&local` stored into a field or returned), so
/// its storage is heap-promoted into a `__Ptr_T` wrapper. The local *is* the
/// wrapper: reading/writing the name goes through `Value`, member access takes
/// `Value`'s address, and `&local` (handled in EmitAddressOfVariable) yields the
/// wrapper reference itself — so an escaped pointer and the live local share one
/// heap cell (Go pointer aliasing). Mirrors the closure display-class hoist.
public sealed class WrapperBackedLocalSlot : ILSlot, IWrapperBackedSlot
{
    public VariableDefinition Wrapper { get; }
    public FieldReference ValueField { get; }
    readonly TypeReference _elementType;

    public WrapperBackedLocalSlot(VariableDefinition wrapper, FieldReference valueField, TypeReference elementType)
    {
        Wrapper = wrapper;
        ValueField = valueField;
        _elementType = elementType;
    }

    public override TypeReference Type => _elementType;
    public TypeReference WrapperType => Wrapper.VariableType;

    public void EmitWrapperLoad(ILBuilder il) => il.LoadLocal(Wrapper);

    public void EmitWrapperStore(ILBuilder il, Action emitWrapper)
    {
        emitWrapper();
        il.StoreLocal(Wrapper);
    }

    public override void EmitLoad(ILBuilder il)
    {
        il.LoadLocal(Wrapper);
        il.LoadField(ValueField);
    }

    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        il.LoadLocal(Wrapper);
        emitValue();
        il.StoreField(ValueField);
    }

    public override void EmitAddress(ILBuilder il)
    {
        il.LoadLocal(Wrapper);
        il.LoadFieldAddress(ValueField);
    }
}

/// A field on an enclosing display class (closure capture). The owning emitter
/// tells us how to load the display instance — either as a local in the outer
/// method or as `this` when we're an instance method on the display class itself.
public sealed class HoistedFieldSlot : ILSlot
{
    readonly FieldDefinition _field;
    readonly Action<ILBuilder> _loadDisplay;

    public HoistedFieldSlot(FieldDefinition field, Action<ILBuilder> loadDisplay)
    {
        _field = field;
        _loadDisplay = loadDisplay;
    }

    public FieldDefinition Field => _field;
    public override TypeReference Type => _field.FieldType;

    public override void EmitLoad(ILBuilder il)
    {
        _loadDisplay(il);
        il.LoadField(_field);
    }

    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        _loadDisplay(il);
        emitValue();
        il.StoreField(_field);
    }

    public override void EmitAddress(ILBuilder il)
    {
        _loadDisplay(il);
        il.LoadFieldAddress(_field);
    }
}

/// A field on whatever `this` is for the current method — used both for
/// init-body struct fields (where the declaring struct is the receiver) and
/// for async state machine fields inside MoveNext (where the SM struct is).
/// The store path uses a temp-reorder so it survives an await suspension
/// inside `emitValue()`: the value is computed first, stashed to a scratch
/// local, and only then is `this` reloaded for the actual stfld.
public sealed class SelfFieldSlot : ILSlot
{
    readonly FieldDefinition _field;
    // The field reference used in IL. When the declaring type is a generic state
    // machine, `host` is the closed self-instance (`<…>d__N<!0>`) and the field is
    // re-homed onto it so substitution against `this` lines up; otherwise it's the
    // bare FieldDefinition (the common, non-generic case).
    readonly FieldReference _ref;
    public SelfFieldSlot(FieldDefinition field, GenericInstanceType? host = null)
    {
        _field = field;
        _ref = host is null ? field : new FieldReference(field.Name, field.FieldType, host);
    }
    public FieldDefinition Field => _field;
    public FieldReference Reference => _ref;
    public override TypeReference Type => _field.FieldType;

    public override void EmitLoad(ILBuilder il)
    {
        il.LoadArg(il.Method.Body.ThisParameter);
        il.LoadField(_ref);
    }

    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        emitValue();
        var temp = new VariableDefinition(_field.FieldType);
        il.Method.Body.Variables.Add(temp);
        il.StoreLocal(temp);
        il.LoadArg(il.Method.Body.ThisParameter);
        il.LoadLocal(temp);
        il.StoreField(_ref);
    }

    public override void EmitAddress(ILBuilder il)
    {
        il.LoadArg(il.Method.Body.ThisParameter);
        il.LoadFieldAddress(_ref);
    }
}

/// <summary>
/// The async-state-machine variant of <see cref="WrapperBackedLocalSlot"/>.
/// It keeps the durable wrapper in a state field rather than a method local, so
/// a source local that is both address-escaped and live across an <c>await</c>
/// retains one identity across every suspension and resume.
/// </summary>
public sealed class WrapperBackedSelfFieldSlot : ILSlot, IWrapperBackedSlot
{
    readonly FieldReference _wrapperField;
    readonly FieldReference _valueField;
    readonly TypeReference _elementType;

    public WrapperBackedSelfFieldSlot(
        FieldReference wrapperField,
        FieldReference valueField,
        TypeReference elementType)
    {
        _wrapperField = wrapperField;
        _valueField = valueField;
        _elementType = elementType;
    }

    public override TypeReference Type => _elementType;
    public TypeReference WrapperType => _wrapperField.FieldType;

    public void EmitWrapperLoad(ILBuilder il)
    {
        il.LoadArg(il.Method.Body.ThisParameter);
        il.LoadField(_wrapperField);
    }

    public void EmitWrapperStore(ILBuilder il, Action emitWrapper)
    {
        emitWrapper();
        var temp = new VariableDefinition(_wrapperField.FieldType);
        il.Method.Body.Variables.Add(temp);
        il.StoreLocal(temp);
        il.LoadArg(il.Method.Body.ThisParameter);
        il.LoadLocal(temp);
        il.StoreField(_wrapperField);
    }

    public override void EmitLoad(ILBuilder il)
    {
        EmitWrapperLoad(il);
        il.LoadField(_valueField);
    }

    public override void EmitStore(ILBuilder il, Action emitValue)
    {
        EmitWrapperLoad(il);
        emitValue();
        il.StoreField(_valueField);
    }

    public override void EmitAddress(ILBuilder il)
    {
        EmitWrapperLoad(il);
        il.LoadFieldAddress(_valueField);
    }
}
