# Esharp.Lowering

Lowering is the stage between the binder and CodeGen. It takes a fully-bound `BoundProgram` that
still carries high-level **FEATURE** constructs (`match`, `?`, `??`, `with`, `defer`, `for`,
lambdas, `async`, …) and rewrites it into a tree of only **CORE** nodes — the small set CodeGen
emits mechanically. The contract is one invariant:

> **No FEATURE node reaches CodeGen.** Lowering produces a complete, correct CORE bound tree;
> CodeGen is a mechanical CIL walk over it.

When a construct needs a primitive the IR lacks, the primitive is added properly (schema node +
generated rewriter + CodeGen case) — never shimmed around. Each pass ports the *behavior* ESC
produces (the exact IL a construct must yield), never ESC's emit-time *structure*.

---

## The CORE / FEATURE tier split

Every bound node is tagged `CORE` or `FEATURE` in
[`../BoundTree/bound-nodes.schema.json`](../BoundTree/bound-nodes.schema.json).

- **CORE** — survives lowering; CodeGen emits it directly. Blocks, `if`/`while`, assignments,
  calls, member/index access, conversions (`BoundConversion`), object creation, the ternary
  (`BoundConditionalExpression`), `goto`/label, `try`, **and interpolated strings**
  (`BoundInterpolatedStringExpression` is a CORE leaf — CodeGen emits `string.Concat(object[])`,
  boxing value-type holes, exactly as ESC does).
- **FEATURE** — must be eliminated by a pass before CodeGen. `match`, `?`/`ok`/`error`, `??`/`?.`,
  `with`, `defer`, `raise`, `for`, compound assignment, `let … else`, lambdas, `chan`/`spawn`/
  `select`, `await`, `async let`, `if`/`match` in expression position.

Union layout is **not** a FEATURE concern handled here: CodeGen (`EmitChoice`) emits the tag-enum +
struct and computes the case tags itself.

---

## The rewriter hierarchy

All traversal rests on one generated, totally-recursive identity rewriter and two hand-written
bases over it:

```
BoundTreeRewriter          (generated from the schema — descends into EVERY child position:
  │                         lambda bodies, ternary branches, ctor args, tuple/index/list elements,
  │                         every call arg. Regenerate via bound-nodes-gen.py; never hand-edit.)
  │
  └── LoweringRewriter      (hand-written; adds the synthetic-temp allocator: FreshTemp(role))
        │
        ├── «Base A»        a non-hoisting pass derives here directly
        │
        └── SpillingBoundTreeRewriter   («Base B» — adds the statement-hoist buffer +
                                          conditional-hoist machinery)
```

A pass overrides **only** the FEATURE nodes it transforms and calls `base.*` for descent, so it
inherits total coverage and can never leak a FEATURE node in a position it forgot to walk. (The
historical bug class — hand-rolled partial walks that missed lambda bodies / ternary branches /
ctor args — is structurally impossible once a pass derives from these bases.)

### Base A vs Base B

| | Base A — `LoweringRewriter` | Base B — `SpillingBoundTreeRewriter` |
|---|---|---|
| For | passes that replace a node with another node | passes that must **hoist statements** before a value (a temp, a nil-test branch, an early return) |
| Examples | Match, Event, Defer, ForEach, AsyncForeach, Closure | Result, NullFlow, With, LetGuard, Assignment, ExpressionForm, Concurrency |
| Extra API | `FreshTemp` | `FreshTemp`, `Hoist(stmt)`, `RewriteCapturing(expr)` |

#### The conditional-hoist problem (why Base B exists)

A hoist that originates inside a **conditionally-evaluated** sub-expression — the right operand of
`&&`/`||`, or a ternary branch — must **not** splice before the enclosing statement; that would run
its side effects unconditionally and reorder them (valid IL, **wrong semantics** — ILVerify won't
catch it). `SpillingBoundTreeRewriter` handles this: `RewriteBlockStatement` opens a per-statement
hoist buffer; `RewriteConditionalExpression` and the short-circuit `RewriteBinaryExpression`
**materialize** the whole conditional into a temp + guarded `if` when a branch produces hoists, so
the hoisted setup runs only on the path that produced it. A branch with no hoists takes the cheap
structural path. `RewriteCapturing(expr)` is the primitive a derived pass uses to capture a
conditionally-evaluated operand's hoists into the right branch (e.g. the right side of `??`).

---

## Shared substrate

| File | Role |
|---|---|
| [`Synth.cs`](Synth.cs) | The one home for the CORE node shapes every pass builds: `Default(T)` → `BoundDefaultExpression` (correct for ref / value / type-parameter — never an empty object-creation), the **nullable vocabulary** (`IsNil`/`IsPresent`/`Unwrap`/`WrapInto`, value-vs-reference correct — never a reference compare against a value-type `Nullable<T>`), literals, temps, members, calls, blocks. A fix to a shared shape lands once. |
| [`LoweringDriver.cs`](LoweringDriver.cs) | `MapBodies(program, rewriter)` drives a rewriter over **every** executable position — function bodies, instance-method bodies, **and `init` constructor bodies + their `base(…)`/`this(…)` args** (the positions the old hand-rolled walks skipped). A second overload, `MapBodies(program, Func<BoundType?, BoundTreeRewriter>)`, builds a fresh rewriter per body with that body's **return type** in hand — used by `ResultLowering`, whose `?` targets the enclosing function's `Result` type. |
| [`IBoundTreePass.cs`](IBoundTreePass.cs) | `BoundProgram Lower(BoundProgram, SynthesizedSymbolSink)` — the contract for one ordered pass. |
| [`SynthesizedSymbolSink.cs`](SynthesizedSymbolSink.cs) | Interns the types/methods passes mint — closure **display classes**, async **state-machine structs**, **iterator structs** — into the symbol table by reference identity, and holds synthesized method bodies. Names use unspellable `<>`-bracketed forms so they cannot collide with source names. |

A typical pass is a thin shell:

```csharp
public sealed class WithLowering : IBoundTreePass
{
    public static readonly WithLowering Instance = new();
    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new WithRewriter());
}

sealed class WithRewriter : SpillingBoundTreeRewriter
{
    protected override BoundExpression RewriteWithExpression(BoundWithExpression node) { … }
}
```

---

## The pipeline

[`LoweringPipeline.cs`](LoweringPipeline.cs) runs the passes in this order; **order is
load-bearing** (a pass consumes the CORE shapes earlier passes leave, and emits FEATURE shapes
later passes consume).

| # | Pass | Base | Lowers |
|---|---|---|---|
| 1 | `AsyncStreamLowering` | — | a residual `IAsyncEnumerable` yield-function → producer + wrapper (backstop; the real split is the parse-time desugar) |
| 2 | `AsyncForeachLowering` | A | `await for v in src` (a `BoundForEachStatement` with `IsAwait`) → async-enumerator drain |
| 3 | `ExpressionFormLowering` | B | `if`/`match` in value position → temp filled by the statement form |
| 4 | `MatchLowering` | A | `match` → tag/`isinst`/literal test chains |
| 5 | `ResultLowering` | B | `ok`/`error`/`?` → struct-init / nil-guarded early return |
| 6 | `NullFlowLowering` | B | `??` / `?.` → null-test + branch (value- and reference-nullable correct) |
| 7 | `WithLowering` | B | `with { f: v }` → copy + field stores |
| 8 | `LetGuardLowering` | B | `let x = e else { }` → nil-guard + bind |
| 9 | `ForEachLowering` | A | sync `for` → GetEnumerator/MoveNext loop, **loop-scoped** dispose |
| 10 | `AssignmentLowering` | B | compound assignment → binary + assign (index spilled once) |
| 11 | `DeferLowering` | A | `defer` → try/finally (also finishes ForEach's loop-scoped dispose) |
| 12 | `EventLowering` | A | `raise` → null-safe capture-then-invoke |
| 13 | `ConcurrencyLowering` | B | `chan` / `spawn` / `select` → stdlib construction/calls |
| 14 | `ClosureConversion` | A* | lambda → display class + delegate |
| 15 | `IteratorLowering` | — | `IAsyncEnumerable` wrapper → iterator struct |
| 16 | `AsyncLowering` | — | async function → state-machine struct + sync stub |

After pass 16 the pipeline asserts no FEATURE node survives (see *Assertions*).

\* ClosureConversion is per-function (it mints one display class per function), so it drives its own
member walk rather than `MapBodies`, but its body/closure rewrites derive from `LoweringRewriter`.

### Runs at bind time, not in the pipeline

Two rewrites run inside `DeclarationBinder.BindFunction`, before either backend sees the tree:

- [`AsyncLetLowering.cs`](AsyncLetLowering.cs) — `async let` → eager `Task.Run`/awaitable start at
  the declaration + an implicit `await` at the binding's **first use**. The join is injected before
  the first statement whose *directly-evaluated* expressions reference the name (a condition, an
  initializer, a captured closure), never a statement that only reaches it through a nested
  control-flow body — so an `async let` over a `Result` joined with `?` short-circuits only on the
  path that uses it. Once joined on a path the name leaves the pending set (no double-await); a
  closure body is its own async scope.
- [`AsyncSpill/`](AsyncSpill/) — `AsyncSpillLowering` + `AwaitFacts` + `SpillExpressionRewriter`:
  decides which locals are live across an await and must spill into state-machine fields (NEW spills
  only live-across-await locals, where ESC hoists every local — the headline "spill-only-live" win).

---

## The async state machine (`AsyncLowering` + region staging)

NEW builds the state machine **in the bound tree**, not at emit time: `AsyncLowering` replaces an
async function with (1) a synthesized state-machine **struct** (`_state`, `_builder`, parameter
fields, spill fields, one awaiter field per await site) and (2) a synchronous **stub** that creates
the struct and calls `builder.Start`. `MoveNextRewriter` synthesizes `MoveNext` as a single linear
statement stream of CORE `goto`/label/`if`/`try`:

- a top **state dispatch** routes `_state` to the matching resume point;
- each await expands inline to: `awaiter = X.GetAwaiter(); if IsCompleted goto __completed_N;
  _state = N; spill; AwaitUnsafeOnCompleted; return; __resume_N: restore; __completed_N: GetResult`;
- every `return e` becomes `__result = e; goto __async_tryend`, converging on the single
  `SetResult` emitted *after* the try (a throwing `SetResult` must not be caught by the machine's
  own catch);
- the whole body sits in one `try { … } catch (ex) { SetException(ex) }`.

### Await inside a protected region

An await inside a user `try`/`defer` (both lower to `BoundTryStatement` before this pass) cannot
resume by branching straight into a `.try`. `AwaitPointAnalyzer` records each await's **enclosing-try
chain** (outermost-first); `MoveNextRewriter` then stages each region:

- a **pre-entry label** before the region's `.try` — the outer dispatch routes a region-await to the
  outermost region's pre-entry (falling into the `.try` is legal);
- a **body-entry dispatcher** at the top of the try body forwards a staged state inward (to the next
  region's pre-entry, or the await's `__resume_N` when innermost — a `br` *within* the `.try`);
- a **finally guard** `if (_state >= 0) goto __finallyend` at the top of a region's finally, so a
  suspend's `leave` (which the CLR runs through intervening finallys) skips cleanup while suspended;
  on real exit `_state` is `-1`/`-2` and cleanup runs exactly once.

This is ESC's `ILAsyncEmitter` region algorithm ported into the bound tree. (The corresponding
emit-time hooks in CodeGen — `EmitRegionPreEntry`/`EmitTryBodyEntry`/`EmitFinallyGuard`/… — are inert
residue: the staging now lives here.)

---

## Assertions

Two checkers currently enforce the invariant and are kept in lock-step:

- `LoweringPipeline.AssertCoreOnly` — runs after the last pass.
- `../CodeGen/FeatureNodeAssertion.cs` — runs at CodeGen entry.

The end state collapses these into **one generated `BoundFeatureAssert`** emitted from the same
schema as the rewriter, so adding a node updates the rewriter and the assert together and they
cannot drift.

---
