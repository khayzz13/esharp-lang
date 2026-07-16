using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.BoundTree;
using ILOpCode = System.Reflection.Metadata.ILOpCode;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{
    // =========================================================================
    // Conversion emit — the single dispatch for BoundConversion.
    //
    // BoundConversion is the only CORE cast/narrow node. ConversionKind encodes
    // which IL instruction(s) are emitted. BoundTypeTestExpression (the boolean
    // `is` test) is separate and handled by EmitTypeTest below.
    //
    // See BoundConversion.cs and spine-deltas.md §3 for the rationale behind
    // the four-node → one-node collapse.
    // =========================================================================

    void EmitConversion(BoundConversion conv)
    {
        switch (conv.Kind)
        {
            case ConversionKind.Identity:
                // No IL — emit the operand as-is. The CLR slot already holds the
                // correct representation; no instruction changes it.
                EmitExpression(conv.Operand);
                break;

            case ConversionKind.Box:
                // value type → object / interface: `box T`
                EmitExpression(conv.Operand);
                _il.Box(_types.Resolve(conv.Operand.Type));
                break;

            case ConversionKind.Unbox:
                // object / interface → value type: `unbox.any T`
                EmitExpression(conv.Operand);
                // A direct value→value asserting cast is already on the stack as a
                // value. Box it first so `unbox.any` still receives the required
                // object reference; object/interface sources take this branch free.
                if (_types.IsValueType(conv.Operand.Type))
                    _il.Box(_types.Resolve(conv.Operand.Type));
                _il.UnboxAny(_types.Resolve(conv.TargetType));
                break;

            case ConversionKind.CastClass:
                // assert-cast (throws on miss): [box operand]; castclass T
                EmitExpression(conv.Operand);
                if (_types.IsValueType(conv.Operand.Type))
                    _il.Box(_types.Resolve(conv.Operand.Type));
                _il.CastClass(_types.Resolve(conv.TargetType));
                break;

            case ConversionKind.IsInst:
                // safe cast (null on miss): [box operand]; isinst T
                // When the target is a value type, the `isinst` yields a boxed T or null;
                // lift the result to Nullable<T> so callers see T? as the result type.
                EmitIsInstConversion(conv);
                break;

            case ConversionKind.Narrow:
                // Flow-proven narrowing. The binder has established that operand IS T
                // at this program point; no null check is generated at the call site.
                //
                // Null axis (Nullable<T> → T for a value type, from an `x != nil` guard):
                // unwrap via Nullable<T>.GetValueOrDefault rather than isinst.
                //
                // Type axis (reference type → subtype or value type, flow-proven by `is`
                // guard or match-arm discriminant): emit the confirmed-narrow shape.
                EmitNarrowConversion(conv);
                break;

            case ConversionKind.NullableWrap:
                // Implicit T → Nullable<T> present-case lift.
                //
                // ESC oracle (ILMethodEmitter.Primitives.cs, EmitNullConditionalAccess):
                //   <push inner value>
                //   newobj Nullable`1<T>::.ctor(!0)
                //
                // The operand is a value type T; TargetType is NullableType(T), which
                // resolves to the closed `Nullable<T>` GenericInstanceType via ILTypeResolver.
                // NullableCtor builds the correct MethodReference to `.ctor(T value)` on
                // the closed instance, matching what the three inline store-paths do.
                EmitExpression(conv.Operand);
                _il.NewObj(NullableCtor(_types.Resolve(conv.TargetType)));
                break;

            case ConversionKind.NumericChecked:
                EmitNumericCheckedConversion(conv);
                break;

            case ConversionKind.ImplicitSpan:
                // BCL implicit span conversion: emit the operand (array / Span<T>),
                // then `call` the framework op_Implicit closed on the target span.
                EmitExpression(conv.Operand);
                EmitSpanImplicit(conv.Operand.Type, conv.TargetType);
                break;

            case ConversionKind.IntegerToEnum:
                // integer → enum: push the integer and narrow to the enum's underlying
                // width (unchecked, like a C# `(Enum)i`). An enum has no runtime shape
                // distinct from its underlying integral, so the value then IS the enum.
                EmitExpression(conv.Operand);
                EmitIntegerNarrow(((EnumType)conv.TargetType).UnderlyingPrimitiveName);
                break;

            default:
                // Unknown kind — report and emit a best-effort `castclass`.
                _diagnostics.Report("", 0, 0, $"IL: unknown ConversionKind {conv.Kind} on BoundConversion; emitting castclass as fallback");
                EmitExpression(conv.Operand);
                _il.CastClass(_types.Resolve(conv.TargetType));
                break;
        }
    }

    // IsInst case extracted for clarity: handles both reference-target (plain isinst)
    // and value-type-target (isinst → Nullable<T> lift) shapes.
    void EmitIsInstConversion(BoundConversion conv)
    {
        // The bound result of `x as int` is Nullable<int>, while the CLR test
        // must target the boxed *inner* int. Testing/then unboxing Nullable<int>
        // is invalid (boxed Nullable<T> has no such representation) and wrapping
        // it again feeds a Nullable<int> to the Nullable<int>(int) constructor.
        var valueTarget = conv.TargetType is Esharp.BoundTree.NullableType nullable
            ? nullable.Inner
            : conv.TargetType;

        if (!_types.IsValueType(valueTarget))
        {
            // Reference target → `[box operand]; isinst T` (yields T or null).
            EmitExpression(conv.Operand);
            if (_types.IsValueType(conv.Operand.Type))
                _il.Box(_types.Resolve(conv.Operand.Type));
            _il.IsInst(_types.Resolve(valueTarget));
            return;
        }

        // Value-type target: isinst yields boxed T or null; lift to Nullable<T>.
        // conv.TargetType is the non-nullable T; the result type is Nullable<T>
        // (the binder set it via BoundConversion.SafeCast(operand, NullableType(T))).
        var nullableRef = _types.Resolve(conv.Type);   // Nullable<T> from result type
        var valueRef    = _types.Resolve(valueTarget); // inner T

        EmitExpression(conv.Operand);
        if (_types.IsValueType(conv.Operand.Type))
            _il.Box(_types.Resolve(conv.Operand.Type));
        _il.IsInst(valueRef);  // isinst T → boxed T or null

        var hasValue = _il.DefineLabel();
        var end      = _il.DefineLabel();
        var tmp      = new VariableDefinition(nullableRef);
        _method.Body.Variables.Add(tmp);

        _il.Dup();
        _il.BranchIfTrue(hasValue);
        // null branch → default Nullable<T> (no value).
        _il.Pop();
        _il.LoadLocalAddress(tmp);
        _il.InitObj(nullableRef);
        _il.LoadLocal(tmp);
        _il.Branch(end);
        // non-null branch → unbox the value and wrap in Nullable<T>.
        _il.MarkLabel(hasValue);
        _il.UnboxAny(valueRef);
        _il.NewObj(NullableCtor(nullableRef));
        _il.MarkLabel(end);
    }

    // Narrow case: flow-proven narrowing. Two sub-shapes:
    //   1. Null-axis unwrap: Nullable<T> → T (from a `x != nil` guard on a value type).
    //   2. Type-axis narrow: reference type → subtype or value type (flow-proven `is T` guard).
    void EmitNarrowConversion(BoundConversion conv)
    {
        // Null-axis: operand is Nullable<T> and target is the non-nullable inner T.
        if (conv.Operand.Type is NullableType nt && _types.IsValueType(nt.Inner))
        {
            var nullableRef = _types.Resolve(conv.Operand.Type);
            var tmp = new VariableDefinition(nullableRef);
            _method.Body.Variables.Add(tmp);
            EmitExpression(conv.Operand);
            _il.StoreLocal(tmp);
            _il.LoadLocalAddress(tmp);
            _il.Call(NullableMember(nullableRef, "GetValueOrDefault"));
            return;
        }

        // Type-axis: confirmed downcast (the guard already proved the type).
        // Box a value-type operand first so isinst/castclass has a reference.
        EmitExpression(conv.Operand);
        if (_types.IsValueType(conv.Operand.Type))
            _il.Box(_types.Resolve(conv.Operand.Type));
        var targetRef = _types.Resolve(conv.TargetType);
        // Value-type target: unbox.any (the guard proved it's boxed T).
        // Reference target: castclass (the guard proved it's T, but verifier still
        // requires a checkable instruction; we use the cheaper isinst+unbox.any for
        // value targets, and castclass for reference targets — identical to EmitHardNarrow).
        if (_types.IsValueType(conv.TargetType))
            _il.UnboxAny(targetRef);
        else
            _il.CastClass(targetRef);
    }

    void EmitNumericCheckedConversion(BoundConversion conv)
    {
        if (conv.Operand.Type == conv.TargetType)
        {
            EmitExpression(conv.Operand);
            return;
        }

        if (conv.Operand.Type is PrimitiveType { Name: "decimal" }
            || conv.TargetType is PrimitiveType { Name: "decimal" })
        {
            EmitDecimalConversion(conv);
            return;
        }

        EmitExpression(conv.Operand);
        // An enum operand's stack value is its underlying integral, so classify its
        // signedness by that underlying type (`enum C: byte` → unsigned source).
        var sourceName = conv.Operand.Type switch
        {
            PrimitiveType p => p.Name,
            EnumType e => e.UnderlyingPrimitiveName,
            _ => null,
        };
        var sourceUnsigned = sourceName is "byte" or "ushort" or "uint" or "ulong" or "nuint" or "char";
        var sourceFloat = sourceName is "float" or "double";
        var target = ((PrimitiveType)conv.TargetType).Name;
        if (target is "float" or "double")
        {
            if (sourceUnsigned) _il.Convert(ILOpCode.Conv_r_un);
            if (target == "float") _il.Convert(ILOpCode.Conv_r4);
            else if (!sourceUnsigned) _il.Convert(ILOpCode.Conv_r8);
            return;
        }
        var op = (target, sourceUnsigned && !sourceFloat) switch
        {
            ("sbyte", false) => ILOpCode.Conv_ovf_i1,
            ("sbyte", true) => ILOpCode.Conv_ovf_i1_un,
            ("byte", false) => ILOpCode.Conv_ovf_u1,
            ("byte", true) => ILOpCode.Conv_ovf_u1_un,
            ("short", false) => ILOpCode.Conv_ovf_i2,
            ("short", true) => ILOpCode.Conv_ovf_i2_un,
            ("ushort" or "char", false) => ILOpCode.Conv_ovf_u2,
            ("ushort" or "char", true) => ILOpCode.Conv_ovf_u2_un,
            ("int", false) => ILOpCode.Conv_ovf_i4,
            ("int", true) => ILOpCode.Conv_ovf_i4_un,
            ("uint", false) => ILOpCode.Conv_ovf_u4,
            ("uint", true) => ILOpCode.Conv_ovf_u4_un,
            ("long", false) => ILOpCode.Conv_ovf_i8,
            ("long", true) => ILOpCode.Conv_ovf_i8_un,
            ("ulong", false) => ILOpCode.Conv_ovf_u8,
            ("ulong", true) => ILOpCode.Conv_ovf_u8_un,
            ("nint", false) => ILOpCode.Conv_ovf_i,
            ("nint", true) => ILOpCode.Conv_ovf_i_un,
            ("nuint", false) => ILOpCode.Conv_ovf_u,
            ("nuint", true) => ILOpCode.Conv_ovf_u_un,
            _ => ILOpCode.Conv_i4,
        };
        _il.Convert(op);
    }

    /// Unchecked narrowing `conv` to an integral primitive's width — the truncating
    /// form a C# enum cast uses. `int` needs none (values sit as int32 on the stack).
    void EmitIntegerNarrow(string primitiveName)
    {
        ILOpCode? op = primitiveName switch
        {
            "byte" => ILOpCode.Conv_u1,
            "sbyte" => ILOpCode.Conv_i1,
            "short" => ILOpCode.Conv_i2,
            "ushort" or "char" => ILOpCode.Conv_u2,
            "uint" => ILOpCode.Conv_u4,
            "long" => ILOpCode.Conv_i8,
            "ulong" => ILOpCode.Conv_u8,
            "nint" => ILOpCode.Conv_i,
            "nuint" => ILOpCode.Conv_u,
            _ => null,   // int (or unknown): already int32 on the stack
        };
        if (op is { } o) _il.Convert(o);
    }

    void EmitDecimalConversion(BoundConversion conv)
    {
        var source = _types.BoundTypeToRuntime(conv.Operand.Type)
            ?? throw new InvalidOperationException("numeric conversion source has no CLR type");
        var target = _types.BoundTypeToRuntime(conv.TargetType)
            ?? throw new InvalidOperationException("numeric conversion target has no CLR type");
        var method = typeof(decimal).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(m => (m.Name is "op_Implicit" or "op_Explicit")
                && m.ReturnType == target && m.GetParameters() is var ps && ps.Length == 1 && ps[0].ParameterType == source);
        if (method is null)
            throw new InvalidOperationException($"no decimal conversion from {source} to {target}");
        EmitExpression(conv.Operand);
        _il.Call(_types.Module.ImportReference(method));
    }

    // =========================================================================
    // BoundTypeTestExpression — kept separate (yields bool, not a typed value).
    // operand is [not] T  →  [box operand]; isinst T; ldnull; (cgt.un | ceq)
    // =========================================================================

    void EmitTypeTest(BoundTypeTestExpression tt)
    {
        EmitExpression(tt.Operand);
        if (_types.IsValueType(tt.Operand.Type))
            _il.Box(_types.Resolve(tt.Operand.Type));
        _il.IsInst(_types.Resolve(tt.TargetType));
        _il.LoadNull();
        // `cgt.un` against null → non-null → `is T`.
        // `ceq` against null → null → `is not T`.
        if (tt.Negated)
            _il.Ceq();
        else
            _il.CgtUn();
    }
}
