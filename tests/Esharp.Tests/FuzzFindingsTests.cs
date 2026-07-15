using Esharp.Syntax.Parsing;

namespace Esharp.Tests;

/// Regression tests graduated from minimized fuzzer findings. Each test preserves
/// the failing shape directly so the defect cannot silently return.
public class FuzzFindingsTests
{
    // ── Finding #4: oversized integer literal → ICE in the parser ──────────────
    // `ParseNumber` called `UInt64.Parse` on the raw digits with no overflow
    // guard, throwing an uncatchable OverflowException out of the front end. A
    // malformed literal must degrade to a diagnostic, never crash.
    [Fact]
    public void HugeIntLiteral_ProducesDiagnostic_DoesNotThrow()
    {
        var src = """
        namespace Test
        func go() -> int {
            return 99999999999999999999999999999999999999999999999999999999999999999999999999999999
        }
        """;
        var diags = EsHarness.AllDiagnostics(src);
        Assert.Contains(diags, d => d.Message.Contains("out of range"));
    }

    [Fact]
    public void IntLiteralAtUlongMax_StillParses()
    {
        // ulong.MaxValue itself must remain valid — the guard rejects only past it.
        var parser = new Parser("namespace Test\nfunc go() -> uint64 { return 18446744073709551615 }", "test.es");
        parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    // ── Finding #5: pathological bracket nesting → native stack overflow ────────
    // The recursive-descent expression/statement/type parsers recursed once per
    // nesting level; ~1758 levels aborted the process (SIGABRT). A depth guard now
    // turns deep nesting into a diagnostic.
    [Fact]
    public void BracketBomb_ProducesDiagnostic_DoesNotCrash()
    {
        var bomb = "namespace Test\nfunc go() -> int {\n  let x = " +
            new string('[', 5000) + "1" + new string(']', 5000) + "\n  return 1\n}";
        var parser = new Parser(bomb, "test.es");
        parser.ParseCompilationUnit();
        Assert.Contains(parser.Diagnostics, d => d.Message.Contains("nested too deeply"));
    }

    [Fact]
    public void ParenBomb_ProducesDiagnostic_DoesNotCrash()
    {
        var bomb = "namespace Test\nfunc go() -> int {\n  return " +
            new string('(', 5000) + "1" + new string(')', 5000) + "\n}";
        var parser = new Parser(bomb, "test.es");
        parser.ParseCompilationUnit();
        Assert.Contains(parser.Diagnostics, d => d.Message.Contains("nested too deeply"));
    }

    [Fact]
    public void BlockBomb_ProducesDiagnostic_DoesNotCrash()
    {
        // Nested `if { if { ... }` recurses through the block parser.
        var head = string.Concat(Enumerable.Repeat("if true {\n", 5000));
        var tail = string.Concat(Enumerable.Repeat("}\n", 5000));
        var bomb = "namespace Test\nfunc go() -> int {\n" + head + "return 1\n" + tail + "return 0\n}";
        var parser = new Parser(bomb, "test.es");
        parser.ParseCompilationUnit();
        Assert.Contains(parser.Diagnostics, d => d.Message.Contains("nested too deeply"));
    }

    [Fact]
    public void ModeratelyNestedList_StillCompiles()
    {
        // A realistically deep (but legal) literal must remain accepted: the guard
        // bound sits far above any hand-written nesting.
        var src = "namespace Test\nfunc go() -> int {\n  let x = " +
            new string('[', 32) + "7" + new string(']', 32) + "\n  return 1\n}";
        var parser = new Parser(src, "test.es");
        parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    // ── Finding #8: `static func` untyped `let` rejected though spec marks the
    // type optional. The parser required the `: Type` annotation; it now infers
    // the field type from the initializer (matching the `const` form). ───────────
    [Fact]
    public void StaticFunc_UntypedLet_InfersTypeFromInitializer()
    {
        var src = """
        namespace Test
        static Bag {
            let factor = 8
            pub func mix(a: int, b: int) -> int {
                return a * factor + b
            }
        }
        func go() -> int {
            return Bag.mix(5, 1)
        }
        """;
        Assert.Equal(41, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void StaticFunc_UntypedLet_NoInitializer_IsDiagnosed()
    {
        var src = """
        namespace Test
        static Bag {
            let factor
        }
        """;
        var diags = EsHarness.AllDiagnostics(src);
        Assert.Contains(diags, d => d.Message.Contains("type annotation or an initializer"));
    }

    // ── Finding #2: immediately-invoked lambda evaluated to its argument ─────────
    // `((b) => b + 24)(3)` returned 3 — the lambda body never ran. The call
    // emitter's non-name/non-member fallback dropped the call.
    [Fact]
    public void LambdaIife_RunsBody_ReturnsResult()
    {
        var src = """
        namespace Test
        func go() -> int {
            return ((b) => b + 24)(3)
        }
        """;
        Assert.Equal(27, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void LambdaIife_WithCapture_RunsBody()
    {
        var src = """
        namespace Test
        func go() -> int {
            let m = 3
            return ((a) => a * 7 + m)(0 - 1)
        }
        """;
        Assert.Equal(-4, EsHarness.Run(src, "go"));
    }

    // ── Finding #7: canonical printer not idempotent on a primary-ctor-capture
    // class. The parser surfaces a type body's methods into the top-level member
    // list (for binding); the printer emitted those surfaced copies too, so each
    // print/parse round added another top-level copy of every type method and the
    // canonical form never stabilized. The printer now skips surfaced methods. ────
    [Fact]
    public void Printer_PrimaryCtorCaptureClass_ReachesFixpoint()
    {
        var src = """
        namespace Test
        class Svc(maxRetries: int) {
            tokens: int = maxRetries
            init() {
                if maxRetries <= 0 {
                    tokens = 1
                }
            }
            func budget(self: Svc) -> int {
                return self.tokens + maxRetries
            }
        }
        func run() -> int {
            let bad = Svc(0)
            let good = Svc(4)
            return (bad.budget() * 100) + good.budget()
        }
        """;
        var print1 = Esharp.Syntax.SyntaxPrinter.PrintCanonical(new Parser(src, "f7.es").ParseCompilationUnit());
        var print2 = Esharp.Syntax.SyntaxPrinter.PrintCanonical(new Parser(print1, "f7.es").ParseCompilationUnit());
        Assert.Equal(print1, print2);
    }

    // ── Finding #6: non-deterministic emit ─────────────────────────────────────
    // Synthesized closure/display types were numbered from a process-static counter
    // that kept climbing across compilations, so the same source emitted twice in one
    // process produced different `<>c__Display_N` / `<>c__Lambda_N` names — and thus
    // structurally different assemblies. The counter is now per-emit.
    [Fact]
    public void Emit_IsDeterministic_AcrossTwoCompilationsInOneProcess()
    {
        var src = """
        namespace Test
        func cls1(x: int, m: int) -> int {
            return ((a) => a * 5 + m)(x) + ((b) => b + 12)(m)
        }
        func cls2(y: int) -> int {
            return ((c) => c * 2)(y) + ((d) => d - 1)(y)
        }
        func go() -> int {
            return cls1(3, 4) + cls2(10)
        }
        """;
        Assert.Equal(StructuralDump(EsHarness.CompileToPath(src, "det")),
                     StructuralDump(EsHarness.CompileToPath(src, "det")));
    }

    /// MVID/timestamp-free structural dump (types — including synthesized closure
    /// types — and their members and IL opcode streams). Same source → same dump.
    static string StructuralDump(string assemblyPath)
    {
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(assemblyPath);
        var sb = new System.Text.StringBuilder();
        void Dump(Mono.Cecil.TypeDefinition t)
        {
            sb.Append("type ").Append(t.FullName).Append(" base=").Append(t.BaseType?.FullName ?? "-").Append('\n');
            foreach (var f in t.Fields) sb.Append("  field ").Append(f.Name).Append(':').Append(f.FieldType.FullName).Append('\n');
            foreach (var m in t.Methods)
            {
                sb.Append("  method ").Append(m.Name).Append('(')
                    .Append(string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName))).Append(")\n");
                if (m.HasBody)
                    foreach (var instr in m.Body.Instructions)
                        sb.Append("    ").Append(instr.OpCode.Name).Append('\n');
            }
            foreach (var nested in t.NestedTypes.OrderBy(n => n.FullName, StringComparer.Ordinal))
                Dump(nested);
        }
        foreach (var t in module.Types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            Dump(t);
        return sb.ToString();
    }

    // ── Finding #3: promoted method + list literal over a nested data field ─────
    // Inside a promoted instance method, a member access `p.f2.f0` (where f2 is a
    // nested `data`) resolved the trailing `.f0` against the *receiver's* type rather
    // than f2's type whenever the receiver also declared an `f0`. The receiver's
    // fields are preloaded by bare name for bare-field access, and that by-name lookup
    // shadowed the real target type — emitting `ldfld D1::f0` off a `D0&` (unverifiable).
    [Fact]
    public void PromotedMethod_NestedFieldInListLiteral_ResolvesAgainstTargetType()
    {
        var src = """
        namespace Test
        struct D0 { f0: int, f1: int }
        struct D1 { f0: int, f1: int, f2: D0 }
        func (p1_0: D1) h1() -> int {
            let v1 = [p1_0.f2.f0, (-27)]
            return p1_0.f0
        }
        func h2(p2_0: int) -> int {
            return D1 { f0: p2_0, f1: (-4), f2: D0 { f0: p2_0, f1: 39 } }.h1()
        }
        func go() -> int {
            return h2(5)
        }
        """;
        Assert.Equal(5, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void NestedDataField_ShadowedFieldName_ReadsInnerValue()
    {
        // Direct (non-list) read of a nested field whose name collides with an outer
        // field must read the INNER value.
        var src = """
        namespace Test
        struct Inner { v: int }
        struct Outer { v: int, inner: Inner }
        func (o: Outer) readInner() -> int {
            return o.inner.v
        }
        func go() -> int {
            return Outer { v: 100, inner: Inner { v: 7 } }.readInner()
        }
        """;
        Assert.Equal(7, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Printer_ClassMethod_NotDuplicatedAtTopLevel()
    {
        // The surfaced method must appear exactly once — inside the class — not
        // again as a free function in the printed canonical text.
        var src = """
        namespace Test
        class Svc(maxRetries: int) {
            tokens: int = maxRetries
            func budget(self: Svc) -> int {
                return self.tokens
            }
        }
        """;
        var printed = Esharp.Syntax.SyntaxPrinter.PrintCanonical(new Parser(src, "f7b.es").ParseCompilationUnit());
        var occurrences = printed.Split("func budget").Length - 1;
        Assert.Equal(1, occurrences);
    }
}
