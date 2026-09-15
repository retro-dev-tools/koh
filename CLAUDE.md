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
dotnet test --project tests/Compiler/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj   # one project (fast)
dotnet msbuild build.proj -t:Test                          # fast suite (no Koh.Compiler.Tests)
dotnet msbuild build.proj -t:TestAll                       # + Koh.Compiler.Tests — memory-hungry
```

Tests use **TUnit** (`[Test] async Task`, `Assert.That(x).IsEqualTo(y)` / `.IsEmpty()` /
`.IsEquivalentTo(...)`), not xUnit. Test methods are `async Task` without an `Async`
suffix (established repo pattern; the `AGENTS.md` async-suffix rule is for production code).

## Layout

- `src/Common/Koh.Common` — `Diagnostic`/`DiagnosticBag`, `TextSpan`, `SourceText`: the syntax-free base every tool shares.
- `src/Asm/Koh.Core` — the assembler front end (syntax, binding); `EmitModelBuilder`/`PatchExpressionBuilder` turn its binding state into the shared object model.
- `src/Common/Koh.Opcodes` — `Sm83InstructionTable` (the canonical SM83 opcode table) and
  `Sm83Disassembler`, which decodes from that table (the debugger's disassembly view uses it).
- `src/Common/Koh.Objects` — the shared object model (`EmitModel`, `SectionData`, `SymbolData`, `PatchEntry` with a
  syntax-free `PatchExpression`), the `.kobj`/RGBDS object readers and writers, and the `.kdbg` reader.
- `src/Link/Koh.Linker` (+ `Koh.Asm`/`Koh.Link` CLIs) — linking and `.kdbg` writing; `RomWriter` fills the cartridge
  header/global checksums.
- `src/Emulator/Koh.Emulator` (+ `Koh.Emulator.App`), `src/Emulator/Koh.Debugger`, `src/Asm/Koh.Lsp`, `KohUI*`.
- `src/Compiler/Koh.Compiler` — the compiler platform (details below).
- `src/Compiler/Koh.GameBoy` — the managed reference runtime a Koh C# game builds/runs against under the plain
  .NET SDK: `Hardware`/`Gb` primitives (`[KohIntrinsic]`-tagged: a managed desktop implementation plus
  the metadata the CIL frontend reads for the ROM address) plus a `Hal/` framework (`Lcd`, `Joypad`,
  `Tilemap`/`TileData`, `Ppu`, `Cgb`, `Direction`) and `Mem.cs`/`SoftFloat.cs` that are ordinary compiled
  C# — a ROM gets them not by being fed extra source, but because the CIL frontend lowers
  `Koh.GameBoy.dll` (a normal build reference, listed in `@(ReferencePath)`) on demand, transitively,
  the first time a game actually calls into it. `src/Compiler/Koh.Build.Tasks` — the in-process MSBuild task
  (`CompileKohRom`) that drives the compiler+linker; `src/Compiler/Koh.Sdk` — the MSBuild SDK that ties them
  together so a game project (e.g. `gb-2048-cs`) is a normal C# project that also emits a `.gb`.
- `src/Compiler/Koh.GameBoy/Graphics` (Bg/Sprites/Palettes/Text/Win, vblank-safe VRAM writes) and `Framework`
  (`Game.Run`, `Scene`, `Input`, `Rng`, `Clock`, `TileAsset`) sit on top of `Hal/`; the ideal-code spec
  is `docs/superpowers/specs/2026-07-19-ideal-game-api-design.md`.
- `src/Emulator/Koh.Boot` — Koh's DMG/CGB boot ROMs (asm, assembled at build). `src/Emulator/Koh.Verify` — `RomHarness`
  for scripted headless runs + PNG/GIF capture (used by `samples/*/verify`).
- `tests/Koh.*.Tests` mirror `src/`. `samples/`: `gb-2048` (asm), `gb-2048-cs`; `gb-2048-v2` and
  `gb-jrpg` are the north-star games (acceptance: `tests/Compiler/Koh.Compiler.Tests/Samples/Gb2048V2Tests.cs`,
  `GbJrpgTests.cs`); `gb-3d`, `gb-gfx-demo` are graphics demos. `docs/superpowers/specs/` holds design specs.

## The compiler platform (`src/Compiler/Koh.Compiler`)

Two-waist hourglass: a generic typed-SSA IR waist, and the existing `EmitModel`/`.kobj`
waist. Pipeline: `IFrontend.Lower(CompilerInput) -> IrModule` then `IBackend.Compile(module) -> EmitModel`,
orchestrated by `CompilerDriver`; frontends/backends are registered by hand in
`CompilerRegistry` (AOT-safe; no reflection scanning).

- `Ir/` — `IrType`, `IrValue`, `IrModule`, `IrInstruction`, `IrBuilder`, `IrPrinter`,
  `IrParser` (round-trips the printer), `IrVerifier`.
- `Frontends/Cil/` — the ONLY frontend (the former `Frontends/CSharp/` Roslyn-syntax-directed
  frontend was deleted once this one reached parity). Lowers a **compiled assembly** (`CompilerInput.FromAssembly`,
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
- **Semantics are Roslyn's (ECMA-334).** The frontend lowers IL as emitted: int promotion, mixed
  signed/unsigned conversions, overload/generic binding, `switch` lowering are decided before it runs
  (`byte * 16` does not wrap). Code written for the old Koh-subset narrow arithmetic computes different
  results — by design, not a bug.
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
  ROM (`Koh.Linker.Linker`), load it in `GameBoySystem`, set `PC`/`SP`, step, read
  registers/memory. A CIL-frontend test compiles real C# with Roslyn to a real assembly on disk first
  (`CompilerInput.FromAssembly`) — see `CilLoweringTests`/`CilEndToEndTests`/`CilGame2048Tests` for the
  harness pattern; the test project keeps `Microsoft.CodeAnalysis.CSharp` for exactly this (compiling
  fixtures to assemblies), even though `Koh.Compiler` itself no longer references Roslyn at all. A perf-
  or timing-sensitive fixture must compile that Roslyn step at `OptimizationLevel.Release`, not the
  default `Debug` — Debug IL's redundant stores/un-folded constants are real cost the CIL frontend
  lowers faithfully, unlike the old syntax-directed frontend which never saw IL at all and so never
  varied with build configuration.
### "Koh C#" is standard C# — a subset by what the backend can lower

A game is an ordinary C# project (`AllowUnsafeBlocks=true`, nothing else nonstandard); an unlowerable
IL shape is a diagnostic, not a parse error. The full catalog of what the backend can and cannot lower lives in the `koh-csharp-subset` skill
(`.claude/skills/koh-csharp-subset/SKILL.md`) — read it before writing or porting a game's C# source,
or when diagnosing an out-of-subset diagnostic.

## Gotchas

- Building the C# sample ROM: `dotnet build samples/csharp/gb-2048-cs` (the Koh SDK emits `2048.gb` after the
  managed build). `dotnet run --project samples/csharp/gb-2048-cs` builds the ROM and opens it in the Koh
  emulator — the SDK (`Sdk.targets`) overrides `RunCommand` to launch `Koh.Emulator.App` on the game's
  ROM, so this is the default for every Koh game; the managed reference build is still the project's own
  binary — `dotnet exec samples/csharp/gb-2048-cs/bin/<config>/net10.0/Gb2048CSharp.dll` for the terminal
  renderer. Under the hood this is the `cil` frontend (`Koh.Sdk`'s `KohFrontend` MSBuild property,
  `CompileKohRom` task): the plain .NET SDK compiles the game to a real managed assembly first, then
  `CompileKohRom` hands `TargetPath` + `@(ReferencePath)` (which includes `Koh.GameBoy.dll`, so the Hal
  framework/`Mem.Copy`/softfloat all resolve) to `CilFrontend` — no source files are read a second time.
  There is only ever one frontend registered (`cil`); `KohFrontend`/`CompileKohRom.Frontend` exist so a
  future frontend can be added without touching `Sdk.targets` again, not because `csharp` is still an
  option.
- Don't commit built ROMs (`*.gb`/`*.gbc`), `bin/`, `obj/` — samples ship a `.gitignore`.
- The model identifier you run as must not appear in commits, PR bodies, or code.
- A cartridge now boots through Koh's own boot ROM (`src/Emulator/Koh.Boot`, assembled by koh-asm/koh-link at build time). A hand-built test ROM needs the Nintendo logo + header checksum (`TestRom.Create()`) or it freezes at `jr nz,@`; a harness that jumps straight to PC=$0100 must `Array.Clear(gb.Mmu.VramArray)`, because VRAM powers on as $FF.
- `Koh.Boot.csproj` finds the koh-asm/koh-link binaries via `<MSBuild Targets="GetTargetPath">`; don't hard-code `bin/...` or add `GlobalPropertiesToRemove` (that builds Koh.Core twice and races on its `deps.json` under `dotnet publish`). Check with `dotnet publish src/Emulator/Koh.Emulator.App -c Release -r linux-x64`.
- Close a file stream before `File.Move`-ing it: Windows refuses to rename an open file, and only Windows CI catches it.
- Deleting or renaming a CI job: also update master's required status checks (`gh api repos/retro-dev-tools/koh/branches/master/protection/required_status_checks`), or every PR is blocked.
