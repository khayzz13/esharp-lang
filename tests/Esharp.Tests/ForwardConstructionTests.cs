using System.Reflection;
using Esharp.Compilation;

namespace Esharp.Tests;

/// Forward-reference construction + external-ctor optional arguments — two emit
/// gaps the E# language server's bring-up surfaced. Constructor bodies defer until
/// after the full populate sweep,
/// so an init may construct any declared type regardless of declaration or file
/// order; an external constructor call fills its omitted optional tail like an
/// external method call always has.
public sealed class ForwardConstructionTests
{
    static int _asmCounter;

    // An init body constructs a type declared LATER in the same file — the
    // spec's "forward references always resolve" guarantee, on the ctor path.
    [Fact]
    public void Init_Constructs_LaterDeclaredType_SameFile()
    {
        Assert.Equal(1, (int)EsHarness.Run("""
namespace Test

pub class Host {
    carrier: Carrier

    init(seed: int) {
        self.carrier = Carrier(seed)
    }

    pub func ping() -> int = self.carrier.ping()
}

pub class Carrier {
    seed: int

    init(seed: int) {
        self.seed = seed
    }

    pub func ping() -> int = self.seed
}

func go() -> int {
    let h = Host(1)
    return h.ping()
}
""", "go")!);
    }

    // The same shape across FILES, with the constructing file sorting before the
    // declaring file — the language server's server.es < transport.es ordering.
    [Fact]
    public void Init_Constructs_TypeFromLaterFile()
    {
        var asmName = $"EsFwdCtor_{Interlocked.Increment(ref _asmCounter)}";
        var ws = new Workspace(asmName);
        ws.AddDocument("a_host.es", """
namespace Test

pub class Host {
    carrier: Carrier

    init(seed: int) {
        self.carrier = Carrier(seed)
    }

    pub func ping() -> int = self.carrier.ping()
}

pub func go() -> int {
    let h = Host(41)
    return h.ping() + 1
}
""");
        ws.AddDocument("b_carrier.es", """
namespace Test

pub class Carrier {
    seed: int

    init(seed: int) {
        self.seed = seed
    }

    pub func ping() -> int = self.seed
}
""");
        var dir = Path.Combine(Path.GetTempPath(), $"esharp_fwdctor_{asmName}");
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, asmName + ".dll");
        var result = ws.CurrentCompilation.EmitToFile(dllPath, debugSymbols: false);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var asm = Assembly.LoadFrom(dllPath);
        var host = asm.GetType("Test.Test")!;
        var go = host.GetMethod("go", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(42, (int)go.Invoke(null, null)!);
    }

    // A field default's initializer is body-level too — it may construct a
    // later-declared type.
    [Fact]
    public void FieldDefault_Constructs_LaterDeclaredType()
    {
        Assert.Equal(7, (int)EsHarness.Run("""
namespace Test

pub class Holder {
    box: Box = Box(7)

    init(unused: int) { }

    pub func read() -> int = self.box.v
}

pub class Box {
    v: int

    init(v: int) { self.v = v }
}

func go() -> int {
    let h = Holder(0)
    return h.read()
}
""", "go")!);
    }

    // External constructor with an omitted optional tail: the modern BCL
    // StreamWriter ctor is (Stream, Encoding? = null, int = -1, bool = false) —
    // `StreamWriter(ms)` must fill the three defaults, exactly like the
    // language server's `Workspace("lsp")`.
    [Fact]
    public void ExternalCtor_OptionalTail_Filled()
    {
        Assert.Equal("hi", (string)EsHarness.Run("""
namespace Test

func go() -> string {
    let ms = MemoryStream()
    let w = StreamWriter(ms)
    w.Write("hi")
    w.Flush()
    return Encoding.UTF8.GetString(ms.ToArray())
}
""", "go")!);
    }
}
