using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{
    /// Find a C#-sibling (csHandle) method overload callable with `argCount` positional
    /// args. An exact parameter-count match wins; otherwise a longer overload matches
    /// when every surplus trailing parameter is optional (E# fills them with their
    /// declared defaults). Without the optional fallback, omitting a trailing optional
    /// matched nothing and the call was never emitted — invalid IL.
    static ICSharpMemberHandle? FindCSharpMethod(
        IReadOnlyList<ICSharpMemberHandle> members, string name, int argCount, bool wantStatic)
    {
        ICSharpMemberHandle? Match(Func<ICSharpMemberHandle, bool> arity) =>
            members.FirstOrDefault(m => m.Name == name
                && m.Kind == CSharpMemberKind.Method
                && m.IsStatic == wantStatic
                && arity(m));

        return Match(m => m.Parameters.Count == argCount)                                  // exact arity
            ?? Match(m => argCount < m.Parameters.Count && AllOptionalFrom(m.Parameters, argCount)); // optional tail
    }

    static bool AllOptionalFrom(IReadOnlyList<ICSharpParameterHandle> ps, int start)
    {
        for (var i = start; i < ps.Count; i++)
            if (!ps[i].IsOptional) return false;
        return true;
    }

    /// Push the declared defaults for trailing optional parameters the call omitted
    /// (those at index >= `provided`), so a csHandle call's stack matches the callee
    /// signature. Mirrors EmitOptionalArgument for the reflection path.
    void EmitCSharpOptionalDefaults(IReadOnlyList<ICSharpParameterHandle> ps, int provided)
    {
        for (var i = provided; i < ps.Count; i++)
            EmitCSharpOptionalDefault(ps[i]);
    }

    void EmitCSharpOptionalDefault(ICSharpParameterHandle p)
    {
        switch (p.DefaultValue)
        {
            case int i: _il.LoadInt(i); return;
            case bool b: _il.LoadInt(b ? 1 : 0); return;
            case string s: _il.LoadString(s); return;
            case long l: _il.LoadLong(l); return;
            case ulong ul: _il.LoadLong(unchecked((long)ul)); return;
            case double d: _il.LoadDouble(d); return;
            case float f: _il.LoadFloat(f); return;
            case char c: _il.LoadInt(c); return;
            case uint ui: _il.LoadInt(unchecked((int)ui)); return;
            case short sh: _il.LoadInt(sh); return;
            case ushort ush: _il.LoadInt(ush); return;
            case byte by: _il.LoadInt(by); return;
            case sbyte sb: _il.LoadInt(sb); return;
        }
        // Null default or an explicit `default(T)`: ldnull for a reference type, initobj
        // a fresh local for a value type (ldnull is invalid for value types).
        var pType = _types.Resolve(p.Type);
        if (!pType.IsValueType) { _il.LoadNull(); return; }
        var local = new VariableDefinition(pType);
        _method.Body.Variables.Add(local);
        _il.LoadLocalAddress(local);
        _il.InitObj(pType);
        _il.LoadLocal(local);
    }

    static System.Reflection.MethodInfo? ResolveIndexerGetter(Type t)
    {
        // Most BCL collections use [IndexerName("Item")] so GetProperty("Item") works.
        // string is the exception: its default indexer property is named "Chars" and
        // surfaced through the [DefaultMember] attribute. Honor that to make
        // s[i] -> char emission work uniformly.
        var item = t.GetProperty("Item");
        if (item?.GetGetMethod() is { } direct) return direct;

        var defaultMember = t.GetCustomAttributes(typeof(System.Reflection.DefaultMemberAttribute), inherit: true)
            .OfType<System.Reflection.DefaultMemberAttribute>()
            .FirstOrDefault();
        if (defaultMember is not null)
        {
            var prop = t.GetProperty(defaultMember.MemberName);
            if (prop?.GetGetMethod() is { } byName) return byName;
        }

        // Fallback: any single-arg indexed property.
        foreach (var p in t.GetProperties())
        {
            if (p.GetIndexParameters().Length == 1 && p.GetGetMethod() is { } g)
                return g;
        }
        return null;
    }

    static System.Reflection.MethodInfo? ResolveRuntimeMethod(Type declaringType, string methodName, Type[] argTypes)
    {
        var candidates = declaringType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name == methodName && m.GetParameters().Length == argTypes.Length)
            .ToList();
        if (candidates.Count == 0) return null;

        // Prefer an overload whose parameters match directly.
        foreach (var cand in candidates)
        {
            var ps = cand.GetParameters();
            var ok = true;
            for (var i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType.IsGenericParameter) continue;
                if (ps[i].ParameterType != argTypes[i]) { ok = false; break; }
            }
            if (ok && !cand.IsGenericMethodDefinition) return cand;
        }

        // Generic method: pick the overload whose parameter SHAPES best match the
        // arguments before inferring type args. Critically, a `ReadOnlySpan<T>` argument
        // must select a `ReadOnlySpan<T>` overload over a sibling `Span<T>` one —
        // `MemoryMarshal.AsBytes` / `Cast` expose exactly that pair, and picking the
        // first-declared generic overload bound a ReadOnlySpan arg to the Span overload
        // (unverifiable IL). Score by matching each parameter's generic type definition
        // against the argument's; ties keep declaration order.
        var generic = candidates
            .Where(m => m.IsGenericMethodDefinition)
            .OrderByDescending(m => GenericParamShapeScore(m.GetParameters(), argTypes))
            .FirstOrDefault();
        if (generic is null) return candidates[0];

        var typeParams = generic.GetGenericArguments();
        var paramInfos = generic.GetParameters();
        var inferred = new Type[typeParams.Length];
        for (var i = 0; i < paramInfos.Length && i < argTypes.Length; i++)
        {
            var pt = paramInfos[i].ParameterType;
            if (pt.IsGenericParameter)
            {
                var pos = pt.GenericParameterPosition;
                if (pos < inferred.Length)
                    inferred[pos] = argTypes[i];
            }
        }
        for (var i = 0; i < inferred.Length; i++)
            inferred[i] ??= typeof(object);

        try { return generic.MakeGenericMethod(inferred); }
        catch { return null; }
    }

    /// How many parameters' generic type definitions match the corresponding argument's
    /// — the discriminator between overloads that differ only by a constructed-generic
    /// parameter (`Span<T>` vs `ReadOnlySpan<T>`, `List<T>` vs `IEnumerable<T>`).
    static int GenericParamShapeScore(System.Reflection.ParameterInfo[] ps, Type[] argTypes)
    {
        var score = 0;
        for (var i = 0; i < ps.Length && i < argTypes.Length; i++)
        {
            var pt = ps[i].ParameterType;
            var at = argTypes[i];
            if (pt.IsGenericType && at.IsGenericType
                && pt.GetGenericTypeDefinition() == at.GetGenericTypeDefinition())
                score++;
        }
        return score;
    }

    /// Reflect on `aw.Inner` to determine the exact runtime awaitable type
    /// (Task, Task&lt;T&gt;, ValueTask, ValueTask&lt;T&gt;). The binder only tracks
    /// the unwrapped result type, so BoundTypeToRuntime on the inner expression
    /// won't surface that it's actually a Task-of-something. External static
    /// calls (Task.Delay, Task.FromResult, etc.) carry enough info in the
    /// reflection API to close their return type here.
    private protected Type? ResolveAwaitableRuntimeType(BoundExpression inner)
    {
        // External static call: TypeName.Method(args) — reflect to get the
        // actual MethodInfo and its closed return type.
        if (inner is BoundCallExpression
            {
                Target: BoundMemberAccessExpression { Target: BoundNameExpression staticType } staticMa
            } staticCall)
        {
            var runtimeType = _types.TryResolveRuntimeType(staticType.Name);
            if (runtimeType is not null)
            {
                var argRuntimeTypes = staticCall.Arguments
                    .Select(a => _types.BoundTypeToRuntime(a.Type) ?? typeof(object))
                    .ToArray();
                var methodInfo = ResolveRuntimeMethod(runtimeType, staticMa.MemberName, argRuntimeTypes);
                if (methodInfo is not null)
                    return methodInfo.ReturnType;
            }
        }

        // Plain function call inside the same module — look up the user
        // function's return type via the method table and map the Cecil
        // TypeReference back to a runtime Type.
        if (inner is BoundCallExpression { Target: BoundNameExpression funcName } directCall)
        {
            var method = FindMethod(funcName.Name, directCall.Arguments.Count);
            if (method is not null)
            {
                var rt = CecilToRuntimeType(method.ReturnType);
                if (rt is not null) return rt;
            }
        }

        // Name reference: look at the slot's resolved type directly. This
        // covers async-let pending fields (`__async_let_foo: Task<T>`).
        if (inner is BoundNameExpression nameRef && TryResolveSlot(nameRef.Name) is { } slot)
        {
            // The slot's Cecil TypeReference; convert back to runtime Type.
            var tr = slot.Type;
            var rt = CecilToRuntimeType(tr);
            if (rt is not null) return rt;
        }

        return _types.BoundTypeToRuntime(inner.Type);
    }

    static Type? UnwrapTaskRuntimeType(Type type)
    {
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(System.Threading.Tasks.Task<>) || def == typeof(System.Threading.Tasks.ValueTask<>))
                return type.GetGenericArguments()[0];
        }
        if (type == typeof(System.Threading.Tasks.Task) || type == typeof(System.Threading.Tasks.ValueTask))
            return typeof(void);
        return null;
    }

    static bool IsNullableValueType(TypeReference type) =>
        type is GenericInstanceType git && git.ElementType.FullName == "System.Nullable`1";

    void EmitResultCall(BoundResultCallExpression rc)
    {
        // ok(value) / error(value): build the closed Result<TOk,TErr> and construct it.
        // EmitResultConstruct picks the shape for the active Result — inline field
        // assignment for the E# stdlib (no factory class), or the C# seed's static
        // Ok/Error generic factory. Closing over the precise Cecil arg types keeps
        // user-defined payloads (DataType/ChoiceType) from erasing to `object`.
        var okRef = _types.Resolve(rc.OkType);
        var errRef = _types.Resolve(rc.ErrorType);
        var closedResult = _types.Resolve(new Esharp.BoundTree.ResultType(rc.OkType, rc.ErrorType));
        EmitResultConstruct(closedResult, rc.IsOk, okRef, errRef, () => EmitExpression(rc.Argument));
    }

    void EmitCall(BoundCallExpression call)
    {
        // Spine fast path: a call whose binder-resolved symbol names a plain
        // static method (free function or static-func member) emits straight to
        // that Cecil method by reference identity — the `Owner.member(args)`
        // member-access shape of a static-func call carries no receiver, so the
        // args are the whole stack. (Promoted instance calls have a real receiver
        // and keep EmitMemberCall's receiver-aware lowering — they never set
        // ResolvedMethod.)
        if (call.ResolvedMethod is { } resolvedSym && _types.MethodForSymbol(resolvedSym) is { } resolvedRef)
        {
            if (call.ExplicitTypeArguments is { Count: > 0 } symTypeArgs && resolvedRef.HasGenericParameters)
            {
                var generic = new Mono.Cecil.GenericInstanceMethod(resolvedRef);
                foreach (var t in symTypeArgs)
                    generic.GenericArguments.Add(_types.ResolveGenericArgument(t));
                resolvedRef = generic;
            }
            EmitCallArgsCoerced(call.Arguments, resolvedRef);
            _il.Call(resolvedRef);
            if (resolvedRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
            return;
        }

        if (call.Target is BoundMemberAccessExpression ma)
        {
            if (TryEmitDelegateInvoke(ma, call.Arguments))
                return;
            EmitMemberCall(ma, call.Arguments, call.ExplicitTypeArguments);
            return;
        }

        if (call.Target is BoundNameExpression name)
        {
            // First try: static/module method (the spine fast path above already
            // handled any call whose ResolvedMethod mapped to an emitted method).
            var methodRef = FindMethod(name.Name, call.Arguments.Count);
            if (methodRef is not null)
            {
                // Explicit generic args at the call site — close the open generic
                // method over those type args. Without this the JIT executes the
                // open method's body against uninitialized stack and returns
                // garbage. e.g. `id<int>(42)` resolves `id<T>(T) -> T`, here we
                // produce `id<int>(int) -> int`.
                if (call.ExplicitTypeArguments is { Count: > 0 } typeArgs
                    && methodRef.HasGenericParameters)
                {
                    var generic = new Mono.Cecil.GenericInstanceMethod(methodRef);
                    foreach (var t in typeArgs)
                        generic.GenericArguments.Add(_types.ResolveGenericArgument(t));
                    methodRef = generic;
                }
                EmitCallArgsCoerced(call.Arguments, methodRef);
                _il.Call(methodRef);
            }
            // Second try: *T first-param static method (e.g. prepend(list, 10))
            else if (call.Arguments.Count > 0 && TryEmitHeapPointerStaticCall(name.Name, call.Arguments))
            {
                // handled
            }
            // Third try: delegate invocation (local variable holding Action/Func)
            else if (TryEmitDelegateInvoke(name.Name, call.Arguments))
            {
                // handled
            }
            // C#-sourced constructor: `Box(n)` where Box is .cs-defined.
            // The name resolves to ExternalCSharpType — we look up the matching
            // constructor on the handle's member surface and emit newobj.
            else if (name.Type is Esharp.BoundTree.ExternalCSharpType csCtor
                  && TryEmitCSharpConstructor(csCtor, call.Arguments))
            {
                // handled
            }
            // Third try: user-defined data type constructor (init)
            //   e.g. `Square(5)` → newobj Square..ctor(int); `Box<int>(42)` → closed ctor
            else if (TryEmitUserConstructor(name.Name, call.Arguments, call.Type))
            {
                // handled
            }
            // Fourth try: external type constructor (e.g. List<int>(), Dictionary<string, int>())
            else if (TryEmitExternalConstructor(name.Name, call.Type, call.Arguments))
            {
                // handled
            }
            else
            {
                // Promoted instance method: when a free `func foo(p: P, ...)`
                // landed on P as an instance method via promotion, the call
                // site `foo(p_val, args...)` needs to lower to a virtual or
                // direct call where args[0] is the receiver. Search type-method
                // tables for a match. This is the IL-side rewrite of the
                // binder-only promotion; without it `foo(p_val, x)` silently
                // loads its args and skips the call.
                if (TryEmitPromotedInstanceCall(name.Name, call.Arguments))
                {
                    // handled
                }
                else
                {
                    var argTypes = CollectCallArgTypes(call.Arguments);
                    var explicitRuntimeTypeArgs = call.ExplicitTypeArguments?.Select(t => _types.BoundTypeToRuntime(t) ?? typeof(object)).ToArray();
                    var importedStatic = _types.ResolveImportedStaticMethod(name.Name, call.Arguments.Count, argTypes, explicitRuntimeTypeArgs);
                    if (importedStatic is not null)
                    {
                        EmitCallArgsCoerced(call.Arguments, importedStatic);
                        _il.Call(importedStatic);
                        if (importedStatic.ReturnType.FullName == "System.Void")
                            _lastCallWasVoid = true;
                    }
                    else
                    {
                        foreach (var arg in call.Arguments)
                            EmitExpression(arg);
                        _diagnostics.Report("", 0, 0, $"IL: unresolved function '{name.Name}' (argCount={call.Arguments.Count})");
                    }
                }
            }
        }
        else
        {
            // Callee is an arbitrary expression — an immediately-invoked lambda
            // (`((a) => a + 1)(x)`), a parenthesized delegate value, or a call
            // returning a delegate. Evaluate it to a delegate on the stack and
            // callvirt its Invoke. (The previous fallback emitted only the
            // arguments: the call vanished and the last argument silently
            // became the expression's value.)
            EmitExpressionInvoke(call);
        }
    }

    void EmitExpressionInvoke(BoundCallExpression call)
    {
        var callee = call.Target;
        TypeReference? delegateRef = null;
        if (callee is BoundFunctionLiteralExpression literal)
        {
            var retType = _types.Resolve(literal.ReturnType);
            var paramTypes = literal.Parameters.Select(p => _types.Resolve(p.Type)).ToList();
            delegateRef = ResolveFuncActionDelegate(retType, paramTypes).ClosedRef;
        }
        else if (_types.Resolve(callee.Type) is { } resolvedType
            && resolvedType.Resolve()?.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate")
        {
            delegateRef = resolvedType;
        }

        var invokeDef = delegateRef?.Resolve()?.Methods.FirstOrDefault(m => m.Name == "Invoke");
        if (delegateRef is null || invokeDef is null)
        {
            _diagnostics.Report("", 0, 0,
                "IL: this expression is not callable — only functions, methods, and delegate-typed values can be invoked.");
            return;
        }

        EmitExpression(callee);
        foreach (var arg in call.Arguments)
            EmitSingleCallArg(arg);

        MethodReference invokeRef;
        if (delegateRef is GenericInstanceType git)
        {
            // Keep Invoke's slots owned by the open Func/Action definition and
            // re-host that definition on the closed delegate instance.  Replacing
            // those slots manually with concrete types makes a MemberRef that looks
            // right in a disassembly but does not resolve as Func<,>.Invoke at
            // runtime.  HostExternalMethodOnType preserves the CLR generic-owner
            // identity while the closed declaring type performs substitution.
            invokeRef = HostExternalMethodOnType(invokeDef, git);
        }
        else
        {
            invokeRef = _types.Module.ImportReference(invokeDef);
        }

        _il.CallVirt(invokeRef);
        if (invokeRef.ReturnType.FullName == "System.Void")
            _lastCallWasVoid = true;
    }

    bool TryEmitPromotedInstanceCall(string funcName, IReadOnlyList<BoundExpression> args)
    {
        if (args.Count == 0) return false;
        // Inspect args[0]'s bound type to derive a candidate declaring type
        // name for an instance-method lookup.
        var receiver = args[0];
        string? recvTypeName = receiver.Type switch
        {
            Esharp.BoundTree.DataType dt => dt.Name,
            Esharp.BoundTree.ChoiceType ct => ct.Name,
            Esharp.BoundTree.ExternalType et => et.Name,
            _ => null,
        };
        if (recvTypeName is null) return false;

        TypeDefinition? owner = null;
        foreach (var t in _types.Module.Types)
        {
            if (t.Name == recvTypeName) { owner = t; break; }
        }
        if (owner is null) return false;

        // Instance method on the owner type with one fewer parameter than the
        // call's arg count (self contributes the missing one).
        var method = owner.Methods.FirstOrDefault(m =>
            m.Name == funcName && !m.IsStatic && m.Parameters.Count == args.Count - 1);
        if (method is null) return false;

        // Receiver loading: struct (value type) needs an address for instance
        // method calls so the method body's self-arg points at the live local
        // (mutating methods must update the caller's storage). Class receivers
        // pass the reference directly.
        if (owner.IsValueType) EmitAddress(receiver);
        else EmitExpression(receiver);

        // Remaining args.
        for (var i = 1; i < args.Count; i++)
            EmitSingleCallArg(args[i]);

        // Close the generic on the receiver's instantiation: a promoted call onto
        // a generic owner (`same(x)` → `x.same()` where x is `Pair<int,int>`) must
        // target `Pair<int,int>::same`, not the open def.
        Mono.Cecil.MethodReference methodRef = method;
        if (_types.Resolve(receiver.Type) is GenericInstanceType git)
            methodRef = HostMethodOnType(method, git);

        // Method-level generics (a promoted `mapped<U>` whose `U` is not a receiver type
        // param): infer the method type args from the bound argument types — a lambda's
        // delegate shape pins `U` from its body-inferred return — and close the call over
        // them, so the open `Func<T,!!0>`/`Wrap<!!0>` becomes the concrete instantiation.
        if (method.HasGenericParameters)
        {
            if (InferPromotedMethodTypeArgs(method, args) is not { } methodArgs) return false;
            var gim = new Mono.Cecil.GenericInstanceMethod(methodRef);
            foreach (var a in methodArgs) gim.GenericArguments.Add(a);
            methodRef = gim;
        }

        { if (owner.IsValueType) _il.Call(methodRef); else _il.CallVirt(methodRef); }

        if (method.ReturnType.FullName == "System.Void")
            _lastCallWasVoid = true;
        return true;
    }

    /// A pointer-receiver method call `p.m(args)` / `c.m(args)`. The method emits as a static
    /// host `m(*T, args)` whose leading parameter is the receiver — a `__Ptr_T` wrapper or, when
    /// escape analysis downgraded it, a managed pointer `T&`. So this lowers the call to
    /// `m(receiver, args)`: a `*T` receiver is passed as-is (deref to address if the host wants a
    /// byref), a value `T` receiver passes its address (byref host) or a fresh wrapper (escaping
    /// host). Returns false when no such static host exists (e.g. a value-receiver method, which
    /// has an instance method and no static host) so ordinary resolution continues.
    bool TryEmitPointerReceiverStaticCall(BoundMemberAccessExpression ma, IReadOnlyList<BoundExpression> args)
    {
        var recvType = ma.Target.Type;
        TypeReference? innerRef = recvType switch
        {
            HeapPointerBoundType hp => _types.Resolve(hp.Inner),
            // Only a value `data` (CLR struct) has a static `m(*T, …)` host and is
            // addressable for an auto-`&` call. A `ref data`/`class` is already a
            // reference — its methods are ordinary instance calls, so it must skip
            // this path entirely (else `ResolveWrapperType` below would mint a stray
            // `__Ptr_T` wrapper before the host lookup fails).
            Esharp.BoundTree.DataType { Classification: Esharp.BoundTree.DataClassification.Struct } => _types.Resolve(recvType),
            _ => null,
        };
        if (innerRef is null) return false;
        var innerName = innerRef.FullName;
        var wrapperRef = ILHeapPointer.ResolveWrapperType(_types.Module, innerRef);

        // Static host on the module class: `m(*Inner | Inner&, args)`.
        var host = _types.Module.Types.SelectMany(t => t.Methods).FirstOrDefault(m =>
            m.IsStatic && m.Name == ma.MemberName
            && m.Parameters.Count == args.Count + 1
            && m.Parameters.Count > 0
            && (m.Parameters[0].ParameterType.FullName == wrapperRef.FullName
                || (m.Parameters[0].ParameterType is ByReferenceType br && br.ElementType.FullName == innerName)));
        if (host is null) return false;

        var wantsByRef = host.Parameters[0].ParameterType is ByReferenceType;
        if (recvType is HeapPointerBoundType)
        {
            // A durable pointer parameter/local reads as its pointee in ordinary
            // source expression position. This receiver call is an ABI boundary
            // instead: the static host consumes the `__Ptr_T` carrier (or its
            // Value address), so preserve the carrier before optional deref.
            EmitHeapPointerCarrier(ma.Target);
            if (wantsByRef) ILHeapPointer.EmitDerefToAddress(_il, _types.Module, wrapperRef);
        }
        else // value `T` receiver — auto-address (Go addressable-value method call)
        {
            if (wantsByRef) EmitAddress(ma.Target);
            else { EmitExpression(ma.Target); ILHeapPointer.EmitHeapAlloc(_il, _types.Module, innerRef); }
        }
        foreach (var arg in args) EmitSingleCallArg(arg);
        _il.Call(host);
        if (host.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
        return true;
    }

    /// Infer a promoted instance method's OWN type arguments (`mapped<U>`) from the
    /// bound call args, by structurally matching each open parameter signature against
    /// the argument's runtime type — a lambda contributes its delegate shape
    /// (`Func<int,int>`), pinning `U` from the body-inferred return. Returns the closed
    /// Cecil type args in declaration order, or null if any stays unpinned.
    TypeReference[]? InferPromotedMethodTypeArgs(MethodDefinition method, IReadOnlyList<BoundExpression> args)
    {
        var gps = method.GenericParameters;
        var inferred = new TypeReference?[gps.Count];
        for (var i = 0; i < method.Parameters.Count && i < args.Count; i++)
        {
            var argRuntime = args[i] is BoundFunctionLiteralExpression fl
                ? RuntimeDelegateType(fl)
                : _types.BoundTypeToRuntime(args[i].Type);
            if (argRuntime is null) continue;
            UnifyMethodGenericParam(method.Parameters[i].ParameterType, argRuntime, gps, inferred);
        }
        for (var i = 0; i < inferred.Length; i++)
            if (inferred[i] is null) return null;
        return inferred!;
    }

    // An instance method may be generic even when its receiver is an already-closed
    // external generic type.  `Result<int,string>.Map<TNew>(Func<int,TNew>)` is the
    // important stdlib example: re-hosting Map on Result<int,string> closes TValue and
    // TError, but leaves the method-owned TNew (`!!0`) open.  A lambda argument has the
    // concrete delegate shape needed to infer TNew, so close the method before emitting
    // either the delegate or the call.  Leaving it open produces a Func<int,int> where
    // the call still expects Func<int,!!0>, which the verifier correctly rejects.
    MethodReference CloseInstanceMethodGenerics(MethodReference methodRef, MethodDefinition method,
        IReadOnlyList<BoundExpression> args, IReadOnlyList<BoundType>? explicitTypeArgs)
    {
        if (!method.HasGenericParameters) return methodRef;

        IEnumerable<TypeReference>? typeArgs = null;
        if (explicitTypeArgs is { Count: > 0 } && explicitTypeArgs.Count == method.GenericParameters.Count)
            typeArgs = explicitTypeArgs.Select(_types.Resolve).ToArray();
        else if (InferPromotedMethodTypeArgs(method, args) is { } inferred)
            typeArgs = inferred;

        if (typeArgs is null) return methodRef;
        var generic = new GenericInstanceMethod(methodRef);
        foreach (var typeArg in typeArgs)
            generic.GenericArguments.Add(typeArg);
        return generic;
    }

    /// Match an open parameter `TypeReference` (which may mention the method's generic
    /// parameters as `!!n`) against a concrete runtime arg type, filling `inferred[n]`
    /// for each METHOD generic parameter it pins. Type-owned params (`!0`, already fixed
    /// by the receiver instantiation) are skipped. Recurses through constructed generics.
    void UnifyMethodGenericParam(TypeReference paramType, Type argRuntime, Mono.Collections.Generic.Collection<GenericParameter> gps, TypeReference?[] inferred)
    {
        if (paramType is GenericParameter gp)
        {
            if (gp.Type != GenericParameterType.Method) return; // a receiver type param (`!0`)
            var idx = gps.IndexOf(gp);
            if (idx >= 0 && inferred[idx] is null)
                inferred[idx] = _types.Module.ImportReference(argRuntime);
            return;
        }
        if (paramType is GenericInstanceType pgit && argRuntime.IsGenericType)
        {
            var aArgs = argRuntime.GetGenericArguments();
            for (var i = 0; i < pgit.GenericArguments.Count && i < aArgs.Length; i++)
                UnifyMethodGenericParam(pgit.GenericArguments[i], aArgs[i], gps, inferred);
        }
    }

    /// Re-host a MethodDefinition on a closed GenericInstanceType so the call
    /// resolves to the instantiated method (`Pair<string,int>::Equals`) rather
    /// than the open definition. Signature types stay as the definition's (they
    /// reference the type's own generic parameters); Cecil maps them through the
    /// GenericInstanceType's arguments.
    // Emit an instance call on a generic-instance receiver whose closed Cecil type
    // carries a module-defined type argument (e.g. `List<Pt>`). The method is taken
    // from the open type definition (its parameters reference `!0`, `!1`, …) and
    // re-hosted on the closed instance, so user type args survive instead of being
    // erased to `object` by the runtime-reflection path.
    // Unify an extension method's `this`-parameter reflection type against the receiver's
    // closed Cecil type, filling `slots[pos]` for each method type parameter the receiver
    // pins. `this IAsyncEnumerable<T>` vs `IAsyncEnumerable<!0>` ⇒ slots[T] = !0; recurses
    // through nested generic instances.
    static void UnifyExtensionReceiver(Type reflectParam, TypeReference cecilArg, TypeReference?[] slots)
    {
        if (reflectParam.IsGenericMethodParameter)
        {
            if (reflectParam.GenericParameterPosition < slots.Length && slots[reflectParam.GenericParameterPosition] is null)
                slots[reflectParam.GenericParameterPosition] = cecilArg;
            return;
        }
        if (reflectParam.IsGenericType && cecilArg is GenericInstanceType git)
        {
            var ra = reflectParam.GetGenericArguments();
            for (var i = 0; i < ra.Length && i < git.GenericArguments.Count; i++)
                UnifyExtensionReceiver(ra[i], git.GenericArguments[i], slots);
        }
    }

    bool TryEmitModuleGenericInstanceCall(BoundType targetType, BoundMemberAccessExpression ma,
        IReadOnlyList<BoundExpression> args, bool isValueType)
    {
        TypeReference closed;
        try { closed = _types.Resolve(targetType); }
        catch { return false; }
        if (closed is not GenericInstanceType git) return false;
        // This path is for a closed receiver whose generic argument has no reliable
        // runtime Type (a module type or a generic parameter). Ordinary BCL closed
        // types retain the reflection overload resolver; async builders have their own
        // exact GenericInstanceMethod path below.
        if (!git.GenericArguments.Any(a => MentionsModuleType(a, _types.Module) || a.ContainsGenericParameter))
            return false;
        var def = git.Resolve();
        if (def is null) return false;

        // Pick a non-static method by name and arg count — exact, or with a trailing
        // optional tail the call omits (`TryComplete()` over `TryComplete(Exception = null)`,
        // `WaitToWriteAsync()` over `WaitToWriteAsync(CancellationToken = default)`).
        // EmitCallArgsCoerced fills the omitted optionals from their preserved defaults.
        var candidates = def.Methods
            .Where(m => !m.IsStatic && m.Name == ma.MemberName
                && (m.Parameters.Count == args.Count
                    || (args.Count < m.Parameters.Count && m.Parameters.Skip(args.Count).All(p => p.IsOptional))))
            .ToList();
        if (candidates.Count == 0) return false;
        var openMethod = candidates.FirstOrDefault(m =>
                m.ReturnType.ContainsGenericParameter || m.Parameters.Any(p => p.ParameterType.ContainsGenericParameter))
            ?? candidates[0];

        var methodRef = HostExternalMethodOnType(openMethod, git);
        methodRef = CloseInstanceMethodGenerics(methodRef, openMethod, args, explicitTypeArgs: null);
        if (isValueType) EmitAddress(ma.Target);
        else EmitExpression(ma.Target);
        EmitCallArgsCoerced(args, methodRef);
        { if (isValueType) _il.Call(methodRef); else _il.CallVirt(methodRef); }

        if (methodRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
        // Record the closed return (e.g. `List<T>.Enumerator`) so a value→interface
        // return can box it — the binder erases this nested generic value type.
        _lastCallReturnType = methodRef.ReturnType;
        return true;
    }

    // External static GENERIC method whose explicit type argument is a module type
    // or type parameter (`Channel.CreateUnbounded<T>()`). The reflection path erases
    // such an arg to `object`; instead resolve the open method, import it, and close
    // it over the Cecil-resolved args (so `T` stays `!0`, the return `Channel<!0>`).
    bool TryEmitExternalStaticGenericCall(Type runtimeType, BoundMemberAccessExpression ma,
        IReadOnlyList<BoundExpression> args, IReadOnlyList<BoundType> explicitTypeArgs)
    {
        TypeReference[] cecilArgs;
        try { cecilArgs = explicitTypeArgs.Select(_types.Resolve).ToArray(); }
        catch { return false; }
        // A method-owned generic parameter (`T` in `Task.Run<T>`) also has no
        // runtime `System.Type` to feed MakeGenericMethod. Route it through the
        // Cecil-only path just like a module-local data type; otherwise the
        // reflection fallback substitutes object and emits `Func<Task<object>>`
        // inside an open generic method.
        if (!cecilArgs.Any(a => MentionsModuleType(a, _types.Module) || a.ContainsGenericParameter)) return false;

        // Do not use first-reflection-order here. `Task.Run` has both
        // `Func<TResult>` and `Func<Task<TResult>>`; for a lambda returning
        // Task<T>, the latter is the more specific overload. Selecting the former
        // constructs Task<Task<T>> and leaves a verifier-invalid call site.
        static int GenericShapeDepth(Type type) => type.IsGenericParameter
            ? 0
            : type.IsGenericType
                ? 1 + type.GetGenericArguments().Sum(GenericShapeDepth)
                : 0;

        static int CecilGenericShapeDepth(TypeReference type) => type switch
        {
            GenericInstanceType generic => 1 + generic.GenericArguments.Sum(CecilGenericShapeDepth),
            _ => 0,
        };

        // Task.Run has both Func<TResult> and Func<Task<TResult>> generic overloads.
        // Their reflection generic arities are identical, so selecting solely by
        // "most nested" shape chooses the unwrap overload even for Func<T>. Compare
        // each formal shape against the already-bound Cecil argument shape first;
        // this retains T without asking reflection to manufacture a System.Type for it.
        var argumentShapeDepths = args.Select(arg =>
        {
            try { return CecilGenericShapeDepth(_types.Resolve(arg.Type)); }
            catch { return 0; }
        }).ToArray();

        // The open generic type-definition each argument is constructed from
        // (`System.ReadOnlySpan`1`), so overloads that differ ONLY by which
        // constructed generic they take — `Cast<TFrom,TTo>(Span<TFrom>)` vs
        // `Cast<TFrom,TTo>(ReadOnlySpan<TFrom>)`, `AsBytes` likewise — are
        // discriminated by matching the parameter's own generic definition.
        // Shape depth alone ties them (both are depth-1 generics), so without
        // this a ReadOnlySpan arg binds to the Span overload → unverifiable IL.
        static string? OpenDefName(Type t) =>
            t.IsGenericType ? t.GetGenericTypeDefinition().FullName : null;
        var argumentDefNames = args.Select(arg =>
        {
            try { return _types.Resolve(arg.Type) is GenericInstanceType g ? g.ElementType.FullName : null; }
            catch { return null; }
        }).ToArray();
        int DefNameMatchCount(System.Reflection.MethodInfo m) => m.GetParameters()
            .Where((p, i) => i < argumentDefNames.Length
                && OpenDefName(p.ParameterType) is { } d && d == argumentDefNames[i])
            .Count();

        var openInfo = runtimeType
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name == ma.MemberName
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == explicitTypeArgs.Count
                && m.GetParameters().Length == args.Count)
            .OrderByDescending(DefNameMatchCount)
            .ThenBy(m => m.GetParameters()
                .Select((p, i) => Math.Abs(GenericShapeDepth(p.ParameterType) - argumentShapeDepths[i]))
                .Sum())
            .ThenByDescending(m => m.GetParameters().Sum(p => GenericShapeDepth(p.ParameterType)))
            .FirstOrDefault();
        if (openInfo is null) return false;

        var gim = new Mono.Cecil.GenericInstanceMethod(_types.Module.ImportReference(openInfo));
        foreach (var a in cecilArgs) gim.GenericArguments.Add(a);
        EmitCallArgsCoerced(args, gim);
        _il.Call(gim);
        if (gim.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
        return true;
    }

    // A generic EXTENSION call that touches a module (user) type — the DI family is the
    // motivating case (`services.AddSingleton<IngestService>()`, `.AddSingleton(seedBatch())`,
    // `.AddHostedService<Svc>((sp) => …)`). Reflection's MakeGenericMethod takes only runtime
    // types, so a module type arg erases to `object` (losing the type, sometimes violating a
    // constraint) AND the first-match overload scan picks wrong overloads. Resolve the best
    // overload by SHAPE, close it over CECIL type args (explicit or inferred from the value
    // args), and call with the receiver as arg 0. Returns false (reflection path handles it)
    // when no module type is involved or no good generic overload exists.
    bool TryEmitExtensionGenericCall(Type receiverRuntimeType, BoundMemberAccessExpression ma,
        IReadOnlyList<BoundExpression> args, IReadOnlyList<BoundType>? explicitTypeArgs)
    {
        var isLambda = new bool[args.Count];
        for (var i = 0; i < args.Count; i++) isLambda[i] = args[i] is BoundFunctionLiteralExpression;

        var openInfo = _types.FindBestExtensionMethod(receiverRuntimeType, ma.MemberName, args.Count,
            explicitTypeArgs?.Count ?? 0, isLambda);
        if (openInfo is null || !openInfo.IsGenericMethodDefinition) return false;

        var typeParams = openInfo.GetGenericArguments();
        var cecilTypeArgs = new TypeReference?[typeParams.Length];
        try
        {
            if (explicitTypeArgs is { Count: > 0 } && explicitTypeArgs.Count == typeParams.Length)
            {
                for (var i = 0; i < typeParams.Length; i++) cecilTypeArgs[i] = _types.Resolve(explicitTypeArgs[i]);
            }
            else
            {
                // No explicit type args: infer each method type parameter from a value arg
                // whose parameter slot IS that bare parameter (`<T>(this IServiceCollection,
                // T instance)` ⇒ T = the arg's type). Func/lambda slots stay for the explicit
                // path; this covers `AddSingleton(seedBatch())`.
                var paramInfos = openInfo.GetParameters();
                // Infer from the RECEIVER first: `ToBlockingEnumerable<T>(this
                // IAsyncEnumerable<T>, …)` carries `T` only in the `this` parameter, so
                // unify it against the receiver's closed Cecil type (`IAsyncEnumerable<!0>`
                // ⇒ T = !0). Without this an extension whose type param appears only in the
                // receiver is never inferred and the call erases to `<object>`.
                if (paramInfos.Length > 0)
                {
                    try { UnifyExtensionReceiver(paramInfos[0].ParameterType, _types.Resolve(ma.Target.Type), cecilTypeArgs); }
                    catch { /* receiver not resolvable to a closed instance — fall back to arg inference */ }
                }
                for (var i = 0; i < args.Count && i + 1 < paramInfos.Length; i++)
                {
                    var pt = paramInfos[i + 1].ParameterType;
                    if (pt.IsGenericMethodParameter && cecilTypeArgs[pt.GenericParameterPosition] is null)
                        cecilTypeArgs[pt.GenericParameterPosition] = _types.Resolve(args[i].Type);
                }
            }
        }
        catch { return false; }
        if (cecilTypeArgs.Any(t => t is null)) return false;
        // Take over from the reflection path when a type argument can't survive the erasing
        // runtime-Type round-trip: a module (user-defined) type, OR a generic parameter
        // (the enclosing type's own — `ch.Reader.ReadAllAsync(ct).ToBlockingEnumerable(ct)`
        // inside `Chan<T>` closes the extension's T to `!0`). Pure-closed-BCL generic
        // extensions (LINQ `Where`/`Select` over `List<int>`) resolve fine the normal way.
        if (!cecilTypeArgs.Any(a => MentionsModuleType(a!, _types.Module) || a!.ContainsGenericParameter)) return false;

        var gim = new Mono.Cecil.GenericInstanceMethod(_types.Module.ImportReference(openInfo));
        foreach (var a in cecilTypeArgs) gim.GenericArguments.Add(a);

        // The receiver is the static extension's first parameter; coerce it and the rest
        // against the CLOSED signature so a lambda arg materializes as the closed `Func<…>`.
        var withReceiver = new List<BoundExpression>(args.Count + 1) { ma.Target };
        withReceiver.AddRange(args);
        EmitCallArgsCoerced(withReceiver, gim);
        _il.Call(gim);
        if (gim.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
        return true;
    }

    // Whether a generic argument would be erased to `object` by the runtime
    // (System.Type) resolution path, so the closed Cecil instance must host the
    // member instead. True for user (module) types AND for `ValueTuple<…>` (the
    // runtime path can't parse the `(a, b)` tuple syntax and falls back to
    // `object`). Recurses through nested generic arguments.
    static bool MentionsModuleType(TypeReference tr, ModuleDefinition module)
    {
        // A bare generic parameter (`!0`) — e.g. `List<T>` inside a generic `Seq<T>` —
        // is erased to `object` by the runtime-reflection path, so the call must be
        // hosted on the closed Cecil instance (`List<!0>`) instead.
        if (tr is GenericParameter) return true;
        if (tr is GenericInstanceType g)
            return g.ElementType.Name.StartsWith("ValueTuple", StringComparison.Ordinal)
                || MentionsModuleType(g.ElementType, module)   // the element itself (e.g. user `Box`1`)
                || g.GenericArguments.Any(a => MentionsModuleType(a, module));
        var def = tr as TypeDefinition ?? tr.Resolve();
        return def is not null && ReferenceEquals(def.Module, module);
    }

    // Like HostMethodOnType, but imports the open method's parameter/return types
    // into the current module. Required when the open definition lives in another
    // module (e.g. corelib `List<T>`): a method such as `AddRange(IEnumerable<!0>)`
    // references a cross-module type that Cecil rejects at write time unless
    // imported. Bare generic-parameter references (`!0`) survive import unchanged.
    Mono.Cecil.MethodReference HostExternalMethodOnType(Mono.Cecil.MethodDefinition method, GenericInstanceType declaringType)
    {
        // The parameter/return signatures must keep their generic-parameter
        // references (`!0`) — the closed declaring instance (`List<Pt>`) is what
        // binds them at the call site. We only need to IMPORT the cross-module
        // *element* types of any constructed generic argument
        // (`AddRange(IEnumerable<!0>)` → import `IEnumerable<>`, keep `!0`), since
        // those would otherwise be "declared in another module" at write time.
        var rehosted = new Mono.Cecil.MethodReference(method.Name, ImportConstructedKeepingParams(method.ReturnType), declaringType)
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention,
        };
        foreach (var p in method.Parameters)
        {
            var np = new ParameterDefinition(p.Name, p.Attributes, ImportConstructedKeepingParams(p.ParameterType));
            // Preserve the optional default so a call that omits a trailing optional arg
            // (e.g. `ChannelWriter<T>.TryComplete()` over `TryComplete(Exception = null)`)
            // can fill it — the Attributes carry the Optional flag, but the Constant is a
            // separate field EmitOptionalArgument reads.
            if (p.HasConstant) np.Constant = p.Constant;
            rehosted.Parameters.Add(np);
        }
        foreach (var gp in method.GenericParameters)
            rehosted.GenericParameters.Add(new GenericParameter(gp.Name, rehosted));
        return rehosted;
    }

    // Import the cross-module *constructed* parts of `t` while leaving bare
    // generic parameters (`!0`, `!!0`) untouched, so the resulting signature still
    // resolves against the declaring generic instance. ImportReference on a free
    // generic parameter throws, hence the structural recursion.
    TypeReference ImportConstructedKeepingParams(TypeReference t)
    {
        switch (t)
        {
            case GenericParameter:
                return t;
            case GenericInstanceType git:
            {
                var closed = new GenericInstanceType(_types.Module.ImportReference(git.ElementType));
                foreach (var a in git.GenericArguments)
                    closed.GenericArguments.Add(ImportConstructedKeepingParams(a));
                return closed;
            }
            case ArrayType at:
                return new ArrayType(ImportConstructedKeepingParams(at.ElementType), at.Rank);
            case ByReferenceType brt:
                return new ByReferenceType(ImportConstructedKeepingParams(brt.ElementType));
            default:
                return _types.Module.ImportReference(t);
        }
    }

    static Mono.Cecil.MethodReference HostMethodOnType(Mono.Cecil.MethodDefinition method, GenericInstanceType declaringType)
    {
        var rehosted = new Mono.Cecil.MethodReference(method.Name, method.ReturnType, declaringType)
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention,
        };
        foreach (var p in method.Parameters)
            rehosted.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        foreach (var gp in method.GenericParameters)
            rehosted.GenericParameters.Add(new GenericParameter(gp.Name, rehosted));
        return rehosted;
    }

    // ─── Async builder generic method emission ────────────────────────────────
    //
    // The async builder types (AsyncValueTaskMethodBuilder<T>, AsyncTaskMethodBuilder<T>,
    // AsyncVoidMethodBuilder) expose two generic methods that the reflection path cannot
    // close with a Cecil-only type argument:
    //
    //   Start<TStateMachine>(ref TStateMachine stateMachine)
    //   AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter a, ref TStateMachine sm)
    //   AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter a, ref TStateMachine sm)
    //
    // The state-machine struct is a module-local `TypeDefinition` — it has no `System.Type`
    // counterpart, so `MethodInfo.MakeGenericMethod(smType)` throws and the call is "not found".
    //
    // The fix mirrors the ESC oracle (ILAsyncEmitter.cs `ResolveBuilderStart`,
    // ILAsyncMethodEmitter.cs `EmitAwaitUnsafeOnCompleted`): find the open method via
    // reflection, import it into Cecil, set the declaring type to the CLOSED builder
    // instance when the builder is generic (e.g. `AsyncValueTaskMethodBuilder<int>`),
    // then add the Cecil type arguments as `GenericArguments` on a `GenericInstanceMethod`.
    //
    // `SetResult`, `SetException`, and `SetStateMachine` are non-generic so the ordinary
    // reflection path handles them — but they still need the builder's declaring type to
    // be the closed generic instance, which the standard `ResolveExternalMethod` already
    // does for us (the receiver's `BoundTypeToRuntime` returns the closed type). They are
    // listed in `AsyncBuilderGenericMethods` only as a fast-path sentinel; non-generic
    // builder calls fall through to the standard path.
    //
    // Called from EmitMemberCall immediately before the reflection fallback, so the builder
    // receiver is already known to have a resolvable runtime type (the builder itself is
    // a BCL type — it's the SM type-argument that is Cecil-only).

    // Names of the five async-builder instance methods that require special handling.
    // The generic set is the precise failure surface; SetResult / SetException /
    // SetStateMachine work fine via the reflection path once the receiver type resolves,
    // but are listed here so the fast gate can skip the full resolver for all builder
    // methods in one branch.
    static readonly System.Collections.Generic.HashSet<string> AsyncBuilderGenericMethods =
        new(StringComparer.Ordinal) { "Start", "AwaitUnsafeOnCompleted", "AwaitOnCompleted" };

    // The four well-known async builder base names (no arity suffix).  A receiver whose
    // runtime type's name (without the "`1") matches one of these is an async builder.
    static bool IsAsyncBuilderType(Type t)
    {
        var name = t.IsGenericType ? t.GetGenericTypeDefinition().Name : t.Name;
        // Strip arity suffix (`AsyncValueTaskMethodBuilder`1` → `AsyncValueTaskMethodBuilder`)
        var backtick = name.IndexOf('`');
        if (backtick >= 0) name = name[..backtick];
        return name is "AsyncValueTaskMethodBuilder"
            or "AsyncTaskMethodBuilder"
            or "AsyncVoidMethodBuilder";
    }

    static bool IsAsyncBuilderBoundType(BoundType type) => type is ExternalType ext
        && (ext.Name.StartsWith("AsyncValueTaskMethodBuilder", StringComparison.Ordinal)
            || ext.Name.StartsWith("AsyncTaskMethodBuilder", StringComparison.Ordinal)
            || ext.Name.StartsWith("AsyncVoidMethodBuilder", StringComparison.Ordinal));

    /// <summary>
    /// Emits a call to one of the async builder's generic instance methods
    /// (<c>Start&lt;SM&gt;</c>, <c>AwaitUnsafeOnCompleted&lt;TAwaiter,SM&gt;</c>,
    /// <c>AwaitOnCompleted&lt;TAwaiter,SM&gt;</c>) using Mono.Cecil generic-instance
    /// method construction so the module-local SM struct survives as a Cecil
    /// <see cref="TypeReference"/> rather than being erased to <c>object</c> by
    /// <c>MethodInfo.MakeGenericMethod</c>.
    ///
    /// Mirrors the ESC oracle: <c>ILAsyncEmitter.cs</c> <c>ResolveBuilderStart</c>
    /// and <c>ILAsyncMethodEmitter.cs</c> <c>EmitAwaitUnsafeOnCompleted</c>.
    ///
    /// Returns <c>false</c> when the method is not a generic builder method or when
    /// the receiver is not an async builder, letting the caller fall through to the
    /// standard reflection path (which handles non-generic builder calls correctly).
    /// </summary>
    bool TryEmitAsyncBuilderCall(
        Type builderRuntimeType,
        BoundMemberAccessExpression ma,
        IReadOnlyList<BoundExpression> args,
        IReadOnlyList<BoundType>? explicitTypeArgs)
    {
        // Only intercept the generic builder methods; non-generic ones (SetResult,
        // SetException, SetStateMachine) resolve fine via the standard reflection path.
        if (!AsyncBuilderGenericMethods.Contains(ma.MemberName)) return false;
        if (!IsAsyncBuilderType(builderRuntimeType)) return false;

        // AsyncLowering always supplies the exact builder-method type arguments.
        // Emit every such call through Cecil: the state-machine type is metadata-only,
        // even when a shallow runtime probe happens to make it look representable.
        if (explicitTypeArgs is not { Count: > 0 }) return false;
        var cecilTypeArgs = new TypeReference[explicitTypeArgs.Count];
        for (var i = 0; i < explicitTypeArgs.Count; i++)
            cecilTypeArgs[i] = _types.Resolve(explicitTypeArgs[i]);

        // The final generic argument of Start<TStateMachine> and
        // Await*OnCompleted<TAwaiter,TStateMachine> is passed as `ref this`.
        // Take its type from this exact emitted method's declaring type rather
        // than re-resolving the bound spelling: generic-parameter identity is
        // owner-sensitive in metadata, and a same-named `T` from the bound model
        // is not necessarily the `T` owned by the generated state-machine TypeDef.
        if (args.LastOrDefault() is BoundOutArgumentExpression { Name: var name }
            && IsSelfReference(name))
        {
            var currentType = _method.DeclaringType;
            if (currentType is not null)
            {
                if (currentType.HasGenericParameters)
                {
                    var closedCurrent = new GenericInstanceType(_types.Module.ImportReference(currentType));
                    foreach (var parameter in currentType.GenericParameters)
                        closedCurrent.GenericArguments.Add(parameter);
                    cecilTypeArgs[^1] = closedCurrent;
                }
                else
                {
                    cecilTypeArgs[^1] = currentType;
                }
            }
        }

        // Find the open generic method definition via reflection.  Crucially: always
        // look on the OPEN generic type definition (e.g. `AsyncValueTaskMethodBuilder`1`)
        // rather than on the closed form (`AsyncValueTaskMethodBuilder<int>`), matching
        // the ESC oracle pattern in ILAsyncEmitter.ResolveBuilderStart and
        // ILAsyncMethodEmitter.EmitAwaitUnsafeOnCompleted.  The open form's method
        // infos have `!!0`/`!!1`-typed parameters that ImportReference preserves as-is;
        // importing from the closed form may yield substituted signatures that confuse
        // Cecil's generic-parameter bookkeeping.
        var openBuilderReflType = builderRuntimeType.IsGenericType
            ? builderRuntimeType.GetGenericTypeDefinition()
            : builderRuntimeType;
        var openInfo = openBuilderReflType
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == ma.MemberName
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == explicitTypeArgs.Count
                && m.GetParameters().Length == args.Count);
        if (openInfo is null) return false;

        // Import the open method reference into the current module. After import the
        // declaring type on the reference is the OPEN builder type; for a generic builder
        // (`AsyncValueTaskMethodBuilder`1`) it must be re-hosted onto the CLOSED Cecil
        // declaring instance so the declaring-class type parameter (`!0` → `int`) is
        // bound in the method token.  This is identical to the ESC oracle pattern:
        //
        //   imported.DeclaringType = new GenericInstanceType(module.ImportReference(openType))
        //       { GenericArguments = { innerType } };
        //
        // The closed declaring type supplies the T-substitution for the class's own
        // generic parameters (the `!0` in `Task`), which is distinct from the method's
        // own parameters (the `!!0`/`!!1` for SM / awaiter).
        var imported = _types.Module.ImportReference(openInfo);
        if (_types.Resolve(ma.Target.Type) is GenericInstanceType cecilBuilder)
        {
            imported.DeclaringType = cecilBuilder;
        }
        else if (builderRuntimeType.IsGenericType)
        {
            // Build the closed declaring type from the Cecil open ref + the RUNTIME
            // generic args (primitive types like `int`, `string`) so the declaration
            // token matches the actual closed builder instance in the field.
            var openBuilderCecilRef = _types.Module.ImportReference(openBuilderReflType);
            var closedDeclaringType = new GenericInstanceType(openBuilderCecilRef);
            foreach (var rtArg in builderRuntimeType.GetGenericArguments())
                closedDeclaringType.GenericArguments.Add(_types.Module.ImportReference(rtArg));
            imported.DeclaringType = closedDeclaringType;
        }

        // Build the closed generic method via Cecil — `MakeGenericMethod` is intentionally
        // bypassed here because the SM struct is a `TypeDefinition` in the current module
        // with no `System.Type` counterpart. `GenericInstanceMethod` + `GenericArguments.Add`
        // keeps everything in Cecil's own representation, which the writer can tokenize.
        var gim = new Mono.Cecil.GenericInstanceMethod(imported);
        foreach (var cecilArg in cecilTypeArgs)
            gim.GenericArguments.Add(cecilArg);

        // Emit the receiver address (builder is a value type, so ldflda, not ldobj).
        // `EmitAddress` calls `ldflda` for a member-access receiver and `ldloca` for a
        // local — both produce the managed pointer that the builder's instance methods
        // expect.  The receiver was already confirmed to be a value type (all builders
        // are structs).
        EmitAddress(ma.Target);
        // Emit the args (ref awaiter → ldflda/ldloca via BoundOutArgumentExpression,
        // ref sm → ldarg.0 / ldloca via BoundOutArgumentExpression with "this").
        EmitCallArgsCoerced(args, gim);
        _il.Call(gim);
        _lastCallWasVoid = true; // Start / AwaitUnsafeOnCompleted / AwaitOnCompleted all return void
        return true;
    }

    void EmitMemberCall(BoundMemberAccessExpression ma, IReadOnlyList<BoundExpression> args, IReadOnlyList<BoundType>? explicitTypeArgs = null)
    {
        // Resolve explicit type args to runtime Types up front if provided
        Type[]? explicitRuntimeTypeArgs = null;
        if (explicitTypeArgs is { Count: > 0 })
        {
            explicitRuntimeTypeArgs = new Type[explicitTypeArgs.Count];
            for (var i = 0; i < explicitTypeArgs.Count; i++)
                explicitRuntimeTypeArgs[i] = _types.BoundTypeToRuntime(explicitTypeArgs[i]) ?? typeof(object);
        }

        // 0. Generic value-choice factory: `Option<int>.some(99)`. The receiver is
        //    a closed `ChoiceType` carrying type arguments; resolve the reified
        //    `Option`1<int>` and host its factory method on the closed instance so
        //    the payload type stays precise.
        if (ma.Target.Type is Esharp.BoundTree.ChoiceType { IsRef: false } closedChoice
            && closedChoice.TypeArgs.Count > 0
            && _types.Resolve(closedChoice) is GenericInstanceType choiceGit)
        {
            var def = choiceGit.Resolve();
            var factory = def?.Methods.FirstOrDefault(m =>
                m.IsStatic && m.Name == ma.MemberName && m.Parameters.Count == args.Count);
            if (factory is not null)
            {
                var factoryRef = HostExternalMethodOnType(factory, choiceGit);
                EmitCallArgsCoerced(args, factoryRef);
                _il.Call(factoryRef);
                return;
            }
        }

        // 1. Check if it's a factory call on a type defined in this module (choice/data/enum)
        if (ma.Target is BoundNameExpression typeName)
        {
            foreach (var type in _types.Module.Types)
            {
                if (type.Name == typeName.Name)
                {
                    // Enum: load the constant directly
                    if (type.IsEnum)
                    {
                        var enumField = type.Fields.FirstOrDefault(f => f.IsStatic && f.IsLiteral && f.Name == ma.MemberName);
                        if (enumField?.Constant is int val)
                        {
                            _il.LoadInt(val);
                            return;
                        }
                    }

                    var factory = type.Methods.FirstOrDefault(m => m.Name == ma.MemberName && m.IsStatic);
                    if (factory is not null)
                    {
                        // A generic static-func member (`Ops.identity<int>(7)`) must be
                        // closed over its explicit type args — calling the open method
                        // is invalid IL. Mirrors the bare-name path.
                        MethodReference target = factory;
                        if (explicitTypeArgs is { Count: > 0 } && factory.HasGenericParameters)
                        {
                            var gim = new Mono.Cecil.GenericInstanceMethod(factory);
                            foreach (var t in explicitTypeArgs)
                                gim.GenericArguments.Add(_types.Resolve(t));
                            target = gim;
                        }
                        EmitCallArgsCoerced(args, target);
                        _il.Call(target);
                        return;
                    }
                }
            }
        }

        // 1b. Namespace-qualified free-function call: `Lib.Inner.makeNums(a, b)`. A
        //     cross-namespace free function is a STATIC member of its namespace's host
        //     class (`namespace A.B` → type `A.B.B`); it does NOT promote to an instance
        //     method across namespaces (promotion is same-namespace only for both value-
        //     and `*T`-receivers). The qualifier is a namespace path, not a BCL/CLR type, so resolve
        //     the member as a static method on that namespace's host class. The path comes
        //     in two shapes: a single dotted name (`Lib.Inner`, from a binder-qualified bare
        //     call) or nested member access (`(Lib.Inner)` from an explicitly-written
        //     `Lib.Inner.fn()`); `NamespacePathOf` flattens both, and yields null for a real
        //     receiver chain (`obj.field`) so ordinary member calls fall through untouched.
        if (NamespacePathOf(ma.Target) is { } nsPath
            && _types.TryResolveRuntimeType(nsPath) is null)
        {
            var hostFn = _types.Module.Types
                .Where(t => t.Namespace == nsPath)
                .SelectMany(t => t.Methods)
                .FirstOrDefault(m => m.IsStatic && m.Name == ma.MemberName && m.Parameters.Count == args.Count);
            if (hostFn is not null)
            {
                MethodReference target = hostFn;
                // Close a generic free function over its explicit type args (`Util.id<int>(x)`).
                if (explicitTypeArgs is { Count: > 0 } && hostFn.HasGenericParameters)
                {
                    var gim = new Mono.Cecil.GenericInstanceMethod(hostFn);
                    foreach (var t in explicitTypeArgs) gim.GenericArguments.Add(_types.Resolve(t));
                    target = gim;
                }
                EmitCallArgsCoerced(args, target);
                _il.Call(target);
                if (target.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                return;
            }
            // No static host method on this namespace: there is intentionally no promoted-
            // instance fallback. Promotion is namespace-local (a function attaches to a type
            // only in that type's namespace), so a method always lives on the namespace host
            // class as a static here, or on its receiver type as `recv.m(...)` — never reached
            // by qualifying it as a free function. `NS.fn(recv)` of a same-namespace promoted
            // method is the binder's ES2142; falling through here would resurrect that misuse.
        }

        // 2a. C#-sourced static method call: CsTypeName.Method(args).
        //     Resolved through the type handle so Cecil doesn't try to .Resolve()
        //     against the not-yet-loadable C# half assembly. Same as 2 below but
        //     scoped to .cs types declared in the project.
        if (ma.Target is BoundNameExpression { Type: Esharp.BoundTree.ExternalCSharpType csStaticType })
        {
            var member = FindCSharpMethod(csStaticType.Handle.Members, ma.MemberName, args.Count, wantStatic: true);
            if (member is not null)
            {
                var methodRef = BuildCSharpMethodReference(csStaticType, ma.MemberName, member);
                foreach (var arg in args) EmitExpression(arg);
                EmitCSharpOptionalDefaults(member.Parameters, args.Count);   // fill omitted trailing optionals
                _il.Call(methodRef);
                if (member.ReturnType is Esharp.BoundTree.VoidType) _lastCallWasVoid = true;
                return;
            }
        }

        // 2. External static method call: TypeName.Method(args)
        //    e.g., Console.WriteLine(x), Math.Max(a, b), Guid.NewGuid()
        if (ma.Target is BoundNameExpression staticTypeName)
        {
            // `Async*MethodBuilder<T>.Create()` must retain a Cecil-only `T`.
            if (IsAsyncBuilderBoundType(staticTypeName.Type)
                && _types.Resolve(staticTypeName.Type) is GenericInstanceType closedBuilder)
            {
                var create = closedBuilder.Resolve()?.Methods.FirstOrDefault(m =>
                    m.IsStatic && m.Name == ma.MemberName && m.Parameters.Count == args.Count);
                if (create is not null)
                {
                    var createRef = HostExternalMethodOnType(create, closedBuilder);
                    EmitCallArgsCoerced(args, createRef);
                    _il.Call(createRef);
                    return;
                }
            }
            var runtimeType = _types.TryResolveRuntimeType(staticTypeName.Name);
            if (runtimeType is not null)
            {
                // Async builder static calls (`AsyncValueTaskMethodBuilder.Create()`): the
                // builder type name is always the bare non-generic form (`AsyncValueTaskMethodBuilder`
                // not `AsyncValueTaskMethodBuilder\`1`), so `TryResolveRuntimeType` returns the
                // arity-0 void builder even when the call is on the generic `AsyncValueTaskMethodBuilder<T>`.
                // Correct by upgrading to the full closed runtime type from the BOUND target type —
                // `BoundTypeToRuntime(ma.Target.Type)` resolves the generic builder form properly.
                // This affects `Create()` whose return type must match the field (`AsyncValueTaskMethodBuilder<T>`
                // vs `AsyncValueTaskMethodBuilder`) — without the upgrade the stored value has wrong
                // type and subsequent `Start<SM>` / `Task` property access emits invalid IL.
                if (IsAsyncBuilderType(runtimeType)
                    && _types.BoundTypeToRuntime(ma.Target.Type) is { } closedBuilderRt
                    && closedBuilderRt != runtimeType)
                {
                    runtimeType = closedBuilderRt;
                }

                // Generic static call whose explicit type arg is a module type / type
                // parameter (`Channel.CreateUnbounded<T>()` inside `Ch<T>`): close it via
                // Cecil so `T` survives as `!0` instead of being erased to `object` by the
                // reflection path (which can't represent a generic parameter).
                if (explicitTypeArgs is { Count: > 0 }
                    && TryEmitExternalStaticGenericCall(runtimeType, ma, args, explicitTypeArgs))
                    return;

                // Resolve first (before emitting args) so we can adapt to params overloads + boxing.
                var argTypesDryRun = CollectCallArgTypes(args);
                var methodRef = _types.ResolveExternalMethod(runtimeType, ma.MemberName, args.Count, argTypesDryRun, silent: true, explicitTypeArgs: explicitRuntimeTypeArgs);

                if (methodRef is not null)
                {
                    EmitCallArgsCoerced(args, methodRef);
                    _il.Call(methodRef);
                    if (methodRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }

                // Params overload fallback.
                var paramsMatch = _types.ResolveParamsMethod(runtimeType, ma.MemberName, argTypesDryRun);
                if (paramsMatch is not null)
                {
                    EmitCallArgsWithParams(args, paramsMatch.Value.fixedCount, paramsMatch.Value.elementType);
                    _il.Call(paramsMatch.Value.method);
                    if (paramsMatch.Value.method.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }

                // Final fallback: no-arg-type match (handles boxing like Console.WriteLine(object))
                var fallbackRef = _types.ResolveExternalMethod(runtimeType, ma.MemberName, args.Count, explicitTypeArgs: explicitRuntimeTypeArgs);
                if (fallbackRef is not null)
                {
                    EmitCallArgsCoerced(args, fallbackRef);
                    _il.Call(fallbackRef);
                    if (fallbackRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }
            }
        }

        // 3. Instance method call: expr.Method(args)
        {
            // A ref local produced by `let location = &property` transparently
            // exposes the pointee's method set. Keep the bound target expression
            // intact (its slot knows how to load/address through the byref), but
            // resolve dispatch against the pointee type.
            var targetType = ma.Target.Type is ByRefBoundType byRefTarget
                ? byRefTarget.Inner
                : ma.Target.Type;

            // 3a. C#-sourced instance method call: csValue.Method(args).
            //     Same reasoning as 2a — go through the type handle's member surface
            //     and build a Cecil MethodReference scoped to the C# half assembly.
            if (targetType is Esharp.BoundTree.ExternalCSharpType csInstance)
            {
                var member = FindCSharpMethod(csInstance.Handle.Members, ma.MemberName, args.Count, wantStatic: false);
                // Walk base class chain — C# methods can be inherited.
                if (member is null)
                {
                    var cursor = csInstance.Handle.BaseType;
                    while (cursor is not null && member is null)
                    {
                        member = FindCSharpMethod(cursor.Members, ma.MemberName, args.Count, wantStatic: false);
                        cursor = cursor.BaseType;
                    }
                }
                if (member is not null)
                {
                    var methodRef = BuildCSharpMethodReference(csInstance, ma.MemberName, member);
                    EmitExpression(ma.Target);
                    foreach (var arg in args) EmitExpression(arg);
                    EmitCSharpOptionalDefaults(member.Parameters, args.Count);   // fill omitted trailing optionals
                    // Class methods through virtual dispatch by default — covers
                    // virtual/override and interface impls automatically. Non-virtual
                    // methods on classes work the same way.
                    { if (csInstance.Handle.IsRefType) _il.CallVirt(methodRef); else _il.Call(methodRef); }

                    if (member.ReturnType is Esharp.BoundTree.VoidType) _lastCallWasVoid = true;
                    return;
                }
            }

            // Function pointer field call: target.fpField(args) → ldfld + calli
            if (targetType is DataType dt)
            {
                var typeDef = _types.TryResolveRegistered(dt.Name);
                if (typeDef is not null)
                {
                    var fpField = typeDef.Fields.FirstOrDefault(f => f.Name == ma.MemberName && f.FieldType is Mono.Cecil.FunctionPointerType);
                    if (fpField is not null)
                    {
                        var fp = (Mono.Cecil.FunctionPointerType)fpField.FieldType;
                        // Push args, then load the function pointer from the field, then calli
                        foreach (var arg in args)
                            EmitSingleCallArg(arg);
                        if (_types.IsValueType(targetType))
                            EmitAddress(ma.Target);
                        else
                            EmitExpression(ma.Target);
                        _il.LoadField(fpField);
                        var callSite = new CallSite(fp.ReturnType)
                        {
                            HasThis = false,
                            CallingConvention = MethodCallingConvention.Default,
                        };
                        foreach (var p in fp.Parameters)
                            callSite.Parameters.Add(new ParameterDefinition(p.ParameterType));
                        _il.CallIndirect(callSite, args.Count);
                        if (fp.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                        return;
                    }
                }
            }

            // Protocol-typed target: resolve method on the Cecil interface definition
            if (targetType is InterfaceType proto)
            {
                var ifaceDef = _types.TryResolveRegistered(proto.Name);
                if (ifaceDef is not null)
                {
                    var ifaceMethod = ifaceDef.Methods.FirstOrDefault(m => m.Name == ma.MemberName && m.Parameters.Count == args.Count);
                    if (ifaceMethod is not null)
                    {
                        MethodReference callTarget = ifaceMethod;
                        // Closed generic interface receiver (`b: IBox<int>`): re-home the
                        // method onto the closed `IBox<int>` so the CLR substitutes the
                        // signature's type params (`!0` → int) — both the `this`/receiver
                        // type and the return type verify against the closed instance.
                        if (proto.TypeArgs.Count > 0 && _types.Resolve(proto) is GenericInstanceType closedProto)
                        {
                            var rehomed = new MethodReference(ifaceMethod.Name, ifaceMethod.ReturnType, closedProto)
                            {
                                HasThis = ifaceMethod.HasThis,
                            };
                            foreach (var p in ifaceMethod.Parameters)
                                rehomed.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                            callTarget = _types.Module.ImportReference(rehomed);
                        }
                        EmitExpression(ma.Target);
                        foreach (var arg in args)
                            EmitExpression(arg);
                        _il.CallVirt(callTarget);
                        if (callTarget.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                        return;
                    }
                }
            }

            // *T heap-pointer receiver calling a value-receiver method (promoted onto
            // the inner type, emitted as an INSTANCE method on it): deref the wrapper to
            // a managed pointer and call. The receiver is wrapper-represented either by
            // its binder type (HeapPointerBoundType) OR by its slot — a `*T` foreach
            // element / async SM field is stored as `__Ptr_T` even when the binder typed
            // it `ref T` (a managed pointer can't be a heap field). Pointer-receiver
            // *free* functions are static, never found here, so they fall through to the
            // static-dispatch path below; only inner instance methods match.
            {
                var wrapperRef = targetType is HeapPointerBoundType
                    ? _types.Resolve(targetType)
                    : TryGetReceiverWrapperType(ma.Target);
                if (wrapperRef is not null)
                {
                    var innerType = ILHeapPointer.GetValueField(_types.Module, wrapperRef).FieldType;
                    var typeDef = innerType.Resolve();
                    var method = typeDef?.Methods.FirstOrDefault(m =>
                        !m.IsStatic && !m.IsConstructor && m.Name == ma.MemberName && m.Parameters.Count == args.Count);
                    if (method is not null)
                    {
                        // Load wrapper ref → ldflda Value → call on managed pointer
                        EmitHeapPointerCarrier(ma.Target);
                        ILHeapPointer.EmitDerefToAddress(_il, _types.Module, wrapperRef);
                        foreach (var arg in args)
                            EmitSingleCallArg(arg);
                        _il.Call(method);
                        if (method.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                        return;
                    }
                }
            }

            // Pointer-receiver method call `p.m(args)` / `c.m(args)`: the method emits as a
            // static host `m(*T, args)` (Binder Pass 3), so the call lowers to `m(receiver, args)`
            // with the receiver as the leading `*T` argument. Tried after the value-method-via-
            // pointer path above (those have an inner instance method) and before ordinary
            // instance resolution (value receivers have an instance method, never a static host).
            if (TryEmitPointerReceiverStaticCall(ma, args)) return;

            bool isValueType = _types.IsValueType(targetType);

            // Non-generic async-builder members (`SetResult`, `SetException`,
            // `Task`) must be hosted on Async*MethodBuilder<T> too. Reflection
            // sees only Async*MethodBuilder<object> when T is a Cecil parameter.
            if (IsAsyncBuilderBoundType(targetType)
                && explicitTypeArgs is not { Count: > 0 }
                && _types.Resolve(targetType) is GenericInstanceType closedBuilder)
            {
                var member = closedBuilder.Resolve()?.Methods.FirstOrDefault(m =>
                    !m.IsStatic && !m.HasGenericParameters && m.Name == ma.MemberName
                    && m.Parameters.Count == args.Count);
                if (member is not null)
                {
                    var memberRef = HostExternalMethodOnType(member, closedBuilder);
                    EmitAddress(ma.Target);
                    EmitCallArgsCoerced(args, memberRef);
                    _il.Call(memberRef);
                    if (memberRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }
            }

            // ChanType-typed receiver with a user-defined element type: the
            // default BoundTypeToRuntime path can't MakeGenericType with a
            // Cecil-only type, so we route through Esharp.Stdlib.ChanOps.<Op>
            // which is a generic static method (Cecil handles generic method
            // instantiation cleanly). Primitive-element chans go through the
            // normal runtime-type path below.
            if (targetType is ChanType chanTy && _types.BoundTypeToRuntime(chanTy.ElementType) is null)
            {
                var closedRef = _types.Resolve(chanTy);
                if (TryEmitChanInstanceCall(chanTy, closedRef, ma, args))
                    return;
            }

            // Generic-instance receiver carrying a user-defined (module) type arg
            // — e.g. `List<Pt>`. BoundTypeToRuntime erases the arg to `object`, so
            // resolving the method against the runtime type would emit
            // `List<object>::Add(object)` against a `List<Pt>` receiver. Resolve the
            // method on the CLOSED Cecil instance instead, hosting the open
            // definition's `!0`-typed signature on it.
            if (!IsAsyncBuilderBoundType(targetType)
                && TryEmitModuleGenericInstanceCall(targetType, ma, args, isValueType))
                return;

            // Try to resolve the method on the target's runtime type first so we know the
            // parameter types for boxing/coercion.
            var instanceRuntimeType = _types.BoundTypeToRuntime(targetType);
            if (instanceRuntimeType is not null)
            {
                // Async builder generic methods (`Start<SM>`, `AwaitUnsafeOnCompleted<TAwaiter,SM>`,
                // `AwaitOnCompleted<TAwaiter,SM>`): the state-machine struct is a module-local
                // Cecil TypeDefinition — it has no System.Type, so `MakeGenericMethod` cannot
                // form the closed call and ResolveExternalMethod reports "not found".  Intercept
                // here, before the reflection resolver, and build the GenericInstanceMethod via
                // Cecil directly (see TryEmitAsyncBuilderCall for the full rationale and the ESC
                // oracle pattern it mirrors).
                if (TryEmitAsyncBuilderCall(instanceRuntimeType, ma, args, explicitTypeArgs))
                    return;

                var argTypes = CollectCallArgTypes(args);
                var methodRef = _types.ResolveExternalMethod(instanceRuntimeType, ma.MemberName, args.Count, argTypes, silent: true, explicitTypeArgs: explicitRuntimeTypeArgs);
                methodRef ??= _types.ResolveExternalMethod(instanceRuntimeType, ma.MemberName, args.Count, silent: true, explicitTypeArgs: explicitRuntimeTypeArgs);
                if (methodRef is not null)
                {
                    // Reflection resolves a member on a generic BCL receiver through a
                    // runtime stand-in (for example ValueTask<object>). Importing that
                    // method directly leaves its `!0` in the return/parameter signature
                    // open, even though the E# receiver is already closed (such as
                    // ValueTask<bool>). Re-host the resolved definition on the exact Cecil
                    // receiver so `AsTask()` emits Task<bool>, not Task<!0>. This is needed
                    // for ordinary BCL generics too, not only module-type arguments.
                    if (_types.Resolve(targetType) is GenericInstanceType closedReceiver
                        && methodRef.Resolve() is { } methodDefinition
                        && methodDefinition.DeclaringType.FullName == closedReceiver.ElementType.FullName)
                    {
                        methodRef = HostExternalMethodOnType(methodDefinition, closedReceiver);
                    }
                    // ResolveExternalMethod already returns a GenericInstanceMethod
                    // for an explicit runtime-closed call such as
                    // `scope.Chan<int>(1)`.  Wrapping that MethodSpec again creates
                    // an unencodable MethodSpec-of-MethodSpec.  Only infer/close an
                    // actually open method here (the Result.Map lambda case).
                    if (methodRef is not GenericInstanceMethod
                        && methodRef.Resolve() is { HasGenericParameters: true } genericMethod)
                        methodRef = CloseInstanceMethodGenerics(methodRef, genericMethod, args, explicitTypeArgs);
                    // Reflection can resolve an enum's inherited ToString on
                    // System.Enum. That is a reference receiver even though the
                    // source target is a value, so `ldloca; call Enum::ToString`
                    // is unverifiable. Dispatch through a boxed receiver exactly
                    // when the resolved owner is reference-shaped.
                    var boxValueReceiver = isValueType
                        && methodRef.DeclaringType.Resolve() is { IsValueType: false };
                    if (boxValueReceiver)
                    {
                        EmitExpression(ma.Target);
                        _il.Box(_types.Resolve(targetType));
                    }
                    else if (isValueType)
                        EmitAddress(ma.Target);
                    else
                        EmitExpression(ma.Target);
                    EmitCallArgsCoerced(args, methodRef);
                    { if (boxValueReceiver || !isValueType) _il.CallVirt(methodRef); else _il.Call(methodRef); }

                    if (methodRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }

                // Params fallback
                var paramsMatch = _types.ResolveParamsMethod(instanceRuntimeType, ma.MemberName, argTypes);
                if (paramsMatch is not null)
                {
                    if (isValueType)
                        EmitAddress(ma.Target);
                    else
                        EmitExpression(ma.Target);
                    EmitCallArgsWithParams(args, paramsMatch.Value.fixedCount, paramsMatch.Value.elementType);
                    { if (isValueType) _il.Call(paramsMatch.Value.method); else _il.CallVirt(paramsMatch.Value.method); }

                    if (paramsMatch.Value.method.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }

                // Generic extension touching a module type (`services.AddSingleton<Foo>()`,
                // `.AddSingleton(seedBatch())`): pick the right overload by shape and close it
                // over Cecil types so the module type isn't erased to `object`. Must precede the
                // reflection path, which would MakeGenericMethod(object) and lose the type / throw.
                if (TryEmitExtensionGenericCall(instanceRuntimeType, ma, args, explicitTypeArgs))
                    return;

                // Fallback: extension method dispatch. Receiver becomes arg[0] of a static call.
                // Explicit type args (`xs.OfType<T>()`) are threaded through — for OfType/Cast
                // they're the only source for the result type parameter.
                var extensionRef = _types.ResolveExtensionMethod(instanceRuntimeType, ma.MemberName, args.Count, argTypes, explicitRuntimeTypeArgs);
                if (extensionRef is not null)
                {
                    // The receiver is the extension's first parameter — an ORDINARY by-value
                    // parameter (`SequenceEqual(this ReadOnlySpan<T>, …)`), not an instance
                    // `this`. Emit it by value; take its address only when the parameter is
                    // genuinely by-ref (`this ref`/`this in`). Then apply the same span
                    // coercion the value args get, so a `Span<T>` receiver flows into a
                    // `ReadOnlySpan<T>` `this` (`op_Implicit`).
                    var recvParam = EffectiveParameterType(extensionRef, 0);
                    if (recvParam.IsByReference)
                        EmitAddress(ma.Target);
                    else
                    {
                        EmitExpression(ma.Target);
                        TryEmitSpanArg(ma.Target, recvParam);
                    }
                    EmitCallArgsCoerced(args, extensionRef, parameterOffset: 1);
                    _il.Call(extensionRef);
                    if (extensionRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }

                _diagnostics.Report("", 0, 0, $"IL: method '{ma.MemberName}' not found on '{instanceRuntimeType.Name}' (argCount={args.Count})");
                return;
            }

            // No runtime type known — try step 4 before emitting anything.

            // 4. Try resolving on the struct/class types defined in this module.
            // Handles both local variables (BoundNameExpression) and member access
            // chains (e.g., e.Vec2.sum() where Vec2 is an embedded struct field).
            {
                TypeDefinition? typeDef = null;
                // The closed receiver reference, kept distinct from its resolved
                // definition: for a generic receiver (`Pair<string,int>`) the
                // method must be referenced on the GenericInstanceType, not the
                // open `Pair` def, or the CLR rejects the call as "not fully
                // instantiated".
                TypeReference? closedTypeRef = null;
                if (ma.Target is BoundNameExpression instanceName)
                {
                    // `self` is the implicit `this` in instance methods — it has
                    // no entry in _slots (CLR loads it via ldarg.0). Resolve to
                    // the enclosing method's declaring type so member-call
                    // resolution works on it like any other receiver.
                    if (IsSelfReference(instanceName.Name))
                    {
                        typeDef = _method.DeclaringType;
                        // Inside a generic type's method `self` is the type closed
                        // over its own parameters; a sibling call (`self.Equals`)
                        // must target `T<A,B>::M`, not the open `T::M`.
                        if (typeDef is { HasGenericParameters: true })
                        {
                            var selfGit = new GenericInstanceType(typeDef);
                            foreach (var gp in typeDef.GenericParameters)
                                selfGit.GenericArguments.Add(gp);
                            closedTypeRef = selfGit;
                        }
                    }
                    else
                        closedTypeRef = TryResolveSlot(instanceName.Name)?.Type;
                }
                else
                {
                    try { closedTypeRef = _types.Resolve(targetType); }
                    catch { /* type might not be resolvable */ }
                }
                typeDef ??= closedTypeRef?.Resolve();

                if (typeDef is not null)
                {
                    // Walk the inheritance chain — own methods first, then base class.
                    // `methodOwner` records which type along the chain actually
                    // declared the resolved method (the receiver itself vs. a base
                    // such as System.Object for inherited GetType/ToString/Equals).
                    Mono.Cecil.MethodDefinition? method = null;
                    TypeDefinition? methodOwner = null;
                    for (var cursor = typeDef; cursor is not null; cursor = cursor.BaseType?.Resolve())
                    {
                        method = cursor.Methods.FirstOrDefault(m =>
                            m.Name == ma.MemberName
                            && m.Parameters.Count == args.Count
                            // Overloads may differ only by generic arity.  An explicit
                            // `<T>` call must never bind the first same-arity non-generic
                            // method (`runChild<T>(work)` was incorrectly emitted as the
                            // Task-returning `runChild(work)`).
                            && (explicitTypeArgs is not { Count: > 0 }
                                || m.GenericParameters.Count == explicitTypeArgs.Count));
                        if (method is not null) { methodOwner = cursor; break; }
                        if (cursor.BaseType is null) break;
                    }
                    if (method is not null)
                    {
                        // Import if the resolved method lives in another module (an
                        // inherited System.Object member resolves into CoreLib) —
                        // otherwise Cecil cannot tokenize the operand at write time.
                        Mono.Cecil.MethodReference methodRef =
                            method.Module != _types.Module ? _types.Module.ImportReference(method) : method;
                        // Re-host the method on the closed generic instance when the
                        // receiver is one, so the CLR dispatches to the instantiated
                        // method (`Pair<string,int>::Equals`), not the open def.
                        if (closedTypeRef is GenericInstanceType git)
                            methodRef = HostMethodOnType(method, git);

                        // Close the method's OWN generic parameters (the receiver's type
                        // args already ride on the declaring instance). Explicit args win
                        // (`b.count<string>(...)` → `Box`1<int>::count<string>`); otherwise
                        // infer them from the bound arg types — a promoted `mapped<U>`
                        // pins `U` from a lambda's body-inferred return. Without either,
                        // the call references the open `!!0` and the verifier rejects it.
                        if (method.HasGenericParameters)
                        {
                            Mono.Cecil.GenericInstanceMethod? gim = null;
                            if (explicitTypeArgs is { Count: > 0 })
                            {
                                gim = new Mono.Cecil.GenericInstanceMethod(methodRef);
                                foreach (var t in explicitTypeArgs)
                                    gim.GenericArguments.Add(_types.Resolve(t));
                            }
                            else if (InferPromotedMethodTypeArgs(method, args) is { } inferredArgs)
                            {
                                gim = new Mono.Cecil.GenericInstanceMethod(methodRef);
                                foreach (var a in inferredArgs)
                                    gim.GenericArguments.Add(a);
                            }
                            if (gim is not null) methodRef = gim;
                        }

                        // A method declared on a reference base (System.Object or
                        // System.Enum) called against a value-type receiver needs a
                        // boxed object reference, not a managed pointer — e.g.
                        // `point.GetType()` or `kind.ToString()`. An external enum may
                        // resolve through its Enum base while still having its concrete
                        // value on the stack, so the owner itself is the reliable fact.
                        var boxValueReceiver = isValueType && methodOwner is not null
                            && !methodOwner.IsValueType;
                        if (boxValueReceiver)
                        {
                            EmitExpression(ma.Target);
                            _il.Box(closedTypeRef ?? _types.Resolve(targetType));
                        }
                        else if (isValueType)
                            EmitAddress(ma.Target);
                        else
                            EmitExpression(ma.Target);
                        EmitCallArgsCoerced(args, methodRef);
                        // Virtual methods (and boxed value receivers reaching an
                        // Object method) dispatch via callvirt; static-bound use call.
                        var useCallvirt = boxValueReceiver || (!isValueType && method.IsVirtual);
                        { if (useCallvirt) _il.CallVirt(methodRef); else _il.Call(methodRef); }

                        if (method.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                        return;
                    }
                }
            }

            // 5. Dot-syntax dispatch to a promoted pointer-receiver static: v.addX(5) →
            //    addX(v, 5), where `addX(v: *T)` joined T's method set. Promotion is
            //    namespace-local, so this dispatches ONLY to a free function declared in the
            //    receiver type's own namespace. A free function in another namespace never
            //    promotes — it has no method form — so `recv.m(...)` of a cross-namespace
            //    function is a hard error pointing at the free spelling `m(recv, ...)`.
            {
                var totalArgs = args.Count + 1; // receiver + args
                var receiverNs = ReceiverDataNamespace(ma.Target.Type);
                MethodDefinition? crossNsCandidate = null;
                foreach (var moduleType in _types.Module.Types)
                {
                    var staticMethod = moduleType.Methods.FirstOrDefault(m =>
                        m.IsStatic && m.Name == ma.MemberName && m.Parameters.Count == totalArgs);
                    if (staticMethod is null) continue;
                    // The function promotes onto the receiver only if it lives in the
                    // receiver type's namespace (its host class's CLR namespace IS that
                    // namespace). A same-name static elsewhere is a free function that never
                    // attached — record it to surface the right calling convention, skip it.
                    if (receiverNs is not null && moduleType.Namespace != receiverNs)
                    {
                        crossNsCandidate ??= staticMethod;
                        continue;
                    }
                    // Receiver is argument 0 — coerce it and the rest against the
                    // static method's parameters so a wrapper receiver flowing into
                    // a downgraded `ref T` self-parameter is unwrapped (and aliases).
                    var withReceiver = new List<BoundExpression>(args.Count + 1) { ma.Target };
                    withReceiver.AddRange(args);
                    EmitCallArgsCoerced(withReceiver, staticMethod);
                    _il.Call(staticMethod);
                    if (staticMethod.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }
                if (crossNsCandidate is not null)
                    _diagnostics.Report("", 0, 0,
                        $"IL: '{ma.MemberName}' is a free function in namespace '{crossNsCandidate.DeclaringType.Namespace}', " +
                        $"not a method on '{ma.Target.Type.EmitName}' — a function does not promote across namespaces. " +
                        $"Call it free as `{ma.MemberName}(<{ma.Target.Type.EmitName}>, …)` " +
                        $"(bare with `using \"{crossNsCandidate.DeclaringType.Namespace}\"`, or `{crossNsCandidate.DeclaringType.Namespace}.{ma.MemberName}(…)`).");
                else
                    _diagnostics.Report("", 0, 0,
                        $"IL: no method '{ma.MemberName}' on type '{ma.Target.Type.EmitName}' (argCount={args.Count}).");
            }
        }
    }

    /// The dotted namespace path a call qualifier denotes, or null when the target is a
    /// real value/receiver chain. Two shapes flatten to the same string: a single dotted
    /// `BoundNameExpression` (`Lib.Inner`, produced when the binder qualifies a bare call)
    /// and nested member access (`(Lib.Inner)` from a source-written `Lib.Inner.fn()`).
    /// Gated on `ExternalType`: a namespace segment is unresolved-as-a-value, so an actual
    /// receiver (`obj.field`, whose parts carry concrete types) yields null and a normal
    /// member call proceeds.
    static string? NamespacePathOf(BoundExpression target) => target switch
    {
        // A namespace segment is unresolved-as-a-value: a flat ExternalType (the
        // segment name) or the inference hole (a dotted segment the binder could
        // not type as a member). An actual receiver carries a concrete type.
        BoundNameExpression { Type: Esharp.BoundTree.ExternalType or Esharp.BoundTree.InferredType } n => n.Name,
        BoundMemberAccessExpression { Type: Esharp.BoundTree.ExternalType or Esharp.BoundTree.InferredType } m
            when NamespacePathOf(m.Target) is { } prefix => $"{prefix}.{m.MemberName}",
        _ => null,
    };

    /// The declaring namespace of a receiver's underlying `data` type (peeling `*T`/`ref T`),
    /// or null when the receiver is not a user `data`. Drives the namespace-local promotion
    /// gate for dot-syntax dispatch: a free function attaches to a type only in that type's
    /// namespace, so a candidate static is a real method form only when its host class's
    /// namespace equals this. Null leaves dispatch ungated (non-`data` receiver — promotion
    /// does not apply to it anyway).
    static string? ReceiverDataNamespace(BoundType t) => t switch
    {
        DataType d => d.Namespace,
        HeapPointerBoundType hp => ReceiverDataNamespace(hp.Inner),
        ByRefBoundType br => ReceiverDataNamespace(br.Inner),
        _ => null,
    };

    /// The `__Ptr_T` wrapper Cecil type of a receiver whose SLOT is wrapper-represented,
    /// or null. Covers a `*T` foreach element / async state-machine field stored as the
    /// wrapper even though the binder typed the name `ref T` — a managed pointer can't be
    /// a heap field, so the slot is forced to the wrapper. Lets value-receiver method
    /// resolution deref the wrapper regardless of the (possibly downgraded) binder type.
    TypeReference? TryGetReceiverWrapperType(BoundExpression target)
    {
        TypeReference? cecilType = null;
        if (target is BoundNameExpression name)
            cecilType = TryResolveSlot(name.Name)?.Type;
        else
        {
            try { cecilType = _types.Resolve(target.Type); }
            catch { /* unresolvable receiver type — not a wrapper */ }
        }
        return cecilType is not null && ILHeapPointer.IsWrapperType(cecilType) ? cecilType : null;
    }
}
