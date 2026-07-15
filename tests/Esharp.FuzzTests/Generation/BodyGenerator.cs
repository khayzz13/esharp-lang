namespace Esharp.FuzzTests.Generation;

internal sealed record GenLocal(string Name, TypeRef Type, bool Mutable, int ListLength = -1)
{
    public LocalRef Ref => new(Name, Type);
}

internal sealed class GenEnv
{
    public List<GenLocal> Locals { get; } = [];
    public IReadOnlyList<GenLocal> OfKind(TypeKind kind) => Locals.Where(l => l.Type.Kind == kind).ToList();
    public IReadOnlyList<GenLocal> MutableInts() => Locals.Where(l => l.Type.Kind == TypeKind.Int && l.Mutable).ToList();
}

/// Free-form typed statement/expression generation. Every production is
/// type-correct against the environment by construction, and every node the
/// generator emits self-evaluates — that is what makes the construction oracle
/// possible. Division appears only with non-zero literal divisors (excluding
/// -1, so MinValue/-1 can't trap); everything else is unrestricted, including
/// wrap-around arithmetic and boundary literals.
internal sealed class BodyGenerator(Random rng, ProgramModel program, GeneratorProfile profile)
{
    int _nameCounter;
    static readonly int[] SafeDivisors = [2, 3, 5, 7, 9, -2, -3, -7];
    static readonly int[] BoundaryLiterals = [0, 1, -1, int.MaxValue, int.MinValue, 255, -128, 65536];
    static readonly string[] StringAtoms = ["", "a", "ab", "xyz", "hello", "p_q", "Zw"];

    public string NextName() => $"v{_nameCounter++}";

    // ── Expressions ─────────────────────────────────────────────────────────

    public Expr GenInt(GenEnv env, int depth)
    {
        if (depth <= 0)
            return IntLeaf(env);

        switch (rng.Next(14))
        {
            case 0 or 1 or 2:
            {
                var op = (IntOp)rng.Next(3); // Add/Sub/Mul
                return new IntBin(op, GenInt(env, depth - 1), GenInt(env, depth - 1));
            }
            case 3:
            {
                var op = rng.Next(2) == 0 ? IntOp.Div : IntOp.Mod;
                return new IntBin(op, GenInt(env, depth - 1), new IntLit(SafeDivisors[rng.Next(SafeDivisors.Length)]));
            }
            case 4:
                return new Ternary(GenBool(env, depth - 1), GenInt(env, depth - 1), GenInt(env, depth - 1));
            case 5:
            {
                var read = FieldReadOf(env, TypeKind.Int, depth);
                if (read is not null) return read;
                goto default;
            }
            case 6:
            {
                var lists = env.Locals.Where(l => l.Type.Kind == TypeKind.ListInt && l.ListLength > 0).ToList();
                if (lists.Count > 0)
                {
                    var list = lists[rng.Next(lists.Count)];
                    if (rng.Next(3) == 0)
                        return new ListCount(list.Ref);
                    var fromEnd = rng.Next(4) == 0;
                    var index = fromEnd ? 1 + rng.Next(list.ListLength) : rng.Next(list.ListLength);
                    return new ListIndex(list.Ref, index, fromEnd);
                }
                goto default;
            }
            case 7:
            {
                var str = GenStr(env, Math.Min(depth - 1, 2));
                return new StrLen(str);
            }
            case 8:
            {
                var call = HelperCallOf(env, TypeKind.Int, depth);
                if (call is not null) return call;
                goto default;
            }
            default:
                return IntLeaf(env);
        }
    }

    Expr IntLeaf(GenEnv env)
    {
        var ints = env.OfKind(TypeKind.Int);
        if (ints.Count > 0 && rng.Next(2) == 0)
            return ints[rng.Next(ints.Count)].Ref;
        if (rng.Next(20) == 0)
            return new IntLit(BoundaryLiterals[rng.Next(BoundaryLiterals.Length)]);
        return new IntLit(rng.Next(-40, 41));
    }

    public Expr GenBool(GenEnv env, int depth)
    {
        if (depth <= 0)
        {
            var bools = env.OfKind(TypeKind.Bool);
            if (bools.Count > 0 && rng.Next(2) == 0)
                return bools[rng.Next(bools.Count)].Ref;
            return new BoolLit(rng.Next(2) == 0);
        }

        switch (rng.Next(8))
        {
            case 0 or 1 or 2:
                return new IntCmp((CmpOp)rng.Next(6), GenInt(env, depth - 1), GenInt(env, depth - 1));
            case 3:
                return new BoolBin(rng.Next(2) == 0, GenBool(env, depth - 1), GenBool(env, depth - 1));
            case 4:
                return new NotExpr(GenBool(env, depth - 1));
            case 5:
                return new StrEq(GenStr(env, Math.Min(depth - 1, 1)), GenStr(env, Math.Min(depth - 1, 1)));
            case 6:
            {
                var read = FieldReadOf(env, TypeKind.Bool, depth);
                if (read is not null) return read;
                goto default;
            }
            default:
            {
                var call = HelperCallOf(env, TypeKind.Bool, depth);
                if (call is not null) return call;
                return new BoolLit(rng.Next(2) == 0);
            }
        }
    }

    public Expr GenStr(GenEnv env, int depth)
    {
        if (depth <= 0)
        {
            var strs = env.OfKind(TypeKind.Str);
            if (strs.Count > 0 && rng.Next(2) == 0)
                return strs[rng.Next(strs.Count)].Ref;
            return new StrLit(StringAtoms[rng.Next(StringAtoms.Length)]);
        }

        switch (rng.Next(6))
        {
            case 0 or 1:
                return new StrConcat(GenStr(env, depth - 1), GenStr(env, depth - 1));
            case 2:
            {
                // "{a}txt{b}" — interpolation holes are int/string locals or
                // shallow expressions; text segments never contain braces.
                var partCount = 1 + rng.Next(2);
                var segments = new List<string> { StringAtoms[rng.Next(StringAtoms.Length)] };
                var parts = new List<Expr>();
                for (var i = 0; i < partCount; i++)
                {
                    parts.Add(rng.Next(2) == 0 ? GenInt(env, 1) : GenStr(env, 0));
                    segments.Add(StringAtoms[rng.Next(StringAtoms.Length)]);
                }
                return new StrInterp(segments, parts);
            }
            case 3:
                return new Ternary(GenBool(env, depth - 1), GenStr(env, depth - 1), GenStr(env, depth - 1));
            case 4:
            {
                var read = FieldReadOf(env, TypeKind.Str, depth);
                if (read is not null) return read;
                goto default;
            }
            default:
            {
                var call = HelperCallOf(env, TypeKind.Str, depth);
                if (call is not null) return call;
                return new StrLit(StringAtoms[rng.Next(StringAtoms.Length)]);
            }
        }
    }

    /// A value of a generated data type: an existing local, a composite
    /// literal, or a with-update over either.
    public Expr GenData(TypeModel type, GenEnv env, int depth)
    {
        var existing = env.Locals.Where(l => l.Type.Kind == TypeKind.Data && l.Type.Model == type).ToList();
        if (existing.Count > 0 && depth > 0 && rng.Next(2) == 0)
        {
            var target = existing[rng.Next(existing.Count)].Ref;
            if (rng.Next(2) == 0)
            {
                var field = type.Fields[rng.Next(type.Fields.Count)];
                return new WithUpdate(target, field.Name, GenOf(field.Type, env, depth - 1));
            }
            return target;
        }
        return new CompositeData(type, type.Fields.Select(f => GenOf(f.Type, env, Math.Max(0, depth - 1))).ToList());
    }

    public Expr GenOf(TypeRef type, GenEnv env, int depth) => type.Kind switch
    {
        TypeKind.Int => GenInt(env, depth),
        TypeKind.Bool => GenBool(env, depth),
        TypeKind.Str => GenStr(env, depth),
        TypeKind.Data => GenData(type.Model!, env, depth),
        TypeKind.Enum => new EnumLit(type.Model!, type.Model!.Members[rng.Next(type.Model!.Members.Count)]),
        TypeKind.Choice => GenChoice(type.Model!, env, depth),
        _ => throw new InvalidOperationException($"GenOf {type.Kind}"),
    };

    public Expr GenChoice(TypeModel type, GenEnv env, int depth)
    {
        var caseIndex = rng.Next(type.Cases.Count);
        var args = type.Cases[caseIndex].Payload
            .Select(p => GenOf(p.Type, env, Math.Max(0, depth - 1)))
            .ToList();
        return new ChoiceMake(type, caseIndex, args);
    }

    Expr? FieldReadOf(GenEnv env, TypeKind kind, int depth)
    {
        var candidates = new List<Expr>();
        foreach (var local in env.Locals)
        {
            var model = local.Type.Kind is TypeKind.Data or TypeKind.Ptr ? local.Type.Model : null;
            if (model is null)
                continue;
            foreach (var field in model.Fields)
            {
                if (field.Type.Kind == kind)
                    candidates.Add(new FieldRead(local.Ref, field.Name, field.Type));
                else if (field.Type.Kind == TypeKind.Data && local.Type.Kind == TypeKind.Data)
                    foreach (var nested in field.Type.Model!.Fields.Where(n => n.Type.Kind == kind))
                        candidates.Add(new FieldRead(new FieldRead(local.Ref, field.Name, field.Type), nested.Name, nested.Type));
            }
        }
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    Expr? HelperCallOf(GenEnv env, TypeKind returnKind, int depth)
    {
        var helpers = program.Helpers.Where(h => h.ReturnType.Kind == returnKind).ToList();
        if (helpers.Count == 0 || depth <= 1)
            return null;
        var helper = helpers[rng.Next(helpers.Count)];
        var args = helper.Params.Select(p => GenOf(p.Type, env, depth - 2)).ToList();
        return new HelperCall(helper, args);
    }

    // ── Statements ──────────────────────────────────────────────────────────

    /// One declaration statement, registering the new local in the env. The
    /// initializer is generated BEFORE the local is registered — a binding must
    /// never reference itself.
    public Stmt GenDecl(GenEnv env, int depth)
    {
        var name = NextName();
        switch (rng.Next(10))
        {
            case 0 or 1 or 2:
            {
                var mutable = rng.Next(2) == 0;
                var init = GenInt(env, depth);
                env.Locals.Add(new GenLocal(name, TypeRef.Int, mutable));
                return new DeclStmt(name, mutable, init);
            }
            case 3:
            {
                var init = GenBool(env, depth);
                env.Locals.Add(new GenLocal(name, TypeRef.Bool, false));
                return new DeclStmt(name, false, init);
            }
            case 4:
            {
                var init = GenStr(env, depth);
                env.Locals.Add(new GenLocal(name, TypeRef.Str, false));
                return new DeclStmt(name, false, init);
            }
            case 5 when program.Types.Any(t => t.Kind == TypeKind.Data):
            {
                var type = PickType(TypeKind.Data);
                var init = GenData(type, env, depth);
                env.Locals.Add(new GenLocal(name, TypeRef.Data(type), false));
                return new DeclStmt(name, false, init);
            }
            case 6:
            {
                var length = 1 + rng.Next(5);
                var items = Enumerable.Range(0, length).Select(_ => GenInt(env, Math.Max(0, depth - 1))).ToList();
                env.Locals.Add(new GenLocal(name, TypeRef.ListInt, false, length));
                return new DeclStmt(name, false, new ListLit(items));
            }
            case 7 when program.Types.Any(t => t.Kind == TypeKind.Choice):
            {
                var type = PickType(TypeKind.Choice);
                var init = GenChoice(type, env, depth);
                env.Locals.Add(new GenLocal(name, TypeRef.Choice(type), false));
                return new DeclStmt(name, false, init);
            }
            case 8 when program.Types.Any(t => t.Kind == TypeKind.Enum):
            {
                var type = PickType(TypeKind.Enum);
                var init = new EnumLit(type, type.Members[rng.Next(type.Members.Count)]);
                env.Locals.Add(new GenLocal(name, TypeRef.Enum(type), false));
                return new DeclStmt(name, false, init);
            }
            case 9 when program.Types.Any(t => t.Kind == TypeKind.Data):
            {
                var type = PickType(TypeKind.Data);
                type.MutatedViaPtr = true;
                var args = type.Fields.Select(f => GenOf(f.Type, env, Math.Max(0, depth - 1))).ToList();
                env.Locals.Add(new GenLocal(name, TypeRef.Ptr(type), true));
                return new DeclStmt(name, true, new NewPtr(type, args), TypeRef.Ptr(type));
            }
            default:
            {
                var init = GenInt(env, depth);
                env.Locals.Add(new GenLocal(name, TypeRef.Int, true));
                return new DeclStmt(name, true, init);
            }
        }
    }

    /// A small effect statement: assignment, compound assignment, or a fold
    /// into the accumulator.
    public Stmt GenEffect(GenEnv env, string accName, int depth)
    {
        var mutables = env.MutableInts();
        switch (rng.Next(6))
        {
            case 0 when mutables.Count > 0:
            {
                var target = mutables[rng.Next(mutables.Count)];
                return new AssignStmt(target.Name, GenInt(env, depth));
            }
            case 1 or 2 when mutables.Count > 0:
            {
                var target = mutables[rng.Next(mutables.Count)];
                var op = rng.Next(3) switch { 0 => IntOp.Add, 1 => IntOp.Sub, _ => IntOp.Mul };
                return new CompoundStmt(target.Name, op, GenInt(env, Math.Max(0, depth - 1)));
            }
            case 3:
            {
                var ptrs = env.Locals.Where(l => l.Type.Kind == TypeKind.Ptr).ToList();
                if (ptrs.Count > 0)
                {
                    var ptr = ptrs[rng.Next(ptrs.Count)];
                    var intFields = ptr.Type.Model!.Fields.Where(f => f.Type.Kind == TypeKind.Int).ToList();
                    if (intFields.Count > 0)
                        return new PtrFieldAssign(ptr.Name, intFields[rng.Next(intFields.Count)].Name, GenInt(env, depth));
                }
                goto default;
            }
            default:
                return Fold(accName, GenInt(env, depth));
        }
    }

    /// `acc = acc * 31 + expr` — the canonical observation fold.
    public static Stmt Fold(string accName, Expr value)
        => new AssignStmt(accName, new IntBin(IntOp.Add,
            new IntBin(IntOp.Mul, new LocalRef(accName, TypeRef.Int), new IntLit(31)), value));

    /// One control-flow statement folding observations into `accName`.
    public Stmt GenControl(GenEnv env, string accName, int depth)
    {
        Stmt IfFallback()
        {
            var then = SmallBlock(env, accName, depth);
            var hasElse = rng.Next(2) == 0;
            return new IfStmt(GenBool(env, depth), then, hasElse ? SmallBlock(env, accName, depth) : null);
        }

        switch (rng.Next(6))
        {
            case 0 or 1:
                return IfFallback();
            case 2:
            {
                var counter = NextName();
                var body = SmallBlock(env, accName, Math.Max(1, depth - 1));
                return new CountedWhile(counter, rng.Next(profile.LoopBoundMax + 1), body);
            }
            case 3:
            {
                var lists = env.Locals.Where(l => l.Type.Kind == TypeKind.ListInt && l.ListLength > 0).ToList();
                if (lists.Count == 0) return IfFallback();
                var item = NextName();
                var list = lists[rng.Next(lists.Count)];
                var itemEnv = Snapshot(env);
                itemEnv.Locals.Add(new GenLocal(item, TypeRef.Int, false));
                var body = new List<Stmt> { Fold(accName, GenInt(itemEnv, Math.Max(1, depth - 1))) };
                return new ForInList(item, list.Ref, body);
            }
            case 4:
            {
                var choices = env.Locals.Where(l => l.Type.Kind == TypeKind.Choice).ToList();
                if (choices.Count == 0) return IfFallback();
                var scrutinee = choices[rng.Next(choices.Count)];
                return GenChoiceMatch(env, scrutinee, accName, depth);
            }
            default:
            {
                var enums = env.Locals.Where(l => l.Type.Kind == TypeKind.Enum).ToList();
                if (enums.Count == 0) return IfFallback();
                var scrutinee = enums[rng.Next(enums.Count)];
                var model = scrutinee.Type.Model!;
                var arms = new List<(string, IReadOnlyList<Stmt>)>();
                var covered = rng.Next(2) == 0 ? model.Members.Count : 1 + rng.Next(model.Members.Count);
                for (var i = 0; i < covered; i++)
                    arms.Add((model.Members[i], [Fold(accName, GenInt(env, Math.Max(1, depth - 1)))]));
                IReadOnlyList<Stmt>? @default = covered < model.Members.Count
                    ? [Fold(accName, GenInt(env, Math.Max(1, depth - 1)))]
                    : null;
                return new MatchEnumStmt(scrutinee.Ref, arms, @default);
            }
        }
    }

    Stmt GenChoiceMatch(GenEnv env, GenLocal scrutinee, string accName, int depth)
    {
        var model = scrutinee.Type.Model!;
        var arms = new List<(int, string?, IReadOnlyList<Stmt>)>();
        for (var caseIndex = 0; caseIndex < model.Cases.Count; caseIndex++)
        {
            var payload = model.Cases[caseIndex].Payload;
            if (payload.Count == 0)
            {
                arms.Add((caseIndex, null, [Fold(accName, GenInt(env, Math.Max(1, depth - 1)))]));
                continue;
            }

            var bind = NextName();
            var armEnv = Snapshot(env);
            if (payload.Count == 1)
            {
                armEnv.Locals.Add(new GenLocal(bind, payload[0].Type, false));
            }
            else
            {
                // Case-view binding: bind.field projects each payload by name.
                var view = new TypeModel { Name = model.Name, Kind = TypeKind.Data };
                view.Fields.AddRange(payload);
                armEnv.Locals.Add(new GenLocal(bind, TypeRef.Data(view), false));
            }
            arms.Add((caseIndex, bind, [Fold(accName, GenInt(armEnv, Math.Max(1, depth - 1)))]));
        }
        return new MatchChoiceStmt(scrutinee.Ref, arms);
    }

    List<Stmt> SmallBlock(GenEnv env, string accName, int depth)
    {
        var inner = Snapshot(env);
        var body = new List<Stmt>();
        var count = 1 + rng.Next(3);
        for (var i = 0; i < count; i++)
            body.Add(GenEffect(inner, accName, Math.Max(1, depth - 1)));
        return body;
    }

    /// Block-scoped view: inner blocks may *read* outer locals, but locals they
    /// declare must not leak back out. Mutation still flows through because
    /// assignment targets are outer names.
    static GenEnv Snapshot(GenEnv env)
    {
        var copy = new GenEnv();
        copy.Locals.AddRange(env.Locals);
        return copy;
    }

    TypeModel PickType(TypeKind kind)
    {
        var matching = program.Types.Where(t => t.Kind == kind).ToList();
        return matching[rng.Next(matching.Count)];
    }
}
