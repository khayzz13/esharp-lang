namespace Esharp.Tests;

/// Probe: does E# resolve the BCL `System.Threading.Channels` generic surface that
/// Chan.es must wrap? Each test isolates one resolution question so a failure names
/// the exact compiler gap to fix (the directive: fix the limitation, never shim).
public sealed class ChannelResolveProbe
{
    // Channel.CreateUnbounded<int>() static generic factory + Writer/Reader members +
    // TryWrite / TryRead(out).
    [Fact]
    public void ChannelFactory_TryWriteTryRead_Runs() => Assert.Equal(7, EsHarness.Run("""
namespace Test
static Test {
    func go() -> int {
        let ch = Channel.CreateUnbounded<int>()
        ch.Writer.TryWrite(7)
        var got = 0
        if ch.Reader.TryRead(out got) {
            return got
        }
        return -1
    }
}
""", "go"));

    // Channel<int> as a local/field type (the Chan.es field `c: Channel<T>`).
    [Fact]
    public void ChannelGeneric_AsLocalType_Compiles() => EsHarness.Compile("""
namespace Test
func go() -> int {
    let ch: Channel<int> = Channel.CreateUnbounded<int>()
    ch.Writer.TryWrite(3)
    return 0
}
""");

    // A generic class with a Channel<T> field — the exact Chan<T> shape.
    [Fact]
    public void GenericClass_WithChannelField_Compiles() => EsHarness.Compile("""
namespace Test
class Box<T> {
    c: Channel<T>
}
func go() -> int = 0
""");

    // With an explicit import of the channels namespace — does the closed generic +
    // static factory resolve then? (Pins whether the gap is assembly-load, not syntax.)
    [Fact]
    public void ChannelFactory_WithExplicitUsing_Runs() => Assert.Equal(7, EsHarness.Run("""
namespace Test
using "System.Threading.Channels"
static Test {
    func go() -> int {
        let ch = Channel.CreateUnbounded<int>()
        ch.Writer.TryWrite(7)
        var got = 0
        if ch.Reader.TryRead(out got) {
            return got
        }
        return -1
    }
}
""", "go"));

    // ISOLATE A: explicit ChannelWriter<int> param (no factory, no .Writer chain) +
    // using — does the EMITTER resolve ChannelWriter<int> and find TryWrite? If this
    // fails, the gap is emit-side open-generic resolution of ChannelWriter`1.
    [Fact]
    public void ChannelWriter_ExplicitParam_TryWrite_Compiles() => EsHarness.Compile("""
namespace Test
using "System.Threading.Channels"
func push(w: ChannelWriter<int>) -> int {
    w.TryWrite(5)
    return 0
}
""");

    // ISOLATE C: a generic class wrapping Channel<T> — the Chan.es shape. Distinguishes
    // arg'd expression-bodied calls (doWrite) from no-arg block-bodied calls (doComplete)
    // on a member typed with the enclosing type's own generic parameter.
    [Fact]
    public void GenericClass_WrappingChannel_MemberCalls_Compile() => EsHarness.Compile("""
namespace Test
class Box<T> {
    c: Channel<T>
    init() { self.c = Channel.CreateUnbounded<T>() }
    pub func doWrite(v: T) -> bool = self.c.Writer.TryWrite(v)
    pub func doComplete() {
        self.c.Writer.TryComplete()
    }
}
func go() -> int = 0
""");

    // The blocking Chan<T>.Send slow path relies on ValueTask<bool>.AsTask()
    // retaining the receiver's closed bool argument.  A reflection-imported
    // ValueTask<T>.AsTask reference used to leave `!0` open in the emitted
    // signature and fail IL verification at Task.Wait().
    [Fact]
    public void GenericValueTask_AsTask_BlockingResult_Runs() => Assert.True((bool)EsHarness.Run("""
namespace Test
using "System.Threading.Tasks"
class Box<T> {
    pub func waitForWritable() -> bool {
        let wait: ValueTask<bool> = ValueTask<bool>(true)
        let task = wait.AsTask()
        task.Wait()
        return task.Result
    }
}
static Test {
    func go() -> bool = Box<int>().waitForWritable()
}
""", "go")!);

    // ISOLATE B: split the chain — bind `ch` to its own typed local, then a separate
    // statement reads `.Writer`. Distinguishes "ch erased" from ".Writer substitution
    // erased". With using present.
    [Fact]
    public void ChannelFactory_SplitWriter_Compiles() => EsHarness.Compile("""
namespace Test
using "System.Threading.Channels"
func go() -> int {
    let ch: Channel<int> = Channel.CreateUnbounded<int>()
    let w: ChannelWriter<int> = ch.Writer
    w.TryWrite(9)
    return 0
}
""");
}
