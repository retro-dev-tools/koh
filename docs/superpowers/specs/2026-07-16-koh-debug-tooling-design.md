# Koh debug tooling design

Status: design only, not implemented. Motivated by a real debugging session (this branch) for
"board tiles render garbled on mGBA but SCORE text is fine" in `samples/gb-2048-cs`, which took
hours longer than it should have because every diagnostic step required a hand-written, throwaway
TUnit harness. This doc inventories what existed already, what was missing, and proposes the
minimal additions — reusing existing hooks/state wherever one already does the job.

The actual bug (for context on why each pain point mattered): CPU writes into VRAM were landing
during PPU mode 3 (Drawing) with the LCD on, which real hardware silently drops. Confirming that
required (1) a way to see the framebuffer, (2) a way to see VRAM contents, (3) a way to run the ROM
far enough to reach steady state, and (4) a way to catch the illegal writes in the act — all of
which had to be improvised from scratch, more than once, in more than one test project.

## What already exists (read before designing — do not rebuild these)

- `GameBoySystem` (`src/Koh.Emulator.Core/GameBoySystem.cs`): `RunFrame()`, `StepInstruction()`,
  `DebugReadByte`/`DebugWriteByte` (thin wrappers over `Mmu.DebugRead`/`DebugWrite`, always readable
  regardless of DMA/PPU bus ownership), and `RunUntil(in StopCondition)`.
- `StopCondition`/`StopConditionKind` (`src/Koh.Emulator.Core/StopCondition.cs`): a `[Flags]` enum
  already *declares* `PcEquals`, `PcInRange`, `PcLeavesRange`, `MaxCycles`, `VBlank`, `Return` — but
  `GameBoySystem.StopConditionMet` (lines 476-500) only implements the three `Pc*` cases. `MaxCycles`,
  `VBlank`, and `Return` are dead enum members today.
- `RunUntil` itself (lines 439-474) is frame-bounded: its outer loop is
  `while (Clock.FrameSystemTicks < frameBudget)`, so it returns `StopReason.FrameComplete` after at
  most one frame even if the condition never fires. It cannot "run until spinning" across many
  frames as-is.
- `Mmu.Hook` (`src/Koh.Emulator.Core/Bus/Mmu.cs:35`): a single settable `MemoryHook?` (abstract
  `OnRead`/`OnWrite`), called from `ReadByte`/`WriteByte`. **It is one slot, not a list** — every
  existing hook (`Mode3WriteGuard`, `WatchpointHook`, ad hoc `VramWriteRecorder`s) assumes it owns
  `Mmu.Hook` outright. Any design that wants to combine hooks (e.g. mode-3 guard + an address
  watchpoint) needs a small `CompositeMemoryHook : MemoryHook` that fans out to a list — trivial, but
  worth stating so nobody rediscovers the single-slot constraint mid-implementation.
- **The "mode-3 write guard" already exists three times, verbatim**: `Koh.Compiler.Tests`'s
  `CilTileSetTests.Mode3WriteGuard`, `Koh.Compat.Tests`'s `Board2048RenderDiagnosticTests.Mode3WriteGuard`
  (written earlier this session), and `samples/gb-3d/verify/Mode3WriteGuard.cs`. All three are the
  same ~15 lines: on `OnWrite`, skip unless address is `$8000-$9FFF`, `LCDC & 0x80 != 0`, and
  `Ppu.Mode == PpuMode.Drawing`; otherwise record `(address, value, LY)`. Triplicated code is exactly
  the signal that it should be a production class, not a test fixture.
- **Source-mapped debugging already exists, but only in the DAP path, and is dead for SDK-built
  games**: `Koh.Debugger.Session.SourceMap` does `(file, line) -> addresses` and
  `Lookup(BankedAddress) -> SourceLocation?` (address -> file/line) already; `DebugInfoLoader.Load`
  populates it from a parsed `.kdbg` — and in the *same loop* also populates a `SymbolMap` from
  `parsed.Symbols` (`foreach (var sym in parsed.Symbols) SymbolMap.Add(sym)`), so a `.kdbg` gives both
  halves of "PC 0x15E9 = `Tilemap.Clear` loop, Video.cs:56": the symbol name from `SymbolMap`, the
  file/line from `SourceMap`. There's also `Koh.Debugger.Disassembler.DecodeOne(Func<ushort,byte>,
  ushort)` — a minimal but working SM83 disassembler already usable standalone (it takes a plain byte
  reader delegate, e.g. `gb.DebugReadByte`, no DAP session needed) for turning a raw PC into a
  mnemonic when a symbol/line isn't available. The linker already knows how to produce the `.kdbg`
  that feeds all of this: `Koh.Link/Program.cs:104-107` calls `DebugInfoBuilder` +
  `DebugInfoPopulator.Populate(builder, result)` + `KdbgFileWriter.Write`, fed from both
  `LinkResult.Symbols` (function/label names) and `LinkResult.LineMap`
  (`IReadOnlyList<ResolvedLineMapEntry>`) — one `Populate` call populates both the symbol table and the
  address map in the `.kdbg`, so nothing extra is needed beyond what `Koh.Link` already calls.
  **But** `CompileKohRom`
  (`src/Koh.Build.Tasks/CompileKohRom.cs:92-110`, the task the Koh SDK invokes after every `dotnet
  build`) only pulls `link.RomData` out of the `LinkResult` and throws the rest away — it never calls
  `DebugInfoPopulator`/`KdbgFileWriter`, so **no `.gb` built by a game project ever gets a `.kdbg` next
  to it**. This means the entire existing DAP source-mapping stack is unusable for any SDK-built game
  today, not because it's missing, but because the one call site that would produce its input file
  never runs. This is the single highest leverage/effort fix in this document — it's ~10 lines wired
  into an existing task, using pieces (`DebugInfoBuilder`, `DebugInfoPopulator`, `KdbgFileWriter`) that
  already work and are already exercised by `Koh.Link`'s own tests.
- `WatchpointHook` (`src/Koh.Debugger/Session/WatchpointHook.cs`) is the existing memory-condition
  stop primitive: a `MemoryHook` that matches configured addresses and calls
  `system.RunGuard.RequestStop(StopReason.Watchpoint)`. `RunGuard` (`src/Koh.Emulator.Core/RunGuard.cs`)
  is a thread-agnostic stop flag `GameBoySystem` already polls once per instruction in `RunFrame`/
  `RunUntil`. A headless "run until a memory condition" primitive is this same hook reused outside the
  DAP session, not new machinery.
- Screenshot-to-PNG already has two independent from-scratch implementations this session/repo:
  `Acid2Tests` (`tests/Koh.Compat.Tests/Emulation/Acid2Tests.cs`) and
  `Board2048RenderDiagnosticTests` (same directory, written as a throwaway this session): both do
  `RunFrame()` in a loop, `Framebuffer.Front.ToArray()`, `Image.LoadPixelData<Rgba32>(...,
  Framebuffer.Width, Framebuffer.Height)`, `SaveAsPngAsync`. `Framebuffer`
  (`src/Koh.Emulator.Core/Ppu/Framebuffer.cs`) is already RGBA8888, 160x144, double-buffered,
  `Front`/`Back`/`Flip()` — nothing about it needs to change.
- `Koh.Emulator.App/Program.cs` already parses ad hoc flags out of `args` before treating the rest as
  positional (see its `--dap=<pipe>` handling) and already loads a ROM headlessly via
  `EmulatorApp.LoadRomFromDisk` before any window opens — the scaffold for a headless flag-driven mode
  already exists in the one binary that already references `GameBoySystem`, `ImageSharp` is not yet a
  dependency of it but is already a test dependency, so adding it is low-risk.
- There is **no top-level `koh` CLI** — only `Koh.Asm`, `Koh.Link`, `Koh.Emulator.App` (GL window +
  optional DAP server), `Koh.Debugger` (DAP library, hosted by `Koh.Emulator.App`/VS Code), and the
  SDK's in-process `CompileKohRom` task. Any "one-command" proposal below places itself in one of
  these, not in an invented new umbrella binary.

## Priority ranking (leverage per line of new code)

1. **Wire `.kdbg` output into `CompileKohRom`.** ~10 lines in an existing task, reusing
   `DebugInfoBuilder`/`DebugInfoPopulator`/`KdbgFileWriter` verbatim as `Koh.Link` already does.
   Unblocks source-mapped debugging (#5) for every SDK game and makes the *existing* DAP debugger
   (VS Code) usable against `gb-2048-cs` for the first time. Nothing here is new design — it is
   turning on a call that already exists elsewhere in the codebase.
2. **Promote `Mode3WriteGuard` to `Koh.Emulator.Core.Debug`.** Deletes three copies of the same 15
   lines; no new logic, since the guard's exact behavior (LCDC/mode/region checks) is already proven
   in three call sites.
3. **Headless screenshot mode.** The harness pattern already exists twice (`Acid2Tests`,
   `Board2048RenderDiagnosticTests`); this is extraction plus a CLI flag, not new rendering code.
4. **Finish `RunUntil` into a real "run until condition or terminal spin" primitive.** Partially
   built already (`StopCondition`, frame-bounded `RunUntil`, `RunGuard`); needs (a) implement the
   `MaxCycles` branch as the loud safety cap, (b) loop `RunUntil` across frames instead of returning
   after one, (c) add spin detection (the one genuinely new piece of logic), (d) reuse
   `WatchpointHook` for the memory-condition case instead of reinventing it.
5. **VRAM/OAM/tilemap/register inspector.** Legitimately the most new code of the five — no tile
   decoder, tilemap-grid formatter, OAM decoder, or IO-register name table exists anywhere in the
   repo today. Still worth building (this is what a human actually stares at), but honestly ranked
   last: it has the lowest reuse-to-new-code ratio.

Below, each pain point from the motivating list gets its own section with a recommended home,
minimal API surface, and reuse notes; the ranking above cuts across them (#5 in the pain-point list
maps to priority 1, #4 maps to priority 2, #1 maps to priority 3, #3 maps to priority 4, #2 maps to
priority 5).

---

## 1. Headless "run ROM → screenshot" (pain point 1; priority 3)

**Where it lives:** a new headless branch in `Koh.Emulator.App/Program.cs`, gated by a `--screenshot=
<out.png>` flag parsed the same way `--dap=` already is (`args.StartsWith("--screenshot=")`),
combined with `--frames=N` (default e.g. 120, matching `Acid2Tests`'s existing default) and optional
`--input=<script>` for scripted joypad edges. When the flag is present, skip window/audio/GL setup
entirely: construct `GameBoySystem` directly (same as `Acid2Tests`/`Board2048RenderDiagnosticTests`
already do), run `RunFrame()` in a loop (parsing the input script to call `JoypadPress`/`JoypadRelease`
at the right frame numbers, mirroring `Board2048RenderDiagnosticTests.PressAndRelease`), then encode
`Framebuffer.Front` via ImageSharp and exit — no `Runner`/`GlBackend` construction at all. This makes
`dotnet run --project src/Koh.Emulator.App -- 2048.gb --screenshot=out.png --frames=120` the one-liner
the pain point asks for, reusing `Koh.Emulator.App`'s existing ROM-loading/cartridge code instead of a
second copy in a new project.

**Minimal API surface:**
- `Koh.Emulator.App`: parse `--screenshot=<path>`, `--frames=<n>`, `--input=<path>` alongside the
  existing `--dap=`; if `--screenshot` is present, run the headless capture path and `return` before
  any UI backend is constructed.
- Input script format: reuse whatever's simplest — a text file of `frame:button:press|release` lines
  is enough for the `PressAndRelease` pattern already used in `Board2048RenderDiagnosticTests`; no
  need for a new DSL.
- `ImageSharp` becomes a dependency of `Koh.Emulator.App` (already a test-only dependency; this
  promotes it to a runtime one for this binary only).

**Why not a new tool:** `Koh.Emulator.App` already loads ROMs and references `GameBoySystem`/
`Framebuffer`; a separate `koh screenshot` binary would duplicate cartridge loading, hardware-mode
detection, and boot-state setup that already lives there.

---

## 2. VRAM/OAM/tilemap/register inspector (pain point 2; priority 5)

**Where it lives:** a new headless dump mode alongside the screenshot one — `--dump=<what>` in
`Koh.Emulator.App` (e.g. `--dump=vram,tilemap,oam,io`), writing text (ASCII tile art / hex grids) to
stdout or a file, run against a snapshot after N frames the same way `--screenshot` does. This is the
one pain point with no existing implementation to lift, so it's kept as a single flag rather than a
family of separate tools to avoid re-deriving the "load ROM, run N frames, then inspect" boilerplate
per dump kind.

**Minimal API surface (all new, all reading only `GameBoySystem.DebugReadByte`/`Ppu`, nothing that
needs write access):**
- Tile decoder: given a base address and count, read 16 bytes/tile from VRAM and render each as an
  8x8 ASCII block (map 2bpp color id 0-3 to `. ` `: ` `+ ` `# ` or similar) or a small PNG grid
  (reusing the screenshot mode's ImageSharp reference). No existing code does this 2bpp decode outside
  the PPU's own internal rendering path — this is genuinely new, small (\<50 lines), well-understood
  logic.
- Tilemap dump: read 32x32 bytes from `$9800`/`$9C00` via `DebugReadByte`, print as a hex grid (index
  per cell) — this is what `Board2048RenderDiagnosticTests` and `CilTileSetTests` currently do ad hoc
  with a raw byte loop and hand rolled `Console.WriteLine` formatting; formalizing it is low effort.
- OAM dump: 40 entries x 4 bytes at `$FE00`, decode Y/X/tile/flags into one line each — same shape as
  the tilemap dump, over a different fixed region.
- IO register dump: **there is no existing name<->address table** for GB IO registers anywhere in the
  repo (checked `IoRegisters.cs`: it switches on raw hex addresses, no lookup table). This piece needs
  a small new static table (`0xFF40 -> "LCDC"`, etc.) — the only place in this whole design that has no
  prior art to lean on, which is exactly why it's ranked last.

**Reuse:** everything reads through `GameBoySystem.DebugReadByte`, already always-valid regardless of
PPU/DMA bus ownership (see its doc comment on `Mmu.DebugRead`) — no new read path needed.

---

## 3. Mode-3 write guard as a built-in diagnostic (pain point 4; priority 2)

**Where it lives:** `src/Koh.Emulator.Core/Debug/Mode3WriteGuard.cs`, next to `MemoryHook` itself —
promoted from the three verbatim copies (`CilTileSetTests.Mode3WriteGuard`,
`Board2048RenderDiagnosticTests.Mode3WriteGuard`, `samples/gb-3d/verify/Mode3WriteGuard.cs`) into one
production class. Delete the three copies and have all three call sites (plus any headless tool)
construct the shared one.

**Minimal API surface (matches the existing three copies almost exactly, so effectively zero new
design risk):**
```
public sealed class Mode3WriteGuard(GameBoySystem system) : MemoryHook
{
    public IReadOnlyList<Mode3Violation> Violations { get; }
    public override void OnWrite(ushort address, byte value); // unchanged logic
    public override void OnRead(ushort address, byte value) { } // no-op, unchanged
}
public readonly record struct Mode3Violation(ushort Address, byte Value, byte Ly);
```
**Source-line enrichment (the "silently dropped write, with source line" ask):** once pain point 5's
`.kdbg` wiring exists, a violation's discovery-time `Cpu.Registers.Pc` can be resolved through
`SourceMap.Lookup(new BankedAddress(bank, pc))` for `Video.cs:56`-style output, and through
`SymbolMap` for the enclosing function name (`Tilemap.Clear`) — the same pair used in section 5's
"PC -> name + file:line" story. That means `Mode3WriteGuard` should record `Pc` per violation (one
extra field) and leave the symbol/file/line lookup to the caller (headless tool or DAP surfacing)
rather than taking a `SourceMap`/`SymbolMap` dependency itself — keeps `Koh.Emulator.Core` free of a
`Koh.Debugger`/`Koh.Linker.Core` reference.

**Single-slot caveat:** since `Mmu.Hook` is one slot, a caller that also wants a watchpoint (or the
VRAM-write recorder pattern already used ad hoc) needs to compose hooks. Add one small
`CompositeMemoryHook : MemoryHook` (fan out `OnRead`/`OnWrite` to a `List<MemoryHook>`) in the same
`Debug` folder — this is the one piece of net-new plumbing this pain point needs, and it's a few
lines.

---

## 4. Run-until-condition primitive with a loud safety cap (pain point 3; priority 4)

**Where it lives:** extend the existing `GameBoySystem.RunUntil`/`StopCondition` rather than adding a
parallel API — the scaffold (enum, struct, `RunGuard`, frame-bounded loop) already exists; it's
incomplete, not wrong.

**Concrete gaps to close, in the existing files:**
- `StopConditionMet` (`GameBoySystem.cs:476-500`): implement the already-declared `MaxCycles` case —
  compare total elapsed T-cycles since `RunUntil` started against `condition.MaxTCycles` and return
  true (with a distinct `StopReason`, e.g. `BudgetExceeded`, so callers can tell "hit the condition"
  from "ran out of budget" instead of silently returning `FrameComplete` as the current fixed-budget
  test helpers do). This is the "loud safety cap" pain point 3 explicitly asks for — the whole reason
  `RunUntilSpinning`'s silent 200k-instruction cutoff caused a multi-hour false trail is that hitting
  the cap and finding the real condition looked identical from the caller's side.
- `RunUntil`'s outer loop: currently `while (Clock.FrameSystemTicks < frameBudget)`, i.e. bounded to
  one frame. Change to loop across frames — call `Clock.ResetFrameCounter()` per frame boundary
  internally and keep iterating — so a condition (or the `MaxCycles` cap above) can span the many
  frames a real `Video.Init`-style spin loop needs, rather than requiring the caller to call
  `RunUntil` once per frame itself.
- Terminal-spin detection: genuinely new logic — the *comment* on the existing
  `CilTileSetTests.RunUntilSpinning` already claims "stops once PC has stopped moving forward for a
  while (revisited the same small PC set)"; the actual body is `for (steps < budget)
  gb.StepInstruction()` with no such detection at all (confirmed by reading it — this mismatch between
  doc comment and implementation is exactly what caused the false trail this session). The real
  version: track a small ring buffer or set of the last K distinct PCs; if PC re-enters that set
  without growing it for M consecutive instructions, treat it as "spinning" and stop with a new
  `StopReason.Spinning`. Expose this as a new `StopConditionKind.Spinning` (joining the existing enum)
  rather than a separate method, so it composes with `MaxCycles`/watchpoints in one `RunUntil` call.
- Memory-condition stopping: don't add a new mechanism — attach a `WatchpointHook` (already exists,
  already calls `RunGuard.RequestStop`) via `Mmu.Hook` (or the new `CompositeMemoryHook` from section
  3) before calling `RunUntil`; `RunUntil` already checks `RunGuard.StopRequested` every instruction.

This turns "run until the program reaches its terminal spin, a PC condition, or a memory condition,
with a loud cap" into one `RunUntil` call composing pieces that already exist, plus the `MaxCycles`
branch and spin detection as the only truly new lines.

---

## 5. `.kdbg` for SDK-built games + source-mapped headless output (pain point 5; priority 1)

**Where it lives:** `src/Koh.Build.Tasks/CompileKohRom.cs`, right after the existing
`link.RomData`/`File.WriteAllBytes(OutputPath, rom)` block (lines 92-110).

**Concrete change:** mirror `Koh.Link/Program.cs:104-107` exactly:
```
var builder = new DebugInfoBuilder();
DebugInfoPopulator.Populate(builder, link);
using var kdbgStream = File.Create(Path.ChangeExtension(OutputPath, ".kdbg"));
KdbgFileWriter.Write(kdbgStream, builder);
```
plus a `FileWrites` entry for the `.kdbg` path (matching the existing `FileWrites` entry for the ROM
in `Sdk.targets`, so incremental build/clean tracks it). Optionally gate behind a new
`KohEmitDebugInfo` MSBuild property (default true) for parity with how `KohCgbCompatible` is already
exposed, in case a release build wants to skip it.

**What this unlocks for free:** the DAP debugger (`src/Koh.Debugger`) already has everything needed to
answer "PC 0x15E9 = `Tilemap.Clear` loop, Video.cs:56" — both halves: `DebugInfoLoader.Load(kdbgBytes)`
populates `SourceMap` (address -> file/line, via `SourceMap.Lookup(BankedAddress)`) *and* `SymbolMap`
(address -> name) from the same `.kdbg` in the same loop. Once `gb-2048-cs` (and every other SDK game)
produces a `.kdbg` next to its `.gb`, VS Code's existing DAP launch (`LaunchHandler.cs:27`, which
already does `Path.ChangeExtension(args.Program, ".kdbg")` as its default) works against it with
**zero new debugger code** — the gap was purely "no file to load," not missing functionality.

**For the headless tools in sections 1-4:** a small standalone loader — reuse
`Koh.Debugger.Session.DebugInfoLoader`/`SourceMap`/`SymbolMap` directly (add a project reference from
wherever the headless tool lives to `Koh.Debugger`, or extract just `SourceMap`+`SymbolMap`+
`KdbgReader` parsing into a lower-level shared spot if `Koh.Emulator.App` shouldn't depend on the DAP
library) — to resolve a PC to `name` + `file:line` when printing violations/dumps, falling back to
`Koh.Debugger.Disassembler.DecodeOne` for a raw mnemonic when no symbol/line is available (e.g. inside
`Koh.GameBoy`'s own compiled Hal code if it ever ships without a matching `.kdbg`). This is wiring, not
new source-mapping logic: the lookup itself already exists and already works (it's exercised by the
DAP debugger today).

---

## Summary of decisions this design makes explicit

| Pain point | Home | New code | Reused |
|---|---|---|---|
| `.kdbg` for SDK games | `CompileKohRom.cs` | ~10 lines + MSBuild property | `DebugInfoBuilder`/`Populator`/`KdbgFileWriter` (from `Koh.Link`) |
| Mode-3 guard | `Koh.Emulator.Core.Debug.Mode3WriteGuard` (new, promoted) | 0 (copy existing logic) + `CompositeMemoryHook` | `MemoryHook`, `Ppu.Mode`/`LCDC` |
| Screenshot | `Koh.Emulator.App --screenshot` | flag parsing + input-script format | `Acid2Tests`/`Board2048RenderDiagnosticTests` harness pattern, `Framebuffer` |
| Run-until-condition | `GameBoySystem.RunUntil`/`StopCondition` (extended) | `MaxCycles` branch, cross-frame loop, spin detection | `RunGuard`, `WatchpointHook` |
| VRAM/OAM/tilemap/register dump | `Koh.Emulator.App --dump` | tile decoder, tilemap/OAM formatters, IO name table | `DebugReadByte` |

Nothing above has been implemented; this is the plan.
