using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;
using ILOpCode = System.Reflection.Metadata.ILOpCode;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{

    // The declaring type closed over its own generic parameters — `Result`2<!0,!1>`
    // for a generic value type, the bare TypeDefinition otherwise. Used as the
    // `ldobj` operand when loading a value-type `self` by value.
    static TypeReference SelfInstantiationRef(TypeDefinition typeDef) => ReceiverInstantiationRef(typeDef);

    /// The self/receiver type reference for `typeDef`, instantiated over its own generic
    /// parameters when generic (so `ldobj`/member access bind to the open instantiation).
    internal static TypeReference ReceiverInstantiationRef(TypeDefinition typeDef)
    {
        if (!typeDef.HasGenericParameters) return typeDef;
        var git = new Mono.Cecil.GenericInstanceType(typeDef);
        foreach (var gp in typeDef.GenericParameters)
            git.GenericArguments.Add(gp);
        return git;
    }

    void EmitDefault(BoundDefaultExpression def)
    {
        var typeRef = _types.Resolve(def.Type);
        // `initobj` is correct for value types AND for unconstrained generic
        // parameters: a generic `T` may be instantiated with a value type, so
        // `ldnull` is invalid (it produces a verifier "Nullobjref vs value 'T'"
        // mismatch). `initobj T` zeroes a temp for any instantiation — null for a
        // reference T, the zero value for a value T. Only a type KNOWN to be a
        // reference type can take the `ldnull` fast path.
        if (_types.IsValueType(def.Type) || typeRef.IsGenericParameter)
        {
            var temp = new VariableDefinition(typeRef);
            _method.Body.Variables.Add(temp);
            _il.LoadLocalAddress(temp);
            _il.InitObj(typeRef);
            _il.LoadLocal(temp);
        }
        else
        {
            _il.LoadNull();
        }
        _lastCallWasVoid = false;
    }

    void EmitLiteral(BoundLiteralExpression lit)
    {
        // Use the bound type to determine IL instruction, not just the runtime value type
        if (lit.Type is PrimitiveType pt)
        {
            switch (pt.Name)
            {
                case "int" or "uint" or "short" or "ushort" or "byte" or "sbyte" or "char":
                    _il.LoadInt(Convert.ToInt32(lit.Value));
                    return;
                case "long" or "ulong":
                    _il.LoadLong(Convert.ToInt64(lit.Value));
                    return;
                case "double":
                    _il.LoadDouble(Convert.ToDouble(lit.Value));
                    return;
                case "float":
                    _il.LoadFloat(Convert.ToSingle(lit.Value));
                    return;
                case "bool":
                    _il.LoadInt(lit.Value is true ? 1 : 0);

                    return;
                case "string":
                    // Interpolated strings are lowered to BoundInterpolatedStringExpression
                    // in the binder; any string literal reaching here is plain text.
                    if (lit.Value is string s)
                        _il.LoadString(s);
                    return;
            }
        }

        // Fallback: use runtime type
        switch (lit.Value)
        {
            case int i:
                _il.LoadInt(i);
                break;
            case char c:
                _il.LoadInt((int)c);
                break;
            case long l:
                _il.LoadLong(l);
                break;
            case double d:
                _il.LoadDouble(d);
                break;
            case float f:
                _il.LoadFloat(f);
                break;
            case bool b:
                _il.LoadInt(b ? 1 : 0);

                break;
            case string s:
                _il.LoadString(s);
                break;
            case null:
                _il.LoadNull();
                break;
        }
    }

    /// Emit a bound interpolated string as `string.Concat(object[])`. Each part
    /// is either literal text (Ldstr) or a bound hole expression (emitted through
    /// the normal pipeline, boxed if it is a value type). This replaces the legacy
    /// emit-time string-splitting (`EmitInterpolatedString`), which could only
    /// load identifier.dotted.chains and silently mis-emitted operators, calls,
    /// and payload-view projections.
    void EmitInterpolatedExpression(BoundInterpolatedStringExpression interp)
    {
        _il.LoadInt(interp.Parts.Count);
        _il.NewArray(_types.ImportReference(typeof(object)));

        for (var idx = 0; idx < interp.Parts.Count; idx++)
        {
            _il.Dup();
            _il.LoadInt(idx);

            var part = interp.Parts[idx];
            if (part.Expr is { } holeExpr)
            {
                EmitExpression(holeExpr);
                // Box value types so they fit the object[]. Prefer the inferred
                // concrete type — a hole typed `var` (e.g. a call) still needs the
                // real value type to box correctly.
                var holeType = InferConcreteType(holeExpr) ?? _types.Resolve(holeExpr.Type);
                if (holeType.IsValueType)
                    _il.Box(holeType);
            }
            else
            {
                _il.LoadString(part.Literal ?? "");
            }

            _il.EmitPrimitive(ILOpCode.Stelem_ref);
        }

        var concatMethod = _types.Module.ImportReference(
            typeof(string).GetMethod("Concat", [typeof(object[])]));
        _il.Call(concatMethod);
    }

    void EmitNameLoad(BoundNameExpression name)
    {
        // A static receiver name is a compile-time alias for a member surface.
        // Member/call emission consumes it as a type receiver; it has no runtime
        // value and therefore contributes no IL stack value itself.
        if (name.Type is StaticFuncType)
            return;

        // The self parameter itself (bare `self`) resolves to arg0 directly.
        if (IsSelfReference(name.Name))
        {
            _il.LoadArg(_method.Body.ThisParameter);
            // On a value-type receiver, arg0 is a managed pointer (`T&`). Loading
            // `self` as a VALUE (return self, pass self by value, store self) must
            // dereference it. Member access keeps the address (it goes through the
            // EmitAddress path), so this only affects whole-value uses of `self`.
            if (_method.DeclaringType is { IsValueType: true } selfDef)
                _il.LoadObject(SelfInstantiationRef(selfDef));
            return;
        }

        var slot = TryResolveSlot(name.Name);
        if (slot is not null)
        {
            slot.EmitLoad(_il);
            return;
        }

        // Bare name inside a `static Foo` body → host class static field.
        if (TryResolveStaticFuncFieldOnSelf(name.Name) is { } sfField)
        {
            // Literal const → inline value; otherwise Ldsfld.
            var resolved = sfField.Resolve();
            if (resolved?.IsLiteral == true && resolved.Constant is not null)
            {
                switch (resolved.Constant)
                {
                    case int i: _il.LoadInt(i); return;
                    case long l: _il.LoadLong(l); return;
                    case bool b: _il.LoadInt(b ? 1 : 0); return;
                    case string s: _il.LoadString(s); return;
                    case float f: _il.LoadFloat(f); return;
                    case double d: _il.LoadDouble(d); return;
                }
            }
            _il.LoadStaticField(sfField);
            return;
        }

        if (TryResolveNamespacePropertyGetter(name.Name) is { } getter)
        {
            _il.Call(getter);
            _lastCallWasVoid = false;
            return;
        }

        _diagnostics.Report("", 0, 0, $"IL: undefined variable '{name.Name}' in method '{_method.Name}'");
    }

    /// When this method is hosted on a `static Foo` class, look up a
    /// matching static field by bare name. Returns null otherwise.
    FieldReference? TryResolveStaticFuncFieldOnSelf(string fieldName)
    {
        var declType = _method.DeclaringType;
        if (declType is null) return null;
        // Static-func host classes are abstract+sealed (the standard C# static
        // class pattern). Filter to avoid accidentally hitting regular types.
        if (declType.IsAbstract && declType.IsSealed)
        {
            var own = declType.Fields.FirstOrDefault(f => f.IsStatic && f.Name == fieldName);
            if (own is not null) return own;
        }

        // Namespace state remains bare inside class methods, closures, and lowered
        // async state-machine methods whose CLR declaring type is no longer the host.
        // Recover the canonical NS.NS host by namespace and load its static field.
        var ns = declType.Namespace;
        if (string.IsNullOrEmpty(ns)) return null;
        var hostName = ns[(ns.LastIndexOf('.') + 1)..];
        var host = _types.Module.Types.FirstOrDefault(t => t.Namespace == ns && t.Name == hostName
            && t.IsAbstract && t.IsSealed);
        return host?.Fields.FirstOrDefault(f => f.IsStatic && f.Name == fieldName);
    }

    TypeDefinition? NamespaceHost()
    {
        var ns = _method.DeclaringType?.Namespace;
        if (string.IsNullOrEmpty(ns)) return null;
        var hostName = ns[(ns.LastIndexOf('.') + 1)..];
        return _types.Module.Types.FirstOrDefault(t => t.Namespace == ns && t.Name == hostName
            && t.IsAbstract && t.IsSealed);
    }

    MethodReference? TryResolveNamespacePropertyGetter(string propertyName) =>
        NamespaceHost()?.Methods.FirstOrDefault(m => m.IsStatic && m.Name == "get_" + propertyName
            && m.Parameters.Count == 0);

    private protected MethodReference? TryResolveNamespacePropertySetter(string propertyName) =>
        NamespaceHost()?.Methods.FirstOrDefault(m => m.IsStatic && m.Name == "set_" + propertyName
            && m.Parameters.Count == 1);

    private protected MethodReference? TryResolveNamespacePropertyLoca(string propertyName) =>
        NamespaceHost()?.Methods.FirstOrDefault(m => m.IsStatic && m.Name == "getloca_" + propertyName
            && m.Parameters.Count == 0 && m.ReturnType is ByReferenceType);

    private protected FieldReference? TryResolveNamespacePropertyBacking(string propertyName) =>
        NamespaceHost()?.Fields.FirstOrDefault(f => f.IsStatic
            && f.Name == CodeGenerator.PropertyBackingFieldName(propertyName));

    void EmitBinary(BoundBinaryExpression bin)
    {
        // Short-circuit logicals: && / and / || / or must NOT evaluate the
        // right operand when the left already determines the result. Otherwise
        // patterns like `i < arr.Length && arr[i] == x` index out of range.
        if (bin.Op is SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword)
        {
            var falseLabel = _il.DefineLabel();
            var endLabel = _il.DefineLabel();
            EmitExpression(bin.Left);
            _il.BranchIfFalse(falseLabel);
            EmitExpression(bin.Right);
            _il.Branch(endLabel);
            _il.MarkLabel(falseLabel);
            _il.LoadInt(0);
            _il.MarkLabel(endLabel);
            return;
        }
        if (bin.Op is SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword)
        {
            var trueLabel = _il.DefineLabel();
            var endLabel = _il.DefineLabel();
            EmitExpression(bin.Left);
            _il.BranchIfTrue(trueLabel);
            EmitExpression(bin.Right);
            _il.Branch(endLabel);
            _il.MarkLabel(trueLabel);
            _il.LoadInt(1);
            _il.MarkLabel(endLabel);
            return;
        }

        // String equality: `string == string` / `string != string` must be a
        // VALUE comparison (`String.op_Equality`), not `ceq` reference identity.
        // `ceq` only returns true when both operands are the same object — two
        // equal-but-distinct strings (a command-line arg vs an interned literal,
        // a concatenation result, etc.) would compare unequal. `char == char`
        // stays on the `ceq` path below since char is a value type.
        if (bin.Op is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals
            && (IsStringExpr(bin.Left) || IsStringExpr(bin.Right)))
        {
            EmitExpression(bin.Left);
            EmitExpression(bin.Right);
            var stringEquals = _types.Module.ImportReference(
                typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) })!);
            _il.Call(stringEquals);
            if (bin.Op is SyntaxTokenKind.BangEquals)
            {
                _il.LoadInt(0);
                _il.Ceq();
            }
            return;
        }

        // Value-shaped equality on a user `data` type: dispatch to the generated
        // op_Equality / op_Inequality (field-wise), not `ceq` reference identity.
        if (bin.Op is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals
            && bin.Left.Type is DataType && bin.Right.Type is DataType)
        {
            var typeRef = _types.Resolve(bin.Left.Type);
            var opName = bin.Op is SyntaxTokenKind.EqualsEquals ? "op_Equality" : "op_Inequality";
            var opDef = typeRef.Resolve()?.Methods.FirstOrDefault(mm => mm.Name == opName && mm.Parameters.Count == 2);
            if (opDef is not null)
            {
                Mono.Cecil.MethodReference opRef = typeRef is GenericInstanceType git
                    ? HostMethodOnType(opDef, git)
                    : _types.Module.ImportReference(opDef);
                EmitExpression(bin.Left);
                EmitExpression(bin.Right);
                _il.Call(opRef);
                return;
            }
        }

        // `x == nil` / `x != nil` on a value-type `T?`: the CLR form is the struct
        // `Nullable<T>` — `ldnull; ceq` is unverifiable against it. Nil-ness IS
        // `!HasValue`: spill to a temp (the operand may be any expression), call
        // `get_HasValue` on its address, and invert for `==`.
        if (bin.Op is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals)
        {
            static bool IsNil(BoundExpression e) =>
                e is BoundLiteralExpression { Value: null } || e.Type is NullType;
            var nullableSide =
                IsNil(bin.Right) && bin.Left.Type is NullableType ln && _types.IsValueType(ln.Inner) ? bin.Left
                : IsNil(bin.Left) && bin.Right.Type is NullableType rn && _types.IsValueType(rn.Inner) ? bin.Right
                : null;
            if (nullableSide is not null)
            {
                var nullableRef = _types.Resolve(nullableSide.Type);
                EmitExpression(nullableSide);
                var temp = new VariableDefinition(nullableRef);
                _method.Body.Variables.Add(temp);
                _il.StoreLocal(temp);
                _il.LoadLocalAddress(temp);
                _il.Call(NullableMember(nullableRef, "get_HasValue"));
                if (bin.Op is SyntaxTokenKind.EqualsEquals) // == nil ⇔ !HasValue
                {
                    _il.LoadInt(0);
                    _il.Ceq();
                }
                return;
            }

            // `*T == nil` is a carrier comparison.  A durable pointer parameter
            // is represented by `__Ptr_T`, but its normal source load is T so
            // ordinary `EmitExpression(b)` would emit `b.Value` and compare a
            // value type to null.  Keep the source-level pointer identity here;
            // this is the pointer analogue of Nullable<T>.HasValue above.
            var pointerSide = IsNil(bin.Right) ? bin.Left : IsNil(bin.Left) ? bin.Right : null;
            if (pointerSide is not null && IsPointerCarrierForNilComparison(pointerSide))
            {
                EmitPointerCarrierForNilComparison(pointerSide);
                _il.LoadNull();
                EmitBinaryOp(bin.Op);
                return;
            }
        }

        // Comparison operators that produce bool
        if (bin.Op is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals or
            SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or
            SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals)
        {
            EmitExpression(bin.Left);
            EmitExpression(bin.Right);
            EmitBinaryOp(bin.Op);
            return;
        }

        // String concatenation: `string + string` → call System.String.Concat(string, string).
        // The `add` opcode is for numerics — using it on a reference type produces
        // an InvalidProgramException at JIT time. Handle `+` and `+=` on string here.
        if (bin.Op is SyntaxTokenKind.Plus or SyntaxTokenKind.PlusEquals
            && (IsStringExpr(bin.Left) || IsStringExpr(bin.Right)))
        {
            EmitExpression(bin.Left);
            EmitExpression(bin.Right);
            var concat = typeof(string).GetMethod(
                nameof(string.Concat),
                new[] { typeof(string), typeof(string) })!;
            _il.Call(_types.Module.ImportReference(concat));
            return;
        }

        // Arithmetic
        EmitExpression(bin.Left);
        EmitExpression(bin.Right);
        EmitBinaryOp(bin.Op);
    }

    static bool IsStringExpr(BoundExpression e) =>
        e.Type is PrimitiveType { Name: "string" } || e.Type is ExternalType { Name: "string" };

    void EmitBinaryOp(SyntaxTokenKind op)
    {
        switch (op)
        {
            case SyntaxTokenKind.Plus or SyntaxTokenKind.PlusEquals:
                _il.Add(); break;
            case SyntaxTokenKind.Minus or SyntaxTokenKind.MinusEquals:
                _il.Sub(); break;
            case SyntaxTokenKind.Star or SyntaxTokenKind.StarEquals:
                _il.Mul(); break;
            case SyntaxTokenKind.Slash or SyntaxTokenKind.SlashEquals:
                _il.Div(); break;
            case SyntaxTokenKind.Percent:
                _il.Rem(); break;
            case SyntaxTokenKind.EqualsEquals:
                _il.Ceq(); break;
            case SyntaxTokenKind.BangEquals:
                _il.Ceq();
                _il.LoadInt(0);
                _il.Ceq();
                break;
            case SyntaxTokenKind.Less:
                _il.Clt(); break;
            case SyntaxTokenKind.Greater:
                _il.Cgt(); break;
            case SyntaxTokenKind.LessEquals:
                _il.Cgt();
                _il.LoadInt(0);
                _il.Ceq();
                break;
            case SyntaxTokenKind.GreaterEquals:
                _il.Clt();
                _il.LoadInt(0);
                _il.Ceq();
                break;
            case SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword:
                _il.And(); break;
            case SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword:
                _il.Or(); break;
        }
    }

    void EmitUnary(BoundUnaryExpression unary)
    {
        EmitExpression(unary.Operand);
        switch (unary.Op)
        {
            case SyntaxTokenKind.Minus:
                _il.Neg();
                break;
            case SyntaxTokenKind.Bang or SyntaxTokenKind.NotKeyword:
                _il.LoadInt(0);
                _il.Ceq();
                break;
        }
    }

    void EmitNullConditionalAccess(BoundNullConditionalAccessExpression nca)
    {
        var nullLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        // When the member is a value type, `s?.Length` is `int?` — the non-null
        // path wraps the member value in `Nullable<T>` and the null path yields a
        // default `Nullable<T>` (not `ldnull`, which is invalid for a value type).
        var resultIsValueNullable = nca.Type is NullableType nrt && _types.IsValueType(nrt.Inner);
        TypeReference? nullableRef = resultIsValueNullable ? _types.Resolve(nca.Type) : null;

        void WrapMember()
        {
            if (nullableRef is not null)
                _il.NewObj(NullableCtor(nullableRef));
        }

        void EmitNullBranch()
        {
            _il.Branch(endLabel);
            _il.MarkLabel(nullLabel);
            _il.Pop();
            if (nullableRef is not null)
            {
                var none = new VariableDefinition(nullableRef);
                _method.Body.Variables.Add(none);
                _il.LoadLocalAddress(none);
                _il.InitObj(nullableRef);
                _il.LoadLocal(none);
            }
            else
            {
                _il.LoadNull();
            }
            _il.MarkLabel(endLabel);
        }

        EmitExpression(nca.Target);
        _il.Dup();
        _il.BranchIfFalse(nullLabel);

        // Non-null path: load member (target is on stack)
        var targetType = nca.Target.Type;
        var innerType = targetType is NullableType nt ? nt.Inner : targetType;
        var runtimeType = _types.BoundTypeToRuntime(innerType);
        if (runtimeType is not null)
        {
            var prop = runtimeType.GetProperty(nca.MemberName);
            if (prop?.GetGetMethod() is { } getter)
            {
                _il.CallVirt(_types.Module.ImportReference(getter));
                WrapMember();
                EmitNullBranch();
                return;
            }
        }

        // Fallback: try to resolve as field
        var typeRef = _types.Resolve(innerType);
        var field = FindFieldOnType(typeRef, nca.MemberName);
        if (field is not null)
        {
            _il.LoadField(field);
            WrapMember();
            EmitNullBranch();
            return;
        }

        // If nothing resolved, pop and push the null branch's value
        _il.Pop();
        EmitNullBranch();
    }

    // Build a MethodReference to the `Nullable<T>(T value)` constructor on the
    // CLOSED nullable type, so `s?.Length` can wrap the int in `Nullable<int>`.
    MethodReference NullableCtor(TypeReference closedNullable)
    {
        var openCtor = typeof(System.Nullable<>).GetConstructors()[0];
        var openRef = _types.Module.ImportReference(openCtor);
        var closedRef = new MethodReference(".ctor", _types.Module.TypeSystem.Void, closedNullable)
        {
            HasThis = true,
        };
        foreach (var p in openRef.Parameters)
            closedRef.Parameters.Add(new ParameterDefinition(p.ParameterType));
        return closedRef;
    }

    void EmitNullCoalescing(BoundNullCoalescingExpression nc)
    {
        // Value-type nullable: `v ?? fallback` where `v : T?` is a CLR
        // `Nullable<T>` (a struct). `dup; brtrue` is invalid on a value type, so
        // dispatch on `HasValue` and unwrap via `GetValueOrDefault`:
        //   ldloca v; call get_HasValue; brtrue has
        //   <fallback>; br end
        //   has: ldloca v; call GetValueOrDefault
        //   end:
        if (nc.Left.Type is NullableType nt && _types.IsValueType(nt.Inner))
        {
            var nullableRef = _types.Resolve(nc.Left.Type);
            var hasValueGetter = NullableMember(nullableRef, "get_HasValue");
            var getOrDefault = NullableMember(nullableRef, "GetValueOrDefault");

            var temp = new VariableDefinition(nullableRef);
            _method.Body.Variables.Add(temp);
            EmitExpression(nc.Left);
            _il.StoreLocal(temp);

            var hasLabel = _il.DefineLabel();
            var endLabel0 = _il.DefineLabel();
            _il.LoadLocalAddress(temp);
            _il.Call(hasValueGetter);
            _il.BranchIfTrue(hasLabel);
            EmitExpression(nc.Right);
            _il.Branch(endLabel0);
            _il.MarkLabel(hasLabel);
            _il.LoadLocalAddress(temp);
            _il.Call(getOrDefault);
            _il.MarkLabel(endLabel0);
            return;
        }

        var endLabel = _il.DefineLabel();
        EmitExpression(nc.Left);
        _il.Dup();
        _il.BranchIfTrue(endLabel);
        _il.Pop();
        EmitExpression(nc.Right);
        _il.MarkLabel(endLabel);
    }

    // Build a MethodReference to a `Nullable<T>` instance member on the CLOSED
    // nullable type. The open member's signature (return `!0` for
    // GetValueOrDefault, bool for HasValue) is preserved; the runtime substitutes
    // `T` via the DeclaringType's generic argument — the same hosting trick used
    // for Result members.
    MethodReference NullableMember(TypeReference closedNullable, string name)
    {
        var openType = typeof(System.Nullable<>);
        var info = name.StartsWith("get_")
            ? openType.GetProperty(name[4..])?.GetGetMethod()
            : openType.GetMethod(name, Type.EmptyTypes);
        if (info is null)
            throw new InvalidOperationException($"Nullable<> has no member '{name}'");
        var openRef = _types.Module.ImportReference(info);
        var closedRef = new MethodReference(openRef.Name, openRef.ReturnType, closedNullable)
        {
            HasThis = openRef.HasThis,
            ExplicitThis = openRef.ExplicitThis,
            CallingConvention = openRef.CallingConvention,
        };
        foreach (var p in openRef.Parameters)
            closedRef.Parameters.Add(new ParameterDefinition(p.ParameterType));
        return closedRef;
    }

    void EmitConditional(BoundConditionalExpression cond)
    {
        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        EmitExpression(cond.Condition);
        _il.BranchIfFalse(elseLabel);
        EmitExpression(cond.Consequence);
        _il.Branch(endLabel);
        _il.MarkLabel(elseLabel);
        EmitExpression(cond.Alternative);
        _il.MarkLabel(endLabel);
    }
}
