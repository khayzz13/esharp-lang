using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Drives a <see cref="BoundTreeRewriter"/> over every executable position in a
/// <see cref="BoundProgram"/> — function bodies, instance-method bodies, and (the position
/// the old hand-rolled passes silently skipped) <c>init</c> constructor bodies plus their
/// <c>base(...)</c> / <c>this(...)</c> delegation arguments. A lowering pass that is a pure
/// BoundTree→BoundTree rewrite hands its rewriter here and inherits total coverage; it never
/// re-implements the program/member walk.
/// </summary>
public static class LoweringDriver
{
    public static BoundProgram MapBodies(BoundProgram program, BoundTreeRewriter rw)
        => MapBodies(program, _ => rw);

    /// <summary>
    /// Per-body variant: the rewriter is built fresh for each executable body with that body's
    /// enclosing <em>return type</em> in hand — <c>null</c> for <c>init</c> / <c>base(...)</c> /
    /// <c>this(...)</c> contexts, which have no <c>Result</c> return. Used by passes whose
    /// transform depends on the enclosing signature: notably <see cref="ResultLowering"/>'s
    /// <c>?</c> propagation, which builds a fresh <c>Result</c> of the <em>enclosing function's</em>
    /// return type. The plain <see cref="MapBodies(BoundProgram, BoundTreeRewriter)"/> overload is
    /// the common case (one rewriter, ignores the return type).
    /// </summary>
    public static BoundProgram MapBodies(BoundProgram program, Func<BoundType?, BoundTreeRewriter> make)
    {
        List<BoundCompilationUnit>? units = null;
        for (var i = 0; i < program.Units.Count; i++)
        {
            var unit = program.Units[i];
            var mapped = MapUnit(unit, make);
            if (!ReferenceEquals(mapped, unit) && units is null)
                units = [.. program.Units.Take(i)];
            units?.Add(mapped);
        }
        return units is null ? program : program with { Units = units };
    }

    static BoundCompilationUnit MapUnit(BoundCompilationUnit unit, Func<BoundType?, BoundTreeRewriter> make)
    {
        List<BoundMember>? members = null;
        for (var i = 0; i < unit.Members.Count; i++)
        {
            var m = unit.Members[i];
            var mapped = m switch
            {
                BoundFunctionDeclaration fn   => MapFunction(fn, make(fn.ReturnType)),
                BoundDataDeclaration data     => MapData(data, make),
                BoundStaticFuncDeclaration sf => MapStaticFunc(sf, make),
                BoundNamespaceInitDeclaration init => MapNamespaceInit(init, make(null)),
                BoundNamespaceStateDeclaration state => MapNamespaceState(state, make(state.Field.Type)),
                _                             => m,
            };
            if (!ReferenceEquals(mapped, m) && members is null)
                members = [.. unit.Members.Take(i)];
            members?.Add(mapped);
        }
        return members is null ? unit : unit with { Members = members };
    }

    static BoundDataDeclaration MapData(BoundDataDeclaration data, Func<BoundType?, BoundTreeRewriter> make)
    {
        var changed = false;

        List<BoundFunctionDeclaration>? methods = null;
        for (var i = 0; i < data.InstanceMethods.Count; i++)
        {
            var m = data.InstanceMethods[i];
            var mapped = MapFunction(m, make(m.ReturnType));
            if (!ReferenceEquals(mapped, m) && methods is null)
                methods = [.. data.InstanceMethods.Take(i)];
            methods?.Add(mapped);
        }
        if (methods is not null) changed = true;

        List<BoundInitDeclaration>? inits = null;
        if (data.Inits is { } existingInits)
        {
            var initRw = make(null);
            for (var i = 0; i < existingInits.Count; i++)
            {
                var init = existingInits[i];
                var mapped = MapInit(init, initRw);
                if (!ReferenceEquals(mapped, init) && inits is null)
                    inits = [.. existingInits.Take(i)];
                inits?.Add(mapped);
            }
            if (inits is not null) changed = true;
        }

        if (!changed) return data;
        return data with
        {
            InstanceMethods = methods ?? data.InstanceMethods,
            Inits = inits ?? data.Inits,
        };
    }

    static BoundCompilationUnit MapUnit(BoundCompilationUnit unit, BoundTreeRewriter rw)
    {
        List<BoundMember>? members = null;
        for (var i = 0; i < unit.Members.Count; i++)
        {
            var m = unit.Members[i];
            var mapped = MapMember(m, rw);
            if (!ReferenceEquals(mapped, m) && members is null)
                members = [.. unit.Members.Take(i)];
            members?.Add(mapped);
        }
        return members is null ? unit : unit with { Members = members };
    }

    static BoundMember MapMember(BoundMember member, BoundTreeRewriter rw) => member switch
    {
        BoundFunctionDeclaration fn   => MapFunction(fn, rw),
        BoundDataDeclaration data     => MapData(data, rw),
        BoundStaticFuncDeclaration sf => MapStaticFunc(sf, rw),
        BoundNamespaceInitDeclaration init => MapNamespaceInit(init, rw),
        BoundNamespaceStateDeclaration state => MapNamespaceState(state, rw),
        _ => member,
    };

    // A `static func` host carries free functions whose bodies are executable positions
    // exactly like a namespace function's. Descend into them so every lowering pass
    // reaches match/if-expr/compound-assign/defer inside a `static func` member — the
    // position both MapUnit overloads previously skipped, leaving FEATURE nodes to ICE
    // in CodeGen.
    static BoundStaticFuncDeclaration MapStaticFunc(BoundStaticFuncDeclaration sf, BoundTreeRewriter rw)
    {
        List<BoundFunctionDeclaration>? fns = null;
        for (var i = 0; i < sf.Functions.Count; i++)
        {
            var f = sf.Functions[i];
            var mapped = MapFunction(f, rw);
            if (!ReferenceEquals(mapped, f) && fns is null)
                fns = [.. sf.Functions.Take(i)];
            fns?.Add(mapped);
        }
        return fns is null ? sf : sf with { Functions = fns };
    }

    static BoundStaticFuncDeclaration MapStaticFunc(BoundStaticFuncDeclaration sf, Func<BoundType?, BoundTreeRewriter> make)
    {
        List<BoundFunctionDeclaration>? fns = null;
        for (var i = 0; i < sf.Functions.Count; i++)
        {
            var f = sf.Functions[i];
            var mapped = MapFunction(f, make(f.ReturnType));
            if (!ReferenceEquals(mapped, f) && fns is null)
                fns = [.. sf.Functions.Take(i)];
            fns?.Add(mapped);
        }
        return fns is null ? sf : sf with { Functions = fns };
    }

    static BoundFunctionDeclaration MapFunction(BoundFunctionDeclaration fn, BoundTreeRewriter rw)
    {
        var body = Block(rw, fn.Body);
        return ReferenceEquals(body, fn.Body) ? fn : fn with { Body = body };
    }

    static BoundNamespaceInitDeclaration MapNamespaceInit(BoundNamespaceInitDeclaration init, BoundTreeRewriter rw)
    {
        var body = Block(rw, init.Body);
        return ReferenceEquals(body, init.Body) ? init : init with { Body = body };
    }

    static BoundNamespaceStateDeclaration MapNamespaceState(BoundNamespaceStateDeclaration state, BoundTreeRewriter rw)
    {
        var initializer = state.Field.DefaultValue is null ? null : rw.RewriteExpression(state.Field.DefaultValue);
        var getter = state.ComputedGetter is null ? null : rw.RewriteExpression(state.ComputedGetter);
        var setter = state.SetterBody is null ? null : rw.RewriteExpression(state.SetterBody);
        var field = ReferenceEquals(initializer, state.Field.DefaultValue)
            ? state.Field
            : state.Field with { DefaultValue = initializer };
        return ReferenceEquals(field, state.Field)
            && ReferenceEquals(getter, state.ComputedGetter) && ReferenceEquals(setter, state.SetterBody)
            ? state
            : state with { Field = field, ComputedGetter = getter, SetterBody = setter };
    }

    static BoundDataDeclaration MapData(BoundDataDeclaration data, BoundTreeRewriter rw)
    {
        var changed = false;

        List<BoundFunctionDeclaration>? methods = null;
        for (var i = 0; i < data.InstanceMethods.Count; i++)
        {
            var m = data.InstanceMethods[i];
            var mapped = MapFunction(m, rw);
            if (!ReferenceEquals(mapped, m) && methods is null)
                methods = [.. data.InstanceMethods.Take(i)];
            methods?.Add(mapped);
        }
        if (methods is not null) changed = true;

        List<BoundInitDeclaration>? inits = null;
        if (data.Inits is { } existingInits)
        {
            for (var i = 0; i < existingInits.Count; i++)
            {
                var init = existingInits[i];
                var mapped = MapInit(init, rw);
                if (!ReferenceEquals(mapped, init) && inits is null)
                    inits = [.. existingInits.Take(i)];
                inits?.Add(mapped);
            }
            if (inits is not null) changed = true;
        }

        if (!changed) return data;
        return data with
        {
            InstanceMethods = methods ?? data.InstanceMethods,
            Inits = inits ?? data.Inits,
        };
    }

    static BoundInitDeclaration MapInit(BoundInitDeclaration init, BoundTreeRewriter rw)
    {
        var body = Block(rw, init.Body);
        var baseArgs = MapArgs(init.BaseArguments, rw);
        var thisArgs = MapArgs(init.ThisArguments, rw);
        if (ReferenceEquals(body, init.Body)
            && ReferenceEquals(baseArgs, init.BaseArguments)
            && ReferenceEquals(thisArgs, init.ThisArguments))
            return init;
        return init with { Body = body, BaseArguments = baseArgs, ThisArguments = thisArgs };
    }

    static IReadOnlyList<BoundExpression>? MapArgs(IReadOnlyList<BoundExpression>? args, BoundTreeRewriter rw)
    {
        if (args is null) return null;
        List<BoundExpression>? result = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var mapped = rw.RewriteExpression(a);
            if (!ReferenceEquals(mapped, a) && result is null)
                result = [.. args.Take(i)];
            result?.Add(mapped);
        }
        return result ?? args;
    }

    static BoundBlockStatement Block(BoundTreeRewriter rw, BoundBlockStatement body)
        => (BoundBlockStatement)rw.RewriteStatement(body);
}
