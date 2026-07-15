using System.Globalization;
using System.Text;

namespace Esharp.FuzzTests.Generation;

// ─────────────────────────────────────────────────────────────────────────────
// The self-evaluating program model: a typed AST that renders to E# source AND
// interprets to a value. The interpreter IS the construction oracle — the
// generator only ever builds programs it can evaluate itself, so every
// generated program carries a known answer with no second compiler needed.
// Semantics mirrored deliberately: int is wrapping int32 (IL add/sub/mul),
// && and || short-circuit, division appears only with generator-guarded
// divisors, data is value-semantic (copied), *T is shared-box identity.
// ─────────────────────────────────────────────────────────────────────────────

internal enum TypeKind { Int, Bool, Str, Data, Ptr, ListInt, Enum, Choice }

internal sealed record TypeRef(TypeKind Kind, TypeModel? Model = null)
{
    public static readonly TypeRef Int = new(TypeKind.Int);
    public static readonly TypeRef Bool = new(TypeKind.Bool);
    public static readonly TypeRef Str = new(TypeKind.Str);
    public static readonly TypeRef ListInt = new(TypeKind.ListInt);
    public static TypeRef Data(TypeModel m) => new(TypeKind.Data, m);
    public static TypeRef Ptr(TypeModel m) => new(TypeKind.Ptr, m);
    public static TypeRef Enum(TypeModel m) => new(TypeKind.Enum, m);
    public static TypeRef Choice(TypeModel m) => new(TypeKind.Choice, m);

    public string Render() => Kind switch
    {
        TypeKind.Int => "int",
        TypeKind.Bool => "bool",
        TypeKind.Str => "string",
        TypeKind.ListInt => "List<int>",
        TypeKind.Data or TypeKind.Enum or TypeKind.Choice => Model!.Name,
        TypeKind.Ptr => "*" + Model!.Name,
        _ => throw new InvalidOperationException(),
    };
}

/// A generated nominal type: data (fields), enum (members), or choice (cases).
internal sealed class TypeModel
{
    public required string Name { get; init; }
    public required TypeKind Kind { get; init; }
    public List<(string Name, TypeRef Type)> Fields { get; } = [];          // data
    public List<string> Members { get; } = [];                              // enum
    public List<(string Name, List<(string Name, TypeRef Type)> Payload)> Cases { get; } = [];  // choice
    /// Render fields as `var` so the program may assign through *T.
    public bool MutatedViaPtr { get; set; }
}

// ── Values ───────────────────────────────────────────────────────────────────

internal abstract record Value
{
    public abstract string Show();
}

internal sealed record VInt(int N) : Value
{
    public override string Show() => N.ToString(CultureInfo.InvariantCulture);
}

internal sealed record VBool(bool B) : Value
{
    public override string Show() => B ? "True" : "False";
}

internal sealed record VStr(string S) : Value
{
    public override string Show() => S;
}

internal sealed record VList(IReadOnlyList<Value> Items) : Value
{
    public override string Show() => "[" + string.Join(",", Items.Select(i => i.Show())) + "]";
}

internal sealed record VEnum(TypeModel Type, string Member) : Value
{
    public override string Show() => $"{Type.Name}.{Member}";
}

internal sealed record VChoice(TypeModel Type, int CaseIndex, IReadOnlyList<Value> Payload) : Value
{
    public override string Show() => $"{Type.Name}.{Type.Cases[CaseIndex].Name}";
}

/// Value-semantic data: copied on binding/with; never mutated in place.
internal sealed record VData(TypeModel Type, IReadOnlyDictionary<string, Value> FieldValues) : Value
{
    public override string Show() => Type.Name;
}

/// Shared mutable box behind *T: identity semantics, aliases see writes.
internal sealed class PtrBox(TypeModel type, Dictionary<string, Value> fields)
{
    public TypeModel Type { get; } = type;
    public Dictionary<string, Value> Fields { get; } = fields;
}

internal sealed record VPtr(PtrBox Box) : Value
{
    public override string Show() => "*" + Box.Type.Name;
}

// ── Interpreter machinery ────────────────────────────────────────────────────

internal sealed class RuntimeBudget(int maxSteps = 2_000_000)
{
    int _steps;
    public void Step()
    {
        if (++_steps > maxSteps)
            throw new InvalidOperationException("Interpreter step budget exceeded — generator produced a runaway program.");
    }
}

internal sealed class Scope(ProgramModel program, RuntimeBudget budget)
{
    public ProgramModel Program { get; } = program;
    public RuntimeBudget Budget { get; } = budget;
    public Dictionary<string, Value> Locals { get; } = [];

    public Scope NewFrame() => new(Program, Budget);
}

internal sealed class ReturnSignal(Value value) : Exception
{
    public Value Value { get; } = value;
}

internal sealed class FuncModel
{
    public required string Name { get; init; }
    public required List<(string Name, TypeRef Type)> Params { get; init; }
    public required TypeRef ReturnType { get; init; }
    public List<Stmt> Body { get; } = [];
    /// First parameter is a data type → callable only as `recv.name(rest)`.
    public bool Promoted => Params.Count > 0 && Params[0].Type.Kind == TypeKind.Data;

    public Value Invoke(Scope caller, IReadOnlyList<Value> args)
    {
        caller.Budget.Step();
        var frame = caller.NewFrame();
        for (var i = 0; i < Params.Count; i++)
            frame.Locals[Params[i].Name] = args[i];
        try
        {
            foreach (var stmt in Body)
                stmt.Exec(frame);
        }
        catch (ReturnSignal ret)
        {
            return ret.Value;
        }
        throw new InvalidOperationException($"Generated helper {Name} fell off the end without returning.");
    }
}

/// Everything the generator produced for one program: nominal types, helper
/// functions, island declaration text, and the body of go().
internal sealed class ProgramModel
{
    public List<TypeModel> Types { get; } = [];
    public List<FuncModel> Helpers { get; } = [];
    public List<string> IslandDeclarations { get; } = [];
    public List<string> LibDeclarations { get; } = [];
    public string? LibNamespace { get; set; }
    public List<Stmt> GoBody { get; } = [];
    public bool GoIsAsync { get; set; }

    public FuncModel Helper(string name) => Helpers.First(h => h.Name == name);

    public int Evaluate()
    {
        var scope = new Scope(this, new RuntimeBudget());
        try
        {
            foreach (var stmt in GoBody)
                stmt.Exec(scope);
        }
        catch (ReturnSignal ret)
        {
            return ((VInt)ret.Value).N;
        }
        throw new InvalidOperationException("go() fell off the end without returning.");
    }
}

// ── Expressions ──────────────────────────────────────────────────────────────

internal abstract record Expr(TypeRef Type)
{
    public abstract Value Eval(Scope s);
    public abstract void Render(StringBuilder sb);

    public string Source
    {
        get { var sb = new StringBuilder(); Render(sb); return sb.ToString(); }
    }

    protected static int I(Scope s, Expr e) => ((VInt)e.Eval(s)).N;
    protected static bool B(Scope s, Expr e) => ((VBool)e.Eval(s)).B;
    protected static string S(Scope s, Expr e) => ((VStr)e.Eval(s)).S;

    /// Render a receiver that's about to be followed by `.member` / `[index]`.
    /// A brace-leading expression (`T { … }`, `new T { … }`, `x with { … }`)
    /// is ambiguous with a block when it lands in statement-condition position,
    /// so such receivers are parenthesized.
    protected static void RenderReceiver(StringBuilder sb, Expr target)
    {
        var needsParens = target is CompositeData or WithUpdate or NewPtr;
        if (needsParens) sb.Append('(');
        target.Render(sb);
        if (needsParens) sb.Append(')');
    }
}

internal sealed record IntLit(int N) : Expr(TypeRef.Int)
{
    public override Value Eval(Scope s) => new VInt(N);
    public override void Render(StringBuilder sb)
    {
        // Negative literals parenthesized so `a - -3` never renders as `a --3`.
        // MinValue has no negatable literal form (2147483648 overflows int_lit),
        // so it renders as an expression.
        if (N == int.MinValue) sb.Append("(0 - 2147483647 - 1)");
        else if (N < 0) sb.Append('(').Append(N).Append(')');
        else sb.Append(N);
    }
}

internal sealed record BoolLit(bool Bv) : Expr(TypeRef.Bool)
{
    public override Value Eval(Scope s) => new VBool(Bv);
    public override void Render(StringBuilder sb) => sb.Append(Bv ? "true" : "false");
}

internal sealed record StrLit(string Sv) : Expr(TypeRef.Str)
{
    public override Value Eval(Scope s) => new VStr(Sv);
    public override void Render(StringBuilder sb) => sb.Append('"').Append(Sv).Append('"');
}

internal sealed record LocalRef(string Name, TypeRef RefType) : Expr(RefType)
{
    public override Value Eval(Scope s) => s.Locals[Name];
    public override void Render(StringBuilder sb) => sb.Append(Name);
}

internal enum IntOp { Add, Sub, Mul, Div, Mod }

internal sealed record IntBin(IntOp Op, Expr L, Expr R) : Expr(TypeRef.Int)
{
    public override Value Eval(Scope s)
    {
        s.Budget.Step();
        int l = I(s, L), r = I(s, R);
        return new VInt(Op switch
        {
            IntOp.Add => unchecked(l + r),
            IntOp.Sub => unchecked(l - r),
            IntOp.Mul => unchecked(l * r),
            IntOp.Div => l / r,
            IntOp.Mod => l % r,
            _ => throw new InvalidOperationException(),
        });
    }

    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        L.Render(sb);
        sb.Append(Op switch
        {
            IntOp.Add => " + ", IntOp.Sub => " - ", IntOp.Mul => " * ",
            IntOp.Div => " / ", _ => " % ",
        });
        R.Render(sb);
        sb.Append(')');
    }
}

internal enum CmpOp { Lt, Gt, Le, Ge, Eq, Ne }

internal sealed record IntCmp(CmpOp Op, Expr L, Expr R) : Expr(TypeRef.Bool)
{
    public override Value Eval(Scope s)
    {
        int l = I(s, L), r = I(s, R);
        return new VBool(Op switch
        {
            CmpOp.Lt => l < r, CmpOp.Gt => l > r, CmpOp.Le => l <= r,
            CmpOp.Ge => l >= r, CmpOp.Eq => l == r, _ => l != r,
        });
    }

    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        L.Render(sb);
        sb.Append(Op switch
        {
            CmpOp.Lt => " < ", CmpOp.Gt => " > ", CmpOp.Le => " <= ",
            CmpOp.Ge => " >= ", CmpOp.Eq => " == ", _ => " != ",
        });
        R.Render(sb);
        sb.Append(')');
    }
}

internal sealed record BoolBin(bool IsAnd, Expr L, Expr R) : Expr(TypeRef.Bool)
{
    public override Value Eval(Scope s)
        => new VBool(IsAnd ? B(s, L) && B(s, R) : B(s, L) || B(s, R));

    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        L.Render(sb);
        sb.Append(IsAnd ? " && " : " || ");
        R.Render(sb);
        sb.Append(')');
    }
}

internal sealed record NotExpr(Expr E) : Expr(TypeRef.Bool)
{
    public override Value Eval(Scope s) => new VBool(!B(s, E));
    public override void Render(StringBuilder sb)
    {
        sb.Append("!(");
        E.Render(sb);
        sb.Append(')');
    }
}

internal sealed record Ternary(Expr Cond, Expr Then, Expr Else) : Expr(Then.Type)
{
    public override Value Eval(Scope s) => B(s, Cond) ? Then.Eval(s) : Else.Eval(s);
    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        Cond.Render(sb);
        sb.Append(" ? ");
        Then.Render(sb);
        sb.Append(" : ");
        Else.Render(sb);
        sb.Append(')');
    }
}

internal sealed record StrConcat(Expr L, Expr R) : Expr(TypeRef.Str)
{
    public override Value Eval(Scope s) => new VStr(S(s, L) + S(s, R));
    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        L.Render(sb);
        sb.Append(" + ");
        R.Render(sb);
        sb.Append(')');
    }
}

internal sealed record StrEq(Expr L, Expr R) : Expr(TypeRef.Bool)
{
    public override Value Eval(Scope s) => new VBool(string.Equals(S(s, L), S(s, R), StringComparison.Ordinal));
    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        L.Render(sb);
        sb.Append(" == ");
        R.Render(sb);
        sb.Append(')');
    }
}

internal sealed record StrLen(Expr E) : Expr(TypeRef.Int)
{
    public override Value Eval(Scope s) => new VInt(S(s, E).Length);
    public override void Render(StringBuilder sb)
    {
        RenderReceiver(sb, E);
        sb.Append(".Length");
    }
}

/// `"text{expr}text{expr}…"` — Segments has one more text part than Parts.
internal sealed record StrInterp(IReadOnlyList<string> Segments, IReadOnlyList<Expr> Parts) : Expr(TypeRef.Str)
{
    public override Value Eval(Scope s)
    {
        var sb = new StringBuilder(Segments[0]);
        for (var i = 0; i < Parts.Count; i++)
        {
            var v = Parts[i].Eval(s);
            sb.Append(v switch
            {
                VInt n => n.N.ToString(CultureInfo.InvariantCulture),
                VStr str => str.S,
                _ => v.Show(),
            });
            sb.Append(Segments[i + 1]);
        }
        return new VStr(sb.ToString());
    }

    public override void Render(StringBuilder sb)
    {
        sb.Append('"').Append(Segments[0]);
        for (var i = 0; i < Parts.Count; i++)
        {
            // The hole rule: `{` opens a hole only before letter/_/(/!. A bare
            // digit (`{42}`) or string literal (`{"x"}`) stays literal text, so
            // every hole is parenthesized to guarantee it opens.
            sb.Append("{(");
            Parts[i].Render(sb);
            sb.Append(")}").Append(Segments[i + 1]);
        }
        sb.Append('"');
    }
}

internal sealed record FieldRead(Expr Target, string Field, TypeRef FieldType) : Expr(FieldType)
{
    public override Value Eval(Scope s) => Target.Eval(s) switch
    {
        VData d => d.FieldValues[Field],
        VPtr p => p.Box.Fields[Field],
        var other => throw new InvalidOperationException($"Field read on {other}."),
    };

    public override void Render(StringBuilder sb)
    {
        RenderReceiver(sb, Target);
        sb.Append('.').Append(Field);
    }
}

internal sealed record WithUpdate(Expr Target, string Field, Expr NewValue) : Expr(Target.Type)
{
    public override Value Eval(Scope s)
    {
        var data = (VData)Target.Eval(s);
        var fields = new Dictionary<string, Value>(data.FieldValues) { [Field] = NewValue.Eval(s) };
        return new VData(data.Type, fields);
    }

    public override void Render(StringBuilder sb)
    {
        RenderReceiver(sb, Target);
        sb.Append(" with { ").Append(Field).Append(": ");
        NewValue.Render(sb);
        sb.Append(" }");
    }
}

internal sealed record CompositeData(TypeModel DataType, IReadOnlyList<Expr> Args) : Expr(TypeRef.Data(DataType))
{
    public override Value Eval(Scope s)
    {
        var fields = new Dictionary<string, Value>();
        for (var i = 0; i < DataType.Fields.Count; i++)
            fields[DataType.Fields[i].Name] = Args[i].Eval(s);
        return new VData(DataType, fields);
    }

    public override void Render(StringBuilder sb)
    {
        sb.Append(DataType.Name).Append(" { ");
        for (var i = 0; i < Args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(DataType.Fields[i].Name).Append(": ");
            Args[i].Render(sb);
        }
        sb.Append(" }");
    }
}

internal sealed record NewPtr(TypeModel DataType, IReadOnlyList<Expr> Args) : Expr(TypeRef.Ptr(DataType))
{
    public override Value Eval(Scope s)
    {
        var fields = new Dictionary<string, Value>();
        for (var i = 0; i < DataType.Fields.Count; i++)
            fields[DataType.Fields[i].Name] = Args[i].Eval(s);
        return new VPtr(new PtrBox(DataType, fields));
    }

    public override void Render(StringBuilder sb)
    {
        sb.Append("new ").Append(DataType.Name).Append(" { ");
        for (var i = 0; i < Args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(DataType.Fields[i].Name).Append(": ");
            Args[i].Render(sb);
        }
        sb.Append(" }");
    }
}

internal sealed record ListLit(IReadOnlyList<Expr> Items) : Expr(TypeRef.ListInt)
{
    public override Value Eval(Scope s) => new VList(Items.Select(i => i.Eval(s)).ToList());
    public override void Render(StringBuilder sb)
    {
        sb.Append('[');
        for (var i = 0; i < Items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Items[i].Render(sb);
        }
        sb.Append(']');
    }
}

internal sealed record ListIndex(Expr List, int Index, bool FromEnd = false) : Expr(TypeRef.Int)
{
    public override Value Eval(Scope s)
    {
        var list = (VList)List.Eval(s);
        return list.Items[FromEnd ? list.Items.Count - Index : Index];
    }

    public override void Render(StringBuilder sb)
    {
        RenderReceiver(sb, List);
        sb.Append('[');
        if (FromEnd) sb.Append('^');
        sb.Append(Index).Append(']');
    }
}

internal sealed record ListCount(Expr List) : Expr(TypeRef.Int)
{
    public override Value Eval(Scope s) => new VInt(((VList)List.Eval(s)).Items.Count);
    public override void Render(StringBuilder sb)
    {
        RenderReceiver(sb, List);
        sb.Append(".Count");
    }
}

internal sealed record EnumLit(TypeModel EnumType, string Member) : Expr(TypeRef.Enum(EnumType))
{
    public override Value Eval(Scope s) => new VEnum(EnumType, Member);
    // Enum cases construct with trailing parens: `Color.red()` (spec: enum).
    public override void Render(StringBuilder sb) => sb.Append(EnumType.Name).Append('.').Append(Member).Append("()");
}

internal sealed record ChoiceMake(TypeModel ChoiceType, int CaseIndex, IReadOnlyList<Expr> Args) : Expr(TypeRef.Choice(ChoiceType))
{
    public override Value Eval(Scope s)
        => new VChoice(ChoiceType, CaseIndex, Args.Select(a => a.Eval(s)).ToList());

    public override void Render(StringBuilder sb)
    {
        sb.Append(ChoiceType.Name).Append('.').Append(ChoiceType.Cases[CaseIndex].Name).Append('(');
        for (var i = 0; i < Args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Args[i].Render(sb);
        }
        sb.Append(')');
    }
}

internal sealed record HelperCall(FuncModel Func, IReadOnlyList<Expr> Args) : Expr(Func.ReturnType)
{
    public override Value Eval(Scope s) => Func.Invoke(s, Args.Select(a => a.Eval(s)).ToList());

    public override void Render(StringBuilder sb)
    {
        if (Func.Promoted)
        {
            // Promoted method: receiver spelling is the only valid one.
            RenderReceiver(sb, Args[0]);
            sb.Append('.').Append(Func.Name).Append('(');
            for (var i = 1; i < Args.Count; i++)
            {
                if (i > 1) sb.Append(", ");
                Args[i].Render(sb);
            }
            sb.Append(')');
        }
        else
        {
            sb.Append(Func.Name).Append('(');
            for (var i = 0; i < Args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Args[i].Render(sb);
            }
            sb.Append(')');
        }
    }
}

/// A feature-island call site: rendered from a text template with generated
/// int sub-expressions in the holes, modeled by a C# lambda that mirrors the
/// island's E# semantics exactly. `Parts.Count == Args.Count + 1`.
internal sealed record ExternCall(
    IReadOnlyList<string> Parts,
    IReadOnlyList<Expr> Args,
    Func<int[], int> Model,
    bool Await = false) : Expr(TypeRef.Int)
{
    public override Value Eval(Scope s)
    {
        s.Budget.Step();
        var values = Args.Select(a => ((VInt)a.Eval(s)).N).ToArray();
        return new VInt(Model(values));
    }

    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        if (Await) sb.Append("await ");
        sb.Append(Parts[0]);
        for (var i = 0; i < Args.Count; i++)
        {
            Args[i].Render(sb);
            sb.Append(Parts[i + 1]);
        }
        sb.Append(')');
    }
}

// ── Statements ───────────────────────────────────────────────────────────────

internal abstract record Stmt
{
    public abstract void Exec(Scope s);
    public abstract void Render(StringBuilder sb, int indent);

    protected static void Indent(StringBuilder sb, int indent) => sb.Append(' ', indent * 4);

    protected static void RenderBlock(StringBuilder sb, int indent, IReadOnlyList<Stmt> body)
    {
        foreach (var stmt in body)
            stmt.Render(sb, indent);
    }

    protected static void ExecBlock(Scope s, IReadOnlyList<Stmt> body)
    {
        foreach (var stmt in body)
            stmt.Exec(s);
    }
}

internal sealed record DeclStmt(string Name, bool Mutable, Expr Init, TypeRef? ExplicitType = null) : Stmt
{
    public override void Exec(Scope s) => s.Locals[Name] = Init.Eval(s);
    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append(Mutable ? "var " : "let ").Append(Name);
        if (ExplicitType is not null) sb.Append(": ").Append(ExplicitType.Render());
        sb.Append(" = ");
        Init.Render(sb);
        sb.Append('\n');
    }
}

internal sealed record AssignStmt(string Name, Expr ValueExpr) : Stmt
{
    public override void Exec(Scope s) => s.Locals[Name] = ValueExpr.Eval(s);
    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append(Name).Append(" = ");
        ValueExpr.Render(sb);
        sb.Append('\n');
    }
}

internal sealed record CompoundStmt(string Name, IntOp Op, Expr ValueExpr) : Stmt
{
    public override void Exec(Scope s)
    {
        var current = ((VInt)s.Locals[Name]).N;
        var operand = ((VInt)ValueExpr.Eval(s)).N;
        s.Locals[Name] = new VInt(Op switch
        {
            IntOp.Add => unchecked(current + operand),
            IntOp.Sub => unchecked(current - operand),
            IntOp.Mul => unchecked(current * operand),
            _ => throw new InvalidOperationException("Compound /= and %= are not generated."),
        });
    }

    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append(Name).Append(Op switch
        {
            IntOp.Add => " += ", IntOp.Sub => " -= ", _ => " *= ",
        });
        ValueExpr.Render(sb);
        sb.Append('\n');
    }
}

internal sealed record PtrFieldAssign(string Name, string Field, Expr ValueExpr) : Stmt
{
    public override void Exec(Scope s)
    {
        var ptr = (VPtr)s.Locals[Name];
        ptr.Box.Fields[Field] = ValueExpr.Eval(s);
    }

    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append(Name).Append('.').Append(Field).Append(" = ");
        ValueExpr.Render(sb);
        sb.Append('\n');
    }
}

internal sealed record IfStmt(Expr Cond, IReadOnlyList<Stmt> Then, IReadOnlyList<Stmt>? Else = null) : Stmt
{
    public override void Exec(Scope s)
    {
        s.Budget.Step();
        if (((VBool)Cond.Eval(s)).B) ExecBlock(s, Then);
        else if (Else is not null) ExecBlock(s, Else);
    }

    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append("if ");
        Cond.Render(sb);
        sb.Append(" {\n");
        RenderBlock(sb, indent + 1, Then);
        Indent(sb, indent);
        sb.Append('}');
        if (Else is not null)
        {
            sb.Append(" else {\n");
            RenderBlock(sb, indent + 1, Else);
            Indent(sb, indent);
            sb.Append('}');
        }
        sb.Append('\n');
    }
}

/// `var i = 0 … while i < Bound { body; i += 1 }` — counted, so termination is
/// by construction; the body still mutates arbitrary locals.
internal sealed record CountedWhile(string Counter, int Bound, IReadOnlyList<Stmt> Body) : Stmt
{
    public override void Exec(Scope s)
    {
        s.Locals[Counter] = new VInt(0);
        while (((VInt)s.Locals[Counter]).N < Bound)
        {
            s.Budget.Step();
            ExecBlock(s, Body);
            s.Locals[Counter] = new VInt(((VInt)s.Locals[Counter]).N + 1);
        }
    }

    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append("var ").Append(Counter).Append(" = 0\n");
        Indent(sb, indent);
        sb.Append("while ").Append(Counter).Append(" < ").Append(Bound).Append(" {\n");
        RenderBlock(sb, indent + 1, Body);
        Indent(sb, indent + 1);
        sb.Append(Counter).Append(" += 1\n");
        Indent(sb, indent);
        sb.Append("}\n");
    }
}

internal sealed record ForInList(string Item, Expr List, IReadOnlyList<Stmt> Body) : Stmt
{
    public override void Exec(Scope s)
    {
        var list = (VList)List.Eval(s);
        foreach (var value in list.Items)
        {
            s.Budget.Step();
            s.Locals[Item] = value;
            ExecBlock(s, Body);
        }
    }

    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append("for ").Append(Item).Append(" in ");
        List.Render(sb);
        sb.Append(" {\n");
        RenderBlock(sb, indent + 1, Body);
        Indent(sb, indent);
        sb.Append("}\n");
    }
}

/// `match scrutinee { .case(bind) { … } … }` over a generated value choice.
/// Single-payload cases bind the payload value; multi-payload cases bind a
/// case view whose fields are read as `bind.field`. The generator pre-declares
/// any locals the arms assign.
internal sealed record MatchChoiceStmt(
    Expr Scrutinee,
    IReadOnlyList<(int CaseIndex, string? BindName, IReadOnlyList<Stmt> Body)> Arms) : Stmt
{
    public override void Exec(Scope s)
    {
        var value = (VChoice)Scrutinee.Eval(s);
        foreach (var (caseIndex, bindName, body) in Arms)
        {
            if (caseIndex != value.CaseIndex)
                continue;
            if (bindName is not null)
            {
                var payload = value.Type.Cases[caseIndex].Payload;
                s.Locals[bindName] = payload.Count == 1
                    ? value.Payload[0]
                    : new VData(value.Type, payload.Select((f, i) => (f.Name, V: value.Payload[i]))
                        .ToDictionary(p => p.Name, p => p.V));
            }
            ExecBlock(s, body);
            return;
        }
    }

    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append("match ");
        Scrutinee.Render(sb);
        sb.Append(" {\n");
        var choiceType = Scrutinee.Type.Model!;
        foreach (var (caseIndex, bindName, body) in Arms)
        {
            Indent(sb, indent + 1);
            sb.Append('.').Append(choiceType.Cases[caseIndex].Name);
            if (bindName is not null)
                sb.Append('(').Append(bindName).Append(')');
            sb.Append(" {\n");
            RenderBlock(sb, indent + 2, body);
            Indent(sb, indent + 1);
            sb.Append("}\n");
        }
        Indent(sb, indent);
        sb.Append("}\n");
    }
}

/// `match (x: Enum) { .member { … } default { … } }`.
internal sealed record MatchEnumStmt(
    LocalRef Scrutinee,
    IReadOnlyList<(string Member, IReadOnlyList<Stmt> Body)> Arms,
    IReadOnlyList<Stmt>? Default) : Stmt
{
    public override void Exec(Scope s)
    {
        var value = (VEnum)Scrutinee.Eval(s);
        foreach (var (member, body) in Arms)
        {
            if (member == value.Member)
            {
                ExecBlock(s, body);
                return;
            }
        }
        if (Default is not null)
            ExecBlock(s, Default);
    }

    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append("match (").Append(Scrutinee.Name).Append(": ").Append(Scrutinee.Type.Model!.Name).Append(") {\n");
        foreach (var (member, body) in Arms)
        {
            Indent(sb, indent + 1);
            sb.Append('.').Append(member).Append(" {\n");
            RenderBlock(sb, indent + 2, body);
            Indent(sb, indent + 1);
            sb.Append("}\n");
        }
        if (Default is not null)
        {
            Indent(sb, indent + 1);
            sb.Append("default {\n");
            RenderBlock(sb, indent + 2, Default);
            Indent(sb, indent + 1);
            sb.Append("}\n");
        }
        Indent(sb, indent);
        sb.Append("}\n");
    }
}

internal sealed record ReturnStmt(Expr Value) : Stmt
{
    public override void Exec(Scope s) => throw new ReturnSignal(Value.Eval(s));
    public override void Render(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.Append("return ");
        Value.Render(sb);
        sb.Append('\n');
    }
}
