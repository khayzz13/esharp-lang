using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Compiler.Binding;
using Esharp.Compiler.Syntax;
using ExceptionHandler = Mono.Cecil.Cil.ExceptionHandler;

namespace Esharp.ILEmit;

public sealed class ILMethodEmitter
{
    readonly ILProcessor _il;
    readonly ILTypeResolver _types;
    readonly MethodDefinition _method;
    readonly Dictionary<string, VariableDefinition> _locals = new(StringComparer.Ordinal);
    readonly Dictionary<string, ParameterDefinition> _params = new(StringComparer.Ordinal);
    readonly Dictionary<string, FieldDefinition> _structFields;
    bool _inTryOrDefer;
    bool _lastCallWasVoid;
    int _tempCounter;

    public ILMethodEmitter(
        MethodDefinition method,
        ILTypeResolver types,
        Dictionary<string, FieldDefinition>? structFields = null)
    {
        _method = method;
        _il = method.Body.GetILProcessor();
        _types = types;
        _structFields = structFields ?? [];

        foreach (var p in method.Parameters)
            _params[p.Name] = p;
    }

    public void EmitBlock(BoundBlockStatement block)
    {
        foreach (var stmt in block.Statements)
            EmitStatement(stmt);
    }

    void EmitStatement(BoundStatement stmt)
    {
        switch (stmt)
        {
            case BoundVariableDeclaration v:
                EmitVarDecl(v);
                break;
            case BoundAssignment a:
                EmitAssignment(a);
                break;
            case BoundCompoundAssignment ca:
                EmitCompoundAssignment(ca);
                break;
            case BoundIfStatement i:
                EmitIf(i);
                break;
            case BoundWhileStatement w:
                EmitWhile(w);
                break;
            case BoundReturnStatement r:
                EmitReturn(r);
                break;
            case BoundExpressionStatement e:
                _lastCallWasVoid = false;
                EmitExpression(e.Expression);
                // Only pop if the expression left a value on the stack
                if (e.Expression.Type is not VoidType && !_lastCallWasVoid)
                    _il.Emit(OpCodes.Pop);
                break;
            case BoundMatchStatement m:
                EmitMatch(m);
                break;
            case BoundDeferStatement d:
                _inTryOrDefer = true;
                EmitDefer(d);
                break;
            case BoundForEachStatement fe:
                EmitForEach(fe);
                break;
            case BoundBlockStatement b:
                EmitBlock(b);
                break;
        }
    }

    void EmitVarDecl(BoundVariableDeclaration v)
    {
        var varType = _types.Resolve(v.DeclaredType);
        var local = new VariableDefinition(varType);
        _method.Body.Variables.Add(local);
        _locals[v.Name] = local;

        EmitExpression(v.Initializer);
        _il.Emit(OpCodes.Stloc, local);
    }

    void EmitAssignment(BoundAssignment a)
    {
        if (a.Target is BoundNameExpression name)
        {
            EmitExpression(a.Value);
            if (_locals.TryGetValue(name.Name, out var local))
                _il.Emit(OpCodes.Stloc, local);
            else if (_params.TryGetValue(name.Name, out var param))
                _il.Emit(OpCodes.Starg, param);
        }
        else if (a.Target is BoundMemberAccessExpression ma)
        {
            EmitAddress(ma.Target); // push struct address
            EmitExpression(a.Value);
            if (TryResolveField(ma, out var field))
                _il.Emit(OpCodes.Stfld, field);
        }
    }

    void EmitCompoundAssignment(BoundCompoundAssignment ca)
    {
        if (ca.Target is BoundNameExpression name)
        {
            EmitExpression(ca.Target);
            EmitExpression(ca.Value);
            EmitBinaryOp(ca.Op);
            if (_locals.TryGetValue(name.Name, out var local))
                _il.Emit(OpCodes.Stloc, local);
            else if (_params.TryGetValue(name.Name, out var param))
                _il.Emit(OpCodes.Starg, param);
        }
        else if (ca.Target is BoundMemberAccessExpression ma)
        {
            EmitAddress(ma.Target);
            _il.Emit(OpCodes.Dup);
            if (TryResolveField(ma, out var field))
            {
                _il.Emit(OpCodes.Ldfld, field);
                EmitExpression(ca.Value);
                EmitBinaryOp(ca.Op);
                _il.Emit(OpCodes.Stfld, field);
            }
        }
    }

    void EmitIf(BoundIfStatement i)
    {
        EmitExpression(i.Condition);
        var elseLabel = _il.Create(OpCodes.Nop);
        var endLabel = _il.Create(OpCodes.Nop);

        _il.Emit(OpCodes.Brfalse, elseLabel);
        EmitStatement(i.Then);

        if (i.Else is not null)
        {
            _il.Emit(OpCodes.Br, endLabel);
            _il.Append(elseLabel);
            EmitStatement(i.Else);
            _il.Append(endLabel);
        }
        else
        {
            _il.Append(elseLabel);
        }
    }

    void EmitWhile(BoundWhileStatement w)
    {
        var condLabel = _il.Create(OpCodes.Nop);
        var endLabel = _il.Create(OpCodes.Nop);

        _il.Append(condLabel);
        EmitExpression(w.Condition);
        _il.Emit(OpCodes.Brfalse, endLabel);
        EmitStatement(w.Body);
        _il.Emit(OpCodes.Br, condLabel);
        _il.Append(endLabel);
    }

    void EmitReturn(BoundReturnStatement r)
    {
        if (r.Expression is BoundCallExpression tailCall && !_inTryOrDefer
            && tailCall.Target is BoundNameExpression tailName)
        {
            // Tail call: only for direct function calls (not member access / factory calls)
            var methodRef = FindMethod(tailName.Name, tailCall.Arguments.Count);
            if (methodRef is not null)
            {
                foreach (var arg in tailCall.Arguments)
                    EmitExpression(arg);
                _il.Emit(OpCodes.Tail);
                _il.Emit(OpCodes.Call, methodRef);
                _il.Emit(OpCodes.Ret);
                return;
            }
        }
        if (r.Expression is not null)
            EmitExpression(r.Expression);
        _il.Emit(OpCodes.Ret);
    }

    void EmitMatch(BoundMatchStatement m)
    {
        // Emit subject, get the tag
        EmitExpression(m.Subject);

        // If subject is a choice type, access .Tag for the switch
        var subjectType = m.SubjectType;
        VariableDefinition? subjectLocal = null;

        if (subjectType is ChoiceType)
        {
            // Store the choice value for payload extraction in arms
            var choiceTypeRef = _types.Resolve(subjectType);
            subjectLocal = new VariableDefinition(choiceTypeRef);
            _method.Body.Variables.Add(subjectLocal);
            _il.Emit(OpCodes.Stloc, subjectLocal);

            // Load .Tag for the switch
            _il.Emit(OpCodes.Ldloca, subjectLocal);
            var tagField = FindFieldOnType(choiceTypeRef, "Tag");
            if (tagField is not null)
                _il.Emit(OpCodes.Ldfld, tagField);
        }

        // Build jump table: one target per case arm, plus default
        var endLabel = _il.Create(OpCodes.Nop);
        var defaultLabel = endLabel;

        // Collect non-default arms that have case names (for indexed jump table)
        var caseArms = m.Arms.Where(a => !a.Pattern.IsDefault && a.Pattern.CaseName is not null).ToList();
        var defaultArm = m.Arms.FirstOrDefault(a => a.Pattern.IsDefault);

        if (subjectType is ChoiceType && caseArms.Count > 0)
        {
            // IL switch opcode: jump table indexed by the int on stack
            var armLabels = caseArms.Select(_ => _il.Create(OpCodes.Nop)).ToArray();
            var defaultArmLabel = _il.Create(OpCodes.Nop);
            if (defaultArm is not null)
                defaultLabel = defaultArmLabel;

            _il.Emit(OpCodes.Switch, armLabels);
            _il.Emit(OpCodes.Br, defaultLabel); // fall-through = default

            for (var i = 0; i < caseArms.Count; i++)
            {
                _il.Append(armLabels[i]);

                // If arm has a binding, extract the payload
                var arm = caseArms[i];
                if (arm.Pattern.BindingName is not null && subjectLocal is not null)
                {
                    var payloadFieldName = arm.Pattern.CaseName!;
                    var choiceTypeRef = _types.Resolve(subjectType);
                    var payloadField = FindFieldOnType(choiceTypeRef, payloadFieldName);
                    if (payloadField is not null && arm.Pattern.BindingType is not null)
                    {
                        var bindingLocal = new VariableDefinition(_types.Resolve(arm.Pattern.BindingType));
                        _method.Body.Variables.Add(bindingLocal);
                        _locals[arm.Pattern.BindingName] = bindingLocal;

                        _il.Emit(OpCodes.Ldloca, subjectLocal);
                        _il.Emit(OpCodes.Ldfld, payloadField);
                        _il.Emit(OpCodes.Stloc, bindingLocal);
                    }
                }

                EmitBlock(arm.Body);
                _il.Emit(OpCodes.Br, endLabel);
            }

            if (defaultArm is not null)
            {
                _il.Append(defaultArmLabel);
                EmitBlock(defaultArm.Body);
                _il.Emit(OpCodes.Br, endLabel);
            }
        }
        else
        {
            // Fallback: cascading if/else for non-choice matches
            // Pop the subject value since we won't use switch
            if (subjectType is not ChoiceType)
                ; // value is still on stack from EmitExpression — needs handling per case

            for (var i = 0; i < m.Arms.Count; i++)
            {
                var arm = m.Arms[i];
                if (arm.Pattern.IsDefault)
                {
                    EmitBlock(arm.Body);
                    _il.Emit(OpCodes.Br, endLabel);
                }
                else
                {
                    // Simplified: just emit bodies sequentially for now
                    EmitBlock(arm.Body);
                    _il.Emit(OpCodes.Br, endLabel);
                }
            }
        }

        _il.Append(endLabel);
    }

    void EmitDefer(BoundDeferStatement d)
    {
        // defer { body } → try { ... remaining ... } finally { body }
        // Simplified: emit as try/finally wrapping the defer body
        // In practice, defer should wrap everything AFTER this point until function end.
        // For now, emit the body in a finally block around a nop try.
        var tryStart = _il.Create(OpCodes.Nop);
        var tryEnd = _il.Create(OpCodes.Nop);
        var finallyStart = _il.Create(OpCodes.Nop);
        var finallyEnd = _il.Create(OpCodes.Endfinally);
        var afterFinally = _il.Create(OpCodes.Nop);

        _il.Append(tryStart);
        _il.Append(tryEnd);
        _il.Emit(OpCodes.Leave, afterFinally);
        _il.Append(finallyStart);
        EmitBlock(d.Body);
        _il.Append(finallyEnd);
        _il.Append(afterFinally);

        _method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = tryStart,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = afterFinally,
        });
    }

    void EmitForEach(BoundForEachStatement fe)
    {
        // Emit: var enumerator = collection.GetEnumerator()
        //       try { while (enumerator.MoveNext()) { var x = enumerator.Current; body } }
        //       finally { enumerator.Dispose(); }
        _inTryOrDefer = true;

        EmitExpression(fe.Collection);

        // Resolve IEnumerable<T>.GetEnumerator → IEnumerator<T>
        var enumerableType = _types.ImportReference(typeof(System.Collections.IEnumerable));
        var getEnumerator = _types.Module.ImportReference(
            typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator"));
        var moveNext = _types.Module.ImportReference(
            typeof(System.Collections.IEnumerator).GetMethod("MoveNext"));
        var getCurrent = _types.Module.ImportReference(
            typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod());
        var dispose = _types.Module.ImportReference(
            typeof(IDisposable).GetMethod("Dispose"));

        // Store enumerator
        var enumeratorType = _types.ImportReference(typeof(System.Collections.IEnumerator));
        var enumeratorLocal = new VariableDefinition(enumeratorType);
        _method.Body.Variables.Add(enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, getEnumerator);
        _il.Emit(OpCodes.Stloc, enumeratorLocal);

        // Loop variable
        var elementType = _types.ImportReference(typeof(object));
        var elementLocal = new VariableDefinition(elementType);
        _method.Body.Variables.Add(elementLocal);
        _locals[fe.Identifier] = elementLocal;

        var tryStart = _il.Create(OpCodes.Nop);
        var condLabel = _il.Create(OpCodes.Nop);
        var loopBody = _il.Create(OpCodes.Nop);
        var finallyStart = _il.Create(OpCodes.Nop);
        var afterFinally = _il.Create(OpCodes.Nop);

        // try {
        _il.Append(tryStart);
        _il.Emit(OpCodes.Br, condLabel);

        // loop body: var x = enumerator.Current
        _il.Append(loopBody);
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, getCurrent);
        _il.Emit(OpCodes.Stloc, elementLocal);
        EmitStatement(fe.Body);

        // condition: enumerator.MoveNext()
        _il.Append(condLabel);
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, moveNext);
        _il.Emit(OpCodes.Brtrue, loopBody);
        _il.Emit(OpCodes.Leave, afterFinally);

        // } finally { enumerator.Dispose(); }
        _il.Append(finallyStart);
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, dispose);
        _il.Emit(OpCodes.Endfinally);
        _il.Append(afterFinally);

        _method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = tryStart,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = afterFinally,
        });
    }

    // === Expressions — each pushes exactly one value onto the stack ===

    public void EmitExpression(BoundExpression expr)
    {
        switch (expr)
        {
            case BoundLiteralExpression lit:
                EmitLiteral(lit);
                break;
            case BoundNameExpression name:
                EmitNameLoad(name);
                break;
            case BoundBinaryExpression bin:
                EmitBinary(bin);
                break;
            case BoundUnaryExpression unary:
                EmitUnary(unary);
                break;
            case BoundMemberAccessExpression ma:
                EmitMemberAccess(ma);
                break;
            case BoundCallExpression call:
                EmitCall(call);
                break;
            case BoundObjectCreationExpression oc:
                EmitObjectCreation(oc);
                break;
            case BoundParenthesizedExpression p:
                EmitExpression(p.Inner);
                break;
            case BoundDotCaseExpression dc:
                EmitDotCase(dc);
                break;
            case BoundAddressOfExpression ao:
                EmitAddressOf(ao);
                break;
            case BoundAwaitExpression aw:
                EmitAwait(aw);
                break;
            case BoundResultCallExpression rc:
                // Simplified: just emit the argument for now
                EmitExpression(rc.Argument);
                break;
            case BoundIndexExpression idx:
                // Simplified: emit target and index, call indexer
                EmitExpression(idx.Target);
                EmitExpression(idx.Index);
                break;
        }
    }

    void EmitAddressOf(BoundAddressOfExpression ao)
    {
        // ldftn — push function pointer onto the stack
        var methodRef = FindMethod(ao.FunctionName, ao.ParameterTypes.Count);
        if (methodRef is not null)
            _il.Emit(OpCodes.Ldftn, methodRef);
    }

    void EmitAwait(BoundAwaitExpression aw)
    {
        // v1: blocking await — expr.GetAwaiter().GetResult()
        // Emits correct result on stack, blocks the thread.
        // Full struct state machine (true async) is v2.
        EmitExpression(aw.Inner);

        // The inner expression left a Task<T>/ValueTask<T>/etc on the stack.
        // We need to call GetAwaiter() then GetResult() on it.
        // Since we don't know the exact type at compile time, use reflection to find methods.
        // For common types (Task, Task<T>, ValueTask<T>), resolve directly.

        // Store to temp so we can call methods on it
        var innerType = aw.Inner.Type;
        var tempType = _types.BoundTypeToRuntime(innerType);

        // Try Task<T> family
        if (tempType is not null && (tempType.Name.StartsWith("Task") || tempType.Name.StartsWith("ValueTask")))
        {
            // Use reflection on the runtime type to find GetAwaiter, then GetResult
            var getAwaiterInfo = tempType.GetMethod("GetAwaiter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, Type.EmptyTypes);
            if (getAwaiterInfo is not null)
            {
                var awaiterRuntimeType = getAwaiterInfo.ReturnType;
                var getResultInfo = awaiterRuntimeType.GetMethod("GetResult", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, Type.EmptyTypes);
                if (getResultInfo is not null)
                {
                    _il.Emit(OpCodes.Call, _types.Module.ImportReference(getAwaiterInfo));
                    // GetResult is on a value type (awaiter) — need address
                    var awaiterLocal = new VariableDefinition(_types.Module.ImportReference(awaiterRuntimeType));
                    _method.Body.Variables.Add(awaiterLocal);
                    _il.Emit(OpCodes.Stloc, awaiterLocal);
                    _il.Emit(OpCodes.Ldloca, awaiterLocal);
                    _il.Emit(OpCodes.Call, _types.Module.ImportReference(getResultInfo));
                    return;
                }
            }
        }

        // Fallback: treat as generic awaitable — call .GetAwaiter().GetResult() via object
        // For Task (non-generic), just call .Wait() and push nothing
        // For Task<T>, call .Result property
        var taskType = typeof(System.Threading.Tasks.Task);
        var waitMethod = _types.Module.ImportReference(taskType.GetMethod("Wait", Type.EmptyTypes)!);
        _il.Emit(OpCodes.Callvirt, waitMethod);
    }

    void EmitDotCase(BoundDotCaseExpression dc)
    {
        // Resolve the factory method on the choice struct
        foreach (var arg in dc.Arguments)
            EmitExpression(arg);

        // Find the factory method: static method named dc.CaseName on the type dc.ResolvedTypeName
        foreach (var type in _types.Module.Types)
        {
            if (type.Name == dc.ResolvedTypeName)
            {
                var factory = type.Methods.FirstOrDefault(m => m.Name == dc.CaseName && m.IsStatic);
                if (factory is not null)
                {
                    _il.Emit(OpCodes.Call, factory);
                    return;
                }
            }
        }
    }

    void EmitLiteral(BoundLiteralExpression lit)
    {
        // Use the bound type to determine IL instruction, not just the runtime value type
        if (lit.Type is PrimitiveType pt)
        {
            switch (pt.Name)
            {
                case "int" or "uint" or "short" or "ushort" or "byte" or "sbyte" or "char":
                    _il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(lit.Value));
                    return;
                case "long" or "ulong":
                    _il.Emit(OpCodes.Ldc_I8, Convert.ToInt64(lit.Value));
                    return;
                case "double":
                    _il.Emit(OpCodes.Ldc_R8, Convert.ToDouble(lit.Value));
                    return;
                case "float":
                    _il.Emit(OpCodes.Ldc_R4, Convert.ToSingle(lit.Value));
                    return;
                case "bool":
                    _il.Emit(lit.Value is true ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    return;
                case "string":
                    if (lit.Value is string s)
                    {
                        if (s.Contains('{'))
                            EmitInterpolatedString(s);
                        else
                            _il.Emit(OpCodes.Ldstr, s);
                    }
                    return;
            }
        }

        // Fallback: use runtime type
        switch (lit.Value)
        {
            case int i:
                _il.Emit(OpCodes.Ldc_I4, i);
                break;
            case long l:
                _il.Emit(OpCodes.Ldc_I8, l);
                break;
            case double d:
                _il.Emit(OpCodes.Ldc_R8, d);
                break;
            case float f:
                _il.Emit(OpCodes.Ldc_R4, f);
                break;
            case bool b:
                _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;
            case string s:
                if (s.Contains('{'))
                    EmitInterpolatedString(s);
                else
                    _il.Emit(OpCodes.Ldstr, s);
                break;
            case null:
                _il.Emit(OpCodes.Ldnull);
                break;
        }
    }

    void EmitInterpolatedString(string template)
    {
        // Parse interpolation segments and emit string.Concat
        var parts = new List<(bool isExpr, string text)>();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i);
                if (end > i)
                {
                    parts.Add((true, template[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            var start = i;
            while (i < template.Length && template[i] != '{') i++;
            parts.Add((false, template[start..i]));
        }

        // For simplicity, emit as string.Concat(object[])
        _il.Emit(OpCodes.Ldc_I4, parts.Count);
        _il.Emit(OpCodes.Newarr, _types.ImportReference(typeof(object)));

        for (var idx = 0; idx < parts.Count; idx++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, idx);

            if (parts[idx].isExpr)
            {
                // Try to resolve as local or param, then box if value type
                var exprName = parts[idx].text;
                // Handle dotted access like "v.x"
                if (exprName.Contains('.'))
                {
                    var segments = exprName.Split('.');
                    EmitNameLoadByString(segments[0]);
                    for (var s = 1; s < segments.Length; s++)
                        EmitFieldAccessByName(segments[s]);
                }
                else
                {
                    EmitNameLoadByString(exprName);
                }
                _il.Emit(OpCodes.Box, _types.ImportReference(typeof(object)));
            }
            else
            {
                _il.Emit(OpCodes.Ldstr, parts[idx].text);
            }

            _il.Emit(OpCodes.Stelem_Ref);
        }

        var concatMethod = _types.Module.ImportReference(
            typeof(string).GetMethod("Concat", [typeof(object[])]));
        _il.Emit(OpCodes.Call, concatMethod);
    }

    void EmitNameLoadByString(string name)
    {
        if (_locals.TryGetValue(name, out var local))
            _il.Emit(OpCodes.Ldloc, local);
        else if (_params.TryGetValue(name, out var param))
            _il.Emit(OpCodes.Ldarg, param);
    }

    void EmitFieldAccessByName(string fieldName)
    {
        // This is a simplified path for string interpolation
        // The value on stack is a struct — need address for ldfld
        // For now, store to temp, load address, access field
        if (_structFields.TryGetValue(fieldName, out var field))
        {
            var temp = new VariableDefinition(field.DeclaringType);
            _method.Body.Variables.Add(temp);
            _il.Emit(OpCodes.Stloc, temp);
            _il.Emit(OpCodes.Ldloca, temp);
            _il.Emit(OpCodes.Ldfld, field);
        }
    }

    void EmitNameLoad(BoundNameExpression name)
    {
        if (_locals.TryGetValue(name.Name, out var local))
            _il.Emit(OpCodes.Ldloc, local);
        else if (_params.TryGetValue(name.Name, out var param))
            _il.Emit(OpCodes.Ldarg, param);
    }

    void EmitBinary(BoundBinaryExpression bin)
    {
        // Comparison operators that produce bool
        if (bin.Op is SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals or
            SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or
            SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals or
            SyntaxTokenKind.AndKeyword or SyntaxTokenKind.OrKeyword or
            SyntaxTokenKind.AmpAmp or SyntaxTokenKind.PipePipe)
        {
            EmitExpression(bin.Left);
            EmitExpression(bin.Right);
            EmitBinaryOp(bin.Op);
            return;
        }

        // Arithmetic
        EmitExpression(bin.Left);
        EmitExpression(bin.Right);
        EmitBinaryOp(bin.Op);
    }

    void EmitBinaryOp(SyntaxTokenKind op)
    {
        switch (op)
        {
            case SyntaxTokenKind.Plus or SyntaxTokenKind.PlusEquals:
                _il.Emit(OpCodes.Add); break;
            case SyntaxTokenKind.Minus or SyntaxTokenKind.MinusEquals:
                _il.Emit(OpCodes.Sub); break;
            case SyntaxTokenKind.Star or SyntaxTokenKind.StarEquals:
                _il.Emit(OpCodes.Mul); break;
            case SyntaxTokenKind.Slash or SyntaxTokenKind.SlashEquals:
                _il.Emit(OpCodes.Div); break;
            case SyntaxTokenKind.Percent:
                _il.Emit(OpCodes.Rem); break;
            case SyntaxTokenKind.EqualsEquals:
                _il.Emit(OpCodes.Ceq); break;
            case SyntaxTokenKind.BangEquals:
                _il.Emit(OpCodes.Ceq);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                break;
            case SyntaxTokenKind.Less:
                _il.Emit(OpCodes.Clt); break;
            case SyntaxTokenKind.Greater:
                _il.Emit(OpCodes.Cgt); break;
            case SyntaxTokenKind.LessEquals:
                _il.Emit(OpCodes.Cgt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                break;
            case SyntaxTokenKind.GreaterEquals:
                _il.Emit(OpCodes.Clt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                break;
            case SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword:
                _il.Emit(OpCodes.And); break;
            case SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword:
                _il.Emit(OpCodes.Or); break;
        }
    }

    void EmitUnary(BoundUnaryExpression unary)
    {
        EmitExpression(unary.Operand);
        switch (unary.Op)
        {
            case SyntaxTokenKind.Minus:
                _il.Emit(OpCodes.Neg);
                break;
            case SyntaxTokenKind.Bang or SyntaxTokenKind.NotKeyword:
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                break;
        }
    }

    void EmitMemberAccess(BoundMemberAccessExpression ma)
    {
        EmitAddress(ma.Target); // struct needs address for ldfld
        if (TryResolveField(ma, out var field))
            _il.Emit(OpCodes.Ldfld, field);
    }

    void EmitCall(BoundCallExpression call)
    {
        if (call.Target is BoundMemberAccessExpression ma)
        {
            EmitMemberCall(ma, call.Arguments);
            return;
        }

        // Push all arguments first for simple name calls
        foreach (var arg in call.Arguments)
            EmitExpression(arg);

        if (call.Target is BoundNameExpression name)
        {
            var methodRef = FindMethod(name.Name, call.Arguments.Count);
            if (methodRef is not null)
                _il.Emit(OpCodes.Call, methodRef);
        }
    }

    void EmitMemberCall(BoundMemberAccessExpression ma, IReadOnlyList<BoundExpression> args)
    {
        // 1. Check if it's a factory call on a type defined in this module (choice/data)
        if (ma.Target is BoundNameExpression typeName)
        {
            foreach (var type in _types.Module.Types)
            {
                if (type.Name == typeName.Name)
                {
                    var factory = type.Methods.FirstOrDefault(m => m.Name == ma.MemberName && m.IsStatic);
                    if (factory is not null)
                    {
                        foreach (var arg in args)
                            EmitExpression(arg);
                        _il.Emit(OpCodes.Call, factory);
                        return;
                    }
                }
            }
        }

        // 2. External static method call: TypeName.Method(args)
        //    e.g., Console.WriteLine(x), Math.Max(a, b), Guid.NewGuid()
        if (ma.Target is BoundNameExpression staticTypeName)
        {
            var runtimeType = _types.TryResolveRuntimeType(staticTypeName.Name);
            if (runtimeType is not null)
            {
                foreach (var arg in args)
                    EmitExpression(arg);

                // Build runtime arg types for overload resolution
                var argTypes = args.Select(a => _types.BoundTypeToRuntime(a.Type) ?? typeof(object)).ToArray();
                var methodRef = _types.ResolveExternalMethod(runtimeType, ma.MemberName, args.Count, argTypes);
                // Fallback without exact types (handles boxing scenarios like Console.WriteLine(object))
                methodRef ??= _types.ResolveExternalMethod(runtimeType, ma.MemberName, args.Count);
                if (methodRef is not null)
                {
                    _il.Emit(OpCodes.Call, methodRef);
                    if (methodRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }
            }
        }

        // 3. Instance method call: expr.Method(args)
        //    e.g., list.Add(item), str.ToUpper(), value.ToString()
        {
            // Push the target (instance) onto stack
            var targetType = ma.Target.Type;

            // For value types, we need the address; for reference types, the reference
            bool isValueType = targetType is DataType or PrimitiveType { Name: not "string" };
            if (isValueType)
                EmitAddress(ma.Target);
            else
                EmitExpression(ma.Target);

            // Push arguments
            foreach (var arg in args)
                EmitExpression(arg);

            // Try to resolve the method on the target's runtime type
            var instanceRuntimeType = _types.BoundTypeToRuntime(targetType);
            if (instanceRuntimeType is not null)
            {
                var argTypes = args.Select(a => _types.BoundTypeToRuntime(a.Type) ?? typeof(object)).ToArray();
                var methodRef = _types.ResolveExternalMethod(instanceRuntimeType, ma.MemberName, args.Count, argTypes);
                methodRef ??= _types.ResolveExternalMethod(instanceRuntimeType, ma.MemberName, args.Count);
                if (methodRef is not null)
                {
                    // Value types + sealed/struct methods use call; reference types use callvirt
                    _il.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, methodRef);
                    if (methodRef.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
                    return;
                }
            }

            // 4. Try resolving on the struct types defined in this module
            if (ma.Target is BoundNameExpression instanceName)
            {
                if (_locals.TryGetValue(instanceName.Name, out var local))
                {
                    var typeDef = local.VariableType.Resolve();
                    if (typeDef is not null)
                    {
                        var method = typeDef.Methods.FirstOrDefault(m => m.Name == ma.MemberName && m.Parameters.Count == args.Count);
                        if (method is not null)
                        {
                            _il.Emit(OpCodes.Call, method);
                            return;
                        }
                    }
                }
            }
        }
    }

    void EmitObjectCreation(BoundObjectCreationExpression oc)
    {
        var typeRef = _types.Resolve(oc.ObjectType);

        // Create local for the struct
        var local = new VariableDefinition(typeRef);
        _method.Body.Variables.Add(local);

        // initobj to zero-initialize
        _il.Emit(OpCodes.Ldloca, local);
        _il.Emit(OpCodes.Initobj, typeRef);

        // Set each field
        foreach (var fieldInit in oc.Fields)
        {
            _il.Emit(OpCodes.Ldloca, local);
            EmitExpression(fieldInit.Value);

            var fieldDef = FindFieldOnType(typeRef, fieldInit.Name);
            if (fieldDef is not null)
                _il.Emit(OpCodes.Stfld, fieldDef);
        }

        // Push the struct value
        _il.Emit(OpCodes.Ldloc, local);
    }

    // === Helpers ===

    /// <summary>Push the address of a value (for struct field access)</summary>
    void EmitAddress(BoundExpression expr)
    {
        if (expr is BoundNameExpression name)
        {
            if (_locals.TryGetValue(name.Name, out var local))
                _il.Emit(OpCodes.Ldloca, local);
            else if (_params.TryGetValue(name.Name, out var param))
                _il.Emit(OpCodes.Ldarga, param);
        }
        else
        {
            // For complex expressions, evaluate to a temp and take its address
            EmitExpression(expr);
            var temp = new VariableDefinition(_types.ImportReference(typeof(object)));
            _method.Body.Variables.Add(temp);
            _il.Emit(OpCodes.Stloc, temp);
            _il.Emit(OpCodes.Ldloca, temp);
        }
    }

    bool TryResolveField(BoundMemberAccessExpression ma, out FieldReference field)
    {
        field = null!;
        // Check if the target type has this field in our defined structs
        if (ma.Target is BoundNameExpression name)
        {
            if (_locals.TryGetValue(name.Name, out var local))
            {
                var f = FindFieldOnType(local.VariableType, ma.MemberName);
                if (f is not null) { field = f; return true; }
            }
            else if (_params.TryGetValue(name.Name, out var param))
            {
                var f = FindFieldOnType(param.ParameterType, ma.MemberName);
                if (f is not null) { field = f; return true; }
            }
        }
        // Fallback: check struct fields passed to this emitter
        if (_structFields.TryGetValue(ma.MemberName, out var sf))
        {
            field = sf;
            return true;
        }
        return false;
    }

    FieldReference? FindFieldOnType(TypeReference typeRef, string fieldName)
    {
        var typeDef = typeRef.Resolve();
        if (typeDef is null) return null;
        return typeDef.Fields.FirstOrDefault(f => f.Name == fieldName);
    }

    MethodReference? FindMethod(string name, int argCount)
    {
        // Search the module for a matching static method
        foreach (var type in _types.Module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (method.Name == name && method.Parameters.Count == argCount)
                    return method;
            }
        }
        return null;
    }
}
