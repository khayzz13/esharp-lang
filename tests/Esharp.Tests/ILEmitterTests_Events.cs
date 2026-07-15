using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

// Field-style events (`event Name: T` + `raise Name(args)`) on `class` and
// `interface`. Events build on the delegate tier; they emit the exact CLR shape C#
// emits (backing field + add/remove accessors + EventDefinition).
public sealed class ILEmitterTests_Events
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpEventTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path);
    }

    [Fact]
    public void Event_Declared_EmitsCanonicalClrShape()
    {
        // An event typed by a `delegate func` emits: an EventDefinition, public
        // add_/remove_ accessors, and a private backing field — exactly C#'s shape.
        var asm = CompileAndLoad("""
namespace Test

delegate func Notify(value: int)

pub class Server {
    pub event OnReady: Notify
}
""");
        var server = asm.GetType("Test.Server") ?? throw new Exception("Test.Server not emitted");
        var notify = asm.GetType("Test.Notify")!;

        var ev = server.GetEvent("OnReady", Any) ?? throw new Exception("event OnReady not emitted");
        Assert.Equal(notify, ev.EventHandlerType);
        Assert.NotNull(server.GetMethod("add_OnReady", Any));
        Assert.NotNull(server.GetMethod("remove_OnReady", Any));

        var backing = server.GetField("OnReady", Any);
        Assert.NotNull(backing);
        Assert.True(backing!.IsPrivate, "backing field is private");
        Assert.Equal(notify, backing.FieldType);
    }

    [Fact]
    public void Event_Raise_InvokesSubscribers()
    {
        // raise fires every subscriber. Subscribe an Action<int> from the test, drive
        // an E# method that raises, observe the value.
        var asm = CompileAndLoad("""
namespace Test

pub class Counter {
    var total: int

    pub event OnChanged: Action<int>

    pub func add(n: int) {
        self.total = self.total + n
        raise OnChanged(self.total)
    }
}
""");
        var counterType = asm.GetType("Test.Counter")!;
        var counter = Activator.CreateInstance(counterType, nonPublic: true)!;

        var observed = new List<int>();
        Action<int> handler = v => observed.Add(v);
        counterType.GetMethod("add_OnChanged", Any)!.Invoke(counter, new object?[] { handler });

        var add = counterType.GetMethod("add", Any)!;
        add.Invoke(counter, new object?[] { 5 });
        add.Invoke(counter, new object?[] { 3 });

        Assert.Equal(new[] { 5, 8 }, observed);
    }

    [Fact]
    public void Event_Raise_NoSubscribers_DoesNotThrow()
    {
        // Null-safe by construction: raising with no subscribers is a no-op, not an NRE.
        var asm = CompileAndLoad("""
namespace Test

pub class Bell {
    pub event OnRing: Action

    pub func ring() {
        raise OnRing()
    }
}
""");
        var bellType = asm.GetType("Test.Bell")!;
        var bell = Activator.CreateInstance(bellType, nonPublic: true)!;
        // No subscribers — must not throw.
        bellType.GetMethod("ring", Any)!.Invoke(bell, null);
    }

    [Fact]
    public void Event_Raise_MulticastsToAllSubscribers()
    {
        var asm = CompileAndLoad("""
namespace Test

pub class Hub {
    pub event OnPing: Action<int>
    pub func ping(v: int) { raise OnPing(v) }
}
""");
        var hubType = asm.GetType("Test.Hub")!;
        var hub = Activator.CreateInstance(hubType, nonPublic: true)!;
        var a = new List<int>();
        var b = new List<int>();
        var add = hubType.GetMethod("add_OnPing", Any)!;
        add.Invoke(hub, new object?[] { (Action<int>)(v => a.Add(v)) });
        add.Invoke(hub, new object?[] { (Action<int>)(v => b.Add(v)) });
        hubType.GetMethod("ping", Any)!.Invoke(hub, new object?[] { 7 });
        Assert.Equal(new[] { 7 }, a);
        Assert.Equal(new[] { 7 }, b);
    }

    [Fact]
    public void Event_Unsubscribe_StopsDelivery()
    {
        var asm = CompileAndLoad("""
namespace Test

pub class Hub {
    pub event OnPing: Action<int>
    pub func ping(v: int) { raise OnPing(v) }
}
""");
        var hubType = asm.GetType("Test.Hub")!;
        var hub = Activator.CreateInstance(hubType, nonPublic: true)!;
        var seen = new List<int>();
        Action<int> handler = v => seen.Add(v);
        hubType.GetMethod("add_OnPing", Any)!.Invoke(hub, new object?[] { handler });
        var ping = hubType.GetMethod("ping", Any)!;
        ping.Invoke(hub, new object?[] { 1 });
        hubType.GetMethod("remove_OnPing", Any)!.Invoke(hub, new object?[] { handler });
        ping.Invoke(hub, new object?[] { 2 });
        Assert.Equal(new[] { 1 }, seen);
    }

    [Fact]
    public void Event_Interface_EmitsAbstractAccessorsAndEventDef()
    {
        var asm = CompileAndLoad("""
namespace Test

pub interface IRuntime {
    event OnReady: Action
}
""");
        var iface = asm.GetType("Test.IRuntime")!;
        Assert.True(iface.IsInterface);
        var ev = iface.GetEvent("OnReady", Any) ?? throw new Exception("interface event not emitted");
        Assert.Equal(typeof(Action), ev.EventHandlerType);
        var add = iface.GetMethod("add_OnReady", Any)!;
        Assert.True(add.IsAbstract);
        Assert.True(add.IsVirtual);
    }

    [Fact]
    public void Event_EsharpSideSubscription_AddAndRemove()
    {
        // `+=` / `-=` from E# code resolve the add_/remove_ accessors on the same-
        // compilation event (no runtime Type to reflect — resolved off the module type).
        var asm = CompileAndLoad("""
namespace Test

pub class Counter {
    var total: int
    pub event OnChanged: Action<int>

    pub func wire(h: Action<int>) {
        self.OnChanged += h
    }
    pub func unwire(h: Action<int>) {
        self.OnChanged -= h
    }
    pub func add(n: int) {
        self.total = self.total + n
        raise OnChanged(self.total)
    }
}
""");
        var t = asm.GetType("Test.Counter")!;
        var c = Activator.CreateInstance(t, nonPublic: true)!;
        var seen = new List<int>();
        Action<int> h = v => seen.Add(v);

        t.GetMethod("wire", Any)!.Invoke(c, new object?[] { h });
        t.GetMethod("add", Any)!.Invoke(c, new object?[] { 5 });
        t.GetMethod("unwire", Any)!.Invoke(c, new object?[] { h });
        t.GetMethod("add", Any)!.Invoke(c, new object?[] { 3 });

        Assert.Equal(new[] { 5 }, seen); // delivered once, then unsubscribed
    }

    [Fact]
    public void Event_OnValueData_IsRejected()
    {
        // Events imply identity — illegal on value-semantic `data`.
        var diags = EsHarness.AllDiagnostics("""
namespace Test

struct Bad {
    event OnX: Action
}
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Event_NonDelegateType_IsRejected()
    {
        var diags = EsHarness.AllDiagnostics("""
namespace Test

class Bad {
    event OnX: int
}
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Event_MultipleEvents_AreIndependent()
    {
        // Two events on one type each get their own backing field + accessors; raising
        // one must not deliver to the other's subscribers.
        var asm = CompileAndLoad("""
namespace Test

pub class Bus {
    pub event OnA: Action<int>
    pub event OnB: Action<int>
    pub func fireA(v: int) { raise OnA(v) }
    pub func fireB(v: int) { raise OnB(v) }
}
""");
        var t = asm.GetType("Test.Bus")!;
        var bus = Activator.CreateInstance(t, nonPublic: true)!;
        var a = new List<int>();
        var b = new List<int>();
        t.GetMethod("add_OnA", Any)!.Invoke(bus, new object?[] { (Action<int>)(v => a.Add(v)) });
        t.GetMethod("add_OnB", Any)!.Invoke(bus, new object?[] { (Action<int>)(v => b.Add(v)) });

        t.GetMethod("fireA", Any)!.Invoke(bus, new object?[] { 1 });
        t.GetMethod("fireB", Any)!.Invoke(bus, new object?[] { 2 });

        Assert.Equal(new[] { 1 }, a);
        Assert.Equal(new[] { 2 }, b);
    }

    [Fact]
    public void Event_TypedByEventHandlerGeneric_EmitsCorrectShape()
    {
        // EventHandler<T> is the canonical BCL event delegate — an event typed by it
        // emits the same add/remove/EventDefinition shape, EventHandlerType = EventHandler<T>.
        var asm = CompileAndLoad("""
namespace Test

pub class Widget {
    pub event OnResize: EventHandler<int>
}
""");
        var w = asm.GetType("Test.Widget")!;
        var ev = w.GetEvent("OnResize", Any) ?? throw new Exception("OnResize not emitted");
        Assert.Equal(typeof(EventHandler<int>), ev.EventHandlerType);
        Assert.NotNull(w.GetMethod("add_OnResize", Any));
        Assert.NotNull(w.GetMethod("remove_OnResize", Any));
    }
}
