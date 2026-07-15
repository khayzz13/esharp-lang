using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Esharp.BoundTree;        // BoundCompilationUnit
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_HeapPointer
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static (Assembly asm, AssemblyDefinition cecil) CompileAndLoadWithCecil(string source)
    {
        var asmName = $"HeapPtrTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (cecilAsm, diags) = EsHarness.EmitBound(binder, bound, asmName);
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        cecilAsm.Write(path);
        var loaded = Assembly.LoadFrom(path);
        return (loaded, cecilAsm);
    }

    static Assembly CompileAndLoad(string source)
    {
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        cecil.Dispose();
        return asm;
    }

    static string DumpMethod(AssemblyDefinition cecil, string typeName, string methodName)
    {
        var lines = new List<string>();
        foreach (var type in cecil.MainModule.Types)
        {
            if (!type.FullName.EndsWith(typeName)) continue;
            foreach (var m in type.Methods)
            {
                if (m.Name != methodName) continue;
                lines.Add($"Method: {m.FullName}");
                if (!m.HasBody) continue;
                foreach (var v in m.Body.Variables)
                    lines.Add($"  .local [{v.Index}] {v.VariableType.FullName}");
                foreach (var inst in m.Body.Instructions)
                    lines.Add($"  {inst}");
            }
        }
        return string.Join("\n", lines);
    }

    static string DumpAllTypes(AssemblyDefinition cecil)
    {
        var lines = new List<string>();
        foreach (var type in cecil.MainModule.Types)
        {
            lines.Add($"=== {type.FullName} ({(type.IsValueType ? "struct" : "class")}) ===");
            foreach (var f in type.Fields)
                lines.Add($"  field {f.FieldType.FullName} {f.Name}");
            foreach (var m in type.Methods)
            {
                lines.Add($"  {m.FullName}");
                if (!m.HasBody) continue;
                foreach (var v in m.Body.Variables)
                    lines.Add($"    .local [{v.Index}] {v.VariableType.FullName}");
                foreach (var inst in m.Body.Instructions)
                    lines.Add($"    {inst}");
            }
        }
        return string.Join("\n", lines);
    }

    static (BoundCompilationUnit bound, IReadOnlyList<Esharp.Diagnostics.Diagnostic> diagnostics) BindOnly(string source)
    {
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        return (bound, binder.Diagnostics);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
            ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, AnyStatic)
            ?? throw new Exception($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args);
    }

    // === Test 1: *T field compiles, wrapper class generated ===

    [Fact]
    public void HeapPointer_FieldDeclaration_Compiles()
    {
        const string source = """
namespace Test

struct Inner {
    var x: int
}

class Outer {
    ptr: *Inner
}

func check() -> bool {
    var o = Outer()
    return o.ptr == nil
}
""";
        var asm = CompileAndLoad(source);
        var result = Invoke(asm, "Test", "check");
        Assert.Equal(true, result);
    }

    // === Test 2: new T{...} creates heap-allocated struct ===

    [Fact]
    public void HeapPointer_HeapAlloc_StoresValue()
    {
        const string source = """
namespace Test

struct Point {
    var x: int
    var y: int
}

class Holder {
    pt: *Point
}

func getX() -> int {
    var h = Holder()
    h.pt = new Point { x: 42, y: 7 }
    return h.pt.x
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        var methodDump = DumpMethod(cecil, "Test", "getX");
        cecil.Dispose();

        // Verify wrapper class was generated
        Assert.Contains("__Ptr_Point", dump);

        var result = Invoke(asm, "Test", "getX");
        Assert.Equal(42, result);
    }

    // === Test 3: Auto-deref read through *T field ===

    [Fact]
    public void HeapPointer_AutoDeref_Read()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

class Entity {
    pos: *Vec2
}

func readY() -> int {
    var e = Entity()
    e.pos = new Vec2 { x: 10, y: 20 }
    return e.pos.y
}
""";
        var asm = CompileAndLoad(source);
        var result = Invoke(asm, "Test", "readY");
        Assert.Equal(20, result);
    }

    // === Test 4: Auto-deref write through *T field ===

    [Fact]
    public void HeapPointer_AutoDeref_Write()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

class Entity {
    pos: *Vec2
}

func writeAndRead() -> int {
    var e = Entity()
    e.pos = new Vec2 { x: 0, y: 0 }
    e.pos.x = 99
    return e.pos.x
}
""";
        var asm = CompileAndLoad(source);
        var result = Invoke(asm, "Test", "writeAndRead");
        Assert.Equal(99, result);
    }

    // === Test 5: *T field is nil by default ===

    [Fact]
    public void HeapPointer_NilByDefault()
    {
        const string source = """
namespace Test

struct Inner { var x: int }

class Box {
    inner: *Inner
}

func isNil() -> bool {
    var b = Box()
    return b.inner == nil
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "isNil"));
    }

    // === Test 6: Nil check on *T field ===

    [Fact]
    public void HeapPointer_NilCheck()
    {
        const string source = """
namespace Test

struct Inner { var x: int }

class Box {
    inner: *Inner
}

func checkBoth() -> int {
    var b = Box()
    if b.inner == nil { return 1 }
    return 0
}

func checkAfterSet() -> int {
    var b = Box()
    b.inner = new Inner { x: 5 }
    if b.inner != nil { return 2 }
    return 0
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "checkBoth"));
        Assert.Equal(2, Invoke(asm, "Test", "checkAfterSet"));
    }

    // === Test 7: Nil out a *T field ===

    [Fact]
    public void HeapPointer_AssignNil()
    {
        const string source = """
namespace Test

struct Inner { var x: int }

class Box {
    inner: *Inner
}

func setThenNil() -> bool {
    var b = Box()
    b.inner = new Inner { x: 10 }
    b.inner = nil
    return b.inner == nil
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "setThenNil"));
    }

    // === Test 8: &varName copies value type to heap ===

    [Fact]
    public void HeapPointer_AddressOfLocal_CopiesToHeap()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

class Holder {
    pt: *Vec2
}

func copyToHeap() -> int {
    var h = Holder()
    let v = Vec2 { x: 77, y: 88 }
    h.pt = &v
    return h.pt.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(77, Invoke(asm, "Test", "copyToHeap"));
    }

    // === Test 9: Compound assignment through *T field ===

    [Fact]
    public void HeapPointer_CompoundAssignment()
    {
        const string source = """
namespace Test

struct Counter {
    var value: int
}

class Box {
    c: *Counter
}

func increment() -> int {
    var b = Box()
    b.c = new Counter { value: 10 }
    b.c.value += 5
    return b.c.value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "increment"));
    }

    // === Test 10: *T method via dot syntax on *T variable ===

    [Fact]
    public void HeapPointer_NamedReceiver_DataType()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

func (v: *Vec2) addX(amount: int) {
    v.x += amount
}

func getSum() -> int {
    var v: *Vec2 = new Vec2 { x: 10, y: 20 }
    v.addX(5)
    return v.x
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();
        var result = Invoke(asm, "Test", "getSum");
        Assert.True(15.Equals(result), $"Expected 15 got {result}\n\nIL:\n{dump}");
    }

    // === Test 11: *T receiver on class ===

    [Fact]
    public void HeapPointer_NamedReceiver_RefData()
    {
        const string source = """
namespace Test

struct Inner { var x: int }

class Manager {
    inner: *Inner
    var count: int
}

func (m: Manager) setup() {
    m.inner = new Inner { x: 42 }
    m.count = 1
}

func getValue() -> int {
    var m = Manager()
    m.setup()
    return m.inner.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "getValue"));
    }

    // === Test 12: *Node self-referential (no ES2002 auto-promotion) ===

    [Fact]
    public void HeapPointer_SelfReferential_StaysStruct()
    {
        const string source = """
namespace Test

struct Node {
    value: int
    next: *Node
}

func sum() -> int {
    var n3: *Node = new Node { value: 3, next: nil }
    var n2: *Node = new Node { value: 2, next: n3 }
    var n1: *Node = new Node { value: 1, next: n2 }
    var total = 0
    var cur = n1
    while cur != nil {
        total += cur.value
        cur = cur.next
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "sum"));
    }

    // === Test 13: *T return type ===

    [Fact]
    public void HeapPointer_ReturnType()
    {
        const string source = """
namespace Test

struct Node {
    value: int
    next: *Node
}

func prepend(head: *Node, value: int) -> *Node {
    return new Node { value: value, next: head }
}

func sum() -> int {
    var list: *Node = nil
    list = prepend(list, 10)
    list = prepend(list, 20)
    list = prepend(list, 30)
    var total = 0
    var cur = list
    while cur != nil {
        total += cur.value
        cur = cur.next
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(60, Invoke(asm, "Test", "sum"));
    }

    // === Test 14: *T local variable with explicit type annotation ===

    [Fact]
    public void HeapPointer_LocalVariable_ExplicitType()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

func test() -> int {
    var p: *Vec2 = new Vec2 { x: 5, y: 10 }
    return p.x + p.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "test"));
    }

    // === Test 15: *T local nil then assign ===

    [Fact]
    public void HeapPointer_LocalVariable_NilThenAssign()
    {
        const string source = """
namespace Test

struct Inner { var x: int }

func test() -> int {
    var p: *Inner = nil
    if p == nil {
        p = new Inner { x: 99 }
    }
    return p.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "test"));
    }

    // === Test 16: Multiple *T fields on same struct ===

    [Fact]
    public void HeapPointer_MultiplePointerFields()
    {
        const string source = """
namespace Test

struct Health {
    var current: int
    var max: int
}

struct Position {
    var x: int
    var y: int
}

class Entity {
    hp: *Health
    pos: *Position
}

func test() -> int {
    var e = Entity()
    e.hp = new Health { current: 80, max: 100 }
    e.pos = new Position { x: 10, y: 20 }
    return e.hp.current + e.pos.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(90, Invoke(asm, "Test", "test"));
    }

    // === Test 17: *T field in data (struct with pointer field) ===

    [Fact]
    public void HeapPointer_FieldInStruct()
    {
        const string source = """
namespace Test

struct Inner { var x: int }

struct Outer {
    tag: int
    ptr: *Inner
}

func test() -> int {
    var o = Outer { tag: 5, ptr: new Inner { x: 42 } }
    return o.tag + o.ptr.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(47, Invoke(asm, "Test", "test"));
    }

    // === Test 18: Nested auto-deref chain (ptr.struct.field) ===

    [Fact]
    public void HeapPointer_NestedAutoDeref()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

struct Transform {
    var position: Vec2
    var scale: double
}

class Entity {
    transform: *Transform
}

func test() -> int {
    var e = Entity()
    e.transform = new Transform {
        position: Vec2 { x: 33, y: 44 },
        scale: 1.0
    }
    return e.transform.position.x + e.transform.position.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(77, Invoke(asm, "Test", "test"));
    }

    // === Test 19: Nested auto-deref write ===

    [Fact]
    public void HeapPointer_NestedAutoDeref_Write()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

struct Transform {
    var position: Vec2
    var scale: double
}

class Entity {
    transform: *Transform
}

func test() -> int {
    var e = Entity()
    e.transform = new Transform {
        position: Vec2 { x: 0, y: 0 },
        scale: 1.0
    }
    e.transform.position.x = 100
    e.transform.position.y = 200
    return e.transform.position.x + e.transform.position.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(300, Invoke(asm, "Test", "test"));
    }

    // === Test 20: Named receiver mutates through *T field ===

    [Fact]
    public void HeapPointer_ReceiverMutatesThroughPointer()
    {
        const string source = """
namespace Test

struct Health {
    var current: int
    var max: int
}

class Entity {
    hp: *Health
}

func (e: Entity) takeDamage(amount: int) {
    if e.hp == nil { return }
    e.hp.current -= amount
    if e.hp.current < 0 {
        e.hp.current = 0
    }
}

func test() -> int {
    var e = Entity()
    e.hp = new Health { current: 100, max: 100 }
    e.takeDamage(30)
    e.takeDamage(80)
    return e.hp.current
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test"));
    }

    // === Test 21: Linked list with prepend (full end-to-end) ===

    [Fact]
    public void HeapPointer_LinkedList_EndToEnd()
    {
        const string source = """
namespace Test

struct Node {
    value: int
    next: *Node
}

func prepend(head: *Node, value: int) -> *Node {
    return new Node { value: value, next: head }
}

func length(head: *Node) -> int {
    var count = 0
    var cur = head
    while cur != nil {
        count += 1
        cur = cur.next
    }
    return count
}

func testLength() -> int {
    var list: *Node = nil
    list = prepend(list, 10)
    list = prepend(list, 20)
    list = prepend(list, 30)
    return length(list)
}

func testSum() -> int {
    var list: *Node = nil
    list = prepend(list, 10)
    list = prepend(list, 20)
    list = prepend(list, 30)
    var total = 0
    var cur = list
    while cur != nil {
        total += cur.value
        cur = cur.next
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "testLength"));
        Assert.Equal(60, Invoke(asm, "Test", "testSum"));
    }

    // === Test 22: Struct implicit interface satisfaction ===

    [Fact]
    public void HeapPointer_StructProtocol_ImplicitSatisfaction()
    {
        const string source = """
namespace Test

interface IDescribable {
    func describe() -> string
}

struct Client : IDescribable {
    name: string
}

func (c: Client) describe() -> string {
    return c.name
}

func printInfo(d: IDescribable) -> string {
    return d.describe()
}

func test() -> string {
    let c = Client { name: "Alice" }
    return printInfo(c)
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();

        // Verify the struct implements the interface and boxing is present
        Assert.True(dump.Contains("box"), $"Missing box in IL:\n{dump}");

        Assert.Equal("Alice", Invoke(asm, "Test", "test"));
    }

    // === Test 23: Assigning T to *T without & → compile error (plan item 7) ===

    [Fact]
    public void HeapPointer_AssignT_ToStarT_WithoutAmpersand_Error()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

class Holder {
    pt: *Vec2
}

func test() {
    var h = Holder()
    h.pt = Vec2 { x: 1, y: 2 }
}
""";
        var (_, diagnostics) = BindOnly(source);
        Assert.Contains(diagnostics, d => d.Message.Contains("Cannot assign") && d.Message.Contains("*"));
    }

    // === Test 24: *T where T is class → error (a class is already a reference) ===

    [Fact]
    public void HeapPointer_StarRefData_Error()
    {
        const string source = """
namespace Test

class Connection {
    id: int
}

class Manager {
    conn: *Connection
}
""";
        var (_, diagnostics) = BindOnly(source);
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2003") && d.Message.Contains("Connection") && d.Message.Contains("illegal"));
    }

    // === Test 25: *T static call with dot syntax (plan item 10 — adapted) ===

    [Fact]
    public void HeapPointer_DotSyntax_StaticCall()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

func (v: *Vec2) scale(factor: int) {
    v.x *= factor
    v.y *= factor
}

func test() -> int {
    var v: *Vec2 = new Vec2 { x: 3, y: 4 }
    v.scale(10)
    return v.x + v.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(70, Invoke(asm, "Test", "test"));
    }

    // === Test 26: pointer-receiver method call on a *T value (dot dispatch) ===

    [Fact]
    public void HeapPointer_PointerReceiver_DotCall()
    {
        // A pointer-receiver method emits a static host; `v.addX(n)` on a `*Vec2` value
        // lowers to that host with `v` as the leading argument. (The free-call spelling
        // `addX(v, n)` of a receiver method is ES2142 — see Ns_PointerReceiverFreeCall.)
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

func (v: *Vec2) addX(amount: int) {
    v.x += amount
}

func test() -> int {
    var v: *Vec2 = new Vec2 { x: 0, y: 0 }
    v.addX(10)
    v.addX(5)
    return v.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "test"));
    }

    // === *T Protocol Satisfaction Tests ===

    // Test 27: *T with matching pointer receiver satisfies protocol
    [Fact]
    public void PointerProtocol_Compiles()
    {
        const string source = """
namespace Test

struct Node : ISummable {
    value: int
    next: *Node
}

func (head: *Node) sum() -> int {
    var total = 0
    var current = head
    while current != nil {
        total += current.value
        current = current.next
    }
    return total
}

interface ISummable {
    func sum() -> int
}

func report(s: ISummable) -> int {
    return s.sum()
}

func test() -> int {
    var list: *Node = nil
    list = new Node { value: 10, next: nil }
    list = new Node { value: 20, next: list }
    list = new Node { value: 30, next: list }
    return report(list)
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();

        // Wrapper should implement Summable
        Assert.Contains("Summable", dump);

        Assert.Equal(60, Invoke(asm, "Test", "test"));
    }

    // Test 28: T with only *T receiver does NOT satisfy protocol
    [Fact]
    public void PointerProtocol_ValueTypeDoesNotSatisfy()
    {
        const string source = """
namespace Test

struct Widget : IDescribable {
    var x: int
}

func (w: *Widget) describe() -> string {
    return "widget"
}

interface IDescribable {
    func describe() -> string
}

func check() -> bool {
    return true
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();

        // Widget (struct) should NOT implement Describable — only *Widget does
        // The struct type should not list Describable as an interface
        var widgetSection = dump.Split("===").FirstOrDefault(s => s.Contains("Widget (struct)"));
        Assert.NotNull(widgetSection);
        Assert.DoesNotContain("Describable", widgetSection);

        Assert.Equal(true, Invoke(asm, "Test", "check"));
    }

    // Test 29: Value receiver methods in both T and *T method sets
    [Fact]
    public void PointerProtocol_ValueReceiverInBothSets()
    {
        const string source = """
namespace Test

struct Counter : IReadable {
    var value: int
}

func (c: Counter) current() -> int {
    return c.value
}

interface IReadable {
    func current() -> int
}

func fetch(r: IReadable) -> int {
    return r.current()
}

func testValue() -> int {
    let c = Counter { value: 42 }
    return fetch(c)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "testValue"));
    }

    // Test 30: Mixed method set — *T satisfies protocol with both value + pointer receivers
    [Fact]
    public void PointerProtocol_MixedMethodSet()
    {
        const string source = """
namespace Test

struct Acc : IAccumulator {
    var total: int
}

func (a: Acc) get() -> int {
    return a.total
}

func (a: *Acc) add(n: int) {
    a.total += n
}

interface IAccumulator {
    func get() -> int
    func add(n: int)
}

func useAcc(a: IAccumulator) -> int {
    a.add(10)
    a.add(20)
    return a.get()
}

func test() -> int {
    var a: *Acc = new Acc { total: 0 }
    return useAcc(a)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(30, Invoke(asm, "Test", "test"));
    }

    // Test 31: Protocol with multiple methods — all must be in *T's method set
    [Fact]
    public void PointerProtocol_AllMethodsRequired()
    {
        const string source = """
namespace Test

struct Pair {
    var a: int
    var b: int
}

func (p: *Pair) getA() -> int {
    return p.a
}

interface INeedsTwo {
    func getA() -> int
    func getB() -> int
}

func check() -> bool {
    return true
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();

        // *Pair only has getA, not getB — should NOT satisfy NeedsTwo
        // Check the wrapper class specifically, not the whole dump (protocol definition exists)
        var wrapperSection = dump.Split("===").FirstOrDefault(s => s.Contains("__Ptr_Pair"));
        if (wrapperSection is not null)
            Assert.DoesNotContain("NeedsTwo", wrapperSection);

        Assert.Equal(true, Invoke(asm, "Test", "check"));
    }

    // Test 32: Wrapper class has interface implementation + delegate methods in IL
    [Fact]
    public void PointerProtocol_WrapperHasDelegateMethods()
    {
        const string source = """
namespace Test

struct Item : IValued {
    var x: int
}

func (i: *Item) value() -> int {
    return i.x
}

interface IValued {
    func value() -> int
}

func getValue(v: IValued) -> int {
    return v.value()
}

func test() -> int {
    var item: *Item = new Item { x: 99 }
    return getValue(item)
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();

        // Wrapper should have a 'value' method that delegates to static
        Assert.Contains("__Ptr_Item", dump);
        // The wrapper should implement Valued
        var wrapperSection = dump.Split("===").FirstOrDefault(s => s.Contains("__Ptr_Item"));
        Assert.NotNull(wrapperSection);
        Assert.Contains("value", wrapperSection);

        Assert.Equal(99, Invoke(asm, "Test", "test"));
    }

    // ── Additional coverage ──

    [Fact]
    public void HeapPointer_Nil_Default_Construction()
    {
        const string source = """
namespace Test

struct Node { value: int }

func test() -> bool {
    let n: *Node = nil
    return n == nil
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void HeapPointer_NonNil_Compare_Returns_False_To_Nil()
    {
        const string source = """
namespace Test

struct Node { value: int }

func test() -> bool {
    let n: *Node = new Node { value: 7 }
    return n == nil
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(false, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void HeapPointer_Auto_Deref_Field_Access()
    {
        const string source = """
namespace Test

struct Node { value: int }

func test() -> int {
    let n: *Node = new Node { value: 21 }
    return n.value * 2
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // === added: pointer deref depth + new/value parity (Theory) ===

    [Theory]
    [InlineData("a.b.b.v", 7)]
    [InlineData("a.b.b.v + 1", 8)]
    public void Added_PointerChainDeref(string expr, int expected)
    {
        var asm = CompileAndLoad($$"""
namespace Test

struct Leaf { v: int }
struct Mid { b: *Leaf }
struct Top { b: *Mid }

func test() -> int {
    let a = new Top { b: new Mid { b: new Leaf { v: 7 } } }
    return {{expr}}
}
""");
        Assert.Equal(expected, Invoke(asm, "Test", "test"));
    }

    // `&Point{}` is the deprecated spelling (ES2143 warning), which this file's
    // diagnostic-strict harness rejects — so parity is asserted with `new` only;
    // ILEmitterTests_New.IL_NewAndAmp_EmitIdenticalBody covers byte-for-byte parity.
    [Fact]
    public void Added_New_FieldSum()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Point { x: int, y: int }

func test() -> int {
    let p = new Point { x: 10, y: 20 }
    return p.x + p.y
}
""");
        Assert.Equal(30, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_PointerFieldReassignedThroughChain()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Inner { var n: int }
struct Outer { inner: *Inner }

func test() -> int {
    let o = new Outer { inner: new Inner { n: 1 } }
    o.inner.n = 5
    return o.inner.n
}
""");
        Assert.Equal(5, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_PointerParameterMutatesCaller()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Counter { var value: int }

func bump(c: *Counter) { c.value += 10 }

func test() -> int {
    var c = new Counter { value: 5 }
    bump(c)
    return c.value
}
""");
        Assert.Equal(15, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_NilPointerDefaultThenAssign()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Node { value: int, next: *Node }

func test() -> int {
    var head: *Node = nil
    head = new Node { value: 42, next: nil }
    return head.value
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_PointerReturnedAndChained()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Box { n: int }

func make(v: int) -> *Box = new Box { n: v }

func test() -> int {
    let b = make(21)
    return b.n + b.n
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_TwoLevelEmbedPointer()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Vec2 { var x: int, var y: int }
struct Entity { *Vec2, name: string }

func test() -> int {
    var e = new Entity { Vec2: new Vec2 { x: 30, y: 12 }, name: "p" }
    return e.x + e.y
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }
}
