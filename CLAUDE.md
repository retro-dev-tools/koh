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
  .NET SDK: `Hardware`/`Gb` primitives (managed-only, special-cased by the frontend) plus a `Hal/`
  framework written in the Koh C# subset (`Lcd`, `Joypad`, `Tilemap`/`TileData`, `Ppu`, `Direction`)
  that the SDK also feeds to the frontend, so ROMs get it too. `src/Koh.Build.Tasks` — the in-process
  MSBuild task (`CompileKohRom`) that drives the compiler+linker; `sdk/Koh.Sdk` — the MSBuild SDK that
  ties them together so a game project (e.g. `gb-2048-cs`) is a normal C# project that also emits a `.gb`.
- `tests/Koh.*.Tests` mirror `src/`. `samples/` holds runnable examples (e.g. `gb-2048`,
  `gb-2048-cs`). `docs/superpowers/specs/` holds design specs.

## The compiler platform (`src/Koh.Compiler`)

Two-waist hourglass: a generic typed-SSA IR waist, and the existing `EmitModel`/`.kobj`
waist. Pipeline: `IFrontend.Lower(source) -> IrModule` then `IBackend.Compile(module) -> EmitModel`,
orchestrated by `CompilerDriver`; frontends/backends are registered by hand in
`CompilerRegistry` (AOT-safe; no reflection scanning).

- `Ir/` — `IrType`, `IrValue`, `IrModule`, `IrInstruction`, `IrBuilder`, `IrPrinter`,
  `IrParser` (round-trips the printer), `IrVerifier`.
- `Frontends/CSharp/` — Roslyn parses; `CSharpFrontend` + `MethodLowerer` lower a systems
  subset of C# to IR via syntax-directed lowering. A real `CSharpCompilation`/`SemanticModel`
  (`CSharpSemantics`, incl. candidate acceptance for Koh-legal-but-C#-illegal code) is the ONLY
  resolution path for names/members/calls/intrinsics; monomorphized generic instances bind in a
  second constructed tree, and generic calls route by template symbol + mangled type-arg suffix.
  Koh's own C-like typing (widths/signedness) stays authoritative regardless of what Roslyn
  would infer.
- `Backends/Sm83/Sm83Backend.cs` — hand-written, correctness-first SM83 code generation.
- `Targets/` — `DataLayout` (per-target pointer width / endianness / native int widths).

### Invariants that are easy to break

- **Type sizes flow through `IrType.SizeInBytes`/`SizeInBits`.** A pointer is *not*
  `IrType.Bits` (that is 0 for pointers); its width comes from `DataLayout`. Never size a
  type with `(Ir.Bits + 7) / 8` — use the accessor, or pointer struct fields / globals break.
- **The C# frontend produces no phis** — locals/params are `alloca`s, so control flow needs
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
- **Mixed signed/unsigned** binary ops go through `MethodLowerer.CommonType` (usual-arithmetic
  conversions: wider width wins; a mixed pair whose sign matters promotes to a signed type wide
  enough, else a diagnostic). Do not take signedness/width from the left operand alone.
- **Symbol resolution (via `CSharpSemantics`, incl. candidate acceptance) is the ONLY
  name-resolution path** in the C# frontend. The remaining string-keyed tables (`_methods` for
  softfloat runtime routing / MathF / duplicate detection, per-body locals dicts, and type-name
  resolution in `Types.cs` until Stage-2 P6) are declaration plumbing, not resolution fallbacks —
  don't reintroduce a string lookup where a symbol should resolve; an unresolved symbol is a
  diagnostic. A compilation is required: `LowerCore` reports one diagnostic and stops if
  `CSharpSemantics.Compilation` is null (no supported host hits this).
- **Backend errors are not caught by the driver.** A `NotSupportedException` from the backend
  escapes; the frontend catches `CSharpNotSupportedException` and reports diagnostics. Prefer
  reporting a diagnostic over throwing where the input is user code.
- **Verify end-to-end on the emulator**, not just via unit types: link the `EmitModel` to a
  ROM (`Koh.Linker.Core.Linker`), load it in `GameBoySystem`, set `PC`/`SP`, step, read
  registers/memory. See `Game2048Tests` and `CSharpEndToEndTests` for the harness pattern.

### The "Koh C#" subset

Supported: `byte`/`sbyte`/`ushort`/`short`/`int`/`uint`/`long`/`ulong`/`Int128`/`UInt128`/`bool`
(full arithmetic including mul/div/rem/shift at every width — i8/i16 via register routines, i32/i64/
i128 via generic width-N memory routines; i64/i128 have no register room so they return via
`Sm83Backend.ReturnScratch`),
`char`/string literals (strings only as `byte[]` initializers), `enum` (custom base), `const`,
pointers (`T*` incl. arithmetic/`++`/compare/casts, `*(T*)addr` MMIO, and `stackalloc T[n]` frame
buffers), the `Gb.*` memory regions (`Gb.Vram`/`Gb.TileMap`/… as constant base pointers), fixed arrays
(local + static ROM/WRAM data), value-type `struct`s (nested, arrays-of, whole-copy, `ref`-passed); reference-type `class`es
(heap-allocated via the `Mem` arena, instance fields + non-virtual instance methods with `this`; a class
type also names fields — including of its own type, so linked structures work — parameters, and returns,
all as heap pointers; an instance is usable as a value/`byte*` (`return this;`), and assignment copies the
reference, not the bytes); dynamic allocation (`Mem.Alloc`/`Mem.Reset`); generic methods (monomorphized —
specialized per concrete type argument, transitively; a value shadowing a type parameter is a diagnostic);
array LINQ reductions (`Where`/`Select` pipelines ending in `Sum`/`Count`/`Any`/`All`, plus `Max`/`Min`
directly on an array, compiled to a loop with inlined lambdas); cooperative coroutines (a linear run of
`yield return`s, or a single counted `for` loop with one `yield`, lowered to a MoveNext/Current
state-machine class that captures the iterator's parameters);
`if`/`while`/`do`/`for`/`switch`/`break`/`continue`/`return`; arithmetic/bitwise/shift/compare/`~`,
`&&`/`||`/`?:`/`++`/`--`, compound assignment, usual-arithmetic conversions on mixed signed/unsigned
(mixed pairs promote to a wider signed type up to `long`); a program written as bare top-level
functions **or** as top-level `static class`es (their static methods lower to `Class.Method` functions
— qualified calls plus unqualified sibling calls — and their static fields become program-scope
statics; the entry is the `Main` method wherever it lives; `using` directives and a file-scoped
`namespace` are accepted and dropped, since the frontend resolves by simple name),
`static` fields (WRAM/ROM/const), `ref`/`out`/`in`; a `Hardware` register surface and
`[Interrupt("VBlank")]` handlers, and recursion (direct and mutual; a recursive program moves the CALL
stack into WRAM so it runs hundreds of levels deep, and `rt.pushframe` traps on a stack/heap collision
rather than corrupting memory). Out by design: 128-bit+, classes/GC/generics/async/LINQ. Out-of-subset
constructs are reported as diagnostics.

## Gotchas

- Building the C# sample ROM: `dotnet build samples/gb-2048-cs` (the Koh SDK emits `2048.gb` after the
  managed build). `dotnet run --project samples/gb-2048-cs` builds the ROM and opens it in the Koh
  emulator — the SDK (`Sdk.targets`) overrides `RunCommand` to launch `Koh.Emulator.App` on the game's
  ROM, so this is the default for every Koh game; the managed reference build is still the project's own
  binary — `dotnet exec samples/gb-2048-cs/bin/<config>/net10.0/Gb2048CSharp.dll` for the terminal renderer.
- Don't commit built ROMs (`*.gb`/`*.gbc`), `bin/`, `obj/` — samples ship a `.gitignore`.
- The model identifier you run as must not appear in commits, PR bodies, or code.
