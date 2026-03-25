# E# Concurrency & Async Specification

Design spec for E#'s async story. Four tiers, each independently implementable, each building on CLR primitives. No colored functions.

## Design Principles

1. **No function coloring.** A function's signature says what it returns, not how it's implemented. `func fetchUser(id: Guid) -> User` returns `User` regardless of whether it awaits internally.
2. **Explicit suspension.** `await` marks where a function may yield, like `*` marks ref-passing. No invisible suspension points.
3. **Value-type state machines.** The IL compiler emits struct-based `IAsyncStateMachine` implementations. Stack-friendly, consistent with E#'s defaults.
4. **`ValueTask<T>` over `Task<T>`.** Avoids allocation on synchronously-completing paths. The default async return type at the CLR level.
5. **Structured concurrency.** `async let` bindings are scoped — child operations complete before their parent scope exits. No dangling tasks.
6. **Interop is a boundary, not a constraint.** E# callers see uncolored types. C# callers see the real CLR type (`ValueTask<T>`). Same IL, two views.

## Tier 1: `spawn` + `chan` (Implemented)

Explicit concurrency. Already working.

```
let ch = chan<Event>(256)
let job = spawn {
    for event in ch {
        process(event)
    }
}
ch.Send(event)
```

- `spawn { }` → `Task.Run` → `Job` wrapper
- `chan<T>(cap)` → `System.Threading.Channels.Channel<T>`
- `for x in ch` → `ReadAllAsync().ToBlockingEnumerable()`
- `Job` → cancel, wait

No changes needed. This tier handles fire-and-forget parallelism and message passing.

## Tier 2: `await` — Suspension Without Coloring

### Syntax

```
func fetchUser(id: Guid) -> User {
    let response = await http.GetAsync("/users/{id}")
    let body = await response.Content.ReadAsStringAsync()
    return parseUser(body)
}

// Caller — no async ceremony
let user = fetchUser(id)
```

### Rules

- `await` is an expression-level keyword. `await expr` where `expr` returns `Task<T>`, `ValueTask<T>`, or anything with the awaiter pattern (`GetAwaiter()`).
- The function is NOT marked `async`. The return type is `User`, not `Task<User>`.
- Any function containing `await` is internally an async state machine. The compiler infers this.
- Functions without `await` compile to normal synchronous methods. No overhead.

### CLR-Level Emit

A function with `await`:

```
func fetchUser(id: Guid) -> User {
    let response = await http.GetAsync(url)
    return parseUser(await response.Content.ReadAsStringAsync())
}
```

Emits as (conceptual IL):

```csharp
// Actual CLR signature — ValueTask<User> return
[AsyncStateMachine(typeof(fetchUser_StateMachine))]
public static ValueTask<User> fetchUser(Guid id)

// Struct state machine
private struct fetchUser_StateMachine : IAsyncStateMachine
{
    public int _state;
    public AsyncValueTaskMethodBuilder<User> _builder;
    public Guid id;
    // Captured locals that cross suspension points:
    public HttpResponseMessage response;

    public void MoveNext() { ... }
    public void SetStateMachine(IAsyncStateMachine machine) { ... }
}
```

### E# Calling E#

When E# calls a function that contains `await`, the compiler knows both sides. At the call site, it auto-awaits:

```
// E# source
let user = fetchUser(id)

// Emitted IL (conceptual)
// The compiler sees fetchUser returns ValueTask<User> at CLR level
// It emits an await on the ValueTask automatically
ValueTask<User> __vt = fetchUser(id);
User user = await __vt;  // state machine suspension point
```

This means the *caller* also becomes an async state machine if the callee has `await`. The async-ness propagates through the IL, but **not through the E# source**. The programmer never sees it.

If the caller is a non-async context (e.g., top-level entry point, spawn body), the compiler emits `.GetAwaiter().GetResult()` — a blocking wait. This is intentional: E# prefers explicit `spawn` for concurrency, and `await` for I/O suspension within an already-concurrent context.

### C# Calling E#

C# sees the real CLR signature:

```csharp
// C# sees:
public static ValueTask<User> fetchUser(Guid id)

// C# calls:
var user = await EsharpModule.fetchUser(id);
```

C#'s coloring is C#'s problem. E# emits a standard `ValueTask<T>`-returning method that C# can `await` normally.

### E# Calling C#

```
// C# library method:  Task<HttpResponseMessage> GetAsync(string url)

// E# usage:
let response = await http.GetAsync(url)
```

The `await` keyword triggers the awaiter pattern on the `Task<T>` return. The compiler emits `GetAwaiter()`/`IsCompleted`/`GetResult()`/`OnCompleted()` into the state machine's `MoveNext()`.

### Token & AST

- New keyword: `AwaitKeyword`
- New AST node: `AwaitExpressionSyntax(ExpressionSyntax Inner)`
- New bound node: `BoundAwaitExpression(BoundExpression Inner, BoundType ResultType)` where `ResultType` is `T` extracted from `Task<T>`/`ValueTask<T>`

### Binder Logic

When binding `await expr`:
1. Bind the inner expression
2. Check if the type is `Task<T>`, `ValueTask<T>`, or has `GetAwaiter()` pattern
3. Extract `T` as the result type
4. Mark the enclosing function as requiring async state machine transformation

### C# Emitter

```csharp
// Input: await http.GetAsync(url)
// Output: (await http.GetAsync(url))
```

The C# emitter adds `async` to the enclosing method signature and changes the return type to `ValueTask<T>`. The `await` keyword passes through.

### IL Emitter

This is the complex part. The IL emitter must:

1. Detect functions containing `BoundAwaitExpression`
2. Generate a struct implementing `IAsyncStateMachine`
3. Emit `MoveNext()` with state tracking and suspension/resumption
4. Use `AsyncValueTaskMethodBuilder<T>` for the builder pattern
5. Capture only variables that cross suspension points (liveness analysis)

This is a significant implementation effort. Implementation order: C# emitter first (let Roslyn generate the state machine), IL emitter later.

## Tier 3: `async let` — Structured Concurrent Bindings

### Syntax

```
func fetchDashboard(userId: Guid) -> Dashboard {
    async let user = fetchUser(userId)
    async let posts = fetchPosts(userId)
    async let stats = fetchStats(userId)

    // All three run concurrently.
    // All must complete before this scope exits.
    // First access to any binding awaits its completion.
    return Dashboard {
        user: user
        posts: posts
        stats: stats
    }
}
```

### Rules

- `async let name = expr` starts the expression immediately on the thread pool.
- The binding's value is available when first accessed — that access blocks (awaits) until the operation completes.
- All `async let` bindings in a scope must complete before the scope exits. If the scope exits early (return, error), pending operations are cancelled.
- If any `async let` operation fails, the others are cancelled and the error propagates.

### CLR-Level Emit

```csharp
// Start all three concurrently
Task<User> __user_task = Task.Run(() => fetchUser(userId));
Task<List<Post>> __posts_task = Task.Run(() => fetchPosts(userId));
Task<Stats> __stats_task = Task.Run(() => fetchStats(userId));

// Await all (structured — all must complete)
await Task.WhenAll(__user_task, __posts_task, __stats_task);

User user = __user_task.Result;
List<Post> posts = __posts_task.Result;
Stats stats = __stats_task.Result;
```

The IL compiler could optimize this: instead of `Task.WhenAll` (which allocates a `Task[]`), emit a custom struct-based multi-awaiter that polls each `ValueTask` without allocation.

### Token & AST

- `async` before `let` in statement position → `AsyncLetStatementSyntax(string Name, ExpressionSyntax Initializer)`
- No new keyword needed (`async` is contextual before `let`)
- The binder tracks all `async let` bindings in a scope for structured completion

### Cancellation

When an `async let` scope exits early:

```
func fetch(id: Guid) -> Result<Dashboard, AppError> {
    async let user = fetchUser(id)
    async let perms = fetchPermissions(id)

    if not perms.canView {
        // perms completed, user may still be running
        // compiler cancels the user task before returning
        return error(AppError.forbidden())
    }

    return ok(Dashboard { user: user, perms: perms })
}
```

The compiler emits `CancellationTokenSource` plumbing. Each `async let` receives a linked token. On early exit, the token is cancelled and pending tasks are awaited (to ensure cleanup).

## Tier 4: `select` — Channel Multiplexing

### Syntax

```
func eventLoop(requests: chan<Request>, events: chan<Event>, quit: chan<bool>) {
    while true {
        select {
            .recv(req, requests) { processRequest(req) }
            .recv(evt, events) { logEvent(evt) }
            .recv(_, quit) { return }
            .timeout(5000) { handleIdle() }
            default { yield() }
        }
    }
}
```

### Arms

- `.recv(binding, channel) { body }` — receive from channel. Blocks until one channel has data.
- `.send(value, channel) { body }` — send to channel. Blocks until one channel has capacity.
- `.timeout(ms) { body }` — fire after timeout if no other arm completes.
- `default { body }` — non-blocking. If no channel is ready, execute immediately.

### Rules

- `select` blocks until one arm is ready, then executes that arm's body.
- If multiple arms are ready, one is chosen (implementation-defined, fair if possible).
- If `default` is present, `select` is non-blocking.
- `.timeout` creates a `CancellationTokenSource` with the specified delay.

### CLR-Level Emit

Without `default` (blocking):
```csharp
// Create read tasks for each channel arm
ValueTask<Request> __t0 = requests.Reader.ReadAsync(ct);
ValueTask<Event> __t1 = events.Reader.ReadAsync(ct);
ValueTask<bool> __t2 = quit.Reader.ReadAsync(ct);

// Wait for first completion
Task completed = await Task.WhenAny(__t0.AsTask(), __t1.AsTask(), __t2.AsTask());

// Dispatch to matching arm
if (completed == __t0.AsTask()) { var req = __t0.Result; processRequest(req); }
else if (completed == __t1.AsTask()) { var evt = __t1.Result; logEvent(evt); }
else if (completed == __t2.AsTask()) { return; }
```

With `default` (non-blocking):
```csharp
if (requests.Reader.TryRead(out var req)) { processRequest(req); }
else if (events.Reader.TryRead(out var evt)) { logEvent(evt); }
else if (quit.Reader.TryRead(out var _)) { return; }
else { yield(); }
```

The IL compiler optimization: avoid `Task.WhenAny` allocation by using `ValueTask` polling with a custom multiplexer struct. This is where direct IL emission produces fundamentally different (better) code than the transpiler.

### Token & AST

- New keyword: `SelectKeyword`
- New AST nodes: `SelectStatementSyntax(IReadOnlyList<SelectArm> Arms)`
- `SelectArm` variants: `RecvArm(string Binding, ExpressionSyntax Channel, BlockStatementSyntax Body)`, `SendArm(ExpressionSyntax Value, ExpressionSyntax Channel, BlockStatementSyntax Body)`, `TimeoutArm(ExpressionSyntax Milliseconds, BlockStatementSyntax Body)`, `DefaultArm(BlockStatementSyntax Body)`

## Implementation Priority

### Phase 1: `await` (transpiler)
The biggest interop win. Let Roslyn generate the state machine.
1. Add `AwaitKeyword` token + lexer
2. Add `AwaitExpressionSyntax` AST node + parser
3. Add `BoundAwaitExpression` + binder (extract T from Task<T>/ValueTask<T>)
4. C# emitter: pass through `await`, mark function `async`, change return type to `async ValueTask<T>`
5. Tests: call C# async APIs from E#, verify correct results

### Phase 2: `await` (IL compiler)
The hard part. Struct-based async state machine emission via Cecil.
1. Detect functions with await → flag for async transformation
2. Liveness analysis: which locals cross suspension points
3. Emit state machine struct: state field, builder, captured locals
4. Emit `MoveNext()` with state switch + awaiter pattern
5. Compare IL output with Roslyn's for correctness

### Phase 3: `async let`
Structured concurrency. Can be transpiler-first.
1. Parse `async let` as contextual keyword combo
2. Binder: track async let bindings per scope
3. C# emitter: emit `Task.Run` + `Task.WhenAll` + `.Result` pattern
4. Cancellation: emit `CancellationTokenSource` for early exit
5. Tests: verify concurrent execution, verify cancellation on early return

### Phase 4: `select`
Channel multiplexing. Requires Tier 2 for `WhenAny`-based blocking.
1. Add `select` keyword + parser
2. Binder: resolve channel types, extract element types
3. C# emitter: blocking → `Task.WhenAny`, non-blocking → `TryRead`
4. Tests: multi-channel dispatch, timeout, default arm

### Phase 5: IL compiler optimizations
Where the compiler path proves its value.
1. Custom multi-awaiter struct for `async let` (no Task[] allocation)
2. Custom channel multiplexer for `select` (no WhenAny allocation)
3. Minimal state machine captures (liveness-based)
4. `calli`-based continuations (function pointer callbacks instead of delegates)

## Interop Summary

| Caller | Callee has `await` | What happens |
|---|---|---|
| E# → E# | Yes | Compiler auto-awaits. Caller becomes async state machine too (transparent). |
| E# → E# | No | Normal synchronous call. |
| E# → C# async | N/A | `await` on the Task/ValueTask. E# function becomes async state machine. |
| C# → E# with await | N/A | C# sees `ValueTask<T>` return. Standard C# `await` works. |
| E# top-level / spawn | Calls func with await | Blocking `.GetAwaiter().GetResult()` at the boundary. |

## Open Questions

1. **Should E# allow explicit `Task<T>` return types?** For cases where the user wants to expose a proper async API to C# without the `ValueTask` default. Maybe: `func fetch(id: Guid) -> async User` to opt into `ValueTask<User>` CLR return, vs `func fetch(id: Guid) -> Task<User>` for explicit `Task` return.

2. **Cancellation threading.** Should `await` automatically pass a `CancellationToken` from the enclosing `Job`? If `spawn` returns a `Job` with a `CancellationTokenSource`, and the spawned body contains `await`, should the token flow through automatically?

3. **Error handling in `async let`.** If one binding errors, what happens to the others? Cancel and propagate first error? Aggregate errors? The spec says cancel, but `Result<T,E>` vs exceptions makes this nuanced.

4. **`select` fairness.** When multiple channels are ready, Go uses pseudo-random selection. `Task.WhenAny` returns the first completed, which may bias toward earlier tasks. Does E# guarantee fairness?

5. **Nested `async let`.** Can you nest `async let` scopes? If so, the inner scope's tasks must complete before the outer scope's access. This composes naturally but the cancellation plumbing gets complex.

6. **`await` in `defer`.** Should `await` be allowed in `defer` blocks? C# doesn't allow `await` in `finally`. The CLR doesn't support it either. E# should probably forbid this and make the error clear.

## What This Unlocks for Dogfooding

The action router dogfood project doesn't need async. But the next dogfood targets do:

- **Data fetch pipeline** — E# module that fetches market data over HTTP, parses it, pushes to channels. Exercises `await` + `chan` composition.
- **WebSocket handler** — Long-lived connection with `select` over message channel + heartbeat timeout.
- **Parallel indicator calculation** — `async let` for concurrent indicator computation across multiple timeframes.

Each dogfood project exercises a different tier and generates real compiler feedback.
