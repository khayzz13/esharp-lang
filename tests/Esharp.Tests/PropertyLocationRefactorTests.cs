namespace Esharp.Tests;

/// <summary>Regression surface for the declaration/property-location refactor.</summary>
public sealed class PropertyLocationRefactorTests
{
    static object? Run(string source) => EsHarness.Run(source, "go");
    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics(string source) => EsHarness.AllDiagnostics(source);

    [Fact]
    public void TypedValueLocal_IsMutableButRejectsWritableAndReadonlyAddress()
    {
        Assert.Equal(42, Run("""
namespace Test
func go() -> int { count: int = 41 count += 1 return count }
"""));

        var writable = Diagnostics("""
namespace Test
func take(value: *int) { }
func bad() { count: int = 1 take(&count) }
""");
        var readOnly = Diagnostics("""
namespace Test
func take(value: readonly *int) { }
func bad() { count: int = 1 take(&count) }
""");
        Assert.Contains(writable, d => d.Message.Contains("typed value binding 'count'"));
        Assert.Contains(readOnly, d => d.Message.Contains("typed value binding 'count'"));
    }

    [Fact]
    public void MembersAndNamespaceLetVar_EmitProperties()
    {
        var asm = EsHarness.Compile("""
namespace Test
let label: string = "ready"
var count: int = 2
class Shape { bare: int = 1 let fixed: int = 2 var changing: int = 3 }
""");
        const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var shape = asm.GetType("Test.Shape")!;
        Assert.NotNull(shape.GetField("bare", instance));
        Assert.Null(shape.GetProperty("bare", instance));
        Assert.NotNull(shape.GetProperty("fixed", instance));
        Assert.NotNull(shape.GetProperty("changing", instance));
        var host = asm.GetType("Test.Test")!;
        Assert.NotNull(host.GetProperty("label", statics));
        Assert.NotNull(host.GetProperty("count", statics));
    }

    [Fact]
    public void InitOwnedPrivateField_IsRealPrivateStorage()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Meter { init(value: int) { priv self.storage: int = value } func read() -> int = self.storage }
""");
        var field = asm.GetType("Test.Meter")!.GetField("storage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.True(field!.IsPrivate);
    }

    [Fact]
    public void ExplicitLoca_UsesRefReturnWithoutClassPointers()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter { init(value: int) { priv self.storage: int = value } var value: int { loca => &self.storage } }
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
"""));
    }

    [Fact]
    public void DirectMut_UsesSelectedStableStorage()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter { init(value: int) { priv self.storage: int = value } var value: int { mut => &self.storage } }
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
"""));
    }

    [Fact]
    public void ClassFieldAddressAndComputedPropertyAddress_AreRejected()
    {
        var field = Diagnostics("""
namespace Test
class Holder { storage: int }
func take(value: *int) { }
func bad(holder: Holder) { take(&holder.storage) }
""");
        var computed = Diagnostics("""
namespace Test
class Holder { value: int let doubled: int => self.value * 2 }
func take(value: *int) { }
func bad(holder: Holder) { take(&holder.doubled) }
""");
        Assert.Contains(field, d => d.Message.Contains("class field 'storage'"));
        Assert.Contains(computed, d => d.Message.Contains("property 'doubled'"));
    }

    [Fact]
    public void ScopedMut_GetAndSet_RunThroughTheLendAndResumeProtocol()
    {
        Assert.Equal(9, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    let value: int {
        mut {
            let original: int = self.storage
            var working: int = original
            yield &working
            self.storage = working
        }
    }
}
func go() -> int { let meter = Meter(4) meter.value = 9 return meter.value }
"""));
    }

    [Fact]
    public void ScopedMut_BorrowWritesBackAfterTheBorrowingCall()
    {
        Assert.Equal(5, Run("""
namespace Test
struct Cell { value: int }
class Meter {
    init(value: int) { priv self.storage: int = value }
    let value: Cell {
        mut {
            var working: Cell = Cell { value: self.storage }
            yield &working
            self.storage = working.value
        }
    }
}
func increment(cell: *Cell) { cell.value += 1 }
func go() -> int { let meter = Meter(4) increment(&meter.value) return meter.value.value }
"""));
    }

    [Fact]
    public void ScopedMut_EmitsAccessorsButNeverADurableLocationAccessor()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    let value: int { mut { var working: int = self.storage yield &working self.storage = working } }
}
""");
        var meter = asm.GetType("Test.Meter")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Assert.NotNull(meter.GetMethod("get_value", flags));
        Assert.NotNull(meter.GetMethod("set_value", flags));
        Assert.Null(meter.GetMethod("getloca_value", flags));
    }

    [Fact]
    public void ScopedMut_RequiresExactlyOneLendPoint()
    {
        var missing = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var working: int = 0 } } }
""");
        var duplicate = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var first: int = 0 var second: int = 1 yield &first yield &second } } }
""");
        Assert.Contains(missing, d => d.Message.Contains("exactly one `yield &location`"));
        Assert.Contains(duplicate, d => d.Message.Contains("exactly one `yield &location`"));
    }

    [Fact]
    public void ScopedMut_LendShapeDiagnosticsHaveDistinctStableCodes()
    {
        var missing = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var working: int = 0 } } }
""");
        var nonLocal = Diagnostics("""
namespace Test
class Meter {
    storage: int
    let value: int { mut { yield &self.storage } }
}
""");
        var wrongType = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var working: string = "" yield &working } } }
""");

        Assert.Contains(missing, diagnostic => diagnostic.Message.StartsWith("ES2223:"));
        Assert.Contains(nonLocal, diagnostic => diagnostic.Message.StartsWith("ES2224:"));
        Assert.Contains(wrongType, diagnostic => diagnostic.Message.StartsWith("ES2225:"));
    }

    [Fact]
    public void ScopedMut_CannotEscapeAsAStoredAddress()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var working: int = 0 yield &working } } }
func bad(meter: Meter) { var escaped: *int = &meter.value }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot be returned, stored, captured, or otherwise escape"));
    }

    [Fact]
    public void ScopedMut_ReadonlyWorkingLocation_RejectsWritableBorrow()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Meter { let value: int { mut { let working: int = 0 yield &working } } }
func change(value: *int) { }
func bad(meter: Meter) { change(&meter.value) }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("immutable location"));
    }

    [Fact]
    public void ScopedMut_CannotHaveTwoLendsInOneCall()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var working: int = 0 yield &working } } }
func pair(left: *int, right: *int) { }
func bad(meter: Meter) { pair(&meter.value, &meter.value) }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("at most one scoped `mut` property"));
    }

    [Fact]
    public void ScopedMut_RejectsReturnAndClosureEscapeForms()
    {
        var returning = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var working: int = 0 return yield &working } } }
""");
        var closure = Diagnostics("""
namespace Test
class Meter { let value: int { mut { var working: int = 0 let f = func() -> int { return working } yield &working } } }
""");
        Assert.Contains(returning, d => d.Message.Contains("`return` is not valid inside scoped `mut`"));
        Assert.Contains(closure, d => d.Message.Contains("cannot capture or spawn"));
    }

    [Fact]
    public void ScopedMut_ResumeRunsWhenTheBorrowingCallThrows()
    {
        Assert.Equal(5, Run("""
namespace Test
struct Cell { value: int }
class Meter {
    init(value: int) { priv self.storage: int = value }
    let value: Cell {
        mut {
            var working: Cell = Cell { value: self.storage }
            yield &working
            self.storage = working.value
        }
    }
}
func explode(cell: *Cell) { cell.value += 1 throw InvalidOperationException("boom") }
func go() -> int {
    let meter = Meter(4)
    try { explode(&meter.value) } catch (error: Exception) { }
    return meter.value.value
}
"""));
    }

    [Fact]
    public void ScopedMut_ValueCallPreservesTheCallResultAndResumes()
    {
        Assert.Equal(23, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    let value: int {
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working
        }
    }
}
func bump(value: *int) -> int { value += 2 return value + 1 }
func go() -> int { let meter = Meter(9) let result = bump(&meter.value) return result + meter.value }
"""));
    }

    [Fact]
    public void ScopedMut_BorrowEvaluatesThePropertyReceiverExactlyOnce()
    {
        Assert.Equal(1, Run("""
namespace Test
var calls: int = 0
class Meter {
    init() { }
    let value: int {
        mut {
            var working: int = 1
            yield &working
        }
    }
}
func make() -> Meter { calls += 1 return Meter() }
func touch(value: *int) { value += 1 }
func go() -> int { touch(&make().value) return calls }
"""));
    }

    [Fact]
    public void ScopedMut_UsesAnOpaqueInternalLeaseInsteadOfAPropertyRefReturn()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    let value: int { mut { var working: int = self.storage yield &working self.storage = working } }
}
""");
        var meter = asm.GetType("Test.Meter")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var begin = meter.GetMethod("__mut_begin_value", flags);
        var resume = meter.GetMethod("__mut_resume_value", flags);
        Assert.NotNull(begin);
        Assert.NotNull(resume);
        Assert.True(begin!.IsAssembly);
        Assert.True(resume!.IsAssembly);
        Assert.True(begin.ReturnType.IsNestedAssembly);
        Assert.Null(meter.GetMethod("getloca_value", flags));
    }

    [Fact]
    public void InitOwnedFields_MustAgreeOnTypeAndVisibilityAcrossRootConstructors()
    {
        var mismatchedType = Diagnostics("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    init(value: string) { priv self.storage: string = value }
}
""");
        var mismatchedVisibility = Diagnostics("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    init() { pub self.storage: int = 0 }
}
""");
        Assert.Contains(mismatchedType, d => d.Message.Contains("matching types and visibility"));
        Assert.Contains(mismatchedVisibility, d => d.Message.Contains("matching types and visibility"));
    }

    [Fact]
    public void InitOwnedFields_AreInheritedByThisDelegatingConstructors()
    {
        Assert.Equal(7, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    init() : this(7) { }
    func read() -> int = self.storage
}
func go() -> int { return Meter().read() }
"""));
    }

    [Fact]
    public void CustomSetterLocation_RequiresExplicitPolicyAcknowledgement()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Meter { var value: int { set(v) => v } }
func take(value: *int) { }
func bad(meter: Meter) { take(&meter.value) }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("bypasses its `set` accessor"));
    }

    [Fact]
    public void CustomSetterLocation_ExplicitLocaAcknowledgesThePolicy()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int { loca => &self.storage set(v) => v }
}
func take(value: *int) { }
func good(meter: Meter) { take(&meter.value) }
""");
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("bypasses its `set` accessor"));
    }

    [Fact]
    public void MemberVar_RemainsAPropertyAcrossAnAsyncInterfaceMethod()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System"
using "System.Threading.Tasks"
class Scope : IAsyncDisposable {
    var closed: bool
    init() { self.closed = false }
    func DisposeAsync() -> ValueTask {
        await Task.Delay(1)
        self.closed = true
    }
}
func make() -> Scope = Scope()
""");
        var scope = EsHarness.Invoke(asm, "make")!;
        ((IAsyncDisposable)scope).DisposeAsync().AsTask().GetAwaiter().GetResult();
        var property = scope.GetType().GetProperty("closed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(property);
        Assert.Equal(true, property!.GetValue(scope));
        Assert.Null(scope.GetType().GetField("closed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void ScopedMut_GenericOwnerRetainsTheClosedPropertyType()
    {
        Assert.Equal(42, Run("""
namespace Test
class Box<T> {
    init(value: T) { priv self.storage: T = value }
    let value: T {
        mut {
            var working: T = self.storage
            yield &working
            self.storage = working
        }
    }
}
func increment(value: *int) { value += 1 }
func go() -> int { let box = Box<int>(41) increment(&box.value) return box.value }
"""));
    }

    [Fact]
    public void GenericStoredProperty_ClosesItsGetterAndSetterOnTheReceiver()
    {
        Assert.Equal(42, Run("""
namespace Test
class Box<T> {
    var value: T
    init(value: T) { self.value = value }
}
func go() -> int { let box = Box<int>(41) box.value += 1 return box.value }
"""));
    }

    [Fact]
    public void GenericLocaProperty_UsesTheClosedAccessorAndWritesThrough()
    {
        Assert.Equal(42, Run("""
namespace Test
class Box<T> {
    init(value: T) { priv self.storage: T = value }
    var value: T { loca => &self.storage }
}
func increment(value: *int) { value += 1 }
func go() -> int { let box = Box<int>(41) increment(&box.value) return box.value }
"""));
    }

    [Fact]
    public void DirectMut_OnALetPropertyUsesTheSelectedWritableLocation()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    let value: int { mut => &self.storage }
}
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
"""));
    }

    [Fact]
    public void LocalVarAddress_WritesThroughTheCompilerManagedLocation()
    {
        Assert.Equal(42, Run("""
namespace Test
func increment(value: *int) { value += 1 }
func go() -> int { var count: int = 41 increment(&count) return count }
"""));
    }

    [Fact]
    public void LocalLetAddress_IsReadableButCannotBeMutablyBorrowed()
    {
        Assert.Equal(41, Run("""
namespace Test
func read(value: readonly *int) -> int { return value }
func go() -> int { let count: int = 41 return read(&count) }
"""));

        var diagnostics = Diagnostics("""
namespace Test
func increment(value: *int) { value += 1 }
func bad() { let count: int = 41 increment(&count) }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("immutable location"));
    }

    [Fact]
    public void NamespaceVarProperty_ReadsAndWritesThroughItsStaticAccessors()
    {
        Assert.Equal(42, Run("""
namespace Test
var count: int = 41
func go() -> int { count += 1 return count }
"""));
    }

    [Fact]
    public void SpecificationBindingExample_SeparatesTypedLocalsFieldsAndPropertyLocations()
    {
        Assert.Equal(1011, Run("""
namespace Test
class AppConfig {
    environment: string = "development"
    let source: string = "built-in"
    var displayName: string { }
    init() { priv self.reloadStorage: int = 0 }
    var reloads: int { loca => &self.reloadStorage }
}
func addOne(value: *int) { value += 1 }
func go() -> int {
    let config = AppConfig()
    currentEnv: string = config.environment
    let configuredEnv: string = currentEnv
    var selectedEnv: string = configuredEnv
    selectedEnv = "production"
    currentEnv = selectedEnv
    var localReloads: int = 0
    addOne(&localReloads)
    addOne(&config.reloads)
    config.environment = currentEnv
    return config.environment.Length * 100 + localReloads * 10 + config.reloads
}
"""));
    }

    [Fact]
    public void CustomSetter_HasExactlyOneAttachedClrSetter()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Clamp {
    var value: int { set(v) => v < 0 ? 0 : v }
    init() { self.value = 0 }
}
""");
        var type = asm.GetType("Test.Clamp")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Assert.Equal(1, type.GetMethods(flags).Count(method => method.Name == "set_value"));
        Assert.NotNull(type.GetProperty("value", flags)!.SetMethod);
    }

    [Fact]
    public void CustomSetter_AssignmentRunsTheCustomStoragePolicy()
    {
        Assert.Equal(0, Run("""
namespace Test
class Clamp {
    var value: int { set(v) => v < 0 ? 0 : v }
    init() { self.value = 5 }
}
func go() -> int { let clamp = Clamp() clamp.value = -9 return clamp.value }
"""));
    }

    [Fact]
    public void SelfPropertyAssignment_AfterInitializationRunsTheCustomSetterPolicy()
    {
        Assert.Equal(5, Run("""
namespace Test
class Clamp {
    var value: int { set(v) => v < 0 ? 0 : v }
    init(value: int) { self.value = value }
    func replace(value: int) { self.value = value }
}
func go() -> int {
    let clamp = Clamp(5)
    initialized: int = clamp.value
    clamp.replace(-9)
    return initialized + clamp.value
}
"""));
    }

    [Fact]
    public void InitializationAssignment_BypassesTheCustomSetterButLaterSelfAssignmentDoesNot()
    {
        Assert.Equal(50, Run("""
namespace Test
class Clamp {
    var value: int { set(v) => v < 0 ? 0 : v }
    init(value: int) { self.value = value }
    func replace(value: int) { self.value = value }
}
func go() -> int {
    let clamp = Clamp(-5)
    initialized: int = clamp.value
    clamp.replace(-9)
    return initialized * -10 + clamp.value
}
"""));
    }

    [Fact]
    public void SelfPropertyAssignment_UsesTheScopedMutResumeProtocol()
    {
        Assert.Equal(105, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int {
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working + 100
        }
    }
    func replace(value: int) { self.value = value }
    func raw() -> int = self.storage
}
func go() -> int {
    let meter = Meter(1)
    meter.replace(5)
    return meter.raw()
}
"""));
    }

    [Fact]
    public void InheritedStoredProperty_ReadsThroughTheBaseAccessor()
    {
        Assert.Equal(41, Run("""
namespace Test
open class Base {
    var value: int
    init(value: int) { self.value = value }
}
class Derived : Base {
    init(value: int) : base(value) { }
    func read() -> int = self.value
}
func go() -> int { return Derived(41).read() }
"""));
    }

    [Fact]
    public void InheritedStoredProperty_WritesThroughTheBaseSetter()
    {
        Assert.Equal(42, Run("""
namespace Test
open class Base {
    var value: int
    init(value: int) { self.value = value }
}
class Derived : Base {
    init(value: int) : base(value) { }
    func bump() { self.value += 1 }
}
func go() -> int { let value = Derived(41) value.bump() return value.value }
"""));
    }

    [Fact]
    public void MemberVar_ImplementsInterfaceGetAndSetSlots()
    {
        Assert.Equal(42, Run("""
namespace Test
interface ICounter { var value: int { get set } }
class Counter : ICounter {
    pub var value: int
    init(value: int) { self.value = value }
}
func bump(counter: ICounter) -> int { counter.value += 1 return counter.value }
func go() -> int { return bump(Counter(41)) }
"""));
    }

    [Fact]
    public void MemberVarInterfaceAccessors_AreVirtualClrMethods()
    {
        var asm = EsHarness.Compile("""
namespace Test
interface ICounter { var value: int { get set } }
class Counter : ICounter {
    pub var value: int
    init(value: int) { self.value = value }
}
""");
        var type = asm.GetType("Test.Counter")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Assert.True(type.GetMethod("get_value", flags)!.IsVirtual);
        Assert.True(type.GetMethod("set_value", flags)!.IsVirtual);
    }

    [Fact]
    public void HeapPointerNestedPropertyRead_UsesThePropertyAccessProtocol()
    {
        Assert.Equal(77, Run("""
namespace Test
struct Vec2 { var x: int var y: int }
struct Transform { var position: Vec2 }
class Entity { transform: *Transform }
func go() -> int {
    let entity = Entity()
    entity.transform = new Transform { position: Vec2 { x: 33, y: 44 } }
    return entity.transform.position.x + entity.transform.position.y
}
"""));
    }

    [Fact]
    public void HeapPointerNestedPropertyWrite_UsesTheOriginalStorageLocation()
    {
        Assert.Equal(300, Run("""
namespace Test
struct Vec2 { var x: int var y: int }
struct Transform { var position: Vec2 }
class Entity { transform: *Transform }
func go() -> int {
    let entity = Entity()
    entity.transform = new Transform { position: Vec2 { x: 0, y: 0 } }
    entity.transform.position.x = 100
    entity.transform.position.y = 200
    return entity.transform.position.x + entity.transform.position.y
}
"""));
    }

    [Fact]
    public void InitOwnedField_MustBeDeclaredByEveryRootConstructor()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    init() { }
}
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("matching types and visibility"));
    }

    [Fact]
    public void PropertyCapabilities_AreEmittedAsCompilerOwnedMetadata()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var direct: int { loca => &self.storage }
    let scoped: int { mut { var working: int = self.storage yield &working self.storage = working } }
    let computed: int => self.storage + 1
}
""");
        var meter = asm.GetType("Test.Meter")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var direct = meter.GetProperty("direct", flags)!;
        var scoped = meter.GetProperty("scoped", flags)!;
        var computed = meter.GetProperty("computed", flags)!;

        var directCapability = direct.CustomAttributes.Single(a => a.AttributeType.Name == "__EsharpPropertyCapabilityAttribute");
        var scopedCapability = scoped.CustomAttributes.Single(a => a.AttributeType.Name == "__EsharpPropertyCapabilityAttribute");
        var computedCapability = computed.CustomAttributes.Single(a => a.AttributeType.Name == "__EsharpPropertyCapabilityAttribute");
        Assert.Equal(1, directCapability.ConstructorArguments.Count);
        Assert.Equal(1, scopedCapability.ConstructorArguments.Count);
        Assert.Equal(1, computedCapability.ConstructorArguments.Count);
        Assert.NotEqual(directCapability.ConstructorArguments[0].Value, scopedCapability.ConstructorArguments[0].Value);
        Assert.NotEqual(directCapability.ConstructorArguments[0].Value, computedCapability.ConstructorArguments[0].Value);
    }

    [Fact]
    public void ReferencedEsharpLocaProperty_ReconstructsItsDurableCapability()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerDurableLoaded
pub class Meter {
    init(value: int) { priv self.storage: int = value }
    pub var value: int { loca => &self.storage }
}
""", "PropertyLocationProducer");
        _ = System.Reflection.Assembly.LoadFrom(producerPath);

        var parser = new Esharp.Syntax.Parsing.Parser("""
namespace Test
using "ProducerDurableLoaded"
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
""", "property-location-consumer.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"PropertyLocationConsumer_{Guid.NewGuid():N}.dll");
        var consumer = EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound,
            "PropertyLocationConsumer", consumerPath, referencePaths: [producerPath]);
        Assert.Equal(42, EsHarness.Invoke(consumer, "go"));
    }

    [Fact]
    public void ReferencedEsharpScopedMutProperty_PreservesItsLendAndResumeProtocol()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerScopedLoaded
pub class Meter {
    init(value: int) { priv self.storage: int = value }
    pub let value: int {
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working
        }
    }
}
""", "ScopedMutProducer");
        _ = System.Reflection.Assembly.LoadFrom(producerPath);

        var parser = new Esharp.Syntax.Parsing.Parser("""
namespace Test
using "ProducerScopedLoaded"
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
""", "scoped-mut-consumer.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ScopedMutConsumer_{Guid.NewGuid():N}.dll");
        var consumer = EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound,
            "ScopedMutConsumer", consumerPath, referencePaths: [producerPath]);
        Assert.Equal(42, EsHarness.Invoke(consumer, "go"));
    }

    [Fact]
    public void ReferencedPropertyWithLocaAndScopedMut_PreservesDirectAndDurableRouting()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerDualLocation
pub class Meter {
    init(value: int) { priv self.storage: int = value }
    pub var value: int {
        loca => &self.storage
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working + 100
        }
    }
    pub func raw() -> int = self.storage
}
""", "DualPropertyLocationProducer");
        _ = System.Reflection.Assembly.LoadFrom(producerPath);

        var parser = new Esharp.Syntax.Parsing.Parser("""
namespace Test
using "ProducerDualLocation"
func increment(value: *int) { value += 1 }
func go() -> int {
    let meter = Meter(41)
    increment(&meter.value)
    var durable = &meter.value
    durable += 1
    return meter.raw()
}
""", "dual-property-location-consumer.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"DualPropertyLocationConsumer_{Guid.NewGuid():N}.dll");
        var consumer = EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound,
            "DualPropertyLocationConsumer", consumerPath, referencePaths: [producerPath]);
        Assert.Equal(143, EsHarness.Invoke(consumer, "go"));
    }

    [Fact]
    public void MetadataOnlyImport_ReconstructsScopedMutWithoutLoadingTheProducerAssembly()
    {
        // This intentionally does not call Assembly.LoadFrom(producerPath). The
        // normal compiler path imports a reference through System.Reflection.Metadata,
        // so a scoped property has to retain its capability there rather than relying
        // on the reflection-assisted mixed-language seam used by the runtime test.
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerScopedMetadata
pub class Meter {
    init(value: int) { priv self.storage: int = value }
    pub let value: int {
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working
        }
    }
}
""", "MetadataOnlyScopedMutProducer");

        const string consumerSource = """
namespace Test
using "ProducerScopedMetadata"
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
""";

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"MetadataOnlyScopedMutConsumer_{Guid.NewGuid():N}.dll");
        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var workspace = new Esharp.Compilation.Workspace("MetadataOnlyScopedMutConsumer", references);
        workspace.AddDocument("metadata-only-scoped-mut-consumer.es", consumerSource);
        var emitted = workspace.CurrentCompilation.EmitToFile(consumerPath, debugSymbols: false);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        using var consumer = Mono.Cecil.AssemblyDefinition.ReadAssembly(consumerPath);
        var go = consumer.MainModule.GetType("Test.Test")!.Methods.Single(m => m.Name == "go");
        Assert.Contains(go.Body.Instructions, instruction =>
            instruction.Operand is Mono.Cecil.MethodReference method
            && method.Name == "__mut_begin_value");
        Assert.Contains(go.Body.Instructions, instruction =>
            instruction.Operand is Mono.Cecil.MethodReference method
            && method.Name == "__mut_resume_value");
    }

    [Fact]
    public void MetadataOnlyImport_ReconstructsDurableLocaWithoutLoadingTheProducerAssembly()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerDurableMetadata
pub class Meter {
    init(value: int) { priv self.storage: int = value }
    pub var value: int { loca => &self.storage }
}
""", "MetadataOnlyLocaProducer");

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"MetadataOnlyLocaConsumer_{Guid.NewGuid():N}.dll");
        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var workspace = new Esharp.Compilation.Workspace("MetadataOnlyLocaConsumer", references);
        workspace.AddDocument("metadata-only-loca-consumer.es", """
namespace Test
using "ProducerDurableMetadata"
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
""");
        var emitted = workspace.CurrentCompilation.EmitToFile(consumerPath, debugSymbols: false);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        using var consumer = Mono.Cecil.AssemblyDefinition.ReadAssembly(consumerPath);
        var go = consumer.MainModule.GetType("Test.Test")!.Methods.Single(m => m.Name == "go");
        Assert.Contains(go.Body.Instructions, instruction =>
            instruction.Operand is Mono.Cecil.MethodReference method
            && method.Name == "getloca_value");
    }

    [Fact]
    public void MetadataOnlyImport_ReconstructsDirectMutAsADurableWritableLocation()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerDirectMutMetadata
pub class Meter {
    init(value: int) { priv self.storage: int = value }
    pub var value: int { mut => &self.storage }
}
""", "MetadataOnlyDirectMutProducer");

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"MetadataOnlyDirectMutConsumer_{Guid.NewGuid():N}.dll");
        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var workspace = new Esharp.Compilation.Workspace("MetadataOnlyDirectMutConsumer", references);
        workspace.AddDocument("direct-mut-metadata-consumer.es", """
namespace Test
using "ProducerDirectMutMetadata"
func increment(value: *int) { value += 1 }
func go(meter: Meter) { increment(&meter.value) }
""");
        var emitted = workspace.CurrentCompilation.EmitToFile(consumerPath, debugSymbols: false);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        using var consumer = Mono.Cecil.AssemblyDefinition.ReadAssembly(consumerPath);
        var go = consumer.MainModule.GetType("Test.Test")!.Methods.Single(method => method.Name == "go");
        Assert.Contains(go.Body.Instructions, instruction =>
            instruction.Operand is Mono.Cecil.MethodReference method
            && method.Name == "getloca_value");
    }

    [Fact]
    public void MetadataOnlyImport_CustomSetterStillRequiresExplicitLocationPolicy()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerCustomSetterMetadata
pub class Clamp {
    pub var value: int { set(v) => v < 0 ? 0 : v }
}
""", "MetadataOnlyCustomSetterProducer");

        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var workspace = new Esharp.Compilation.Workspace("MetadataOnlyCustomSetterConsumer", references);
        workspace.AddDocument("custom-setter-metadata-consumer.es", """
namespace Test
using "ProducerCustomSetterMetadata"
func increment(value: *int) { value += 1 }
func bad(value: Clamp) { increment(&value.value) }
""");
        var diagnostics = workspace.CurrentCompilation.GetDiagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("ES2222:"));
    }

    [Fact]
    public void MetadataOnlyImport_ImplicitLetLocationRejectsWritableBorrow()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerReadonlyMetadata
pub class Snapshot { pub let value: int = 42 }
""", "MetadataOnlyReadonlyProducer");

        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var workspace = new Esharp.Compilation.Workspace("MetadataOnlyReadonlyConsumer", references);
        workspace.AddDocument("readonly-metadata-consumer.es", """
namespace Test
using "ProducerReadonlyMetadata"
func increment(value: *int) { value += 1 }
func bad(value: Snapshot) { increment(&value.value) }
""");
        var diagnostics = workspace.CurrentCompilation.GetDiagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }

    [Fact]
    public void PropertyCapabilityMetadata_EncodesExactDurabilityAndWritableDirection()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Capabilities {
    init() {
        priv self.mutableStorage: int = 0
        priv self.readonlyStorage: int = 0
    }
    var ordinary: int
    let frozen: int
    var custom: int { set(v) => v }
    var direct: int { mut => &self.mutableStorage }
    let scoped: int { mut { let working: int = self.readonlyStorage yield &working } }
    let computed: int => self.readonlyStorage
}
""");
        var type = asm.GetType("Test.Capabilities")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        static int Bits(System.Reflection.PropertyInfo property) => (int)property.CustomAttributes
            .Single(attribute => attribute.AttributeType.Name == "__EsharpPropertyCapabilityAttribute")
            .ConstructorArguments.Single().Value!;

        Assert.Equal(0b010011, Bits(type.GetProperty("ordinary", flags)!));
        Assert.Equal(0b000011, Bits(type.GetProperty("frozen", flags)!));
        Assert.Equal(0b110001, Bits(type.GetProperty("custom", flags)!));
        Assert.Equal(0b010111, Bits(type.GetProperty("direct", flags)!));
        Assert.Equal(0b001001, Bits(type.GetProperty("scoped", flags)!));
        Assert.Equal(0, Bits(type.GetProperty("computed", flags)!));
    }

    [Fact]
    public void ClassValuedPropertyLocation_LocalAliasTracksTheLivePropertySlot()
    {
        Assert.Equal(42, Run("""
namespace Test
class View {
    init(value: int) { priv self.storage: int = value }
    func read() -> int = self.storage
}
class Owner {
    init(view: View) { priv self.viewStorage: View = view }
    var view: View { loca => &self.viewStorage }
}
func go() -> int {
    let owner = Owner(View(1))
    let location = &owner.view
    owner.view = View(42)
    return location.read()
}
"""));
    }

    [Fact]
    public void ClassValuedPropertyLocation_CaptureStoresOwnerAndProtocolInsteadOfManagedByref()
    {
        var asm = EsHarness.Compile("""
namespace Test
class View {
    init(value: int) { priv self.storage: int = value }
    func read() -> int = self.storage
}
class Owner {
    init(view: View) { priv self.viewStorage: View = view }
    var view: View { loca => &self.viewStorage }
}
func go() -> int {
    let owner = Owner(View(1))
    let location = &owner.view
    let observe = func() -> int { return location.read() }
    owner.view = View(42)
    return observe()
}
""");

        Assert.Equal(42, EsHarness.Invoke(asm, "go"));
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var display = asm.GetTypes().Single(type => type.Name.Contains("Display", StringComparison.Ordinal));
        Assert.DoesNotContain(display.GetFields(fields), field => field.FieldType.IsByRef);
        Assert.Contains(display.GetFields(fields), field => field.FieldType.Name == "Owner");
    }

    [Fact]
    public void ClassPointerBoundary_RejectsStarLocalAndFieldButNotPropertyLocation()
    {
        var star = Diagnostics("""
namespace Test
class View { init() { } }
func bad(value: *View) { }
""");
        var local = Diagnostics("""
namespace Test
class View { init() { } }
func bad() { let view = View() let location = &view }
""");
        var field = Diagnostics("""
namespace Test
class View { init() { } }
class Owner { view: View init(view: View) { self.view = view } }
func bad(owner: Owner) { let location = &owner.view }
""");
        var property = Diagnostics("""
namespace Test
class View { init() { } }
class Owner {
    init(view: View) { priv self.storage: View = view }
    var view: View { loca => &self.storage }
}
func good(owner: Owner) { let location = &owner.view }
""");

        Assert.Contains(star, diagnostic => diagnostic.Message.Contains("ES2003") && diagnostic.Message.Contains("View"));
        Assert.Contains(local, diagnostic => diagnostic.Message.Contains("ES2003") && diagnostic.Message.Contains("class local 'view'"));
        Assert.Contains(field, diagnostic => diagnostic.Message.Contains("class field 'view'"));
        Assert.Empty(property);
    }

    [Fact]
    public void ValuePropertyLocation_CapturedVarMutatesTheSelectedPropertySlot()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int { loca => &self.storage }
}
func go() -> int {
    let meter = Meter(41)
    var location = &meter.value
    let increment = func() { location += 1 }
    increment()
    return meter.value
}
"""));
    }

    [Fact]
    public void PropertyLocation_AcrossAwaitStoresOwnerInsteadOfManagedByref()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int { loca => &self.storage }
}
func go() -> Task<int> {
    let meter = Meter(41)
    var location = &meter.value
    await Task.Delay(1)
    location += 1
    return meter.value
}
""");

        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(asm, "go")));
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = asm.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(fields), field => field.FieldType.IsByRef);
        Assert.Contains(machine.GetFields(fields), field => field.FieldType.Name == "Meter"
            && field.Name.Contains("property_location_owner", StringComparison.Ordinal));
    }

    [Fact]
    public void PropertyLocationAlias_StaysAByRefBoundLocalBeforeAsyncLowering()
    {
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(new Esharp.Syntax.Parsing.Parser("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int { loca => &self.storage }
}
func go() -> Task<int> {
    let meter = Meter(41)
    var first = &meter.value
    var live = first
    await Task.Delay(1)
    live += 1
    return meter.value
}
""", "property-location-async-alias.es").ParseCompilationUnit());

        Assert.Empty(binder.Diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
        var function = Assert.IsType<Esharp.BoundTree.BoundFunctionDeclaration>(bound.Members
            .Single(member => member is Esharp.BoundTree.BoundFunctionDeclaration { Name: "go" }));
        var live = Assert.IsType<Esharp.BoundTree.BoundVariableDeclaration>(function.Body.Statements
            .Single(statement => statement is Esharp.BoundTree.BoundVariableDeclaration { Name: "live" }));
        Assert.IsType<Esharp.BoundTree.ByRefBoundType>(live.DeclaredType);
        var initializer = Assert.IsType<Esharp.BoundTree.BoundNameExpression>(live.Initializer);
        Assert.IsType<Esharp.BoundTree.ByRefBoundType>(initializer.Type);
    }

    [Fact]
    public void PropertyLocationAlias_AcrossAwaitReacquiresTheSameLocationFromItsSavedOwner()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int { loca => &self.storage }
}
func go() -> Task<int> {
    let meter = Meter(41)
    var first = &meter.value
    var live = first
    await Task.Delay(1)
    live += 1
    return meter.value
}
""");

        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(asm, "go")));
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = asm.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(fields), field => field.FieldType.IsByRef);
        Assert.Contains(machine.GetFields(fields), field => field.FieldType.Name == "Meter"
            && field.Name.Contains("property_location_owner_first", StringComparison.Ordinal));
    }

    [Fact]
    public void PropertyLocationAliasChain_AcrossAwaitUsesOneSavedReceiver()
    {
        Assert.Equal(42, EsHarness.Await(Run("""
namespace Test
using "System.Threading.Tasks"
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int { loca => &self.storage }
}
func go() -> Task<int> {
    let meter = Meter(41)
    var first = &meter.value
    var middle = first
    var live = middle
    await Task.Delay(1)
    live += 1
    return meter.value
}
""")));
    }

    [Fact]
    public void ClassValuedPropertyLocationAlias_AcrossAwaitRemainsAnOpaqueOwnerProtocol()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"
class View {
    init(value: int) { priv self.storage: int = value }
    func read() -> int = self.storage
}
class Owner {
    init(view: View) { priv self.storage: View = view }
    var view: View { loca => &self.storage }
}
func go() -> Task<int> {
    let owner = Owner(View(1))
    let first = &owner.view
    let live = first
    await Task.Delay(1)
    owner.view = View(42)
    return live.read()
}
""");

        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(asm, "go")));
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = asm.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(fields), field => field.FieldType.IsByRef);
        Assert.DoesNotContain(machine.GetFields(fields), field => field.FieldType.Name.Contains("__Ptr_View", StringComparison.Ordinal));
        Assert.Contains(machine.GetFields(fields), field => field.FieldType.Name == "Owner"
            && field.Name.Contains("property_location_owner_first", StringComparison.Ordinal));
    }

    [Fact]
    public void ImmutablePropertyLocationAlias_RejectsMutationBeforeClosureOrAsyncLowering()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int { loca => &self.storage }
}
func bad(meter: Meter) {
    let location = &meter.value
    location += 1
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable binding 'location'"));
    }

    [Fact]
    public void GenericPointerInstantiation_CannotSmuggleAClassPastES2003()
    {
        var diagnostics = Diagnostics("""
namespace Test
class View { init() { } }
func retain<T>(location: *T) -> *T = location
func bad(view: View) { let escaped = retain<View>(&view) }
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("ES2003")
            && diagnostic.Message.Contains("retain<View>")
            && diagnostic.Message.Contains("*View"));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("ES0900"));
    }

    [Fact]
    public void GuidePropertyProject_CompilesAndRunsAsDocumented()
    {
        Assert.Equal(42, Run("""
namespace Test

class Meter {
    let label: string = "requests"
    var limit: int = 100
    let full: bool => self.value >= self.limit

    init(value: int) {
        priv self.storage: int = value
    }

    var value: int {
        loca => &self.storage
    }

    let guarded: int {
        mut {
            var working: int = self.storage
            yield &working
            if working < 0 { self.storage = 0 } else { self.storage = working }
        }
    }
}

func addOne(value: *int) { value += 1 }

func go() -> int {
    let meter = Meter(40)
    addOne(&meter.value)
    addOne(&meter.guarded)
    return meter.value
}
"""));
    }

    [Fact]
    public void ImplicitStoredVar_GetSetAndLocaShareOneBackingSlot()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Counter {
    var value: int = 40
}
func increment(value: *int) { value += 1 }
func go() -> int {
    let counter = Counter()
    counter.value = 41
    increment(&counter.value)
    return counter.value
}
""");

        Assert.Equal(42, EsHarness.Invoke(asm, "go"));
        var counter = asm.GetType("Test.Counter")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var property = counter.GetProperty("value", flags)!;
        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        Assert.True(counter.GetMethod("getloca_value", flags)!.ReturnType.IsByRef);
        Assert.Single(counter.GetFields(flags), field => field.Name == "<value>k__BackingField");
        Assert.Null(counter.GetField("value", flags));
    }

    [Fact]
    public void ImplicitStoredLet_GetInitAndReadonlyLocaShareOneBackingSlot()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Snapshot {
    let value: int = 41
}
func observe(value: readonly *int) -> int { return value }
func go() -> int {
    let snapshot = Snapshot()
    return observe(&snapshot.value)
}
""");

        Assert.Equal(41, EsHarness.Invoke(asm, "go"));
        var snapshot = asm.GetType("Test.Snapshot")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var property = snapshot.GetProperty("value", flags)!;
        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        Assert.True(snapshot.GetMethod("getloca_value", flags)!.ReturnType.IsByRef);
        Assert.Single(snapshot.GetFields(flags), field => field.Name == "<value>k__BackingField");

        var diagnostics = Diagnostics("""
namespace Test
class Snapshot { let value: int = 41 }
func increment(value: *int) { value += 1 }
func bad(snapshot: Snapshot) { increment(&snapshot.value) }
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }

    [Fact]
    public void ImplicitStoredVar_LocaAliasesSubsequentSetterWrites()
    {
        Assert.Equal(42, Run("""
namespace Test
class Counter { var value: int = 1 }
func read(value: readonly *int) -> int { return value }
func go() -> int {
    let counter = Counter()
    let location = &counter.value
    counter.value = 42
    return read(location)
}
"""));
    }

    [Fact]
    public void ImplicitStoredVar_LocaEvaluatesTheReceiverExactlyOnce()
    {
        Assert.Equal(142, Run("""
namespace Test
var calls: int = 0
class Counter { var value: int = 41 }
func make() -> Counter { calls += 1 return Counter() }
func increment(value: *int) { value += 1 }
func go() -> int {
    increment(&make().value)
    return calls * 100 + 42
}
"""));
    }

    [Fact]
    public void ImplicitStoredStructVar_NestedMutationUsesThePropertyLocation()
    {
        Assert.Equal(42, Run("""
namespace Test
struct Cell { value: int }
class Holder { var cell: Cell = Cell { value: 40 } }
func increment(cell: *Cell) { cell.value += 1 }
func go() -> int {
    let holder = Holder()
    holder.cell.value += 1
    increment(&holder.cell)
    return holder.cell.value
}
"""));
    }

    [Fact]
    public void ImplicitNamespaceVar_GetSetAndLocaShareOneStaticBackingSlot()
    {
        var asm = EsHarness.Compile("""
namespace Test
var count: int = 40
func increment(value: *int) { value += 1 }
func go() -> int {
    count = 41
    increment(&count)
    return count
}
""");

        Assert.Equal(42, EsHarness.Invoke(asm, "go"));
        var host = asm.GetType("Test.Test")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var property = host.GetProperty("count", flags)!;
        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        var loca = host.GetMethod("getloca_count", flags);
        Assert.NotNull(loca);
        Assert.True(loca!.IsStatic);
        Assert.True(loca.ReturnType.IsByRef);
        Assert.Single(host.GetFields(flags), field => field.Name == "<count>k__BackingField");
    }

    [Fact]
    public void ImplicitNamespaceLet_GetInitAndReadonlyLocaRejectWritableBorrow()
    {
        var asm = EsHarness.Compile("""
namespace Test
let answer: int = 42
func observe(value: readonly *int) -> int { return value }
func go() -> int { return observe(&answer) }
""");

        Assert.Equal(42, EsHarness.Invoke(asm, "go"));
        var host = asm.GetType("Test.Test")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Assert.NotNull(host.GetProperty("answer", flags)!.GetMethod);
        Assert.True(host.GetMethod("getloca_answer", flags)!.ReturnType.IsByRef);

        var diagnostics = Diagnostics("""
namespace Test
let answer: int = 42
func increment(value: *int) { value += 1 }
func bad() { increment(&answer) }
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }

    [Fact]
    public void MetadataOnlyImport_ReconstructsImplicitStoredVarLoca()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerImplicitLoca
pub class Counter {
    pub var value: int = 41
}
""", "ImplicitLocaProducer");

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ImplicitLocaConsumer_{Guid.NewGuid():N}.dll");
        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var workspace = new Esharp.Compilation.Workspace("ImplicitLocaConsumer", references);
        workspace.AddDocument("implicit-loca-consumer.es", """
namespace Test
using "ProducerImplicitLoca"
func increment(value: *int) { value += 1 }
func go() -> int {
    let counter = Counter()
    increment(&counter.value)
    return counter.value
}
""");
        var emitted = workspace.CurrentCompilation.EmitToFile(consumerPath, debugSymbols: false);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        using var consumer = Mono.Cecil.AssemblyDefinition.ReadAssembly(consumerPath);
        var go = consumer.MainModule.GetType("Test.Test")!.Methods.Single(method => method.Name == "go");
        Assert.Contains(go.Body.Instructions, instruction =>
            instruction.Operand is Mono.Cecil.MethodReference method
            && method.Name == "getloca_value");
    }

    [Fact]
    public void MetadataOnlyImport_PreservesImplicitStoredLetReadonlyLoca()
    {
        var producerPath = EsHarness.CompileToPath("""
namespace ProducerImplicitReadonlyLoca
pub class Snapshot {
    pub let value: int = 42
}
""", "ImplicitReadonlyLocaProducer");

        var consumerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ImplicitReadonlyLocaConsumer_{Guid.NewGuid():N}.dll");
        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var workspace = new Esharp.Compilation.Workspace("ImplicitReadonlyLocaConsumer", references);
        workspace.AddDocument("implicit-readonly-loca-consumer.es", """
namespace Test
using "ProducerImplicitReadonlyLoca"
func observe(value: readonly *int) -> int { return value }
func go() -> int { return observe(&Snapshot().value) }
""");
        var emitted = workspace.CurrentCompilation.EmitToFile(consumerPath, debugSymbols: false);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        using var consumer = Mono.Cecil.AssemblyDefinition.ReadAssembly(consumerPath);
        var go = consumer.MainModule.GetType("Test.Test")!.Methods.Single(method => method.Name == "go");
        Assert.Contains(go.Body.Instructions, instruction =>
            instruction.Operand is Mono.Cecil.MethodReference method
            && method.Name == "getloca_value");
    }

    [Fact]
    public void BareNamespaceTypedDeclaration_EmitsMutableStaticFieldNotProperty()
    {
        var asm = EsHarness.Compile("""
namespace Test
count: int = 41
func go() -> int {
    count += 1
    return count
}
""");

        Assert.Equal(42, EsHarness.Invoke(asm, "go"));
        var host = asm.GetType("Test.Test")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var field = host.GetField("count", flags);
        Assert.NotNull(field);
        Assert.True(field!.IsStatic);
        Assert.False(field.IsInitOnly);
        Assert.Null(host.GetProperty("count", flags));
        Assert.Null(host.GetField("<count>k__BackingField", flags));
    }

    [Fact]
    public void BareNamespaceTypedDeclaration_CanUseAPropertyInitializer()
    {
        Assert.Equal(11, Run("""
namespace Test
class AppConfig { var environment: string = "production" }
config: AppConfig = AppConfig()
currentEnv: string = config.environment
func go() -> int {
    currentEnv = "development"
    return currentEnv.Length
}
"""));
    }

    [Fact]
    public void ImplicitNamespaceLet_UsesAClrInitOnlySetter()
    {
        var asm = EsHarness.Compile("""
namespace Test
let answer: int = 42
""");

        var host = asm.GetType("Test.Test")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var setter = host.GetProperty("answer", flags)!.SetMethod;
        Assert.NotNull(setter);
        Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit),
            setter!.ReturnParameter.GetRequiredCustomModifiers());
    }

    [Fact]
    public void ImplicitNamespaceLet_CanBeInitializedAfterSemanticSymbolRegistration()
    {
        Assert.Equal(42, Run("""
namespace Test
let answer: int { }
init { answer = 42 }
func go() -> int = answer
"""));
    }

    [Fact]
    public void NamespaceInit_DoesNotTreatAShadowingLetLocalAsPropertyInitialization()
    {
        var diagnostics = Diagnostics("""
namespace Test
let status: string { }
init {
    let status = "local"
    status = "changed"
}
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable binding 'status'"));
    }

    [Fact]
    public void ImplicitNamespaceProperties_EmitDurableCapabilityMetadata()
    {
        var asm = EsHarness.Compile("""
namespace Test
let answer: int = 42
var count: int = 41
""");

        var host = asm.GetType("Test.Test")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        foreach (var name in new[] { "answer", "count" })
        {
            var capability = host.GetProperty(name, flags)!.CustomAttributes.Single(attribute =>
                attribute.AttributeType.Name == "__EsharpPropertyCapabilityAttribute");
            var bits = (int)capability.ConstructorArguments.Single().Value!;
            Assert.NotEqual(0, bits & 0b000010);
        }
    }

    [Fact]
    public void ExplicitLocaAndScopedMut_DirectBorrowUsesScopedMut()
    {
        Assert.Equal(142, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int {
        loca => &self.storage
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working + 100
        }
    }
    func raw() -> int = self.storage
}
func increment(value: *int) { value += 1 }
func go() -> int {
    let meter = Meter(41)
    increment(&meter.value)
    return meter.raw()
}
"""));
    }

    [Fact]
    public void ExplicitLocaAndScopedMut_StoredLocationUsesDurableLoca()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int {
        loca => &self.storage
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working + 100
        }
    }
    func raw() -> int = self.storage
}
func go() -> int {
    let meter = Meter(41)
    var location = &meter.value
    location += 1
    return meter.raw()
}
"""));
    }

    [Fact]
    public void ExplicitLocaAndScopedMut_CapturedLocationUsesDurableLoca()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int {
        loca => &self.storage
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working + 100
        }
    }
    func raw() -> int = self.storage
}
func go() -> int {
    let meter = Meter(41)
    var location = &meter.value
    let increment = func() { location += 1 }
    increment()
    return meter.raw()
}
"""));
    }

    [Fact]
    public void CanonicalPrinter_PreservesFieldAndStoredPropertyRepresentations()
    {
        const string source = """
namespace Test
count: int = 1
let answer: int = 42
var total: int = 0
class Record {
    field: int
    let snapshot: int = 1
    var value: int = 2
}
""";
        var firstParser = new Esharp.Syntax.Parsing.Parser(source, "property-kinds.es");
        var first = firstParser.ParseCompilationUnit();
        Assert.Empty(firstParser.Diagnostics);
        var canonical = Esharp.Syntax.SyntaxPrinter.PrintCanonical(first);
        var secondParser = new Esharp.Syntax.Parsing.Parser(canonical, "property-kinds.es");
        var second = secondParser.ParseCompilationUnit();
        Assert.Empty(secondParser.Diagnostics);

        var firstKinds = first.Members.OfType<Esharp.Syntax.NamespaceStateDeclarationSyntax>()
            .Select(member => (member.Name, IsProperty: member.Property is not null, member.Mutable));
        var secondKinds = second.Members.OfType<Esharp.Syntax.NamespaceStateDeclarationSyntax>()
            .Select(member => (member.Name, IsProperty: member.Property is not null, member.Mutable));
        Assert.Equal(firstKinds, secondKinds);

        var firstFields = first.Members.OfType<Esharp.Syntax.DataDeclarationSyntax>().Single().Fields
            .Select(field => (field.Name, IsProperty: field.Property is not null, field.Mutable));
        var secondFields = second.Members.OfType<Esharp.Syntax.DataDeclarationSyntax>().Single().Fields
            .Select(field => (field.Name, IsProperty: field.Property is not null, field.Mutable));
        Assert.Equal(firstFields, secondFields);
    }

    [Fact]
    public void ExplicitLocaAndScopedMut_AsyncLocationUsesDurableLoca()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"
class Meter {
    init(value: int) { priv self.storage: int = value }
    var value: int {
        loca => &self.storage
        mut {
            var working: int = self.storage
            yield &working
            self.storage = working + 100
        }
    }
    func raw() -> int = self.storage
}
func go() -> Task<int> {
    let meter = Meter(41)
    var location = &meter.value
    await Task.Delay(1)
    location += 1
    return meter.raw()
}
""");

        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(asm, "go")));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = asm.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(flags), field => field.FieldType.IsByRef);
    }

    [Fact]
    public void CapturedExplicitLoca_BypassesTheAcknowledgedCustomSetterPolicy()
    {
        Assert.Equal(-9, Run("""
namespace Test
class Clamp {
    init(value: int) { priv self.storage: int = value }
    var value: int {
        loca => &self.storage
        set(v) => if v < 0 { 0 } else { v }
    }
    func raw() -> int = self.storage
}
func go() -> int {
    let clamp = Clamp(5)
    var location = &clamp.value
    let write = func() { location = -9 }
    write()
    return clamp.raw()
}
"""));
    }

    [Fact]
    public void SemanticSymbols_ReportFieldsPropertiesAndLocalRepresentations()
    {
        const string source = """
namespace Test
class Meter {
    field: int
    var value: int = 1
    let guarded: int {
        mut {
            var working: int = self.field
            yield &working
            self.field = working
        }
    }
}
func go() -> int {
    snapshot: int = 1
    let frozen: int = 2
    var writable: int = 3
    return snapshot + frozen + writable
}
""";
        using var workspace = new Esharp.Compilation.Workspace("PropertySymbolSurface");
        var document = workspace.AddDocument("property-symbols.es", source);
        var model = workspace.CurrentCompilation.GetSemanticModel();
        var text = document.Text.Content;
        static int Position(string text, string needle) => text.IndexOf(needle, StringComparison.Ordinal);

        var field = Assert.IsType<Esharp.Symbols.FieldSymbol>(
            model.GetSymbolAt("property-symbols.es", Position(text, "field: int")));
        var value = Assert.IsType<Esharp.Symbols.FieldSymbol>(
            model.GetSymbolAt("property-symbols.es", Position(text, "value: int")));
        var guarded = Assert.IsType<Esharp.Symbols.FieldSymbol>(
            model.GetSymbolAt("property-symbols.es", Position(text, "guarded: int")));
        Assert.Equal(Esharp.Symbols.SymbolKind.Field, field.Kind);
        Assert.Equal(Esharp.Symbols.SymbolKind.Property, value.Kind);
        Assert.Equal(Esharp.Symbols.SymbolKind.Property, guarded.Kind);
        Assert.False(((Esharp.Symbols.IFieldSymbol)field).HasGeneratedAccessors);
        Assert.True(((Esharp.Symbols.IFieldSymbol)value).HasDurableLocation);
        Assert.True(((Esharp.Symbols.IFieldSymbol)guarded).HasScopedMutation);
        Assert.False(((Esharp.Symbols.IFieldSymbol)guarded).HasDurableLocation);
        Assert.Equal("field", Esharp.Diagnostics.Semantics.SymbolDisplay.DescribeKind(field));
        Assert.Equal("durable location property", Esharp.Diagnostics.Semantics.SymbolDisplay.DescribeKind(value));
        Assert.Equal("scoped location property", Esharp.Diagnostics.Semantics.SymbolDisplay.DescribeKind(guarded));

        var snapshot = Assert.IsType<Esharp.Symbols.LocalSymbol>(
            model.GetSymbolAt("property-symbols.es", Position(text, "snapshot: int")));
        var frozen = Assert.IsType<Esharp.Symbols.LocalSymbol>(
            model.GetSymbolAt("property-symbols.es", Position(text, "frozen: int")));
        var writable = Assert.IsType<Esharp.Symbols.LocalSymbol>(
            model.GetSymbolAt("property-symbols.es", Position(text, "writable: int")));
        Assert.False(((Esharp.Symbols.ILocalSymbol)snapshot).IsAddressable);
        Assert.True(((Esharp.Symbols.ILocalSymbol)frozen).IsAddressable);
        Assert.True(((Esharp.Symbols.ILocalSymbol)writable).IsAddressable);
        Assert.Equal("snapshot: int", Esharp.Diagnostics.Semantics.SymbolDisplay.Describe(snapshot));
        Assert.Equal("let frozen: int", Esharp.Diagnostics.Semantics.SymbolDisplay.Describe(frozen));
        Assert.Equal("var writable: int", Esharp.Diagnostics.Semantics.SymbolDisplay.Describe(writable));
    }

    [Fact]
    public void SemanticSymbols_IncludeNamespaceFieldsAndProperties()
    {
        const string source = """
namespace Test
version: int = 1
let answer: int = 42
var count: int = 0
func go() -> int { count += 1 return version + answer + count }
""";
        using var workspace = new Esharp.Compilation.Workspace("NamespacePropertySymbolSurface");
        var document = workspace.AddDocument("namespace-property-symbols.es", source);
        var model = workspace.CurrentCompilation.GetSemanticModel();
        var text = document.Text.Content;
        static int Position(string text, string needle) => text.IndexOf(needle, StringComparison.Ordinal);

        var version = Assert.IsType<Esharp.Symbols.FieldSymbol>(
            model.GetSymbolAt("namespace-property-symbols.es", Position(text, "version: int")));
        var answer = Assert.IsType<Esharp.Symbols.FieldSymbol>(
            model.GetSymbolAt("namespace-property-symbols.es", Position(text, "answer: int")));
        var count = Assert.IsType<Esharp.Symbols.FieldSymbol>(
            model.GetSymbolAt("namespace-property-symbols.es", Position(text, "count: int")));
        Assert.Equal(Esharp.Symbols.SymbolKind.Field, version.Kind);
        Assert.Equal(Esharp.Symbols.SymbolKind.Property, answer.Kind);
        Assert.Equal(Esharp.Symbols.SymbolKind.Property, count.Kind);
        Assert.Equal(Esharp.Symbols.TypeSymbolKind.NamespaceHost, answer.DeclaringType.TypeKind);
        Assert.Contains(model.LookupSymbolsInScope("namespace-property-symbols.es",
            Position(text, "return version")), symbol => symbol.Name == "answer");
    }
}
