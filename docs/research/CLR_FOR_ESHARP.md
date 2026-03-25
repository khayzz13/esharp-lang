# The CLR Through E#'s Lens

How E# maps to the CLR, what it exploits, what it leaves alone, and what it can reach that C# can't. This document connects CLR architecture to concrete E# language decisions.

## Value Types Are the Default — And the CLR Agrees

E#'s `data` emits as a CLR value type (struct). This isn't a workaround — it's what the CLR was designed for.

At the IL level, value types extend `System.ValueType`. They're laid out inline — on the stack as locals, embedded directly in containing structs, stored contiguously in arrays. No object header, no vtable pointer, no GC tracking per instance. A `data Vec2 { x: int, y: int }` is 8 bytes. Period. The equivalent `class Vec2` would be 8 bytes of payload + 16 bytes of object header + a GC reference to track. Three times the memory, plus GC pressure.

The CLR supports three layout modes for value types:
- **Sequential** (what E# uses) — fields in declaration order, with optional packing
- **Auto** — runtime chooses layout for optimal alignment
- **Explicit** — programmer specifies byte offset per field (union-style overlapping)

E# uses sequential layout because it's predictable. The programmer knows the memory layout matches the declaration. This matters for interop, serialization, and cache behavior.

**What E# does today:**
```
data Packet {
    header: uint
    length: int
    flags: byte
}
```
Emits as:
```il
.class public sequential ansi sealed beforefieldinit Esharp.Generated.Packet
    extends [System.Runtime]System.ValueType
{
    .field public uint32 header
    .field public int32 length
    .field public uint8 flags
}
```

**What E# could do with explicit layout:**
```
data NetworkHeader {
    @offset(0) raw: uint
    @offset(0) version: byte      // overlaps raw at byte 0
    @offset(1) headerLen: byte    // overlaps raw at byte 1
    @offset(2) totalLen: ushort   // overlaps raw at bytes 2-3
}
```
This is a C-style union — different views of the same memory. The CLR supports this natively via `StructLayout.Explicit` and `FieldOffset`. C# can do it but requires `[StructLayout(LayoutKind.Explicit)]` and `[FieldOffset(N)]` attributes on every field. E# could make it a natural part of the data declaration.

## Direct Dispatch Is a Branch, Not a Lookup

E#'s `func` emits as a static method. The call instruction is `call` (direct), not `callvirt` (virtual). The difference at the CPU level: `call` is a direct branch to a known address. `callvirt` loads a vtable pointer, indexes into it, then branches. Even with JIT devirtualization, `callvirt` starts slower and only gets optimized when the JIT can prove the concrete type.

```
func add(a: int, b: int) -> int {
    return a + b
}
```

IL:
```il
// Caller:
ldarg.0
ldarg.1
call int32 Esharp.Generated.Module::add(int32, int32)
```

No vtable, no indirection, no devirtualization needed. The JIT sees a direct call and can inline it immediately. This is the dispatch model for all E# module functions.

**Instance methods (promoted)** also use `call`, not `callvirt`, because they're on structs. Value type methods don't need virtual dispatch — there's no inheritance hierarchy to resolve. The `this` parameter is passed as a managed pointer (`&`), not a reference:

```il
// E# instance method on a struct:
ldloca.s myVec    // push address of the struct
call instance int32 Esharp.Generated.Vec2::magnitude()
```

Compare to C# class method:
```il
// C# instance method on a class:
ldloc.0           // push object reference
callvirt instance int32 MyApp.Vec2::Magnitude()
```

The `callvirt` on a class does a null check + vtable lookup even for non-virtual methods (C# always emits `callvirt` for null checking). E#'s struct `call` does neither.

## Tail Calls — What C# Left on the Table

The CLR has a `tail.` prefix instruction. When placed before a `call`, it tells the JIT to reuse the current stack frame for the callee — guaranteed tail call optimization. The stack doesn't grow. Recursive functions can run indefinitely.

C# never emits `tail.`. Roslyn's position is that tail calls are an optimization the JIT should decide, not the programmer. But the JIT's tail call heuristics are conservative — it won't tail-call if there are locals to clean up, if the calling conventions differ, or if it can't prove the optimization is safe. For recursive algorithms, this means stack overflow.

E#'s IL compiler detects tail position calls and emits `tail.`:

```
func countdown(n: int) -> int {
    if n <= 0 { return 0 }
    return countdown(n - 1)    // tail position
}
```

IL:
```il
    ldarg.0
    ldc.i4.1
    sub
    tail.
    call int32 Esharp.Generated.Test::countdown(int32)
    ret
```

The `tail.` prefix guarantees the JIT reuses the stack frame. `countdown(1_000_000)` works. Without `tail.`, it stack overflows around 10,000-50,000 depending on stack size.

**Rules for tail call emission:**
- The call must be immediately followed by `ret`
- The call's return type must match the function's return type
- Cannot be inside a `try`/`catch`/`finally` block (CLR restriction — exception handlers need their stack frame preserved)
- Cannot be inside a `defer` block (which emits as `try`/`finally`)

**What the transpiler can't do:** C# has no syntax for `tail.`. There's no attribute, no keyword, no pragma. The transpiler path cannot emit tail calls. This is the first concrete feature where the IL compiler produces fundamentally different behavior from the transpiler.

## Function Pointers — Zero-Allocation Dispatch

The CLR has two instructions for function pointers:
- `ldftn <method>` — push a pointer to a specific method
- `calli <signature>` — call through a function pointer on the stack

These are the foundation of zero-allocation dispatch. A `Func<int, int>` delegate is a heap-allocated object with a method pointer and a target reference. A function pointer via `ldftn` is just a native integer — no allocation, no GC tracking.

E#'s `&funcName` syntax maps directly:

```
func double(x: int) -> int { return x * 2 }

func apply(ptr: ???, value: int) -> int {
    // call through function pointer
}

let ptr = &double   // ldftn
```

IL:
```il
ldftn int32 Esharp.Generated.Module::double(int32)
// native int on the stack — that's the function pointer
```

**C# comparison:** C# 9 added `delegate*<int, int>` but requires `unsafe` context because C# can't prove the pointer is valid. E# can prove it — the compiler knows the exact signature of every function, so `&double` is type-safe by construction even though the IL is technically unverifiable.

**Where this leads — dispatch tables:**
```
// Future E# pattern:
data Handler {
    name: string
    execute: &func(Request) -> Response   // function pointer field
}
```
An array of `Handler` structs with embedded function pointers. Zero allocation per dispatch. The struct is on the stack or inline in the array. The function pointer is a native int. The whole dispatch table is cache-friendly — handlers are contiguous in memory, pointers are adjacent to their metadata.

Compare to C#'s `Dictionary<string, Func<Request, Response>>` — hash table of heap-allocated delegates pointing to method groups. Orders of magnitude more allocation and indirection.

## Choice Types — Tagged Unions as the CLR Intends

E#'s `choice` emits as a tag enum + value type struct. This is how the CLR naturally represents sum types:

```
choice ConnectionState {
    disconnected
    connecting
    connected
    failed(reason: string)
}
```

Emits as:
```il
.class public sealed auto ansi Esharp.Generated.ConnectionState_Tag
    extends [System.Runtime]System.Enum
{
    .field public int32 value__
    .field public static literal int32 disconnected = 0
    .field public static literal int32 connecting = 1
    .field public static literal int32 connected = 2
    .field public static literal int32 failed = 3
}

.class public sequential sealed Esharp.Generated.ConnectionState
    extends [System.Runtime]System.ValueType
{
    .field public ConnectionState_Tag Tag
    .field public string failed         // payload, only meaningful when Tag == failed
}
```

**Match uses IL `switch` (jump table):**
```il
ldloc tag
switch (IL_case0, IL_case1, IL_case2, IL_case3)   // O(1) dispatch
```

The IL `switch` instruction is a jump table — a single indexed branch, not cascading comparisons. C# emits cascading `if/else` or Roslyn's lowered switch which may or may not produce a jump table depending on density heuristics. E# always emits `switch` for choice matching.

**Memory efficiency:** The struct holds the tag (4 bytes) plus the largest payload. `ConnectionState` is 4 bytes (tag) + 8 bytes (string reference) = 12 bytes. All variants fit in the same space. No heap allocation per variant. No class hierarchy, no vtable, no object headers.

**What C# does instead:** C# developers model this as an abstract class with subclasses:
```csharp
abstract class ConnectionState { }
class Disconnected : ConnectionState { }
class Connected : ConnectionState { }
class Failed(string Reason) : ConnectionState;
```
Each instance is heap-allocated. Pattern matching uses `is` checks (type tests). The JIT emits type comparisons, not jump tables. More memory, more indirection, slower dispatch.

## Protocol Satisfaction — Interfaces Without the Ceremony

E#'s `protocol` emits as a CLR interface. But the satisfaction model is implicit:

```
protocol Describable {
    func describe() -> string
}

data Client {
    name: string
}

// This function's first param is Client → becomes instance method
func describe(client: Client) -> string {
    return "client: {client.name}"
}
// Client now has describe() → satisfies Describable implicitly
```

At the CLR level, the struct declares that it implements the interface:
```il
.class public sequential sealed Esharp.Generated.Client
    extends [System.Runtime]System.ValueType
    implements Esharp.Generated.Describable
{
    .field public string name

    .method public instance string describe() cil managed
    {
        // method body
    }
}
```

The binder checks if the instance methods (promoted functions) on a `data` type match all methods in a `protocol`. If they do, it adds the interface to the struct's implements list. The programmer never writes `implements Describable` — the compiler figures it out from the shape.

**Why this works at the CLR level:** The CLR's interface dispatch doesn't care how the interface was declared. It looks up the method by name and signature in the type's method table. Whether the programmer explicitly said "implements X" or the compiler inferred it, the metadata is identical.

**Constrained calls for generic dispatch:**
When E# code passes a `Client` to a function expecting `Describable`, the compiler can emit:
```il
constrained. Esharp.Generated.Client
callvirt instance string Esharp.Generated.Describable::describe()
```
The `constrained.` prefix tells the JIT: "the type is Client, resolve the interface call directly." For value types, this avoids boxing. The JIT can devirtualize to a direct call because the concrete type is known.

## The GC and E#'s Relationship To It

E#'s value-type-first model means less GC work by default. But the GC isn't the enemy — invisible allocation is.

**What the GC sees:**
- E# `data` on the stack → invisible to GC, zero cost
- E# `data` in an array → one GC object (the array), not N objects
- E# `data` containing a reference type field (like `string`) → the GC must scan the struct for references but doesn't track the struct itself
- E# `data` boxed (cast to `object` or interface) → heap allocation, GC tracks it

**Where E# still allocates:**
- String creation (`"hello {name}"` allocates a new string)
- `List<T>`, `Dictionary<K,V>` — these are reference types, their backing arrays are on the heap
- Boxing — when a value type is cast to `object` or an interface, the CLR copies it to the heap
- `spawn { }` — `Task.Run` allocates a Task object and a closure

**Where E# avoids allocation that C# wouldn't:**
- Choice types as values — no class hierarchy, no per-variant allocation
- Function pointers instead of delegates — `ldftn` vs `new Func<>()`
- Struct state machines for async (future) — `ValueTask` avoids Task allocation on sync paths
- Direct dispatch — no delegate allocation for method references

**The generational GC model:**
- Gen 0: short-lived objects. Collected frequently, cheaply. E#'s value types never enter Gen 0 (they're on the stack).
- Gen 1: survived one collection. Buffer zone.
- Gen 2: long-lived objects. Collected rarely, expensively. E#'s data types avoid Gen 2 entirely unless boxed.
- LOH (Large Object Heap): objects > 85KB. Arrays of E# structs go here if large enough — but it's one array object, not thousands of individual objects.

E# doesn't fight the GC. It makes the GC irrelevant for most data by keeping it on the stack. When heap allocation is needed (strings, collections, interop), the GC handles it normally. The difference is: C# puts everything on the heap by default and the GC works hard. E# puts most things on the stack by default and the GC barely notices.

## Async at the CLR Level — What E# Will Emit

The CLR's async machinery is not a language feature — it's a pattern implemented in IL. Any language can emit it.

**The pattern:**
1. A method returns `ValueTask<T>` (or `Task<T>`)
2. It creates a struct implementing `IAsyncStateMachine`
3. The struct has: a state field (`int`), a builder (`AsyncValueTaskMethodBuilder<T>`), and captured variables
4. The method body is in `MoveNext()` — a switch on the state field, with suspension points between awaits

**What C# emits for async:**
```csharp
async Task<int> Fetch(string url) {
    var response = await http.GetAsync(url);
    return response.StatusCode;
}
```
Roslyn generates:
- A state machine struct (in Release) or class (in Debug)
- `MoveNext()` with state 0 (before first await), state 1 (after first await)
- Builder calls: `Start`, `AwaitUnsafeOnCompleted`, `SetResult`
- The awaiter pattern: `GetAwaiter()`, `IsCompleted`, `GetResult()`, `OnCompleted()`

**What E# will emit:**
The same pattern, but:
- Always a struct (never a class — even in debug builds)
- Minimal captures — only variables that cross suspension points, determined by liveness analysis
- `ValueTask<T>` by default (not `Task<T>`) — avoids allocation when the operation completes synchronously
- No `async` keyword in the source — the compiler detects `await` and transforms automatically

**The state machine struct at the IL level:**
```il
.class private sequential sealed Esharp.Generated.fetch_StateMachine
    extends [System.Runtime]System.ValueType
    implements [System.Runtime]IAsyncStateMachine
{
    .field public int32 __state
    .field public valuetype AsyncValueTaskMethodBuilder`1<int32> __builder
    .field public string url                        // captured parameter
    .field public valuetype TaskAwaiter`1<HttpResponseMessage> __awaiter0

    .method public instance void MoveNext() cil managed
    {
        // state switch → await → resume → set result
    }
}
```

**Why struct matters:** A class state machine allocates on the heap for every async call. A struct state machine starts on the stack and only moves to the heap if the operation doesn't complete synchronously (the builder handles promotion). For operations that complete fast (cached data, buffered I/O), the struct path is zero-allocation.

## Instance Method Promotion — What It Looks Like in IL

When E# promotes a function to an instance method on a struct, the IL transformation is:

**E# source:**
```
data Vec2 { x: int, y: int }

func magnitude(v: Vec2) -> double {
    return Math.Sqrt(v.x * v.x + v.y * v.y)
}
```

**Emitted IL (transpiler path via C#):**
```il
.class public sequential sealed Esharp.Generated.Vec2
    extends [System.Runtime]System.ValueType
{
    .field public int32 x
    .field public int32 y

    .method public instance float64 magnitude() cil managed
    {
        ldarg.0          // 'this' pointer (managed pointer to struct)
        ldfld int32 Vec2::x
        ldarg.0
        ldfld int32 Vec2::x
        mul
        ldarg.0
        ldfld int32 Vec2::y
        ldarg.0
        ldfld int32 Vec2::y
        mul
        add
        conv.r8
        call float64 [System.Runtime]System.Math::Sqrt(float64)
        ret
    }
}
```

**Key detail:** `ldarg.0` in an instance method on a value type is a managed pointer (`&Vec2`), not a copy. The method operates on the original struct in-place. This is why `ref` semantics are natural for struct instance methods — the CLR already passes them by reference.

For the IL compiler, the promoted method receives `this` as an implicit first parameter. The self-param name from the E# source is rewritten to `ldarg.0`. All field accesses on the self-param become `ldarg.0` + `ldfld`.

## The JIT and E# — What Gets Optimized

The JIT (RyuJIT) performs optimizations that benefit E#'s patterns:

**Inlining:** Small methods are inlined at the call site. E#'s module functions (static, small, no virtual dispatch) are ideal inlining candidates. The JIT's heuristic favors methods with few IL bytes, no complex control flow, and known call targets — exactly what E# emits.

**Struct promotion:** When a struct is small (≤ 2 fields on most platforms), the JIT promotes its fields to registers. `data Vec2 { x: int, y: int }` becomes two register values, not a memory-resident struct. E#'s small data types benefit from this automatically.

**Bounds check elimination:** When iterating with a `for` loop, the JIT can prove the index stays within bounds and eliminate the array bounds check. E#'s `for x in collection` pattern benefits when lowered to indexed iteration.

**Devirtualization:** When the JIT can prove the concrete type of a `callvirt` target, it replaces virtual dispatch with direct call. E# rarely needs this (most calls are already direct `call`), but it matters for protocol dispatch through interfaces.

**What the JIT cannot do:**
- Escape analysis for heap-to-stack promotion (the JIT doesn't move allocations to the stack — this is an AOT opportunity)
- Aggressive dead code elimination across method boundaries
- Link-time optimization across assemblies
- Guaranteed tail calls (only respects `tail.` prefix, doesn't infer them)

This is why E#'s IL compiler matters. The JIT won't infer tail calls, won't eliminate unnecessary async state machines, won't use function pointers instead of delegates. E# makes these decisions at compile time and emits the optimal IL. The JIT's job becomes easier — less to optimize because the IL is already close to what the machine wants.

## What C# Leaves on the Table

Features in the CLR's IL instruction set that C# never or rarely emits:

| IL Feature | What it does | C# status | E# opportunity |
|---|---|---|---|
| `tail.` prefix | Reuse stack frame for tail calls | Never emitted | Implemented — automatic for tail-position calls |
| `calli` | Call through function pointer | Only via `delegate*` in unsafe | Planned — natural with `&func` syntax |
| `ldftn` | Load function pointer | Only via `delegate*` in unsafe | Implemented — `&funcName` syntax |
| Fault handler | Like finally but only on exception | Never emitted | Possible — lighter than catch-all |
| `localloc` | Stack-allocate dynamic size | Only via `stackalloc` in limited contexts | Possible — `stack[N]` syntax |
| Explicit layout overlap | Union-style field overlapping | Requires attributes + unsafe | Possible — `@offset` in data declarations |
| `SkipLocalsInit` | Don't zero-initialize locals | Requires attribute, recent C# | Could be default for perf-critical functions |
| `constrained.` callvirt | Direct interface dispatch on value types | Emitted but only by Roslyn's lowering | Explicit control in IL compiler |
| Custom calling conventions | Alternate calling conventions for interop | Limited `UnmanagedCallersOnly` | Full control via Cecil |

## NativeAOT and E#

E#'s value-type-first model is AOT-friendly by design:

- **No reflection on E# types:** `data` and `choice` types have known shapes at compile time. `derive` generates code at compile time, not via reflection. AOT trimming can remove reflection metadata for E# types.
- **Direct dispatch:** Static calls are trivially AOT-compiled. No vtable resolution needed at runtime.
- **No dynamic generic instantiation:** E#'s generics are resolved at compile time. The AOT compiler knows every concrete instantiation.
- **Small struct types:** Value types don't need GC type metadata for heap scanning if they stay on the stack.

**Where E# might conflict with AOT:**
- `spawn { }` uses `Task.Run` which requires thread pool infrastructure
- Protocol dispatch through interfaces needs vtable metadata
- String interpolation uses `string.Format` or `string.Concat` — these need runtime method resolution

The IL compiler path is naturally more AOT-friendly than the transpiler path because it controls exactly what metadata and runtime features the assembly depends on.

## The Execution Engine — Threads, Exceptions, Finalization

**Threading:** The CLR manages threads through the thread pool. `spawn { }` maps to `Task.Run` which queues work on the pool. Each managed thread has its own stack (default 1MB on 64-bit). Value types live on these stacks. The thread pool grows/shrinks based on throughput heuristics.

**Exception handling in IL:** The CLR supports four handler types in IL:
- `catch` — handle a specific exception type
- `filter` — conditional catch (evaluate an expression to decide whether to catch)
- `finally` — always execute on exit (normal or exceptional)
- `fault` — execute only on exceptional exit (C# never uses this)

E#'s `defer` maps to `finally`. A potential future `on_error { }` could map to `fault` — lighter than `catch` because it doesn't catch the exception, just runs cleanup.

Exception handlers are specified in the method body's exception handler table, not as IL instructions. The handler regions are byte offset ranges: try-start, try-end, handler-start, handler-end. This is what the IL compiler emits via Cecil's `ExceptionHandler` type.

**How `defer` interacts with tail calls:** A `tail.` prefix cannot appear inside a try/finally block. The CLR requires the stack frame to exist for the finally handler to execute. This is why E# suppresses tail call emission inside `defer` blocks — if you have cleanup, the stack frame must survive.

## String Interning and Interpolation

The CLR interns string literals — identical string tokens in the #US metadata heap share the same object at runtime. `"hello"` in two different methods points to the same string object.

E#'s string interpolation `"user {name} has {count} items"` currently emits `string.Concat(object[])`:
```il
ldc.i4 3                    // array size
newarr object               // allocate object[]
// ... store segments ...
call string string::Concat(object[])
```

This allocates an `object[]` and boxes value types. The IL compiler could instead emit `DefaultInterpolatedStringHandler` (introduced in .NET 6) which uses stack-allocated `Span<char>` buffers:
```il
// Initialize handler with literal length + hole count
ldloca.s handler
ldc.i4 10                   // literal length
ldc.i4 2                    // hole count
call instance void DefaultInterpolatedStringHandler::.ctor(int32, int32)
// Append segments without boxing
ldloca.s handler
ldstr "user "
call instance void DefaultInterpolatedStringHandler::AppendLiteral(string)
ldloca.s handler
ldloc name
call instance void DefaultInterpolatedStringHandler::AppendFormatted<string>(!!0)
// ... etc ...
ldloca.s handler
call instance string DefaultInterpolatedStringHandler::ToStringAndClear()
```

Zero boxing, stack-allocated buffer for small strings. This is what Roslyn emits for `$""` strings in modern C#. The E# IL compiler could emit the same pattern directly — another area where the IL compiler can produce better code than the current `string.Concat` approach.

## Generic Types at the CLR Level

E#'s `data Pair<A, B>` and `choice Option<T>` emit as open generic types in metadata:

```il
.class public sequential sealed Esharp.Generated.Pair`2<A, B>
    extends [System.Runtime]System.ValueType
{
    .field public !A first    // !A references type parameter A
    .field public !B second   // !B references type parameter B
}
```

**Reified generics:** Unlike the JVM (which erases generics), the CLR preserves generic type information at runtime. `Pair<int, string>` and `Pair<float, bool>` are distinct types with distinct method tables. For value type instantiations, the JIT generates specialized native code per instantiation — `Pair<int, string>` gets different machine code than `Pair<float, bool>` because the field layout differs.

**Reference type sharing:** For reference type arguments, the JIT shares native code. `Option<string>` and `Option<object>` use the same compiled `MoveNext` because all reference types are pointer-sized. This is a JIT optimization — the metadata still distinguishes them.

**What this means for E#:** Generic `data` and `choice` types get optimal JIT treatment automatically. A `Pair<int, int>` is 8 bytes with specialized machine code. The CLR's reified generics are strictly more powerful than the JVM's erased generics — E# gets this for free by targeting the CLR.

## By-Ref Parameters — The CLR's Managed Pointers

E#'s `*T` parameter syntax maps to CLR managed pointers (`&T`):

```
func increment(counter: *int) {
    counter += 1
}
```

IL:
```il
.method public static void increment(int32& counter) cil managed
{
    ldarg.0          // push managed pointer
    ldarg.0
    ldind.i4         // dereference: load int from pointer
    ldc.i4.1
    add
    stind.i4         // store int through pointer
    ret
}
```

Managed pointers (`&`) are tracked by the GC — if the GC moves the object containing the referenced memory, the pointer is updated. This is different from unmanaged pointers (`*` in C#) which are raw addresses.

**Call site:** `increment(*myCounter)` emits as:
```il
ldloca.s myCounter   // push address of local
call void Module::increment(int32&)
```

The explicit `*` at the E# call site maps to `ldloca` (address-of-local) or `ldflda` (address-of-field). The CLR guarantees safety — managed pointers can't dangle, can't be stored in fields (with the exception of `ref struct`), can't escape the stack frame. E#'s by-ref model inherits all of these safety guarantees.

## Channels and the Thread Pool

E#'s `chan<T>` wraps `System.Threading.Channels.Channel<T>`. At the CLR level:

- `Channel.CreateBounded<T>(capacity)` — allocates a channel with fixed buffer size
- `ChannelWriter<T>.TryWrite(item)` — enqueue (non-blocking)
- `ChannelReader<T>.ReadAllAsync()` — returns `IAsyncEnumerable<T>`
- `.ToBlockingEnumerable()` — converts to blocking `IEnumerable<T>` for `foreach`

The thread pool (`ThreadPool.QueueUserWorkItem` / `Task.Run`) manages the worker threads. `spawn { }` queues a work item. The pool starts with `Environment.ProcessorCount` threads and grows if threads are blocked.

**Where the CLR's channel implementation is efficient:**
- Lock-free for single-producer/single-consumer bounded channels
- Async-aware — `ReadAsync()` suspends without blocking a thread
- Backpressure — bounded channels make `WriteAsync()` wait when full

**Where E# can improve on the raw channel API:**
- `for event in ch` is currently blocking (uses `ToBlockingEnumerable`). With `await` support, it could use `ReadAllAsync` non-blockingly inside async state machines.
- `select` over multiple channels would use `Task.WhenAny` on the readers — or the IL compiler could emit a custom multiplexer that polls channels without `Task` allocation.

## The Assembly as a Deployment Unit

An E# compilation produces a standard .NET assembly — a PE file with:

- **Metadata tables:** TypeDef, MethodDef, Field, MemberRef, TypeRef, AssemblyRef — describing all types, methods, and external references
- **IL stream:** The method bodies as CIL bytecode
- **String heaps:** #Strings (identifiers), #US (user strings like string literals), #Blob (signatures), #GUID

The assembly is self-describing. Any .NET tool (ILSpy, dotPeek, Reflection) can inspect it. Any .NET language can consume it. The assembly doesn't carry any E#-specific metadata — it's indistinguishable from a C# assembly at the binary level.

This is E#'s stealth advantage for adoption: **a C# project can reference an E# assembly without knowing or caring that it was written in E#.** The dependency is just a `.dll` with standard .NET types. No interop library, no binding generator, no language-specific runtime. NuGet packages written in E# would look identical to NuGet packages written in C#.
