# Koh Emulator & Debugger Design

**Date:** 2026-04-10
**Status:** Design revised after detailed review; implementation pending.

This revision replaces the first draft in full. Every section has been rewritten to remove the contradictions, imprecisions, and hand-waving identified in review.

## Goals

Give Koh users F5-to-debug from VS Code against a first-party, cycle-accurate Game Boy / Game Boy Color emulator. The emulator is built as a reusable Blazor WebAssembly component that runs in three modes: inside a VS Code webview for debugging, as a standalone web app for playing and sharing ROMs, and via a local dev host for Koh contributors iterating on the emulator and UI.

Non-goals for this design:

- Emulating non-Game-Boy hardware.
- Automatic ROM hacking / patching tools.
- Cloud-hosted multiplayer link cable.
- Reverse execution / time-travel debugging. *(Deferred to a separate future design. It has deep implications for serialization format, memory ownership, and performance that justify independent treatment.)*

## Hardware accuracy scope

The earlier phrase "100% hardware accurate" was too broad to be actionable. This design commits to **cycle-accurate emulation to the level required by a fixed, enumerable set of test-ROM targets**, listed below. Hardware behaviors outside this list may be emulated correctly but are not gated by this design.

### In scope

- **Instruction set:** every documented SM83 opcode, plus the HALT bug, plus a narrowly-defined STOP behavior (see below).
- **STOP behavior (narrow commitment):** on CGB, if `KEY1.0` is set at the moment STOP executes, perform a speed switch and resume at the instruction after STOP. In all other cases, halt until any bit in the joypad matrix reads low. The additional hardware-bug behaviors (DIV reset, 2-byte STOP encoding quirks on certain revisions, interrupt-blocked wait states, failing speed-switch edge cases) are **out of scope** unless a gated test ROM requires them.
- **CPU timing:** correct M-cycle count per instruction, correct bus-access timing within each M-cycle, correct EI-delay-slot semantics, correct interrupt dispatch timing (5 M-cycles from trigger to first instruction of ISR). Interrupts are serviced only at instruction boundaries; the currently-executing instruction always completes first.
- **Memory bus:** correct routing across all regions ($0000–$FFFF), correct VRAM/OAM access blocking during PPU modes 2 and 3, correct behavior of the prohibited region $FEA0–$FEFF, correct echo RAM mirroring.
- **Cartridges (MBCs):** RomOnly, MBC1, MBC3 (including RTC), MBC5. Bank switching, RAM enable/disable, RTC latch and tick.
- **Timer:** DIV at the correct rate, TIMA at the correct rate per TAC, TMA reload with the documented 1-M-cycle reload delay, correct IRQ raising. In CGB double-speed mode, DIV and TIMA rates double relative to single-speed (see §7.2).
- **PPU:** the pixel-FIFO fetcher model (see §7.7). Correct mode 2/3/0/1 timing, variable mode 3 length from SCX mod 8, window enable, sprite penalties, and window restart. Correct LY / LYC / STAT interrupt sources. DMG BGP/OBP0/OBP1 palette. CGB BG/OBJ palette RAM with auto-increment.
- **DMA:** OAM DMA with correct CPU access restrictions during transfer, per §7.6. CGB HDMA general-purpose and HBlank modes.
- **CGB features:** VRAM banking, WRAM banking, double-speed mode, HDMA, CGB palettes, CGB boot.
- **Interrupts:** IF/IE/IME, HALT wake-up, HALT bug.
- **Serial:** one-byte output buffer (enough for Blargg serial reporting). Full link-cable IRQ in Phase 4.
- **APU:** four channels with correct frequencies and envelopes, WebAudio output. Phase 4.

### Out of scope (may work, but not gated)

- Mealybug Tearoom Tests edge cases beyond what our fetcher model naturally covers.
- Mooneye `misc/` tests outside the acceptance subset listed below.
- Uncommon MBCs (MBC2, MBC6, MBC7, HuC1, HuC3, MMM01, Camera, TAMA5).
- Game Boy Super accuracy quirks (SGB mode).
- APU wave-channel quirks (DMG wave-RAM-during-playback corruption, etc.) unless required by a gated test ROM.
- Undocumented CPU opcode behavior beyond the HALT bug.
- **STAT IRQ blocking "bug"** — the real-hardware behavior where a STAT source firing while another is already high does not produce a new IRQ. Not gated by any test ROM in §3. If a future gated ROM requires it, the mode-transition code path in §7.7 is the only place affected.
- **STOP opcode quirks** beyond the narrow commitment above.
- **Mid-instruction observation** (Mealybug-class tests that observe PPU/bus state partway through an M-cycle).

### Test-ROM compliance targets

The design is validated against this fixed list. If a ROM passes on this emulator, the design succeeds for its stated goals.

| Test ROM | Gates phase | Notes |
|---|---|---|
| Blargg cpu_instrs (all 11 sub-tests) | Phase 3 | Instruction semantics |
| Blargg instr_timing | Phase 3 | M-cycle counts per instruction |
| Blargg mem_timing, mem_timing-2 | Phase 3 | Bus access timing |
| Blargg halt_bug | Phase 3 | HALT edge case |
| Blargg interrupt_time | Phase 3 | IRQ dispatch timing |
| Blargg dmg_sound (all 12 sub-tests) | Phase 4 | APU |
| Mooneye acceptance/ (bits, timer, interrupts, oam_dma) | Phase 3 | CPU + timing + DMA |
| Mooneye acceptance/ppu | Phase 2 | PPU edges |
| dmg-acid2 | Phase 2 | Frame-level PPU reference |
| cgb-acid2 | Phase 2 | CGB PPU reference |

Real-game verification list (manual, not automated) for Phase 4 sign-off: Tetris, Pokémon Blue, Pokémon Gold, Super Mario Land 2, Link's Awakening DX. These are smoke tests for the compatibility targets above working together on representative ROMs.

## Accepted trade-offs

The team has explicitly accepted the following constraints. They are documented here so the rationale is not lost.

1. **Blazor WebAssembly is a core dependency of `Koh.Emulator.App`.** `Koh.Emulator.Core` itself stays BCL-only and AOT-compatible, but the application shell takes on Blazor. This relaxes the earlier "ideally BCL-only" preference in exchange for a single emulator codebase that reuses across VS Code, web, and dev host.
2. **Blazor WASM AOT publish is required for release builds.** Debug builds may use non-AOT for faster developer iteration (see §11.6). Without AOT, Mono-WASM throughput is far below what cycle-accurate tick-driven emulation needs in release.
3. **F5 cold-start is measurably slower than launching a native binary.** Loading the Blazor runtime in the VS Code webview takes ~1–2 seconds on first launch of a session. Subsequent launches reuse cached assets.
4. **Emulator bug-hunting happens through unit tests, not by attaching a debugger to a running Blazor WASM instance.** The `Koh.Emulator.Core.Tests` project is the primary development debugger.
5. **Both "debug-in-VS-Code" and "standalone" are first-class outputs of `Koh.Emulator.App`.** Neither is treated as a second-class fallback.
6. **Cycle-accurate means tick-driven at T-cycle granularity, with CPU bus events resolved at M-cycle boundaries.** See §7.4 for the precise model.

A sibling decision record at `docs/decisions/emulator-platform-decision.md` repeats the Blazor-vs-native-binary reasoning alongside the existing LSP AOT decision, for discoverability from outside the specs directory.

## High-level architecture

Three new projects, extensions to two existing ones.

**New:**

- `src/Koh.Emulator.Core/` — BCL-only class library. Pure emulator. AOT-compatible. Synchronous, thread-free, allocation-free on the hot path.
- `src/Koh.Debugger/` — class library (not an executable). DAP message handlers in C#, breakpoint management, source ↔ address mapping, stepping state machines, scope/variable providers, disassembly, stack walking. References `Koh.Emulator.Core` + `Koh.Linker.Core` for `.kdbg` parsing.
- `src/Koh.Emulator.App/` — Blazor WebAssembly project. Razor components for LCD display, debug dashboard, memory viewer, palette viewer, sprite viewer. References `Koh.Emulator.Core` + `Koh.Debugger`. Single compiled artifact, three runtime entry points.

**Extended:**

- `src/Koh.Linker.Core/` — adds `.kdbg` emission alongside existing `.gb` + `.sym`.
- `editors/vscode/` — adds debug type, inline DAP passthrough adapter, webview host for the Blazor app, build task provider. `extension.ts` is refactored into a facade over narrow subsystem modules.

### Ownership boundary: extension vs Blazor app

The review correctly pointed out that an earlier draft overstated the separation. The precise split is:

**VS Code extension owns:**
- VS Code API registration (debug type, task provider, commands, settings).
- Debug session orchestration (launch config synthesis, target selection, build task chain, session lifecycle).
- Webview creation, CSP, asset URI resolution, and disposal.
- DAP message transport reliability (§11.9).
- Build output bundling and freshness checks (§11.6).

**Blazor app (`Koh.Debugger` + `Koh.Emulator.App`) owns:**
- DAP message semantics (handlers, breakpoint resolution, stepping state machines).
- Emulator state and execution.
- Debug data model (`.kdbg` parsing, source/symbol maps).
- All UI rendering (LCD, dashboard, memory view).

## Runtime entry points of `Koh.Emulator.App`

| Mode | Launched by | Role |
|---|---|---|
| **Debug-in-VS-Code** | F5 in VS Code → extension → webview | Primary user workflow — debugging Koh ROMs |
| **Standalone static site** | Published artifact on any static host, or MAUI Blazor Hybrid desktop shell | Playing / sharing ROMs without the Koh toolchain |
| **Dev host** | `dotnet run --project Koh.Emulator.App` → localhost | Koh contributors iterating on emulator and UI |

Runtime mode is detected once at startup via a JS interop probe (am I inside a VS Code webview?) with a query-string fallback. The Razor app adapts its shell accordingly. The dev host and standalone site share the same code path; only the hosting differs. In debug mode, `Koh.Debugger` is wired to the DAP transport; in standalone mode, the debugger components are inert and the UI exposes a file picker and playback controls instead.

## Data flow on F5

```
User presses F5
    │
    ▼
VS Code → KohConfigurationProvider (§11.4)
    │   resolves config from koh.yaml or launch.json, selects a target
    ▼
preLaunchTask (koh task provider): koh-asm + koh-link → game.gb, game.sym, game.kdbg
    │
    ▼
VS Code opens debug session with inline DAP adapter (§11.3)
    │
    ▼
Extension opens Koh Emulator webview, loads bundled Blazor WASM app
    │
    ▼
Blazor detects "debug mode", initializes Koh.Debugger, loads ROM + .kdbg
    │
    ▼
Extension-side DAP queue flushes buffered messages (§11.9)
    │
    ▼
VS Code DAP requests ── postMessage ─▶ Blazor WASM ─▶ Koh.Debugger handlers
                                                           │
                                                           ├── Koh.Emulator.Core (step/run)
                                                           └── Razor components (LCD, dashboard)
```

## `Koh.Emulator.Core`

### Terminology (used throughout §7)

| Term | Definition |
|---|---|
| **T-cycle** | One tick of the 4.194304 MHz DMG CPU clock. Smallest unit of CPU timing in single-speed mode. |
| **M-cycle** | Four T-cycles. SM83 instructions consume an integer number of M-cycles; bus accesses occur at defined positions within an M-cycle. |
| **Dot** | One tick of the PPU's pixel clock. Runs at 4.194304 MHz regardless of CPU speed mode. One scanline = 456 dots. One frame = 154 × 456 = 70 224 dots. |
| **System tick** | The internal scheduling unit used by `GameBoySystem`. One system tick = one PPU dot. In single-speed mode, one system tick also advances the CPU by one T-cycle. In CGB double-speed mode, one system tick advances the CPU by **two** T-cycles. |
| **Single-speed** | CGB compatibility mode or any DMG execution. CPU runs at 4.194304 MHz; one CPU T-cycle per system tick. |
| **Double-speed** | CGB only, enabled via KEY1. CPU runs at 8.388608 MHz; two CPU T-cycles per system tick. PPU, OAM DMA byte rate, and the "dot" clock are unchanged. Timer and HDMA rates double because they are driven by the CPU clock. |

This terminology is load-bearing. The scheduling loop, save-state format, and debugger stepping semantics all assume it without redefinition.

### 7.1 Type layout

```
Koh.Emulator.Core/
├── GameBoySystem.cs              // top-level façade; owns all components
├── HardwareMode.cs               // enum: Dmg, Cgb
├── StopReason.cs
├── StopCondition.cs              // structural stop conditions (no delegates)
├── StepResult.cs
├── SystemClock.cs                // T-cycle counter, frame-budget tracking
├── Bus/
│   ├── Mmu.cs                    // sealed; memory map routing
│   └── IoRegisters.cs            // $FF00-$FF7F dispatch, sealed
├── Cpu/
│   ├── Sm83.cs                   // sealed; CPU state + M-cycle driven state machine
│   ├── CpuRegisters.cs           // A/F/B/C/D/E/H/L/SP/PC + flags (struct)
│   ├── InstructionTable.cs       // static micro-op tables, built once at startup
│   └── Interrupts.cs             // IF/IE/IME servicing
├── Ppu/
│   ├── Ppu.cs                    // sealed; dot-driven state machine + pixel FIFO
│   ├── PpuMode.cs                // enum
│   ├── PixelFifo.cs              // BG + sprite FIFO state
│   ├── Fetcher.cs                // background fetcher state machine
│   ├── Palette.cs                // DMG + CGB palette memory
│   └── Framebuffer.cs            // 160×144, RGBA8888 (see §10.4)
├── Cartridge/
│   ├── Cartridge.cs              // sealed; holds MapperKind + all mapper state
│   ├── MapperKind.cs             // enum: RomOnly, Mbc1, Mbc3, Mbc5
│   └── CartridgeFactory.cs       // parses header, constructs Cartridge
├── Timer/Timer.cs                // sealed; DIV / TIMA / TMA / TAC
├── Dma/OamDma.cs                 // sealed
├── Dma/Hdma.cs                   // sealed; CGB only
├── Joypad/Joypad.cs               // sealed; P1 register, buttons from host
├── Apu/Apu.cs                    // stubbed through Phase 3, full in Phase 4
├── Serial/Serial.cs               // buffer stub (Phase 1), full link cable (Phase 4)
├── State/
│   └── StateVersion.cs            // save-state format version constant
└── Debug/
    ├── MemoryHook.cs              // plugs into Mmu; hot-path when enabled (§7.8 rule 4)
    └── RunGuard.cs                // volatile stop-request flag
```

Phase 1 includes only `RomOnly` and `Mbc1` in `Cartridge.cs`. Other mappers are added alongside Phase 4 peripheral work. The type layout above does not imply Phase 1 shipping beyond those two.

### 7.2 Clocking model

The scheduling loop advances one system tick at a time (§7 terminology). Components tick as follows:

- **PPU** advances one dot per system tick, unconditionally.
- **CPU** advances one T-cycle per system tick in single-speed mode, or two T-cycles per system tick in double-speed.
- **Timer** is driven by the **CPU T-cycle clock**. It advances at the same rate as the CPU. Concretely, the timer's internal 16-bit system counter increments by 1 per CPU T-cycle; `DIV` is bits 9–15 of that counter (equivalent to incrementing at 16 384 Hz in single-speed); `TIMA` increments on a falling edge of a TAC-selected bit (bit 9 / 3 / 5 / 7 of the same counter for TAC 00/01/10/11). In double-speed, the counter advances at the doubled CPU rate; DIV and TIMA both double their observable frequencies as a direct consequence. The 1-M-cycle TMA reload delay is measured in CPU M-cycles and therefore halves in wall-clock time during double-speed.
- **OAM DMA** byte-transfer rate is driven by the **CPU M-cycle clock**: one byte per CPU M-cycle. In single-speed, that is one byte per 4 system ticks; in double-speed, one byte per 2 system ticks. OAM DMA total wall-clock duration therefore halves in double-speed.
- **HDMA** is driven by the CPU M-cycle clock and so doubles in double-speed, matching hardware.

**The invariant is:** only the CPU T-cycle clock changes frequency across single-/double-speed. The PPU dot rate is constant at 4.194304 MHz. Every component is defined relative to one of the two clocks — CPU-clock (Timer, DMAs) or dot-clock (PPU). There is no third "dot-but-doubled" rate.

In code:

```csharp
public void StepOneSystemTick()          // one PPU dot
{
    ppu.TickDot();

    int cpuTs = clock.DoubleSpeed ? 2 : 1;
    for (int i = 0; i < cpuTs; i++)
    {
        cpu.TickT();
        timer.TickT();
        oamDma.TickT();       // increments its T-cycle counter; emits bytes at M-cycle boundaries
        if (hdmaActive) hdma.TickT();
    }
}
```

A frame is **70 224 system ticks** (154 scanlines × 456 dots), regardless of speed mode. `GameBoySystem.RunFrame()` loops until 70 224 system ticks have elapsed or a stop condition fires.

### 7.3 Tick model

Components are strictly passive. Each component exposes `TickT()` (or `TickDot()` for the PPU) and does not call any other component directly. Interaction happens through the `Mmu` bus, which stalls the CPU's memory accesses if the OAM DMA is active and the CPU is attempting to read outside HRAM.

### 7.4 CPU instruction model

A **micro-op** is one **M-cycle (4 T-cycles)**. Each SM83 instruction is a pre-built list of micro-ops stored in a static table in `InstructionTable.cs`. A micro-op describes:

- What the bus does during the M-cycle (`Fetch`, `Read`, `Write`, `Internal`).
- Which address line to drive (if any).
- What ALU / register operation happens at the end of the M-cycle.

`Cpu.TickT()` maintains:

```csharp
private byte tInMCycle;           // 0..3
private byte currentMicroOp;      // index into the current instruction's micro-op list
private Instruction current;
```

Bus actions happen at a specific T-cycle position within each M-cycle (the hardware places them at T3→T4 for reads, T4 for writes). For the purposes of cycle accuracy required by our test-ROM targets, the precise intra-M-cycle timing is modeled as: **bus access at the rising edge of T4, register commit at T4.** Mid-M-cycle observation (certain Mealybug tests) is out of scope per §3 — we target M-cycle-accurate bus events, not finer.

When a micro-op performs a memory access, the CPU calls `Mmu.ReadByte(address)` or `Mmu.WriteByte(address, value)` directly. If the OAM DMA is currently active (see §7.6) and the address is outside HRAM, reads return $FF and writes are dropped — this behavior lives in `Mmu` itself, which consults the `OamDma.IsBusLocking` flag. The CPU does not stall; the bus "contention" is a projection of what value the CPU observes, not a pipeline freeze. This matches hardware behavior.

**Interrupt servicing is an instruction-boundary operation.** The CPU checks `(IF & IE & 0x1F) != 0 && IME` only at the moment it would fetch the next opcode. If the check succeeds, the next instruction fetched is replaced with a 5 M-cycle interrupt-dispatch micro-op sequence (2 internal, 2 stack writes, 1 PC reload). **An interrupt cannot preempt an instruction partway through its micro-ops, and a new interrupt raised during the 5-cycle dispatch cannot preempt the dispatch itself.** This matches real SM83 behavior for the targets in §3.

The HALT state is an instruction-like state that advances zero micro-ops per CPU T-cycle and re-runs the interrupt-dispatch check on every T-cycle; wake-up transitions out of HALT and into either the interrupt dispatch sequence (IME=1 + pending IRQ) or the next instruction (IME=0 + pending IRQ, with the HALT bug causing the next opcode byte to be fetched twice).

### 7.5 Memory bus and MBC dispatch

The bus path is unconditionally hot. The design uses **no interface calls on bus accesses**.

`Cartridge` is a sealed concrete class holding all state for all supported mappers plus a `MapperKind` enum. `ReadRom`, `ReadRam`, `WriteRom` (which mapper registers interpret as bank select), and `WriteRam` are sealed non-virtual methods that dispatch on the enum via a switch.

**Performance claim is a hypothesis, not a guarantee.** The expected native .NET behavior (RyuJIT devirtualizes the switch into a branch table or inlined dispatch, predicted by the branch predictor because MapperKind is stable per run) may not hold identically under Blazor WASM AOT. The Phase 1 benchmark (§12.9) validates the cost empirically. Fallback options if the switch is too expensive in WASM AOT:

1. Cache two function-pointer fields (`readRomFn`, `writeRomFn`) on `Cartridge`, set once at construction, invoked directly. `delegate*` in C# 11+ is AOT-safe.
2. Build separate sealed `Cartridge_RomOnly`, `Cartridge_Mbc1`, etc. types and construct the concrete type at ROM-load time, hoisting the mapper dispatch out of the hot path entirely at the cost of generic-over-cartridge code in `Mmu`.

We do not commit to either fallback now; we commit to measuring and switching strategies if the baseline implementation fails the benchmark.

```csharp
public sealed class Cartridge
{
    public MapperKind Kind;
    // state for all mappers
    public int RomBankLow, RomBankHigh, RamBank, BankingMode;
    public bool RamEnabled;
    public Rtc Rtc;

    public byte ReadRom(ushort addr)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly: return rom[addr];
            case MapperKind.Mbc1:    return Mbc1.ReadRom(this, addr);
            case MapperKind.Mbc3:    return Mbc3.ReadRom(this, addr);
            case MapperKind.Mbc5:    return Mbc5.ReadRom(this, addr);
            default: return 0xFF;
        }
    }
    // similarly ReadRam, WriteRom, WriteRam
}
```

Per-mapper logic lives in `static class Mbc1`, `Mbc3`, `Mbc5`, operating on `Cartridge` by reference. This removes the interface; adds one branch per access. Benchmarks in Phase 1 verify the branch is free.

`Mmu` has concrete, sealed routing code calling `Cartridge`, `Ppu`, `Timer`, `IoRegisters`, etc. directly. No `IMemoryBus` exists.

### 7.6 DMA precise timing

**OAM DMA** timing is defined in exact T-cycle windows relative to the $FF46 write.

Let `T_write` be the T-cycle at which the CPU finishes writing $FF46 (the last T-cycle of the write micro-op). Define byte indices `k = 0..159`.

- **Source address of byte k:** `(value << 8) | k`. Legal value range: $00–$F1 (hardware masks above $DF on DMG and above $F1 on CGB; we use $F1 for both for simplicity — this matches the targeted test ROMs).
- **Destination of byte k:** `$FE00 + k`.
- **Byte k is transferred at T-cycle** `T_write + 4 + 4·k`. (4 T-cycles of start delay, then one byte per M-cycle.)
- **DMA finishes at T-cycle** `T_write + 4 + 4·160 = T_write + 644`.
- **Bus-contention window** (inclusive start, exclusive end):
  - `[T_write + 4, T_write + 4 + 640)` = `[T_write + 4, T_write + 644)`
  - During this window, CPU reads from any address **not** in $FF80–$FFFE return $FF.
  - During this window, CPU writes to any address **not** in $FF80–$FFFE are dropped.
  - The CPU is not halted. CPU instructions execute at their normal T-cycle rate. Code executing from HRAM is unaffected.
  - IRQs are serviced normally if IME=1.
  - $FF46 reads return the last value written during the window.
- **The 4 T-cycles of start delay** (`[T_write, T_write + 4)`) are **not** part of the contention window. The CPU can access any address during this interval.
- **The last transferred byte is written at `T_write + 636` (byte 159). The contention window ends at `T_write + 644`, i.e., 4 T-cycles after the last transfer.** This 4-T-cycle tail is part of the contention window.
- OAM is written by the DMA engine, not the CPU. PPU OAM scan during mode 2 reads OAM as modified by DMA so far.

**HDMA (CGB only).** Source / destination / length registers as described in earlier revisions. Transfer timing:

- **General purpose (HDMA1–4 write, $FF55 bit 7 = 0):**
  - CPU is halted (no instructions execute) from the T-cycle following the $FF55 write until the transfer completes.
  - Transfer rate: 2 bytes per CPU M-cycle (= 16 bytes per 8 CPU M-cycles).
  - In single-speed: 8 CPU M-cycles per 16 bytes = 32 T-cycles = 32 system ticks.
  - In double-speed: 8 CPU M-cycles per 16 bytes = 32 T-cycles, but only 16 system ticks because each system tick now carries 2 CPU T-cycles. Wall-clock duration halves.
- **HBlank ($FF55 bit 7 = 1):**
  - Transfer is gated on PPU mode 0 (HBlank). Exactly 16 bytes are transferred during each HBlank, then the CPU resumes.
  - During the 16-byte transfer, the CPU is halted for 8 CPU M-cycles per HBlank.
  - Writing $00 to $FF55 while HBlank HDMA is active cancels it; reading $FF55 afterward returns the remaining block count with bit 7 set.

**Phase gating.** The algorithms above are the design target. Implementation of OAM DMA arrives in Phase 2 (not Phase 1 — §7.12 updated). HDMA arrives in Phase 2. No compatibility claims depending on either are made before the Phase 2 CGB tests pass.

**HDMA (CGB only)**:

- Source in $FF51/$FF52 ($0000–$DFF0, 16-byte aligned).
- Destination in $FF53/$FF54 ($8000–$9FF0, 16-byte aligned, VRAM).
- Length register $FF55 bits 0–6 = (length/16 − 1). Bit 7 selects mode: 0 = general-purpose, 1 = HBlank.

General-purpose HDMA:
- CPU is **halted** during the transfer (unlike OAM DMA).
- 8 M-cycles per 16 bytes in single-speed; 16 M-cycles in double-speed.
- LY, timers, and PPU continue to run during the halt.

HBlank HDMA:
- 16 bytes are transferred during each HBlank (PPU mode 0).
- CPU is halted for those 8 / 16 M-cycles per HBlank only.
- CPU runs normally during active scanline drawing.
- Writing bit 7 = 0 to $FF55 while an HBlank HDMA is active cancels it; the length register reads as the remaining blocks with bit 7 set.

### 7.7 PPU pixel-FIFO model

The PPU is modeled with the fetcher + FIFO design used by SameBoy and Mooneye-targeted emulators.

The PPU is **algorithmic**, not formula-driven. Mode 3 length is an emergent property of the fetcher state machine, not a lookup. Summary formulas exist only as a sanity check against implementation.

**Scanline structure** (456 dots per scanline):

- **Mode 2 (OAM scan):** fixed 80 dots. The PPU scans OAM for the first 10 sprites whose Y-range includes the current scanline. VRAM accessible by CPU; OAM not accessible by CPU.
- **Mode 3 (Drawing):** variable 172–289 dots, ending when the fetcher has pushed 160 visible pixels to the LCD. Neither VRAM nor OAM accessible by CPU.
- **Mode 0 (HBlank):** the remainder of 456 dots for that scanline. Both VRAM and OAM accessible.
- **Mode 1 (VBlank):** scanlines 144–153, 456 dots each. Both VRAM and OAM accessible.

**Mode 3 algorithmic behavior.** Rather than computing length from a formula, the PPU runs a dot-driven state machine that consumes variable dots per pixel. The penalties the review asked for are emergent from:

1. **SCX mod 8 initial discard.** At the start of mode 3, the fetcher pushes a full 8-pixel tile into the BG FIFO, then discards the first `SCX mod 8` pixels before LCD output begins. Those discarded pixels still cost dots (1 dot each). This produces the 0–7 dot penalty.
2. **Background fetcher pipeline** (8 dots per full BG tile, retrying push until the FIFO has room):
   1. **Get Tile** (2 dots) — reads BG tile map.
   2. **Get Tile Data Low** (2 dots) — reads low byte of tile row.
   3. **Get Tile Data High** (2 dots) — reads high byte of tile row.
   4. **Push** (2 dots, looped) — pushes 8 pixels to the BG FIFO; stalls as long as the FIFO already holds ≥ 8 pixels.
3. **Sprite encounter.** When the next LCD pixel position matches a sprite's X coordinate, LCD output stalls and the sprite fetcher runs:
   1. The BG fetcher is paused **at its current step**.
   2. A 6-dot base penalty is incurred. However, if the BG fetcher is currently in steps 1/2/3 (not Push), the first `6 - current_bg_step_progress` of those dots are spent finishing the BG fetch rather than extra; the visible penalty is `max(0, 6 - already_consumed_dots_in_current_bg_tile)`. This is why the summary formula says "up to 6 dots."
   3. An additional penalty of `(5 - ((sprite.X - 1) mod 8)) + 2` dots applies when the sprite's X mod 8 requires pixel alignment work. Practical range: 0–5 extra dots.
   4. After the sprite fetcher pushes pixels into the sprite FIFO (parallel to the BG FIFO), LCD output resumes and the BG fetcher continues where it left off.
4. **Window activation.** When `LCDC.5 = 1` and `WY ≤ LY` and `WX ≤ current_lcd_x + 7`, the window triggers on the next dot:
   1. The BG FIFO is cleared.
   2. The BG fetcher is reset to step 1, but now indexes the window tile map (LCDC.6).
   3. 6 dots elapse before any pixel is pushed to the LCD after the trigger. This is the "window penalty."
5. **Per-pixel priority resolution** between BG FIFO and sprite FIFO uses sprite priority bit + BG-over-OBJ bit (+ CGB master priority LCDC.0).

The implementation in `Fetcher.cs` + `PixelFifo.cs` directly encodes steps 2–5 as a state machine. Mode 3 length is not computed; it is the number of dots elapsed when the 160th LCD pixel is emitted. The variable-length 172–289 dot range is an observation, not an input.

**CGB specifics:**

- BG attribute map (VRAM bank 1) adds priority, palette index, tile VRAM bank, H/V flip.
- Sprite attributes gain CGB palette index (bits 0–2) and VRAM bank bit.
- LCDC.0 meaning changes: on CGB, LCDC.0 = 0 forces "sprite over BG" globally (master priority override).
- Palette RAM ($FF68–$FF6B for BG, $FF6A–$FF6B for OBJ) with auto-increment on write.

**STAT IRQ sources:** LY=LYC, mode 0 (HBlank), mode 1 (VBlank), mode 2 (OAM scan). The STAT IRQ line is the OR of all enabled sources; an edge (low→high) on the ORed line raises `IF.1`. The "STAT blocking bug" — where a new source firing while the line is already high does not produce an IRQ — is **out of scope** per §3 unless a gated test ROM requires it.

### 7.8 Performance rules (hot path)

All rules derive from the Blazor WASM AOT constraint: virtual dispatch, interface calls, allocations, LINQ, delegates, and boxing on the tick path are significantly more expensive than in native .NET. Therefore:

1. All hot-path types are `sealed`. `GameBoySystem.StepOneSystemTick()` calls concrete `Cpu`, `Ppu`, `Timer`, etc. directly — no polymorphic component base class.
2. No interfaces on hot paths. `Mmu` is concrete. `Cartridge` is concrete with enum-dispatched mapper logic (§7.5). `ICartridge` does not exist.
3. No allocations per tick. Micro-op tables are pre-built once at construction. Instruction state uses indices into static arrays, not object references.
4. No LINQ, no delegates, no closures per tick. `MemoryHook` is an explicit nullable object reference checked once per memory access; when null (the default), the check is one predictable branch. **When set, every memory access incurs the null check plus a virtual call into the hook; this is hot-path work. Enabling watchpoints or trace logging materially slows execution — expect 20–40% throughput loss for trace logging, 5–15% for watchpoints over a small address set. This cost is accepted; users opting into these features accept it.**
5. Ephemeral state as `struct` (`StepResult`, `JoypadState`, `CpuSnapshot`) — passed by `in` or `ref` where reasonable.
6. No threading in the core. Frame loop, pacing, and cancellation live in `Koh.Emulator.App`. The core is synchronous.
7. No `CancellationToken` in the core API. The host uses `RunGuard.RequestStop()` (§7.9) to signal stop; the core checks the flag at instruction boundaries inside `RunFrame` and `RunUntil`.
8. Save-state is cold-path, orchestrated centrally by `GameBoySystem` with no interface indirection (§7.11).

### 7.9 Public API

```csharp
public sealed class GameBoySystem
{
    public GameBoySystem(HardwareMode mode, Cartridge cart);

    public SystemClock Clock { get; }
    public CpuRegisters Registers { get; }
    public Framebuffer Framebuffer { get; }
    public JoypadState Joypad { get; set; }

    // Execution
    public StepResult RunFrame();
    public StepResult StepInstruction();
    public StepResult StepTCycle();
    public StepResult RunUntil(in StopCondition condition);
    public void RequestStop();       // sets RunGuard flag, checked at next instruction boundary

    // Debug peek/poke — contract in §7.10
    public byte DebugReadByte(ushort address);
    public bool DebugWriteByte(ushort address, byte value);

    // Save state — cold path, §7.11
    public void WriteState(Stream output);
    public void ReadState(Stream input);

    // Hooks
    public MemoryHook? MemoryHook { get; set; }
}

public readonly struct StopCondition
{
    public StopConditionKind Kind { get; init; }
    public ushort PcEquals { get; init; }
    public ushort PcRangeStart { get; init; }
    public ushort PcRangeEnd { get; init; }
    public ulong MaxTCycles { get; init; }
    public byte BankFilter { get; init; }         // 0xFF means "any bank"
}

[Flags]
public enum StopConditionKind : uint
{
    None              = 0,
    PcEquals          = 1 << 0,
    PcInRange         = 1 << 1,   // stop when PC ∈ [Start, End)
    PcLeavesRange     = 1 << 2,   // stop when PC ∉ [Start, End) — used by statement step
    MaxCycles         = 1 << 3,
    VBlank            = 1 << 4,
    Return            = 1 << 5,   // stop when SP returns above a recorded threshold (step-out)
}

public readonly record struct StepResult(
    StopReason Reason,
    ulong TCyclesRan,
    ushort FinalPc);
```

`StopCondition` is a struct passed by `in`; no delegate invocation on the hot path. `RunUntil` checks the condition at each **instruction boundary** (not per-tick), so the overhead is amortized over ~8–16 T-cycles.

**Breakpoint and watchpoint checks are also instruction-boundary operations inside the core.** `RunFrame` and `RunUntil` internally consult `BreakpointManager` at every instruction boundary and return early with `StopReason.Breakpoint` or `StopReason.Watchpoint` if a match is found. This is not the responsibility of any outer loop; callers rely on `RunFrame`'s `StopReason` to detect break conditions.

**`RequestStop` latency semantics.** The host calls `RequestStop()` to interrupt a long-running `RunFrame` or `RunUntil`. The flag is checked at the same instruction-boundary points as stop conditions and breakpoints. Worst-case latency is the longest SM83 instruction, which is 24 T-cycles (about 6 µs in single-speed, 3 µs in double-speed). `StepInstruction` and `StepTCycle` return immediately after their defined unit of work and are unaffected.

`StepTCycle` does **not** check `RunGuard` internally — it always advances exactly one T-cycle and returns. The host is responsible for not calling it in a tight loop that races with `RequestStop`.

### 7.10 Debug peek/poke contract

`DebugReadByte` and `DebugWriteByte` are **debugger projections**, not raw MMU access. They return what a debugger consumer expects to see, which may differ from what the CPU would observe. The contract is explicit.

**`DebugReadByte(addr)`** — legal at any time (running or paused). Returns a snapshot value:

- **Memory regions (ROM/VRAM/WRAM/SRAM/OAM/HRAM):** returns the current byte from the backing buffer, ignoring PPU mode-3 VRAM/OAM lockout. Uses the currently selected banks.
- **I/O registers ($FF00–$FF7F):** returns the same computed value the CPU would read if it performed the access right now. No GB I/O register has a read side effect, so the projection is trivially identical to the CPU-visible value. The joypad register $FF00 returns the matrix read for the currently selected nibble.
- **Prohibited region ($FEA0–$FEFF):** returns the documented hardware response ($00 on DMG, $FF on CGB in mode 0/1, nibble duplication in mode 2/3).
- **No side effects, no cycle cost.**
- **Temporal consistency:** if called while `RunFrame` / `StepInstruction` / `RunUntil` is executing, the value reflects state at some point during that execution window — typically *after* the current system tick completes. Multiple reads issued during a single running-execution call return values from successive internal states; they are not sampled from a single atomic snapshot. This is not a thread race (Blazor WASM is single-threaded); it is temporal drift. Debugger UI expecting consistent cross-address state must pause first.

**`DebugWriteByte(addr, value)`** — legal **only when the emulator is paused** (outside any `Run*`/`Step*` call). Returns `false` and performs no write otherwise. When legal, the write is classified by target region:

- **Work RAM / HRAM / external RAM / OAM / VRAM:** direct write into the backing buffer. Mode-3 lockout is bypassed.
- **ROM regions ($0000–$7FFF):** direct write into the in-memory ROM image. **Live patch, not persistent.**
  - Live patches survive across `StepInstruction`, `RunFrame`, and `RunUntil` within the same session.
  - Live patches are **captured** into save-state files: a save-state taken after a patch contains the patched bytes.
  - Live patches are **not** written back to the `.gb` file on disk. Reloading the ROM (debugger restart, session restart) reverts to the on-disk ROM.
  - Live patches **do not** affect the MBC banking state — writing $3000 with value $05 modifies the byte at $3000, it does **not** select ROM bank 5.
- **I/O registers ($FF00–$FF7F):** classified by the I/O register table below.
- **Unmapped / prohibited:** write is silently dropped.

Debug reads and writes never fire `MemoryHook` callbacks.

**I/O register debug-write classification.** I/O registers are not uniform storage; they fall into four categories that determine debug-write behavior:

| Category | Examples | Debug-write behavior |
|---|---|---|
| Storage | BGP ($FF47), SCX/SCY ($FF43/$42), NR50/NR51 volumes | Writes update the register's backing byte; the next CPU read observes the new value. |
| Composed | P1 ($FF00), STAT ($FF41), LCDC ($FF40), KEY1 ($FF4D) | Only the user-writable bit mask is updated; hardware-controlled bits (STAT mode/LY=LYC status, KEY1 current-speed flag) are preserved. |
| Triggered | DMA ($FF46), HDMA5 ($FF55), boot-ROM disable ($FF50), APU length/trigger bits | Writes update the register backing byte but **do not trigger the side effect**. A separate "Execute I/O Write" debugger action (Phase 4) performs a write with side effects if needed. |
| Read-only / latched | LY ($FF44), joypad matrix inputs | Writes rejected silently. |
| Palette auto-increment | BGPD/OBPD ($FF69/$FF6B) | Writes update the indexed byte but do **not** advance the index — debugger writes intentionally do not mutate auto-increment state. |

The exact classification of every $FF00–$FF7F register is tabulated in `IoRegisters.cs` and matches this table.

### 7.11 Save-state contract

**Save-state implementation is deferred to Phase 4. The design constraint exists now so component authors do not make choices that would prevent future serialization.**

**Design constraint (applies from Phase 1):** every component's internal state must be enumerable and reconstructable. Concretely:

- All component fields are either primitive types, fixed-size arrays of primitives, or other components that follow this constraint.
- No component holds a reference to a non-serializable object (delegates, interned strings, objects from outside `Koh.Emulator.Core`).
- Any "latched" or "delayed" timing value — TMA reload countdown, EI-delay latch, interrupt-dispatch mid-cycle, PPU mid-mode transitions — lives as an explicit field, not as an implicit position in code.

**Serialization orchestration (Phase 4 onward):** `GameBoySystem` owns serialization centrally. It exposes:

```csharp
public void WriteState(Stream output);
public void ReadState(Stream input);
```

Internally, `WriteState` calls concrete `CpuState.WriteTo(writer)`, `PpuState.WriteTo(writer)`, etc. There is **no `IStatefulComponent` interface** — the dispatch is direct, sealed, non-virtual calls from `GameBoySystem`. Component authors implement `WriteTo(BinaryWriter)` / `ReadFrom(BinaryReader)` as sealed methods without any interface contract.

The serialized state captures:

- CPU: registers, IME, EI-delay latch, HALT state, current-instruction pointer + M-cycle + T-cycle-within-M-cycle.
- PPU: LY, dot within scanline, mode, fetcher state (step + target address + partial data), BG FIFO, sprite FIFO, current scanline sprite list, internal STAT line state, window line counter.
- Timer: full 16-bit system counter, TIMA, TMA, TAC, TMA-reload-delay counter.
- DMAs: active flag, source/dest high bytes, byte index, HDMA mode, HBlank-pending flag, cancel-pending flag.
- Cartridge: all mapper-state fields including RTC latched time and RTC halt bit.
- All memory arrays (WRAM, VRAM, OAM, HRAM, SRAM, **current ROM image including any live patches per §7.10**).
- Joypad internal state (selected nibble, matrix).

State files carry a format version (`StateVersion`) and a SHA-256 of the **original, unpatched** ROM for compatibility checking. Loading a state with a mismatched base-ROM hash is an error; live patches applied to that state are reapplied on load.

**Determinism requirement:** given two `GameBoySystem` instances constructed from the same state and supplied the same `Joypad` inputs at the same system-tick offsets, their subsequent execution is byte-identical.

### 7.12 Subsystem phasing

Explicit per-subsystem phase map so none of this is ambiguous:

| Subsystem | Phase 1 | Phase 2 | Phase 3 | Phase 4 |
|---|---|---|---|---|
| CPU | Skeleton + representative mock instruction (§12.9) | + opcode subset empirically required by acid2 ROMs | Full SM83 | — |
| PPU | Blank framebuffer, LY/dot counter only | Full pixel-FIFO fetcher + all modes + CGB palettes | — | — |
| MMU | Routing for all regions, RomOnly/Mbc1, no PPU lockout, no DMA contention | + VRAM/OAM lockout during modes 2/3, + OAM DMA contention window | Full (including debug read/write projection per §7.10) | — |
| Timer | **Full** (DIV/TIMA/TMA/TAC, IRQ, 1-M-cycle TMA reload delay) | — | — | — |
| OAM DMA | **Absent** | **Full** (transfer + contention window + PPU OAM-scan interaction) | — | — |
| HDMA | **Absent** | CGB: full (general + HBlank) | — | — |
| Cartridge | RomOnly, Mbc1 | — | — | Mbc3 (with RTC), Mbc5 |
| Joypad | Input buffer, no IRQ | — | + Joypad IRQ on 1→0 edge | — |
| APU | Absent | Absent | Silent stubs that count cycles (for TIMA and future APU triggers) | Full four channels + mixing + WebAudio |
| Serial | 1-byte output buffer (for Blargg) | — | — | Full link-cable with Serial IRQ |
| Interrupts | IF/IE/IME latches + VBlank dispatch + dispatch timing | + STAT dispatch | + Timer + Joypad dispatch | + Serial dispatch |

Phase gating: any claim in the compatibility-targets table in §3 depending on a subsystem is only valid once that subsystem's row reaches its target phase. HDMA-dependent CGB tests cannot run until Phase 2. OAM-DMA-dependent CPU tests (Blargg oam_bug, mooneye oam_dma) cannot run until Phase 2. Timer-dependent tests can run from Phase 1 in principle but are gated behind CPU-instruction availability in Phase 3.

## `Koh.Debugger`

### 8.1 File layout

```
Koh.Debugger/
├── DebugSession.cs              // top-level; owns GameBoySystem + debug state
├── Dap/
│   ├── DapDispatcher.cs         // parses request JSON, routes to handlers
│   ├── DapJson.cs               // JsonSerializerContext source-gen
│   ├── Capabilities.cs          // phase-aware capability advertising
│   ├── Messages/                // request/response/event records
│   └── Handlers/                // one per DAP request, see §8.7
├── Session/
│   ├── BreakpointManager.cs     // banked-address breakpoints (§8.3)
│   ├── DebugInfoLoader.cs       // .kdbg → in-memory maps
│   ├── SourceMap.cs             // (file,line) ↔ banked addresses
│   ├── SymbolMap.cs             // banked address ↔ symbol with scope
│   ├── CallStackWalker.cs       // heuristic SP walk → CallFrame[]
│   ├── ExpansionStackResolver.cs // PC → macro expansion chain
│   └── ExecutionLoop.cs         // cooperative run loop
├── Stepping/
│   ├── StepStrategy.cs          // step-over / in / out algorithms
│   └── SteppingGranularity.cs   // statement / instruction
└── Events/
    └── CustomDapEvents.cs       // koh.framebufferReady, koh.cpuState, koh.tCycleStep
```

### 8.2 Transport abstraction

```csharp
public sealed class DapDispatcher
{
    public void HandleRequest(ReadOnlySpan<byte> jsonBytes);
    public event Action<ReadOnlyMemory<byte>>? ResponseReady;
    public event Action<ReadOnlyMemory<byte>>? EventReady;
}
```

Blazor wires `HandleRequest` to JS interop incoming messages and `ResponseReady` / `EventReady` to outgoing interop calls. The dispatcher only cares about byte buffers. Unit tests feed raw JSON and assert the emitted responses.

### 8.3 Banked-address breakpoints

Breakpoints on the Game Boy must be `(bank, address)` pairs because the ROM is banked. The in-memory representation is:

```csharp
public readonly record struct BankedAddress(byte Bank, ushort Address)
{
    public uint Packed => ((uint)Bank << 16) | Address;
}

public sealed class BreakpointManager
{
    private readonly HashSet<uint> executionBreakpoints = new();

    public bool Check(byte currentBank, ushort pc)
    {
        if (executionBreakpoints.Count == 0) return false;
        uint packed = ((uint)currentBank << 16) | pc;
        return executionBreakpoints.Contains(packed);
    }
}
```

**Bank context per address range:**

- $0000–$3FFF: always bank 0.
- $4000–$7FFF: current ROM bank (tracked by `Cartridge` state).
- $8000–$9FFF: VRAM bank (0 on DMG, 0 or 1 on CGB).
- $A000–$BFFF: current external RAM bank.
- $C000–$DFFF: WRAM bank (0 for $C000–$CFFF; 0 or 1–7 for $D000–$DFFF on CGB).
- $E000–$FFFF: unbanked.

Execution breakpoints use **ROM bank context**. Data breakpoints (Phase 4) use the appropriate region's bank. "Bank 0xFF" is the sentinel for "any bank" (used when the user intentionally wants a breakpoint that fires regardless of bank — rare, but supported).

Per-instruction breakpoint check cost: one `HashSet<uint>` lookup, skipped entirely if the set is empty.

### 8.4 Source mapping for banked ROM

`SourceMap`'s forward direction is `(file, line) → List<BankedAddress>`. Multiple addresses per line are expected (macro used in many places, or the same line copied into multiple overlays).

`AddressMap`'s reverse direction is `BankedAddress → AddressMapEntry`. A lookup uses `(current ROM bank, PC)`. If no exact match, the debugger optionally falls back to the nearest preceding entry within the same bank for disassembly context.

**Path canonicalization.** `.kdbg` stores **workspace-relative paths with forward slashes** by default (§9.3). The debugger resolves these against the currently open VS Code workspace root to produce absolute paths, then normalizes them before comparing against the paths VS Code provides in `setBreakpoints`. Normalization steps:

1. Replace backslashes with forward slashes.
2. Collapse `./` and resolve `../` components.
3. On Windows and macOS (case-insensitive filesystems), lowercase the path for comparison purposes; the original case is preserved for display.
4. Resolve symbolic links only on request, not eagerly (avoids latency and surprising behavior).

Cross-machine reproducibility: a `.kdbg` emitted on a build server with workspace root `/builds/koh/project` is consumable on a developer workstation with workspace root `C:\projekty\koh\project` as long as the workspace-relative structure is identical. Absolute paths (flag bit 2 = 1) inhibit cross-machine use and are emitted only for source files that fall outside the workspace root.

### 8.5 Call stack vs expansion stack

These are two distinct concepts that the previous draft conflated. They are separated now.

**Call stack** = the runtime call chain at the current PC, computed by walking the real SP and heuristically recognizing return addresses. It is presented via DAP `stackTrace`. Frames correspond to `CALL` sites.

**Expansion stack** = the source-level macro nesting at the current PC, encoded in `.kdbg` per AddressMapEntry. It is **not** presented as DAP stack frames. It is presented as a dedicated variable group under a **Source Context** scope in the `variables` panel, showing:

```
Source Context
  ├── File: tile_routines.asm:142
  ├── Expanded from: draw_sprite macro (sprite.asm:30)
  ├── Expanded from: frame_loop macro (main.asm:88)
  └── Macro arguments: (see child nodes)
```

An optional Phase 4 feature is "expand call frames into macro frames" where each call frame that lives inside a macro expansion shows its expansion chain as children. This is a presentation detail and does not change the underlying distinction.

### 8.6 Execution loop

Blazor WASM is single-threaded, so the run loop is cooperative:

```csharp
public async Task ContinueAsync()
{
    while (!session.PauseRequested)
    {
        var result = emulator.RunFrame();   // returns early on Breakpoint / Watchpoint (§7.9)
        framebufferChannel.Publish(emulator.Framebuffer);

        if (result.Reason == StopReason.Breakpoint ||
            result.Reason == StopReason.Watchpoint)
        {
            session.Stop(result.Reason);
            break;
        }

        await framePacer.WaitForNextFrameAsync();
        await Task.Yield(); // let DAP pause requests process
    }
}
```

**Breakpoint responsiveness is not the loop's responsibility.** Per §7.9, `RunFrame` itself checks breakpoints and watchpoints at instruction boundaries and returns early with the appropriate `StopReason`. The outer loop only:

1. Publishes framebuffers between frames.
2. Observes the `StopResult` and terminates on break.
3. Throttles to the frame pacer.
4. Yields to let DAP `pause` messages reach their handler.

A long-running `RunUntil` (e.g., "continue until PC = 0x1234") sees the same breakpoint checking inside the core. The pause button works because the pause handler calls `emulator.RequestStop()`, which the core's instruction-boundary check (§7.9) observes within one instruction.

### 8.7 DAP capabilities by phase

The capability set advertised in `initialize` is phase-dependent. A ✓ in a phase column means **the capability is introduced in that phase and is present in all subsequent phases.** Later-phase columns do not repeat ✓; missing cells mean "not yet" only in phases before the introduction cell. The `Capabilities` class switches on the current build phase.

| Capability / Request | Introduced in |
|---|---|
| `initialize` / `launch` / `configurationDone` | P1 |
| `continue` / `pause` / `terminate` | P1 |
| `setBreakpoints` (verified locations returned, execution does not yet halt) | P1 |
| `scopes` / `variables` (Registers, Hardware scopes) | P1 |
| `exceptionInfo` (for emulator runtime errors) | P1 |
| `readMemory` | P2 |
| `setBreakpoints` (halts execution on hit) | P3 |
| `setInstructionBreakpoints` | P3 |
| `setFunctionBreakpoints` (symbol-based) | P3 |
| `next` / `stepIn` / `stepOut` | P3 |
| Stepping granularity `statement` (source line with entries in `.kdbg`, §8.8) | P3 |
| Stepping granularity `instruction` (single SM83 instruction) | P3 |
| `stackTrace` (heuristic call stack) | P3 |
| `scopes` / `variables` (Symbols scope) | P3 |
| `scopes` / `variables` (Source Context scope, §8.5) | P3 |
| `disassemble` | P3 |
| `evaluate` (literals + symbol lookup) | P3 |
| `breakpointLocations` | P3 |
| `writeMemory` | P4 |
| Conditional breakpoints | P4 |
| Hit-count breakpoints | P4 |
| Data breakpoints (watchpoints via `MemoryHook`) | P4 |

Capabilities not yet introduced are explicitly *not* advertised in `initialize`. The extension reads advertised capabilities from the debugger's `initialize` response and gates UI affordances accordingly.

### 8.8 T-cycle stepping mechanism

T-cycle stepping is **not a DAP-visible feature.** DAP's `stepping granularity` is designed around source-language concepts (statement, line, instruction) and VS Code does not render a "T-cycle" option. Overloading `granularity: instruction` to mean sub-instruction would mislead VS Code's UI.

The mechanism:

1. The `Koh.Emulator.App` webview renders a dedicated "Step T-cycle" button in its debug toolbar (visible only in debug mode).
2. Pressing the button calls `EmulatorHost.StepTCycle()` directly as an in-process C# call inside the Blazor app. No DAP message is sent.
3. `EmulatorHost.StepTCycle()` calls `emulator.StepTCycle()` (§7.9), captures the resulting state, and publishes a `koh.cpuState` event to the `CpuDashboard` component for UI update.
4. After the step, the emulator remains paused. A subsequent `continue` / `next` DAP request from VS Code resumes normal debugging.
5. VS Code's own Variables and Registers panels refresh on the next time VS Code polls them (because the debug session is still in the `stopped` state from its perspective). The emulator's internal pause flag stays asserted.

Because T-cycle stepping changes state without VS Code's knowledge, the webview explicitly sends a DAP `invalidated` event (`areas: ["variables", "stacks"]`) after each T-cycle step so VS Code refetches Variables and stack trace. This is a standard DAP notification VS Code already supports.

**`statement` stepping definition.** The review correctly pointed out that "assembly line" is ambiguous. The precise definition: a `statement` step advances execution until `PC` reaches an address whose `AddressMapEntry` in `.kdbg` has a source file and line different from the current stop position. Lines with no `AddressMapEntry` (blank lines, directives, labels, macro definitions) do not exist as statement boundaries. A line that expands into multiple instructions (macro call) is treated as a single statement — the step completes after the last expanded instruction.

Internally, statement step translates into `RunUntil(StopCondition.PcNotInSourceRange(currentFile, currentLine))`, where `PcNotInSourceRange` is a new flag added to `StopCondition` for this purpose. The range is computed from `.kdbg` as the contiguous set of addresses mapping to the current `(file, line)` including all macro-expanded descendants.

## `.kdbg` debug info format

The linker emits `.kdbg` alongside `.gb` and `.sym`. `.sym` remains unchanged and is output-compatibility-only — see §9.10.

### ID scheme (normalized across sections)

All cross-references in `.kdbg` use **1-based IDs with 0 as the sentinel "none"**:

- `StringId` — 1-based index into the string pool; 0 = "no string."
- `SourceFileId` — 1-based index into the source-file table; 0 = "no file."
- `ScopeId` — 1-based index into the scope table; 0 = "global / root scope, no explicit entry."

This uniformly resolves the previous contradiction between "0-based" and "0 = sentinel." Readers convert an ID to an array index with `id - 1` and treat `id == 0` as the sentinel. Writers assign IDs starting at 1 and never use 0 for a real entry.

### 9.1 Header (exact layout, 32 bytes, little-endian)

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | Magic | ASCII `"KDBG"` |
| 4 | 2 | Version | u16; v1 = initial |
| 6 | 2 | Flags | u16; bit 0 = expansion data present; bit 1 = scope table present; bit 2 = paths are absolute; bits 3–15 reserved |
| 8 | 4 | StringPoolOffset | byte offset from file start; always present, never 0 |
| 12 | 4 | SourceTableOffset | byte offset; always present, never 0 |
| 16 | 4 | ScopeTableOffset | byte offset; 0 if the section is omitted (flag bit 1 = 0) |
| 20 | 4 | SymbolTableOffset | byte offset; always present |
| 24 | 4 | AddressMapOffset | byte offset; always present |
| 28 | 4 | ExpansionPoolOffset | byte offset; 0 if the section is omitted (flag bit 0 = 0) |

**Endianness and integer encoding.** All multi-byte integers are little-endian, unsigned unless explicitly documented otherwise. No padding is inserted between fields at any level of the format. The writer emits fields back-to-back in declared order; the reader reads fields back-to-back in declared order. **There is no "natural alignment"** — the format is byte-packed throughout. Earlier wording to the contrary is removed.

**Section ordering and optional sections.** The header offsets are authoritative; readers must use the offsets, not positional assumptions. Writers emit present sections in the order header → string pool → source table → (scope table if flag bit 1 = 1) → symbol table → address map → (expansion pool if flag bit 0 = 1). Omitted optional sections have offset = 0 in the header and consume no bytes in the file. The offsets of any **following** sections are unaffected by whether an optional section is present — each is computed independently by the writer.

### 9.2 String pool

```
u32 count                           // number of strings
repeated (count) times:
    u16 length                      // byte length of the string
    bytes[length]                   // UTF-8, not null-terminated
```

String IDs are 1-based indices (§ ID scheme). The string at on-disk position 0 has `StringId = 1`. `StringId = 0` is the sentinel "no string." The string at position 0 must not be the empty string by convention (the empty string, if needed, gets `StringId` ≥ 1).

### 9.3 Source file table

```
u32 count
repeated (count) times:
    u32 pathStringId                // references string pool
```

Source-file IDs are 1-based indices. The source file at on-disk position 0 has `SourceFileId = 1`. `SourceFileId = 0` is the sentinel "no file."

**Path format.** When header flag bit 2 = 0 (default), `pathStringId` references a **workspace-relative** path with forward-slash separators (`subdir/file.asm`). When flag bit 2 = 1, paths are absolute. The workspace root used for relative paths is determined by the linker from the input project configuration and recorded externally (the linker emits relative paths whenever it can). This resolves the earlier contradiction about "absolute paths resolved against workspace root" and fixes cross-machine reproducibility: `.kdbg` files emitted on one machine (CI) are consumable on another (developer workstation).

If any source file is outside the workspace root (e.g., a system include), the linker still records it as a relative `../` path if possible, or as absolute with flag bit 2 = 1 as a last resort. The debugger's path-resolution logic (§8.4) combines the relative path with the workspace root of the currently open VS Code session and normalizes case on case-insensitive filesystems.

### 9.4 Scope table

Present only when header flag bit 1 = 1. Byte-packed, no padding.

```
u32 count
repeated (count) times (12 bytes):
    u8  kind                        // 0=Global, 1=LocalToLabel, 2=MacroLocal, 3=File
    u8  reserved                    // must be 0
    u16 reserved                    // must be 0
    u32 parentScopeId               // 1-based ScopeId; 0 = no parent (top-level)
    u32 nameStringId                // display name; 0 if anonymous
```

Scope IDs are 1-based. The scope at on-disk position 0 has `ScopeId = 1`. `ScopeId = 0` is the sentinel "global / root / no explicit scope" used by symbols that do not need a scope entry.

Structural scopes prevent symbol-name collisions between macro-local labels and similarly-named globals from corrupting debugger symbol lookup. Each symbol references its containing scope by `ScopeId`.

### 9.5 Symbol table

Byte-packed, no padding. Each entry is exactly 24 bytes in declared field order.

```
u32 count
repeated (count) times (24 bytes):
    u8  kind                        // 0=Label, 1=EquConstant, 2=RamLabel, 3=Macro, 4=Export
    u8  bank
    u16 address
    u16 size                        // 0 if unknown
    u16 reserved                    // must be 0
    u32 nameStringId
    u32 scopeId                     // 0 = global / no explicit scope
    u32 definitionSourceFileId      // 0 = no source info
    u32 definitionLine
```

Entries are sorted by `(bank, address)` for nearest-match symbolification of disassembly.

### 9.6 Address map

Byte-packed, no padding. Each entry is exactly 16 bytes.

```
u32 count
repeated (count) times (16 bytes, sorted by (bank, address)):
    u8  bank
    u8  byteCount                   // 1..255 ROM bytes covered by this entry
    u16 address
    u32 sourceFileId
    u32 line
    u32 expansionStackOffset        // absolute byte offset into ExpansionPool, or 0xFFFFFFFF if none
```

**Coalescing policy.** The format supports coalesced multi-byte entries (`byteCount > 1`). Emission coalesces contiguous ROM bytes sharing `(bank, sourceFileId, line, expansionStackOffset)` into a single entry up to `byteCount = 255`. Coalescing is **optional**: a conforming writer may emit all entries with `byteCount = 1` and still produce a valid file. The Phase 1 linker implementation emits uncoalesced entries. Coalescing is added in Phase 3 when address-map size becomes a practical concern (§9.8 estimates are post-coalesce; pre-coalesce size is ~5× larger).

Readers handle both coalesced and uncoalesced files identically: a lookup for `(bank, address)` uses binary search to find the predecessor entry whose `[entry.address, entry.address + entry.byteCount)` range contains the query.

### 9.7 Expansion pool

Present only when header flag bit 0 = 1. Byte-packed, no padding.

```
u32 poolByteSize                    // total payload size in bytes
payload:
    byte-packed concatenation of expansion stacks.
    An individual stack at offset O from the start of the payload:
        u16 depth
        repeated (depth) times (8 bytes):
            u32 sourceFileId
            u32 line
```

An `expansionStackOffset` in the address map is an absolute byte offset **from the start of the `.kdbg` file** (not from the payload start), pointing at the `u16 depth` header of a stack. The sentinel `0xFFFFFFFF` means "no expansion": the AddressMapEntry's `sourceFileId`/`line` are the final source location with no macro chain.

Because `ExpansionPoolOffset` in the header points at the `u32 poolByteSize` field, the first valid stack offset for an entry is `ExpansionPoolOffset + 4`.

**Deduplication.** Emission deduplicates identical stacks by sharing offsets. A writer maintains a hash map from `{depth, frames}` content to offset; a new stack that matches an existing one reuses the prior offset. **Deduplication is optional** — a valid writer may emit every stack uniquely. Dedup is the Phase 3 implementation target (matching §9.6 coalescing); Phase 1 does not implement it.

### 9.8 Memory footprint estimate

Justification, not assertion. For a 200 KB ROM with ~10 000 symbols and aggressive macro use (Azure Dreams-class):

| Section | Estimate |
|---|---|
| String pool (file paths + symbol names) | ~150 KB |
| Source table | negligible |
| Scope table | ~50 KB |
| Symbol table (10 000 × 24 bytes) | 240 KB |
| Address map after coalescing (~20 000 entries × 16 bytes) | 320 KB |
| Expansion pool after dedup | ~100–400 KB |
| **Total, loaded** | **~1–1.5 MB** |

Without coalescing and dedup the total is 5–8 MB. With them it fits comfortably in memory during interactive debugging.

### 9.9 Versioning

`Version` is checked on load. `v1` is the current format. Additive extensions bump `Version` to `v2` and add new section offsets after the current fields in the header; new flags bits enable new sections. The reader rejects files with a version newer than it understands.

The `Flags` field's bit 0 distinguishes `.kdbg` files with expansion data from those without (small projects may omit the expansion pool entirely).

### 9.10 `.sym` divergence policy

`.sym` is explicitly **output-compatibility** for BGB / Emulicious / external emulators. It continues to be emitted in its current BGB format: `BB:AAAA SymbolName` per line, labels and RAM names only.

`koh-dbg` does **not** read `.sym`. All debug features use `.kdbg` exclusively.

New Koh-specific debug features (macro expansion display, scope-aware symbol resolution, source mapping, watchpoints) exist only in `.kdbg`. `.sym` is not extended to carry them.

This prevents divergence pressure: there is no "fall back to .sym" path, so `.sym` remains a one-way export and `.kdbg` remains authoritative for Koh's own tooling.

## `Koh.Emulator.App`

### 10.1 Project layout

```
Koh.Emulator.App/
├── Program.cs                       // Blazor WASM entrypoint; registers services
├── App.razor                        // root component; routes to shell by mode
├── Shell/
│   ├── RuntimeMode.cs               // enum: Debug, Standalone
│   ├── RuntimeModeDetector.cs       // JS interop probe
│   ├── DebugShell.razor             // shell when running in VS Code webview
│   └── StandaloneShell.razor        // shell when running standalone / dev host
├── Components/
│   ├── LcdDisplay.razor
│   ├── LcdDisplay.razor.js
│   ├── CpuDashboard.razor
│   ├── MemoryView.razor             // (Phase 2)
│   ├── PaletteView.razor            // (Phase 2)
│   ├── VramView.razor               // (Phase 2)
│   └── OamView.razor                // (Phase 2)
├── Input/
│   ├── JoypadCapture.razor          // keyboard → JoypadState
│   └── JoypadKeyMap.cs              // configurable mapping
├── Services/
│   ├── EmulatorHost.cs              // owns GameBoySystem, frame loop
│   ├── FramePacer.cs                // rAF-driven pacing
│   ├── FramebufferBridge.cs         // single-copy framebuffer JS interop
│   └── RomLoader.cs                 // debug: DAP launch args; standalone: file picker
├── DebugMode/
│   ├── DebugModeBootstrapper.cs
│   ├── VsCodeBridge.razor.js        // postMessage bridge to extension
│   └── DapTransport.cs
├── StandaloneMode/
│   ├── StandaloneBootstrapper.cs
│   ├── RomFilePicker.razor
│   └── PlaybackControls.razor
└── wwwroot/
    ├── index.html
    ├── css/
    └── sample-roms/                 // downloaded, gitignored
```

### 10.2 `EmulatorHost`

Central coordinator. Owns the `GameBoySystem` instance, drives the frame loop, publishes framebuffer and CPU-state events to Razor components, receives joypad state from input capture, and — in debug mode — is injected into `Koh.Debugger.DebugSession` so the debugger can call `RunFrame` / `StepInstruction` through it.

Standalone mode instantiates `EmulatorHost` directly. Debug mode wires `DebugSession` between the host and the DAP transport.

### 10.3 Frame pacing

Frame pacing is **best-effort, aligned to display refresh, subordinate to debug responsiveness**.

- Emulation runs in an `async` loop on the Blazor event loop (single-threaded).
- `FramePacer.WaitForNextFrameAsync()` uses `requestAnimationFrame` (exposed via JS interop) as the primary wait, falling back to `Task.Delay(0)` on environments where rAF is not available.
- `requestAnimationFrame` presents at the browser's composition cadence, typically 60 Hz. The emulator's internal target is 59.727 Hz; when these disagree (e.g., 60 Hz display), the emulator runs slightly fast or drops occasional frames — acceptable for an interactive debug experience.
- Debug operations (`pause`, `stepInstruction`, `setBreakpoints`) bypass the frame pacer entirely. Debug responsiveness is never blocked waiting for the next frame.
- Wall-clock drift correction: if the emulator falls behind by more than 3 frames, the pacer resynchronizes by dropping frames rather than trying to catch up. Running "too fast" is not a problem to correct.
- Benchmark mode disables pacing entirely; `RunFrame` loops as fast as the runtime allows.

### 10.4 Framebuffer data transfer (single-copy)

Base64 encoding and per-frame object allocation are rejected. The pipeline performs **exactly one copy per frame** across the WASM↔JS boundary. This is "single-copy," not "zero-copy" — the previous wording overclaimed. The pipeline is:

1. On Blazor startup, JS interop allocates a persistent `Uint8ClampedArray` of length 160×144×4 = 92 160 bytes and a persistent `ImageData` wrapping it. These live for the lifetime of the webview.
2. `Framebuffer.Pixels` in `Koh.Emulator.Core` is a `Memory<byte>` backing-store writable by the PPU directly (no intermediate allocations per pixel or per frame).
3. After `RunFrame` completes, `FramebufferBridge.CommitAsync()` invokes a single JS interop call that copies the 92 160 bytes into the persistent `Uint8ClampedArray`.
   - The copy uses Blazor's efficient span-to-typedarray bridge (`IJSUnmarshalledRuntime.InvokeUnmarshalled` in .NET 7/8, or the equivalent marshaled span helper in .NET 10).
   - One copy per frame; no serialization, no encoding, no per-pixel JS calls.
4. The JS side calls `ctx.putImageData(persistentImageData, 0, 0)` on the canvas.

This yields one 92 KB memcpy per frame across the WASM↔JS boundary: ~5.5 MB/s, negligible.

A true zero-copy path is available later: allocate the framebuffer's backing store in JS memory and expose it to WASM via `SharedArrayBuffer`, eliminating the single copy. `SharedArrayBuffer` requires cross-origin isolation headers which VS Code webviews do not currently grant, so this optimization is for dev-host / MAUI only if it becomes necessary. Until then, the single-copy path ships everywhere.

### 10.5 Phase 1 UI

- `DebugShell` shows a placeholder LCD (solid gray, centered "Phase 1: awaiting PPU"), live `CpuDashboard`, status bar (mode, FPS, cycles).
- `StandaloneShell` shows a placeholder LCD, `RomFilePicker`, `PlaybackControls`.
- Dashboard values that change between steps highlight briefly in yellow.

### 10.6 Phase 2 UI additions

- `VramView` — tile data as a grid.
- `PaletteView` — BG/OBJ palettes with swatches.
- `OamView` — sprite table with attributes.

## VS Code extension

### 11.1 Module layout

```
editors/vscode/src/
├── extension.ts                    // ~50 lines: activate/deactivate facade
│
├── core/
│   ├── KohExtension.ts             // top-level coordinator
│   ├── DisposableStore.ts
│   └── Logger.ts
│
├── lsp/
│   ├── LspClientManager.ts
│   └── serverPathResolver.ts
│
├── config/
│   ├── KohYamlReader.ts
│   └── WorkspaceConfig.ts
│
├── build/
│   ├── BuildTaskProvider.ts
│   ├── KohBuildTask.ts
│   └── binaryResolver.ts
│
├── debug/
│   ├── KohDebugRegistration.ts
│   ├── ConfigurationProvider.ts
│   ├── TargetSelector.ts           // multi-target picker
│   ├── InlineDapAdapter.ts
│   ├── DapMessageQueue.ts          // ordering + buffering (§11.9)
│   ├── DebugSessionTracker.ts
│   └── launchTypes.ts
│
├── webview/
│   ├── EmulatorPanelHost.ts
│   ├── EmulatorPanel.ts
│   ├── BlazorAssetLoader.ts        // prod bundled vs dev-host override
│   ├── html/
│   │   └── EmulatorHtml.ts
│   └── messages.ts                 // typed contracts
│
└── commands/
    └── CommandRegistrations.ts
```

### 11.2 Wiring rules

Same as first draft (facade + narrow subsystems, one-way dependencies, each subsystem owns its own VS Code API registration).

`extension.ts`:

```ts
import * as vscode from 'vscode';
import { KohExtension } from './core/KohExtension';

let extension: KohExtension | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    extension = new KohExtension(context);
    await extension.start();
}

export async function deactivate(): Promise<void> {
    await extension?.dispose();
    extension = undefined;
}
```

### 11.3 Inline DAP adapter

```ts
export class KohInlineDapAdapter implements vscode.DebugAdapter {
    private readonly messageEmitter = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this.messageEmitter.event;

    constructor(
        private readonly webviewHost: EmulatorPanelHost,
        private readonly queue: DapMessageQueue
    ) {
        webviewHost.onMessageFromWebview(m => {
            if (m.kind === 'dap') this.messageEmitter.fire(m.payload);
        });
    }

    handleMessage(message: vscode.DebugProtocolMessage): void {
        this.queue.enqueueOutbound(message, msg =>
            this.webviewHost.postToWebview({ kind: 'dap', payload: msg }));
    }

    dispose(): void { this.messageEmitter.dispose(); }
}
```

Registered via `vscode.debug.registerDebugAdapterDescriptorFactory('koh', ...)` returning a `vscode.DebugAdapterInlineImplementation(adapter)`.

### 11.4 Configuration resolution

`KohConfigurationProvider.resolveDebugConfiguration` on F5:

1. **Both `koh.yaml` and `launch.json` absent.** F5 shows an actionable message: "No Koh workspace configuration. Create one?" with buttons to scaffold a minimal `koh.yaml` or to create a `launch.json` template. No silent failure.
2. **`koh.yaml` present, `launch.json` absent.** The provider reads `koh.yaml`, enumerates its build targets, and synthesizes one `DebugConfiguration` per target (see §11.4.1). If there is exactly one target, it is chosen automatically. If there are multiple, `TargetSelector` shows a QuickPick.
3. **`launch.json` present.** The user's explicit configurations take precedence. `koh.yaml` is read only to resolve the `preLaunchTask` if the user omits one.
4. **`koh.yaml` incomplete** (missing entrypoint or output path for a named target): the provider raises a clear error pointing at the offending field. No partial defaults are guessed.

#### 11.4.1 Multi-target model

A Koh workspace may contain multiple ROM targets (Koh already supports this via `koh.yaml`). The debug configuration supports this explicitly:

```jsonc
{
    "type": "koh",
    "request": "launch",
    "name": "Debug game-a",
    "target": "game-a",
    "program": "${workspaceFolder}/build/game-a.gb",
    "debugInfo": "${workspaceFolder}/build/game-a.kdbg",
    "preLaunchTask": "koh: build game-a"
}
```

- `target` is a `koh.yaml` target name; when present, `program` and `debugInfo` may be omitted and derived.
- `program` and `debugInfo` override the derived paths when present.
- The synthesized initial configurations include one entry per target in `koh.yaml`.

Breakpoints set in source files that belong to multiple targets are resolved against the currently active debug session's target only; if you debug `game-a`, breakpoints in shared source files hit against `game-a`'s compiled addresses.

### 11.5 Build-on-launch

Two supported paths:

1. **Implicit (zero-config):** synthesized launch config includes `preLaunchTask: "koh: build <target>"`. `BuildTaskProvider` registers a task provider that synthesizes the task from `koh.yaml`.
2. **Explicit:** user-defined `preLaunchTask` runs whatever they want.

Build failure aborts the launch with VS Code's standard "build failed" prompt.

### 11.6 Blazor asset bundling and build strategy

The extension ships with a bundled Blazor WASM build of `Koh.Emulator.App`. Build rules:

- **Production / Marketplace package:** always AOT-published. The `.vsix` build script runs `dotnet publish Koh.Emulator.App -c Release -p:RunAOTCompilation=true` and copies `bin/Release/net10.0/publish/wwwroot/_framework/` and related assets into `editors/vscode/dist/emulator-app/`.
- **Local extension development (`npm run watch`):** non-AOT by default (`-p:RunAOTCompilation=false`), cutting build time from minutes to seconds. The extension's debug launch depends on a watch task `watch-emulator-app` (`dotnet watch publish Koh.Emulator.App -c Debug -p:RunAOTCompilation=false`) that rebuilds on source change.
- **Dev-host override:** see §11.8 for security rules. The setting `koh.emulator.devHostUrl` points `BlazorAssetLoader` at a running `dotnet run` dev host instead of the bundled assets for iterating on Razor components with hot reload.
- **CI verification:** the extension CI job runs a dedicated step that builds `Koh.Emulator.App` with AOT, packages the `.vsix`, and runs a smoke test launching the packaged extension and verifying the Blazor app loads.

**Freshness detection via content hashing, not timestamps.** Timestamp-based freshness checks are brittle with build caches, case-insensitive filesystems, and git checkouts. Instead:

1. The build script computes a SHA-256 over a canonicalized list of all files under `src/Koh.Emulator.App/`, `src/Koh.Emulator.Core/`, `src/Koh.Debugger/`, `src/Koh.Linker.Core/`, plus the .NET SDK version, the Blazor WASM SDK version, and the C# compiler version. The canonicalized input list is a sorted list of `(relative_path, file_sha256)` pairs; the outer SHA-256 is computed over that list.
2. The computed hash is written to `dist/emulator-app/.build-hash` as hex text at build time.
3. The `.vsix` packaging step recomputes the hash and compares it to `.build-hash`. Mismatch fails the package build with a clear error ("emulator-app assets are stale, run `dotnet publish` or `npm run build:emulator-app`").
4. Running `npm run watch` without `watch-emulator-app` is detected at extension activation: the extension reads `.build-hash`, spawns a background task that recomputes the hash (throttled), and — if they diverge — logs a warning to the Koh output channel and shows a "Emulator-app assets may be stale" banner in the webview's status bar.

### 11.7 Extension packaging

- Marketplace packages: prebuilt AOT assets are included in the `.vsix`. Package size target: ≤ 20 MB total.
- `dist/emulator-app/` is gitignored; the packaging pipeline is the source of truth.
- **CI artifact caching uses the same content hash from §11.6 as the cache key.** The cache key formula is:
  `emu-app-${{ sha256(sorted file list of src/Koh.Emulator.App + src/Koh.Emulator.Core + src/Koh.Debugger + src/Koh.Linker.Core + .NET SDK version + Blazor WASM SDK version + C# compiler version) }}`
  Any change to any input file, SDK version, or compiler version invalidates the cache. The cache key does **not** include extension TypeScript source — a TypeScript-only change reuses the cached AOT build.
- Freshness is verified by content hash (§11.6), not timestamps. Out-of-date assets fail the package step.
- Two-stage build: `build:emulator-app` (runs `dotnet publish`, produces `dist/emulator-app/`, writes `.build-hash`) and `build:extension` (runs TypeScript compile, packages `.vsix`, verifies `.build-hash`). CI runs them in sequence with the cache keyed as above.

### 11.8 Settings and dev-host security

```jsonc
"koh.emulator.showDashboard":  { "type": "boolean", "default": true },
"koh.emulator.scale":          { "type": "number", "enum": [1, 2, 3, 4], "default": 3 },
"koh.emulator.devHostUrl":     { "type": "string", "description": "Dev-host URL for Blazor asset loading. Ignored in installed extensions; see dev-host security rules." },
"koh.debugger.logDapTraffic":  { "type": "boolean", "default": false, "description": "Log DAP messages to the Koh output channel" }
```

**Dev-host URL security.** Pointing a webview at an arbitrary URL is powerful, so the setting is gated on three rules enforced at webview creation:

1. **Extension mode gate.** The URL is honored only when `context.extensionMode === vscode.ExtensionMode.Development`. In `Production` and `Test` modes, the setting is ignored and a warning is logged. Installed Marketplace extensions never honor the setting.
2. **URL whitelist.** Only `http://localhost:<port>` and `http://127.0.0.1:<port>` are accepted. Any other scheme, host, or lack of explicit port is rejected with a descriptive error. The port is validated as a number in the 1024–65 535 range.
3. **CSP alignment.** When the dev-host URL is used, the webview's `Content-Security-Policy` is extended to include the dev-host origin as a script/style/connect source. The bundled-asset CSP is strict and omits network sources; the dev-host CSP opens exactly the configured origin and nothing else.

These three rules together ensure that a malicious workspace cannot configure the setting on a user's installed extension to load attacker-controlled content, because installed extensions ignore the setting entirely. Contributor workflows that need the dev host run the extension in the Extension Development Host, which explicitly opts in via `ExtensionMode.Development`.

### 11.9 Transport reliability

The DAP transport between VS Code and the webview must handle ordering, session identity, buffering during app boot, webview disposal, and error recovery.

- **Ordering:** `postMessage` within a single webview is ordered. `DapMessageQueue` on the extension side enforces FIFO for outbound messages even when some arrive before the webview is fully loaded.
- **Boot buffering:** DAP messages received before the Blazor app signals "ready" are queued in `DapMessageQueue`. The Blazor app posts `koh.ready` to the extension as soon as `DapDispatcher` is constructed; the extension drains the queue.
- **Session identity:** each DAP session is tied 1:1 to a specific webview instance. A new debug session opens a new webview. Closing the webview terminates the debug session with DAP `terminated` event.
- **Webview reload:** if the user reloads the webview (debug command), the debug session is terminated. There is no reconnection — a fresh F5 is required. This is intentional and documented.
- **Message sequence numbers:** DAP messages carry a `seq` field as specified. The extension logs gaps but does not request retransmission (postMessage is reliable in-process).
- **Error handling:** if the Blazor app throws an unhandled exception, it posts `{kind: "fatalError", message, stack}`. The extension converts to a DAP `output` event (stderr category) followed by `terminated`. The webview remains open so the user can read the error.
- **Timeouts:** DAP requests time out after 30 seconds of no response. A timeout is logged and the session continues; a pattern of timeouts raises an error banner in the webview.
- **Disposal order:** `KohExtension.dispose()` stops the LSP, disposes the debug registration, disposes the webview host. Webview disposal triggers DAP `terminated` if a session is active. The `DapMessageQueue` rejects any further enqueue after disposal.

## Testing strategy

### 12.1 Semantic opcode tests

One test per SM83 instruction per meaningful addressing mode. For each: construct a tiny ROM with one instruction, run `StepInstruction()`, assert final register state and affected memory. Estimated count: 500–700 tests covering instruction semantics.

### 12.2 M-cycle timing tests

Separate suite verifying **instruction duration**. For each instruction: run `StepInstruction()`, assert `StepResult.TCyclesRan` matches the authoritative SM83 instruction timing table. Estimated count: 500 tests (one per instruction).

### 12.3 CPU hazard tests

Sub-instruction behaviors not captured by semantic or timing tests:

- EI-delay slot (IRQ serviced after the instruction **after** `EI`).
- HALT bug with IME=0 and pending IRQ.
- HALT wake-up with IME=1 (jumps to ISR) and IME=0 (continues without ISR).
- Interrupt dispatch timing (5 M-cycles to first ISR instruction, PC correctly pushed, IF bit cleared, IME cleared).
- Multi-byte opcodes interrupted at M-cycle boundaries by pending IRQs.

Estimated count: 50–100 tests.

### 12.4 PPU tests

- Mode transitions: for each scanline, verify dot positions of mode 2→3, 3→0, line end.
- Mode 3 length with various SCX values, window enable positions, sprite configurations.
- LY/LYC STAT IRQ raising.
- Fetcher output for hand-crafted tile maps (ensures the pixel FIFO produces the expected pixels).
- VRAM/OAM access blocking windows.

Estimated count: 100–200 tests.

### 12.5 DMA tests

- OAM DMA: verify 160-byte transfer, 1-cycle start delay, CPU HRAM-only access during transfer, $FF reads from non-HRAM, dropped writes.
- HDMA general: verify CPU halt, byte counts, double-speed timing.
- HDMA HBlank: verify 16-byte-per-HBlank transfer, cancel-on-write-zero behavior.

Estimated count: ~40 tests.

### 12.6 DAP handler tests (`Koh.Debugger.Tests`)

Feed raw JSON to `DapDispatcher`, assert emitted responses. Categories: protocol shape, breakpoint resolution, stack walking, step-over/in/out semantics, variables provider shape, evaluate. Estimated count: 100 tests.

### 12.7 Compatibility ROM tests (`Koh.Compat.Tests`)

Runs the external test ROMs listed in §3's compliance targets. Blargg ROMs report via serial buffer; Mooneye ROMs via Fibonacci register pattern; acid2 via framebuffer comparison against reference PNGs. Phase-gated per §3.

### 12.8 Integration tests

Small hand-written programs (bubble sort, sprite animation, interrupt-driven counter) run end-to-end and assert behavior. Estimated count: 30–50 tests.

### 12.9 Performance gates per phase

Each phase has a representative benchmark. The gate is defined precisely enough that CI can enforce it without human judgment.

**Workloads by phase.** A workload is "representative" when it exercises the hot paths that will ship at that phase. An empty loop proves nothing.

| Phase | Representative workload |
|---|---|
| **Phase 1** | Mock CPU: each "instruction" is 4 T-cycles and performs one `Mmu.ReadByte` (varying address to defeat caching), one ALU op on registers, one flag update, one conditional branch. Full `Timer` ticks every cycle. Full `Mmu` routing (no DMA active, no PPU lockout active). PPU advances its dot counter but does not fetch pixels. |
| **Phase 2** | Real PPU with full pixel FIFO, tile data in VRAM, 40 sprites visible across a frame, window enabled mid-frame, CGB palettes. Mock CPU from Phase 1. OAM DMA runs once per frame. |
| **Phase 3** | Real CPU running Blargg `cpu_instrs/01-special.gb` in a loop, real PPU rendering its test-output display, real Timer, OAM DMA, interrupts enabled. Known-accurate emulator baseline: ~5.5 M instructions per real-time second. |
| **Phase 4** | Full system: real commercial ROM (Pokémon Gold title screen + title music) with APU, HDMA, real joypad input at a steady-state pattern. All subsystems active. |

**Threshold rule** (applies uniformly):

- **Wall-clock measurement:** the benchmark runs the workload for `N_warmup` seconds of wall-clock time to warm up (AOT JIT, browser compositor, etc.), then measures `N_measure` seconds of wall-clock time, then reports the number of "emulated real-time seconds" accomplished per second of wall-clock time as a multiplier.
- **N_warmup = 5 s**, **N_measure = 30 s** for every phase.
- **Run count:** 5 independent runs per measurement.
- **Aggregation:** discard the single fastest and single slowest result, take the **median** of the remaining 3.
- **Gate passes** if the median meets the threshold **and** no single run falls below `0.9 × threshold`.

**Thresholds:**

| Phase | Median threshold | Hard floor (single-run minimum) |
|---|---|---|
| Phase 1 | ≥ 2.0× real-time | ≥ 1.8× |
| Phase 2 | ≥ 1.5× real-time | ≥ 1.35× |
| Phase 3 | ≥ 1.3× real-time | ≥ 1.17× |
| Phase 4 | ≥ 1.1× real-time | ≥ 1.0× |

**Environment pinning.**

- Benchmarks run in **CI only**, on pinned runner images. A contributor running benchmarks locally gets an informational report; only CI results gate merges.
- Pinned runners: `windows-latest` (GitHub Actions) + `ubuntu-latest` (GitHub Actions). Each runs the benchmarks in headless **Chrome stable** and **Edge stable**. Four (OS × browser) combinations per phase.
- Browser versions are pinned via `setup-chrome` / `setup-edge` actions with a version hash recorded in the CI config.
- Thresholds above are the Chrome-on-linux baseline. Other combinations get per-environment thresholds recorded in `benchmarks/baselines/<env>.json`, updated when the baseline is deliberately re-measured.
- **Regression rule:** a phase gate also requires the median to be within **10%** of the recorded baseline for the same environment. A 10%+ slowdown from baseline fails the gate even if the absolute threshold is met. Baselines are updated by an explicit CI workflow triggered by a human, never automatically, and changes to baselines are committed and reviewed.

**Failure policy.** A failing gate blocks the phase's exit. The options are: (a) diagnose and fix the regression; (b) deliberately update the baseline because we consciously accept a new cost; (c) redefine the workload if the workload itself is wrong. Option (a) is the default expectation.

### 12.10 CI ROM fixtures

Public-domain test ROMs are fetched by `scripts/download-test-roms.ps1` / `.sh` with pinned URLs and SHA-256. CI runs the download step before the test step and caches results between runs. Tests that depend on fixture ROMs are **not** conditional — if the download fails, CI fails with an explicit "test ROM fixtures unavailable" error. This makes emulator regressions visible rather than silently skipped.

## Phased implementation plan

### Phase 0 — Project scaffolding

1. Add `Koh.Emulator.Core`, `Koh.Debugger`, `Koh.Emulator.App` to `Koh.slnx`.
2. Add `Koh.Emulator.Core.Tests`, `Koh.Debugger.Tests`.
3. Write `docs/decisions/emulator-platform-decision.md`.
4. Verify Blazor WASM non-AOT build works for development.
5. Verify Blazor WASM AOT publish works for release (`dotnet publish -p:RunAOTCompilation=true`).
6. Verify dev host runs (`dotnet run --project Koh.Emulator.App`) with a placeholder page.
7. Scaffold `scripts/download-test-roms.ps1` / `.sh` with empty target list.

**Exit:** empty projects compile; dev host serves a hello-world page; both AOT and non-AOT publish succeed.

### Phase 1 — Infrastructure & F5 wiring

End-to-end F5 with no opcodes and no rendering, but a real debug session, real breakpoint storage, real custom-event flow, and a dashboard showing live mock CPU state.

1. **`Koh.Emulator.Core` skeleton** per §7.1, §7.2, §7.3 including the Terminology box. `RomOnly` + `Mbc1` cartridges via `Cartridge` sealed class with enum dispatch (§7.5). Concrete `Mmu` with all-region routing but no PPU lockout and no DMA contention window. `Sm83.cs` implements the representative-workload mock instruction (§12.9 Phase 1). Empty `Ppu` (ticks the dot counter, framebuffer stays gray). **Full `Timer`** including IRQ raising and the TMA 1-M-cycle reload delay. **No OAM DMA, no HDMA** — these arrive in Phase 2. Save-state serialization is **not implemented**; save-state design constraints from §7.11 are followed so nothing blocks later implementation.
2. **`Koh.Emulator.Core.Tests`:** construction, memory read/write (all regions), ROM header parsing, `RunFrame` clock increment, mock-CPU tick budget, Timer (DIV rate, TIMA at all four TAC settings, TMA reload, IRQ raising).
3. **`Koh.Linker.Core` `.kdbg` emission, minimal scope:**
   - Emit the format per §9 with flag bit 1 = 1 (scope table present) and flag bit 0 = 1 (expansion pool present) if the source uses macros.
   - **No coalescing** — every ROM byte gets its own AddressMapEntry with `byteCount = 1`.
   - **No expansion stack deduplication** — identical stacks are written separately.
   - Path handling: workspace-relative paths per §9.3.
   - These optimizations (coalescing, dedup) are deferred to Phase 3 when the file size becomes a practical concern.
   - Linker tests round-trip the format and verify byte-offset positions in the header.
4. **`Koh.Debugger` skeleton:**
   - `DapDispatcher`, `Capabilities` advertising exactly the Phase 1 set per §8.7.
   - Handlers: `initialize`, `launch`, `configurationDone`, `continue`, `pause`, `terminate`, `setBreakpoints` (resolves `.kdbg` locations, returns verified locations to VS Code, does **not** yet halt on hit), `scopes` + `variables` (Registers + Hardware), `exceptionInfo`.
   - `DebugInfoLoader` parses `.kdbg` into `SourceMap` / `SymbolMap`.
   - Publishes `koh.cpuState` events for the dashboard.
   - Step handlers respond with DAP "not supported" errors; stepping capabilities are not advertised.
5. **`Koh.Debugger.Tests`:** DAP JSON round-trip for implemented handlers including `setBreakpoints` verified-location response.
6. **`Koh.Emulator.App` Phase 1 UI** per §10.5. **Single-copy** framebuffer pipeline per §10.4 (transfers blank gray in Phase 1). Frame pacer with rAF. Runtime-mode detector and both shells. Sample-ROM download script and directory in `wwwroot/sample-roms/`.
7. **Phase 1 benchmark** per §12.9 Phase 1 row. Blocks Phase 2.
8. **VS Code extension refactor + debug integration:**
   - Decompose `extension.ts` per §11.1.
   - Register `koh` debug type with inline DAP adapter (§11.3).
   - `ConfigurationProvider` + `TargetSelector` handling all cases in §11.4 (absent, incomplete, single target, multi-target).
   - `BuildTaskProvider` synthesizes build tasks per target.
   - `BlazorAssetLoader` with dev-host override enforcing §11.8 security rules.
   - `DapMessageQueue` with boot buffering.
   - Transport reliability rules §11.9.
9. **Extension build pipeline:**
   - `watch-emulator-app` task for non-AOT watch rebuild.
   - `.vsix` packaging step invoking AOT publish, computing the content hash, writing `.build-hash`, and refusing to package on mismatch (§11.6).

**Exit criteria:**

- F5 on an `.asm` file builds the ROM, opens the webview, and shows the dashboard with live mock-CPU state updating between steps.
- Setting a breakpoint in the VS Code editor gutter causes `setBreakpoints` to run; the debugger returns a `verified = true` breakpoint at the resolved `.kdbg` address and VS Code renders the standard red-circle breakpoint gutter marker. Breakpoints do **not** yet cause execution to halt (halt behavior ships in Phase 3).
- Debug toolbar's Pause actually pauses the run loop; Continue resumes; Stop terminates cleanly.
- Dev host runs; the bundled Blazor app loads in it correctly.
- Standalone file picker loads a ROM (and then runs the mock CPU).
- Phase 1 benchmark passes the §12.9 Phase 1 gate (median ≥ 2.0×, hard floor ≥ 1.8×, within 10% of baseline) across all four (OS × browser) combinations.
- CI builds the extension package, the content hash matches, and the smoke test verifies the bundled Blazor app loads.

### Phase 2 — PPU & rendering

Full pixel-FIFO PPU rendering. A partial CPU implements exactly the opcodes required to run the acid2 test ROMs — this subset is defined empirically rather than guessed up front.

1. **Determine the acid2 opcode subset empirically.** Disassemble `dmg-acid2.gb` and `cgb-acid2.gb` (the exact ROMs we will gate against). Record the set of unique opcodes they execute. Implement exactly that set in `Sm83.cs`. Any opcode not in the set raises `StopReason.HaltedBySystem` with an "unimplemented opcode" diagnostic so missing instructions are visible immediately. This avoids hardcoding guesses about which opcodes suffice.
2. **Full `Ppu` implementation** per §7.7 (algorithmic mode-3 behavior, fetcher + FIFO, sprites, window, CGB palettes).
3. **Full OAM DMA** per §7.6 (transfer + contention window + PPU OAM-scan interaction). Full CGB HDMA (general + HBlank).
4. VRAM/OAM access blocking in modes 2 and 3 via `Mmu` lockout flags driven by `Ppu.Mode`.
5. Single-copy framebuffer pipeline carries real pixels.
6. CGB specifics: VRAM banking, WRAM banking, double-speed mode, palettes, BG attribute map, sprite CGB attributes.
7. Razor additions: `VramView`, `PaletteView`, `OamView`, `MemoryView`.
8. Debugger: `readMemory` capability enabled and advertised.
9. Layer 3 tests: `dmg-acid2` and `cgb-acid2` pass with pixel-perfect framebuffer comparison against reference PNGs.

**Exit criteria:**

- `dmg-acid2` and `cgb-acid2` pass with real rendering driven by the empirical CPU subset. **This proves PPU correctness for the acid2 reference shapes only.** It does not claim general rendering correctness for arbitrary CPU-driven programs — that is Phase 3's responsibility.
- No hand-crafted VRAM harness is used for the exit test; the PPU is driven by the acid2 ROMs via the partial CPU exactly as they would run on real hardware. (Hand-crafted VRAM tests are used elsewhere in the PPU test suite for isolated unit tests.)
- Phase 2 benchmark passes the §12.9 Phase 2 gate across all environments.
- `readMemory` works from the VS Code Variables panel; developers can inspect memory at arbitrary addresses during the paused state.

### Phase 3 — SM83 CPU instructions

Emulator runs real code.

1. Complete SM83 instruction set via M-cycle micro-op tables: 256 unprefixed + 256 CB-prefixed, HALT + STOP, EI delay slot, HALT bug, interrupt dispatch timing.
2. Full Joypad IRQ.
3. Full Timer (IRQ edge cases, TMA reload delay).
4. Test ROMs in Layer 3: Blargg cpu_instrs (all 11), instr_timing, mem_timing, mem_timing-2, halt_bug, interrupt_time. Mooneye `acceptance/` subset (bits, timer, interrupts, oam_dma).
5. Debugger: breakpoints actually halt, `next` / `stepIn` / `stepOut` work, `stackTrace` (heuristic), `scopes/variables` (Symbols + Source Context), `disassemble`, `evaluate`, `breakpointLocations`, `setInstructionBreakpoints`, `setFunctionBreakpoints`, custom `koh.stepTCycle`. Capability advertising updated.

**Exit criteria:** all Blargg CPU/timing test ROMs pass. Setting a breakpoint in an `.asm` file and hitting it via F5 works end-to-end. Source-level step-over, step-in, step-out behave correctly on real code. Phase 3 benchmark passes.

### Phase 4 — Peripherals & polish

1. **APU** — four channels, mixing, WebAudio output. Blargg dmg_sound all 12 sub-tests pass.
2. **Serial** — full link-cable stub with Serial IRQ.
3. **Save states** — implement `WriteState` / `ReadState` per the §7.11 design constraint. UI for save/load in standalone mode; DAP restart restores state for debug mode.
4. **Battery-backed SRAM persistence** — standalone: browser IndexedDB; debug: `.sav` next to ROM.
5. **VS Code-native memory view.** Registers `memoryReference` fields in variables responses, enabling VS Code's built-in Hex Editor integration to open memory views for RAM/ROM regions. This is distinct from the in-webview `MemoryView` Razor component shipped in Phase 2: the Phase 2 component lives inside the emulator webview; this Phase 4 feature integrates with VS Code's own hex editor via DAP `memoryReference`.
6. **`writeMemory` DAP** capability with the debug-write contract from §7.10.
7. **Watchpoints** — data breakpoints via `MemoryHook` plug-in (architecture reserved from Phase 1; implementation here). Accepts the performance cost documented in §7.8 rule 4.
8. **Conditional breakpoints** — expressions evaluated via `evaluate` at breakpoint hit.
9. **Hit-count breakpoints.**
10. **Mbc3 (with RTC), Mbc5** cartridge support.
11. **Manual real-game verification.** Each game has a specific, repeatable checklist that a reviewer can execute in under 15 minutes.

**Real-game verification checklists.**

- **Tetris (DMG):** title screen renders; music plays at the correct tempo; A-type game starts; pieces fall and lock; line clears update the score; sound effects on line clear and game over play; game over screen reached and reset returns to title screen.
- **Pokémon Blue (DMG):** intro cutscene plays without graphical glitches; title screen music plays; "New Game" enters Oak's lab; player naming screen accepts input; first battle versus rival completes through at least three turns; save-to-SRAM works; reload from SRAM resumes at the saved location.
- **Pokémon Gold (CGB):** intro cutscene plays in CGB color; title screen; new game reaches Professor Elm's lab; CGB palette colors match reference screenshots in `fixtures/reference/pokemon-gold/`; save-to-SRAM with RTC works; reload after RTC clock advance shows time-of-day change in the intro.
- **Super Mario Land 2 (DMG):** title screen; world map visible; first level playable; Mario moves, jumps, collects coins; enemies animate; pause menu opens; death returns to the world map.
- **Link's Awakening DX (CGB):** CGB title screen renders in color; Link's house loads; character movement works; Tarin speaks via text box; first screen transition loads adjacent room; CGB palette transitions at dungeon entry work.

A verification run requires all five games to pass their checklists before Phase 4 exit. Failures are documented as bugs, fixed, and the verification re-run.

**Exit criteria:**

- All five real-game checklists pass.
- Blargg dmg_sound all 12 sub-tests pass.
- APU audio plays at correct pitch and timing.
- Phase 4 benchmark passes the §12.9 Phase 4 gate across all environments.
- Save-state works round-trip (save, modify execution, load, verify state matches).
- Watchpoints halt execution on the correct memory access with no more than 15% throughput loss on a program using 3 watchpoints.

### Phase 5 — Optional / future

- **MAUI Blazor Hybrid desktop shell.** Native-WebView wrapper around `Koh.Emulator.App`. Most UI and all emulator logic are reused from the Blazor project. Platform-specific code is required for: file system access (ROM/save file I/O), audio output backend integration, window lifecycle, menu integration, and platform packaging. This is not "zero duplication" — it is "significant reuse, modest per-platform code."
- Publish standalone site as a Koh playground.
- Link cable emulation between two instances for multiplayer debugging.
- Trace logging for profile-guided optimization of user code.
- `Koh.Lsp` integration for "where is PC in my source" highlighting during a debug session.
- **Time-travel / reverse execution** — separate future design. Out of scope for this spec.

## Resolved design decisions

The previous draft left open questions. They are resolved here:

1. **Benchmark threshold per phase.** Resolved in §12.9: 2.0× / 1.5× / 1.3× / 1.1× per phase, measured in Blazor WASM AOT against representative workloads.
2. **MBC support in Phase 1.** Resolved in §7.12 and Phase 1 plan: `RomOnly` + `Mbc1` only.
3. **`.kdbg` version mismatch.** Resolved in §9.9: hard error with clear message pointing at the expected version. No best-effort load.
4. **Bundled Blazor app location.** Resolved in §11.6: `editors/vscode/dist/emulator-app/`, gitignored, packaging-pipeline-authoritative, freshness-checked.
5. **Dev host sample ROMs.** Resolved in §12.10: downloaded by script with SHA-256 verification, cached by CI, not gitignored optional.
6. **Test ROMs in CI.** Resolved in §12.10: mandatory, CI fails on fixture unavailability.
7. **Keyboard mapping persistence.** Resolved: browser localStorage in standalone mode, VS Code workspace setting in debug mode.
8. **`Koh.Lsp` debug integration.** Deferred to Phase 5; not in scope for this spec.
9. **Emulator runtime errors during a debug session.** Resolved in §11.9: caught by `ExecutionLoop`, posted as fatal error, converted to DAP `output` + `terminated`.
10. **Macro expansion stack display.** Resolved in §8.5: presented as a dedicated "Source Context" variable group, **not** as DAP stack frames. Call stack and expansion stack are distinct.
11. **Stepping granularity.** Resolved in §8.7: `statement` = assembly line, `instruction` = single SM83 instruction. T-cycle stepping is a custom reverse request from the webview, not a DAP granularity.
12. **Cancellation ownership.** Resolved in §7.8 and §7.9: host concern. No `CancellationToken` in core APIs; the core uses `RunGuard.RequestStop()`.
13. **`RunUntil` delegate.** Resolved in §7.9: structural `StopCondition` struct, checked at instruction boundaries.
14. **Debug peek/poke semantics.** Resolved in §7.10.
15. **Banked-address breakpoints.** Resolved in §8.3.
16. **Source mapping for banked ROM.** Resolved in §8.4.
17. **Call stack vs expansion stack.** Resolved in §8.5.
18. **DAP capabilities per phase.** Resolved in §8.7.
19. **`writeMemory` scope.** Deferred to Phase 4 per §8.7 and §7.10; Phase 1 does not advertise or implement it.
20. **Transport reliability.** Resolved in §11.9.
21. **Multi-target launch.** Resolved in §11.4.1.
22. **Frame pacing.** Resolved in §10.3.
23. **Framebuffer single-copy transfer.** Resolved in §10.4 (not "zero-copy" — corrected naming).
24. **`.kdbg` coalescing and dedup.** Resolved in §9.6 and §9.7.
25. **Scope identity.** Resolved in §9.4 via the scope table.
26. **`.sym` divergence.** Resolved in §9.10: `.sym` is export-only, `.kdbg` is debugger-authoritative.
27. **Watchpoints architecture.** Resolved in §7.1 and §7.9: `MemoryHook` is reserved from Phase 1, implemented in Phase 4.
28. **Save-state determinism.** Resolved in §7.11.
29. **ROM compatibility targets.** Resolved in §3 with the explicit test-ROM and real-game lists.
30. **"100% hardware accurate" wording.** Replaced with a scoped accuracy statement in §3.

### Design risks (not fully resolved, validated during implementation)

These are acknowledged as risks rather than claimed as resolved. Each will be validated (or the design adjusted) at the point it becomes load-bearing during implementation.

1. **STOP opcode exact behavior.** §3 commits to a narrow definition (joypad wake + CGB speed switch). If a gated test ROM depends on additional STOP quirks (DIV reset on STOP, interrupt-blocking during STOP, 2-byte encoding behavior), §3 narrows further or the implementation extends. Risk is low: no current gated ROM requires more.
2. **Timer / divider behavior in CGB double-speed edge cases.** §7.2 describes the correct doubling behavior of the system counter. Edge cases like TIMA overflow-reload coinciding with a speed switch are not explicitly specified. The Phase 3 Mooneye timer tests are expected to surface any remaining edge cases; if they do, the implementation is adjusted to match hardware.
3. **Exact acid2 opcode subset.** §Phase 2 defines this empirically by disassembly. The list is not known until Phase 2 begins. Risk: if acid2 requires CGB-specific instructions or interrupts we have not planned for, Phase 2 scope grows slightly.
4. **Path canonicalization on edge filesystems.** §9.3 and §8.4 define normalization for case-insensitive filesystems and Windows paths. Less common cases (Unicode normalization forms on macOS APFS, WSL path translations, UNC paths) are not explicitly handled. Known issue to be tested against real workspaces on each platform during Phase 1 end-to-end testing.
5. **`Cartridge` enum-dispatch performance in WASM AOT.** §7.5 commits to measuring and falling back to alternatives if the baseline implementation fails the Phase 1 benchmark. This is a real unknown: the JIT behavior on .NET 10 Blazor WASM AOT is not a known quantity for this specific pattern.
6. **Exact interplay of debug peek/poke with live patches and save states.** §7.10 and §7.11 describe the contract, but the interaction across save → patch → reload sequences is subtle. Phase 4 implementation will produce edge cases worth documenting and may require contract refinement.
7. **Phase 1 benchmark representativeness.** The Phase 1 mock CPU workload (§12.9) is this author's best guess at "representative of a real CPU's tick cost." If the real CPU in Phase 3 ends up significantly more expensive per tick than the mock (e.g., due to instruction decode costs we did not anticipate), the Phase 3 benchmark will catch it, but it may retroactively invalidate the Phase 1 gate.
8. **Request-stop latency during step-out across many instructions.** Step-out walks forward until the stack pops back above a recorded depth; this can take many thousands of instructions. `RunGuard.RequestStop()` is checked per instruction (§7.9) so pause latency is bounded, but a truly runaway loop could still feel unresponsive. Phase 3 implementation may need to add a hard "max instructions before forced check" safety valve.

These risks are **acceptable for design approval** — they are bounded, and each has a concrete phase at which it will be resolved or the design will be revised.
