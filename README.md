# E#

E# is a procedural language for .NET. It compiles to normal CLR assemblies — the same runtime as C# and F#, full interop with both. But it thinks about the CLR differently.

E# inverts the defaults. Value types, direct dispatch, explicit mutation, result-based errors. The full CLR (heap, GC, reflection, LINQ) is reachable but not the path of least resistance. The generated code takes the CLR seriously as a machine, not as a class hierarchy.

## Two Compilation Paths

**Transpiler** (`.es` -> `.g.cs` -> Roslyn -> IL) — the shipping path. Produces human-readable C# that `dotnet build` compiles normally. 39 tests passing, multi-file compilation, generic types.

**IL Compiler** (`.es` -> Binder -> Mono.Cecil -> IL) — the research path. Emits CLR bytecode directly, bypassing C#. Produces leaner IL than Roslyn (fewer locals, no ceremony, direct branch instructions). Already supports features C# cannot express:

- **Tail calls** — `tail.call` prefix for guaranteed TCO. A recursive function that would stack overflow via the transpiler runs indefinitely via the IL compiler.
- **Short-form opcodes** — compact IL encoding matching Roslyn's output quality.
- **Choice types via IL `switch`** — jump table dispatch (O(1)), not cascading branches.

The transpiler validates the language design. The IL compiler validates the runtime model. Both consume the same typed IR from the binder.

## What It Looks Like

```
module Auth

data LoginRequest {
    email: string
    password: string
}

data Session {
    token: string
    userId: Guid
}

choice AuthError {
    invalidCredentials
    accountDisabled
}

func login(
    req: LoginRequest,
    users: AuthDemo.IUserStore,
    hasher: AuthDemo.IPasswordHasher,
    tokens: AuthDemo.ISessionTokens
) -> Result<Session, AuthError> {
    let user = users.findByEmail(req.email) else {
        return error(AuthError.invalidCredentials())
    }

    if not hasher.verify(req.password, user.passwordHash) {
        return error(AuthError.invalidCredentials())
    }

    return ok(Session {
        token: tokens.issue(user.id)
        userId: user.id
    })
}
```

This compiles to a .NET struct and a static method. C# calls it directly. No wrapper, no ceremony.

## Language Features

### Data and Types
- **`data`** — value types (structs). `data Point { x: int, y: int }` -> `partial struct`.
- **`data<T>`** — generic data types. `data Pair<A, B> { first: A, second: B }`.
- **`choice`** — tagged unions. `choice Option { some(value: int), none }` -> tag enum + struct with factory methods.
- **`choice<T>`** — generic choice types. `choice Option<T> { some(value: T), none }`.
- **`enum`** — simple closed sets.
- **`protocol`** — interfaces. Types satisfy protocols implicitly when their instance methods match.
- **`derive equality`** / **`derive debug`** — compile-time code generation, no runtime reflection.

### Functions
- **`func`** — top-level module functions. No classes needed.
- **Instance method promotion** — a function whose first parameter matches a `data` type automatically becomes an instance method on that type.
- **Generic functions** — `func identity<T>(value: T) -> T`.
- **Function literals** — `func(x: int) -> int { return x * 2 }`.

### Control Flow
- **`if`**, **`while`**, **`for...in`**, **`return`** — direct procedural control flow.
- **`match`** — switch over choice types with payload destructuring.
- **`defer { ... }`** — scoped cleanup, emitted as `try/finally`.

### Error Handling
- **`Result<T, E>`** — errors are values, not exceptions.
- **`ok(value)`** / **`error(value)`** — typed Result constructors.
- **`let x = expr else { ... }`** — guard pattern.
- **`expr?`** — error propagation. Unwrap or early-return.

### By-Ref and Stack-Friendly Code
- **`*T` parameters** — by-ref. `func move(p: *Point, dx: int)` -> `ref Point p`.
- **`*expr` at call sites** — marks the ref pass. Mutation is explicit and visible.
- **`data` is `struct`** — value types by default.

### Concurrency
- **`spawn { ... }`** — lightweight jobs backed by `Task.Run`.
- **`chan<T>(capacity)`** — channels backed by `System.Threading.Channels`.
- **`for event in ch`** — range over a channel.

### Other
- String interpolation — `"hello {name}"`, no prefix needed.
- Slices and ranges — `data[..n]`, `items[^1]`.
- Compound assignment — `+=`, `-=`, `*=`, `/=`.
- `.caseName` shorthand — `.invalidCredentials` resolves against declared types.
- `import "AssemblyName"` — external assembly references.

## What It Compiles To

| E# | C# |
|---|---|
| `module Foo` | `public static partial class Foo` |
| `data Point { x: int, y: int }` | `public partial struct Point` |
| `choice Error { ... }` | tag enum + `partial struct` with factories |
| `func add(a: int, b: int) -> int` | `public static int add(int a, int b)` |
| `func describe(p: Point) -> string` | instance method on `Point` (promoted) |
| `func move(p: *Point, dx: int)` | `public static void move(ref Point p, int dx)` |
| `spawn { ... }` | `Job.Spawn(_ => { ... })` |
| `defer { cleanup() }` | `try { ... } finally { cleanup(); }` |
| `let x = f()?` | unwrap with early error return |

## Project Structure

```
src/Esharp.Compiler     Lexer, parser, binder, typed IR, C# emitter
src/Esharp.ILEmit       IL compiler (Mono.Cecil) — direct assembly emission
src/Esharp.Runtime      Result<T,E>, Job, Chan<T>
src/Esharp.Cli          CLI: compile, compile-project, compile-il
src/Esharp.Build        MSBuild integration (stub)
tests/Esharp.Tests      39 tests — transpiler + IL emitter
samples/AuthDemo        Auth module with Result types and choice matching
samples/StateMachine    State machine with by-ref, match, instance methods
samples/ParserDemo      Loops and spawn concurrency
samples/MultiFile       Cross-file type visibility and generic types
```

## Build and Run

```bash
# Build everything
dotnet build Esharp.slnx

# Run tests
dotnet test tests/Esharp.Tests

# Transpile to C#
dotnet run --project src/Esharp.Cli -- compile input.es output.g.cs

# Compile directly to IL
dotnet run --project src/Esharp.Cli -- compile-il input.es output.dll

# Multi-file project
dotnet run --project src/Esharp.Cli -- compile-project ./my-project/

# Run samples
dotnet run --project samples/AuthDemo
dotnet run --project samples/StateMachine
dotnet run --project samples/MultiFile
```

Requires .NET 10 SDK.

## Design Philosophy

E# is not "C# minus features." It is an inverted mental model for the CLR.

**From Go:** small language surface, procedural style, modules over objects, channels, explicit over clever.

**From the CLR:** runtime quality, JIT/AOT/GC, interop with the entire .NET ecosystem, spans and by-ref.

**From Swift:** clean declaration syntax, modern feel without excessive punctuation.

**The insight:** the CLR is a bigger machine than C# lets you use. E# aims to invert the defualts, and provide a new insight and model on how you can use the CLR. 

## Status

Early-stage. The language is working on the set of samples, the compiler emits intermediate language, both transpiler and compiler paths produce "correct" output. Not production-ready — missing exhaustive match checking, full language features, full type inference, IDE support, and many rough edges.

## License

MIT License. See [LICENSE](LICENSE).
