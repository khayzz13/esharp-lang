using System.Globalization;
using System.Text;

namespace Esharp.FuzzTests.Generation;

/// A feature island: declarations for one risky language seam (async, spawn,
/// defer, inheritance, generics, …) parameterized by random constants, plus a
/// call-site factory whose holes take generated sub-expressions and whose C#
/// model mirrors the E# semantics exactly. Free-form generation covers the
/// well-understood core; islands carry the construction oracle into the seams
/// — and because several islands plus the core land in one program, the
/// feature *mixing* axis is exercised by every case.
internal sealed record Island(
    string Name,
    string MainDecls,
    Func<BodyGenerator, GenEnv, ExternCall> MakeCall,
    bool Async = false,
    string? LibNamespace = null,
    string? LibDecls = null);

internal static class Islands
{
    public static IReadOnlyList<Island> Pick(Random rng, GeneratorProfile profile, int count)
    {
        var factories = new List<Func<int, Random, Island>>
        {
            AsyncIsland, SpawnIsland, ChanIsland, DeferIsland, ResultIsland,
            ClosureIsland, StaticFuncIsland, GenericIsland, InterfaceIsland,
            InheritanceIsland, RefChoiceIsland, StringIsland, CrossNamespaceIsland,
        };
        if (profile.Boundary)
        {
            factories.Add(LongMethodIsland);
            factories.Add(ManyLocalsIsland);
        }

        // Sample without replacement so a program never declares one island twice.
        var picked = new List<Island>();
        var pool = factories.ToList();
        for (var slot = 0; slot < count && pool.Count > 0; slot++)
        {
            var index = rng.Next(pool.Count);
            picked.Add(pool[index](slot, rng));
            pool.RemoveAt(index);
        }
        return picked;
    }

    static int C(Random rng, int lo = 2, int hi = 9) => rng.Next(lo, hi + 1);

    static Island AsyncIsland(int k, Random rng)
    {
        int c1 = C(rng), c2 = rng.Next(-50, 51);
        var decls = $$"""
func asy{{k}}(n: int) -> int {
    let a = await Task.FromResult(n * {{c1}})
    let b = await Task.FromResult(a + {{Lit(c2)}})
    return b
}
""";
        return new Island($"async{k}", decls,
            (gen, env) => new ExternCall(
                [$"asy{k}(", ")"],
                [gen.GenInt(env, 2)],
                v => unchecked(v[0] * c1 + c2),
                Await: true),
            Async: true);
    }

    static Island SpawnIsland(int k, Random rng)
    {
        int c1 = C(rng), c2 = rng.Next(-50, 51);
        var decls = $$"""
struct SpCell{{k}} {
    var value: int
}

func spw{{k}}(seed: int) -> int {
    var box: *SpCell{{k}} = new SpCell{{k}} { value: seed }
    let job = spawn {
        box.value = box.value * {{c1}} + {{Lit(c2)}}
    }
    job.Join()
    return box.value
}
""";
        return new Island($"spawn{k}", decls,
            (gen, env) => new ExternCall(
                [$"spw{k}(", ")"],
                [gen.GenInt(env, 2)],
                v => unchecked(v[0] * c1 + c2)));
    }

    static Island ChanIsland(int k, Random rng)
    {
        var c1 = rng.Next(-40, 41);
        var decls = $$"""
func chn{{k}}(a: int, b: int) -> int {
    let ch = chan<int>(4)
    let producer = spawn {
        defer { ch.Close() }
        ch.Send(a)
        ch.Send(b)
        ch.Send({{Lit(c1)}})
    }
    producer.Join()
    var total = 0
    for v in ch {
        total = total * 31 + v
    }
    return total
}
""";
        return new Island($"chan{k}", decls,
            (gen, env) => new ExternCall(
                [$"chn{k}(", ", ", ")"],
                [gen.GenInt(env, 2), gen.GenInt(env, 2)],
                v =>
                {
                    var total = 0;
                    foreach (var item in new[] { v[0], v[1], c1 })
                        total = unchecked(total * 31 + item);
                    return total;
                }));
    }

    static Island DeferIsland(int k, Random rng)
    {
        int c1 = rng.Next(-30, 31), c2 = C(rng), c3 = rng.Next(-30, 31);
        var decls = $$"""
struct DfCell{{k}} {
    var value: int
}

func dfw{{k}}(c: *DfCell{{k}}) {
    defer { c.value = c.value + {{Lit(c1)}} }
    defer { c.value = c.value * {{c2}} }
    c.value = c.value + {{Lit(c3)}}
}

func dfr{{k}}(seed: int) -> int {
    var c: *DfCell{{k}} = new DfCell{{k}} { value: seed }
    dfw{{k}}(c)
    return c.value
}
""";
        // Defers run LIFO at function exit: *c2 first, then +c1.
        return new Island($"defer{k}", decls,
            (gen, env) => new ExternCall(
                [$"dfr{k}(", ")"],
                [gen.GenInt(env, 2)],
                v => unchecked((v[0] + c3) * c2 + c1)));
    }

    static Island ResultIsland(int k, Random rng)
    {
        int c1 = rng.Next(-20, 21), c2 = C(rng, 1, 20);
        var decls = $$"""
func rst{{k}}(n: int) -> Result<int, string> {
    if n < {{Lit(c1)}} {
        return error("low")
    }
    return ok(n + {{c2}})
}

func rch{{k}}(n: int) -> Result<int, string> {
    let a = rst{{k}}(n)?
    let b = rst{{k}}(a)?
    return ok(a * 31 + b)
}

func rrn{{k}}(n: int) -> int {
    let r = rch{{k}}(n)
    match (r: Result<int, string>) {
        .ok(v) { return v }
        .err(e) { return 0 - e.Length }
    }
    return -2
}
""";
        return new Island($"result{k}", decls,
            (gen, env) => new ExternCall(
                [$"rrn{k}(", ")"],
                [gen.GenInt(env, 2)],
                v =>
                {
                    int? Step(int n) => n < c1 ? null : unchecked(n + c2);
                    var a = Step(v[0]);
                    if (a is null) return -3;        // "low".Length == 3
                    var b = Step(a.Value);
                    if (b is null) return -3;
                    return unchecked(a.Value * 31 + b.Value);
                }));
    }

    static Island ClosureIsland(int k, Random rng)
    {
        int c1 = C(rng), c2 = rng.Next(-30, 31);
        var decls = $$"""
func cls{{k}}(x: int, m: int) -> int {
    return ((a) => a * {{c1}} + m)(x) + ((b) => b + {{Lit(c2)}})(m)
}
""";
        return new Island($"closure{k}", decls,
            (gen, env) => new ExternCall(
                [$"cls{k}(", ", ", ")"],
                [gen.GenInt(env, 2), gen.GenInt(env, 2)],
                v => unchecked(v[0] * c1 + v[1] + v[1] + c2)));
    }

    static Island StaticFuncIsland(int k, Random rng)
    {
        int c1 = C(rng);
        var decls = $$"""
static Bag{{k}} {
    let factor: int = {{c1}}

    pub func mix(a: int, b: int) -> int {
        return a * factor + b
    }
}
""";
        return new Island($"staticfunc{k}", decls,
            (gen, env) => new ExternCall(
                [$"Bag{k}.mix(", ", ", ")"],
                [gen.GenInt(env, 2), gen.GenInt(env, 2)],
                v => unchecked(v[0] * c1 + v[1])));
    }

    static Island GenericIsland(int k, Random rng)
    {
        int c1 = rng.Next(-30, 31), c2 = rng.Next(-30, 31);
        var decls = $$"""
func pck{{k}}<T>(c: bool, a: T, b: T) -> T {
    if c {
        return a
    }
    return b
}
""";
        return new Island($"generic{k}", decls,
            (gen, env) => new ExternCall(
                [$"pck{k}<int>(((", $") % 2) == 0, ", $" + {Lit(c1)}, ", $" - {Lit(c2)})"],
                [gen.GenInt(env, 2), gen.GenInt(env, 2), gen.GenInt(env, 2)],
                v => v[0] % 2 == 0 ? unchecked(v[1] + c1) : unchecked(v[2] - c2)));
    }

    static Island InterfaceIsland(int k, Random rng)
    {
        int c1 = C(rng), c2 = C(rng);
        var decls = $$"""
interface IMet{{k}} {
    func met{{k}}() -> int
}

struct Rct{{k}} : IMet{{k}} {
    w: int
    h: int
}

func met{{k}}(r: Rct{{k}}) -> int {
    return r.w * {{c1}} + r.h
}

class Dsk{{k}} : IMet{{k}} {
    radius: int

    init(r: int) {
        self.radius = r
    }

    func met{{k}}() -> int {
        return {{c2}} * self.radius
    }
}

func msr{{k}}(s: IMet{{k}}) -> int {
    return s.met{{k}}()
}
""";
        return new Island($"interface{k}", decls,
            (gen, env) => new ExternCall(
                [$"msr{k}(Rct{k} {{ w: ", ", h: ", $" }}) + msr{k}(Dsk{k}(", "))"],
                [gen.GenInt(env, 2), gen.GenInt(env, 2), gen.GenInt(env, 2)],
                v => unchecked(v[0] * c1 + v[1] + c2 * v[2])));
    }

    static Island InheritanceIsland(int k, Random rng)
    {
        int c1 = rng.Next(-30, 31), c2 = rng.Next(-30, 31), c3 = C(rng);
        var decls = $$"""
open class Bse{{k}} {
    bonus: int

    init(b: int) {
        self.bonus = b
    }

    virtual func tag{{k}}() -> int {
        return self.bonus + {{Lit(c1)}}
    }
}

class Drv{{k}} : Bse{{k}} {
    extra: int

    init(e: int) : base(e + {{Lit(c2)}}) {
        self.extra = e
    }

    : func tag{{k}}() -> int {
        return self.extra * {{c3}} + self.bonus
    }
}

func vtg{{k}}(b: Bse{{k}}) -> int {
    return b.tag{{k}}()
}
""";
        return new Island($"inheritance{k}", decls,
            (gen, env) => new ExternCall(
                [$"Drv{k}(", $").vtg{k}() + Bse{k}(", $").vtg{k}()"],
                [gen.GenInt(env, 2), gen.GenInt(env, 2)],
                v => unchecked(v[0] * c3 + (v[0] + c2) + (v[1] + c1))));
    }

    static Island RefChoiceIsland(int k, Random rng)
    {
        var decls = $$"""
ref union Ast{{k}} {
    leaf(value: int)
    plus(left: Ast{{k}}, right: Ast{{k}})
    times(left: Ast{{k}}, right: Ast{{k}})
}

func evl{{k}}(e: Ast{{k}}) -> int {
    match (e: Ast{{k}}) {
        .leaf(v) { return v }
        .plus(l, r) { return evl{{k}}(l) + evl{{k}}(r) }
        .times(l, r) { return evl{{k}}(l) * evl{{k}}(r) }
    }
    return 0
}
""";
        // A random literal tree, built and evaluated at generation time.
        var (text, value) = Tree(rng, $"Ast{k}", 1 + rng.Next(4));
        return new Island($"refchoice{k}", decls,
            (_, _) => new ExternCall([$"evl{k}({text})"], [], _ => value));

        static (string Text, int Value) Tree(Random rng, string type, int depth)
        {
            if (depth <= 0 || rng.Next(3) == 0)
            {
                var leaf = rng.Next(-20, 21);
                return ($"{type}.leaf({Lit(leaf)})", leaf);
            }
            var (lt, lv) = Tree(rng, type, depth - 1);
            var (rt, rv) = Tree(rng, type, depth - 1);
            return rng.Next(2) == 0
                ? ($"{type}.plus({lt}, {rt})", unchecked(lv + rv))
                : ($"{type}.times({lt}, {rt})", unchecked(lv * rv));
        }
    }

    static Island StringIsland(int k, Random rng)
    {
        int c1 = C(rng);
        var decls = $$"""
func stx{{k}}(n: int) -> int {
    let t = "a{n}b"
    let u = t + t
    return u.Length * {{c1}} + n
}
""";
        return new Island($"string{k}", decls,
            (gen, env) => new ExternCall(
                [$"stx{k}(", ")"],
                [gen.GenInt(env, 2)],
                v =>
                {
                    var t = "a" + v[0].ToString(CultureInfo.InvariantCulture) + "b";
                    return unchecked(t.Length * 2 * c1 + v[0]);
                }));
    }

    static Island CrossNamespaceIsland(int k, Random rng)
    {
        int c1 = C(rng), c2 = rng.Next(-30, 31);
        var lib = $$"""
namespace Lib{{k}}

struct Vec{{k}} {
    x: int
    y: int
}

func bmp{{k}}(v: Vec{{k}}) -> int {
    return v.x * {{c1}} + v.y
}

func mke{{k}}(a: int, b: int) -> Vec{{k}} {
    return Vec{{k}} { x: a, y: b }
}

func inc{{k}}(n: int) -> int {
    return n + {{Lit(c2)}}
}
""";
        // Promoted method (`bmp` has a data receiver) must travel with the type
        // across the namespace boundary and be called receiver-style; the bare
        // free function (`inc`) imports by the same `using`. This is the exact
        // axis of the historical cross-namespace promotion bugs.
        return new Island($"crossns{k}", "",
            (gen, env) => new ExternCall(
                [$"mke{k}(", ", ", $").bmp{k}() + inc{k}(", ")"],
                [gen.GenInt(env, 2), gen.GenInt(env, 2), gen.GenInt(env, 2)],
                v => unchecked(v[0] * c1 + v[1] + v[2] + c2)),
            LibNamespace: $"Lib{k}",
            LibDecls: lib);
    }

    static Island LongMethodIsland(int k, Random rng)
    {
        // 30 arms × 8 statements pushes branch displacements well past ±127
        // bytes — the br.s/ShortenOpcodes overflow zone.
        const int arms = 30;
        const int stmtsPerArm = 8;
        var baseConst = rng.Next(-100, 101);
        var addConsts = new int[arms, stmtsPerArm];
        var mulConsts = new int[arms, stmtsPerArm];
        var body = new StringBuilder();
        body.Append($"func lng{k}(n: int) -> int {{\n    var r = {Lit(baseConst)}\n");
        for (var a = 0; a < arms; a++)
        {
            body.Append($"    if n == {a} {{\n");
            for (var s2 = 0; s2 < stmtsPerArm; s2++)
            {
                addConsts[a, s2] = rng.Next(-1000, 1001);
                mulConsts[a, s2] = rng.Next(1, 4);
                body.Append($"        r += {Lit(addConsts[a, s2])}\n");
                body.Append($"        r *= {mulConsts[a, s2]}\n");
            }
            body.Append("    }\n");
        }
        body.Append("    return r * 31 + n\n}");

        return new Island($"longmethod{k}", body.ToString(),
            (gen, env) => new ExternCall(
                [$"lng{k}(((", $") % {arms} + {arms}) % {arms})"],
                [gen.GenInt(env, 2)],
                v =>
                {
                    var n = (v[0] % arms + arms) % arms;
                    var r = baseConst;
                    for (var s2 = 0; s2 < stmtsPerArm; s2++)
                    {
                        r = unchecked(r + addConsts[n, s2]);
                        r = unchecked(r * mulConsts[n, s2]);
                    }
                    return unchecked(r * 31 + n);
                }));
    }

    static Island ManyLocalsIsland(int k, Random rng)
    {
        // 300 locals forces the emitter past ldloc.s (index > 255) into fat
        // ldloc — the short-form/long-form local seam.
        const int locals = 300;
        var consts = new int[locals];
        var body = new StringBuilder();
        body.Append($"func mny{k}(n: int) -> int {{\n");
        for (var i = 0; i < locals; i++)
        {
            consts[i] = rng.Next(-50, 51);
            body.Append(i == 0
                ? $"    let m0 = n + {Lit(consts[0])}\n"
                : $"    let m{i} = m{i - 1} * 31 + {Lit(consts[i])}\n");
        }
        body.Append($"    return m{locals - 1}\n}}");

        return new Island($"manylocals{k}", body.ToString(),
            (gen, env) => new ExternCall(
                [$"mny{k}(", ")"],
                [gen.GenInt(env, 2)],
                v =>
                {
                    var acc = unchecked(v[0] + consts[0]);
                    for (var i = 1; i < locals; i++)
                        acc = unchecked(acc * 31 + consts[i]);
                    return acc;
                }));
    }

    static string Lit(int n) => n < 0 ? $"(0 - {-(long)n})" : n.ToString(CultureInfo.InvariantCulture);
}
