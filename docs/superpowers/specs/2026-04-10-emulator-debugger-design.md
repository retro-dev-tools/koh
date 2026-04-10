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

- **Instruction set:** every documented SM83 opcode plus the HALT bug and STOP semantics.
- **CPU timing:** correct M-cycle count per instruction, correct bus-access timing within each M-cycle, correct EI-delay-slot semantics, correct interrupt dispatch timing (5 M-cycles from trigger to first instruction of ISR).
- **Memory bus:** correct routing across all regions ($0000–$FFFF), correct VRAM/OAM access blocking during PPU modes 2 and 3, correct behavior of the prohibited region $FEA0–$FEFF, correct echo RAM mirroring.
- **Cartridges (MBCs):** RomOnly, MBC1, MBC3 (including RTC), MBC5. Bank switching, RAM enable/disable, RTC latch and tick.
- **Timer:** DIV at the correct rate, TIMA at the correct rate per TAC, TMA reload with the documented 1-M-cycle reload delay, correct IRQ raising.
- **PPU:** the pixel-FIFO fetcher model (see §7.7). Correct mode 2/3/0/1 timing, variable mode 3 length from SCX mod 8, window enable, sprite penalties, and window restart. Correct LY / LYC / STAT interrupt sources. DMG BGP/OBP0/OBP1 palette. CGB BG/OBJ palette RAM with auto-increment.
- **DMA:** OAM DMA with correct CPU access restrictions during transfer. CGB HDMA general-purpose and HBlank modes.
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
│   ├── IStatefulComponent.cs     // cold-path interface for save-state only
│   └── StateVersion.cs
└── Debug/
    ├── MemoryHook.cs              // cold-path; watchpoint / trace plug-in
    └── RunGuard.cs                // volatile stop-request flag
```

Phase 1 includes only `RomOnly` and `Mbc1` in `Cartridge.cs`. Other mappers are added alongside Phase 4 peripheral work. The type layout above does not imply Phase 1 shipping beyond those two.

### 7.2 Clocking model

There is one authoritative clock: the **CPU T-cycle** at 4.194304 MHz (single-speed) or 8.388608 MHz (CGB double-speed). The PPU operates on its own real-hardware cadence at 4.194304 MHz of "dots" regardless of CPU mode.

The implementation uses a single internal counter stepping at the **PPU dot rate** (4.194304 MHz, equivalently 1 T-cycle of the DMG CPU). Each tick of the counter:

- Advances the PPU by one dot.
- Advances the Timer and DMAs by one T-cycle of the DMG CPU clock.
- Advances the CPU by one T-cycle in single-speed mode, or two T-cycles in double-speed mode.

The invariant: **the PPU's wall-clock rate is identical in both DMG and CGB single-/double-speed modes.** Only the CPU, Timer, OAM DMA, and HDMA speed up in double-speed. This matches the hardware: the crystal runs at ~8.4 MHz; CGB double-speed exposes that full rate to the CPU, while the PPU's internal divider keeps it at its normal cadence.

In code:

```csharp
public void StepOneSystemTick()          // one "dot" at 4.194304 MHz
{
    ppu.TickDot();
    timer.TickT();
    oamDma.TickT();
    if (hdmaActive) hdma.TickT();

    // CPU speed depends on double-speed mode
    cpu.TickT();
    if (clock.DoubleSpeed) cpu.TickT();
}
```

A frame is 70224 system ticks (= 154 scanlines × 456 dots). `GameBoySystem.RunFrame()` loops until 70224 system ticks have elapsed or a stop condition fires.

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

When a micro-op performs a memory access, the CPU consults the `Mmu.AcquireBus(address)`. If the bus is owned by the OAM DMA and the address is outside HRAM, the read returns $FF; writes are dropped. This matches hardware behavior without stalling the CPU (the hardware does not stall either — reads simply return $FF).

Interrupt service (5 M-cycles: 2 internal, 2 stack writes, 1 PC reload) is itself a micro-op sequence inserted between instructions when IME=1 and `(IF & IE & 0x1F) != 0`.

The HALT state is a specific instruction-like micro-op that loops until a pending interrupt satisfies wake-up, with the HALT bug (IME=0 + pending IRQ) implemented as a one-shot PC-decrement flag on wake-up.

### 7.5 Memory bus and MBC dispatch

The bus path is unconditionally hot. The design uses **no interface calls on bus accesses**.

`Cartridge` is a sealed concrete class holding all state for all supported mappers plus a `MapperKind` enum. `ReadRom`, `ReadRam`, `WriteRom` (which mapper registers interpret as bank select), and `WriteRam` are sealed non-virtual methods that dispatch on the enum via a switch. The JIT devirtualizes perfectly; the cost is one predictable branch per memory access.

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

**OAM DMA** (triggered by write to $FF46):

- Source: `value << 8`, i.e., $0000–$F100 in increments of $100.
- Duration: 160 M-cycles (640 T-cycles). One byte per M-cycle.
- Start delay: 1 M-cycle between the trigger write and the first byte transfer.
- Destination: $FE00–$FE9F (OAM).
- During transfer (excluding the 1-M-cycle delay and the cycle after the last byte):
  - CPU reads from **any region except HRAM** return $FF.
  - CPU writes to any region except HRAM are dropped.
  - CPU instructions execute normally; the CPU is not halted, only the bus is contended.
  - The CPU *can* safely execute code from HRAM (this is why every real game's DMA routine is HRAM-resident).
  - IRQs are still serviced if IME=1.
- OAM is written by the DMA engine, not the CPU. PPU OAM-scan during mode 2 reads the OAM that was written so far.

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

**Scanline structure** (456 dots per scanline):

- **Mode 2 (OAM scan):** fixed 80 dots. The PPU scans OAM for sprites on the current scanline. VRAM accessible by CPU; OAM not accessible by CPU.
- **Mode 3 (Drawing):** variable 172–289 dots. Neither VRAM nor OAM accessible by CPU. Mode 3 length is `172 + (SCX mod 8) + sprite penalty + window penalty`.
  - `SCX mod 8` adds 0–7 dots.
  - Each visible sprite on this scanline adds 6 dots, plus an additional penalty based on the sprite's X position mod 8 (0–5 extra dots, per hardware measurement).
  - Enabling the window mid-scanline adds 6 dots and restarts the fetcher.
- **Mode 0 (HBlank):** the remainder of 456 dots. Both VRAM and OAM accessible.
- **Mode 1 (VBlank):** scanlines 144–153, 456 dots each. Both VRAM and OAM accessible.

**Fetcher state machine** (8 dots per full BG tile fetch):

1. Get Tile (2 dots)
2. Get Tile Data Low (2 dots)
3. Get Tile Data High (2 dots)
4. Push to FIFO (2 dots, retries until FIFO has room)

The background FIFO holds up to 16 pixels. When it contains more than 8, one pixel is shifted out per dot into the LCD. If a sprite appears at the current pixel position, the sprite fetcher preempts the BG fetcher for 6 base dots (plus position penalty) and mixes sprite pixels into a parallel sprite FIFO; per-pixel priority resolves BG vs sprite.

**Window activation** during mode 3:

- When `LCDC.5 = 1` and `WX - 7 ≤ current X`, the window triggers: BG fetcher is reset, fetcher address switches to the window tile map, and the BG FIFO is cleared. A penalty of 6 dots is added to mode 3.

**CGB specifics:**

- BG attribute map (VRAM bank 1) adds priority, palette index, tile VRAM bank, H/V flip.
- Sprite attributes gain CGB palette index (bits 0–2) and VRAM bank bit.
- Master priority bit in LCDC.0 swaps meaning on CGB.
- Palette RAM ($FF68–$FF6B for BG, $FF6A–$FF6B for OBJ) with auto-increment on write.

**STAT IRQ sources** (edge-triggered via internal OR): LY=LYC, mode 0, mode 1, mode 2. The well-known STAT bug (spurious IRQs on certain mode transitions when multiple sources are enabled) is modeled.

### 7.8 Performance rules (hot path)

All rules derive from the Blazor WASM AOT constraint: virtual dispatch, interface calls, allocations, LINQ, delegates, and boxing on the tick path are significantly more expensive than in native .NET. Therefore:

1. All hot-path types are `sealed`. `GameBoySystem.StepOneSystemTick()` calls concrete `Cpu`, `Ppu`, `Timer`, etc. directly — no polymorphic component base class.
2. No interfaces on hot paths. `Mmu` is concrete. `Cartridge` is concrete with enum-dispatched mapper logic (§7.5). `ICartridge` does not exist.
3. No allocations per tick. Micro-op tables are pre-built once at construction. Instruction state uses indices into static arrays, not object references.
4. No LINQ, no delegates, no closures per tick. Diagnostic hooks (§7.10, `MemoryHook`) are explicit object references checked once per memory access and set to null on the hot-path when disabled.
5. Ephemeral state as `struct` (`StepResult`, `JoypadState`, `CpuSnapshot`) — passed by `in` or `ref` where reasonable.
6. No threading in the core. Frame loop, pacing, and cancellation live in `Koh.Emulator.App`. The core is synchronous.
7. No `CancellationToken` in the core API. The host uses `RunGuard.RequestStop()` (§7.9) to signal stop; the core checks the flag at instruction boundaries inside `RunFrame` and `RunUntil`.
8. `IStatefulComponent` exists only for save-state, which is cold-path (user-triggered, not per-tick).

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
    public bool StopOnVBlank { get; init; }
    public bool StopOnReturn { get; init; }       // used by step-out
}

[Flags]
public enum StopConditionKind : uint
{
    None         = 0,
    PcEquals     = 1 << 0,
    PcInRange    = 1 << 1,
    MaxCycles    = 1 << 2,
    VBlank       = 1 << 3,
    Return       = 1 << 4,
}

public readonly record struct StepResult(
    StopReason Reason,
    ulong TCyclesRan,
    ushort FinalPc);
```

`StopCondition` is a struct passed by `in`; no delegate invocation on the hot path. `RunUntil` checks the condition at each **instruction boundary** (not per-tick), so the overhead is amortized over ~8–16 T-cycles.

### 7.10 Debug peek/poke contract

- **`DebugReadByte(addr)`** returns the raw underlying memory content regardless of CPU access restrictions. VRAM during mode 3 returns the actual byte (the CPU would see $FF). Reads from MBC-banked regions use the currently selected bank. No side effects. No cycle cost. Legal at any time; values observed while the emulator is running are not atomic with the tick loop and may be inconsistent.
- **`DebugWriteByte(addr, value)`** is legal **only while the system is paused** (i.e., not inside `RunFrame` / `StepInstruction` / `RunUntil`). Returns `false` and performs no write if called while running.
  - Writes to RAM regions (WRAM, HRAM, VRAM, OAM, external RAM) update memory directly.
  - Writes to read-only ROM regions update the backing buffer (allowing edit-in-place during debug).
  - Writes to I/O registers that would trigger side effects (e.g., $FF46 OAM DMA, $FF55 HDMA, $FF50 boot ROM disable) **do not trigger the side effect** — they update the register value only. Developers who want the side effect run the emulator one step and let the CPU do it.
- Debug reads/writes never raise `MemoryHook` callbacks.

### 7.11 Save-state contract

Every component exposes `WriteState(Stream)` / `ReadState(Stream)` via `IStatefulComponent`. The serialized form captures all internal state needed for byte-for-byte determinism, including:

- CPU: registers, IME, IME-delay latch, HALT state, current instruction pointer + M-cycle + T-cycle-in-M-cycle.
- PPU: LY, dot within scanline, mode, fetcher state, BG FIFO, sprite FIFO, current sprite list, STAT IRQ line latch.
- Timer: DIV, TIMA, TMA, TAC, TIMA-reload-delay counter.
- DMAs: active flag, current byte index, source/destination, HDMA mode, HBlank trigger flag.
- Cartridge: all mapper state (bank, RAM enable, RTC latch+time).
- Bus: nothing (it's stateless).
- All memory arrays.

State files carry a version number (`StateVersion`) and a hash of the ROM for safety. Loading a state with a mismatched ROM hash is an error.

Determinism requirement: given two `GameBoySystem` instances constructed from the same state and supplied the same `Joypad` inputs at the same tick positions, their subsequent execution is byte-identical. This requirement exists now so component authors keep it in mind; save-state UI ships in Phase 4.

### 7.12 Subsystem phasing

Explicit per-subsystem phase map so none of this is ambiguous:

| Subsystem | Phase 1 | Phase 2 | Phase 3 | Phase 4 |
|---|---|---|---|---|
| CPU | Skeleton, no opcodes, mock tick | NOP + JR + LD to let PPU tests run | Full SM83 | — |
| PPU | Blank framebuffer | Full pixel-FIFO fetcher | — | — |
| MMU | Basic routing, RomOnly/Mbc1 | + VRAM/OAM blocking | + full bus contention | — |
| Timer | Skeleton | — | Full | — |
| OAM DMA | Skeleton | Full | — | — |
| HDMA | — | CGB: full | — | — |
| Cartridge | RomOnly, Mbc1 | — | — | Mbc3 (RTC), Mbc5 |
| Joypad | Buffer (no IRQ) | — | + Joypad IRQ | — |
| APU | Absent | Absent | Silent stubs counting cycles | Full four channels + WebAudio |
| Serial | 1-byte buffer | — | — | Full link-cable IRQ |
| Interrupts | IF/IE/IME latches | + VBlank + STAT | + Timer + Joypad | + Serial |

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

Source-file identity in `.kdbg` is canonical: the linker emits absolute paths resolved against the workspace root, which the debugger compares byte-for-byte against the paths VS Code provides in `setBreakpoints`. A workspace-relative-path normalization table handles case-insensitive filesystems (Windows, macOS default).

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
        var result = emulator.RunFrame();
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

`Task.Yield()` between frames is what allows DAP `pause` requests to be processed while the loop is running — the JS event loop runs pending messages during the yield. `session.PauseRequested` is a volatile bool flipped by the pause handler, which also calls `emulator.RequestStop()` in case we're inside a long `RunUntil`.

### 8.7 DAP capabilities by phase

The capability set advertised in `initialize` is phase-dependent. Advertising a capability before it actually works is prohibited. The `Capabilities` class switches on the current build phase:

| Capability / Request | P1 | P2 | P3 | P4 |
|---|---|---|---|---|
| `initialize` / `launch` / `configurationDone` | ✓ | | | |
| `continue` / `pause` / `terminate` | ✓ | | | |
| `setBreakpoints` (stored, not yet hitting) | ✓ | ✓ | | |
| `setBreakpoints` (hitting) | | | ✓ | |
| `setInstructionBreakpoints` | | | ✓ | |
| `setFunctionBreakpoints` (symbol-based) | | | ✓ | |
| `next` / `stepIn` / `stepOut` | | | ✓ | |
| `stepping granularity: statement` | | | ✓ | |
| `stepping granularity: instruction` | | | ✓ | |
| `stackTrace` (heuristic call stack) | | | ✓ | |
| `scopes` / `variables` (Registers, Hardware) | ✓ | ✓ | ✓ | ✓ |
| `scopes` / `variables` (Symbols) | | | ✓ | ✓ |
| `scopes` / `variables` (Source Context) | | | ✓ | ✓ |
| `readMemory` | | ✓ | ✓ | ✓ |
| `writeMemory` | | | | ✓ |
| `disassemble` | | | ✓ | ✓ |
| `evaluate` (literals + symbols) | | | ✓ | ✓ |
| `breakpointLocations` | | | ✓ | ✓ |
| Conditional breakpoints | | | | ✓ |
| Hit-count breakpoints | | | | ✓ |
| Data breakpoints (watchpoints) | | | | ✓ |
| `exceptionInfo` (for emulator exceptions) | ✓ | ✓ | ✓ | ✓ |
| Custom: `koh.stepTCycle` | | | ✓ | ✓ |
| Custom: `koh.framebufferReady` | ✓ | ✓ | ✓ | ✓ |
| Custom: `koh.cpuState` | ✓ | ✓ | ✓ | ✓ |

T-cycle stepping is exposed as a **custom DAP reverse request** (`koh.stepTCycle`) sent from the emulator webview's toolbar rather than as an extension of DAP's `stepping granularity` (which is designed around source-language concepts). This avoids the contradiction of overloading `granularity: instruction` to mean "sub-instruction," and keeps the T-cycle control visible in the webview where developers iterating on emulator internals will look for it.

The extension reads advertised capabilities from the debugger's `initialize` response and gates UI affordances accordingly.

## `.kdbg` debug info format

The linker emits `.kdbg` alongside `.gb` and `.sym`. `.sym` remains unchanged and is output-compatibility-only — see §9.10.

### 9.1 Header (exact layout, 32 bytes, little-endian)

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | Magic | ASCII `"KDBG"` |
| 4 | 2 | Version | u16; v1 = initial |
| 6 | 2 | Flags | u16; bit 0 = contains expansion data; bit 1 = contains scope table; bits 2–15 reserved |
| 8 | 4 | StringPoolOffset | byte offset from file start |
| 12 | 4 | SourceTableOffset | byte offset |
| 16 | 4 | ScopeTableOffset | byte offset; 0 if absent |
| 20 | 4 | SymbolTableOffset | byte offset |
| 24 | 4 | AddressMapOffset | byte offset |
| 28 | 4 | ExpansionPoolOffset | byte offset; 0 if absent |

All integer fields are unsigned little-endian. Offsets are absolute from the start of the file. Sections appear in the order header → string pool → source table → scope table → symbol table → address map → expansion pool, but the header offsets are authoritative: readers must not assume order.

### 9.2 String pool

```
u32 count
repeated count times:
    u16 length (bytes)
    bytes[length]  (UTF-8, not null-terminated)
```

String IDs are 0-based indices into this table. ID 0 is reserved as "no string."

### 9.3 Source file table

```
u32 count
repeated count times:
    u32 pathStringId    (references string pool)
```

Source-file IDs are 0-based indices into this table. ID 0 is reserved as "no file."

### 9.4 Scope table (new since first draft)

```
u32 count
repeated count times:
    u8  kind            (0=Global, 1=LocalToLabel, 2=MacroLocal, 3=File)
    u8  reserved
    u16 reserved
    u32 parentScopeId   (0 if root; 0-based index into this table)
    u32 nameStringId    (display name)
```

Scope IDs are 0-based indices into this table. Each entry is exactly 12 bytes. ID 0 is reserved as "global scope."

Structural scopes prevent symbol-name collisions between macro-local labels and similarly-named globals from corrupting the debugger's symbol lookup. Each symbol references its containing scope by ID, not by scope name.

### 9.5 Symbol table

```
u32 count
repeated count times (24 bytes each):
    u8  kind                    (0=Label, 1=EquConstant, 2=RamLabel, 3=Macro, 4=Export)
    u8  bank
    u16 address
    u16 size                    (0 if unknown)
    u16 reserved
    u32 nameStringId
    u32 scopeId                 (0 = global scope)
    u32 definitionSourceFileId
    u32 definitionLine
```

Entries are 24 bytes, naturally aligned. Sorted by `(bank, address)` for nearest-match symbolification of disassembly.

### 9.6 Address map

```
u32 count
repeated count times (16 bytes each, sorted by (bank, address)):
    u8  bank
    u8  byteCount               (1..255 ROM bytes covered by this entry)
    u16 address
    u32 sourceFileId
    u32 line
    u32 expansionStackOffset    (absolute byte offset into ExpansionPool, or 0xFFFFFFFF = none)
```

16 bytes exactly with natural alignment.

**Coalescing policy:** during emission, contiguous ROM bytes sharing `(bank, sourceFileId, line, expansionStackOffset)` are merged into a single entry with `byteCount` up to 255. The assembler/linker emits AddressMapEntries with `byteCount = 1` initially; the builder's `FinalizeCoalesce()` step walks the entries in sorted order and merges mergeable runs. Coalescing typically reduces the address map size by ~80% on real code.

Lookups use binary search over `(bank, address)`; for addresses within a coalesced range, the binary search finds the predecessor whose `[address, address + byteCount)` contains the query.

### 9.7 Expansion pool

```
header:
    u32 byteSize                (total byte size of the pool payload that follows)
payload:
    sequence of expansion stacks at self-describing offsets:
        u16 depth
        repeated depth times (8 bytes each):
            u32 sourceFileId
            u32 line
```

An `expansionStackOffset` in the address map is an absolute byte offset into the pool payload, pointing at a `u16 depth` header. The sentinel `0xFFFFFFFF` means "no expansion" (the AddressMapEntry's `sourceFileId`/`line` are the final source location; no macro chain).

**Deduplication:** during emission, identical expansion stacks share the same offset. The builder maintains a hash index of stack contents; a new stack that matches an existing one reuses the existing offset. This is what makes `.kdbg` tractable for macro-heavy projects like Azure Dreams GBC.

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
│   ├── FramebufferBridge.cs         // zero-copy framebuffer JS interop
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

### 10.4 Framebuffer data transfer (zero-copy)

Base64 encoding and per-frame object allocation are rejected. The framebuffer pipeline is:

1. On Blazor startup, JS interop allocates a persistent `Uint8ClampedArray` of length 160×144×4 = 92 160 bytes and a persistent `ImageData` wrapping it. These live for the lifetime of the webview.
2. `Framebuffer.Pixels` in `Koh.Emulator.Core` is a `Memory<byte>` backing-store writable by the PPU directly (no intermediate allocations per pixel or per frame).
3. After `RunFrame` completes, `FramebufferBridge.CommitAsync()` invokes a single JS interop call that copies the 92 160 bytes into the persistent `Uint8ClampedArray`.
   - The copy uses Blazor's efficient span-to-typedarray bridge (`IJSUnmarshalledRuntime.InvokeUnmarshalled` in .NET 7/8, or the equivalent marshaled span helper in .NET 10).
   - One copy per frame; no serialization, no encoding, no per-pixel JS calls.
4. The JS side calls `ctx.putImageData(persistentImageData, 0, 0)` on the canvas.

This yields one 92 KB memcpy per frame across the WASM↔JS boundary: ~5.5 MB/s, negligible.

An optimization available later: allocate the framebuffer's backing store in JS memory and expose it to WASM via `SharedArrayBuffer`, eliminating the copy. `SharedArrayBuffer` requires cross-origin isolation headers which VS Code webviews do not currently grant, so this optimization is for dev-host / MAUI only if it becomes necessary.

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

- **Production / Marketplace package:** always AOT-published. The `.vsix` build script runs `dotnet publish Koh.Emulator.App -c Release -p:RunAOTCompilation=true` and copies `bin/Release/net10.0/publish/wwwroot/_framework/` and related assets into `editors/vscode/dist/emulator-app/`. The `.vsix` packaging step verifies that `dist/emulator-app/` is fresh by comparing timestamps against the newest file under `src/Koh.Emulator.App/` and `src/Koh.Emulator.Core/` and `src/Koh.Debugger/`. Stale assets fail the build.
- **Local extension development (`npm run watch`):** non-AOT by default (`-p:RunAOTCompilation=false`), cutting build time from minutes to seconds. Debug builds still use Blazor WASM but skip AOT. The extension's debug launch depends on a watch task `watch-emulator-app` (`dotnet watch publish Koh.Emulator.App -c Debug -p:RunAOTCompilation=false`) that rebuilds on source change.
- **Dev-host override:** a VS Code setting `koh.emulator.devHostUrl` (e.g. `"http://localhost:5001"`) instructs `BlazorAssetLoader` to point the webview at a running `dotnet run` dev host instead of the bundled assets. This enables iterating on Razor components with hot reload while the extension is running. Only honored in extension development mode.
- **CI verification:** the extension CI job runs a dedicated step that builds `Koh.Emulator.App` with AOT, packages the `.vsix`, and runs a smoke test launching the packaged extension and verifying the Blazor app loads.
- **Freshness rule for contributors:** running `npm run watch` without the `watch-emulator-app` task yields stale assets; the extension logs a warning and the dashboard shows a "Blazor assets may be stale" banner.

### 11.7 Extension packaging

- Marketplace packages: prebuilt AOT assets are included in the `.vsix`. Package size target: ≤ 20 MB total.
- `dist/emulator-app/` is gitignored; the packaging pipeline is the source of truth.
- CI artifact caching retains the `dist/emulator-app/` directory across runs when only extension TypeScript changes, to avoid re-AOT-compiling on every CI run.
- A prepackaging script verifies freshness by comparing input and output timestamps. Out-of-date assets fail the package step.

### 11.8 Settings

```jsonc
"koh.emulator.showDashboard":  { "type": "boolean", "default": true },
"koh.emulator.scale":          { "type": "number", "enum": [1, 2, 3, 4], "default": 3 },
"koh.emulator.devHostUrl":     { "type": "string", "description": "If set, webview loads Blazor from this URL instead of bundled assets. For extension development only." },
"koh.debugger.logDapTraffic":  { "type": "boolean", "default": false, "description": "Log DAP messages to the Koh output channel" }
```

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

Each phase has a representative benchmark with explicit pass criteria. Benchmarks run in Blazor WASM AOT in a real browser (Edge and Chrome), not in native .NET (though native benchmarks exist in `Koh.Benchmarks` for regression detection). A phase cannot close unless its gate passes.

| Phase | Representative workload | Target (real-time multiplier) |
|---|---|---|
| **Phase 1** | Mock CPU: each "instruction" performs one memory read via `Mmu`, one ALU op on registers, one flag update, one branch check; takes 4 T-cycles. All real components tick (Timer, DMAs inactive, PPU ticking dot counter only). | ≥ 2.0× real-time |
| **Phase 2** | Real PPU (full pixel FIFO, tile data in VRAM, 40 sprites per scanline, window enabled) driving rendered output. Mock CPU from Phase 1. | ≥ 1.5× real-time |
| **Phase 3** | Real CPU running Blargg cpu_instrs sub-test 01 (special instructions), real PPU, timer, OAM DMA. | ≥ 1.3× real-time |
| **Phase 4** | Full system with APU, HDMA active, real commercial ROM (Pokémon Gold) running its title screen. | ≥ 1.1× real-time |

"Representative" means: the workload exercises the actual hot paths that will ship. A benchmark that runs an empty loop proves nothing; the Phase 1 mock CPU must touch memory and registers like a real CPU so the tick costs are measured honestly.

If a phase's gate fails, work stops and the cause is investigated before proceeding. Gates are not optional.

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

1. **`Koh.Emulator.Core` skeleton** per §7.1, §7.2, §7.3. `RomOnly` + `Mbc1` cartridges. Concrete `Mmu`. `Mock CPU` in `Sm83.cs` that implements a representative-workload instruction (§12.9 Phase 1). Empty `Ppu` (ticks the dot counter, framebuffer stays gray). Full `Timer` is a stretch but the skeleton is in place. OAM DMA skeleton (trigger register wired, byte transfer implemented). No HDMA. Full save-state serialization skeleton (even though nothing meaningful to save yet).
2. **`Koh.Emulator.Core.Tests`:** construction, memory read/write (all regions), ROM header parsing, `RunFrame` clock increment, mock CPU tick budget.
3. **`Koh.Linker.Core` `.kdbg` emission:** full format per §9, including coalescing and expansion pool deduplication. Linker tests round-trip.
4. **`Koh.Debugger` skeleton:**
   - `DapDispatcher`, `Capabilities` with Phase 1 set per §8.7.
   - Handlers: `initialize`, `launch`, `configurationDone`, `continue`, `pause`, `terminate`, `setBreakpoints` (stored, not yet hitting), `scopes` + `variables` (Registers + Hardware).
   - `DebugInfoLoader` parses `.kdbg` into `SourceMap` / `SymbolMap`.
   - Publishes `koh.cpuState` events from the mock CPU state.
   - Step handlers return "not supported" in Phase 1; capabilities do not advertise stepping.
5. **`Koh.Debugger.Tests`:** DAP JSON round-trip for implemented handlers.
6. **`Koh.Emulator.App` Phase 1 UI** per §10.5. Zero-copy framebuffer pipeline built now even though it transfers blank gray. Frame pacer with rAF. Runtime-mode detector and both shells. Sample-ROM download script and directory in `wwwroot/sample-roms/`.
7. **Phase 1 benchmark** per §12.9 Phase 1 row. Blocks Phase 2.
8. **VS Code extension refactor + debug integration:**
   - Decompose `extension.ts` per §11.1.
   - Register `koh` debug type with inline DAP adapter.
   - `ConfigurationProvider` + `TargetSelector` handling all cases in §11.4 (absent, incomplete, single target, multi-target).
   - `BuildTaskProvider` synthesizes build tasks per target.
   - `BlazorAssetLoader` with dev-host override.
   - `DapMessageQueue` with boot buffering.
   - Transport reliability rules §11.9.
9. **Extension build pipeline:**
   - `watch-emulator-app` task for non-AOT watch rebuild.
   - `.vsix` packaging step invoking AOT publish and freshness check.

**Exit criteria:**
- F5 on a `.asm` file builds the ROM, opens the webview, shows the dashboard with live mock-CPU state.
- Breakpoints can be set in source files and appear in `.kdbg`-resolved form in the debugger's breakpoint table (they do not yet halt anything).
- Debug toolbar's Pause actually pauses the run loop; Continue resumes.
- Stop terminates cleanly.
- Dev host runs. Standalone file picker loads a ROM (and then runs the mock CPU).
- Phase 1 benchmark passes 2× real-time with the representative workload.
- CI builds the extension package and the smoke test verifies the bundled Blazor app loads.

### Phase 2 — PPU & rendering

Full pixel-FIFO PPU rendering against VRAM contents. CPU is still mostly the mock CPU plus a minimal subset (`NOP`, `JR`, `LD A,n`, `LD (nn),A`) sufficient to run the acid2 ROMs.

1. Full `Ppu` implementation per §7.7.
2. OAM DMA complete; CGB HDMA general + HBlank.
3. VRAM/OAM access blocking in modes 2 and 3.
4. Minimal CPU opcode subset for acid2.
5. Framebuffer zero-copy pipeline carries real pixels.
6. CGB specifics: VRAM banking, WRAM banking, double-speed mode, palettes.
7. Razor additions: `VramView`, `PaletteView`, `OamView`, `MemoryView`.
8. Debugger: `readMemory` capability enabled.
9. Layer 3 tests: dmg-acid2 and cgb-acid2 pass.

**Exit criteria:** dmg-acid2 and cgb-acid2 pass with real rendering driven by a minimal CPU. The test proves PPU correctness. Integrated CPU-driven rendering correctness is not yet claimed; that is Phase 3.

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
3. **Save states** — UI for save/load in standalone mode; DAP restart restores state for debug mode.
4. **Battery-backed SRAM persistence** — standalone: browser IndexedDB; debug: `.sav` next to ROM.
5. **Memory viewer** in VS Code variables view with live update on step/break.
6. **`writeMemory` DAP** capability.
7. **Watchpoints** — data breakpoints via `MemoryHook` plugin (architecture reserved from Phase 1; implementation here).
8. **Conditional breakpoints** — expressions evaluated via `evaluate` at breakpoint hit.
9. **Hit-count breakpoints.**
10. **Mbc3 (with RTC), Mbc5** cartridge support.
11. **Manual real-game verification** on the list in §3.

**Exit criteria:** real commercial ROMs run correctly on the verification list. APU audio plays. Blargg dmg_sound passes. Phase 4 benchmark passes.

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
23. **Framebuffer zero-copy.** Resolved in §10.4.
24. **`.kdbg` coalescing and dedup.** Resolved in §9.6 and §9.7.
25. **Scope identity.** Resolved in §9.4 via the scope table.
26. **`.sym` divergence.** Resolved in §9.10: `.sym` is export-only, `.kdbg` is debugger-authoritative.
27. **Watchpoints architecture.** Resolved in §7.1 and §7.9: `MemoryHook` is reserved from Phase 1, implemented in Phase 4.
28. **Save-state determinism.** Resolved in §7.11.
29. **ROM compatibility targets.** Resolved in §3 with the explicit test-ROM and real-game lists.
30. **"100% hardware accurate" wording.** Replaced with a scoped accuracy statement in §3.

No items remain open at design-approval time.
