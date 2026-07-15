# CLAUDE.md

Guidance for Claude Code working in this repo. Conventions (style, naming, commit
format, security) live in [`AGENTS.md`](AGENTS.md) — read it and follow it; this
file adds a map of the codebase and the non-obvious invariants that are easy to break.

## What this is

Koh is a .NET 10 (C# 14) Game Boy development toolchain: assembler, linker, LSP,
emulator, DAP debugger, a UI layer, and a retargetable **compiler platform**
(`Koh.Compiler`) with pluggable frontends over a shared SSA IR over pluggable
backends.

## Build & test

`dotnet` must be a .NET 10 SDK (see `global.json`). Common loops:

```bash
dotnet build Koh.Ci.slnf                                   # CI build (0 warnings; TreatWarningsAsErrors)
dotnet test --project tests/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj   # one project (fast)
dotnet msbuild build.proj -t:Test                          # full suite (excludes RGBDS compat tests)
```

Tests use **TUnit** (`[Test] async Task`, `Assert.That(x).IsEqualTo(y)` / `.IsEmpty()` /
`.IsEquivalentTo(...)`), not xUnit. Test methods are `async Task` without an `Async`
suffix (established repo pattern; the `AGENTS.md` async-suffix rule is for production code).

## Layout

- `src/Koh.Core` — shared: diagnostics, text spans, binding (`EmitModel`, `LineMapEntry`),
  and `Encoding/Sm83InstructionTable` (the canonical SM83 opcode table).
- `src/Koh.Emit`, `src/Koh.Linker.Core` (+ `Koh.Asm`/`Koh.Link` CLIs) — object emission and
  linking; `RomWriter` fills the cartridge header/global checksums.
- `src/Koh.Emulator.Core` (+ `Koh.Emulator.App`), `src/Koh.Debugger`, `src/Koh.Lsp`, `KohUI*`.
- `src/Koh.Compiler` — the compiler platform (details below).
- `src/Koh.GameBoy` — the managed reference runtime a Koh C# game builds/runs against under the plain
  .NET SDK: `Hardware`/`Gb` primitives (`[KohIntrinsic]`-tagged: a managed desktop implementation plus
  the metadata the CIL frontend reads for the ROM address) plus a `Hal/` framework (`Lcd`, `Joypad`,
  `Tilemap`/`TileData`, `Ppu`, `Cgb`, `Direction`) and `Mem.cs`/`SoftFloat.cs` that are ordinary compiled
  C# — a ROM gets them not by being fed extra source, but because the CIL frontend lowers
  `Koh.GameBoy.dll` (a normal build reference, listed in `@(ReferencePath)`) on demand, transitively,
  the first time a game actually calls into it. `src/Koh.Build.Tasks` — the in-process MSBuild task
  (`CompileKohRom`) that drives the compiler+linker; `sdk/Koh.Sdk` — the MSBuild SDK that ties them
  together so a game project (e.g. `gb-2048-cs`) is a normal C# project that also emits a `.gb`.
- `tests/Koh.*.Tests` mirror `src/`. `samples/` holds runnable examples (e.g. `gb-2048`,
  `gb-2048-cs`). `docs/superpowers/specs/` holds design specs.

## The compiler platform (`src/Koh.Compiler`)

Two-waist hourglass: a generic typed-SSA IR waist, and the existing `EmitModel`/`.kobj`
waist. Pipeline: `IFrontend.Lower(CompilerInput) -> IrModule` then `IBackend.Compile(module) -> EmitModel`,
orchestrated by `CompilerDriver`; frontends/backends are registered by hand in
`CompilerRegistry` (AOT-safe; no reflection scanning).

- `Ir/` — `IrType`, `IrValue`, `IrModule`, `IrInstruction`, `IrBuilder`, `IrPrinter`,
  `IrParser` (round-trips the printer), `IrVerifier`.
- `Frontends/Cil/` — the ONLY frontend (the former `Frontends/CSharp/` Roslyn-syntax-directed
  frontend was deleted once this one reached parity; see `docs/superpowers/specs/
  2026-07-14-cil-frontend-design.md`). Lowers a **compiled assembly** (`CompilerInput.FromAssembly`,
  never source text) read with Mono.Cecil — a resolved object model over standard C# IL, not
  hand-parsed syntax, so the game's own source is ordinary, standard-semantics C# a plain `csc`/Roslyn
  build already accepts. `CilFrontend` -> `CilModuleLowerer` (declarations) -> `CilMethodLowerer` (IL
  opcode-by-opcode body lowering, split across `CilMethodLowerer.*.cs` for structs/arrays/delegates/
  generics/iterators/LINQ/statics/floats) -> `CilLoweringContext` (the shared per-compile state: function/
  global/class-layout caches, on-demand lowering entry point `EnsureLowered`). `Koh.Compiler` never
  references `Koh.GameBoy` — the hardware/runtime surface arrives entirely through two attributes read
  by SIMPLE TYPE NAME off metadata (`CilIntrinsicIndex`/`CilRuntimeIndex` build the lookup tables by
  scanning for these names, not a hardcoded assembly reference):
  - `[KohIntrinsic(kind, address)]` on a `Koh.GameBoy.Hardware`/`Gb` member — `"register"`/`"region"`
    (a fixed MMIO address), `"alloc"`/`"heapreset"` (the arena heap), or an address-less control
    intrinsic (`"ei"`/`"di"`/`"halt"`/`"nop"`/`"stop"`). This is where hardware addresses live now —
    NOT in the compiler.
  - `[KohRuntime(key)]` on a method — the ROM implementation the frontend CALLS for an IL-level
    operation it can't inline (e.g. `[KohRuntime("f32.add")]` on `Koh.GameBoy.SoftFloat`'s add routine
    for a `float` `add` opcode).
  Framework code that is neither of those (the `Hal/` classes, `Mem.Copy`/`Fill`'s byte-shuffling loops)
  is ordinary compiled Koh.GameBoy.dll IL, lowered ON DEMAND the first time a game actually calls it
  (`CilLoweringContext.EnsureLowered`, transitively) — never eagerly, never by a name-keyed table. Pass 1
  eagerly declares (and Pass 2 lowers) every hand-written static method in the game's own module
  regardless of reachability, so `CilModuleLowerer.Lower` prunes EVERY function — game module's own dead
  code included, not just an unreachable referenced-assembly function — unconditionally, before the
  optimizer (`IrOptimizer.RemoveUnreachableFunctions(module)`, no `removable` scope), from the real roots
  (the entry function plus every function with an `InterruptVector`): pruning a dead game function removes
  its dangling calls too, so a call can never point at a callee the sweep already dropped. An interrupt
  handler is never called explicitly, so it must be (and is) a root in its own right, or it would be
  wrongly pruned as unreachable.
- `Backends/Sm83/Sm83Backend.cs` — hand-written, correctness-first SM83 code generation.
- `Targets/` — `DataLayout` (per-target pointer width / endianness / native int widths).

### Invariants that are easy to break

- **Type sizes flow through `IrType.SizeInBytes`/`SizeInBits`.** A pointer is *not*
  `IrType.Bits` (that is 0 for pointers); its width comes from `DataLayout`. Never size a
  type with `(Ir.Bits + 7) / 8` — use the accessor, or pointer struct fields / globals break.
- **The CIL frontend produces no phis** — locals/params are `alloca`s, so control flow needs
  no phi construction. But the IR optimizer's `Mem2RegPass` (default-on in `CompilerDriver`) *does*
  insert phis, so the backend's phi path runs on real compiled programs, not just hand-written/parsed
  IR (`Sm83ControlFlowTests`). A wide phi is forced to interfere with its incoming values in
  `FunctionAllocation` so a byte-by-byte edge copy can't partially overlap its own source.
- **`IrVerifier` is not run inside `CompilerDriver`** — only tests call it. Invalid IR reaches
  the backend, which may "work by accident." Assert `IrVerifier.Verify(module).IsEmpty()` in
  tests for new lowering.
- **SM83 backend is an accumulator machine**: everything flows through `A`; `HL` is the
  pointer register; static WRAM allocation (NESFab-style). Recursion is supported: a function
  in a call cycle saves/restores its shared static frame on a software stack (`SoftSp`) around
  each entry, takes its args via `ArgScratch`, and returns via `ReturnScratch`. i8 returns in
  `A`, i16 in `HL`, i32 in `DE:HL`, i64 (and any recursive return) in memory (`ReturnScratch`).
  A recursive program also relocates the hardware CALL stack from the tiny HRAM window into WRAM
  (`SP = HwStackTop`, growing down) at entry, and `rt.pushframe` traps if the software stack meets
  the descending `SP` or the heap ceiling — so deep recursion runs hundreds deep and overflows halt
  cleanly instead of crashing into the I/O registers. A recursive interrupt handler is rejected
  (its epilogue must be `RETI` with a balanced stack, incompatible with the memory-return path).
- **Register allocator (`FunctionAllocation`)**: a multi-byte result is written in place
  byte-by-byte, so it *interferes with its own operands* (a partial slot overlap would clobber
  a source mid-read). Phi parallel-copies detect clobbers by *allocated slot*, not SSA identity.
  If you add or change a wide-result emitter, keep it consistent with this rule.
- **ROM banking** (MBC1, emitted automatically when a program overflows a single 32KB ROM):
  - *Data*: read-only data past the fixed ROM0 window (`[0x2000, 0x4000)`) spills into switchable
    banks (windowed at `0x4000`). A banked global's address is only valid while its bank is mapped,
    so code selects the bank first (`*(byte*)0x2000 = bank;`).
  - *Code*: when the overflow fits one extra bank, functions past the ROM0 code window
    (`[CodeBase, 0x2000)`) plus the runtime move into bank 1 — the bank MBC1 maps by default and this
    code never switches away from, so all calls stay direct. When the overflow needs 2+ banks,
    `CompileMultiBank` re-emits with the far-call-thunk model: ROM0 keeps the entry, interrupt
    handlers, the runtime, and one thunk per banked function; every other function is packed into
    switchable banks. A call to a banked function goes through its ROM0 thunk, which maps the callee's
    bank (`CurBank` tracks the current one), CALLs it through the `0x4000` window, and restores the
    caller's bank; banked functions return via `ReturnScratch` so the restore can't clobber the result.
    Addresses resolve per region in `Emitter.Resolve`.
  - Code and data banking are **mutually exclusive** (banked code needs its bank mapped, banked data
    needs to switch away). A single banked function can't exceed 16KB, and the ROM0 thunk table must
    fit the ROM0 code window; overflowing either is a diagnostic.
- **Mixed signed/unsigned** binary ops follow the IL's own usual-arithmetic-conversion shape (Roslyn
  already promoted mixed operands per ECMA-334 by the time the frontend sees the IL — see the next
  bullet); the frontend does not re-derive width/signedness from source syntax the way the deleted
  C# frontend's `MethodLowerer.CommonType` did.
- **Arithmetic promotion is now standard C# (ECMA-334), not the old Koh-C#-subset rule.** The deleted
  `CSharpFrontend` never widened operands before a mixed-width op (`byte * 16` wrapped mod 256); the
  CIL frontend lowers whatever IL Roslyn already emitted, and Roslyn performs ordinary C# int/usual-
  arithmetic promotion before that IL exists. A game written against the old subset's narrow-arithmetic
  assumption computes a DIFFERENT result under the CIL frontend for the same source — this is by
  design (the whole point of the CIL frontend is standard C# semantics), not a bug to chase.
- **Name/member/intrinsic resolution has no string-keyed table to get wrong** — Mono.Cecil hands the
  frontend already-resolved `MethodReference`/`TypeReference`/`FieldReference` operands (the CLR did
  the binding when the game assembly was compiled), so there is no Koh-side symbol table to keep in
  sync the way the deleted C# frontend's `CSharpSemantics` was. The two places a NAME still matters are
  both metadata-attribute matches by SIMPLE TYPE NAME (`CilIntrinsicIndex`/`CilRuntimeIndex`, and
  `[Interrupt]` kind lookup in `CilLoweringContext.InterruptVectorOf`) — deliberate, since
  `Koh.Compiler` must never reference `Koh.GameBoy` directly.
- **Backend errors are not caught by the driver.** A `NotSupportedException` from the backend
  escapes; the frontend catches its own `CilNotSupportedException` per-method (one bad method reports
  a diagnostic and is skipped, not a whole-compile abort) and reports diagnostics. Prefer reporting a
  diagnostic over throwing where the input is user code.
- **Verify end-to-end on the emulator**, not just via unit types: link the `EmitModel` to a
  ROM (`Koh.Linker.Core.Linker`), load it in `GameBoySystem`, set `PC`/`SP`, step, read
  registers/memory. A CIL-frontend test compiles real C# with Roslyn to a real assembly on disk first
  (`CompilerInput.FromAssembly`) — see `CilLoweringTests`/`CilEndToEndTests`/`CilGame2048Tests` for the
  harness pattern; the test project keeps `Microsoft.CodeAnalysis.CSharp` for exactly this (compiling
  fixtures to assemblies), even though `Koh.Compiler` itself no longer references Roslyn at all. A perf-
  or timing-sensitive fixture must compile that Roslyn step at `OptimizationLevel.Release`, not the
  default `Debug` — Debug IL's redundant stores/un-folded constants are real cost the CIL frontend
  lowers faithfully, unlike the old syntax-directed frontend which never saw IL at all and so never
  varied with build configuration.
### "Koh C#" is now standard C# — a subset by what the backend can lower, not by parser rules

There is no Koh-specific syntax or Koh-specific typing rule left: a game is an ordinary C# project a
plain `csc`/Roslyn build already accepts (`AllowUnsafeBlocks=true`, nothing else nonstandard), and the
CIL frontend lowers WHATEVER IL that produces. Arithmetic promotion, operator overload resolution,
overload/generic-method binding, `switch` pattern lowering — all of it is Roslyn's, decided before the
CIL frontend ever runs. What makes a program compile to a ROM is purely whether the SM83 backend can
lower the resulting IL shapes; an out-of-scope construct is a diagnostic, not a parse error.

Supported: `byte`/`sbyte`/`ushort`/`short`/`int`/`uint`/`long`/`ulong`/`Int128`/`UInt128`/`bool`
(full arithmetic including mul/div/rem/shift at every width — i8/i16 via register routines, i32/i64/
i128 via generic width-N memory routines; i64/i128 have no register room so they return via
`Sm83Backend.ReturnScratch`),
`char`/string literals (strings only as `byte[]` initializers), `enum` (custom base), `const`,
pointers (`T*` incl. arithmetic/`++`/compare/casts, `*(T*)addr` MMIO, and `stackalloc T[n]` frame
buffers — ordinary C# unsafe code, so every containing method/type needs the real `unsafe` keyword,
unlike the deleted frontend's own parser which never required it), the `Gb.*` memory regions
(`Gb.Vram`/`Gb.TileMap`/… — `[KohIntrinsic("region", addr)]`-tagged properties on `Koh.GameBoy.Gb`,
constant base pointers on a ROM), fixed arrays (local + static ROM/WRAM data), value-type `struct`s
(nested, arrays-of, whole-copy, `ref`-passed); reference-type `class`es (heap-allocated via the `Mem`
arena, instance fields + non-virtual instance methods with `this`; a class type also names fields —
including of its own type, so linked structures work — parameters, and returns, all as heap pointers;
an instance is usable as a value/`byte*` (`return this;`), and assignment copies the reference, not the
bytes); dynamic allocation (`Mem.Alloc`/`Mem.Reset` are `[KohIntrinsic("alloc"/"heapreset")]`;
`Mem.Copy`/`Mem.Fill` are ordinary compiled `Koh.GameBoy` code (`Mem.cs`), lowered on demand like any
other referenced-assembly method — not appended/hand-written; forward copy, overlap defined only when
destination < source, count==0 a no-op, NOT vblank-aware — caller's responsibility like
`Cgb.CopyToVram`); generic methods (monomorphized — specialized per concrete type argument,
transitively via `CilMethodLowerer.Generics.cs`'s `CilGenericSubst`, which substitutes Cecil's own
`GenericInstanceMethod` type arguments directly — no syntax-tree rewriting, since Cecil already exposes
the concrete types at the call site); array LINQ reductions (`Where`/`Select` pipelines ending in
`Sum`/`Count`/`Any`/`All`, plus `Max`/`Min` directly on an array, compiled to a loop with inlined
lambdas — matched off the BCL `Enumerable`/lambda IL shape, not source syntax); cooperative coroutines
(a linear run of `yield return`s, or a single counted `for` loop with one `yield`, lowered from the
C# compiler's OWN generated state-machine class — the frontend recognizes and re-lowers Roslyn's
iterator boilerplate rather than building its own);
`if`/`while`/`do`/`for`/`switch`/`break`/`continue`/`return`; arithmetic/bitwise/shift/compare/`~`,
`&&`/`||`/`?:`/`++`/`--`, compound assignment, STANDARD C# (ECMA-334) usual-arithmetic conversions on
mixed signed/unsigned (int promotion applies — `byte * 16` no longer wraps mod 256 the way the deleted
frontend's own narrower rule did; this is a deliberate, load-bearing behavior change, not a bug); a
program written as top-level `static class`es (their static methods lower to `Class.Method` functions;
static fields become program-scope statics; the entry is whichever method is actually named `Main`) —
delegates and closures (a capturing lambda's compiler-generated display class, lowered like any other
class); `static` fields (WRAM/ROM/const) plus a `.cctor` for non-trivial static initializers/ROM array
data; `ref`/`out`/`in`; a `Hardware` register surface (`[KohIntrinsic("register", addr)]`-tagged
properties on `Koh.GameBoy.Hardware`) and `[Interrupt("VBlank")]` handlers (matched by the attribute
type's simple name — `Koh.Compiler` never references `Koh.GameBoy`); recursion (direct and mutual; a
recursive program moves the CALL stack into WRAM so it runs hundreds of levels deep, and
`rt.pushframe` traps on a stack/heap collision rather than corrupting memory); and `float`/`double`
arithmetic, routed through `[KohRuntime(key)]`-tagged `Koh.GameBoy.SoftFloat` routines rather than
inline codegen. Out by design: 128-bit+ float, reflection, unbounded/dynamic allocation patterns the
backend can't statically size. Out-of-subset constructs are reported as diagnostics, never silently
miscompiled.

## Gotchas

- Building the C# sample ROM: `dotnet build samples/gb-2048-cs` (the Koh SDK emits `2048.gb` after the
  managed build). `dotnet run --project samples/gb-2048-cs` builds the ROM and opens it in the Koh
  emulator — the SDK (`Sdk.targets`) overrides `RunCommand` to launch `Koh.Emulator.App` on the game's
  ROM, so this is the default for every Koh game; the managed reference build is still the project's own
  binary — `dotnet exec samples/gb-2048-cs/bin/<config>/net10.0/Gb2048CSharp.dll` for the terminal
  renderer. Under the hood this is the `cil` frontend (`Koh.Sdk`'s `KohFrontend` MSBuild property,
  `CompileKohRom` task): the plain .NET SDK compiles the game to a real managed assembly first, then
  `CompileKohRom` hands `TargetPath` + `@(ReferencePath)` (which includes `Koh.GameBoy.dll`, so the Hal
  framework/`Mem.Copy`/softfloat all resolve) to `CilFrontend` — no source files are read a second time.
  There is only ever one frontend registered (`cil`); `KohFrontend`/`CompileKohRom.Frontend` exist so a
  future frontend can be added without touching `Sdk.targets` again, not because `csharp` is still an
  option.
- Don't commit built ROMs (`*.gb`/`*.gbc`), `bin/`, `obj/` — samples ship a `.gitignore`.
- The model identifier you run as must not appear in commits, PR bodies, or code.
