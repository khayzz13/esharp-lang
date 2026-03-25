# What C# Hides From You

A guide for C# developers who want to understand what's actually happening underneath — and why a language like E# exists.

## Your Objects Are Bigger Than You Think

Every C# `class` instance carries a 16-byte header on 64-bit systems: 8 bytes for the method table pointer (vtable) and 8 bytes for the sync block / hash code. A `class Point { int X; int Y; }` is 24 bytes — 8 bytes of your data and 16 bytes of runtime bookkeeping you never asked for. An array of 10,000 points is 240KB, and 160KB of that is headers.

A `struct Point { int X; int Y; }` is 8 bytes. An array of 10,000 is 80KB. Same data, one-third the memory, and the GC doesn't track individual elements — just the single array object.

C# teaches you to reach for `class` first. The CLR was designed for both equally, and for data without identity, `struct` is what the runtime actually wants.

## Every Virtual Call Has a Cost You Don't See

When you write `myObj.DoSomething()`, C# almost always emits `callvirt` — even when the method isn't virtual. This is because `callvirt` includes an implicit null check that `call` doesn't. The tradeoff: every method call goes through virtual dispatch machinery (vtable lookup) even when there's only one possible target.

For class methods, the JIT can often devirtualize — prove there's only one implementation and replace the `callvirt` with a direct call. But it has to do that analysis work for every call site. For `sealed` classes or struct methods, the analysis is trivial. For deep class hierarchies, it's expensive and sometimes fails.

Struct instance methods use `call` (direct branch to known address). No vtable, no null check needed (structs can't be null), no devirtualization needed. One instruction, one branch.

When you have a function that takes data and returns data — no polymorphism needed — the virtual dispatch machinery is pure overhead. That's most functions in most codebases.

## Your async Methods Are State Machines

Every `async Task<T>` method gets transformed by the compiler into a state machine. What looks like:

```csharp
async Task<int> GetValue(string url)
{
    var response = await http.GetAsync(url);
    var body = await response.Content.ReadAsStringAsync();
    return int.Parse(body);
}
```

Becomes a struct (in Release) or class (in Debug) with:
- A `state` field (which `await` you're at)
- A `builder` field (manages the Task lifecycle)
- Captured locals: `url`, `response`, `body` — even if some aren't needed after an await
- An awaiter field per `await` expression
- A `MoveNext()` method with a switch statement over the state

In Debug builds, this is a *class* — heap-allocated on every call. In Release, it's a struct that starts on the stack but gets boxed (moved to heap) if the first await doesn't complete synchronously.

The issue isn't that state machines exist — they're necessary for suspending and resuming. The issue is that C# captures more than it needs to. Roslyn doesn't do liveness analysis, so a variable declared before an `await` gets captured even if it's never used after. And you can't control any of this — there's no way to see or influence what the state machine looks like.

## The GC Is Doing More Work Than Necessary

C#'s default allocation path is the heap. `new MyClass()` allocates on the heap. String concatenation allocates on the heap. LINQ chains allocate iterator objects on the heap. Every `Func<>` delegate allocates on the heap.

The GC handles this with a generational collector:
- **Gen 0**: short-lived objects, collected frequently (~every few milliseconds under pressure)
- **Gen 1**: survived one collection, buffer zone
- **Gen 2**: long-lived, collected rarely but expensively (can pause all threads)

A typical C# application creates thousands of small Gen 0 objects per second — LINQ iterators, string intermediates, delegate captures, async state machines. Most die immediately. The GC collects them efficiently. But "efficiently" still means: scan the stack for roots, walk the object graph, compact memory, update references. For each collection cycle.

A value-type-first approach makes most of these invisible to the GC. A struct on the stack isn't tracked. A struct in an array is one object, not thousands. A function pointer is an integer, not a delegate object. The GC isn't slower — it just has less to do.

## Pattern Matching Uses Type Checks, Not Jump Tables

When you write:

```csharp
return state switch
{
    Disconnected => "off",
    Connecting => "pending",
    Connected => "on",
    Failed f => $"error: {f.Reason}",
    _ => "unknown"
};
```

This compiles to cascading type checks — `isinst` instructions that test the runtime type of each object. It's the class hierarchy equivalent of `if (x is A) ... else if (x is B) ...`.

The IL `switch` instruction is a jump table — an array of branch targets indexed by an integer. O(1) dispatch regardless of the number of cases. The CLR supports it natively. But to use it, your discriminant needs to be an integer (like an enum tag), not a type hierarchy.

Tagged unions (a struct with a tag enum + payload) use the `switch` instruction naturally. Class hierarchies can't — they use cascading `isinst`. For a discriminated union with 30 cases, the difference is 30 type checks versus one indexed jump.

## You Can't Request Tail Calls

The CLR has a `tail.` IL prefix that guarantees tail call optimization — the current stack frame is reused for the callee. This means recursive functions can run with O(1) stack space.

C# has no way to request it. There's no `[TailCall]` attribute, no keyword, no pragma. Roslyn's position is that the JIT should decide. But the JIT's heuristics are conservative — it won't tail-call if there are locals to clean up, if the caller and callee have different signatures, or if it's not sure the optimization is safe.

The result: if you write a recursive descent parser or a state machine that uses recursion, it will stack overflow at around 10,000-50,000 frames. The fix exists in the instruction set. You just can't reach it from C#.

F# does emit `tail.` — it's one of the few .NET languages that does. Any language targeting the CLR can use it. C# chose not to.

## Function References Allocate

```csharp
Func<int, int> doubler = x => x * 2;
```

This allocates a delegate object on the heap. The delegate contains: a method pointer, a target reference (null for static methods), and object header overhead. If you store it in a list, pass it around, or use it in a hot loop, the GC tracks it.

The CLR's `ldftn` instruction loads a raw function pointer — a native integer. No allocation, no GC tracking. You can store it, pass it, and call through it with `calli`. C# 9 added `delegate*` for this, but requires `unsafe` context because C# can't prove the pointer type matches the call signature.

A language that knows the type of every function at compile time can make function pointers safe without `unsafe` — the compiler guarantees the signature match. Zero allocation, zero GC, full type safety.

## Roslyn Adds Ceremony to Your IL

Decompile any C# method and you'll see patterns you didn't write:

**Single return point:** Roslyn often stores the return value in a local and branches to a single `ret` at the bottom. This is for debugger experience (one breakpoint for "function returns"), but it adds unnecessary locals and branches.

**Null check via callvirt:** Even `sealed class` methods get `callvirt` instead of `call` — just for the null check side effect. The JIT usually optimizes this away, but the IL is bigger.

**Condition locals:** An `if (x > 0)` may store the comparison result to a `bool` local, then load the local and branch — instead of branching directly on the comparison. This is Roslyn's IR lowering, and the JIT cleans it up, but it inflates the method body.

**Async padding:** State machine `MoveNext()` methods contain try/catch for the entire body, sequence point `nop` instructions for the debugger, and local slots for every awaiter even if they're not all alive simultaneously.

None of this is wrong. Roslyn is a production compiler that prioritizes debuggability, correctness, and compilation speed. But it means the IL the JIT optimizes is not a minimal representation of what you wrote. There's a layer of compiler convenience between your intent and the machine.

## Interfaces Are Slower Than You'd Expect

C# developers use interfaces everywhere — dependency injection, abstraction boundaries, test mocking. At the IL level, interface dispatch uses a technique called **virtual stub dispatch**:

1. Each call site has a one-entry cache (the "stub")
2. First call: the runtime resolves the interface method on the concrete type and caches the result
3. Subsequent calls: if the concrete type matches, use the cached target (fast path)
4. If a different concrete type arrives (polymorphic call site): fall back to a hash table lookup

For monomorphic call sites (always the same type), this is fast after warmup. For polymorphic sites (multiple types flowing through — common with DI), it's a hash lookup on every call.

Direct `call` on a known type is a single branch instruction. No cache, no lookup, no warmup. For value types, the method address is known at JIT time. The call is as fast as calling a C function.

This doesn't mean interfaces are bad — they're essential for polymorphic boundaries. But using them everywhere (the DI pattern of interface-per-class) adds dispatch overhead on every method call in the chain.

## Your Strings Are Immutable But Your Allocations Aren't

`"Hello " + name + ", you have " + count + " items"` allocates 3 intermediate strings and creates 3 string objects that die immediately.

`$"Hello {name}, you have {count} items"` in modern C# (10+) uses `DefaultInterpolatedStringHandler` which stack-allocates a `Span<char>` buffer. One allocation for the final string. Much better.

But older code, libraries targeting older frameworks, and manual concatenation still allocate like the first pattern. The runtime has the efficient path — `Span<T>`, stack allocation, `DefaultInterpolatedStringHandler` — but C# only uses it for `$""` literals. Any other string building falls back to heap allocation.

## The Runtime Was Designed For Multiple Languages

The CLR — **Common** Language Runtime — was built to run C#, VB.NET, C++/CLI, F#, and languages that haven't been written yet. Its instruction set includes features for functional languages (`tail.`), systems languages (`calli`, `localloc`, explicit layout), and dynamic languages (`DynamicMethod`).

C# uses maybe 60% of the IL instruction set. The other 40% is there for languages with different models. Tail calls, fault handlers, function pointers, constrained generic dispatch, explicit struct layout, custom calling conventions — these aren't deprecated features. They're active, maintained, JIT-optimized instructions that no mainstream .NET language exposes.

The instruction set manual (ECMA-335) is 500+ pages. C#'s Roslyn compiler emits patterns from maybe 300 of those pages. The rest is waiting.

## What This Means

None of this makes C# a bad language. C# is an excellent general-purpose language that makes productive choices for the vast majority of code. The heap allocation, virtual dispatch, and GC are *correct* defaults for code that needs flexibility and fast iteration.

But there's a class of code — performance-critical paths, data-heavy systems, state machines, protocol handlers, parsers, dispatch tables — where C#'s defaults are exactly wrong. For this code, C# developers fight the language: using structs with careful discipline, avoiding LINQ in hot paths, writing unsafe blocks for function pointers, copying allocation-free patterns from blog posts and conference talks.

The CLR supports a different set of defaults — value types, direct dispatch, explicit control, zero-allocation function references. It's been supporting them since 2002. They're in the instruction set, tested, JIT-optimized, and stable.

The language just hasn't let you use them naturally. Until now.
