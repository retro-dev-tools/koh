# Koh on standard C#: a CIL frontend

## Context

This started as "why is `MemRuntime.cs` a string?" The answer turned out to be architectural, and the
root is one decision: **Koh C# has its own C-like typing rules** (no int promotion, C-like
widths/signedness), so Koh-legal code is routinely C#-illegal and has no assembly at all. That forces
everything downstream:

- `IntrinsicsStub.cs` — Roslyn must be able to *bind* code it considers illegal, so the compiler
  synthesizes fake `Hardware`/`Gb`/`Mem` declarations.
- `MemRuntime.cs` / `SoftFloatRuntime.cs` — runtime code can only be delivered as **source text**,
  because source text is the only thing the two worlds share.
- `Sdk.targets`' `$(KohRoot)src/Koh.GameBoy/Hal/**/*.cs` glob — the SDK has to know where the
  framework's *files* live, because a reference only gives you an assembly.
- The compiler carries hardware addresses (`HardwareRegisters.Addresses`/`Regions`) and framework
  method names (`__mem_copy`, `Mem.Copy`) it has no business knowing.

Decision: **accept standard C# semantics.** The game is a normal .NET project against the real BCL;
a Roslyn analyzer enforces the subset at edit time; the compiler consumes the game's **compiled
assembly and its references** and lowers CIL to Koh IR. Then framework code is just code in a
referenced assembly, and every workaround above deletes itself.

## Target architecture

### 1. `Frontends/Cil` — a CIL frontend (Mono.Cecil)

`CilFrontend : IFrontend` reads the game's assembly and its references with **Mono.Cecil** (a
resolved object model — instructions with operands already bound to `TypeReference`/`MethodReference`
— rather than hand-decoding metadata blobs over `System.Reflection.Metadata`; pure metadata reading,
so it stays AOT-safe).

Stack machine → IR is smaller than it sounds: Roslyn-emitted IL has an **empty evaluation stack at
nearly every basic-block boundary** (the exceptions are `&&`/`||`/ternary, which carry one value
across a join). So the frontend simulates the stack at compile time, maps IL locals and arguments to
`alloca`s exactly as `CSharpFrontend` already does, and spills to an `alloca` only at a non-empty
join. **`Mem2RegPass` (already written, already default-on) then does the actual SSA construction.**

`IFrontend.Lower(SourceText, …)` grows a `CompilerInput` (file path + optional text + reference
paths) so a frontend can be assembly-driven rather than text-driven.

**Line maps come from the portable PDB** (Cecil `ReadSymbols` → sequence points → `LineMapEntry`),
which is strictly better than what the source frontend hand-rolls today — a real win for the DAP
debugger.

### 2. Intrinsics by attribute — `IntrinsicsStub.cs` dies

`Koh.GameBoy` declares what it is, and the compiler reads it from metadata:

```csharp
[KohIntrinsic("register", 0xFF44)] public static byte LY { get; set; }         // Hardware
[KohIntrinsic("region",   0x8000)] public static byte* Vram => Base + 0x8000;  // Gb
[KohIntrinsic("alloc")]            public static byte* Alloc(int size) { … }   // Mem
[KohIntrinsic("halt")]             public static void Halt() { … }
```

The property/method **body is the desktop implementation**; the **attribute is the ROM
implementation**. One declaration, both worlds, no duplication — which is exactly the thing that was
missing when `Mem.cs` and `MemRuntime.cs` had to be kept in sync by hand.

`HardwareRegisters`' address tables **move out of the compiler and into `Koh.GameBoy`**. The compiler
stops knowing register names, region addresses, or that a thing called `Mem` exists.

### 3. Runtime as ordinary code — the string runtimes die

`Mem.Copy`/`Mem.Fill` become plain methods in `Koh.GameBoy`, lowered from IL like any other code.
No string, no injection, no `UsesMemRuntime`, no `__mem_copy`.

Softfloat needs the reverse mapping — the frontend must *find* the routine to call when it sees an IL
`add` on `float32`. Symmetric attribute: `[KohRuntime("f32.add")]` marks the implementing method, and
the frontend indexes them from the referenced assemblies. So:

> `[KohIntrinsic]` = "compiler implements this." `[KohRuntime]` = "compiler calls this."

`SoftFloatRuntime.cs` and `MemRuntime.cs` are deleted. `RemoveUnreachableFunctions` generalizes from
a name-prefix predicate to "came from a referenced assembly, not the game."

### 4. Narrowing pass

Standard C# promotes: `byte + byte` computes in `int32`. `byte c = a + b;` doesn't compile in real C#
(you must write `(byte)(a + b)`), so every byte-typed *assignment* already ends in a `conv.u1` — the
promotion only bites in intermediate expressions, which makes this a local pattern-match, not
open-ended range inference: an `i32` op whose operands are extensions of `i8`/`i16` and whose result
is truncated back is demoted in place.

We build the pass and **measure rather than gate**. The guardrails already exist and run in the
suite: `MemRuntimeTests.Copy_MarginalCostPerByte_IsWithinLooseCeiling` pins ~302 dots/byte, and
`samples/gb-3d/verify` pins per-render-path frame budgets. A missed narrowing shows up there as a
loud failure, not a silent regression.

### 5. Devirtualization — one pass, four features

LINQ, lambdas, delegates and `yield return` are all **required**, and they turn out to be the same
problem. Roslyn lowers a lambda to a closure class (`<>c__DisplayClass`) plus a delegate, and an
iterator to a sealed state-machine class reached through `IEnumerator<T>`. In Koh-subset code the
target is always statically known:

- a delegate reaching `Invoke` traces back to a single `ldftn` (Roslyn caches it in a `<>9__0_0`
  static field — the pass follows a static field with exactly one assignment);
- an enumerator's concrete type is fixed by the iterator method's return type;
- the state-machine class is `sealed`.

So a single **call-site devirtualization pass** resolves delegate/interface/virtual calls to their one
known target, and the **existing IR inliner** then flattens the small bodies — reproducing what the
source frontend does today with a hand-written LINQ special case, but generalized. Closure classes
are ordinary classes, which the backend already heap-allocates.

Where a target genuinely cannot be resolved to one callee, that is a diagnostic (an indirect-call
backend is out of scope; the analyzer should catch it at edit time).

**This pass is a phase-1 concern, not a later one** — it is load-bearing for two shipped features and
its feasibility should be proven early, not discovered late.

### 6. Debug *and* Release IL

Both are supported inputs. Debug IL is nop-laden, branches to the next instruction, and round-trips
every value through a local; Release IL is dense. Most of that difference is erased by machinery that
already exists — stack simulation into `alloca`s, then `Mem2RegPass` and `SimplifyCfgPass`.

The guarantee is a **test matrix**: every CIL fixture is compiled in *both* configurations, and both
must (a) verify clean under `IrVerifier` and (b) produce identical observable behavior in
`GameBoySystem`. That makes config-dependence a loud failure rather than a lurking one.

### 7. `Koh.Analyzers` — subset enforcement at edit time

Because the game references the real BCL, nothing but an analyzer stands between an author and a
`List<T>`. A Roslyn analyzer (shipped by the SDK) reports out-of-subset constructs **in the IDE**,
which is a strict upgrade over today's compile-time frontend diagnostics.

### 8. Build path — the `$(KohRoot)` glob dies

`CompileKohRom` takes `$(IntermediateAssembly)` + `@(ReferencePath)` instead of `.cs` globs. The game
references `Koh.GameBoy`; the compiler gets what the game references. Nothing knows where anyone's
files live.

### 9. Migration & deletion

Both frontends live (`CompilerRegistry` is built for this). The source frontend keeps every existing
test and sample green while the CIL frontend grows up. The decisive test is only possible *because*
both live: **compile the same source through both frontends, link both, run both in `GameBoySystem`,
and diff observable state** — an A/B oracle for every miscompile.

When `gb-2048-cs` and `gb-3d` both build and pass their verify harnesses on the CIL path,
`CSharpFrontend`, `IntrinsicsStub`, `MemRuntime.cs` and `SoftFloatRuntime.cs` are deleted in one
commit.

## Known hard parts (not hand-waved)

- **Struct IL** (`initobj`, `ldloca`, `ldobj`/`stobj`, `constrained.` calls) and switch tables are
  ordinary but must all be covered.
- **Generic instantiation from IL** — Cecil exposes `GenericInstanceMethod`, and the existing
  monomorphization model maps onto it, but transitive instantiation must be walked.
- The devirtualization pass is the highest-risk piece; see §5.

## Phase 1: walking skeleton

Minimal slices of §1, §2 and §8. Take a C# source file that pokes `Hardware.LCDC` in a loop, run
`dotnet build`, feed the **assembly** to the new `cil` frontend, emit a ROM, run it in
`GameBoySystem`, assert the register writes landed — in **both Debug and Release** configurations. No
narrowing, no structs, no classes, no analyzer, no runtime routing.

It proves the genuinely unknown things — that compile-time stack simulation + `Mem2RegPass` produces
correct IR from Roslyn's IL (in both IL flavors), and that attribute-driven intrinsics can replace
`IntrinsicsStub` — and it turns every later piece into an incremental fill-in against a working
pipeline with an A/B oracle.

A **devirtualization spike** rides alongside phase 1 (§5): prove a single lambda and a single
`yield return` resolve to a direct call, before the design depends on it at scale.

## Verification

- Phase 1: a new `tests/Koh.Compiler.Tests/Frontends/CilEndToEndTests.cs` following the established
  harness (frontend → `IrVerifier.Verify(module).IsEmpty()` → SM83 backend → linker →
  `GameBoySystem`, step, assert registers/memory). CLAUDE.md requires the `IrVerifier` assertion for
  new lowering.
- Ongoing: the dual-frontend A/B oracle described in §9.
- Quality: the existing cycle budgets (`MemRuntimeTests` dots/byte; `samples/gb-3d/verify` frame
  budgets per render path, DMG and CGB).
- End-to-end: `dotnet build samples/gb-2048-cs` and `samples/gb-3d`, then their verify harnesses.

## Execution model

Per the repo's own convention, this lands on a **feature branch off `master`** and integrates via PR.

The work is orchestrated as a **Workflow**, not done inline:

- **Orchestrator: Fable 5** — decomposes each phase, assigns tasks, and *verifies* every returned
  result rather than trusting it. It holds the architecture and the acceptance criteria.
- **Execution: Sonnet subagents** — the substantive implementation work (CIL lowering, the
  devirtualization pass, the narrowing pass, the attribute-driven intrinsic surface, the build task).
- **Execution: Haiku subagents** — the mechanical and verification work (running the test matrix in
  both configurations, sweeping call sites, migrating fixtures, reporting diffs from the A/B oracle).
- Every implementation task is **adversarially verified** before it counts as done: the A/B oracle
  (§9) gives an objective pass/fail — the same program through both frontends must produce identical
  observable state in `GameBoySystem` — so verification is empirical, not a second opinion.
