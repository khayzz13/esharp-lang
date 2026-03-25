# The .NET Common Language Runtime: Deep Technical Architecture

## 1. IL Instruction Set

### The Stack-Based Execution Model

CIL (Common Intermediate Language, formally ECMA-335 Partition III) is a stack-based bytecode. Every instruction consumes operands from and pushes results onto an evaluation stack. The evaluation stack is an abstraction; the JIT maps it to registers and native stack during compilation. A method body consists of a header (specifying max stack depth, locals signature token, and exception handler table), followed by an instruction stream.

The evaluation stack carries typed slots. At any given program point, the verifier requires that all control flow paths reaching that point produce an identical stack state (same depth, same types). This is the **stack merge** requirement and it is what makes IL verifiable.

### Opcode Categories

**Load/Store — Locals and Arguments:**
- `ldloc.0` through `ldloc.3`, `ldloc.s <index>`, `ldloc <index>` — push local variable value onto the stack
- `stloc.0` through `stloc.3`, `stloc.s`, `stloc` — pop stack into local
- `ldarg.0` through `ldarg.3`, `ldarg.s`, `ldarg` — push argument value
- `starg.s`, `starg` — pop stack into argument slot
- `ldloca.s`, `ldloca` — push *address* of local (managed pointer, `&`)
- `ldarga.s`, `ldarga` — push *address* of argument

Locals are declared in a method's `LocalsSignature` (metadata token in the method header). They are always zero-initialized unless the `localsinit` flag is cleared in the method header — a feature C# always sets but that you can suppress via `[SkipLocalsInit]` (which maps to clearing that flag in the IL header).

**Constant Loading:**
- `ldc.i4.m1` through `ldc.i4.8` — push small int constants
- `ldc.i4.s <int8>`, `ldc.i4 <int32>`, `ldc.i8 <int64>`
- `ldc.r4 <float32>`, `ldc.r8 <float64>`
- `ldnull` — push null reference
- `ldstr <token>` — push string literal (from the #US metadata heap)

**Arithmetic and Logic:**
- `add`, `sub`, `mul`, `div`, `rem` — binary operations
- `add.ovf`, `add.ovf.un`, `sub.ovf`, `sub.ovf.un`, `mul.ovf`, `mul.ovf.un` — overflow-checking variants (throw `OverflowException`)
- `and`, `or`, `xor`, `not`, `neg` — bitwise/unary
- `shl`, `shr`, `shr.un` — shifts
- `div.un`, `rem.un` — unsigned division/remainder

The type system at the IL level is simpler than C#. The stack only holds `int32`, `int64`, `native int`, `F` (native-size float, typically mapped to `float64`), `O` (object reference), and `&` (managed pointer). Smaller types (`int8`, `int16`, `bool`, `char`) are widened to `int32` on load. This is why `ldind.i1` sign-extends and `ldind.u1` zero-extends.

**Comparison and Branching:**
- `ceq`, `cgt`, `cgt.un`, `clt`, `clt.un` — push 1 or 0
- `beq`, `bne.un`, `bge`, `bge.un`, `bgt`, `bgt.un`, `ble`, `ble.un`, `blt`, `blt.un` — conditional branch
- `brfalse` (`brzero`), `brtrue` (`brinst`) — branch on zero/non-zero
- `br` — unconditional branch
- `switch <n> <targets>` — jump table

**Object Model:**
- `newobj <ctor-token>` — allocate and construct
- `ldfld`, `stfld` — instance field access
- `ldsfld`, `stsfld` — static field access
- `ldflda`, `ldsflda` — field address
- `castclass`, `isinst` — type casting
- `box`, `unbox`, `unbox.any` — value type boxing
- `ldobj`, `stobj`, `cpobj`, `initobj` — value type operations via pointers
- `sizeof <type-token>` — push size of value type
- `ldtoken` — push RuntimeTypeHandle/RuntimeMethodHandle/RuntimeFieldHandle
- `ldelem.*`, `stelem.*`, `ldelema` — array element access
- `newarr <type>` — create single-dimension zero-based array

**Method Calls (detailed in Section 4 below):**
- `call`, `callvirt`, `calli` — invocation
- `ldftn`, `ldvirtftn` — load function pointer
- `ret` — return from method
- `jmp` — tail-jump to method (replaces current frame, rare)

**Indirect Memory Access:**
- `ldind.i1`, `ldind.u1`, `ldind.i2`, `ldind.u2`, `ldind.i4`, `ldind.u4`, `ldind.i8`, `ldind.r4`, `ldind.r8`, `ldind.i`, `ldind.ref` — load via pointer
- `stind.i1`, `stind.i2`, `stind.i4`, `stind.i8`, `stind.r4`, `stind.r8`, `stind.i`, `stind.ref` — store via pointer

**Conversion:**
- `conv.i1`, `conv.i2`, `conv.i4`, `conv.i8`, `conv.r4`, `conv.r8`, `conv.u1`, `conv.u2`, `conv.u4`, `conv.u8`, `conv.i`, `conv.u`, `conv.r.un`
- Overflow-checking: `conv.ovf.i1`, `conv.ovf.i1.un`, etc.

**Stack Manipulation:**
- `dup` — duplicate top of stack
- `pop` — discard top of stack
- `nop` — no operation

**Miscellaneous:**
- `localloc` — allocate memory on the stack (returns `void*`; method cannot be inlined; requires localsinit flag behavior)
- `cpblk`, `initblk` — memory block operations
- `mkrefany`, `refanyval`, `refanytype` — typed reference operations (rarely used)
- `arglist` — vararg support
- `throw`, `rethrow` — exception control
- `endfinally` (`endfault`), `endfilter` — exception handler terminators
- `leave`, `leave.s` — exit protected region (like `br` but clears evaluation stack and runs finally handlers)

### Prefix Instructions

These modify the behavior of the immediately following instruction:

- **`tail.`** — The following `call`/`callvirt`/`calli` is a tail call. The callee reuses the caller's stack frame. The CLR is not required to honor this; it's a hint. RyuJIT will honor it when possible (same calling convention, no byref args pointing into current frame, etc.). C# emits `tail.` only for specific recursive patterns; F# uses it extensively.

- **`volatile.`** — The following load/store must not be reordered by the JIT or CPU cache with respect to other volatile operations. Maps to acquire/release semantics. C# emits this for `volatile` field access.

- **`constrained. <type>`** — Prefixes `callvirt`. Tells the runtime: "the `this` pointer is a managed pointer to `<type>`. If `<type>` is a value type that overrides the method, call directly without boxing. Otherwise, box and call." This is how generic code (`T.ToString()` where T is unconstrained) avoids unconditional boxing. Critical for performance — without it, every virtual call on a generic type parameter would box.

- **`readonly.`** — Prefixes `ldelema`. The resulting managed pointer won't be used to mutate the array element. Enables the JIT to skip type checking on `stelem` for covariant arrays. C# emits this when indexing into arrays in `in`/`readonly` contexts.

- **`unaligned. <alignment>`** — The following memory access may not be naturally aligned. Important for platforms with strict alignment requirements (ARM). Values: 1, 2, or 4.

- **`no. <flag>`** — Suppress specified runtime checks. Flags: `typecheck` (0x01), `rangecheck` (0x02), `nullcheck` (0x04). Rarely used; the JIT already eliminates redundant checks.

### Verifiable vs. Valid IL

**Valid IL** is any IL the runtime will execute without crashing. It may use unsafe operations like `calli` with arbitrary pointers, `localloc`, pointer arithmetic, etc.

**Verifiable IL** is a strict subset that a static verifier can prove type-safe. Verification ensures:
- No type confusion (every value used according to its declared type)
- No access to private members outside their scope
- All stack states merge consistently at join points
- No pointer arithmetic on managed pointers beyond what the type system allows
- No `localloc`, `calli`, or unmanaged pointer manipulation

C# in safe mode emits verifiable IL. C# `unsafe` blocks emit valid-but-unverifiable IL. For a compiler targeting the CLR directly, the distinction matters: if you want your code to run in partial-trust environments (historically) or pass verification for security analyzers, you must stay within the verifiable subset. In modern .NET (CoreCLR), verification is not enforced at runtime by default but is still relevant for NativeAOT trimming analysis and certain security contexts.

### What C# Emits vs. What's Actually Available

C# uses a relatively narrow slice of CIL. It never emits: `jmp`, `calli` (until function pointers in C# 9, and even then only for `unmanaged` calling conventions), fault handlers, `mkrefany`/`refanyval`/`refanytype`, custom calling conventions beyond the standard set, `tail.` (rarely, only since C# 8 with recursive patterns), the `no.` prefix, or `arglist` (C# doesn't support `__arglist` widely). Much of the IL surface is there for other languages: C++/CLI uses `calli` extensively, F# uses `tail.` as a core mechanism, and IronPython used dynamic dispatch patterns.

---

## 2. Type System at the CLR Level

### Metadata Representation

All types in a .NET assembly are described in metadata tables conforming to ECMA-335 Partition II. The key tables are:

**TypeDef (table 0x02):** Defines a type within the current module. Each row contains: Flags (visibility, layout, semantics), TypeName, TypeNamespace, Extends (coded index to the base type — TypeDef, TypeRef, or TypeSpec), FieldList (index into Field table), MethodList (index into MethodDef table). Every type defined in your assembly gets exactly one TypeDef row.

**TypeRef (table 0x01):** A reference to a type defined in another assembly (or another module). Contains: ResolutionScope (where to find it — AssemblyRef, ModuleRef, TypeRef for nested types), TypeName, TypeNamespace. The runtime resolves TypeRefs to TypeDefs at load time by searching the referenced assembly's metadata.

**TypeSpec (table 0x1B):** Represents a *constructed* type — a generic instantiation, an array, a pointer, or a byref. Contains a single Signature blob that describes the construction. For example, `List<int>` would be a TypeSpec whose signature encodes `GENERICINST CLASS TypeRef[List`1] 1 VALUETYPE TypeRef[Int32]`. TypeSpec is the only way to reference a closed generic type.

**MethodDef (table 0x06):** Defines a method in the current assembly. Contains: RVA (pointer to IL body or native code), ImplFlags, Flags, Name, Signature, ParamList.

**MemberRef (table 0x0A):** A reference to a method or field in another type. Contains: Class (TypeRef, TypeDef, TypeSpec, ModuleRef, or MethodDef), Name, Signature. This is how you reference `Console.WriteLine` from your assembly — the Class points to a TypeRef for `Console`, and the signature encodes the specific overload.

**MethodSpec (table 0x2B):** A generic method instantiation. Contains: Method (MethodDef or MemberRef), Instantiation (a signature blob listing the type arguments). This is how `Enumerable.Select<Person, string>(...)` is encoded — the MethodSpec points to the open `Select` MethodDef/MemberRef and supplies the concrete type arguments.

**StandAloneSig (table 0x11):** Standalone method signatures for `calli` and local variable declarations.

### Value Types vs. Reference Types at the Runtime Level

At the CLR level, the distinction is fundamental:

**Reference types** (classes, interfaces, delegates, arrays) are always heap-allocated. An object reference (`O` on the stack) is a pointer to the heap. The heap layout is: `[SyncBlock index (word before ObjectHeader)] [MethodTable pointer] [instance fields...]`. The MethodTable pointer is how the GC and runtime identify the type. `sizeof` on reference types gives the pointer size, not the object size.

**Value types** (structs, enums, primitive types) are stored inline — on the stack, inside other objects, or in arrays without indirection. They have no object header or MethodTable pointer when unboxed. When boxed (via `box` opcode), they get wrapped in a heap object with the standard header. The `unbox` opcode returns a managed pointer into the boxed object's data; `unbox.any` copies the value out.

Layout control for value types:
- **`LayoutKind.Sequential`** — fields laid out in declaration order, with configurable packing (`Pack` attribute). Default for structs with `[StructLayout]`.
- **`LayoutKind.Auto`** — the CLR reorders fields for optimal alignment and minimal padding. This is the default for classes and for structs without explicit layout attributes (in practice, C# structs default to Sequential for interop compatibility, but the spec allows Auto).
- **`LayoutKind.Explicit`** — each field has a byte offset (`[FieldOffset(n)]`). Enables unions (overlapping fields). The CLR verifies that reference fields don't overlap with value type fields (this would break GC safety), but value type fields can freely overlap.

### Generics in Metadata

**Open types** have unbound type parameters: `List<T>` where T is a GenericParam. The GenericParam table (0x2A) defines type parameters for types and methods. Each row contains: Number (ordinal), Flags (variance, constraints), Owner (TypeDef or MethodDef), Name. Constraints are specified via GenericParamConstraint table (0x2C), referencing TypeDef/TypeRef/TypeSpec.

**Closed types** (instantiations) are represented as TypeSpec entries with GENERICINST signatures. `Dictionary<string, int>` is a TypeSpec whose blob says "generic instantiation of TypeRef[Dictionary`2] with TypeRef[String] and TypeRef[Int32]."

At runtime, the CLR uses a concept of **canonical instantiations**. For reference type arguments, all instantiations share code: `List<string>` and `List<object>` use the same JIT-compiled native code because all reference types are pointer-sized. For value type arguments, each instantiation gets its own code: `List<int>` and `List<double>` produce different native code because the value type sizes and layouts differ. The canonical form for reference type instantiations is `List<__Canon>` (a special internal type).

This is a profound difference from Java's erased generics — CLR generics are **reified**. The runtime knows at every point what the actual type arguments are. `typeof(T)` works inside generic methods. There is no erasure.

### Runtime Data Structures

When a type is loaded, the runtime constructs:

- **MethodTable:** The primary runtime representation. Contains: GC layout information (which offsets contain references), the virtual method slot table, interface map, parent MethodTable pointer, module reference, EEClass pointer, generic instantiation information. Every managed object's first field points to its MethodTable.

- **EEClass:** Shared metadata that doesn't need to be per-instantiation. Contains: field descriptors, method descriptors, layout info, custom attributes cache. For generic types, the EEClass is shared across all instantiations of the same generic type definition.

- **MethodDesc:** Describes an individual method. Subtypes: IL, FCall, PInvoke, EEImpl (delegates), Array, Instantiated (generic methods), Dynamic, ComInterop. Each MethodDesc occupies ~8 bytes (non-generic) and carries a slot containing the current entry point (initially a PreStub that triggers JIT compilation).

- **FieldDesc:** Describes an individual field, with offset information and type.

- **TypeDesc:** Handles special constructed types that aren't classes/structs: arrays (ArrayTypeDesc), pointers (ParamTypeDesc with `ELEMENT_TYPE_PTR`), byrefs (ParamTypeDesc with `ELEMENT_TYPE_BYREF`), function pointers (FnPtrTypeDesc), and type variables (TypeVarTypeDesc).

### Cross-Assembly Resolution

When the runtime encounters a TypeRef, it resolves it by:
1. Examining the ResolutionScope — typically an AssemblyRef row
2. Loading the referenced assembly (via AssemblyLoadContext probing)
3. Searching the target assembly's TypeDef table by namespace + name
4. Caching the result in a RidMap (token-indexed array) for O(1) subsequent access

For TypeSpec, the runtime parses the signature blob recursively, resolving each component type and constructing the appropriate TypeHandle (MethodTable for classes/structs, TypeDesc for arrays/pointers/etc.).

---

## 3. Method Dispatch

### The Four Call Instructions

**`call <method-token>`** — Direct, non-virtual call. The target is resolved at JIT time to a specific MethodDesc and its entry point. Used for: static methods, non-virtual instance methods, base class calls (`base.Method()`), calls to value type methods. The `this` pointer (if any) is the first argument. No null check is implied by `call` itself, but the callee may dereference `this`.

**`callvirt <method-token>`** — Virtual dispatch. Even when calling a non-virtual method, C# emits `callvirt` to get an implicit null check (the dispatch mechanism dereferences `this` to get the MethodTable). For actual virtual methods, the runtime uses the MethodTable's vtable slot. The target token resolves to a slot number; the actual target depends on the runtime type of `this`.

How vtable dispatch works:
1. The object reference is dereferenced to get the MethodTable pointer
2. The slot index (determined at JIT time from the method token) indexes into the MethodTable's slot array
3. The slot contains the entry point address
4. Jump to that address

This is one indirection beyond a direct call. For non-sealed virtual methods, the JIT cannot generally eliminate it (but see devirtualization below).

**`calli <sig-token>`** — Indirect call through a function pointer. The signature token (StandAloneSig) describes the calling convention and parameter types. The function pointer is on the stack. Used for: delegate invocation internals, P/Invoke, function pointer calls (C# `delegate*`). This is the most flexible and least optimizable call form.

**`ldftn <method-token>`** — Push a function pointer for a non-virtual method. The result is a `native int` that can be used with `calli`.

**`ldvirtftn <method-token>`** — Push a function pointer for a virtual method, resolved against the object on the stack. Performs virtual dispatch at the point of the instruction, not at the call site. Used in delegate construction.

### Interface Dispatch

Interface dispatch is more complex than virtual dispatch because a type can implement multiple interfaces, and the slot position of an interface method varies across implementing types.

The CLR uses **Virtual Stub Dispatch (VSD)** for interface calls. The system has three stub types forming a dispatch pipeline:

1. **Lookup Stub:** The initial state. Created per-token at JIT time. Passes the dispatch token (`<interfaceID, slot>` pair) and the object's MethodTable to the generic resolver. Slowest path.

2. **Dispatch Stub (monomorphic cache):** After the first call resolves, the call site is backpatched to a dispatch stub that compares the incoming MethodTable against the cached type. On match, jumps directly to the target. On miss, falls through to the resolve stub. This is a single comparison + conditional branch — very fast for monomorphic call sites.

3. **Resolve Stub (polymorphic cache):** Consults a global hash table of `<token, MethodTable, target>` triples. On hit, jumps to target. On miss, calls the generic resolver to compute the target and populate the cache.

If a dispatch stub misses too frequently (the call site is polymorphic), the system backpatches to the resolve stub directly, avoiding the overhead of the always-failing type check.

Interface dispatch is slower than virtual dispatch because:
- Virtual dispatch is one indexed load from a fixed offset in the MethodTable
- Interface dispatch requires at minimum a type comparison (monomorphic) or hash lookup (polymorphic)
- Megamorphic call sites (many different types) degrade to hash table lookups on every call

VSD currently applies **only** to interface methods. Virtual instance methods use traditional vtables because the startup overhead and throughput cost of VSD for virtuals was measured to be net-negative.

### Constrained Calls

The `constrained. <type>` prefix before `callvirt` is essential for generic code. When T is unconstrained:

```
constrained. !!T
callvirt instance string [System.Runtime]System.Object::ToString()
```

The runtime behavior depends on the instantiation:
- If T is a reference type: dereference the managed pointer, dispatch virtually. Identical to normal `callvirt`.
- If T is a value type that overrides `ToString()`: call the override directly on the value, no boxing. This is the fast path.
- If T is a value type that does NOT override `ToString()`: box the value, then dispatch virtually on the boxed object.

Without `constrained.`, the compiler would have to box unconditionally, which is a heap allocation on every call.

### JIT Devirtualization

RyuJIT performs several devirtualization optimizations:

**Exact type devirtualization:** If the JIT can prove the exact runtime type (e.g., immediately after `newobj`), it replaces `callvirt` with a direct call. This also enables inlining the target.

**Sealed method/class devirtualization:** If the target method is `sealed` or the class is `sealed`, no override is possible. Direct call.

**Guarded devirtualization (GDV):** Using PGO (profile-guided optimization) data from tier-0 execution, the JIT inserts a type check guard: "if the type is X (the common case observed at runtime), call X's method directly; otherwise, fall back to virtual dispatch." The direct-call path can then be inlined. This is one of the most impactful PGO-driven optimizations.

**Type hierarchy analysis:** In NativeAOT, where the entire program is visible, the compiler can determine that a virtual method has only one implementation in the sealed type hierarchy and devirtualize statically.

---

## 4. Memory Model

### Stack Frames

Each method invocation creates a stack frame. The layout (managed by the JIT, not the programmer) typically contains:
- Saved registers (callee-saved per platform ABI)
- Return address
- Local variables (those not enregistered)
- Spill slots for register allocator temporaries
- Outgoing argument space (on some platforms, arguments beyond register capacity)
- Security object / GS cookie (for stack buffer overrun detection)

On x64 Windows, the frame uses RBP-based or RSP-based addressing (the JIT chooses). On ARM64, X29 is the frame pointer. The CLR ABI requires frame pointer chains for stack walking during GC and exception handling.

`localloc` allocates dynamically-sized memory on the stack frame, returning a `void*`. This is how `stackalloc` in C# works. The allocation is freed when the method returns. Methods containing `localloc` cannot be inlined. The allocated memory is always zero-initialized when the `localsinit` flag is set.

### Managed Heap

The GC heap is where all reference-type objects live. Allocation is extremely fast — essentially a pointer bump:

1. Each thread has an **allocation context** — a pointer to the current allocation position and a limit within a pre-assigned region (~8KB **allocation quantum**)
2. Allocating = increment the pointer by the object size. If it fits within the limit, no synchronization needed
3. When the quantum is exhausted, the GC assigns a new quantum (may trigger collection)

This makes small object allocation essentially lock-free per-thread.

Object layout on the heap:
```
[SyncBlock index]   ← word before the MethodTable pointer (object header)
[MethodTable*]      ← first word of the "object" (what references point to)
[field1]
[field2]
...
```

The SyncBlock index is used for: `lock` (monitor) support, hash code caching, COM interop info, and other per-object metadata. It's stored in a separate SyncBlock table indexed by this slot. Most objects never need a SyncBlock (the slot stays 0), so this is allocated on demand.

### Generational GC

Objects are allocated into **Generation 0**. When gen0 fills up, a gen0 collection runs. Survivors are promoted to **Generation 1**. When gen1 pressure is high, a gen1 collection runs; survivors promote to **Generation 2**. Gen2 collections are full collections (expensive).

The generational hypothesis: most objects die young. Gen0/gen1 collections are fast because they only scan a small portion of the heap. The challenge is finding references from older generations into younger ones (these are roots for ephemeral collections).

**Card tables** solve this: the heap is divided into ~256-byte "cards." When a reference field in an old-generation object is written, the JIT-emitted **write barrier** marks the corresponding card as dirty. During ephemeral collection, only dirty cards are scanned.

The write barrier is a critical piece of JIT-emitted code that runs on every reference field assignment:
```
// Pseudocode for write barrier
void WriteBarrier(ref object slot, object value) {
    slot = value;
    byte* card = cardTable + ((byte*)&slot >> cardShift);
    *card = 0xFF;
}
```

### Large Object Heap (LOH)

Objects >= 85,000 bytes are allocated on the LOH (also called Generation 3). The LOH is:
- Not compacted by default (too expensive to copy large objects) — collected via sweep/free-list
- Always collected with gen2 collections
- Subject to fragmentation
- Since .NET 4.5.1, compaction can be requested via `GCSettings.LargeObjectHeapCompactionMode`

Arrays are the most common LOH residents. A `byte[85000]` goes on the LOH; a `byte[84999]` goes on the SOH (Small Object Heap).

### Pinning

Pinning prevents the GC from moving an object during compaction. At the IL level, a local variable can be declared as `pinned` in the locals signature:

```
.locals init (int32& pinned V_0)
```

The GC treats pinned objects as immovable. This is necessary for passing managed buffers to native code (P/Invoke). Excessive pinning degrades GC performance because it creates fragmentation — the compactor must work around pinned objects.

`fixed` in C# emits pinned locals. `GCHandle.Alloc(obj, GCHandleType.Pinned)` creates a long-lived pin.

### Regions (Modern GC)

Starting with .NET 7 (server GC) and .NET 8 (workstation GC), the GC uses **regions** instead of segments. Regions are smaller (typically 4MB vs. segments' larger sizes), independently reclaimable, and can change generation dynamically. A region initially holds gen0 objects; when those survive, the region becomes a gen1 region. This improves memory efficiency and reduces the "one ephemeral segment" constraint.

### Span\<T\> and ref struct

`Span<T>` is a `ref struct` — a value type that can only live on the stack (never boxed, never a field of a class, never captured by a lambda/async). At the IL/runtime level:

A `ref struct` is marked with the `IsByRefLike` attribute (a custom attribute in metadata). The runtime enforces: no boxing, no heap allocation, no use as a generic type argument (before .NET 9's anti-constraint `allows ref struct`).

`Span<T>` is internally a managed pointer (`byref T`) plus a length. The managed pointer can point to: the managed heap (array interior), the stack (`stackalloc`), or native memory. The GC understands interior pointers — if the span points into a managed array, the GC will update the pointer if the array moves during compaction.

At the IL level, `Span<T>` is just a struct with a `ByReference<T>` field (which is a `ref T` — a managed pointer stored in a struct field, enabled by runtime special-casing) and an `int _length` field.

### Stack Allocation

`localloc` is the IL instruction for stack allocation. It pops a `native uint` (size in bytes) and pushes a `void*`. The memory is part of the current stack frame and is automatically freed on method return. When `localsinit` is set, `localloc` memory is zero-filled.

In modern .NET, `stackalloc` in C# can produce either `localloc` (when assigned to a pointer) or a `Span<T>` (which uses `localloc` under the hood but wraps the result in a span for bounds checking).

---

## 5. JIT Compilation

### RyuJIT Architecture

RyuJIT is the production JIT compiler for all .NET platforms (x64, x86, ARM32, ARM64). It evolved from the legacy jit32 and jit64 compilers, unified into a single codebase.

The central data structure is the `Compiler` object, which maintains a doubly-linked list of `BasicBlock` values forming the control flow graph.

### Intermediate Representation

RyuJIT uses **GenTree** nodes as its IR. Each node has an opcode (`GT_ADD`, `GT_CALL`, `GT_LCL_VAR`, etc.), child pointers, type information, value numbers, and register assignments. The IR exists in two forms:

**HIR (High-level IR):** Tree/statement-oriented. After IL importation, the IR is organized as statements containing expression trees. Evaluation order follows tree structure with `GTF_REVERSE_OPS` for reordering. `GT_COMMA` nodes allow sequencing within trees. HIR persists through all front-end optimization phases.

**LIR (Low-level IR):** After rationalization, the IR becomes a linear doubly-linked list of GenTree nodes in execution order. No tree structure is relied upon. This is the form consumed by register allocation and code generation.

### Compilation Phases

1. **Pre-import / Importation:** IL is read instruction-by-instruction, building the BasicBlock graph and populating GenTree nodes. The importer handles stack-to-tree conversion — each IL push creates a tree node, each IL consumer wires it as a child. Branch targets establish basic block boundaries.

2. **Inlining:** A state machine estimates the native code size of callee methods. Heuristics consider: callee size, call site frequency (from PGO), caller size budget, whether the callee is a "force inline" intrinsic. Successful inlines splice the callee's trees into the caller, with argument and return value plumbing.

3. **Struct Promotion:** Fields of local struct variables are promoted to individual local variables when profitable (the struct is small, fields fit in registers, no address-taking that prevents decomposition).

4. **Morphing:** A suite of normalization and canonicalization passes. Field access becomes pointer arithmetic (`ldobj + offset`). Array access becomes bounds check + pointer arithmetic. `GT_QMARK` ternaries are expanded into basic blocks.

5. **Loop Identification:** Natural loops (strongly connected components with a dominating header) are identified. Loops are canonicalized to have single preheaders and do-while form.

6. **Loop Transformations:** Loop cloning (for guarded devirtualization or bounds check versioning), loop unrolling (complete unrolling only), loop inversion (while → do-while).

7. **SSA Construction:** Standard SSA form with phi nodes. `LclSsaVarDsc` descriptors track SSA definitions. Only `lvTracked` locals (those profitable to track — typically a limited number for efficiency) enter SSA.

8. **Value Numbering:** Symbolic evaluation assigns value numbers to expressions. Expressions with the same value number are known to compute the same value. Used by CSE, copy propagation, assertion propagation, and redundant branch optimization.

9. **Optimization Passes:**
   - **Loop Invariant Code Hoisting (LICM):** Moves loop-invariant computations to preheaders
   - **Copy Propagation:** Replaces variables with their definitions when value numbers match
   - **CSE (Common Subexpression Elimination):** Identifies repeated computations via value numbers, extracts to temporaries
   - **Assertion Propagation:** Propagates non-null, type, and range facts
   - **Range Analysis / Bounds Check Elimination:** Uses value numbering and induction variable analysis to prove array accesses are in-bounds, removing bounds checks
   - **Induction Variable Optimization:** Strength reduction, IV widening on 64-bit targets, dead IV removal
   - **Dead Store Elimination:** Removes stores where the new value equals the old value (VN-based)
   - **If-conversion:** Transforms conditional assignments into `GT_SELECT` (conditional move)
   - **Redundant Branch Optimization:** Folds branches whose conditions are known from value numbering; jumps threading

10. **Rationalization:** Converts from HIR (tree/statement) to LIR (linear execution order). Eliminates `GT_COMMA`, removes `Statement` wrappers, establishes `gtNext`/`gtPrev` as the execution order chain.

11. **Lowering:** Target-specific transformations. Expands switch statements into jump tables or if-else chains. Constructs addressing modes (`GT_LEA` for base+index*scale+offset). Determines block copy strategies. Marks nodes as "contained" (consumed directly by parent, no separate register needed — e.g., a constant that becomes an immediate in the parent instruction) or "reg-optional" (can be consumed from memory if the register allocator doesn't assign a register).

12. **Register Allocation (Linear Scan):** Assigns physical registers to virtual values. Uses a linear scan algorithm with `Intervals` (live ranges), `RegRecords` (physical registers), and `RefPositions` (use/def points). Four sub-phases: preparation, ref-position building, allocation, and write-back. Handles spilling, splitting, and register preferences.

13. **Code Generation:** Walks the LIR in execution order, emitting machine instructions via the `Emitter`. Handles prolog/epilog generation, GC info recording (which registers/stack slots hold live references at each safepoint), and debug info mapping (IL offset → native offset for debugger step-through).

### Tiered Compilation

.NET uses tiered compilation to balance startup speed with steady-state throughput:

**Tier 0:** Methods are JIT-compiled quickly with minimal optimization. No SSA, no value numbering, no CSE, no loop optimizations. The goal is fast startup. Tier 0 code includes instrumentation to collect **profile data** (PGO): call counts, branch taken/not-taken frequencies, type profiles at virtual call sites.

**Tier 1:** After a method is called enough times (threshold ~30 calls), it is re-compiled with full optimizations using the collected PGO data. Tier 1 benefits from: profile-guided inlining decisions, guarded devirtualization (GDV) based on observed types, hot/cold block layout based on branch frequencies.

**On-Stack Replacement (OSR):** For long-running methods (especially those with loops that execute millions of times in tier-0), waiting for the method to return before recompiling is unacceptable. OSR allows replacing a running method's code mid-execution. The tier-0 version includes "patchpoints" at loop backedges. When the trigger fires, the runtime JIT-compiles a specialized version that can resume from the current program counter, transitions the live state, and continues in optimized code. OSR is essential for getting PGO benefits for methods that loop forever (like game loops or server request loops).

### Profile-Guided Optimization (PGO)

Dynamic PGO (default in .NET 8+) collects at tier 0:
- **Block counts:** How many times each basic block executed
- **Edge profiles:** Which branch directions are hot
- **Type profiles:** At virtual/interface call sites, what concrete types were observed
- **Method profiles:** At delegate invocation sites, what targets were called

Tier 1 uses this to:
- Guide inlining (inline hot callees, don't inline cold ones)
- Guarded devirtualization (insert type checks for observed types, direct-call + inline the hot path)
- Hot/cold code separation (put rarely-executed blocks at the end)
- Better register allocation (allocate more registers for hot paths)

### What the JIT Cannot Do That AOT Can

- **Whole-program analysis:** The JIT compiles one method at a time. It cannot see the entire call graph, cannot perform interprocedural optimizations beyond inlining, cannot do whole-program dead code elimination.
- **Link-time optimizations:** Cannot inline across assembly boundaries without loading and JIT-compiling the callee (which it does, but only at the call site — no global code motion).
- **Binary size optimization:** The JIT doesn't care about code size (it's in memory anyway). AOT can aggressively optimize for size.
- **Static devirtualization via sealed hierarchy analysis:** Without seeing all types that will ever be loaded, the JIT cannot prove that a virtual method has only one implementation. (PGO + GDV approximates this, but it's speculative, not proven.)

### Ready-to-Run (R2R)

R2R images contain pre-compiled native code alongside IL. The pre-compiled code is version-resilient (doesn't hard-code offsets or addresses — uses indirections). At runtime, R2R code is used directly for tier-0, avoiding JIT compilation cost entirely. Methods can still be re-JIT-compiled at tier-1 with PGO for better steady-state performance. R2R is a middle ground: faster startup than pure JIT, better steady-state than pure AOT (because tier-1 recompilation can use runtime profiles).

---

## 6. Async Machinery

### The State Machine

When C# compiles an `async` method, it transforms it into a state machine struct (for `async Task`/`async ValueTask`) or class (when the struct would be captured, or for `async void`). At the IL level, the original method body is replaced with:

1. A stub method that creates the state machine, initializes it, and starts it
2. The state machine type implementing `IAsyncStateMachine` with a `MoveNext()` method containing the original logic restructured as a jump table

The state machine has fields for:
- `<>1__state` — current state (int). -2 = not started/completed, -1 = running, 0+ = suspended at a specific await point
- `<>t__builder` — `AsyncTaskMethodBuilder` or `AsyncValueTaskMethodBuilder` — manages task creation and completion
- `<>u__N` — one field per distinct awaiter type used in the method
- Fields for every local variable that lives across an await boundary

`MoveNext()` contains a big switch on `<>1__state`. Each state resumes at the corresponding await point. The general pattern for each await:

```
// Pseudocode of MoveNext() for one await point
case N:
    awaiter = <>u__N;
    <>u__N = default;
    goto AfterAwait_N;

// ... earlier code runs, reaches the await ...
awaiter = someTask.GetAwaiter();
if (!awaiter.IsCompleted) {
    <>1__state = N;
    <>u__N = awaiter;
    <>t__builder.AwaitUnsafeOnCompleted(ref awaiter, ref this);
    return;  // Suspend
}
AfterAwait_N:
result = awaiter.GetResult();
// ... continue with result ...
```

### AsyncTaskMethodBuilder

This is the builder that orchestrates the async machinery. Its responsibilities:
- Creates the `Task` object lazily (only if the method actually suspends — if it completes synchronously, it can return `Task.CompletedTask` or a cached task for small int results)
- `AwaitUnsafeOnCompleted()` registers the state machine's `MoveNext()` as a continuation on the awaiter
- `SetResult()` / `SetException()` complete the task
- The builder stores the state machine (boxed to the heap on first suspension — the struct is stack-allocated initially, and boxing is deferred until actually needed)

### The Awaiter Pattern

Any type can be awaited if it follows the pattern:
- `GetAwaiter()` method (instance or extension) returning a type that implements:
  - `bool IsCompleted { get; }` — if true, the result is available synchronously
  - `void OnCompleted(Action continuation)` or `void UnsafeOnCompleted(Action continuation)` — register a callback
  - `TResult GetResult()` — retrieve the result (or throw the exception)
  - The awaiter must implement `INotifyCompletion` (or `ICriticalNotifyCompletion` for `UnsafeOnCompleted`)

### Task vs. ValueTask

**`Task<T>`:** Heap-allocated, reference type. Represents a single future result. Can be awaited multiple times. Has combinators (`WhenAll`, `WhenAny`). The standard choice.

**`ValueTask<T>`:** A discriminated union (struct) that is either a `T` result value (synchronous completion — zero allocation) or an `IValueTaskSource<T>` (asynchronous completion, poolable). Benefits:
- Zero allocation on the synchronous path (extremely common for cached/buffered I/O)
- When backed by `IValueTaskSource<T>`, the backing object can be pooled and reused across calls, amortizing allocation
- Cannot be awaited multiple times. Cannot be stored. More restrictive API surface.

At the IL level, `ValueTask<T>` is just a struct with a `T _result` field, an `object _obj` field (null for sync, or the `IValueTaskSource<T>`), and a `short _token` field (for pooling validation).

### Thread Pool Integration

When an async method suspends (`AwaitUnsafeOnCompleted`), the continuation is posted to the **thread pool** (unless a `SynchronizationContext` or `TaskScheduler` captures it). The thread pool uses work-stealing queues — each thread has a local queue, and idle threads steal from others.

`ConfigureAwait(false)` suppresses `SynchronizationContext` capture, ensuring the continuation runs on a thread pool thread rather than being marshaled back to the original context (UI thread, ASP.NET request context, etc.).

### SynchronizationContext

A virtual base class that controls where continuations are posted. `SynchronizationContext.Current` is thread-local. UI frameworks install one that marshals to the UI thread. ASP.NET Core does not install one (all continuations run on the thread pool). The async machinery captures it at the await point and posts the continuation via `Post()`.

---

## 7. Assembly Model

### PE Format

.NET assemblies are Portable Executable (PE) files (`.dll` or `.exe`). The PE structure is:

```
DOS Header (legacy)
PE Header (COFF)
  → OptionalHeader
    → DataDirectories[15] = CLI Header RVA  (IMAGE_DIRECTORY_ENTRY_COMHEADER)
CLI Header (Cor20Header)
  → MetaData RVA/Size
  → EntryPointToken
  → Flags (ILOnly, Required32Bit, StrongNameSigned, etc.)
  → Resources, StrongNameSignature, VTableFixups
Metadata
  → #~ stream (compressed metadata tables)
  → #Strings heap (type/method/field names)
  → #US heap (user strings — string literals)
  → #Blob heap (signatures, custom attribute values)
  → #GUID heap
IL Method Bodies (at RVAs referenced by MethodDef table)
Managed Resources
```

### Metadata Tables

ECMA-335 defines 45 metadata tables. The critical ones for a compiler:

| Table | Index | Purpose |
|-------|-------|---------|
| Module | 0x00 | Module identity (name, MVID GUID) |
| TypeRef | 0x01 | References to types in other assemblies |
| TypeDef | 0x02 | Types defined in this assembly |
| Field | 0x04 | Fields defined in this assembly |
| MethodDef | 0x06 | Methods defined in this assembly |
| Param | 0x08 | Method parameters |
| InterfaceImpl | 0x09 | Interface implementations |
| MemberRef | 0x0A | References to methods/fields in other types |
| CustomAttribute | 0x0C | Custom attributes on any metadata entity |
| ClassLayout | 0x0F | Explicit size/packing for types |
| FieldLayout | 0x10 | Explicit field offsets |
| StandAloneSig | 0x11 | Standalone signatures (locals, calli) |
| TypeSpec | 0x1B | Constructed type specifications |
| Assembly | 0x20 | Assembly identity |
| AssemblyRef | 0x23 | References to other assemblies |
| NestedClass | 0x29 | Nested type relationships |
| GenericParam | 0x2A | Generic type parameters |
| MethodSpec | 0x2B | Generic method instantiations |
| GenericParamConstraint | 0x2C | Constraints on generic parameters |

Metadata tokens are 4-byte values: high byte = table index, low 3 bytes = row index (1-based). So `0x02000005` means TypeDef table, row 5.

**Coded indices** are compact encodings used when a column can reference multiple tables. For example, `TypeDefOrRef` is a coded index that can point to TypeDef (tag 00), TypeRef (tag 01), or TypeSpec (tag 10). The tag bits are the low bits; the remaining bits are the row index.

### Assembly Identity and Strong Naming

Assembly identity consists of: Name, Version, Culture, PublicKeyToken. Strong naming adds a cryptographic signature (RSA) over the assembly hash, embedded in the PE. The runtime uses the full identity for assembly binding.

In modern .NET (CoreCLR), strong naming is largely vestigial — the runtime doesn't enforce version matching by default, and loading policies are controlled by the application's `deps.json` file and AssemblyLoadContext.

### AssemblyLoadContext (ALC)

ALC is the isolation boundary for assembly loading. Each ALC maintains its own set of loaded assemblies. The default ALC loads the application and its dependencies. Additional ALCs enable plugin scenarios (load/unload assemblies dynamically).

Resolution order:
1. Check if already loaded in this ALC
2. Call the ALC's `Load()` override (for custom loading logic)
3. Fall back to the default ALC
4. Call the `Resolving` event
5. Throw `FileNotFoundException`

ALCs are collectible — when all references to types loaded in a collectible ALC are released, the entire ALC (and its assemblies) can be garbage collected. This replaces the old AppDomain unloading mechanism.

### Custom Attributes at the Metadata Level

Custom attributes are stored in the CustomAttribute table (0x0C). Each row contains: Parent (the entity the attribute is on — any HasCustomAttribute-coded entity), Type (the attribute constructor — CustomAttributeType-coded MemberRef or MethodDef), Value (a blob encoding the constructor arguments and named property/field assignments).

The blob format (ECMA-335 §II.23.3) is:
```
Prolog (0x0001, 2 bytes)
Fixed args (serialized according to constructor parameter types)
NumNamed (2 bytes, uint16)
Named args (each: FIELD/PROPERTY byte, type, name string, value)
```

This is a compact binary format, not IL. The runtime parses it lazily when `GetCustomAttributes()` is called.

For a compiler emitting attributes via Mono.Cecil, you construct `CustomAttribute` objects with `MethodReference` to the constructor and `CustomAttributeArgument` values — Cecil handles the blob encoding.

---

## 8. Execution Engine

### Thread Management

The CLR manages threads through a `Thread` object (internal runtime structure, not `System.Threading.Thread`) that tracks:
- GC mode (preemptive vs. cooperative)
- Allocation context (for per-thread bump-pointer allocation)
- Exception state
- Last thrown exception
- Synchronization state

**Cooperative mode:** The thread is executing managed code and has committed to responding to GC suspension requests at safepoints. The GC can only collect when all managed threads are at safepoints.

**Preemptive mode:** The thread is executing unmanaged code (P/Invoke, native interop). The GC can proceed without waiting for this thread (the thread's managed state is consistent because it's not modifying managed memory).

**Safepoints** are positions in JIT-compiled code where the GC can safely examine the stack and registers. At each safepoint, the JIT emits GC info describing which registers and stack slots contain live object references. For fully-interruptible code, every instruction is a safepoint. For partially-interruptible code, only call sites and explicit poll points are safepoints.

Thread suspension for GC:
1. The GC requests suspension by setting a global flag
2. For threads in cooperative mode: hijacking (overwriting the return address on the thread's stack with a stub that redirects to the GC) or trap checks (at loop backedges and method entries, the JIT checks the suspension flag)
3. For threads in preemptive mode: no action needed (they're already safe)

### AppDomain (Historical)

In .NET Framework, AppDomains provided in-process isolation. Each AppDomain had its own loaded assemblies, static fields, and security policy. The CLR could unload an AppDomain, reclaiming its resources.

In CoreCLR (.NET 5+), there is only one AppDomain per process. The functionality is replaced by AssemblyLoadContext (for isolation) and separate processes (for true isolation). This simplification removed significant complexity from the runtime.

### Exception Handling

IL encodes exception handling via a table in the method header (the **Exception Handling Clause** table). Each clause specifies:
- **Flags:** catch, filter, finally, or fault
- **TryOffset/TryLength:** The protected region
- **HandlerOffset/HandlerLength:** The handler code
- **ClassToken** (for catch): The exception type to catch
- **FilterOffset** (for filter): Where the filter expression starts

**Clause types:**

- **catch:** Catches exceptions of the specified type (or derived). The exception object is pushed onto the stack at handler entry. Exits normally with fall-through or `leave`.
- **finally:** Runs on both normal exit (`leave` instruction) and exceptional exit. The `endfinally` instruction terminates the handler. Cannot access the exception object.
- **fault:** Like finally but runs only on exceptional exit. C# doesn't emit fault handlers (it uses finally for everything), but they exist in the IL spec and some languages use them.
- **filter:** A two-part handler. First, the filter expression (starting at FilterOffset) executes with the exception object on the stack. It pushes 1 (handle) or 0 (don't handle) and executes `endfilter`. If 1, the handler runs. C# emits filter handlers for `when` clauses in catch.

**Two-pass exception handling:**

Pass 1 (search phase): Walk the stack looking for a handler. For each frame, check if the faulting IP falls within a try region. For catch handlers, test the exception type. For filter handlers, execute the filter expression. If a handler is found, proceed to pass 2. If no handler is found, the process terminates (unhandled exception).

Pass 2 (unwind phase): Walk the stack again from the faulting frame to the handling frame. For each frame, execute finally/fault handlers for all try regions being exited. Then transfer control to the selected catch/filter handler.

**Platform specifics:**

On Windows, managed exception handling integrates with SEH (Structured Exception Handling). The CLR registers its own SEH handler (`ProcessCLRException`) on each managed-to-native transition. Hardware exceptions (access violation, divide by zero) are caught as SEH exceptions and translated to managed exceptions (`NullReferenceException`, `DivideByZeroException`).

On Unix, hardware exceptions are delivered as signals (SIGSEGV, SIGFPE). The CLR installs signal handlers that convert these to managed exceptions. The signal handler must carefully transition from the signal context to managed exception handling, which involves unwinding from the signal frame to the faulting managed frame and beginning the two-pass process.

### leave Instruction

`leave` is the only way to exit a protected region (try block) in verifiable IL. It is not `br` — `leave` clears the evaluation stack, executes all nested finally handlers, and then branches to the target. You cannot `br` out of a try block.

---

## 9. Interop

### P/Invoke

P/Invoke (Platform Invoke) calls native functions from managed code. At the metadata level, a P/Invoke method is a `MethodDef` with the `pinvokeimpl` flag and an entry in the ImplMap metadata table (mapping the method to a DLL name and entry point name).

The runtime generates a **stub** for each P/Invoke call that:
1. Transitions the thread from cooperative to preemptive mode (so GC can proceed)
2. Marshals arguments from managed to native representation
3. Pins managed objects if needed (strings, arrays)
4. Calls the native function
5. Marshals the return value from native to managed representation
6. Transitions back to cooperative mode

**Calling conventions** supported: `Cdecl` (default on most platforms), `StdCall` (Windows), `ThisCall` (Windows COM), `FastCall`. In modern .NET, `UnmanagedCallersOnly` allows native code to call managed methods directly with a specified calling convention.

### Blittable Types

A type is **blittable** if its managed and native memory representations are identical. Blittable types can be passed to native code without any marshaling — the runtime just passes the pointer. Blittable types include:
- Primitive types: `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `nint`, `nuint`
- Structs containing only blittable fields with sequential or explicit layout
- One-dimensional arrays of blittable types (when pinned)

Non-blittable types require marshaling: `string` (varies by charset), `bool` (1 byte in managed, 4 bytes as BOOL in native on Windows), arrays of non-blittable types, delegates, etc.

### Function Pointers

C# 9 introduced `delegate*` syntax, which emits `calli` instructions. At the IL level:

```
ldftn void NativeLib::SomeFunction(int32)
// ... or obtained from native code ...
calli unmanaged cdecl void(int32)
```

The `StandAloneSig` for the `calli` specifies the calling convention (`unmanaged cdecl`, `unmanaged stdcall`, etc.) and parameter/return types. `UnmanagedCallersOnly` methods can have their function pointer passed to native code as a callback without marshaling overhead.

### COM Interop

COM interop wraps COM objects in Runtime Callable Wrappers (RCW) and exposes managed objects as COM objects via COM Callable Wrappers (CCW). At the IL level, COM interface methods use regular `callvirt` — the interop layer is transparent. COM methods in metadata are `MethodDef` entries on an interface with the `[ComImport]` attribute, and the runtime generates the RCW stubs dynamically.

COM interop is not supported in NativeAOT.

### Marshal Class

`System.Runtime.InteropServices.Marshal` provides low-level interop primitives: `AllocHGlobal`/`FreeHGlobal` (native memory), `PtrToStructure`/`StructureToPtr` (marshal structs), `GetFunctionPointerForDelegate`/`GetDelegateForFunctionPointer`, `ReadByte`/`WriteByte` family (raw memory access), `StringToHGlobalAnsi`/`StringToHGlobalUni` (string marshaling).

---

## 10. What C# Doesn't Use

### tail. prefix

The `tail.` prefix requests that the runtime perform a tail call — reusing the caller's stack frame for the callee. C# historically never emitted this (until C# 8, which sometimes emits it for local function recursion under specific conditions, and even then unreliably). F# uses `tail.` extensively because functional languages depend on tail-call optimization for recursive algorithms that would otherwise overflow the stack.

At the runtime level, `tail.` is a complex optimization. RyuJIT may:
- Honor it and reuse the frame (when signatures are compatible)
- Use a "tail call helper" that performs the call via a trampoline (when the callee has a larger frame or incompatible calling convention)
- Ignore it (the `tail.` prefix is technically a request, not a demand — but CoreCLR tries hard to honor it)

For a language compiler, `tail.` is critical if your language has tail-call guarantees (like F# or Scheme targeting CLR). Without it, deep recursion will stack overflow.

### calli with Managed Calling Conventions

C# `delegate*` supports only `unmanaged` calling conventions. But `calli` at the IL level can use the `managed` calling convention — calling a managed method through a function pointer obtained via `ldftn`/`ldvirtftn` without going through a delegate. This is faster than delegate invocation (no delegate object, no invoke stub) but C# doesn't expose it. A custom compiler can emit `calli managed` directly.

### Fault Handlers

Fault handlers run only on exceptional exit from a try block (unlike finally, which runs on both normal and exceptional exit). C# always uses finally. Fault handlers exist in ECMA-335 and are valid IL. They are useful for: undoing partial state changes that should only be undone on failure, without the overhead of a flag variable that finally handlers would need to check.

```
.try {
    // ... code that might throw ...
    leave AfterTry
}
fault {
    // runs only if an exception occurred
    // clean up partial state
    endfault
}
```

### Custom Calling Conventions

The IL and metadata support calling conventions beyond what C# exposes. The `CallConv*` types (`CallConvCdecl`, `CallConvStdcall`, `CallConvThiscall`, `CallConvFastcall`, `CallConvSuppressGCTransition`, `CallConvMemberFunction`) modify function pointer behavior. `SuppressGCTransition` is particularly interesting — it omits the cooperative-to-preemptive mode transition for P/Invoke calls to trivially short native functions, dramatically reducing interop overhead.

### Explicit Struct Layout with Overlapping Fields

C# supports `[StructLayout(LayoutKind.Explicit)]` and `[FieldOffset]`, but the verifier restricts overlapping reference-type fields with value-type fields. The CLR itself supports overlapping value-type fields (unions), which is how you implement C-style unions. C++/CLI uses this for `union` types.

You can also overlap value-type fields of different sizes to create reinterpretation casts (type punning) — like `[FieldOffset(0)] int asInt; [FieldOffset(0)] float asFloat;`.

### Module Initializers

Before C# 9, module initializers (`.cctor` on the `<Module>` type) were an IL feature that C# never emitted. The module initializer runs before any code in the module executes — before any type's static constructor. C# 9 added `[ModuleInitializer]` which emits calls in the `<Module>..cctor`. But the raw mechanism (placing code in `<Module>..cctor()`) has always been available at the IL level.

### init-only Fields at the IL Level

The `initonly` metadata flag on fields (mapped to `readonly` in C#) is enforced only by the verifier, not the runtime. Outside the constructor (for instance `initonly` fields) or static constructor (for static `initonly` fields), assigning to an `initonly` field is unverifiable but valid IL — the runtime will happily execute it. This means `initonly` is an advisory/verification constraint, not a hardware protection.

`init` property setters (C# 9 records) use `IsExternalInit` modreq to indicate that the setter should only be called during initialization. This is entirely a compiler/verifier convention — the IL is just a normal setter method.

### Generic Constraints Not Exposed by C#

The CLR's generic constraint system supports:
- `class` (reference type) — C# exposes this
- `struct` (non-nullable value type) — C# exposes this
- `new()` (.ctor constraint) — C# exposes this
- Interface/base class constraints — C# exposes this
- `notnull` — C# exposes this (C# 8)
- `unmanaged` — C# exposes this (C# 7.3)
- `enum` constraint — the CLR supports constraining to `System.Enum`, which C# exposes since C# 7.3
- `delegate` constraint — similarly via `System.Delegate`
- `allows ref struct` (anti-constraint) — C# 13 / .NET 9

The metadata supports arbitrary type constraints via GenericParamConstraint rows. C# exposes most of them now, but historically it was more restrictive. A custom compiler can use any constraint the runtime supports.

### The jmp Instruction

`jmp <method-token>` transfers control to the target method, replacing the current stack frame. It's like a tail call but without any call setup — the arguments must already match the target's signature. The current method's locals are abandoned. C# never emits this. It's useful for trampolines or method forwarding without stack growth.

### Varargs (\_\_arglist)

The CLR supports C-style varargs via the `vararg` calling convention, `arglist` instruction (gets a handle to the variable argument list), and `System.ArgIterator`. C# has `__arglist` syntax but it's essentially undocumented, rarely used, and not supported on all platforms. At the IL level, vararg methods have a sentinel (`...`) in their signature separating fixed from variable parameters.

### refanytype, refanyval, mkrefany

These instructions work with `TypedReference` — a CLR primitive that packages a managed pointer and its type together. `mkrefany` creates one, `refanyval` extracts the pointer (with type checking), `refanytype` extracts the type token. This is used by `__makeref`, `__refvalue`, `__reftype` in C#, which are undocumented and rarely used. `TypedReference` is an intrinsic type that the CLR handles specially — it's always stack-only (like a ref struct before ref structs existed).

---

## 11. NativeAOT

### How It Differs from JIT

NativeAOT compiles the entire application to native code ahead of time using the ILC (IL Compiler). The output is a self-contained native executable with no dependency on the .NET runtime or JIT. The compilation uses the same RyuJIT backend but in a different mode — it compiles everything upfront rather than on-demand.

The compilation pipeline:
1. **IL scanning / whole-program analysis:** Determine the complete set of types, methods, and generic instantiations reachable from the entry point (transitive closure)
2. **Type system construction:** Build the full type graph, including MethodTables, vtables, and interface maps
3. **Compilation:** JIT-compile every reachable method using RyuJIT
4. **Linking:** Produce a native executable with embedded metadata (minimal), managed-to-native stubs, GC info, and the native code

### What's Lost

**Reflection:** Unrestricted reflection is not available. `Type.GetType("SomeType")` may fail if the type was trimmed. `Activator.CreateInstance` works only for types the AOT compiler can statically see. `MakeGenericType`/`MakeGenericMethod` fails for instantiations not statically reachable. The trimmer aggressively removes unreachable code and metadata.

**Dynamic code generation:** `System.Reflection.Emit`, `Expression.Compile()` (uses interpreted fallback, not compiled), and `AssemblyLoadContext.LoadFromStream()` are not available. There is no JIT to compile dynamically generated IL.

**Some generic instantiations:** For generic virtual methods and certain complex generic patterns, the AOT compiler must generate code for every possible instantiation. If it can't determine the complete set (universal generic code sharing is limited compared to JIT), it may fail to compile or fall back to slower universal shared code.

**Dynamic assembly loading:** No `Assembly.Load()`, no plugin loading. Everything must be known at compile time.

### What's Gained

**Startup time:** No JIT compilation at startup. The process launches and begins executing native code immediately. Startup is typically 5-10x faster than JIT.

**Memory footprint:** No JIT compiler loaded in memory. No IL metadata for methods (only what's needed for remaining reflection). Smaller working set.

**Deterministic performance:** No tier-0 cold-start penalty. No JIT pauses. Every method runs at full optimization from the first call.

**Binary size:** Trimming removes unreachable code, reducing binary size to only what's needed. Typical applications: 5-20 MB self-contained (vs. 60+ MB for a self-contained JIT deployment with the entire runtime).

**Security:** No JIT means no writable-executable memory pages (W^X). Smaller attack surface.

### Trimming

The ILC trimmer performs whole-program dead code elimination:
- Starting from roots (entry point, preserved types/methods), it marks reachable code
- Unremarked code, types, and metadata are removed from the output
- Warnings are generated when the trimmer encounters patterns it can't analyze statically (reflection, dynamic loading)
- `[DynamicallyAccessedMembers]` attributes guide the trimmer about reflection usage

### Impact on Language Design

For a language targeting CLR via NativeAOT:
- Avoid patterns requiring `MakeGenericType`/`MakeGenericMethod` at runtime
- Ensure all generic instantiations are statically determinable
- Don't rely on unrestricted reflection for core language features
- Use `[DynamicallyAccessedMembers]` annotations when reflection is needed
- Consider that `Reflection.Emit` is unavailable — if your language has a runtime code generation feature, it won't work under NativeAOT
- Source generators and compile-time code generation are the AOT-friendly alternatives

---

## 12. CLR vs. JVM: Architectural Comparison

### Value Types

**CLR:** First-class value types (structs) that live on the stack or inline in other objects. No heap allocation, no object header, no virtual dispatch overhead. `Span<T>`, `ValueTuple<>`, `DateTime`, all numeric types are value types. Value types can implement interfaces (boxed when needed, or used generically with constrained calls to avoid boxing).

**JVM:** Until Project Valhalla (in preview), all user-defined types are reference types. Primitives (`int`, `long`, etc.) are value types but cannot implement interfaces or have methods. Every object has a heap allocation and an object header (12-16 bytes). Valhalla aims to add "inline classes" but this is still in development.

This is arguably the CLR's single biggest architectural advantage for performance-sensitive code.

### Generics

**CLR:** Reified generics. `List<int>` and `List<string>` are distinct types at runtime. Value type instantiations get specialized native code (no boxing). `typeof(T)` works. No type erasure. Generic constraints are enforced by the runtime.

**JVM:** Erased generics. `List<Integer>` and `List<String>` are both `List` at runtime. Generics are a compile-time fiction enforced by the Java compiler. No specialization for primitives (must box to `Integer`, `Long`, etc.). `T.class` doesn't compile. Casts are inserted by the compiler.

CLR's approach means generic code over value types (e.g., `Span<byte>`, numeric algorithms over `T where T : INumber<T>`) pays zero abstraction cost — the JIT generates code identical to hand-specialized versions.

### Unsigned Types

**CLR:** Full support for `byte` (uint8), `ushort` (uint16), `uint` (uint32), `ulong` (uint64), `nuint`. Unsigned arithmetic instructions (`div.un`, `rem.un`, `shr.un`, `conv.ovf.*.un`, `bge.un`, etc.) are part of the IL instruction set.

**JVM:** No unsigned types. All integer operations are signed. Workarounds exist (`Integer.toUnsignedLong()`, etc.) but they're library-level, not bytecode-level.

### Ref Returns and Ref Locals

**CLR:** Methods can return managed pointers (`byref`). Local variables can hold managed pointers. This enables zero-copy access into arrays, structs, and other memory without copying. `Span<T>` depends on this.

**JVM:** No equivalent. Methods can only return values or object references. No managed-pointer concept.

### Pointers and Unsafe Code

**CLR:** Unmanaged pointers (`void*`), managed pointers (`ref`/`byref`), function pointers, `stackalloc`, `localloc`, `Unsafe` class, `fixed` statement. The CLR is designed to support systems programming alongside managed code.

**JVM:** `sun.misc.Unsafe` (internal, deprecated), `VarHandle` (limited). No stack allocation, no pointer arithmetic, no function pointers. Panama's Foreign Function & Memory API (JDK 22) is improving this but it's still more constrained.

### Delegates vs. Functional Interfaces

**CLR:** Delegates are first-class multicast function objects with efficient invocation. `Func<>`, `Action<>` are generic delegates. At the IL level, delegates are classes with `Invoke`, `BeginInvoke`, `EndInvoke` methods generated by the runtime.

**JVM:** Functional interfaces + lambda metafactory (`invokedynamic`). More flexible (any single-abstract-method interface qualifies) but more complex machinery. `invokedynamic` enables the JVM to choose the lambda implementation strategy at runtime.

### Bytecode-Level Differences

**CLR IL:** Stack-based, typed evaluation stack, prefix instructions, structured exception handling with filter and fault, constrained calls, explicit value type support, tail call prefix, function pointers.

**JVM Bytecode:** Stack-based, typed but simpler (no managed pointers, no unsigned ops), `invokedynamic` for dynamic dispatch, no value type instructions, no tail call support, `goto`/`jsr` for flow control (no `leave`-style structured exception exit), verification model doesn't distinguish managed/unmanaged pointers.

**JVM's invokedynamic** has no CLR equivalent — it allows the JVM to defer method binding to a user-defined bootstrap method at the call site's first execution. The CLR's closest analog is `DynamicMethod` + delegate caching, but it's fundamentally different (CLR doesn't have call-site-level customizable dispatch).

### Where CLR is Strictly More Capable

1. Value types with full language support (stack allocation, generic specialization)
2. Ref returns and ref locals (zero-copy access patterns)
3. Reified generics (runtime type information preserved)
4. Unsigned types as first-class citizens
5. `Span<T>` and `ref struct` (safe stack-only types with interior pointers)
6. Tail call support in the runtime (not just compiler)
7. `constrained.` call for efficient generic virtual dispatch
8. Explicit memory layout control (`StructLayout`, `FieldOffset`)
9. `calli` with multiple calling conventions (interop without P/Invoke overhead)
10. Filter exception handlers (`catch when` at the IL level)

### Where JVM Has Advantages

1. `invokedynamic` — flexible call-site binding (no CLR equivalent)
2. Mature escape analysis and scalar replacement (the JVM is better at eliminating short-lived object allocations through escape analysis, partially because it's more critical without value types)
3. C2 / Graal JIT compilers have more aggressive speculative optimizations in some scenarios
4. Startup-oriented alternatives (GraalVM Native Image, CDS, AppCDS) have been mature longer than NativeAOT
5. Larger ecosystem for JVM bytecode manipulation (ASM, ByteBuddy, Javassist)

---

## 13. Historical Design Intent and Multi-Language Heritage

### Designed for Multiple Languages

The CLR was explicitly designed as a multi-language runtime. The original .NET launch (2002) included C#, VB.NET, C++/CLI (then Managed C++), and J#. Later, F# (functional), IronPython, IronRuby, and many research languages targeted the CLR.

This multi-language intent shaped the architecture in specific ways:

**The Common Type System (CTS)** defines the full set of types the runtime supports — including types no single language uses completely. C# doesn't use fault handlers; C++/CLI does. C# doesn't use `jmp`; other languages might. The CTS is a superset.

**The Common Language Specification (CLS)** defines the subset of the CTS that all CLS-compliant languages must support for interoperability. If your library's public API uses only CLS-compliant types, it's guaranteed to work from any CLS-compliant language. CLS restricts: no unsigned types in public APIs, no pointer types, no varargs, etc.

### Facilities for Non-C# Languages

**Tail calls:** Added for functional languages (F#, IronScheme). The `tail.` prefix and the runtime's tail-call helper mechanism exist because F# needs guaranteed tail-call optimization.

**Fault handlers:** Added for languages that distinguish "cleanup on exception" from "cleanup always." C++ destructors on stack-unwinding are naturally fault handlers, not finally handlers.

**Varargs:** Added for C/C++ interop. C++/CLI needs to call C vararg functions.

**TypedReference / mkrefany / refanyval:** Added for languages that need to pass references with runtime type information without boxing. Useful for implementing generic parameter passing in languages without generics.

**Module initializers:** Run-before-everything initialization that languages with static initialization semantics need.

**Unsigned types:** Some languages (C, C++) have unsigned types as core features. The CLR provides them at the instruction level.

**Explicit layout and unions:** C/C++ union types map to explicit layout with overlapping fields.

**`constrained.` calls:** Added to make generics work efficiently for value types. Without this, generic code over value types would either box on every virtual call or require language-level specialization.

**`readonly.` prefix:** Added to support covariant array safety without penalizing the common case.

### ECMA-335

The CLR is standardized as ECMA-335 (also ISO/IEC 23271). The standard defines:
- **Partition I:** Architecture — concepts, type system, metadata
- **Partition II:** Metadata definition and semantics — tables, signatures, validation
- **Partition III:** CIL instruction set — every opcode, stack behavior, verification rules
- **Partition IV:** Profiles and libraries — BCL requirements
- **Partition V:** Debug interchange format
- **Partition VI:** Annexes — implementation guidelines

The standard has not been updated since the 6th edition (2012), which covers .NET 4.5-era features. Features added since (ref returns, ref struct, default interface methods, static interface members, generic math, `allows ref struct`) are de facto extensions understood by the Microsoft runtime and Roslyn compiler but not formally standardized in ECMA-335.

For a compiler author using Mono.Cecil, ECMA-335 Partition II (metadata format) and Partition III (instruction set) are the authoritative references for what's legal. Mono.Cecil abstracts the metadata encoding but understanding the underlying model (coded indices, blob encoding, token semantics) is essential for debugging and for generating correct metadata for advanced scenarios.

### Practical Implications for E# (or Any New Language Targeting CLR)

1. **You have the full IL instruction set available.** You're not limited to what C# uses. `tail.`, `calli managed`, fault handlers, `jmp`, `constrained.` — all fair game.

2. **Mono.Cecil gives you direct access to all metadata tables.** You can construct TypeDef, MethodDef, GenericParam, CustomAttribute, etc. exactly as the runtime expects them, without going through C#'s abstractions.

3. **The verifier is your friend and your enemy.** If you want verifiable IL, you must follow strict rules (no pointer arithmetic, no `calli` without appropriate tokens, stack states merge at join points). If you're comfortable with valid-but-unverifiable IL, you have much more freedom but lose some safety guarantees.

4. **Generic instantiation is your responsibility.** You emit the TypeSpec/MethodSpec tokens, and the runtime handles specialization. Understand the canonical form system — all reference-type instantiations share code, value-type instantiations get specialized code.

5. **The JIT is your optimization backend.** Design your IL emission to be JIT-friendly: avoid unnecessary boxing, use `constrained.` for generic calls, emit `readonly.` for safe array indexing, mark methods with appropriate attributes (`AggressiveInlining`, `AggressiveOptimization`). The JIT will handle register allocation, instruction selection, and platform-specific optimization.

6. **NativeAOT compatibility should be considered from the start.** If you want E# code to compile with NativeAOT, ensure all generic instantiations are statically determinable, minimize reflection usage, and avoid runtime code generation.

7. **The CLR's type system is richer than C#'s.** You can use generic constraints that C# doesn't expose, emit module initializers freely, use fault handlers for exception semantics that differ from C#'s finally-only model, and leverage explicit layout for memory-layout control that C# restricts.

8. **Interop is a first-class concern.** `UnmanagedCallersOnly`, `calli`, `SuppressGCTransition`, blittable types — these are the building blocks for zero-overhead native interop. A language that exposes these directly can match C performance for FFI.
