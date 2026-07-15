using System.Globalization;
using System.Text;
using Esharp.FuzzTests.Execution;

namespace Esharp.FuzzTests.Generation;

internal enum RepresentationPin { None, Struct, Class }

internal sealed class GeneratedProgram(ProgramModel model, IReadOnlyList<Island> islands, int expected, int seed)
{
    public ProgramModel Model { get; } = model;
    public IReadOnlyList<Island> Islands { get; } = islands;
    public int Expected { get; } = expected;
    public int Seed { get; } = seed;
    public string ExpectedText => Expected.ToString(CultureInfo.InvariantCulture);

    public CaseRequest BuildRequest(string id, RepresentationPin pin = RepresentationPin.None,
        bool shortenOpcodes = true, bool emitTwice = false, int timeoutMs = 20_000)
        => new(id, RenderFiles(pin), Invoke: true, ShortenOpcodes: shortenOpcodes,
            EmitTwice: emitTwice, TimeoutMs: timeoutMs);

    public IReadOnlyList<SourceFile> RenderFiles(RepresentationPin pin)
    {
        var files = new List<SourceFile> { new("main.es", ProgramRenderer.RenderMain(Model, Islands, pin)) };
        foreach (var island in Islands.Where(i => i.LibDecls is not null))
            files.Add(new SourceFile($"{island.LibNamespace!.ToLowerInvariant()}.es", island.LibDecls!));
        return files;
    }
}

/// Generates a whole program — nominal types, helper functions, feature
/// islands, and a go() body — interprets it for the expected value, and
/// renders it (optionally with every generated `data` pinned [Struct] or
/// [Class], the representation-metamorphism axis).
internal sealed class ProgramGenerator(int seed, GeneratorProfile profile)
{
    readonly Random _rng = new(seed);

    public GeneratedProgram Generate()
    {
        var model = new ProgramModel();
        var generator = new BodyGenerator(_rng, model, profile);

        GenerateDataTypes(model);
        GenerateEnum(model);
        GenerateChoice(model);

        var islands = Islands.Pick(_rng, profile, profile.IslandCount);
        model.GoIsAsync = islands.Any(i => i.Async);

        for (var i = 0; i < profile.HelperCount; i++)
            GenerateHelper(model, generator, i);

        GenerateGo(model, generator, islands);

        var expected = model.Evaluate();
        return new GeneratedProgram(model, islands, expected, seed);
    }

    void GenerateDataTypes(ProgramModel model)
    {
        for (var i = 0; i < profile.DataTypeCount; i++)
        {
            var type = new TypeModel { Name = $"D{i}", Kind = TypeKind.Data };
            var fieldCount = 2 + _rng.Next(3);
            for (var f = 0; f < fieldCount; f++)
            {
                var fieldType = _rng.Next(6) switch
                {
                    0 => TypeRef.Bool,
                    1 => TypeRef.Str,
                    // Nested data fields only over earlier types — no cycles (ES2002).
                    2 when i > 0 => TypeRef.Data(model.Types.First(t => t.Kind == TypeKind.Data)),
                    _ => TypeRef.Int,
                };
                type.Fields.Add(($"f{f}", fieldType));
            }
            model.Types.Add(type);
        }
    }

    void GenerateEnum(ProgramModel model)
    {
        var type = new TypeModel { Name = "En0", Kind = TypeKind.Enum };
        var memberCount = 3 + _rng.Next(3);
        for (var i = 0; i < memberCount; i++)
            type.Members.Add($"e{i}");
        model.Types.Add(type);
    }

    void GenerateChoice(ProgramModel model)
    {
        var type = new TypeModel { Name = "Ch0", Kind = TypeKind.Choice };
        var caseCount = 2 + _rng.Next(2);
        for (var i = 0; i < caseCount; i++)
        {
            var payload = new List<(string, TypeRef)>();
            var payloadCount = _rng.Next(3); // 0, 1, or 2 fields
            for (var p = 0; p < payloadCount; p++)
                payload.Add(($"p{p}", _rng.Next(3) == 0 ? TypeRef.Str : TypeRef.Int));
            type.Cases.Add(($"c{i}", payload));
        }
        model.Types.Add(type);
    }

    void GenerateHelper(ProgramModel model, BodyGenerator generator, int index)
    {
        var paramCount = _rng.Next(4);
        var parameters = new List<(string, TypeRef)>();
        for (var p = 0; p < paramCount; p++)
        {
            var type = _rng.Next(6) switch
            {
                0 => TypeRef.Bool,
                1 => TypeRef.Str,
                2 when model.Types.Any(t => t.Kind == TypeKind.Data)
                    => TypeRef.Data(model.Types.First(t => t.Kind == TypeKind.Data)),
                _ => TypeRef.Int,
            };
            parameters.Add(($"p{index}_{p}", type));
        }
        // A data-typed first parameter makes the function a promoted method.
        if (_rng.Next(3) == 0 && model.Types.Any(t => t.Kind == TypeKind.Data) && parameters.Count > 0)
            parameters[0] = (parameters[0].Item1,
                TypeRef.Data(model.Types.Where(t => t.Kind == TypeKind.Data).Skip(_rng.Next(profile.DataTypeCount)).First()));

        var returnType = _rng.Next(5) switch
        {
            0 => TypeRef.Bool,
            1 => TypeRef.Str,
            _ => TypeRef.Int,
        };

        var helper = new FuncModel { Name = $"h{index}", Params = parameters, ReturnType = returnType };
        var env = new GenEnv();
        foreach (var (name, type) in parameters)
            env.Locals.Add(new GenLocal(name, type, false));

        // A couple of locals, an optional branch, then a definite return.
        var statements = 1 + _rng.Next(3);
        for (var i = 0; i < statements; i++)
            helper.Body.Add(generator.GenDecl(env, 2));
        if (_rng.Next(2) == 0)
        {
            var earlyReturn = new ReturnStmt(generator.GenOf(returnType, env, 2));
            helper.Body.Add(new IfStmt(generator.GenBool(env, 2), [earlyReturn]));
        }
        helper.Body.Add(new ReturnStmt(generator.GenOf(returnType, env, 2)));

        // Registered only after the body exists — helpers reference strictly
        // earlier helpers, so the call graph is acyclic by construction.
        model.Helpers.Add(helper);
    }

    void GenerateGo(ProgramModel model, BodyGenerator generator, IReadOnlyList<Island> islands)
    {
        var env = new GenEnv();
        model.GoBody.Add(new DeclStmt("acc", true, new IntLit(0)));
        env.Locals.Add(new GenLocal("acc", TypeRef.Int, true));

        var pendingIslands = new Queue<Island>(islands);
        for (var i = 0; i < profile.GoStatements; i++)
        {
            // Spread island folds through the body so their holes see a
            // progressively richer environment.
            if (pendingIslands.Count > 0 && _rng.Next(3) == 0)
            {
                var island = pendingIslands.Dequeue();
                model.GoBody.Add(BodyGenerator.Fold("acc", island.MakeCall(generator, env)));
                continue;
            }

            var stmt = _rng.Next(10) switch
            {
                <= 3 => generator.GenDecl(env, profile.ExprDepth),
                <= 6 => generator.GenEffect(env, "acc", profile.ExprDepth),
                _ => generator.GenControl(env, "acc", profile.ExprDepth),
            };
            model.GoBody.Add(stmt);
        }

        while (pendingIslands.Count > 0)
            model.GoBody.Add(BodyGenerator.Fold("acc", pendingIslands.Dequeue().MakeCall(generator, env)));

        model.GoBody.Add(new ReturnStmt(new LocalRef("acc", TypeRef.Int)));
    }
}

internal static class ProgramRenderer
{
    public static string RenderMain(ProgramModel model, IReadOnlyList<Island> islands, RepresentationPin pin)
    {
        var sb = new StringBuilder(1 << 14);
        sb.Append("namespace Test\n");
        foreach (var island in islands.Where(i => i.LibNamespace is not null))
            sb.Append($"using \"{island.LibNamespace}\"\n");
        sb.Append('\n');

        foreach (var type in model.Types)
        {
            RenderType(sb, type, pin);
            sb.Append('\n');
        }

        foreach (var island in islands.Where(i => i.MainDecls.Length > 0))
            sb.Append(island.MainDecls).Append("\n\n");

        foreach (var helper in model.Helpers)
        {
            RenderFunc(sb, helper);
            sb.Append('\n');
        }

        sb.Append("func go() -> int {\n");
        foreach (var stmt in model.GoBody)
            stmt.Render(sb, 1);
        sb.Append("}\n");
        return sb.ToString();
    }

    static void RenderType(StringBuilder sb, TypeModel type, RepresentationPin pin)
    {
        switch (type.Kind)
        {
            case TypeKind.Data:
                if (pin == RepresentationPin.Struct) sb.Append("[Struct]\n");
                if (pin == RepresentationPin.Class) sb.Append("[Class]\n");
                sb.Append("struct ").Append(type.Name).Append(" {\n");
                foreach (var (name, fieldType) in type.Fields)
                    sb.Append("    ").Append(name).Append(": ").Append(fieldType.Render()).Append('\n');
                sb.Append("}\n");
                break;
            case TypeKind.Enum:
                sb.Append("enum ").Append(type.Name).Append(" {\n");
                foreach (var member in type.Members)
                    sb.Append("    ").Append(member).Append('\n');
                sb.Append("}\n");
                break;
            case TypeKind.Choice:
                sb.Append("union ").Append(type.Name).Append(" {\n");
                foreach (var (caseName, payload) in type.Cases)
                {
                    sb.Append("    ").Append(caseName);
                    if (payload.Count > 0)
                        sb.Append('(').Append(string.Join(", ", payload.Select(p => $"{p.Name}: {p.Type.Render()}"))).Append(')');
                    sb.Append('\n');
                }
                sb.Append("}\n");
                break;
            default:
                throw new InvalidOperationException($"RenderType {type.Kind}");
        }
    }

    static void RenderFunc(StringBuilder sb, FuncModel func)
    {
        sb.Append("func ").Append(func.Name).Append('(');
        sb.Append(string.Join(", ", func.Params.Select(p => $"{p.Name}: {p.Type.Render()}")));
        sb.Append(") -> ").Append(func.ReturnType.Render()).Append(" {\n");
        foreach (var stmt in func.Body)
            stmt.Render(sb, 1);
        sb.Append("}\n");
    }
}
