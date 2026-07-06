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
  subset of C# to IR **without the semantic model** (own C-like typing).
- `Backends/Sm83/Sm83Backend.cs` — hand-written, correctness-first SM83 code generation.
- `Targets/` — `DataLayout` (per-target pointer width / endianness / native int widths).

### Invariants that are easy to break

- **Type sizes flow through `IrType.SizeInBytes`/`SizeInBits`.** A pointer is *not*
  `IrType.Bits` (that is 0 for pointers); its width comes from `DataLayout`. Never size a
  type with `(Ir.Bits + 7) / 8` — use the accessor, or pointer struct fields / globals break.
- **The C# frontend produces no phis** — locals/params are `alloca`s, so control flow needs
  no phi construction. Phi handling in the backend is exercised only by hand-written/parsed IR
  (e.g. `Sm83ControlFlowTests`).
- **`IrVerifier` is not run inside `CompilerDriver`** — only tests call it. Invalid IR reaches
  the backend, which may "work by accident." Assert `IrVerifier.Verify(module).IsEmpty()` in
  tests for new lowering.
- **SM83 backend is an accumulator machine**: everything flows through `A`; `HL` is the
  pointer register; static WRAM allocation (NESFab-style). Recursion is supported: a function
  in a call cycle saves/restores its shared static frame on a software stack (`SoftSp`) around
  each entry, takes its args via `ArgScratch`, and returns via `ReturnScratch`. i8 returns in
  `A`, i16 in `HL`, i32 in `DE:HL`, i64 (and any recursive return) in memory (`ReturnScratch`).
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
pointers (`T*` incl. arithmetic/`++`/compare/casts and `*(T*)addr` MMIO), fixed arrays (local +
static ROM/WRAM data), value-type `struct`s (nested, arrays-of, whole-copy, `ref`-passed); reference-type `class`es
(heap-allocated via the `Mem` arena, instance fields + non-virtual instance methods with `this`);
dynamic allocation (`Mem.Alloc`/`Mem.Reset`); generic methods (monomorphized — specialized per
concrete type argument, transitively);
`if`/`while`/`do`/`for`/`switch`/`break`/`continue`/`return`; arithmetic/bitwise/shift/compare/`~`,
`&&`/`||`/`?:`/`++`/`--`, compound assignment, usual-arithmetic conversions on mixed signed/unsigned
(mixed pairs promote to a wider signed type up to `long`); static methods + top-level functions,
`static` fields (WRAM/ROM/const), `ref`/`out`/`in`; a `Hardware` register surface and
`[Interrupt("VBlank")]` handlers, and recursion (direct and mutual). Out by design: 128-bit+,
classes/GC/generics/async/LINQ. Out-of-subset constructs are reported as diagnostics.

## Gotchas

- Building the C# sample ROM: `dotnet run --project samples/gb-2048-cs/build`.
- Don't commit built ROMs (`*.gb`/`*.gbc`), `bin/`, `obj/` — samples ship a `.gitignore`.
- The model identifier you run as must not appear in commits, PR bodies, or code.
