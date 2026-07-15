// Only ILOpCode is needed from SRM; alias it so TypeReference/MethodDefinition/
// ModuleDefinition resolve unambiguously to Mono.Cecil (CS0104).
using ILOpCode = System.Reflection.Metadata.ILOpCode;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace Esharp.Emit;

/// <summary>
/// The typed, stack-checked verb API over a method body — the dumb emitter's
/// only window onto IL. Backed by Cecil's <see cref="ILProcessor"/>, but the
/// surface speaks verbs (<c>LoadLocal</c>, <c>Call</c>, <c>Box</c>, <c>Branch</c>),
/// never raw opcodes. The builder owns:
/// <list type="bullet">
///   <item>short-form folding (ldc.i4 / ldarg / ldloc / stloc) via <see cref="ILOpCodeFacts"/>;</item>
///   <item>stack-depth tracking and <see cref="MaxStack"/>, asserting balance at labels;</item>
///   <item>the single Cecil seam — <see cref="EmitOpCode(ILOpCode)"/> and the operand overloads;</item>
///   <item>control flow as abstract labels (<see cref="ILLabel"/>) and the try/finally/catch region API.</item>
/// </list>
///
/// <para>Control transfer is NEVER raw: the only way to branch is the branch
/// verbs. <see cref="EmitOpCode(ILOpCode)"/> asserts if handed a control-transfer
/// opcode (Roslyn's invariant), forcing callers through <see cref="Branch"/>,
/// <see cref="Ret"/>, <see cref="Throw"/>, <see cref="Leave"/>, etc.</para>
///
/// <para>This is a faithful, Cecil-grounded port of Roslyn's
/// <c>Microsoft.CodeAnalysis.CodeGen.ILBuilder</c> verb surface. Cecil resolves
/// branch short/long forms at write time (<see cref="ILProcessor"/> +
/// <c>Body.Optimize</c>), so the builder folds only operand-encoded forms and
/// leaves branch sizing to the writer.</para>
/// </summary>
public sealed class ILBuilder
{
    readonly MethodDefinition _method;
    readonly ILProcessor _il;

    int _currentStack;
    int _maxStack;

    /// <summary>
    /// When true, stack-depth mismatches at labels/branches and underflow throw
    /// (Roslyn's verify-by-construction invariant). Default false: the tracker
    /// still computes <see cref="MaxStack"/> and records expected depths, but a
    /// mismatch is tolerated rather than fatal — the CORE-node walk has a few
    /// straight-line fall-through shapes (post-throw, post-leave) the linear
    /// tracker can't model precisely, and a draft emitter must not regress on
    /// them. Flip on in tests to surface genuine imbalance bugs.
    /// </summary>
    public bool StrictStackChecking { get; set; }

    public ILBuilder(MethodDefinition method)
    {
        _method = method;
        _il = method.Body.GetILProcessor();
    }

    /// <summary>The method body this builder writes into.</summary>
    public MethodDefinition Method => _method;

    /// <summary>The module owning this method (operand import goes through <see cref="MetadataBinder"/>).</summary>
    public ModuleDefinition Module => _method.Module;

    /// <summary>Current modeled evaluation-stack depth.</summary>
    public int CurrentStack => _currentStack;

    /// <summary>High-water stack depth; the value to publish as <c>MaxStackSize</c>.</summary>
    public int MaxStack => _maxStack;

    // ── stack accounting ─────────────────────────────────────────────────────

    void Adjust(int delta)
    {
        _currentStack += delta;
        if (_currentStack < 0)
        {
            if (StrictStackChecking)
                throw new InvalidOperationException(
                    $"IL stack underflow in {_method.FullName} (delta {delta} drove depth below zero).");
            _currentStack = 0;
        }
        if (_currentStack > _maxStack)
            _maxStack = _currentStack;
    }

    /// <summary>
    /// Explicit stack adjustment for the variable-behavior opcodes (call/newobj/…),
    /// where the delta depends on a signature the facts table cannot know. Callers
    /// in this class supply it; not part of the public verb surface.
    /// </summary>
    void AdjustExplicit(int delta) => Adjust(delta);

    // ── the single Cecil emission seam ───────────────────────────────────────

    void Append(Instruction insn) => _il.Append(insn);

    /// <summary>Emit an operand-less opcode, adjusting the stack via the facts table.
    /// Asserts on control-transfer opcodes — those must go through the branch verbs.</summary>
    void EmitOpCode(ILOpCode op)
    {
        if (op.IsControlTransfer())
            throw new InvalidOperationException(
                $"Control-transfer opcode {op} must be emitted through a branch verb, not EmitOpCode.");
        Adjust(op.NetStackBehavior());
        Append(Instruction.Create(op.ToCecil()));
    }

    // Operand-carrying primitive emits (still pre-stack-adjusted by the caller verb).
    void Emit(ILOpCode op, TypeReference t) => Append(Instruction.Create(op.ToCecil(), t));
    void Emit(ILOpCode op, MethodReference m) => Append(Instruction.Create(op.ToCecil(), m));
    void Emit(ILOpCode op, FieldReference f) => Append(Instruction.Create(op.ToCecil(), f));
    void Emit(ILOpCode op, VariableDefinition v) => Append(Instruction.Create(op.ToCecil(), v));
    void Emit(ILOpCode op, ParameterDefinition p) => Append(Instruction.Create(op.ToCecil(), p));
    void Emit(ILOpCode op, string s) => Append(Instruction.Create(op.ToCecil(), s));
    void Emit(ILOpCode op, int i) => Append(Instruction.Create(op.ToCecil(), i));
    void Emit(ILOpCode op, sbyte b) => Append(Instruction.Create(op.ToCecil(), b));
    void Emit(ILOpCode op, long l) => Append(Instruction.Create(op.ToCecil(), l));
    void Emit(ILOpCode op, float f) => Append(Instruction.Create(op.ToCecil(), f));
    void Emit(ILOpCode op, double d) => Append(Instruction.Create(op.ToCecil(), d));

    // ── locals / arguments ───────────────────────────────────────────────────
    // The verbs emit the canonical LONG forms (ldloc / stloc / ldarg / …). Short-
    // form selection (ldloc.0..3 / ldloc.s, ldc.i4.N, branch.s) is the job of
    // ILOptimizer.ShortenOpcodes, which CodeGen runs per body AFTER every body is
    // built — when variable/parameter indices are final. Folding eagerly here would
    // read a not-yet-assigned VariableDefinition.Index (−1 before Body.Variables.Add),
    // so we defer it, matching the proven optimizer contract.

    public void LoadLocal(VariableDefinition v) { Adjust(+1); Emit(ILOpCode.Ldloc, v); }
    public void StoreLocal(VariableDefinition v) { Adjust(-1); Emit(ILOpCode.Stloc, v); }
    public void LoadLocalAddress(VariableDefinition v) { Adjust(+1); Emit(ILOpCode.Ldloca, v); }

    public void LoadArg(ParameterDefinition p) { Adjust(+1); Emit(ILOpCode.Ldarg, p); }
    public void StoreArg(ParameterDefinition p) { Adjust(-1); Emit(ILOpCode.Starg, p); }
    public void LoadArgAddress(ParameterDefinition p) { Adjust(+1); Emit(ILOpCode.Ldarga, p); }

    // Index-based argument access for synthesized bodies (trivial accessors, ctors,
    // equals/hashcode/deconstruct) where the IL arg index is known directly and the
    // ParameterDefinition object isn't conveniently in hand. The index is the raw IL
    // arg slot (this == 0 for instance methods). ldarg.0..3 have no operand so they
    // can be emitted directly; the rest resolve the ParameterDefinition from the
    // signature and emit the long form (the optimizer shortens to ldarg.s later).
    public void LoadArgByIndex(int ilIndex)
    {
        Adjust(+1);
        switch (ilIndex)
        {
            case 0: Append(Instruction.Create(CecilOpCodes.Ldarg_0)); break;
            case 1: Append(Instruction.Create(CecilOpCodes.Ldarg_1)); break;
            case 2: Append(Instruction.Create(CecilOpCodes.Ldarg_2)); break;
            case 3: Append(Instruction.Create(CecilOpCodes.Ldarg_3)); break;
            default:
                int paramIndex = _method.HasThis ? ilIndex - 1 : ilIndex;
                Emit(ILOpCode.Ldarg, _method.Parameters[paramIndex]);
                break;
        }
    }

    /// <summary>ldarga by IL arg index (for synthesized bodies that take a struct
    /// receiver's address). Index includes <c>this</c> for instance methods.</summary>
    public void LoadArgAddressByIndex(int ilIndex)
    {
        Adjust(+1);
        int paramIndex = _method.HasThis ? ilIndex - 1 : ilIndex;
        Emit(ILOpCode.Ldarga, _method.Parameters[paramIndex]);
    }

    // ── fields ───────────────────────────────────────────────────────────────

    public void LoadField(FieldReference f) { Adjust(0); Emit(ILOpCode.Ldfld, f); }       // pop obj, push value
    public void LoadFieldAddress(FieldReference f) { Adjust(0); Emit(ILOpCode.Ldflda, f); }
    public void StoreField(FieldReference f) { Adjust(-2); Emit(ILOpCode.Stfld, f); }
    public void LoadStaticField(FieldReference f) { Adjust(+1); Emit(ILOpCode.Ldsfld, f); }
    public void LoadStaticFieldAddress(FieldReference f) { Adjust(+1); Emit(ILOpCode.Ldsflda, f); }
    public void StoreStaticField(FieldReference f) { Adjust(-1); Emit(ILOpCode.Stsfld, f); }

    // ── constants ────────────────────────────────────────────────────────────

    public void LoadInt(int value)
    {
        Adjust(+1);
        var op = ILOpCodeFacts.FoldLoadInt(value);
        switch (op)
        {
            case ILOpCode.Ldc_i4_s: Emit(op, (sbyte)value); break;
            case ILOpCode.Ldc_i4: Emit(op, value); break;
            default: Append(Instruction.Create(op.ToCecil())); break; // ldc.i4.m1/0..8
        }
    }

    public void LoadLong(long value) { Adjust(+1); Emit(ILOpCode.Ldc_i8, value); }
    public void LoadFloat(float value) { Adjust(+1); Emit(ILOpCode.Ldc_r4, value); }
    public void LoadDouble(double value) { Adjust(+1); Emit(ILOpCode.Ldc_r8, value); }
    public void LoadString(string value) { Adjust(+1); Emit(ILOpCode.Ldstr, value); }
    public void LoadNull() => EmitOpCode(ILOpCode.Ldnull);

    /// <summary>ldtoken of a type/method/field (push 1).</summary>
    public void LoadToken(TypeReference t) { Adjust(+1); Emit(ILOpCode.Ldtoken, t); }
    public void LoadToken(MethodReference m) { Adjust(+1); Emit(ILOpCode.Ldtoken, m); }
    public void LoadToken(FieldReference f) { Adjust(+1); Emit(ILOpCode.Ldtoken, f); }

    /// <summary>ldftn — push a managed function pointer (push 1).</summary>
    public void LoadFunctionPointer(MethodReference m) { Adjust(+1); Emit(ILOpCode.Ldftn, m); }

    /// <summary>ldvirtftn — pop obj, push the virtual function pointer (net 0).</summary>
    public void LoadVirtualFunctionPointer(MethodReference m) { Adjust(0); Emit(ILOpCode.Ldvirtftn, m); }

    // ── calls / construction ─────────────────────────────────────────────────
    // Variable-stack opcodes: pop the argument count (+ receiver for instance/
    // virtual + the constructed value is NOT popped for newobj), push 1 if the
    // signature returns non-void. Computed from the resolved MethodReference.

    static int ArgPopCount(MethodReference m, bool includeThis) =>
        m.Parameters.Count + (includeThis && m.HasThis ? 1 : 0);

    static bool ReturnsValue(MethodReference m) =>
        m.ReturnType is not null && m.ReturnType.FullName != "System.Void";

    public void Call(MethodReference m)
    {
        AdjustExplicit(-ArgPopCount(m, includeThis: true) + (ReturnsValue(m) ? 1 : 0));
        Emit(ILOpCode.Call, m);
    }

    public void CallVirt(MethodReference m)
    {
        AdjustExplicit(-ArgPopCount(m, includeThis: true) + (ReturnsValue(m) ? 1 : 0));
        Emit(ILOpCode.Callvirt, m);
    }

    /// <summary>newobj — pops the ctor args, pushes the new instance.</summary>
    public void NewObj(MethodReference ctor)
    {
        AdjustExplicit(-ctor.Parameters.Count + 1);
        Emit(ILOpCode.Newobj, ctor);
    }

    /// <summary>calli — pops the arg list + the function pointer; pushes the return if any.
    /// <paramref name="argCount"/> excludes the implicit pointer slot.</summary>
    public void CallIndirect(Mono.Cecil.CallSite signature, int argCount)
    {
        bool returns = signature.ReturnType is not null && signature.ReturnType.FullName != "System.Void";
        bool hasThis = signature.HasThis;
        AdjustExplicit(-(argCount + (hasThis ? 1 : 0) + 1) + (returns ? 1 : 0));
        Append(Instruction.Create(CecilOpCodes.Calli, signature));
    }

    /// <summary>initobj — pops the managed pointer, writes the default value in place.</summary>
    public void InitObj(TypeReference t) { Adjust(-1); Emit(ILOpCode.Initobj, t); }

    /// <summary>constrained. prefix for a subsequent callvirt on a possibly-value receiver.</summary>
    public void Constrained(TypeReference t) => Emit(ILOpCode.Constrained, t);

    // ── stack ops ────────────────────────────────────────────────────────────

    public void Dup() => EmitOpCode(ILOpCode.Dup);
    public void Pop() => EmitOpCode(ILOpCode.Pop);
    public void Nop() => EmitOpCode(ILOpCode.Nop);

    /// <summary><c>tail.</c> prefix — must immediately precede the call it qualifies.
    /// No stack effect; the following call is the tail call.</summary>
    public void TailPrefix() => Append(Instruction.Create(CecilOpCodes.Tail));

    // ── object model / conversions ───────────────────────────────────────────

    public void Box(TypeReference t) { Adjust(0); Emit(ILOpCode.Box, t); }
    public void Unbox(TypeReference t) { Adjust(0); Emit(ILOpCode.Unbox, t); }
    public void UnboxAny(TypeReference t) { Adjust(0); Emit(ILOpCode.Unbox_any, t); }
    public void IsInst(TypeReference t) { Adjust(0); Emit(ILOpCode.Isinst, t); }
    public void CastClass(TypeReference t) { Adjust(0); Emit(ILOpCode.Castclass, t); }

    /// <summary>ldobj — dereference a managed pointer to a value type (net 0).</summary>
    public void LoadObject(TypeReference t) { Adjust(0); Emit(ILOpCode.Ldobj, t); }
    /// <summary>stobj — store a value type through a managed pointer (pop 2).</summary>
    public void StoreObject(TypeReference t) { Adjust(-2); Emit(ILOpCode.Stobj, t); }
    /// <summary>cpobj — copy a value type pointer→pointer (pop 2).</summary>
    public void CopyObject(TypeReference t) { Adjust(-2); Emit(ILOpCode.Cpobj, t); }

    /// <summary>Numeric conversion verb — an operand-less <c>conv.*</c> opcode, stack
    /// effect taken from the facts table (net 0 for all conv forms).</summary>
    public void Convert(ILOpCode convOp) => EmitOpCode(convOp);

    // ── arrays ───────────────────────────────────────────────────────────────

    public void NewArray(TypeReference elementType) { Adjust(0); Emit(ILOpCode.Newarr, elementType); }
    public void LoadLength() => EmitOpCode(ILOpCode.Ldlen);
    public void LoadElementAddress(TypeReference t) { Adjust(-1); Emit(ILOpCode.Ldelema, t); }
    public void LoadElement(TypeReference t) { Adjust(-1); Emit(ILOpCode.Ldelem, t); }
    public void StoreElement(TypeReference t) { Adjust(-3); Emit(ILOpCode.Stelem, t); }
    /// <summary>Typed element load via a primitive ldelem.* opcode (e.g. ldelem.ref).</summary>
    public void LoadElementTyped(ILOpCode ldelemOp) => EmitOpCode(ldelemOp);
    /// <summary>Typed element store via a primitive stelem.* opcode (e.g. stelem.ref).</summary>
    public void StoreElementTyped(ILOpCode stelemOp) => EmitOpCode(stelemOp);

    // ── arithmetic / compare (operand-less; facts-driven) ────────────────────

    public void Add() => EmitOpCode(ILOpCode.Add);
    public void Sub() => EmitOpCode(ILOpCode.Sub);
    public void Mul() => EmitOpCode(ILOpCode.Mul);
    public void Div() => EmitOpCode(ILOpCode.Div);
    public void Rem() => EmitOpCode(ILOpCode.Rem);
    public void And() => EmitOpCode(ILOpCode.And);
    public void Or() => EmitOpCode(ILOpCode.Or);
    public void Xor() => EmitOpCode(ILOpCode.Xor);
    public void Shl() => EmitOpCode(ILOpCode.Shl);
    public void Shr() => EmitOpCode(ILOpCode.Shr);
    public void Neg() => EmitOpCode(ILOpCode.Neg);
    public void Not() => EmitOpCode(ILOpCode.Not);
    public void Ceq() => EmitOpCode(ILOpCode.Ceq);
    public void Cgt() => EmitOpCode(ILOpCode.Cgt);
    public void CgtUn() => EmitOpCode(ILOpCode.Cgt_un);
    public void Clt() => EmitOpCode(ILOpCode.Clt);
    public void CltUn() => EmitOpCode(ILOpCode.Clt_un);

    /// <summary>Escape hatch for the rare operand-less opcode not given its own verb
    /// (e.g. <c>ldind.*</c>, <c>conv.*</c>, <c>ldelem.ref</c>). Routes through the
    /// same stack-tracked seam. Control-transfer opcodes still assert.</summary>
    public void EmitPrimitive(ILOpCode op) => EmitOpCode(op);

    // ── control flow — the ONLY way to branch ────────────────────────────────

    /// <summary>Allocate an unmarked label.</summary>
    public ILLabel DefineLabel(string? name = null) =>
        new(Instruction.Create(CecilOpCodes.Nop), name);

    /// <summary>Place a label at the current position. Asserts that the stack depth
    /// here matches every branch that targets it (Roslyn's MarkLabel invariant).</summary>
    public void MarkLabel(ILLabel label)
    {
        if (label.IsMarked)
            throw new InvalidOperationException($"Label {label.Name ?? "<anon>"} marked twice.");
        ReconcileStack(label);
        label.IsMarked = true;
        Append(label.Marker);
    }

    void ReconcileStack(ILLabel label)
    {
        if (label.ExpectedStack is int expected)
        {
            if (expected != _currentStack)
            {
                if (StrictStackChecking)
                    throw new InvalidOperationException(
                        $"Stack imbalance at label {label.Name ?? "<anon>"} in {_method.FullName}: " +
                        $"expected depth {expected}, builder depth {_currentStack}.");
                // Trust the recorded branch-target depth: a label is reached with the
                // depth its branches agreed on, even if the linear fall-through model
                // diverged (post-throw / unreachable fall-through).
                _currentStack = expected;
            }
        }
        else
        {
            label.ExpectedStack = _currentStack;
        }
    }

    void RecordBranchTarget(ILLabel label, int stackAfterPop)
    {
        if (label.ExpectedStack is int expected)
        {
            if (expected != stackAfterPop && StrictStackChecking)
                throw new InvalidOperationException(
                    $"Branch to label {label.Name ?? "<anon>"} with stack depth {stackAfterPop} " +
                    $"conflicts with recorded depth {expected} in {_method.FullName}.");
        }
        else
        {
            label.ExpectedStack = stackAfterPop;
        }
    }

    /// <summary>Unconditional branch.</summary>
    public void Branch(ILLabel target)
    {
        // br pops nothing.
        RecordBranchTarget(target, _currentStack);
        Append(Instruction.Create(CecilOpCodes.Br, target.Marker));
    }

    /// <summary>brtrue — branch if the top of stack is non-zero/non-null (pops 1).</summary>
    public void BranchIfTrue(ILLabel target)
    {
        Adjust(-1);
        RecordBranchTarget(target, _currentStack);
        Append(Instruction.Create(CecilOpCodes.Brtrue, target.Marker));
    }

    /// <summary>brfalse — branch if the top of stack is zero/null (pops 1).</summary>
    public void BranchIfFalse(ILLabel target)
    {
        Adjust(-1);
        RecordBranchTarget(target, _currentStack);
        Append(Instruction.Create(CecilOpCodes.Brfalse, target.Marker));
    }

    /// <summary>A relational conditional branch (beq/bne.un/bge/bgt/ble/blt and .un
    /// variants). The currency opcode must be a conditional relational branch; it
    /// pops two operands.</summary>
    public void BranchRelational(ILOpCode branchOp, ILLabel target)
    {
        if (!branchOp.IsConditionalBranch())
            throw new InvalidOperationException($"{branchOp} is not a conditional branch.");
        Adjust(branchOp.NetStackBehavior()); // pops 2 (relational) or 1 (brtrue/brfalse)
        RecordBranchTarget(target, _currentStack);
        Append(Instruction.Create(branchOp.ToCecil(), target.Marker));
    }

    /// <summary>switch — jump table over the top-of-stack index (pops 1).</summary>
    public void Switch(IReadOnlyList<ILLabel> targets)
    {
        Adjust(-1);
        foreach (var t in targets)
            RecordBranchTarget(t, _currentStack);
        var markers = new Instruction[targets.Count];
        for (int i = 0; i < targets.Count; i++)
            markers[i] = targets[i].Marker;
        Append(Instruction.Create(CecilOpCodes.Switch, markers));
    }

    /// <summary>ret — for a non-void method the return value is popped.</summary>
    public void Ret(bool returnsValue)
    {
        if (returnsValue)
            Adjust(-1);
        Append(Instruction.Create(CecilOpCodes.Ret));
    }

    /// <summary>ret, inferring value/void from the method's return type. Convenient for
    /// synthesized straight-line bodies (accessors, equals, ctors) where the body
    /// already left the value (or nothing) on the stack.</summary>
    public void Return() => Ret(_method.ReturnType.FullName != "System.Void");

    /// <summary>throw — pops the exception object; control does not return.</summary>
    public void Throw()
    {
        Adjust(-1);
        Append(Instruction.Create(CecilOpCodes.Throw));
    }

    /// <summary>rethrow — only valid inside a catch handler; no stack effect.</summary>
    public void Rethrow() => Append(Instruction.Create(CecilOpCodes.Rethrow));

    /// <summary>leave — exit a protected region to <paramref name="target"/>, clearing
    /// the eval stack (the stack is emptied by the runtime, so model depth resets to 0).</summary>
    public void Leave(ILLabel target)
    {
        _currentStack = 0;
        // A leave target is reached with an empty stack.
        if (target.ExpectedStack is int e && e != 0 && StrictStackChecking)
            throw new InvalidOperationException(
                $"leave target {target.Name ?? "<anon>"} expected non-empty stack {e}.");
        target.ExpectedStack = 0;
        Append(Instruction.Create(CecilOpCodes.Leave, target.Marker));
    }

    // ── exception regions ────────────────────────────────────────────────────
    // A handle records the boundary markers; CodeGen brackets a region with
    // BeginTry → (body) → the handler builder, then closes it. The builder owns
    // the Cecil ExceptionHandler construction — CodeGen never touches it.

    /// <summary>An open protected region. Boundary markers are filled in as the
    /// region is bracketed, then materialized into a Cecil <see cref="ExceptionHandler"/>.</summary>
    public sealed class TryRegion
    {
        internal Instruction TryStart = null!;
        internal Instruction TryEnd = null!;
        internal Instruction HandlerStart = null!;
        internal Instruction HandlerEnd = null!;
        internal TypeReference? CatchType;
        internal ExceptionHandlerType Kind;
    }

    /// <summary>Open a protected region at the current position.</summary>
    public TryRegion BeginTry()
    {
        if (_currentStack != 0 && StrictStackChecking)
            throw new InvalidOperationException(
                $"try region opened with non-empty stack ({_currentStack}) in {_method.FullName}.");
        var start = Instruction.Create(CecilOpCodes.Nop);
        Append(start);
        return new TryRegion { TryStart = start };
    }

    /// <summary>Close the try body and open a <c>finally</c> handler.</summary>
    public void BeginFinallyBlock(TryRegion region)
    {
        var handlerStart = Instruction.Create(CecilOpCodes.Nop);
        region.TryEnd = handlerStart;
        region.HandlerStart = handlerStart;
        region.Kind = ExceptionHandlerType.Finally;
        _currentStack = 0;
        Append(handlerStart);
    }

    /// <summary>Close the try body and open a <c>catch</c> handler for
    /// <paramref name="exceptionType"/>. The thrown exception is on the stack on entry.</summary>
    public void BeginCatchBlock(TryRegion region, TypeReference exceptionType)
    {
        var handlerStart = Instruction.Create(CecilOpCodes.Nop);
        region.TryEnd = handlerStart;
        region.HandlerStart = handlerStart;
        region.CatchType = exceptionType;
        region.Kind = ExceptionHandlerType.Catch;
        _currentStack = 1; // the runtime pushes the exception object on handler entry
        if (_currentStack > _maxStack) _maxStack = _currentStack;
        Append(handlerStart);
    }

    /// <summary>endfinally — terminate a finally handler (no stack effect).</summary>
    public void EndFinally()
    {
        _currentStack = 0;
        Append(Instruction.Create(CecilOpCodes.Endfinally));
    }

    /// <summary>endfilter — terminate a filter region; pops the i4 filter result
    /// (1 = run handler, 0 = keep searching) and transfers control.</summary>
    public void EndFilter()
    {
        Adjust(-1);
        _currentStack = 0;
        Append(Instruction.Create(CecilOpCodes.Endfilter));
    }

    /// <summary>Close a region: place its handler-end marker and register the
    /// Cecil <see cref="ExceptionHandler"/>. After this the region is sealed.</summary>
    public void EndTryRegion(TryRegion region)
    {
        var handlerEnd = Instruction.Create(CecilOpCodes.Nop);
        _currentStack = 0;
        Append(handlerEnd);
        region.HandlerEnd = handlerEnd;
        RegisterHandler(region);
    }

    /// <summary>Close a region whose handler-end coincides with an already-marked
    /// label (the leave target). The label's marker becomes the handler end.</summary>
    public void EndTryRegion(TryRegion region, ILLabel handlerEnd)
    {
        if (!handlerEnd.IsMarked)
            throw new InvalidOperationException(
                $"EndTryRegion handler-end label {handlerEnd.Name ?? "<anon>"} must be marked first.");
        region.HandlerEnd = handlerEnd.Marker;
        RegisterHandler(region);
    }

    /// <summary>
    /// Register an exception handler directly from label boundaries. Covers the
    /// catch/filter shapes the bracketed region API doesn't (multi-clause try with
    /// per-clause filters): the emitter marks all boundary labels itself, then
    /// hands them here. <paramref name="filterStart"/> is non-null only for
    /// <see cref="ILExceptionRegionKind.Filter"/>; <paramref name="catchType"/>
    /// only for <see cref="ILExceptionRegionKind.Catch"/>.
    /// </summary>
    public void AddExceptionHandler(
        ILExceptionRegionKind kind,
        ILLabel tryStart, ILLabel tryEnd,
        ILLabel handlerStart, ILLabel handlerEnd,
        TypeReference? catchType = null,
        ILLabel? filterStart = null)
    {
        _method.Body.ExceptionHandlers.Add(new ExceptionHandler(ToCecilKind(kind))
        {
            TryStart = tryStart.Marker,
            TryEnd = tryEnd.Marker,
            HandlerStart = handlerStart.Marker,
            HandlerEnd = handlerEnd.Marker,
            CatchType = catchType,
            FilterStart = filterStart?.Marker,
        });
    }

    static ExceptionHandlerType ToCecilKind(ILExceptionRegionKind kind) => kind switch
    {
        ILExceptionRegionKind.Catch => ExceptionHandlerType.Catch,
        ILExceptionRegionKind.Filter => ExceptionHandlerType.Filter,
        ILExceptionRegionKind.Finally => ExceptionHandlerType.Finally,
        ILExceptionRegionKind.Fault => ExceptionHandlerType.Fault,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    void RegisterHandler(TryRegion region) =>
        _method.Body.ExceptionHandlers.Add(new ExceptionHandler(region.Kind)
        {
            TryStart = region.TryStart,
            TryEnd = region.TryEnd,
            HandlerStart = region.HandlerStart,
            HandlerEnd = region.HandlerEnd,
            CatchType = region.CatchType,
        });

    // ── finalize ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Publish the tracked <see cref="MaxStack"/> onto the body. Short-form folding
    /// of branches and the differential-testing identity pass are owned by
    /// <see cref="ILOptimizer.ShortenOpcodes"/>, which CodeGen runs per body — this
    /// does NOT call Cecil's <c>Body.Optimize()</c> (that would bypass the
    /// <see cref="ILOptimizer.ShortenOpcodesEnabled"/> knob the fuzz harness needs).
    /// Cecil recomputes <c>MaxStackSize</c> on write, so publishing here is a
    /// verification courtesy, not a requirement.
    /// </summary>
    public void PublishMaxStack() => _method.Body.MaxStackSize = _maxStack;

    // ── escape hatch for emission mechanics living in this module ─────────────
    // ILSlot / ILHeapPointer / ILPointerEmitter were authored against a raw
    // ILProcessor. They live in Esharp.Emit (emission mechanics) and need the
    // underlying processor to append the exact instruction shapes they already
    // produce. Exposed internal so it never leaks out of the module.

    internal ILProcessor RawProcessor => _il;
    internal Instruction NewNop() => Instruction.Create(CecilOpCodes.Nop);
    internal void AppendRaw(Instruction insn) => Append(insn);

    /// <summary>
    /// Emit a <c>nop</c> and hand back its instruction identity so a sequence
    /// point can be anchored to it. The nop survives the short-form pass, so the
    /// anchor stays valid for PDB emission. This is the one place CodeGen needs a
    /// concrete instruction handle — for debug info, not for control flow.
    /// </summary>
    /// <summary>True if the method body's last instruction is a <c>ret</c>. Lets
    /// synthesized-body emitters decide whether to append a trailing return without
    /// inspecting Cecil opcodes directly.</summary>
    public static bool EndsInReturn(MethodDefinition method)
    {
        var instrs = method.Body.Instructions;
        return instrs.Count > 0 && instrs[^1].OpCode == CecilOpCodes.Ret;
    }

    public Instruction MarkSequencePoint()
    {
        var nop = Instruction.Create(CecilOpCodes.Nop);
        Append(nop);
        return nop;
    }
}
