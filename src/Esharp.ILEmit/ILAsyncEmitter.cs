using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Compiler.Binding;
using ExceptionHandler = Mono.Cecil.Cil.ExceptionHandler;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace Esharp.ILEmit;

/// <summary>
/// Emits async functions as CLR struct-based IAsyncStateMachine implementations.
/// Produces the same pattern as Roslyn: a stub method + nested state machine struct with MoveNext().
/// </summary>
public sealed class ILAsyncEmitter
{
    readonly ModuleDefinition _module;
    readonly ILTypeResolver _types;
    readonly Dictionary<string, Dictionary<string, FieldDefinition>> _structFieldMaps;

    public ILAsyncEmitter(ModuleDefinition module, ILTypeResolver types,
        Dictionary<string, Dictionary<string, FieldDefinition>> structFieldMaps)
    {
        _module = module;
        _types = types;
        _structFieldMaps = structFieldMaps;
    }

    // ═══ Analysis ═══

    sealed record AsyncAnalysis(
        List<AwaitPoint> AwaitPoints,
        List<(string Name, BoundType Type)> Locals,
        List<(string Name, BoundType Type)> Parameters,
        BoundType ReturnType,
        bool IsVoid);

    sealed record AwaitPoint(int Index, BoundAwaitExpression Expression);

    AsyncAnalysis Analyze(BoundFunctionDeclaration func)
    {
        var awaitPoints = new List<AwaitPoint>();
        var locals = new List<(string, BoundType)>();
        CollectAwaitPoints(func.Body, awaitPoints);
        CollectLocals(func.Body, locals);

        var parameters = func.Parameters.Select(p => (p.Name, p.Type)).ToList();
        var isVoid = func.ReturnType is VoidType;

        return new AsyncAnalysis(awaitPoints, locals, parameters, func.ReturnType, isVoid);
    }

    static void CollectAwaitPoints(BoundStatement stmt, List<AwaitPoint> points)
    {
        switch (stmt)
        {
            case BoundBlockStatement block:
                foreach (var s in block.Statements) CollectAwaitPoints(s, points);
                break;
            case BoundVariableDeclaration v:
                CollectAwaitPointsExpr(v.Initializer, points);
                break;
            case BoundExpressionStatement e:
                CollectAwaitPointsExpr(e.Expression, points);
                break;
            case BoundReturnStatement r:
                if (r.Expression is not null) CollectAwaitPointsExpr(r.Expression, points);
                break;
            case BoundIfStatement i:
                CollectAwaitPointsExpr(i.Condition, points);
                CollectAwaitPoints(i.Then, points);
                if (i.Else is not null) CollectAwaitPoints(i.Else, points);
                break;
            case BoundAssignment a:
                CollectAwaitPointsExpr(a.Value, points);
                break;
        }
    }

    static void CollectAwaitPointsExpr(BoundExpression expr, List<AwaitPoint> points)
    {
        switch (expr)
        {
            case BoundAwaitExpression aw:
                points.Add(new AwaitPoint(points.Count, aw));
                break;
            case BoundCallExpression call:
                foreach (var arg in call.Arguments) CollectAwaitPointsExpr(arg, points);
                CollectAwaitPointsExpr(call.Target, points);
                break;
            case BoundBinaryExpression bin:
                CollectAwaitPointsExpr(bin.Left, points);
                CollectAwaitPointsExpr(bin.Right, points);
                break;
            case BoundUnaryExpression u:
                CollectAwaitPointsExpr(u.Operand, points);
                break;
            case BoundParenthesizedExpression p:
                CollectAwaitPointsExpr(p.Inner, points);
                break;
            case BoundMemberAccessExpression ma:
                CollectAwaitPointsExpr(ma.Target, points);
                break;
        }
    }

    static void CollectLocals(BoundStatement stmt, List<(string, BoundType)> locals)
    {
        switch (stmt)
        {
            case BoundBlockStatement block:
                foreach (var s in block.Statements) CollectLocals(s, locals);
                break;
            case BoundVariableDeclaration v:
                locals.Add((v.Name, v.DeclaredType));
                break;
            case BoundIfStatement i:
                CollectLocals(i.Then, locals);
                if (i.Else is not null) CollectLocals(i.Else, locals);
                break;
            case BoundWhileStatement w:
                CollectLocals(w.Body, locals);
                break;
            case BoundForEachStatement f:
                locals.Add((f.Identifier, new ExternalType("var")));
                CollectLocals(f.Body, locals);
                break;
        }
    }

    // ═══ Public entry point ═══

    public void EmitAsyncFunction(
        MethodDefinition stubMethod,
        BoundFunctionDeclaration func,
        TypeDefinition moduleClass)
    {
        var analysis = Analyze(func);

        // Change stub method return type to ValueTask<T> or ValueTask
        var valueTaskType = ResolveValueTaskType(analysis);
        stubMethod.ReturnType = valueTaskType;

        // Create the state machine struct as a nested type
        var (smType, fields) = EmitStateMachineType(moduleClass, stubMethod.Name, analysis, valueTaskType);

        // Emit MoveNext()
        EmitMoveNext(smType, fields, func, analysis);

        // Emit SetStateMachine()
        EmitSetStateMachine(smType, fields);

        // Emit the stub method body
        EmitStubBody(stubMethod, smType, fields, analysis);
    }

    // ═══ ValueTask type resolution ═══

    TypeReference ResolveValueTaskType(AsyncAnalysis analysis)
    {
        if (analysis.IsVoid)
            return _module.ImportReference(typeof(System.Threading.Tasks.ValueTask));

        var innerType = _types.Resolve(analysis.ReturnType);
        var vtGeneric = _module.ImportReference(typeof(System.Threading.Tasks.ValueTask<>));
        return new GenericInstanceType(vtGeneric) { GenericArguments = { innerType } };
    }

    TypeReference ResolveBuilderType(AsyncAnalysis analysis)
    {
        if (analysis.IsVoid)
            return _module.ImportReference(typeof(AsyncValueTaskMethodBuilder));

        var innerType = _types.Resolve(analysis.ReturnType);
        var builderGeneric = _module.ImportReference(typeof(AsyncValueTaskMethodBuilder<>));
        return new GenericInstanceType(builderGeneric) { GenericArguments = { innerType } };
    }

    // ═══ State machine struct ═══

    sealed record SmFields(
        FieldDefinition State,
        FieldDefinition Builder,
        Dictionary<string, FieldDefinition> Params,
        Dictionary<string, FieldDefinition> Locals,
        List<FieldDefinition> Awaiters);

    (TypeDefinition smType, SmFields fields) EmitStateMachineType(
        TypeDefinition parent, string funcName, AsyncAnalysis analysis, TypeReference valueTaskType)
    {
        var smName = $"<{funcName}>d__StateMachine";
        var smType = new TypeDefinition(
            "",
            smName,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.AnsiClass |
            TypeAttributes.BeforeFieldInit | TypeAttributes.SequentialLayout,
            _module.ImportReference(typeof(ValueType)));

        // Implement IAsyncStateMachine
        smType.Interfaces.Add(new InterfaceImplementation(
            _module.ImportReference(typeof(IAsyncStateMachine))));

        // _state field
        var stateField = new FieldDefinition("_state", FieldAttributes.Public, _module.ImportReference(typeof(int)));
        smType.Fields.Add(stateField);

        // _builder field
        var builderType = ResolveBuilderType(analysis);
        var builderField = new FieldDefinition("_builder", FieldAttributes.Public, builderType);
        smType.Fields.Add(builderField);

        // Parameter fields
        var paramFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var (name, type) in analysis.Parameters)
        {
            var field = new FieldDefinition(name, FieldAttributes.Public, _types.Resolve(type));
            smType.Fields.Add(field);
            paramFields[name] = field;
        }

        // Local fields — when type is "var"/"object" (unresolved external), use the function return type
        // as a best guess (the most common case is `let result = await someTask()` where result is T)
        var localFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var (name, type) in analysis.Locals)
        {
            var resolvedType = type;
            if (type is ExternalType { Name: "var" or "object" } && !analysis.IsVoid)
                resolvedType = analysis.ReturnType;
            var fieldType = _types.Resolve(resolvedType);
            var field = new FieldDefinition($"_local_{name}", FieldAttributes.Public, fieldType);
            smType.Fields.Add(field);
            localFields[name] = field;
        }

        // Awaiter fields — one per await point
        // For v1, use TaskAwaiter (non-generic) as a default; real type depends on inner expression
        var awaiterFields = new List<FieldDefinition>();
        for (var i = 0; i < analysis.AwaitPoints.Count; i++)
        {
            // Default to TaskAwaiter<object> — the actual awaiter type will be refined
            // when we know the inner expression's runtime type
            var awaiterType = _module.ImportReference(typeof(TaskAwaiter<>));
            var genericAwaiter = new GenericInstanceType(awaiterType)
            {
                GenericArguments = { _module.ImportReference(typeof(object)) }
            };
            var field = new FieldDefinition($"_awaiter{i}", FieldAttributes.Public, genericAwaiter);
            smType.Fields.Add(field);
            awaiterFields.Add(field);
        }

        parent.NestedTypes.Add(smType);
        return (smType, new SmFields(stateField, builderField, paramFields, localFields, awaiterFields));
    }

    // ═══ MoveNext ═══

    void EmitMoveNext(TypeDefinition smType, SmFields fields, BoundFunctionDeclaration func, AsyncAnalysis analysis)
    {
        var moveNext = new MethodDefinition(
            "MoveNext",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual |
            MethodAttributes.Final | MethodAttributes.NewSlot,
            _module.ImportReference(typeof(void)));

        moveNext.Body.InitLocals = true;
        var il = moveNext.Body.GetILProcessor();

        // Local 0: state copy
        var stateLocal = new VariableDefinition(_module.ImportReference(typeof(int)));
        moveNext.Body.Variables.Add(stateLocal);

        // Local 1: result (if non-void)
        VariableDefinition? resultLocal = null;
        if (!analysis.IsVoid)
        {
            resultLocal = new VariableDefinition(_types.Resolve(analysis.ReturnType));
            moveNext.Body.Variables.Add(resultLocal);
        }

        // Local 2: exception
        var exLocal = new VariableDefinition(_module.ImportReference(typeof(Exception)));
        moveNext.Body.Variables.Add(exLocal);

        // Load state
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fields.State);
        il.Emit(OpCodes.Stloc, stateLocal);

        // Labels
        var methodEnd = il.Create(OpCodes.Nop);
        var tryStart = il.Create(OpCodes.Nop);
        var catchStart = il.Create(OpCodes.Nop);
        var afterCatch = il.Create(OpCodes.Nop);

        // Resume labels — one per await point
        var resumeLabels = analysis.AwaitPoints.Select(_ => il.Create(OpCodes.Nop)).ToArray();
        var completedLabels = analysis.AwaitPoints.Select(_ => il.Create(OpCodes.Nop)).ToArray();

        // .try {
        il.Append(tryStart);

        // Switch dispatch
        if (resumeLabels.Length > 0)
        {
            il.Emit(OpCodes.Ldloc, stateLocal);
            il.Emit(OpCodes.Switch, resumeLabels);
        }
        // Fall-through = initial state (-1): start from the beginning

        // ═══ Emit the function body, splitting at await points ═══
        var bodyEmitter = new AsyncBodyEmitter(il, _types, _module, fields, analysis, smType,
            resumeLabels, completedLabels, resultLocal, methodEnd);
        bodyEmitter.EmitBlock(func.Body);

        // Fallback: if body didn't explicitly return, set default result and exit try
        // (For functions with explicit return statements, this is dead code — harmless)
        if (!analysis.IsVoid && resultLocal is not null)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, fields.Builder);
            il.Emit(OpCodes.Ldloc, resultLocal);
            EmitBuilderSetResult(il, fields.Builder, analysis);
            il.Emit(OpCodes.Leave, afterCatch);
        }
        else if (analysis.IsVoid)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, fields.Builder);
            EmitBuilderSetResult(il, fields.Builder, analysis);
            il.Emit(OpCodes.Leave, afterCatch);
        }
        else
        {
            il.Emit(OpCodes.Leave, afterCatch);
        }

        // } catch (Exception ex) {
        il.Append(catchStart);
        il.Emit(OpCodes.Stloc, exLocal);

        // _state = -2
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, -2);
        il.Emit(OpCodes.Stfld, fields.State);

        // _builder.SetException(ex)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, fields.Builder);
        il.Emit(OpCodes.Ldloc, exLocal);
        EmitBuilderSetException(il, fields.Builder, analysis);
        il.Emit(OpCodes.Leave, afterCatch);

        // After catch
        il.Append(afterCatch);

        // _state = -2 (completed)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, -2);
        il.Emit(OpCodes.Stfld, fields.State);

        // methodEnd: final ret (target for suspend leaves)
        il.Append(methodEnd);
        il.Emit(OpCodes.Ret);

        // Exception handler
        moveNext.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = afterCatch,
            CatchType = _module.ImportReference(typeof(Exception)),
        });

        ILOptimizer.ShortenOpcodes(moveNext.Body);
        smType.Methods.Add(moveNext);
    }

    // ═══ SetStateMachine ═══

    void EmitSetStateMachine(TypeDefinition smType, SmFields fields)
    {
        var method = new MethodDefinition(
            "SetStateMachine",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual |
            MethodAttributes.Final | MethodAttributes.NewSlot,
            _module.ImportReference(typeof(void)));

        method.Parameters.Add(new ParameterDefinition("stateMachine", Mono.Cecil.ParameterAttributes.None,
            _module.ImportReference(typeof(IAsyncStateMachine))));

        var il = method.Body.GetILProcessor();
        // _builder.SetStateMachine(stateMachine)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, fields.Builder);
        il.Emit(OpCodes.Ldarg_1);

        var builderType = fields.Builder.FieldType.Resolve();
        var setSmMethod = builderType.Methods.FirstOrDefault(m => m.Name == "SetStateMachine");
        if (setSmMethod is not null)
            il.Emit(OpCodes.Call, _module.ImportReference(setSmMethod));

        il.Emit(OpCodes.Ret);
        smType.Methods.Add(method);
    }

    // ═══ Stub method ═══

    void EmitStubBody(MethodDefinition stub, TypeDefinition smType, SmFields fields, AsyncAnalysis analysis)
    {
        stub.Body = new Mono.Cecil.Cil.MethodBody(stub);
        stub.Body.InitLocals = true;
        var il = stub.Body.GetILProcessor();

        // Local 0: state machine instance
        var smLocal = new VariableDefinition(smType);
        stub.Body.Variables.Add(smLocal);

        // initobj: zero-initialize the struct
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Initobj, smType);

        // _builder = AsyncValueTaskMethodBuilder<T>.Create()
        il.Emit(OpCodes.Ldloca, smLocal);
        var createMethod = ResolveBuilderCreate(analysis);
        if (createMethod is not null)
        {
            il.Emit(OpCodes.Call, createMethod);
            il.Emit(OpCodes.Stfld, fields.Builder);
        }

        // Copy parameters to fields
        for (var i = 0; i < analysis.Parameters.Count; i++)
        {
            var (name, _) = analysis.Parameters[i];
            if (fields.Params.TryGetValue(name, out var paramField))
            {
                il.Emit(OpCodes.Ldloca, smLocal);
                il.Emit(OpCodes.Ldarg, stub.Parameters[i]);
                il.Emit(OpCodes.Stfld, paramField);
            }
        }

        // _state = -1
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stfld, fields.State);

        // _builder.Start(ref stateMachine)
        var startMethod = ResolveBuilderStart(analysis, smType);
        if (startMethod is not null)
        {
            il.Emit(OpCodes.Ldloca, smLocal);
            il.Emit(OpCodes.Ldflda, fields.Builder);
            il.Emit(OpCodes.Ldloca, smLocal);
            il.Emit(OpCodes.Call, startMethod);
        }

        // return _builder.Task
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Ldflda, fields.Builder);
        var taskProp = ResolveBuilderTaskGetter(analysis);
        if (taskProp is not null)
            il.Emit(OpCodes.Call, taskProp);
        il.Emit(OpCodes.Ret);

        ILOptimizer.ShortenOpcodes(stub.Body);
    }

    // ═══ Builder method resolution ═══

    MethodReference? ResolveBuilderCreate(AsyncAnalysis analysis)
    {
        var builderRuntimeType = analysis.IsVoid
            ? typeof(AsyncValueTaskMethodBuilder)
            : typeof(AsyncValueTaskMethodBuilder<>);

        var method = builderRuntimeType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        if (method is null) return null;

        var imported = _module.ImportReference(method);
        if (!analysis.IsVoid)
        {
            var innerType = _types.Resolve(analysis.ReturnType);
            imported.DeclaringType = new GenericInstanceType(_module.ImportReference(builderRuntimeType))
            {
                GenericArguments = { innerType }
            };
        }
        return imported;
    }

    MethodReference? ResolveBuilderStart(AsyncAnalysis analysis, TypeDefinition smType)
    {
        var builderRuntimeType = analysis.IsVoid
            ? typeof(AsyncValueTaskMethodBuilder)
            : typeof(AsyncValueTaskMethodBuilder<>);

        var method = builderRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Start");
        if (method is null) return null;

        var imported = _module.ImportReference(method);

        // Make the declaring type a closed generic if needed
        if (!analysis.IsVoid)
        {
            var innerType = _types.Resolve(analysis.ReturnType);
            imported.DeclaringType = new GenericInstanceType(_module.ImportReference(builderRuntimeType))
            {
                GenericArguments = { innerType }
            };
        }

        // Start<TStateMachine>(ref TStateMachine) — needs generic method instantiation
        var genericStart = new GenericInstanceMethod(imported);
        genericStart.GenericArguments.Add(smType);
        return genericStart;
    }

    MethodReference? ResolveBuilderTaskGetter(AsyncAnalysis analysis)
    {
        var builderRuntimeType = analysis.IsVoid
            ? typeof(AsyncValueTaskMethodBuilder)
            : typeof(AsyncValueTaskMethodBuilder<>);

        var prop = builderRuntimeType.GetProperty("Task", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetGetMethod() is not { } getter) return null;

        var imported = _module.ImportReference(getter);
        if (!analysis.IsVoid)
        {
            var innerType = _types.Resolve(analysis.ReturnType);
            imported.DeclaringType = new GenericInstanceType(_module.ImportReference(builderRuntimeType))
            {
                GenericArguments = { innerType }
            };
        }
        return imported;
    }

    void EmitBuilderSetResult(ILProcessor il, FieldDefinition builderField, AsyncAnalysis analysis)
    {
        var builderRuntimeType = analysis.IsVoid
            ? typeof(AsyncValueTaskMethodBuilder)
            : typeof(AsyncValueTaskMethodBuilder<>);

        var method = builderRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "SetResult");

        if (method is null) return;
        var imported = _module.ImportReference(method);
        if (!analysis.IsVoid)
        {
            var innerType = _types.Resolve(analysis.ReturnType);
            imported.DeclaringType = new GenericInstanceType(_module.ImportReference(builderRuntimeType))
            {
                GenericArguments = { innerType }
            };
        }
        il.Emit(OpCodes.Call, imported);
    }

    void EmitBuilderSetException(ILProcessor il, FieldDefinition builderField, AsyncAnalysis analysis)
    {
        var builderRuntimeType = analysis.IsVoid
            ? typeof(AsyncValueTaskMethodBuilder)
            : typeof(AsyncValueTaskMethodBuilder<>);

        var method = builderRuntimeType.GetMethod("SetException", [typeof(Exception)]);
        if (method is null) return;
        var imported = _module.ImportReference(method);
        if (!analysis.IsVoid)
        {
            var innerType = _types.Resolve(analysis.ReturnType);
            imported.DeclaringType = new GenericInstanceType(_module.ImportReference(builderRuntimeType))
            {
                GenericArguments = { innerType }
            };
        }
        il.Emit(OpCodes.Call, imported);
    }

    // ═══ Async body emitter — emits statements using fields instead of locals ═══

    sealed class AsyncBodyEmitter
    {
        readonly ILProcessor _il;
        readonly ILTypeResolver _types;
        readonly ModuleDefinition _module;
        readonly SmFields _fields;
        readonly AsyncAnalysis _analysis;
        readonly TypeDefinition _smType;
        readonly Instruction[] _resumeLabels;
        readonly Instruction[] _completedLabels;
        readonly VariableDefinition? _resultLocal;
        readonly Instruction _methodEnd;
        int _awaitIndex;

        public AsyncBodyEmitter(ILProcessor il, ILTypeResolver types, ModuleDefinition module,
            SmFields fields, AsyncAnalysis analysis, TypeDefinition smType,
            Instruction[] resumeLabels, Instruction[] completedLabels,
            VariableDefinition? resultLocal, Instruction methodEnd)
        {
            _il = il;
            _types = types;
            _module = module;
            _fields = fields;
            _analysis = analysis;
            _smType = smType;
            _resumeLabels = resumeLabels;
            _completedLabels = completedLabels;
            _resultLocal = resultLocal;
            _methodEnd = methodEnd;
        }

        public void EmitBlock(BoundBlockStatement block)
        {
            foreach (var stmt in block.Statements) EmitStatement(stmt);
        }

        void EmitStatement(BoundStatement stmt)
        {
            switch (stmt)
            {
                case BoundVariableDeclaration v:
                    EmitVarDecl(v);
                    break;
                case BoundReturnStatement r:
                    EmitReturn(r);
                    break;
                case BoundExpressionStatement e:
                    EmitExpression(e.Expression);
                    // Pop non-void results
                    if (e.Expression.Type is not VoidType && !IsVoidCall(e.Expression))
                        _il.Emit(OpCodes.Pop);
                    break;
                case BoundIfStatement i:
                    EmitIf(i);
                    break;
                case BoundWhileStatement w:
                    EmitWhile(w);
                    break;
                case BoundAssignment a:
                    EmitAssignment(a);
                    break;
                case BoundCompoundAssignment ca:
                    EmitCompoundAssignment(ca);
                    break;
                case BoundBlockStatement b:
                    EmitBlock(b);
                    break;
            }
        }

        static bool IsVoidCall(BoundExpression expr) =>
            expr is BoundCallExpression { Type: ExternalType { Name: "var" or "void" } };

        void EmitVarDecl(BoundVariableDeclaration v)
        {
            if (v.Initializer is BoundAwaitExpression aw)
            {
                // Special handling: await + store result to field
                EmitAwaitPoint(aw, v.Name);
                return;
            }

            EmitExpression(v.Initializer);
            StoreToField(v.Name);
        }

        void EmitReturn(BoundReturnStatement r)
        {
            if (!_analysis.IsVoid && r.Expression is not null)
            {
                if (r.Expression is BoundAwaitExpression aw)
                {
                    // await in return position: emit await, then SetResult
                    EmitAwaitPoint(aw, null);
                    // Result is on stack
                }
                else
                {
                    EmitExpression(r.Expression);
                }

                // _builder.SetResult(value)
                if (_resultLocal is not null)
                    _il.Emit(OpCodes.Stloc, _resultLocal);

                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldflda, _fields.Builder);
                if (_resultLocal is not null)
                    _il.Emit(OpCodes.Ldloc, _resultLocal);

                EmitSetResult();
            }
            else if (_analysis.IsVoid)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldflda, _fields.Builder);
                EmitSetResult();
            }
            _il.Emit(OpCodes.Leave, _methodEnd);
        }

        void EmitSetResult()
        {
            var builderRuntimeType = _analysis.IsVoid
                ? typeof(AsyncValueTaskMethodBuilder)
                : typeof(AsyncValueTaskMethodBuilder<>);

            // For open generic types, search by name (can't match by parameter type on open generic)
            var method = builderRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "SetResult");

            if (method is null) return;
            var imported = _module.ImportReference(method);
            if (!_analysis.IsVoid)
            {
                var innerType = _types.Resolve(_analysis.ReturnType);
                imported.DeclaringType = new GenericInstanceType(_module.ImportReference(builderRuntimeType))
                {
                    GenericArguments = { innerType }
                };
            }
            _il.Emit(OpCodes.Call, imported);
        }

        // ═══ The core: await point emission ═══

        void EmitAwaitPoint(BoundAwaitExpression aw, string? targetFieldName)
        {
            var idx = _awaitIndex++;
            if (idx >= _resumeLabels.Length) return;

            // Emit the inner expression (pushes Task<T> / ValueTask<T> onto stack)
            EmitExpression(aw.Inner);

            // Resolve the runtime type of the inner expression for GetAwaiter() lookup
            var innerRuntimeType = _types.BoundTypeToRuntime(aw.Inner.Type);

            // If the bound type is "var" or "object" (external method, binder doesn't know the return type),
            // use Task<T> where T is the function return type. This gives us TaskAwaiter<T> with
            // a typed GetResult(). The actual runtime type on the stack will be Task<T> anyway.
            Type effectiveTaskType;
            if (innerRuntimeType is null || innerRuntimeType == typeof(object) ||
                (!typeof(System.Threading.Tasks.Task).IsAssignableFrom(innerRuntimeType) &&
                 !innerRuntimeType.Name.StartsWith("ValueTask")))
            {
                // Use Task<ReturnType> so we get TaskAwaiter<ReturnType> with typed GetResult
                var resultRuntimeType = _types.BoundTypeToRuntime(_analysis.ReturnType) ?? typeof(object);
                effectiveTaskType = _analysis.IsVoid
                    ? typeof(System.Threading.Tasks.Task)
                    : typeof(System.Threading.Tasks.Task<>).MakeGenericType(resultRuntimeType);
            }
            else
            {
                effectiveTaskType = innerRuntimeType;
            }

            var getAwaiterInfo = effectiveTaskType.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            // Fallback to Task base if the specific type doesn't have GetAwaiter directly
            getAwaiterInfo ??= typeof(System.Threading.Tasks.Task).GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);

            var getAwaiter = _module.ImportReference(getAwaiterInfo!);
            var awaiterRealType = getAwaiterInfo!.ReturnType;
            var awaiterTypeRef = _module.ImportReference(awaiterRealType);

            // Update the awaiter field type to match
            _fields.Awaiters[idx].FieldType = awaiterTypeRef;

            var awaiterLocal = new VariableDefinition(awaiterTypeRef);
            _il.Body.Method.Body.Variables.Add(awaiterLocal);

            // Call GetAwaiter(), store to local
            _il.Emit(OpCodes.Call, getAwaiter);
            _il.Emit(OpCodes.Stloc, awaiterLocal);

            // Use reflection on the runtime awaiter type for IsCompleted and GetResult
            var isCompletedProp = awaiterRealType.GetProperty("IsCompleted", BindingFlags.Public | BindingFlags.Instance);
            if (isCompletedProp?.GetGetMethod() is { } isCompletedGetter)
            {
                _il.Emit(OpCodes.Ldloca, awaiterLocal);
                _il.Emit(OpCodes.Call, _module.ImportReference(isCompletedGetter));
                _il.Emit(OpCodes.Brtrue, _completedLabels[idx]);
            }

            // Not completed — suspend
            // _state = idx
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, idx);
            _il.Emit(OpCodes.Stfld, _fields.State);

            // Save awaiter to field
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, awaiterLocal);
            _il.Emit(OpCodes.Stfld, _fields.Awaiters[idx]);

            // _builder.AwaitUnsafeOnCompleted(ref awaiter, ref this)
            EmitAwaitUnsafeOnCompleted(idx, awaiterTypeRef);

            // leave (return from MoveNext, suspended)
            _il.Emit(OpCodes.Leave, _methodEnd);

            // Resume label — entered when scheduler calls MoveNext again
            _il.Append(_resumeLabels[idx]);
            // Reload awaiter from field
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _fields.Awaiters[idx]);
            _il.Emit(OpCodes.Stloc, awaiterLocal);

            // Completed label — reached when IsCompleted was true (fast path)
            _il.Append(_completedLabels[idx]);

            // GetResult() — pushes result onto stack (or void for non-generic Task)
            var getResultInfo = awaiterRealType.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (getResultInfo is not null)
            {
                _il.Emit(OpCodes.Ldloca, awaiterLocal);
                _il.Emit(OpCodes.Call, _module.ImportReference(getResultInfo));
            }

            // Store result to field if this was a variable declaration
            if (targetFieldName is not null)
            {
                // Result is on stack from GetResult() — store to the local field
                // Stack: [value]. stfld needs [this, value]. Use temp to reorder.
                if (_fields.Locals.TryGetValue(targetFieldName, out var localField))
                {
                    // If field is object but GetResult returns a value type, box first
                    var getResultReturn = getResultInfo?.ReturnType;
                    if (localField.FieldType.FullName == "System.Object" &&
                        getResultReturn is not null && getResultReturn.IsValueType)
                    {
                        _il.Emit(OpCodes.Box, _module.ImportReference(getResultReturn));
                    }

                    var temp = new VariableDefinition(localField.FieldType);
                    _il.Body.Method.Body.Variables.Add(temp);
                    _il.Emit(OpCodes.Stloc, temp);   // pop value into temp
                    _il.Emit(OpCodes.Ldarg_0);        // push this
                    _il.Emit(OpCodes.Ldloc, temp);    // push value
                    _il.Emit(OpCodes.Stfld, localField);
                }
            }
        }

        void EmitAwaitUnsafeOnCompleted(int idx, TypeReference awaiterTypeRef)
        {
            var builderRuntimeType = _analysis.IsVoid
                ? typeof(AsyncValueTaskMethodBuilder)
                : typeof(AsyncValueTaskMethodBuilder<>);

            var method = builderRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "AwaitUnsafeOnCompleted");

            if (method is null) return;

            var imported = _module.ImportReference(method);
            if (!_analysis.IsVoid)
            {
                var innerType = _types.Resolve(_analysis.ReturnType);
                imported.DeclaringType = new GenericInstanceType(_module.ImportReference(builderRuntimeType))
                {
                    GenericArguments = { innerType }
                };
            }

            // AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter, ref TStateMachine)
            var genericMethod = new GenericInstanceMethod(imported);
            genericMethod.GenericArguments.Add(awaiterTypeRef);
            genericMethod.GenericArguments.Add(_smType); // state machine type

            // Push: ref builder (already via ldflda), ref awaiter field, ref this
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldflda, _fields.Builder);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldflda, _fields.Awaiters[idx]);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Call, genericMethod);
        }

        // ═══ Expression emission — reads from fields instead of locals ═══

        void EmitExpression(BoundExpression expr)
        {
            switch (expr)
            {
                case BoundLiteralExpression lit:
                    EmitLiteral(lit);
                    break;
                case BoundNameExpression name:
                    LoadFromField(name.Name);
                    break;
                case BoundBinaryExpression bin:
                    EmitExpression(bin.Left);
                    EmitExpression(bin.Right);
                    EmitBinaryOp(bin.Op);
                    break;
                case BoundUnaryExpression u:
                    EmitExpression(u.Operand);
                    EmitUnaryOp(u.Op);
                    break;
                case BoundCallExpression call:
                    EmitCall(call);
                    break;
                case BoundMemberAccessExpression ma:
                    EmitMemberAccess(ma);
                    break;
                case BoundParenthesizedExpression p:
                    EmitExpression(p.Inner);
                    break;
                case BoundAwaitExpression aw:
                    // Standalone await (not in var decl) — emit point, result stays on stack
                    EmitAwaitPoint(aw, null);
                    break;
                default:
                    // For types we don't handle yet, emit nop
                    _il.Emit(OpCodes.Nop);
                    break;
            }
        }

        void EmitLiteral(BoundLiteralExpression lit)
        {
            if (lit.Type is PrimitiveType pt)
            {
                switch (pt.Name)
                {
                    case "int": _il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(lit.Value)); return;
                    case "long": _il.Emit(OpCodes.Ldc_I8, Convert.ToInt64(lit.Value)); return;
                    case "double": _il.Emit(OpCodes.Ldc_R8, Convert.ToDouble(lit.Value)); return;
                    case "float": _il.Emit(OpCodes.Ldc_R4, Convert.ToSingle(lit.Value)); return;
                    case "bool": _il.Emit(lit.Value is true ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); return;
                    case "string" when lit.Value is string s:
                        _il.Emit(OpCodes.Ldstr, s); return;
                }
            }
            switch (lit.Value)
            {
                case int i: _il.Emit(OpCodes.Ldc_I4, i); break;
                case string s: _il.Emit(OpCodes.Ldstr, s); break;
                case bool b: _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); break;
                case null: _il.Emit(OpCodes.Ldnull); break;
            }
        }

        void LoadFromField(string name)
        {
            // Try local fields first, then param fields
            if (_fields.Locals.TryGetValue(name, out var localField))
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, localField);
            }
            else if (_fields.Params.TryGetValue(name, out var paramField))
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, paramField);
            }
        }

        void StoreToField(string name)
        {
            if (_fields.Locals.TryGetValue(name, out var localField))
            {
                // Stack has the value. Need [this, value] for stfld.
                var temp = new VariableDefinition(localField.FieldType);
                _il.Body.Method.Body.Variables.Add(temp);
                _il.Emit(OpCodes.Stloc, temp);
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, temp);
                _il.Emit(OpCodes.Stfld, localField);
            }
        }

        void EmitCall(BoundCallExpression call)
        {
            // Push arguments
            foreach (var arg in call.Arguments)
                EmitExpression(arg);

            if (call.Target is BoundMemberAccessExpression ma && ma.Target is BoundNameExpression typeName)
            {
                // Static external call: Console.WriteLine etc.
                var runtimeType = _types.TryResolveRuntimeType(typeName.Name);
                if (runtimeType is not null)
                {
                    var argTypes = call.Arguments.Select(a => _types.BoundTypeToRuntime(a.Type) ?? typeof(object)).ToArray();
                    var methodRef = _types.ResolveExternalMethod(runtimeType, ma.MemberName, call.Arguments.Count, argTypes);
                    methodRef ??= _types.ResolveExternalMethod(runtimeType, ma.MemberName, call.Arguments.Count);
                    if (methodRef is not null)
                        _il.Emit(OpCodes.Call, methodRef);
                    return;
                }
            }

            if (call.Target is BoundNameExpression nameTarget)
            {
                // Module-local function call
                foreach (var type in _types.Module.Types)
                {
                    var method = type.Methods.FirstOrDefault(m => m.Name == nameTarget.Name && m.Parameters.Count == call.Arguments.Count);
                    if (method is not null)
                    {
                        _il.Emit(OpCodes.Call, method);
                        return;
                    }
                }
            }
        }

        void EmitMemberAccess(BoundMemberAccessExpression ma)
        {
            // Load address of struct for field access
            if (ma.Target is BoundNameExpression name)
            {
                if (_fields.Locals.TryGetValue(name.Name, out var lf))
                {
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldflda, lf);
                }
                else if (_fields.Params.TryGetValue(name.Name, out var pf))
                {
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldflda, pf);
                }
            }
            else
            {
                EmitExpression(ma.Target);
            }

            // Try to resolve the field
            // This is simplified — full resolution would check the type
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

        void EmitAssignment(BoundAssignment a)
        {
            if (a.Target is BoundNameExpression name)
            {
                EmitExpression(a.Value);
                StoreToField(name.Name);
            }
        }

        void EmitCompoundAssignment(BoundCompoundAssignment ca)
        {
            if (ca.Target is BoundNameExpression name)
            {
                LoadFromField(name.Name);
                EmitExpression(ca.Value);
                EmitBinaryOp(ca.Op);
                StoreToField(name.Name);
            }
        }

        void EmitBinaryOp(Esharp.Compiler.Syntax.SyntaxTokenKind op)
        {
            switch (op)
            {
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Plus or Esharp.Compiler.Syntax.SyntaxTokenKind.PlusEquals: _il.Emit(OpCodes.Add); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Minus or Esharp.Compiler.Syntax.SyntaxTokenKind.MinusEquals: _il.Emit(OpCodes.Sub); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Star or Esharp.Compiler.Syntax.SyntaxTokenKind.StarEquals: _il.Emit(OpCodes.Mul); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Slash or Esharp.Compiler.Syntax.SyntaxTokenKind.SlashEquals: _il.Emit(OpCodes.Div); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.EqualsEquals: _il.Emit(OpCodes.Ceq); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.BangEquals: _il.Emit(OpCodes.Ceq); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Less: _il.Emit(OpCodes.Clt); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Greater: _il.Emit(OpCodes.Cgt); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.LessEquals: _il.Emit(OpCodes.Cgt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.GreaterEquals: _il.Emit(OpCodes.Clt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.AmpAmp or Esharp.Compiler.Syntax.SyntaxTokenKind.AndKeyword: _il.Emit(OpCodes.And); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.PipePipe or Esharp.Compiler.Syntax.SyntaxTokenKind.OrKeyword: _il.Emit(OpCodes.Or); break;
            }
        }

        void EmitUnaryOp(Esharp.Compiler.Syntax.SyntaxTokenKind op)
        {
            switch (op)
            {
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Minus: _il.Emit(OpCodes.Neg); break;
                case Esharp.Compiler.Syntax.SyntaxTokenKind.Bang or Esharp.Compiler.Syntax.SyntaxTokenKind.NotKeyword:
                    _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); break;
            }
        }
    }
}
