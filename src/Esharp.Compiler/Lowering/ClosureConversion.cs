using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Converts every <see cref="BoundFunctionLiteralExpression"/> into a delegate over a synthesized
/// method, lifting captured variables into a compiler-generated display class. This is the
/// bound-tree form of ESC's emit-time closure conversion (<c>ILMethodEmitter.Closures.cs</c>),
/// reproducing its observable behavior:
///
/// <list type="bullet">
///   <item><b>One display per function</b>, holding the union of every capture at every depth.
///   A capturing lambda becomes an instance method on that display and binds to it; a nested
///   capturing lambda binds to the <em>same</em> display via <c>this</c>, so all closures over a
///   scope share one object.</item>
///   <item><b>Shared mutation.</b> A captured variable is <em>hoisted</em> into a display field:
///   its declaration becomes a store to the field, and <em>every</em> read/write — in the function
///   body and in every lambda — routes through the field. A write on either side is seen on the
///   other, exactly as the language requires (and unlike the old copy-at-capture model, which lost
///   the sharing).</item>
///   <item><b>Zero-capture lambdas</b> become free static trampolines (no allocation): a
///   <see cref="BoundMethodGroupConversion"/> with a null receiver. Their bodies are converted as
///   their own scope, so a closure nested in a static lambda gets its own display.</item>
/// </list>
///
/// Total descent (inherited from the generated rewriter) means a lambda in a ternary branch, a
/// constructor argument, a tuple, or another lambda's body is converted too — the positions the old
/// three hand-rolled walks skipped, and the nested-lambda leak they shipped.
///
/// <para>Limitation: a capturing lambda written directly in an <c>init</c>'s <c>base(...)</c> /
/// <c>this(...)</c> argument list is not hoisted (those argument expressions are left untouched);
/// lambdas live in bodies in practice. Init bodies themselves are fully converted.</para>
/// </summary>
public sealed class ClosureConversion : IBoundTreePass
{
    public static readonly ClosureConversion Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
    {
        var ctx = new ClosureContext(sink);

        List<BoundCompilationUnit>? units = null;
        for (var ui = 0; ui < program.Units.Count; ui++)
        {
            var unit = program.Units[ui];
            ctx.UnitSynth.Clear();

            List<BoundMember>? members = null;
            for (var mi = 0; mi < unit.Members.Count; mi++)
            {
                var m = unit.Members[mi];
                var mapped = ctx.ConvertMember(m);
                if (!ReferenceEquals(mapped, m) && members is null) members = [.. unit.Members.Take(mi)];
                members?.Add(mapped);
            }

            var changed = members is not null || ctx.UnitSynth.Count > 0;
            if (!changed) continue;

            var finalMembers = members ?? [.. unit.Members];
            finalMembers.AddRange(ctx.UnitSynth);
            units ??= [.. program.Units.Take(ui)];
            units?.Add(unit with { Members = finalMembers });
            continue;
        }
        return units is null ? program : program with { Units = units };
    }
}

/// Per-program closure-conversion state: the synthesized-symbol sink, the per-unit accumulator of
/// synthesized members (display classes + static trampolines), and a monotonic id source for unique
/// synthetic names.
sealed class ClosureContext(SynthesizedSymbolSink sink)
{
    internal sealed record CapturedPropertyLocation(
        string OwnerFieldName,
        BoundExpression Receiver,
        BoundMemberAccessExpression Property,
        BoundType PointeeType);

    public readonly SynthesizedSymbolSink Sink = sink;
    public readonly List<BoundMember> UnitSynth = [];
    int _id;

    public string Fresh(string role) => $"<{role}>__cc_{_id++}";

    public BoundMember ConvertMember(BoundMember member) => member switch
    {
        BoundFunctionDeclaration fn => ConvertFunction(fn),
        BoundDataDeclaration data   => ConvertData(data),
        BoundStaticFuncDeclaration sf => ConvertStaticFunc(sf),
        BoundNamespaceInitDeclaration init => ConvertNamespaceInit(init),
        BoundNamespaceStateDeclaration state => ConvertNamespaceState(state),
        _                           => member,
    };

    BoundNamespaceStateDeclaration ConvertNamespaceState(BoundNamespaceStateDeclaration state)
    {
        var rw = new StaticLambdaRewriter(this, $"namespace_state_{state.Field.Name}");
        var initializer = state.Field.DefaultValue is null ? null : rw.RewriteExpression(state.Field.DefaultValue);
        var getter = state.ComputedGetter is null ? null : rw.RewriteExpression(state.ComputedGetter);
        var setter = state.SetterBody is null ? null : rw.RewriteExpression(state.SetterBody);
        var field = ReferenceEquals(initializer, state.Field.DefaultValue)
            ? state.Field : state.Field with { DefaultValue = initializer };
        return ReferenceEquals(field, state.Field) && ReferenceEquals(getter, state.ComputedGetter)
            && ReferenceEquals(setter, state.SetterBody)
            ? state : state with { Field = field, ComputedGetter = getter, SetterBody = setter };
    }

    BoundNamespaceInitDeclaration ConvertNamespaceInit(BoundNamespaceInitDeclaration init)
    {
        var body = ConvertScope(init.Body, "namespace_init", []);
        return ReferenceEquals(body, init.Body) ? init : init with { Body = body };
    }

    BoundStaticFuncDeclaration ConvertStaticFunc(BoundStaticFuncDeclaration sf)
    {
        var changed = false;
        var functions = sf.Functions.Select(fn =>
        {
            var converted = ConvertFunction(fn);
            if (!ReferenceEquals(converted, fn)) changed = true;
            return converted;
        }).ToList();
        return changed ? sf with { Functions = functions } : sf;
    }

    BoundFunctionDeclaration ConvertFunction(BoundFunctionDeclaration fn, IReadOnlyList<string>? containingTypeParameters = null)
    {
        var genericParameters = (containingTypeParameters ?? [])
            .Concat(fn.TypeParameters.Where(p => !(containingTypeParameters ?? []).Contains(p, StringComparer.Ordinal)))
            .ToList();
        var body = ConvertScope(fn.Body, fn.Name, ParamNames(fn.Parameters), genericParameters);
        return ReferenceEquals(body, fn.Body) ? fn : fn with { Body = body };
    }

    BoundDataDeclaration ConvertData(BoundDataDeclaration data)
    {
        var changed = false;

        var methods = data.InstanceMethods.Select(m =>
        {
            var converted = ConvertFunction(m, data.TypeParameters);
            if (!ReferenceEquals(converted, m)) changed = true;
            return converted;
        }).ToList();

        List<BoundInitDeclaration>? inits = null;
        if (data.Inits is { } existing)
        {
            inits = existing.Select(init =>
            {
                var convertedBody = ConvertScope(init.Body, $"{data.Name}_init", ParamNames(init.Parameters));
                if (ReferenceEquals(convertedBody, init.Body)) return init;
                changed = true;
                return init with { Body = convertedBody };
            }).ToList();
        }

        if (!changed) return data;
        return data with { InstanceMethods = methods, Inits = inits ?? data.Inits };
    }

    /// Convert one function-like scope (a top function, an init body, or a zero-capture lambda body):
    /// collect its captures, and if any, mint a display, hoist them, and rewrite the body.
    public BoundBlockStatement ConvertScope(BoundBlockStatement body, string scopeName, HashSet<string> paramNames,
        IReadOnlyList<string>? typeParameters = null)
    {
        var captures = CaptureCollector.Collect(body);
        if (captures.Count == 0)
            return (BoundBlockStatement)new StaticLambdaRewriter(this, scopeName).RewriteStatement(body);

        // CLR fields cannot contain managed byrefs.  When a closure captures a
        // durable property location (`let p = &owner.property`), capture the
        // receiver object instead and reconstruct the property's loca protocol at
        // each use.  This is the source-level opaque property-location carrier:
        // it preserves owner + access behavior and never manufactures `*Class`.
        var propertyLocations = FindCapturedPropertyLocations(body, captures);
        var displayCaptures = new Dictionary<string, BoundType>(captures, StringComparer.Ordinal);
        foreach (var (name, location) in propertyLocations)
        {
            displayCaptures.Remove(name);
            displayCaptures[location.OwnerFieldName] = location.Receiver.Type;
        }

        var genericParameters = typeParameters ?? [];
        var (_, displayType) = Sink.SynthesizeDisplayClass(scopeName,
            [.. displayCaptures.Select(kv => (kv.Key, kv.Value))], genericParameters);
        var displayVar = Fresh("display");
        var displayRef = Synth.Name(displayVar, displayType);
        var displayMethods = new List<BoundFunctionDeclaration>();

        var rewriter = new HoistingClosureRewriter(this, displayRef, displayType, captures,
            propertyLocations, displayMethods, scopeName);
        var rewritten = (BoundBlockStatement)rewriter.RewriteStatement(body);

        // Materialize the display class: a sealed reference type with one field per capture and one
        // instance-method trampoline per capturing lambda. CodeGen emits it via the ordinary path.
        UnitSynth.Add(new BoundDataDeclaration(
            IsPublic: false, IsReadonly: false, Name: displayType.Name,
            TypeParameters: [.. genericParameters], DeriveTraits: [],
            Fields: [.. displayCaptures.Select(c => new BoundField(c.Key, c.Value, IsPublic: true, Mutable: true))],
            InstanceMethods: displayMethods,
            Classification: DataClassification.Class, Attributes: ["CompilerGenerated"],
            Modifier: ClassModifier.Sealed));

        // Prologue: allocate the display, then copy each captured PARAMETER into it (a captured
        // local initializes itself when its hoisted declaration runs).
        var prologue = new List<BoundStatement>
        {
            Synth.Var(displayVar, displayType, new BoundObjectCreationExpression(displayType, [])),
        };
        foreach (var (name, type) in captures)
            if (paramNames.Contains(name))
                prologue.Add(Synth.Assign(Synth.Member(displayRef, name, type), Synth.Name(name, type)));

        return Synth.Block([.. prologue, .. rewritten.Statements]) with { Span = body.Span };
    }

    static Dictionary<string, CapturedPropertyLocation> FindCapturedPropertyLocations(
        BoundBlockStatement body, IReadOnlyDictionary<string, BoundType> captures)
    {
        var result = new Dictionary<string, CapturedPropertyLocation>(StringComparer.Ordinal);
        Visit(body);
        return result;

        void Visit(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundVariableDeclaration
                {
                    Name: var name,
                    Initializer: BoundAddressOfVariableExpression
                    {
                        IsScopedPropertyBorrow: false,
                        Target: BoundMemberAccessExpression property,
                        PointeeType: var pointee,
                    }
                } when captures.ContainsKey(name):
                    result.TryAdd(name, new CapturedPropertyLocation(
                        $"__property_location_owner_{name}", property.Target, property, pointee));
                    break;
                case BoundBlockStatement block:
                    foreach (var child in block.Statements) Visit(child);
                    break;
                case BoundIfStatement conditional:
                    Visit(conditional.Then);
                    if (conditional.Else is not null) Visit(conditional.Else);
                    break;
                case BoundWhileStatement loop:
                    Visit(loop.Body);
                    break;
                case BoundTryStatement attempt:
                    Visit(attempt.Body);
                    foreach (var clause in attempt.Catches) Visit(clause.Body);
                    break;
                case BoundForEachStatement each:
                    Visit(each.Body);
                    break;
                case BoundDeferStatement defer:
                    Visit(defer.Body);
                    break;
            }
        }
    }

    public static HashSet<string> ParamNames(IReadOnlyList<BoundParameter> ps)
        => [.. ps.Select(p => p.Name)];

    /// Action<…> / Func<…,TResult> for a lambda's callable signature. An async
    /// lambda's source-level return is its body result, while its delegate observes
    /// the Task/ValueTask wrapper emitted by AsyncLowering.
    public static BoundType DelegateTypeFor(IReadOnlyList<BoundParameter> parameters, BoundType returnType,
        bool isAsync = false, AsyncReturnShape asyncShape = AsyncReturnShape.ValueTask)
    {
        if (isAsync)
            returnType = WrapAsyncReturn(returnType, asyncShape);
        var paramTypes = parameters.Select(p => p.Type).ToList();
        if (returnType is VoidType or PrimitiveType { Name: "void" })
            return paramTypes.Count == 0 ? new ExternalType("Action") : new ExternalType("Action", paramTypes);
        return new ExternalType("Func", [.. paramTypes, returnType]);
    }

    static BoundType WrapAsyncReturn(BoundType resultType, AsyncReturnShape shape)
    {
        if (shape is AsyncReturnShape.Void
            || resultType is ExternalType { Name: "Task" or "ValueTask" })
            return resultType;
        var isVoid = resultType is VoidType or PrimitiveType { Name: "void" };
        return shape switch
        {
            AsyncReturnShape.Void => new PrimitiveType("void"),
            AsyncReturnShape.Task => isVoid ? new ExternalType("Task") : new ExternalType("Task", [resultType]),
            _ => isVoid ? new ExternalType("ValueTask") : new ExternalType("ValueTask", [resultType]),
        };
    }
}

/// Collects the union of all captured variables across every lambda in a scope, at every nesting
/// depth (descending into lambda bodies). Total — it inherits the generated rewriter's full walk,
/// so a lambda buried in a ternary branch or a constructor argument is still seen.
sealed class CaptureCollector : LoweringRewriter
{
    readonly Dictionary<string, BoundType> _captures = new(StringComparer.Ordinal);

    public static Dictionary<string, BoundType> Collect(BoundBlockStatement body)
    {
        var c = new CaptureCollector();
        c.RewriteStatement(body);
        return c._captures;
    }

    protected override BoundExpression RewriteFunctionLiteralExpression(BoundFunctionLiteralExpression node)
    {
        foreach (var cv in node.CapturedVariables)
            _captures.TryAdd(cv.Name, cv.Type);
        return base.RewriteFunctionLiteralExpression(node);   // descend for nested lambdas
    }
}

/// Used for a scope with no captures: leaves names alone, but still converts any zero-capture
/// lambda it contains (recursively, each as its own scope) into a static trampoline.
sealed class StaticLambdaRewriter(ClosureContext ctx, string enclosingName) : LoweringRewriter
{
    protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
    {
        var rewritten = (BoundVariableDeclaration)base.RewriteVariableDeclaration(node);
        return RetargetDelegateBinding(node, rewritten);
    }

    protected override BoundExpression RewriteFunctionLiteralExpression(BoundFunctionLiteralExpression node)
        => ClosureEmit.StaticTrampoline(ctx, enclosingName, node);

    /// <summary>
    /// Closure conversion replaces a source literal with a concrete delegate construction. A source
    /// <c>let f = func(...) { ... }</c> starts life with <see cref="InferredType"/> because the literal
    /// has no type before its body has been bound. Leaving that placeholder on the declaration is wrong
    /// after conversion: async lowering uses declaration types to choose state-machine fields and would
    /// spill <c>f</c> as <c>object</c> while the resumed local expects <c>Func&lt;...&gt;</c>.
    ///
    /// A named delegate remains nominal even when its underlying Invoke shape matches <c>Func&lt;...&gt;</c>.
    /// An inferred binding, on the other hand, adopts the concrete delegate selected by conversion.
    /// </summary>
    internal static BoundStatement RetargetDelegateBinding(BoundVariableDeclaration original, BoundVariableDeclaration rewritten)
    {
        if (original.Initializer is not BoundFunctionLiteralExpression
            || rewritten.Initializer is not BoundMethodGroupConversion methodGroup)
            return rewritten;

        if (original.DeclaredType is NamedDelegateType)
            return rewritten with
            {
                Initializer = methodGroup with { DelegateType = original.DeclaredType },
                DeclaredType = original.DeclaredType,
            };

        return original.DeclaredType is InferredType
            ? rewritten with { DeclaredType = methodGroup.DelegateType }
            : rewritten;
    }
}

/// The workhorse for a scope that has captures. Parameterized by <paramref name="displayRef"/> — the
/// expression denoting the display in the current scope (the display local at function level, or
/// <c>this</c> inside a trampoline). It rewrites captured names to display fields, captured-local
/// declarations to field stores, and lambdas to delegates over the shared display.
sealed class HoistingClosureRewriter(
    ClosureContext ctx,
    BoundExpression displayRef,
    ExternalType displayType,
    Dictionary<string, BoundType> captures,
    Dictionary<string, ClosureContext.CapturedPropertyLocation> propertyLocations,
    List<BoundFunctionDeclaration> displayMethods,
    string enclosingName) : LoweringRewriter
{
    // A captured name (read or write) → displayRef.name. Intercepts at the leaf so it fires in every
    // position; everything else descends through the generated base.
    public override BoundExpression RewriteExpression(BoundExpression expr)
    {
        if (expr is BoundNameExpression n && propertyLocations.TryGetValue(n.Name, out var location))
            return PropertyValue(location);
        return expr is BoundNameExpression captured && captures.ContainsKey(captured.Name)
            ? Synth.Member(displayRef, captured.Name, captured.Type)
            : base.RewriteExpression(expr);
    }

    protected override BoundExpression RewriteAddressOfVariableExpression(BoundAddressOfVariableExpression node)
    {
        if (node.Target is BoundNameExpression name
            && propertyLocations.TryGetValue(name.Name, out var location))
            return new BoundAddressOfVariableExpression(PropertyValue(location), location.PointeeType);
        return base.RewriteAddressOfVariableExpression(node);
    }

    BoundMemberAccessExpression PropertyValue(ClosureContext.CapturedPropertyLocation location)
    {
        var owner = Synth.Member(displayRef, location.OwnerFieldName, location.Receiver.Type);
        return new BoundMemberAccessExpression(owner, location.Property.MemberName, location.PointeeType)
        {
            Member = location.Property.Member,
            IsPropertyLocationProjection = true,
            Span = location.Property.Span,
        };
    }

    // A captured-local declaration `var x = e` → `displayRef.x = e` (the field already exists; this
    // is its first store). A non-captured local stays a local.
    protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
    {
        var init = RewriteExpression(node.Initializer);
        if (propertyLocations.TryGetValue(node.Name, out var location))
        {
            var receiver = RewriteExpression(location.Receiver);
            return Synth.Assign(Synth.Member(displayRef, location.OwnerFieldName, location.Receiver.Type), receiver)
                with { Span = node.Span };
        }
        if (captures.ContainsKey(node.Name))
            return Synth.Assign(Synth.Member(displayRef, node.Name, node.DeclaredType), init) with { Span = node.Span };
        var rewritten = ReferenceEquals(init, node.Initializer) ? node : node with { Initializer = init };
        return StaticLambdaRewriter.RetargetDelegateBinding(node, rewritten);
    }

    protected override BoundExpression RewriteFunctionLiteralExpression(BoundFunctionLiteralExpression node)
    {
        if (node.CapturedVariables.Count == 0)
            return ClosureEmit.StaticTrampoline(ctx, enclosingName, node);

        // A capturing lambda shares this scope's display. Its body is rewritten with displayRef =
        // `this` (arg0 of the trampoline instance method); nested lambdas register on the same
        // display. The lambda's own captured PARAMETERS are copied into the display at entry.
        var thisRef = Synth.Name("this", displayType);
        var inner   = new HoistingClosureRewriter(ctx, thisRef, displayType, captures,
            propertyLocations, displayMethods, enclosingName);
        var body    = (BoundBlockStatement)inner.RewriteStatement(node.Body);

        var paramCopies = new List<BoundStatement>();
        foreach (var p in node.Parameters)
            if (captures.ContainsKey(p.Name))
                paramCopies.Add(Synth.Assign(Synth.Member(thisRef, p.Name, p.Type), Synth.Name(p.Name, p.Type)));
        if (paramCopies.Count > 0)
            body = Synth.Block([.. paramCopies, .. body.Statements]) with { Span = body.Span };

        // A trampoline is an INSTANCE method on the display class; CodeGen's
        // instance-method path treats Parameters[0] as the receiver (drops it with
        // Skip(1), binds it as `self`). So the display receiver must sit at index 0
        // ahead of the lambda's own parameters — otherwise the first real parameter is
        // mistaken for the receiver and eaten, and the emitted arity no longer matches
        // the method-group conversion's call site.
        var trampolineName = ctx.Fresh($"{enclosingName}_lambda");
        displayMethods.Add(new BoundFunctionDeclaration(
            IsPublic: false, Name: trampolineName, TypeParameters: [],
            Parameters: [new BoundParameter("this", displayType, ByRef: false), .. node.Parameters],
            ReturnType: node.ReturnType, Body: body,
            Attributes: [], HasAwait: node.IsAsync, AsyncShape: node.AsyncShape) { Span = node.Span });

        return new BoundMethodGroupConversion(
            trampolineName, node.Parameters.Count,
            ClosureContext.DelegateTypeFor(node.Parameters, node.ReturnType, node.IsAsync, node.AsyncShape),
            Receiver: displayRef) { Span = node.Span };
    }
}

/// Shared emission of a zero-capture lambda as a free static trampoline. Its body is converted as
/// its own scope, so a closure nested inside it gets its own display.
static class ClosureEmit
{
    public static BoundExpression StaticTrampoline(ClosureContext ctx, string enclosingName, BoundFunctionLiteralExpression node)
    {
        var name = ctx.Fresh($"{enclosingName}_staticlambda");
        var body = ctx.ConvertScope(node.Body, name, ClosureContext.ParamNames(node.Parameters));

        ctx.UnitSynth.Add(new BoundFunctionDeclaration(
            IsPublic: false, Name: name, TypeParameters: [],
            Parameters: node.Parameters, ReturnType: node.ReturnType, Body: body,
            Attributes: [], HasAwait: node.IsAsync, AsyncShape: node.AsyncShape) { Span = node.Span });

        return new BoundMethodGroupConversion(
            name, node.Parameters.Count,
            node.IsFunctionPointer
                ? new FunctionPointerType(node.Parameters.Select(p => p.Type).ToList(), node.ReturnType)
                : ClosureContext.DelegateTypeFor(node.Parameters, node.ReturnType, node.IsAsync, node.AsyncShape)) { Span = node.Span };
    }
}
