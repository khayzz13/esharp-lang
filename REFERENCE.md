# E# Language Reference

Complete reference for writing E#, understanding the compiler, and contributing to the language. This document describes what is implemented and working today, what is planned, and the design rationale behind each decision.

---

## Philosophy

E# is an inverted mental model for the CLR. The defaults are: value types, direct dispatch, explicit mutation, result-based errors, stack-friendly data. The full CLR (heap, GC, reflection, LINQ, async) is reachable but not the path of least resistance.

E# is not "C# minus features." It is not functional-first like F#. It is procedural — data lives in structs, behavior lives in module functions, control flow is direct, mutation is visible.

**The CLR is the product.** E# targets the runtime, not C#. JIT, AOT, GC, channels, spans, ref passing, metadata — all of it. The reason for a new language is to think about the CLR differently, not to escape it.

### Why E# Exists

C# presents one view of the CLR: class-based, heap-managed, virtual dispatch, exception-driven. It's a good view for enterprise software, web APIs, and UI frameworks. But the CLR was designed as a **Common** Language Runtime — it supports value types, direct dispatch, tail calls, function pointers, explicit layout, and more. C# uses about 60% of the IL instruction set. The rest is waiting for a language that wants it.

E# is that language. It makes the CLR's full capabilities the path of least resistance:

- **Value types by default** — because the CLR was designed around `System.ValueType` from day one, not as an optimization afterthought
- **Direct dispatch** — because `call` is a single branch instruction and `callvirt` is a vtable lookup + null check
- **Result-based errors** — because exceptions unwind the stack and require try/catch, while results flow through normal return values
- **Explicit mutation** — because knowing where state changes happen matters more than saving a keyword
- **Compile-time generation** — because the CLR's reflection system exists for interop, not as a substitute for code generation

### Inversion of Defaults

| Concern | C# default | E# default |
|---|---|---|
| Allocation | `class` (heap) | `data` → struct (stack) |
| Dispatch | virtual, through class hierarchy | direct, through module functions |
| Errors | exceptions | `Result<T, E>` with `?` propagation |
| Mutation | mutable by default, hidden | `let` immutable, `var` mutable, `*T` explicit ref |
| Concurrency | `async`/`await` (colored functions) | `spawn`/`chan`/`for..in` (Go model) |
| Metaprogramming | runtime reflection | `derive` compile-time generation |
| Type identity | class hierarchy, inheritance | `data` (product), `choice` (sum), `enum` (closed set) |
| Composition | inheritance + interfaces | protocols (implicit satisfaction) + instance method promotion |
| Function references | `Func<>` delegate (heap allocation) | `&func` pointer (zero allocation) |

### Go Influences Adapted for CLR

E# borrows Go's *feel* — small surface, procedural, explicit, boring code is good code — but targets the CLR instead of a custom runtime.

| Go concept | E# adaptation | CLR backing |
|---|---|---|
| `struct` | `data` | `System.ValueType` (sequential layout) |
| Interface satisfaction (implicit) | `protocol` | CLR interface + compiler-inferred `implements` |
| `goroutine` | `spawn { }` | `Task.Run` on the thread pool |
| `chan` | `chan<T>()` | `System.Threading.Channels.Channel<T>` |
| `for range ch` | `for event in ch` | `ReadAllAsync().ToBlockingEnumerable()` |
| `defer` | `defer { }` | `try/finally` block |
| Multiple return values | `Result<T, E>` | Single typed return (not tuples) |
| `:=` / `var` | `let` (immutable) / `var` (mutable) | Local variable declaration |
| `func (s *Server) handle()` | Instance method promotion | Instance method on `partial struct` |
| No inheritance | No inheritance | `data` + `protocol` + `choice` |
| `&x` at call site | `*x` at call site | `ref x` (managed pointer) |
| `select` | `select { }` (planned) | `Task.WhenAny` on channel readers |

### Swift Influences

| Swift concept | E# adaptation |
|---|---|
| `enum` with associated values | `choice` with payloads |
| Protocol-oriented programming | `protocol` with implicit satisfaction |
| Value type emphasis | `data` as default |
| `async let` (structured concurrency) | `async let` (planned) — concurrent bindings that must complete before scope exits |

---

## Syntax Reference

### Module Declaration

Every E# file begins with a module declaration.

```
module MyModule
```

Emits as `public static partial class MyModule`. All functions in the file become static methods on this class (unless promoted to instance methods on a data type). The `partial` modifier allows multiple files to contribute to the same module.

**Multiple files, same module:**
```
// file1.es
module Core

func helperA() -> int { return 1 }
```
```
// file2.es
module Core

func helperB() -> int { return 2 }
```
Both emit into `public static partial class Core`. No conflict.

**Multiple files, different modules:**
```
// types.es
module Types

data User { name: string, age: int }
```
```
// ops.es
module Ops

func greet(user: User) -> string { return "hello {user.name}" }
```
Cross-file type visibility: `User` defined in `types.es` is visible in `ops.es` because the binder registers all types before binding any file.

### Imports

```
import "System.Net.Http"
import "MyProject.Core"
```

Emits as `using` directives. Import external .NET assemblies/namespaces. Imports appear after the module declaration and before any type or function definitions.

**What imports enable:**
- Referencing types from external assemblies (`HttpClient`, `Guid`, `List<T>`)
- Calling static methods on external types
- Calling instance methods on external type values

**What imports don't do:**
- They don't create a dependency. The consuming project's `.csproj` must reference the assembly. The E# `import` just adds a `using` directive.

### Data Types

```
data Point {
    x: int
    y: int
}
```

Emits as `public partial struct Point` with public fields. Value type, stack-allocated by default, sequential layout, no object header, no GC tracking (unless boxed or containing reference type fields).

**Size:** A `data` with N value-type fields is exactly the sum of field sizes (plus alignment padding). No overhead. `data Point { x: int, y: int }` is 8 bytes.

**Generic data:**
```
data Pair<A, B> {
    first: A
    second: B
}
```

At the CLR level, generic data types are open generic value types. The JIT generates specialized native code for each value-type instantiation (`Pair<int, int>` gets different machine code than `Pair<float, bool>`).

**With derive directives:**
```
derive equality
derive debug
data Packet {
    header: uint
    length: int
}
```

`derive equality` generates:
- `Equals(object)` comparing all fields
- `GetHashCode()` combining all field hash codes
- `operator ==` and `operator !=`

`derive debug` generates:
- `ToString()` returning `TypeName { field1: value1, field2: value2 }`

These are compile-time code generation, not runtime reflection. The compiler emits the methods directly. No `System.Reflection` involved.

**Object creation:**
```
let p = Point { x: 10, y: 20 }
let pair = Pair<int, string> { first: 42, second: "hello" }
```

Object creation syntax requires an uppercase-starting identifier followed by `{` to avoid ambiguity with block statements. Fields are assigned by name.

**IL representation:**
```il
.class public sequential ansi sealed beforefieldinit Esharp.Generated.Point
    extends [System.Runtime]System.ValueType
{
    .field public int32 x
    .field public int32 y
}
```

### Choice Types (Tagged Unions)

Choice types are E#'s sum types — a value that is exactly one of several variants, each optionally carrying a payload.

```
choice AuthError {
    invalidCredentials
    accountLocked(untilUtc: DateTimeOffset)
    rateLimited(retryAfterMs: int)
}
```

Emits as:
- `public enum AuthErrorTag { invalidCredentials, accountLocked, rateLimited }` — the tag
- `public partial struct AuthError` with:
  - `Tag` field (the enum)
  - Payload fields (named `_caseName_payloadName`, e.g., `_accountLocked_untilUtc`)
  - Static factory methods: `AuthError.invalidCredentials()`, `AuthError.accountLocked(value)`, etc.

**Generic choice:**
```
choice Option<T> {
    some(value: T)
    none
}

choice Result<T, E> {
    ok(value: T)
    error(err: E)
}
```

**Factory method usage:**
```
let opt = Option<int>.some(42)
let none = Option<int>.none()
let result = Result<User, AuthError>.ok(user)
```

**Dot-case shorthand** (when the type is known from context):
```
func findUser(id: Guid) -> Option<User> {
    let user = db.find(id)
    if user == nil { return .none }
    return .some(user)
}
```

The `.caseName` syntax resolves against the function's return type. No need to spell out `Option<User>.some(...)`.

**Memory layout:** The struct holds the tag (4 bytes for the enum) plus fields for the largest payload. All variants occupy the same space. `AuthError` is: 4 (tag) + 8 (DateTimeOffset, the largest payload) = 12 bytes. No heap allocation per variant, no class hierarchy, no vtable.

**Match on choice types:**
```
match result {
    .ok(user) { processUser(user) }
    .error(err) {
        match err {
            .invalidCredentials { log("bad credentials") }
            .accountLocked(until) { log("locked until {until}") }
            .rateLimited(ms) { log("retry in {ms}ms") }
        }
    }
}
```

**IL dispatch:** The IL compiler emits the `switch` opcode — a jump table indexed by the tag enum's integer value. O(1) dispatch regardless of variant count. C# pattern matching on class hierarchies uses cascading `isinst` type checks — O(n).

**Current limitation:** Each case supports at most ONE payload field. Multi-payload cases like `popout(containerId: uint, slotIndex: int)` are planned but not implemented. Workaround: wrap in a `data` type:
```
data PopoutArgs { containerId: uint, slotIndex: int }

choice Action {
    popout(args: PopoutArgs)
}
```

### Enum

```
enum Direction {
    north
    south
    east
    west
}
```

Emits as a C# `enum`. Use for closed sets of named constants without payloads. If you need payloads, use `choice` instead.

### Protocol (Interface)

```
protocol Drawable {
    func draw(x: int, y: int)
    func bounds() -> Rect
}
```

Emits as `public interface Drawable` with the corresponding method signatures.

**Key design choice:** No `I` prefix. `protocol WindowOps` emits as `interface WindowOps`, not `IWindowOps`. This is a Go-ism — interfaces are named for what they describe, not prefixed for what they are.

**Implicit satisfaction:** If a `data` type has instance methods (via promotion) that match all protocol method signatures, the compiler automatically adds the interface to the struct's `implements` list. No explicit declaration needed:

```
protocol Describable {
    func describe() -> string
}

data Client {
    name: string
}

// This function becomes an instance method on Client via promotion.
// Client now has describe() -> string, which matches Describable.
// The compiler makes Client implement Describable automatically.
func describe(client: Client) -> string {
    return "client: {client.name}"
}
```

**C# interop with protocols:** C# classes can implement E# protocols directly, since protocols emit as standard interfaces:
```csharp
class MyDrawer : Drawable {
    public void draw(int x, int y) { /* ... */ }
    public Rect bounds() { /* ... */ }
}
```

**Protocol method signatures:** Protocol methods use the same syntax as top-level functions but without bodies. Return type is optional (void if omitted). Parameters use `name: Type` syntax.

### Functions

Functions are the primary unit of behavior in E#.

```
func add(a: int, b: int) -> int {
    return a + b
}
```

Emits as `public static int add(int a, int b)` on the module class.

**No return type (void):**
```
func log(msg: string) {
    Console.WriteLine(msg)
}
```

**Generic functions:**
```
func identity<T>(value: T) -> T {
    return value
}

func swap<A, B>(pair: Pair<A, B>) -> Pair<B, A> {
    return Pair<B, A> { first: pair.second, second: pair.first }
}
```

Type parameters appear after the function name in `<angle brackets>`. The CLR's reified generics mean each instantiation is a real type — `identity<int>` and `identity<string>` are distinct at runtime.

**By-ref parameters:**
```
func increment(counter: *int) {
    counter += 1
}

func swap(a: *int, b: *int) {
    let temp = a
    a = b
    b = temp
}
```

`*T` in parameter position emits as `ref T`. At the call site, `*` is required to make the ref passing explicit:
```
var count = 0
increment(*count)    // count is now 1
```

This is E#'s Go influence — the call site shows you that `count` might be mutated. In C#, `ref` at the call site serves the same purpose but is less visually distinct.

**IL detail:** `*T` parameters become managed pointers (`&T` in IL). The CLR tracks these through the GC — if the referenced memory moves during collection, the pointer is updated. They can't dangle, can't be stored in fields (except `ref struct`), and can't escape the stack frame. Full safety guaranteed by the runtime.

### Instance Method Promotion

A function whose first parameter is a `data` type automatically becomes an instance method on that type. This is E#'s version of Go's receiver syntax.

```
data Client {
    name: string
    age: int
    state: ConnectionState
}

func describe(client: Client) -> string {
    return "client {client.name} is {client.age}"
}

func isConnected(client: Client) -> bool {
    return client.state == ConnectionState.connected()
}
```

**What the compiler does:**
1. The binder sees `describe(client: Client)` — first param type matches `data Client`
2. `describe` is marked as an instance method on `Client`
3. The C# emitter places it on `public partial struct Client` with the first parameter removed
4. All references to `client` in the body are rewritten to `this`
5. String interpolation `{client.name}` is rewritten to `{this.name}`
6. Sibling instance method calls `isConnected(client)` are rewritten to `isConnected()` (self-param stripped)

**Emitted C#:**
```csharp
public partial struct Client
{
    public string name;
    public int age;
    public ConnectionState state;

    public string describe()
    {
        return $"client {this.name} is {this.age}";
    }

    public bool isConnected()
    {
        return this.state == ConnectionState.connected();
    }
}
```

**C# usage:** `myClient.describe()` — the promoted method is a real instance method.

**Multi-file promotion:** If `Client` is defined in `types.es` and `describe` is defined in `ops.es`, the method still gets promoted. It appears on a `partial struct Client` in the `.g.cs` file generated from `ops.es`.

**Rules:**
- Only `data` types trigger promotion, not `choice`, `enum`, or primitives
- The first parameter name becomes `this` in the body
- String interpolation inside instance methods rewrites the self-param name
- Sibling instance method calls strip the self-param from arguments
- Functions with a data-type first param that are NOT meant to be promoted need a workaround (rename the type, use a wrapper) — this is a known design tension

### Function Literals

```
let double = func(x: int) -> int { return x * 2 }

app.MapGet("/users", func(ctx: HttpContext) {
    respond(listUsers(ctx), ctx)
})
```

Emits as a C# lambda expression. Function literals are Go-style (not arrow syntax). They support parameters and return types.

**Current limitation:** Function literals don't capture variables from the enclosing scope. They are pure — all data must be passed as parameters. Capture support is planned.

### Function Pointers

```
let ptr = &factorial
```

`&funcName` takes the address of a function. The result is a typed function pointer — zero allocation, zero GC tracking.

**IL compiler path:** Emits `ldftn` — loads a raw managed function pointer as a native integer. Calling through it uses `calli` with the known signature. The E# compiler enforces type safety at compile time (the parameter/return types are known from the function declaration), so no `unsafe` annotation is needed from the user. The IL is technically unverifiable (`calli` can't be statically verified by the CLR's verifier), but modern .NET runs in full trust and doesn't enforce verification.

**Transpiler path:** Emits `delegate*<ParamTypes, ReturnType>` in C# 9+ `unsafe` context. This requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the consuming project. The `unsafe` requirement is a C# limitation — E# guarantees type correctness, but C#'s type system can't express that guarantee.

**Design rationale:** Every `Func<T>` delegate in C# allocates a heap object. Function pointers are a native integer. For dispatch tables, event handlers, and callback arrays, the difference is zero allocation vs. N allocations. E# makes the zero-allocation path the natural one.

**Future: dispatch tables:**
```
data Handler {
    name: string
    execute: &func(Request) -> Response
}

// Array of handlers — contiguous in memory, zero allocation per entry
let handlers = [
    Handler { name: "auth", execute: &handleAuth },
    Handler { name: "data", execute: &handleData },
]
```

### Dot-Case Shorthand

```
return .invalidCredentials
return .some(42)
```

When the expected type is known (typically from the function's return type), `.caseName` resolves to the full factory call. This avoids repeating the type name:

```
// Instead of:
return AuthError.invalidCredentials()

// Write:
return .invalidCredentials
```

The binder resolves `.caseName` by looking up the return type of the enclosing function, finding the matching `choice` type, and emitting the factory call.

---

## Statements

### Variable Declaration

```
let x = 42                       // immutable, type inferred
var total = 0                    // mutable, type inferred
let name: string = getUserName() // immutable, explicit type
var buffer: List<byte> = List<byte>()  // mutable, explicit type
```

`let` bindings cannot be reassigned. `var` bindings can be reassigned. Both emit as C# local variables — the immutability of `let` is enforced by the E# compiler, not the CLR (the CLR doesn't distinguish mutable/immutable locals).

### Assignment

```
total = total + 1
client.state = ConnectionState.connected()
buffer[i] = newValue
```

Full lvalue support: locals, parameters, member access chains, and index expressions.

### Compound Assignment

```
total += i
count -= 1
scale *= 2.0
ratio /= denominator
```

Emits as the corresponding C# compound assignment. Supported operators: `+=`, `-=`, `*=`, `/=`.

### If / Else

```
if x < 0 {
    return 0 - x
}

if condition {
    doA()
} else {
    doB()
}

if a > b {
    return a
} else if b > c {
    return b
} else {
    return c
}
```

No parentheses around the condition (Go-style). Braces are mandatory.

### While

```
while i <= n {
    total += i
    i += 1
}

while true {
    let event = readEvent()
    if event == nil { return }
    process(event)
}
```

### For..In

```
for item in collection {
    process(item)
}

for event in ch {
    handle(event)
}

for i in 0..n {
    buffer[i] = 0
}
```

**Transpiler:** Emits as C# `foreach`.

**IL compiler:** Emits the full `GetEnumerator`/`MoveNext`/`Current` pattern with `try/finally` for disposal:
```il
callvirt GetEnumerator()
stloc enumerator
.try {
    br.s COND
  LOOP:
    ldloc enumerator
    callvirt get_Current()
    stloc item
    // body
  COND:
    ldloc enumerator
    callvirt MoveNext()
    brtrue.s LOOP
    leave.s END
}
finally {
    ldloc enumerator
    callvirt Dispose()
    endfinally
}
END:
```

### Match

Match dispatches over choice type variants. Two forms:

**Without annotation** (when the subject is a param/local of known choice type):
```
match action {
    .closeWindow { ops.close(source) }
    .spawn(wt) { ops.spawnWindow(wt) }
    .failed(reason) { log(reason) }
    default { }
}
```

**With type annotation** (when the type can't be inferred, e.g., member access chain):
```
match (client.state: ConnectionState) {
    .connected { handle(client) }
    .disconnected { reconnect(client) }
    default { }
}
```

**Arm syntax:** `.caseName { body }` or `.caseName(binding) { body }`. The binding name is a new local variable that holds the payload value for that variant.

**`default { }` is the catch-all arm.** Equivalent to `_` in other languages.

**Match in return position:**
```
func describe(state: ConnectionState) -> string {
    match state {
        .connected { return "online" }
        .connecting { return "pending" }
        .disconnected { return "offline" }
        .failed(reason) { return "error: {reason}" }
    }
}
```

Arms that `return` do not emit a trailing `break` (the compiler detects always-terminating arms to avoid unreachable code warnings).

**IL compiler:** Uses the `switch` opcode — a jump table indexed by the tag enum. O(1) dispatch for all variants, versus cascading type checks for C# class hierarchies.

### Defer

```
let handle = openFile(path)
defer { handle.Close() }

// ... use handle ...
// handle.Close() runs when the scope exits, even on error
```

Emits as `try/finally`. Multiple defers execute in LIFO order (last declared, first executed):

```
defer { log("third") }
defer { log("second") }
defer { log("first") }
// Prints: first, second, third
```

**Interaction with tail calls:** The IL compiler suppresses `tail.` emission inside defer blocks because the CLR requires the stack frame to be preserved for `finally` handlers.

### Return

```
return value
return ok(session)
return error(AuthError.invalidCredentials())
return .none    // dot-case shorthand
```

### Let Guard (Early Return on Nil)

```
let user = db.find(id) else {
    return error(AppError.notFound())
}
// user is guaranteed non-nil here
```

If the expression evaluates to nil/null, execute the else block (which must return, break, or otherwise exit the scope). Inspired by Swift's `guard let`.

---

## Expressions

### Literals

| Type | Examples |
|---|---|
| Integer | `42`, `0`, `-1`, `1_000_000` |
| Float | `3.14`, `0.5`, `1.0e10` |
| String | `"hello"`, `"hello {name}"` (interpolated) |
| Boolean | `true`, `false` |
| Null | `nil` |

### String Interpolation

```
"user {name} has {count} items"
"processing {current}/{total} ({percent}%)"
```

No `$` prefix needed. If a string contains `{expr}`, it's automatically interpolated. Emits as `$"user {name} has {count} items"` in C#.

Inside instance methods, the self-parameter name is rewritten. If the promoted function's first param was `client`, then `{client.name}` becomes `{this.name}` in the emitted code.

### Operators

| Category | Operators | Notes |
|---|---|---|
| Arithmetic | `+`, `-`, `*`, `/`, `%` | Standard precedence |
| Comparison | `==`, `!=`, `<`, `>`, `<=`, `>=` | Structural equality for data types with `derive equality` |
| Logical | `&&`, `\|\|`, `!` | Short-circuit evaluation |
| Keyword aliases | `and`, `or`, `not` | Same as `&&`, `\|\|`, `!` |
| Compound assignment | `+=`, `-=`, `*=`, `/=` | |
| Error propagation | `?` | Unwraps Result, early-returns on error |
| Range | `..` | Creates a range (e.g., `0..n`) |
| Index from end | `^` | Index from end (e.g., `^1` = last element) |
| Address-of | `&` | Function pointer (e.g., `&myFunc`) |
| Ref passing | `*` at call site | Pass by reference (e.g., `increment(*count)`) |

### Error Propagation (`?`)

```
func getSession(req: LoginRequest) -> Result<Session, AuthError> {
    let user = db.findByEmail(req.email)?
    let valid = hasher.verify(req.password, user.hash)?
    let session = tokens.issue(user.id)?
    return ok(session)
}
```

The `?` operator unwraps a `Result<T, E>`. If the result is `ok`, the value is extracted. If the result is `error`, the error is immediately returned from the enclosing function. Like Rust's `?`.

**Implementation:** The binder generates a `BoundTryUnwrapExpression` with a temporary variable. The emitter expands to:
```csharp
var __tmp = db.findByEmail(req.email);
if (__tmp.IsError) return Result<Session, AuthError>.error(__tmp.Error);
var user = __tmp.Value;
```

### Object Creation

```
let session = Session {
    token: tokens.issue(user.id)
    userId: user.id
}
```

Creates a value of a `data` type. Only recognized for uppercase-starting identifiers (avoids ambiguity with block statements). Fields are assigned by name using `fieldName: value` syntax.

**Generic object creation:**
```
let pair = Pair<int, string> { first: 42, second: "hello" }
```

### Spawn

```
let job = spawn {
    for event in ch {
        process(event)
    }
}
```

Creates an asynchronous job. Returns a `Job` (wrapping `Task`). Backed by `Task.Run` which queues work on the CLR thread pool.

**Job operations:**
```
job.Cancel()    // request cancellation
job.Wait()      // block until complete
```

### Channel Creation

```
let ch = chan<Event>(256)        // bounded channel, capacity 256
let unbounded = chan<string>()   // unbounded channel
```

Creates a `Chan<T>`. Backed by `System.Threading.Channels.Channel<T>`.

**Channel operations:**
```
ch.Send(event)    // send a value
ch.Close()        // close the channel

for event in ch {  // receive all values until closed
    handle(event)
}
```

### Member Access

```
user.name
client.state
response.headers.contentType
```

Chains of member access emit as-is. For `data` types, this is field access. For external types, Roslyn resolves the member.

### Method Calls

```
// Module function call
let result = validateUser(request)

// Instance method call (on promoted method)
let desc = client.describe()

// External method call
let upper = name.ToUpper()
list.Add(item)

// Static method call
let id = Guid.NewGuid()
```

### Index and Slice

```
let first = items[0]
let last = items[^1]           // index from end
let slice = data[2..5]         // range slice
let head = data[..n]           // from start to n
let tail = data[start..]       // from start to end
```

Maps to C# index and range syntax (`System.Index`, `System.Range`).

---

## Type System

### Primitive Types

| E# type | CLR type | Size |
|---|---|---|
| `int` | `System.Int32` | 4 bytes |
| `uint` | `System.UInt32` | 4 bytes |
| `long` | `System.Int64` | 8 bytes |
| `ulong` | `System.UInt64` | 8 bytes |
| `short` | `System.Int16` | 2 bytes |
| `ushort` | `System.UInt16` | 2 bytes |
| `byte` | `System.Byte` | 1 byte |
| `sbyte` | `System.SByte` | 1 byte |
| `float` | `System.Single` | 4 bytes |
| `double` | `System.Double` | 8 bytes |
| `bool` | `System.Boolean` | 1 byte |
| `string` | `System.String` | Reference type (heap) |
| `char` | `System.Char` | 2 bytes |
| `void` | `System.Void` | 0 bytes |

### Built-in Generic Types

**`Result<T, E>`** — Error-as-value type. The primary error handling mechanism.
- `.Value` — the success value (only valid when `.IsOk`)
- `.Error` — the error value (only valid when `.IsError`)
- `.IsOk` / `.IsError` — check which variant
- Factory: `Result<T, E>.ok(value)`, `Result<T, E>.error(err)`
- Propagation: `?` operator unwraps or early-returns

**`Chan<T>`** — Typed channel for concurrent communication.
- `.Send(value)` — send a value into the channel
- `.Close()` — close the channel (signals no more values)
- Iterable with `for..in` — receives values until closed
- Backed by `System.Threading.Channels.Channel<T>`

**`Job`** — Cancellable task wrapper.
- `.Cancel()` — request cancellation via `CancellationTokenSource`
- `.Wait()` — block until the task completes
- Returned by `spawn { }` expressions

### External Types

Any .NET type can be referenced by name: `Guid`, `DateTime`, `List<string>`, `HttpClient`, etc. The compiler treats unrecognized type names as external types and emits them as-is. Resolution is handled by Roslyn (transpiler path) or by assembly reference (IL compiler path).

```
func createId() -> Guid {
    return Guid.NewGuid()
}

func buildList() -> List<int> {
    let list = List<int>()
    list.Add(1)
    list.Add(2)
    return list
}
```

### Type Resolution Order

1. **Primitive types** — `int`, `string`, `bool`, etc.
2. **Built-in generic types** — `Result<T, E>`, `Chan<T>` (detected by name prefix)
3. **Types defined in the current compilation unit** — `data`, `choice`, `enum`
4. **Types from other files in a multi-file project** — cross-file type visibility via shared binder
5. **External .NET types** — passed through as-is, resolved by Roslyn or assembly reference

---

## Concurrency Model

E# uses Go-style concurrency primitives backed by CLR infrastructure. No colored functions — `spawn`/`chan`/`for..in` handle concurrency without infecting function signatures.

### Current: spawn + chan

```
let ch = chan<string>(100)

let producer = spawn {
    for i in 0..10 {
        ch.Send("event {i}")
    }
    ch.Close()
}

let consumer = spawn {
    for event in ch {
        log(event)
    }
}

consumer.Wait()
```

**How it maps to the CLR:**
- `spawn { }` → `Task.Run(() => { ... })` — queued on the thread pool
- `chan<T>(N)` → `Channel.CreateBounded<T>(N)` — bounded channel with backpressure
- `ch.Send(v)` → `channel.Writer.TryWrite(v)` — enqueue value
- `ch.Close()` → `channel.Writer.Complete()` — signal no more values
- `for v in ch` → `foreach (var v in channel.Reader.ReadAllAsync().ToBlockingEnumerable())` — blocking receive

### Planned: await (uncolored async)

```
func fetchUser(id: Guid) -> User {
    let response = await http.GetAsync("/users/{id}")
    let body = await response.Content.ReadAsStringAsync()
    return parseUser(body)
}
```

- No `async` keyword on the function. Return type says `User`, not `Task<User>`.
- `await` marks suspension points explicitly (like `*` marks ref-passing).
- The compiler detects `await` and generates an async state machine in IL.
- Always a struct state machine (not a class), always `ValueTask<T>` return.
- E# callers see `User`. C# callers see `ValueTask<User>`.

### Planned: async let (structured concurrency)

```
func fetchDashboard(userId: Guid) -> Dashboard {
    async let user = fetchUser(userId)
    async let posts = fetchPosts(userId)
    async let stats = fetchStats(userId)

    // all three run concurrently, all complete before this line
    return Dashboard { user: user, posts: posts, stats: stats }
}
```

Swift-inspired concurrent bindings. All `async let` bindings must complete before the scope exits. If any errors, the others are cancelled.

### Planned: select (channel multiplexing)

```
func eventLoop(requests: chan<Request>, events: chan<Event>, quit: chan<bool>) {
    while true {
        select {
            .recv(req, requests) { processRequest(req) }
            .recv(evt, events) { logEvent(evt) }
            .recv(_, quit) { return }
        }
    }
}
```

Go's `select` adapted for CLR channels. Multiplexes over multiple channel receive operations. Backed by `Task.WhenAny` on channel reader tasks, or a custom multiplexer in the IL compiler.

---

## Error Handling

### Result-Based (Primary)

E# uses `Result<T, E>` as the primary error handling mechanism. Errors are values, not control flow.

```
choice DbError {
    notFound
    connectionFailed(message: string)
    timeout
}

func findUser(id: Guid, db: DbContext) -> Result<User, DbError> {
    let user = db.Users.Find(id)
    if user == nil { return error(.notFound) }
    return ok(user)
}

func getProfile(id: Guid, db: DbContext) -> Result<Profile, DbError> {
    let user = findUser(id, db)?    // propagate error if findUser fails
    let prefs = findPreferences(user.id, db)?
    return ok(Profile { user: user, prefs: prefs })
}
```

**Why not exceptions:**
- Exceptions are invisible in function signatures — you can't tell what a function might throw
- Exceptions unwind the stack — expensive, unpredictable control flow
- `Result<T, E>` makes the error type explicit and forces handling
- The `?` operator makes propagation concise without hiding it

### Exception Interop (Planned)

The .NET ecosystem uses exceptions extensively. E# needs to interop:

```
try {
    let data = File.ReadAllText(path)
    return ok(data)
} catch (e: IOException) {
    return error(AppError.io(e.Message))
}
```

Bridge between E#'s result model and .NET's exception model. Catch at the boundary, convert to `Result`, and use results internally.

---

## Compiler Architecture

### Pipeline

```
Source (.es)
    → Lexer (character-by-character scanner)
    → Tokens (SyntaxToken stream)
    → Parser (recursive descent + precedence climbing)
    → AST (SyntaxTree — untyped)
    → Binder (multi-pass type resolution)
    → BoundTree (typed IR — every expression carries a BoundType)
    → Emitter (C# text or IL bytecode)
```

The binder is the key transformation. It takes untyped syntax nodes and produces typed bound nodes. Every `BoundExpression` carries a `BoundType` that the emitter uses for code generation.

### Two Emitter Backends

**C# Emitter** (`CSharpEmitter.cs`): BoundTree → `.g.cs` text → Roslyn → IL.
- The shipping path. Human-readable output.
- All language features supported.
- Output can be inspected, debugged, and modified.
- Depends on Roslyn for final compilation to IL.

**IL Emitter** (`ILEmitter.cs` + `ILMethodEmitter.cs`): BoundTree → Mono.Cecil → `.dll` directly.
- The research path. Produces leaner IL than the C# → Roslyn path.
- Supports features C# cannot express: `tail.call`, IL `switch` jump tables, `ldftn`/`calli`, short-form opcodes.
- Fewer locals, fewer instructions, no Roslyn ceremony (single-return-point patterns, condition temp variables, null-check callvirt).
- Does not yet support all language features (no external method resolution, limited string handling).

**Both backends consume the same BoundTree.** The front-end (lexer → parser → binder) is shared. Adding a new feature means adding one AST node, one bound node, and emission in both backends.

### Binder Passes

The binder runs four passes:

1. **Register types** — Scan all `data`, `choice`, `enum`, `protocol` declarations across all files. Populate `_typeRegistry`. Record function return types in `_functionReturnTypes`. This pass makes cross-file type references work.

2. **Bind functions** — For each function: create a scope, resolve parameter types, bind the body (every expression gets a `BoundType`), produce `BoundFunctionDeclaration`.

3. **Determine instance methods** — For each `data` type, find functions whose first parameter type matches. These become instance methods on the data type.

4. **Bind data with instance methods + protocol satisfaction** — Attach promoted methods to `BoundDataDeclaration`. For each protocol, check if the data type's instance methods match all protocol method signatures. If so, add the protocol to the data type's implements list.

### Type Hierarchy (BoundType)

```
BoundType
├── PrimitiveType(Name)       // int, string, bool, etc.
├── DataType(Name)            // user-defined data types
├── ChoiceType(Name)          // user-defined choice types
├── EnumType(Name)            // user-defined enums
├── ResultType(ValueType, ErrorType)  // Result<T, E>
├── ChanType(ElementType)     // Chan<T>
├── ExternalType(Name)        // .NET types (passed through)
├── VoidType                  // void
└── NullType                  // nil
```

Every bound expression carries one of these. The emitters use the type information for code generation decisions (e.g., whether to emit `call` vs `callvirt`, how to emit object creation, whether to promote to instance method).

### Key Source Files

**Compiler (`src/Esharp.Compiler/`):**
| File | Purpose |
|---|---|
| `Syntax/SyntaxTokenKind.cs` | All token types (keywords, operators, literals) |
| `Syntax/SyntaxNodes.cs` | AST node definitions (sealed records) |
| `Lexing/Lexer.cs` | Hand-written character-by-character scanner |
| `Parsing/Parser.cs` | Recursive descent parser with precedence climbing |
| `Binding/BoundType.cs` | Type hierarchy for the typed IR |
| `Binding/BoundNodes.cs` | Typed IR nodes (every expression has a BoundType) |
| `Binding/Binder.cs` | Multi-pass binder (register types → bind functions → instance methods → protocol satisfaction) |
| `Binding/BinderScope.cs` | Lexical scope chain with parent lookup |
| `Emit/CSharpEmitter.cs` | BoundTree → C# text |

**IL Compiler (`src/Esharp.ILEmit/`):**
| File | Purpose |
|---|---|
| `ILEmitter.cs` | Assembly skeleton, data structs, choice types, enum types |
| `ILMethodEmitter.cs` | Function bodies → IL instructions (stack-based) |
| `ILTypeResolver.cs` | BoundType → Cecil TypeReference mapping |
| `ILOptimizer.cs` | Post-pass: short-form opcodes, branch shortening |

**Runtime (`src/Esharp.Runtime/`):**
| File | Purpose |
|---|---|
| `Result.cs` | `Result<TValue, TError>`, `Job`, `Chan<T>` |

### Adding a New Language Feature

1. **Token** — add to `SyntaxTokenKind.cs`, add keyword/operator mapping in `Lexer.cs`
2. **AST node** — add sealed record to `SyntaxNodes.cs`
3. **Parser** — add parsing method in `Parser.cs`, wire into the appropriate context
4. **Bound node** — add to `BoundNodes.cs` with type information
5. **Binder** — add binding logic in `Binder.cs`, handle type resolution
6. **C# Emitter** — add emission in `CSharpEmitter.cs`
7. **IL Emitter** — add emission in `ILMethodEmitter.cs` / `ILEmitter.cs`
8. **Tests** — add to `TranspilerTests.cs` and `ILEmitterTests.cs`

### CLI Commands

```bash
esharpc compile <input.es> <output.g.cs>           # Single file → C#
esharpc compile-project <dir> [--out <outdir>]      # Multi-file → C#
esharpc compile-il <input.es> <output.dll>          # Single file → IL assembly
```

---

## Interop Model

### E# → C# (E# types consumed from C#)

E# code compiles to standard .NET assemblies. C# references the assembly and calls methods directly. No wrappers, no binding generators, no special runtime.

```csharp
// C# calling E# functions
var session = Auth.login(request, userStore, hasher, tokens);
if (session.IsOk) Console.WriteLine(session.Value.token);

// C# calling E# instance methods
var client = new Client { name = "Alice", age = 30 };
Console.WriteLine(client.describe());

// C# using E# choice types
var error = AuthError.invalidCredentials();
var locked = AuthError.accountLocked(DateTimeOffset.UtcNow.AddHours(1));
```

**The stealth advantage:** A C# project referencing an E# assembly doesn't know or care that it was written in E#. The types are standard .NET structs, enums, and interfaces. NuGet packages written in E# would be indistinguishable from C# packages.

### C# → E# (E# consuming C# types)

E# references external .NET types via `import`. External types appear as `ExternalType` in the binder and are emitted as-is:

```
import "System.Net.Http"

func fetch(url: string) -> string {
    let client = HttpClient()
    let response = client.GetStringAsync(url).Result
    return response
}
```

Method calls on external types pass through to Roslyn for resolution (transpiler) or require assembly references (IL compiler).

### Protocol Satisfaction (Cross-Language)

E# `protocol` emits a C# `interface`. C# classes implement it directly:

```csharp
// C# implementing an E# protocol
class WindowOpsImpl : WindowOps   // WindowOps is an E# protocol
{
    public void closeWindow(uint source) => _wm.Close(source);
    public void spawnWindow(string type) => _wm.Spawn(type);
    // ...
}
```

E# code accepts the C# implementation through the interface:
```
func execute(action: Action, source: uint, ops: WindowOps) {
    match action {
        .closeWindow { ops.closeWindow(source) }
        .spawn(wt) { ops.spawnWindow(wt) }
    }
}
```

### Bidirectional Architecture

E# is designed to sit in the middle of a C# → E# → C# dependency chain:

```
C# Assembly A          E# Assembly          C# Assembly B
(core types)    →    (business logic)    →    (framework/UI)
                     imports A types          references E# assembly
                     defines protocols        implements protocols
                     pure functions           calls E# functions
```

E# can:
- Import types from C# assembly A
- Define types and functions that C# assembly B consumes
- Define protocols that C# classes satisfy
- Satisfy interfaces from A so B can use E# types polymorphically

---

## Comparison With Other Languages

### vs C#

| | C# | E# |
|---|---|---|
| Default allocation | Heap (class) | Stack (data → struct) |
| Method dispatch | Virtual (callvirt) | Direct (call) |
| Error handling | Exceptions | Result<T, E> + ? |
| Mutation | Implicit (mutable by default) | Explicit (let/var, *T) |
| Composition | Inheritance + interfaces | Protocols + instance method promotion |
| Async | Colored functions (async/await) | Uncolored (spawn/chan/await) |
| Metaprogramming | Runtime reflection | Compile-time derive |
| Function references | Func<> delegate (heap allocation) | &func pointer (zero allocation) |
| IL coverage | ~60% of instruction set | Targets full instruction set |

### vs Go

| | Go | E# |
|---|---|---|
| Runtime | Custom (goroutine scheduler, GC) | CLR (thread pool, generational GC, JIT) |
| Generics | Type parameters (Go 1.18+) | Reified generics (CLR, full type info at runtime) |
| Error handling | `error` interface, `if err != nil` | `Result<T, E>` with `?` propagation |
| Sum types | None (use interfaces) | `choice` (tagged unions, zero allocation) |
| Ecosystem | Go standard library | Entire .NET ecosystem |
| AOT | Default (static binary) | NativeAOT (opt-in, growing support) |
| Interop | CGo (FFI, overhead) | Seamless with C#, F#, VB.NET |

### vs Rust

| | Rust | E# |
|---|---|---|
| Memory model | Ownership + borrowing | GC + value types (CLR managed) |
| Runtime | Minimal (no GC) | Full (CLR: GC, JIT, thread pool) |
| Error handling | `Result<T, E>` + `?` | Same pattern, same syntax |
| Sum types | `enum` with payloads | `choice` with payloads |
| Traits | Explicit `impl Trait for Type` | Implicit protocol satisfaction |
| Target | Native code | CLR bytecode (JIT or AOT) |
| Learning curve | Steep (ownership, lifetimes) | Moderate (no ownership model) |
| Ecosystem | Cargo + crates.io | NuGet + .NET ecosystem |

### vs F#

| | F# | E# |
|---|---|---|
| Paradigm | Functional-first | Procedural-first |
| Types | Discriminated unions, records | `choice`, `data` (similar, different feel) |
| Mutation | Discouraged (immutable by default) | Explicit (`let` vs `var`, `*T`) |
| Interop with C# | Awkward (FSharpFunc, FSharpOption) | Seamless (same types: struct, enum, interface) |
| Composition | Function composition, pipelines | Module functions, instance method promotion |
| Audience | Data science, DSLs, correctness-critical | Systems code, state machines, dispatch, performance-critical |

---

## Planned Features (Not Implemented)

### Multi-Payload Choice Cases

```
choice Action {
    popout(containerId: uint, slotIndex: int)
    layoutSave(windowId: uint, name: string)
}
```

Currently each case supports at most one payload field. Extending to N fields requires changes to parser, `BoundChoiceCase`, binder, and both emitters. Workaround: wrap multi-field payloads in a `data` type.

### Exhaustive Match Checking

The compiler should verify that match statements cover all cases of a choice type, warning on missing arms. Currently, missing arms silently fall through.

### Try/Catch Interop

```
try {
    riskyCall()
} catch (e: IOException) {
    return error(AppError.io(e.Message))
}
```

Bridge between E#'s result-based model and .NET's exception-based ecosystem. Catch at the boundary, convert to Result.

### Closures / Variable Capture

Function literals currently don't capture variables from the enclosing scope. Planned: support for captures, emitting as C# lambdas with captures or as IL anonymous methods.

### Attribute Passthrough

```
@Serializable
@JsonProperty("user_name")
data User {
    name: string
}
```

Pass .NET attributes through to the emitted types for serialization, validation, and framework integration.

### MSBuild Task Integration

Proper `Esharp.Build` MSBuild task instead of `<Exec>` in build targets. Enables IDE integration, incremental compilation, and proper dependency tracking.

### Static Dispatch Helpers

Compiler-generated function-to-delegate conversion and dispatch table construction. Driven by the function pointer feature.

### Inline Runtime Types

`Result<T,E>`, `Job`, `Chan<T>` emitted into the output assembly when used, eliminating the `Esharp.Runtime` dependency. The compiler becomes self-contained — one `.es` file produces one assembly with no external dependencies beyond the .NET runtime.

### Explicit Struct Layout

```
data NetworkHeader {
    @offset(0) raw: uint
    @offset(0) version: byte
    @offset(1) headerLen: byte
    @offset(2) totalLen: ushort
}
```

C-style unions via CLR `StructLayout.Explicit` + `FieldOffset`. Different views of the same memory. The CLR supports this natively.

### Stack-Allocated Dynamic Arrays

```
let buffer = stack[1024]    // 1KB on the stack, no heap allocation
```

Maps to IL `localloc`. Currently C# only exposes this through `stackalloc` in limited contexts.

### Self-Hosting

The E# compiler written in E#. The ultimate validation that the language is expressive enough for real systems programming. Requires: generic types (done), multi-file (done), error handling interop, the runtime library in E#, and sufficient string/collection handling.

---

## Known Limitations

**By design (not bugs):**
- No inheritance — use `protocol` + `data` composition
- No LINQ syntax — use functions over collections
- No `async` keyword on functions — async is inferred from `await`
- No `class` keyword — use `data` (struct) for all user types
- No constructor overloading — use factory functions
- No operator overloading (yet) — may be added for numeric types
- No property syntax — data fields are public fields

**Not yet implemented:**
- Single payload per choice case (multi-payload planned)
- No exhaustive match checking
- No try/catch interop
- No closures/captures in function literals
- No attribute passthrough for .NET interop
- No standalone `.esproj` project type
- No IDE support / LSP / syntax highlighting
- IL compiler: no external method resolution (string methods, static calls on external types)
- `using Esharp.Runtime;` always emitted even when unused
- No `select` for channel multiplexing
- No `async let` for structured concurrency
- No explicit struct layout (`@offset`)

---

## Project Structure

```
esharp/
├── src/
│   ├── Esharp.Compiler/          # Lexer, Parser, Binder, C# Emitter
│   │   ├── Syntax/               # Token kinds, AST nodes
│   │   ├── Lexing/               # Hand-written lexer
│   │   ├── Parsing/              # Recursive descent parser
│   │   ├── Binding/              # Multi-pass binder, type system
│   │   ├── Diagnostics/          # Compiler diagnostics
│   │   └── Emit/                 # C# code emitter
│   ├── Esharp.ILEmit/            # IL compiler (Mono.Cecil backend)
│   ├── Esharp.Runtime/           # Result<T,E>, Job, Chan<T>
│   ├── Esharp.Build/             # MSBuild task (WIP)
│   └── Esharp.Cli/               # CLI entry point (esharpc)
├── tests/
│   └── Esharp.Tests/             # Unit tests for both backends
├── samples/
│   ├── AuthDemo/                 # Login flow with Result, choice, match
│   ├── StateMachine/             # State machine with instance methods
│   ├── ParserDemo/               # Simple expression parser
│   ├── MultiFile/                # Cross-file type visibility
│   ├── Spike/                    # IL compiler test fixture
│   └── WebApi/                   # ASP.NET minimal API with E# handlers
├── docs/
│   ├── research/                 # CLR architecture, analysis docs
│   │   ├── CLR_ARCHITECTURE.md
│   │   ├── CLR_FOR_ESHARP.md
│   │   └── WHAT_CSHARP_HIDES.md
│   └── design_and_discussion/    # Specs and design proposals
│       └── ASYNC_SPEC.md
├── Esharp.slnx                   # Solution file
├── LANGUAGE.md                   # Language overview
├── REFERENCE.md                  # This document
├── README.md                     # Project overview
└── LICENSE                       # MIT
```
