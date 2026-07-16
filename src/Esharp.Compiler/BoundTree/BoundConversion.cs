// ============================================================================
// BoundConversion — unified cast / box / narrow CORE node.
//
// Replaces the four nodes that existed pre-rewrite:
//   BoundSafeCastExpression      → ConversionKind.IsInst   (Type = T?)
//   BoundAssertCastExpression    → ConversionKind.CastClass / Unbox
//   BoundNarrowedExpression      → ConversionKind.Narrow  (flow-proven smart-cast)
//   BoundParenthesizedExpression → DROPPED (binder unwraps; never enters bound tree)
//
// BoundTypeTestExpression is NOT replaced — it is kept as the boolean `is`-test
// primitive that produces bool, not a typed value.
//
// Codegen contract: CodeGen.EmitExpression switches exhaustively on ConversionKind,
// all mechanical IL). No FEATURE node dispatch is needed here — BoundConversion
// is CORE and survives lowering unchanged.
//
// Binder contract: the binder never constructs BoundConversion directly; it calls
// the static factory methods below (one per conversion origin). This keeps the
// ConversionKind assignment logic in one place and out of the binder's site-by-site
// decision trees.
//
// See contract-spine.md §1 "[Δ] Unify the cast/narrow family" for the rationale.
// ============================================================================

namespace Esharp.BoundTree;

// ---------------------------------------------------------------------------
// ConversionKind — the IL-level shapes a conversion can take.
// ---------------------------------------------------------------------------

/// Discriminates the IL instruction (or lack thereof) the codegen emits for a
/// BoundConversion. The values map 1-to-1 onto the cases a single
/// CodeGen.EmitConversion switch handles.
public enum ConversionKind
{
    /// No IL instruction — operand type IS the target type (or an implicit
    /// widening within a single CLR slot, e.g. int → long widened by IL arithmetic).
    /// Produced for: implicit upcasts, same-type assignments, inferred-type resolution.
    Identity,

    /// `box T` — value type to object or interface reference.
    /// Produced for: value-type arguments to interface/object slots, explicit `box` at
    /// BCL boundary, E# `*T` heap-pointer materialization from a struct.
    Box,

    /// `unbox.any T` — object / interface reference back to value type.
    /// Produced for: explicit downcast to a value type from object, BCL returns that
    /// come back as `object` (e.g. Hashtable indexers), match-arm struct payloads.
    Unbox,

    /// `castclass T` — reference downcast that throws InvalidCastException on failure.
    /// Produced for: `as! T` (assert-cast), explicit E# cast syntax on reference types,
    /// match-arm case payloads whose success is proven only by tag comparison.
    CastClass,

    /// `isinst T` — safe reference cast; null on miss, non-null on hit.
    /// Result type is ALWAYS `T?` (nullable reference / NullableType wrapper).
    /// Produced for: `as T` (safe-cast), BCL-interop returns that may be null.
    IsInst,

    /// Binder-proven narrowing — flow analysis established the operand IS T at this
    /// program point. Emits `isinst T` (reference) or `unbox.any T` (value type)
    /// followed by a definite-use of the result slot (the verifier sees a non-null
    /// reference or unboxed value, no null check needed).
    /// Produced for: E# smart-cast projections from `is`-guarded branches, match
    /// bindings of non-nullable union cases, let-guard narrowing.
    /// (The binder produces this via the BoundConversion.Narrow factory.)
    Narrow,

    /// Implicit T → T? lift: wraps a value type `T` into `Nullable<T>` by calling
    /// `newobj Nullable`1::.ctor(!0)`. This is the *present-case* coercion and is
    /// always implicit — the language rule says `T` is assignable to `T?` without
    /// explicit syntax. `TargetType` is the closed `NullableType(T)`.
    ///
    /// This kind exists to make the BoundConversion API complete and type-safe; in
    /// practice the three codegen store-paths (return, call-arg, var-decl / field
    /// initializer) handle the wrap inline because they have the Cecil target type
    /// already at hand. Callers that go through `BoundConversion` should use this
    /// kind so the EmitConversion switch knows exactly which IL sequence to emit.
    NullableWrap,

    /// Checked explicit conversion between E# primitive numeric types. The emitter
    /// selects a CLR conv.ovf opcode or System.Decimal conversion operator.
    NumericChecked,

    /// BCL implicit span conversion realized as a `call op_Implicit`:
    ///   `T[]`     → `Span<T>` / `ReadOnlySpan<T>`
    ///   `Span<T>` → `ReadOnlySpan<T>`
    /// These are the CLR's own implicit operators (not user-defined), the seam that
    /// lets a `stackalloc` span or a heap array flow into the many `ReadOnlySpan<T>`-
    /// taking BCL APIs. Codegen emits the operand, then `call`s the op_Implicit closed
    /// over the target span's element type (invariant, so source element == target).
    ImplicitSpan,

    /// Explicit integer → enum conversion (`ModelFileCodec(byteValue)`), the inverse of
    /// the enum → underlying-integer native conversion. An enum has no runtime
    /// representation distinct from its underlying integral, so codegen emits the operand
    /// and a `conv` to the enum's underlying width (unchecked, matching a C# `(Enum)i`
    /// cast); the stack value then IS the enum.
    IntegerToEnum,

    // NOTE: there is deliberately NO NullCheck or NullableUnwrap kind.
    //   • a null TEST is BoundTypeTestExpression (it yields bool, not a typed value);
    //   • a T? → T UNWRAP is a lowering concern (NullFlowLowering emits get_Value /
    //     GetValueOrDefault for value-type Nullable, identity for refs) — not a CORE opcode.
    // Flow analysis keys null-state off the test node + the narrowed BoundConversion, not a kind.
}

// ---------------------------------------------------------------------------
// BoundConversion — the CORE node.
// ---------------------------------------------------------------------------

public sealed partial record BoundConversion(
    BoundExpression Operand,
    BoundType TargetType,
    ConversionKind Kind)
    : BoundExpression(TargetType)
{
    // -----------------------------------------------------------------------
    // Classification helpers — consumed by codegen and diagnostic formatting.
    // -----------------------------------------------------------------------

    /// True when no IL instruction is emitted (kind == Identity).
    public bool IsNoOp => Kind == ConversionKind.Identity;

    /// True for the two boxing/unboxing kinds that involve value-type↔reference
    /// representation changes. Codegen must not elide these even when operand type
    /// structurally matches target.
    public bool IsRepresentationChange =>
        Kind is ConversionKind.Box or ConversionKind.Unbox;

    /// True for conversions that can fail at runtime (CastClass) or produce null
    /// (IsInst). Codegen emits the appropriate throw / null branch.
    public bool CanFail => Kind is ConversionKind.CastClass or ConversionKind.IsInst;

    /// True for the implicit / binder-proven safe conversions (Identity, Box, Narrow,
    /// NullableWrap). These are never user-written explicit casts — they are produced
    /// synthetically by the binder for implicit coercions, BCL interop, and
    /// flow-proven narrowings.
    public bool IsImplicit =>
        Kind is ConversionKind.Identity or ConversionKind.Box
             or ConversionKind.Narrow   or ConversionKind.NullableWrap
             or ConversionKind.ImplicitSpan;

    /// True when the result type can be null (IsInst → T? result).
    public bool ResultIsNullable => Kind == ConversionKind.IsInst;

    // -----------------------------------------------------------------------
    // Binder-facing factory methods — ONE place per conversion origin.
    // The binder calls these, never the constructor directly.
    // -----------------------------------------------------------------------

    /// No-IL identity coercion: implicit upcast, same-type assignment, or
    /// inferred-type resolution where both sides already agree on the CLR slot.
    public static BoundConversion Identity(BoundExpression operand, BoundType targetType)
        => new(operand, targetType, ConversionKind.Identity);

    /// Box a value type to object or an interface reference.
    /// `targetType` should be the interface or `object` BoundType at the use site.
    public static BoundConversion Box(BoundExpression operand, BoundType targetType)
        => new(operand, targetType, ConversionKind.Box);

    /// Unbox an object / interface reference back to a value type.
    /// `targetType` is the value-type BoundType (the unboxed shape).
    public static BoundConversion Unbox(BoundExpression operand, BoundType targetType)
        => new(operand, targetType, ConversionKind.Unbox);

    /// Assert-cast (throws on failure): `as! T` syntax or explicit downcast on
    /// reference types. `targetType` is T (non-nullable).
    public static BoundConversion AssertCast(BoundExpression operand, BoundType targetType)
        => new(operand, targetType, ConversionKind.CastClass);

    /// Safe cast (`as T`): yields null on miss. `targetType` MUST be the nullable
    /// wrapper (NullableType(T) for value types, the nullable ref type for classes).
    /// The binder is responsible for constructing the nullable target before calling.
    public static BoundConversion SafeCast(BoundExpression operand, BoundType nullableTargetType)
        => new(operand, nullableTargetType, ConversionKind.IsInst);

    /// Flow-proven narrowing: used in smart-cast projections, match-arm bindings,
    /// and let-guard narrowed slots. The binder must have established via flow
    /// analysis that operand is definitely T here — no runtime check is generated
    /// beyond the `isinst`/`unbox.any` the verifier requires.
    public static BoundConversion Narrow(BoundExpression operand, BoundType targetType)
        => new(operand, targetType, ConversionKind.Narrow);

    /// Implicit T → T? lift: wrap a value type operand into its `Nullable<T>` form.
    /// `targetType` MUST be `NullableType(T)` (the closed nullable wrapper). Codegen
    /// emits `newobj Nullable`1::.ctor(!0)` to produce the present-case struct.
    /// This factory mirrors the ESC oracle's unconditional wrap that fires when a bare
    /// value flows into a slot typed as `T?`.
    public static BoundConversion WrapNullable(BoundExpression operand, NullableType nullableTargetType)
        => new(operand, nullableTargetType, ConversionKind.NullableWrap);

    public static BoundConversion NumericChecked(BoundExpression operand, BoundType targetType)
        => new(operand, targetType, ConversionKind.NumericChecked);

    /// BCL implicit span conversion (`T[]`/`Span<T>` → span). `spanTargetType` is the
    /// `Span<T>` / `ReadOnlySpan<T>` ExternalType; codegen resolves the closed
    /// `op_Implicit` from the operand's shape and the target's element.
    public static BoundConversion ImplicitSpan(BoundExpression operand, BoundType spanTargetType)
        => new(operand, spanTargetType, ConversionKind.ImplicitSpan);

    /// Explicit integer → enum conversion (`ModelFileCodec(byteValue)`). `enumTargetType`
    /// is the target `EnumType`; codegen conv's the operand to the enum's underlying width.
    public static BoundConversion IntegerToEnum(BoundExpression operand, EnumType enumTargetType)
        => new(operand, enumTargetType, ConversionKind.IntegerToEnum);

    // -----------------------------------------------------------------------
    // IL opcode hint — a documentation aid for the codegen author; the switch
    // on Kind is still the canonical dispatch, but this gives a human-readable
    // summary of what opcode each case emits.
    // -----------------------------------------------------------------------

    /// Returns a short string describing the IL instruction(s) emitted for this
    /// conversion. Intended for debug output, dump-il tooling, and diagnostic
    /// message formatting — NOT for codegen dispatch (use Kind for that).
    public string ILHint => Kind switch
    {
        ConversionKind.Identity     => "<no-op>",
        ConversionKind.Box          => "box",
        ConversionKind.Unbox        => "unbox.any",
        ConversionKind.CastClass    => "castclass",
        ConversionKind.IsInst       => "isinst",
        ConversionKind.Narrow       => "isinst/unbox.any (proven)",
        ConversionKind.NullableWrap => "newobj Nullable`1::.ctor",
        ConversionKind.NumericChecked => "conv.ovf.* / System.Decimal conversion",
        ConversionKind.ImplicitSpan => "call op_Implicit (span)",
        ConversionKind.IntegerToEnum => "conv.* (integer → enum underlying)",
        _                           => "unknown",
    };

    // -----------------------------------------------------------------------
    // Consolidation map (documentation inline — not executable).
    // The four pre-rewrite nodes each map to exactly one factory:
    //
    //   BoundSafeCastExpression(operand, T)
    //       → BoundConversion.SafeCast(operand, T?)
    //
    //   BoundAssertCastExpression(operand, T)
    //       → BoundConversion.AssertCast(operand, T)     [ref]
    //         BoundConversion.Unbox(operand, T)          [value]
    //
    //   BoundNarrowedExpression(operand, T)
    //       → BoundConversion.Narrow(operand, T)
    //
    //   BoundParenthesizedExpression(inner)
    //       → DROPPED — the binder returns inner directly.
    //
    // BoundTypeTestExpression(operand, T, negated) is unchanged; it produces
    // bool and is not a conversion.
    // -----------------------------------------------------------------------
}

// ---------------------------------------------------------------------------
// ConversionClassification — static analysis utilities.
//
// Helpers that reason about a pair (sourceType, targetType) without a concrete
// BoundConversion node in hand. Used by the binder's implicit-conversion lookup
// and the diagnostics layer's "did you mean `as!`?" suggestion logic.
// ---------------------------------------------------------------------------

/// Classifies a potential conversion between two BoundTypes and recommends the
/// ConversionKind the binder should use. This does NOT perform the conversion —
/// it is purely a classification oracle called during binding to pick the right
/// factory method.
public static class ConversionClassification
{
    /// Classify the conversion from `source` to `target`.
    /// Returns null when no conversion is possible (the binder should emit ES2060).
    public static ConversionKind? Classify(BoundType source, BoundType target)
    {
        // Identical types — no IL needed.
        if (source == target) return ConversionKind.Identity;

        // InferredType hole → always identity (the slot will resolve later).
        if (source is InferredType || target is InferredType)
            return ConversionKind.Identity;

        // Null literal to any nullable or reference type.
        if (source is NullType)
            return IsNullable(target) || IsReference(target)
                ? ConversionKind.Identity
                : null;

        // Value type T → T? (Nullable<T>): the implicit present-case lift.
        // Must be checked BEFORE the box branch so `int → int?` doesn't silently
        // fall through to a box (which would be wrong — a box lifts to `object`, not
        // to the `Nullable<int>` struct the CLR requires for a T? slot).
        if (IsValueType(source) && target is NullableType nt && IsValueType(nt.Inner))
            return ConversionKind.NullableWrap;

        // Value type → object / interface: box.
        if (IsValueType(source) && IsObjectOrInterface(target))
            return ConversionKind.Box;

        // object / interface → value type: unbox.
        if (IsObjectOrInterface(source) && IsValueType(target))
            return ConversionKind.Unbox;

        // BCL implicit span conversion (`T[]`/`Span<T>` → span). Checked BEFORE the
        // reference→reference branch: a `Span<T>` is a by-ref-like ExternalType that
        // IsReference reports true for, so without this it would wrongly classify as a
        // castclass. It is really the framework's `op_Implicit` — a `call`, not a cast.
        if (IsImplicitSpanConversion(source, target))
            return ConversionKind.ImplicitSpan;

        // Reference type → narrower reference type (could fail at runtime): castclass.
        // The binder chooses between CastClass (assert) and IsInst (safe) based on
        // E# syntax (`as!` vs `as`); the caller passes the chosen kind.
        if (IsReference(source) && IsReference(target))
            return ConversionKind.CastClass; // caller may override to IsInst for `as T`.

        return null;
    }

    /// The CLR's implicit span conversions: an array to a span of its element type,
    /// or a `Span<T>` to a `ReadOnlySpan<T>` of the same element. Spans are invariant
    /// in T, so the element must match exactly (`byte[]` → `ReadOnlySpan<byte>`, never
    /// `→ ReadOnlySpan<object>`). Realized as the framework's `op_Implicit`.
    public static bool IsImplicitSpanConversion(BoundType source, BoundType target)
    {
        if (target is not ExternalType { Name: "Span" or "ReadOnlySpan", TypeArguments: { Count: 1 } targetArgs })
            return false;
        var elem = targetArgs[0];
        // T[] → Span<T> / ReadOnlySpan<T>
        if (source is ArrayBoundType arr)
            return arr.ElementType == elem;
        // Span<T> → ReadOnlySpan<T>
        return target is ExternalType { Name: "ReadOnlySpan" }
            && source is ExternalType { Name: "Span", TypeArguments: { Count: 1 } srcArgs }
            && srcArgs[0] == elem;
    }

    /// True when t is a value-type BoundType (struct, enum, primitive, tuple).
    /// External types are conservatively treated as reference.
    /// ByRefBoundType is NOT a value type — it is a managed reference (`ref T` local);
    /// HeapPointerBoundType is NOT a value type — it is a synthesized wrapper class.
    public static bool IsValueType(BoundType t) => t switch
    {
        PrimitiveType p when p.Name is "bool" or "byte" or "sbyte"
                                              or "short" or "ushort"
                                              or "int"   or "uint"
                                              or "long"  or "ulong"
                                              or "float" or "double" or "decimal"
                                              or "char"  or "nint"   or "nuint" => true,
        DataType d => d.Classification == DataClassification.Struct,
        EnumType   => true,
        TupleType  => true,
        _          => false,
    };

    /// True when t is a reference type (class, interface, choice, delegate, array,
    /// external, nullable-ref) or a nullable wrapper over a value type.
    public static bool IsReference(BoundType t) => t switch
    {
        DataType d        => d.Classification == DataClassification.Class,
        InterfaceType     => true,
        ChoiceType c      => c.IsRef,
        NamedDelegateType => true,
        ArrayBoundType    => true,
        ExternalType      => true,
        ExternalCSharpType => true,
        NullableType      => true,
        _                 => false,
    };

    /// True when t can hold null (NullableType, reference types, ExternalType).
    public static bool IsNullable(BoundType t)
        => t is NullableType || IsReference(t);

    /// True when t is object (PrimitiveType("object")) or an InterfaceType.
    public static bool IsObjectOrInterface(BoundType t) => t switch
    {
        PrimitiveType p   => p.Name == "object",
        InterfaceType     => true,
        _                 => false,
    };
}
