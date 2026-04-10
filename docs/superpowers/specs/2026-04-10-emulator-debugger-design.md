# Koh Emulator & Debugger Design

**Date:** 2026-04-10
**Status:** Design approved; implementation pending

## Goals

Give Koh users F5-to-debug from VS Code against a first-party, cycle-accurate Game Boy / Game Boy Color emulator. The emulator is built as a reusable Blazor WebAssembly component that runs in three modes: inside a VS Code webview (for debugging), as a standalone web app (for playing and sharing ROMs), and via a local dev host (for Koh contributors iterating on the emulator and UI).

Non-goals for this design:

- Emulating non-Game-Boy hardware.
- Automatic ROM hacking / patching tools.
- Cloud-hosted multiplayer link cable.

## Accepted trade-offs

The team has explicitly accepted the following constraints. They are documented here so the rationale is not lost.

1. **Blazor WebAssembly is a core dependency of `Koh.Emulator.App`.** `Koh.Emulator.Core` itself stays BCL-only and AOT-compatible, but the application shell takes on Blazor. This relaxes the earlier "ideally BCL-only" preference in exchange for a single emulator codebase that reuses across VS Code, web, and dev host.
2. **Blazor WASM AOT publish is required, not optional.** Without AOT, Mono-WASM throughput is far below what cycle-accurate tick-driven emulation needs. AOT publish times are longer and outputs are larger; that is the price.
3. **F5 cold-start is measurably slower than launching a native binary.** Loading the Blazor runtime in the VS Code webview takes ~1–2 seconds on first launch of a session. Subsequent launches reuse cached assets.
4. **Emulator bug-hunting happens through unit tests, not by attaching a debugger to a running Blazor WASM instance.** The `Koh.Emulator.Core.Tests` project is the primary development debugger.
5. **Both "debug-in-VS-Code" and "standalone" are first-class outputs of `Koh.Emulator.App`.** Neither is treated as a second-class fallback.
6. **100% hardware accurate means cycle-accurate, tick-driven.** Each T-cycle, every component advances exactly one step. Approaches that batch ticks per instruction or per scanline are rejected because they accumulate edge-case hacks when chasing the long tail of test-ROM correctness.

A sibling decision record at `docs/decisions/emulator-platform-decision.md` repeats the Blazor-vs-native-binary reasoning alongside the existing LSP AOT decision, for discoverability from outside the specs directory.

## High-level architecture

Three new projects, extensions to two existing ones.

**New:**

- `src/Koh.Emulator.Core/` — BCL-only class library. Pure emulator: `GameBoySystem`, `SystemClock`, `Sm83`, `Ppu`, `Mmu`, `Cartridge`, `Timer`, `OamDma`, `Hdma`, `Joypad`, `Apu`, `Serial`. No Blazor, no threads, no I/O. AOT-compatible.
- `src/Koh.Debugger/` — class library (not an executable). DAP message handlers in C#: breakpoint management, source ↔ address mapping, stepping state machines, scope/variable providers, disassembly, stack walking. References `Koh.Emulator.Core` + `Koh.Linker.Core` (for `.kdbg` parsing).
- `src/Koh.Emulator.App/` — Blazor WebAssembly project. Razor components for LCD display, debug dashboard, memory viewer, palette viewer, sprite viewer. References `Koh.Emulator.Core` + `Koh.Debugger`. Single compiled artifact, three runtime entry points.

**Extended:**

- `src/Koh.Linker.Core/` — adds `.kdbg` emission alongside existing `.gb` + `.sym`.
- `editors/vscode/` — adds debug type, inline DAP passthrough adapter, webview host for the Blazor app, build task provider. `extension.ts` is refactored into a facade over narrow subsystem modules.

### Three runtime entry points of `Koh.Emulator.App`

| Mode | Launched by | Role |
|---|---|---|
| **Debug-in-VS-Code** | F5 in VS Code → extension → webview | Primary user workflow — debugging Koh ROMs |
| **Standalone static site** | Published artifact on any static host, or MAUI Blazor Hybrid desktop shell | Playing / sharing ROMs without the Koh toolchain |
| **Dev host** | `dotnet run --project Koh.Emulator.App` → localhost | Koh contributors iterating on emulator and UI |

Runtime mode is detected once at startup via a JS interop probe (are we inside a VS Code webview?) with a query-string fallback. The Razor app adapts its shell accordingly. The dev host and standalone site share the same code path; only the hosting differs. In debug mode, `Koh.Debugger` is wired to the DAP transport; in standalone mode, the debugger components are inert and the UI exposes a file picker and playback controls instead.

### Data flow on F5

```
User presses F5
    │
    ▼
VS Code → KohConfigurationProvider (synthesizes from koh.yaml or uses launch.json)
    │
    ▼
preLaunchTask (koh task provider): koh-asm + koh-link → game.gb, game.sym, game.kdbg
    │
    ▼
VS Code opens debug session with inline DAP adapter (TypeScript passthrough)
    │
    ▼
Extension opens Koh Emulator webview, loads Blazor WASM app
    │
    ▼
Blazor detects "debug mode", initializes Koh.Debugger, loads ROM + .kdbg
    │
    ▼
VS Code DAP requests ── postMessage ─▶ Blazor WASM ─▶ Koh.Debugger handlers
                                                           │
                                                           ├── Koh.Emulator.Core (step/run)
                                                           └── Razor components (LCD, dashboard)
```

The TypeScript side of the extension never hosts emulator or debugger logic. It proxies DAP messages between VS Code and the webview; it registers VS Code API surfaces (debug type, task provider, webview); it orchestrates the Blazor app's build. All actual debug logic is C# inside Blazor WASM.

## `Koh.Emulator.Core`

### Type layout

```
Koh.Emulator.Core/
├── GameBoySystem.cs              // top-level façade; owns all components
├── HardwareMode.cs               // enum: Dmg, Cgb
├── SystemClock.cs                // T-cycle counter, frame budget tracking
├── Bus/
│   ├── IMemoryBus.cs             // cold-path interface for clarity
│   ├── Mmu.cs                    // memory map routing ($0000-$FFFF)
│   └── IoRegisters.cs            // $FF00-$FF7F dispatch
├── Cpu/
│   ├── Sm83.cs                   // sealed; CPU state + Tick() state machine
│   ├── CpuRegisters.cs           // A/F/B/C/D/E/H/L/SP/PC + flags
│   ├── Instruction.cs            // static micro-op tables per opcode
│   └── Interrupts.cs             // IF/IE/IME servicing
├── Ppu/
│   ├── Ppu.cs                    // sealed; PPU Tick() state machine
│   ├── PpuMode.cs                // HBlank / VBlank / OAMScan / Drawing
│   ├── Palette.cs                // DMG + CGB palette memory
│   └── Framebuffer.cs            // 160×144 RGBA, double-buffered
├── Cartridge/
│   ├── ICartridge.cs
│   ├── RomOnly.cs
│   ├── Mbc1.cs / Mbc3.cs / Mbc5.cs
│   └── CartridgeFactory.cs       // parses header, picks MBC
├── Timer/Timer.cs                // DIV / TIMA / TMA / TAC
├── Dma/OamDma.cs
├── Dma/Hdma.cs                   // CGB only
├── Joypad/Joypad.cs               // P1 register, buttons from host
├── Apu/Apu.cs                    // stubbed through Phase 3, full in Phase 4
├── Serial/Serial.cs               // buffer stub (enough for Blargg test output)
└── GameBoySystem.Step.cs          // RunFrame / StepInstruction / StepTCycle / RunUntil
```

### Tick model

`GameBoySystem.StepOneTCycle()` drives everything:

1. `cpu.Tick()` advances one T-cycle in its instruction state machine. Memory accesses may stall if DMA is active.
2. `ppu.Tick()` advances one dot (at CGB double-speed: every other T-cycle).
3. `timer.Tick()` increments DIV, checks TIMA overflow, raises IRQs.
4. `oamDma.Tick()` transfers one byte per M-cycle when active.
5. `hdma.Tick()` (CGB only) progresses general-purpose or HBlank DMA.
6. `clock.Cycles++`.

Components never call each other directly. They expose state through the bus. At any given cycle, each component has executed exactly the same number of ticks, which makes tick-driven correctness straightforward to reason about and unit-test in isolation.

### CPU instruction micro-ops

Rather than a giant per-opcode switch, each instruction is a list of micro-operations (`FetchByte`, `AluAdd`, `WriteBack`, `InternalDelay`, etc.) built once at startup into static arrays. The CPU state holds `(currentInstruction, microOpIndex)`. Each `Tick()` advances one micro-op. Memory accesses consult the bus for stall state so DMA and mid-instruction interrupt timing are correct without special cases.

### Performance rules (hot path)

All rules derive from the Blazor WASM AOT constraint: virtual dispatch, interface calls, allocations, LINQ, delegates, and boxing on the tick path are significantly more expensive than in native .NET. Therefore:

1. All hot-path types are `sealed`. `GameBoySystem.StepOneTCycle()` calls concrete `Cpu`, `Ppu`, `Timer`, etc. directly — no polymorphic component base class.
2. No interfaces on hot paths. `IMemoryBus` exists for API clarity at type boundaries, but the actual bus is a concrete `Mmu` called directly from components.
3. No allocations per tick. Micro-op tables are pre-built once at construction. Instruction state uses indices into static arrays, not object references.
4. No LINQ, no delegates, no closures per tick. Diagnostic hooks (optional CPU trace logging) are `Action<string>?` checked once per frame, not per tick.
5. Ephemeral state as `struct` (`StepResult`, `JoypadState`, `CpuSnapshot`) — passed by `in` or `ref` where reasonable.
6. No threading in the core. Frame loop, pacing, and cancellation live in `Koh.Emulator.App`. The core is synchronous — `RunFrame()` returns when the frame's T-cycles are exhausted or a stop condition fires. Blazor WASM is single-threaded anyway; this rule makes the design explicit.
7. `ICartridge` is a cold-path boundary (one call per memory access outside the fetch loop). We accept virtual dispatch there but seal the common MBC implementations.

### Public API surface

```csharp
public sealed class GameBoySystem
{
    public GameBoySystem(HardwareMode mode, ICartridge cart);
    public SystemClock Clock { get; }
    public CpuRegisters Registers { get; }
    public Framebuffer Framebuffer { get; }
    public JoypadState Joypad { get; set; }

    public StepResult RunFrame(CancellationToken ct);
    public StepResult StepInstruction();
    public StepResult StepTCycle();
    public StepResult RunUntil(Predicate<GameBoySystem> halt);

    public byte ReadByte(ushort address);           // debug peek, no cycle cost
    public void WriteByte(ushort address, byte value); // debug poke
}

public readonly record struct StepResult(
    StopReason Reason,
    ulong CyclesRan,
    ushort FinalPc);

public enum StopReason
{
    FrameComplete,
    InstructionComplete,
    Breakpoint,
    Watchpoint,
    HaltedBySystem,
    Cancelled
}
```

### Dependencies

BCL only. No logging library (diagnostic hooks are `Action<string>?`), no DI container, no config framework.

## `Koh.Debugger`

A class library hosted inside `Koh.Emulator.App`. No `StreamJsonRpc`, no `Microsoft.VisualStudio.Shared.VSCodeDebugProtocol` — neither plays well with Blazor WASM AOT. DAP JSON is handled with `System.Text.Json` source generation, which is AOT-safe.

### File layout

```
Koh.Debugger/
├── DebugSession.cs              // top-level; owns GameBoySystem + debug state
├── Dap/
│   ├── DapDispatcher.cs         // parses request JSON, routes to handlers
│   ├── DapJson.cs               // JsonSerializerContext source-gen
│   ├── Messages/                // request/response/event records
│   └── Handlers/
│       ├── InitializeHandler.cs
│       ├── LaunchHandler.cs
│       ├── SetBreakpointsHandler.cs
│       ├── ContinueHandler.cs
│       ├── NextHandler.cs
│       ├── StepInHandler.cs
│       ├── StepOutHandler.cs
│       ├── PauseHandler.cs
│       ├── StackTraceHandler.cs
│       ├── ScopesHandler.cs
│       ├── VariablesHandler.cs
│       ├── DisassembleHandler.cs
│       ├── ReadMemoryHandler.cs
│       └── EvaluateHandler.cs
├── Session/
│   ├── BreakpointManager.cs
│   ├── DebugInfoLoader.cs       // .kdbg → in-memory maps
│   ├── SourceMap.cs
│   ├── SymbolMap.cs
│   ├── CallStackWalker.cs
│   └── ExecutionLoop.cs         // frame-paced run loop, cooperative pause/break
└── Events/
    └── CustomDapEvents.cs       // koh.framebufferReady, koh.cpuState
```

### Transport abstraction

```csharp
public sealed class DapDispatcher
{
    public void HandleRequest(ReadOnlySpan<byte> jsonBytes);
    public event Action<ReadOnlyMemory<byte>>? ResponseReady;
    public event Action<ReadOnlyMemory<byte>>? EventReady;
}
```

Blazor wires `HandleRequest` to JS interop incoming messages and `ResponseReady` / `EventReady` to outgoing interop calls. The dispatcher only cares about byte buffers — no assumption about stdio vs postMessage. Unit tests feed raw JSON and assert the emitted responses.

### DAP requests implemented

| Request | Purpose |
|---|---|
| `initialize` | Capability handshake. Advertises `supportsConfigurationDoneRequest`, `supportsDisassembleRequest`, `supportsReadMemoryRequest`, `supportsSteppingGranularity`, `supportsBreakpointLocationsRequest`. |
| `launch` | Receives `{program, debugInfo, hardwareMode, stopOnEntry}`. Loads ROM + `.kdbg`, creates `GameBoySystem`, waits for `configurationDone`. |
| `setBreakpoints` | Per-source-file breakpoints. `SourceMap` translates `file:line` → ROM address(es). Multiple addresses possible when a macro is used in many places. |
| `setInstructionBreakpoints` | Raw-address breakpoints from the Disassembly view. |
| `configurationDone` | Starts the `ExecutionLoop`. |
| `continue` / `pause` | Resume/halt the run loop. |
| `next` / `stepIn` / `stepOut` | Step-over / step-in / step-out at instruction granularity. T-cycle stepping available via `granularity: instruction`. |
| `stackTrace` | Walks the call stack heuristically (see below). |
| `scopes` / `variables` | Exposes Registers, Hardware, Symbols scopes. |
| `disassemble` | Returns disassembled instructions around a memory reference. |
| `readMemory` / `writeMemory` | Raw memory access for hex view. |
| `evaluate` | Watch expressions: symbol lookup + simple hex/dec literals. |

### Custom DAP events

- `koh.framebufferReady` — fired at each VBlank with `{width, height, pixelsBase64}`. In Blazor-hosted mode these never leave the webview (the LCD component consumes them directly), so they're implemented as in-process C# events, not DAP events. The custom-event name is retained for future compatibility and for the rare case where a remote debugger wants a stream.
- `koh.cpuState` — throttled CPU snapshot (every ~60 ms or on stop) for the dashboard. Same in-process pattern.

### Execution loop

Blazor WASM is single-threaded, so the run loop is cooperative:

```csharp
public async Task ContinueAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested && !session.Paused)
    {
        var result = emulator.RunFrame(ct);
        framebufferChannel.Publish(emulator.Framebuffer);
        if (result.Reason == StopReason.Breakpoint)
        {
            session.Stop(StopReason.Breakpoint);
            break;
        }
        await framePacer.WaitForNextFrameAsync(ct);
        await Task.Yield(); // let DAP pause requests process
    }
}
```

`Task.Yield()` between frames is what allows DAP `pause` requests to be processed while the loop is running — the JS event loop runs pending messages during the yield.

### Breakpoint model

Breakpoints are stored as a `HashSet<ushort>` of ROM/PC addresses for O(1) checks per instruction. `SourceMap` provides `file:line → addresses[]`. When a source line maps to multiple addresses (macro expansion), every address gets a breakpoint. On hit, the debugger maps back to the innermost expansion source for the stopped location and exposes the macro expansion chain through stack frames.

### Stack trace (heuristic)

SM83 has no call-stack metadata. We walk SP looking for return addresses: for each 16-bit value on the stack pointing just after a `CALL` instruction (detectable via the debug info's address map), we record a frame. The top frame is always `PC`. Good enough for interactive debugging and matches what BGB / Emulicious do. Exact tracking via a shadow stack maintained by `DebugSession` is an optional future improvement.

### Scopes / variables

Three scopes returned from `scopes`:

1. **Registers** — A, F, B, C, D, E, H, L, SP, PC (hex), pseudo-registers AF/BC/DE/HL, and the four flag bits Z/N/H/C as booleans.
2. **Hardware** — key I/O registers resolved by name: LCDC, STAT, LY, IE, IF, TIMA, DIV, etc.
3. **Symbols** — workspace constants from `.kdbg`, lazy-expanded because the list can be large.

Memory is exposed via `readMemory` and a separate "Koh: Open Memory View" webview command (Phase 4).

### Dependencies

- `Koh.Emulator.Core` (project reference)
- `Koh.Linker.Core` (project reference — `.kdbg` parsing; avoids duplication)
- `System.Text.Json` source generation (BCL)
- Nothing else

## `.kdbg` debug info format

The linker emits `.kdbg` alongside `.gb` and `.sym`. `.sym` stays unchanged so BGB / Emulicious continue to work. `.kdbg` carries richer info that `.sym` cannot represent: per-byte source mapping, macro expansion stacks, symbol scopes, symbol sizes.

### File layout

```
┌──────────────────────────────────────────────┐
│ Header (16 bytes)                            │
│   Magic: "KDBG" (4 bytes)                    │
│   Version: u16 (starts at 1)                 │
│   Flags: u16                                 │
│   SourceTableOffset: u32                     │
│   AddressMapOffset: u32                      │
├──────────────────────────────────────────────┤
│ String pool                                  │
│   u32 count, then { u16 len, utf8 bytes }    │
├──────────────────────────────────────────────┤
│ Source file table                            │
│   u32 count, then { u32 stringId } per file  │
├──────────────────────────────────────────────┤
│ Symbol table                                 │
│   u32 count, then SymbolEntry[]              │
├──────────────────────────────────────────────┤
│ Address map                                  │
│   u32 count, then AddressMapEntry[]          │
├──────────────────────────────────────────────┤
│ Macro expansion stack pool                   │
│   u32 count, then ExpansionFrame[]           │
└──────────────────────────────────────────────┘
```

### Entries

**SymbolEntry:**

```
u8  kind                      // 0=Label, 1=EquConstant, 2=RamLabel, 3=Macro, 4=Export
u8  bank
u16 address
u16 size                      // 0 if unknown
u32 nameStringId
u32 scopeStringId             // 0 = global
u32 definitionSourceFileId
u32 definitionLine
```

**AddressMapEntry (16 bytes, sorted by (bank, address)):**

```
u8  bank
u16 address
u8  byteCount                 // ROM bytes covered by this entry
u32 sourceFileId
u32 line
u32 expansionStackId          // 0 = no expansion
```

**ExpansionFrame:**

```
u32 sourceFileId
u32 line
```

Expansion stacks are stored inline as `{u16 count, ExpansionFrame[count]}`. A breakpoint hit in a macro can walk the stack and show "inside macro X, called from macro Y, called from file.asm:42".

### Versioning

`Version` is checked on load. v1 is initial. Additive extensions bump the version and add section offsets to the header. Old debuggers reject newer versions with a clear error; new debuggers can read older versions.

### Writer (`Koh.Linker.Core`)

```csharp
public sealed class DebugInfoBuilder
{
    public void AddSymbol(SymbolKind kind, byte bank, ushort addr, ushort size,
                          string name, string? scope, SourceLocation definedAt);
    public void AddAddressMapping(byte bank, ushort addr, byte byteCount,
                                   SourceLocation location,
                                   ReadOnlySpan<SourceLocation> expansionStack);
    public void WriteTo(Stream output);
}
```

The assembler/binder already tracks source locations through macro expansion. The linker enriches them with final bank/address assignments after relocation, then calls `AddAddressMapping` for each emitted byte range. Symbols come from the existing symbol table.

### Reader (`Koh.Debugger.DebugInfoLoader`)

Loads `.kdbg` from `ReadOnlyMemory<byte>` (not a file path — Blazor WASM fetches the file via HTTP). Produces:

- `AddressMap` — dictionary `(bank, addr) → AddressMapEntry` plus a sorted array for nearest-match queries.
- `SourceMap` — inverted: `(file, line) → List<(bank, addr)>`.
- `SymbolMap` — `name → SymbolEntry` plus `(bank, addr) → List<SymbolEntry>`.

Memory footprint estimate for a typical ROM (~200 KB of code, ~10k symbols): 2–4 MB of debug data resident. Acceptable for interactive use.

## `Koh.Emulator.App` (Blazor WebAssembly)

### Project layout

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
│   ├── LcdDisplay.razor             // 160×144 canvas, scaled
│   ├── LcdDisplay.razor.js          // JS interop for putImageData
│   ├── CpuDashboard.razor           // registers, flags, cycle counter
│   ├── MemoryView.razor             // hex view (Phase 4)
│   ├── PaletteView.razor            // (Phase 2)
│   ├── VramView.razor               // (Phase 2)
│   └── OamView.razor                // (Phase 2)
├── Input/
│   ├── JoypadCapture.razor          // keyboard → JoypadState
│   └── JoypadKeyMap.cs              // configurable mapping
├── Services/
│   ├── EmulatorHost.cs              // owns GameBoySystem, frame loop, pacing
│   ├── FramePacer.cs                // await-based 59.7 Hz pacing
│   └── RomLoader.cs                 // debug mode: DAP launch args; standalone: file picker
├── DebugMode/
│   ├── DebugModeBootstrapper.cs     // wires DapDispatcher ↔ JS interop
│   ├── VsCodeBridge.razor.js        // postMessage bridge to extension
│   └── DapTransport.cs              // bytes in ↔ DapDispatcher
├── StandaloneMode/
│   ├── StandaloneBootstrapper.cs    // no DAP; file picker directly to EmulatorHost
│   ├── RomFilePicker.razor          // HTML file input
│   └── PlaybackControls.razor       // reset, pause, save state (Phase 4)
└── wwwroot/
    ├── index.html
    ├── css/
    └── sample-roms/                 // bundled for dev host convenience
```

### `EmulatorHost`

Central coordinator inside the Blazor app. Owns the `GameBoySystem` instance, runs the frame loop, publishes framebuffer events to Razor components, receives joypad state from input capture, and — in debug mode — exposes itself to `Koh.Debugger.DebugSession`. Standalone mode instantiates `EmulatorHost` directly; debug mode instantiates it through `Koh.Debugger` so the debugger owns stepping state.

Razor components subscribe to `EmulatorHost` events (`OnFrameReady`, `OnCpuStateChanged`) rather than polling. This keeps rendering off the tick path.

### Frame pacing

```csharp
public async Task RunAsync(CancellationToken ct)
{
    var stopwatch = Stopwatch.StartNew();
    var nextFrameAt = TimeSpan.FromMilliseconds(16.742);
    while (!ct.IsCancellationRequested)
    {
        if (debugSession?.IsPaused == true)
        {
            await Task.Delay(16, ct);
            continue;
        }
        emulator.RunFrame(ct);
        OnFrameReady?.Invoke(emulator.Framebuffer);
        var remaining = nextFrameAt - stopwatch.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ct);
        nextFrameAt += TimeSpan.FromMilliseconds(16.742);
    }
}
```

Browser timer resolution (~4 ms) is the limiting factor; perfect 60 fps isn't achievable via `Task.Delay` alone, but it's within the 1-frame tolerance that users perceive as smooth.

### Phase 1 UI

- `DebugShell` shows a placeholder LCD (solid dark gray, centered "Phase 1: awaiting PPU"), live `CpuDashboard`, status bar (mode, FPS, cycles).
- `StandaloneShell` shows a placeholder LCD, `RomFilePicker`, `PlaybackControls`.
- Changed values in the dashboard highlight briefly in yellow — cheap affordance that makes stepping feel responsive.

### Phase 2 UI additions

- `VramView` — visualize tile data as a grid.
- `PaletteView` — BG/OBJ palettes, color-swatch display.
- `OamView` — sprite table with per-sprite attributes.

## VS Code extension

### Module decomposition

`extension.ts` is a thin facade; all logic lives in narrow subsystem modules.

```
editors/vscode/src/
├── extension.ts                    // ~50 lines: activate/deactivate
│
├── core/
│   ├── KohExtension.ts             // top-level coordinator; owns subsystems
│   ├── DisposableStore.ts
│   └── Logger.ts                   // shared output channel
│
├── lsp/
│   ├── LspClientManager.ts         // existing LSP setup moved here
│   └── serverPathResolver.ts
│
├── config/
│   ├── KohYamlReader.ts            // parses koh.yaml, watches changes
│   └── WorkspaceConfig.ts          // typed view of koh.yaml
│
├── build/
│   ├── BuildTaskProvider.ts        // implements vscode.TaskProvider
│   ├── KohBuildTask.ts             // synthesizes asm + link tasks
│   └── binaryResolver.ts           // locates koh-asm / koh-link
│
├── debug/
│   ├── KohDebugRegistration.ts     // single entry point for debug wiring
│   ├── ConfigurationProvider.ts    // synthesizes launch from koh.yaml
│   ├── InlineDapAdapter.ts         // passthrough proxy to webview
│   ├── DebugSessionTracker.ts      // onDidStart/End + custom events
│   └── launchTypes.ts              // TS types mirroring LaunchArguments
│
├── webview/
│   ├── EmulatorPanelHost.ts        // panel lifecycle: create, reveal, dispose
│   ├── EmulatorPanel.ts            // single panel instance state
│   ├── html/
│   │   └── EmulatorHtml.ts         // HTML generator for Blazor loader
│   └── messages.ts                 // typed message contracts extension ↔ webview
│
└── commands/
    └── CommandRegistrations.ts     // registers all koh.* commands in one place
```

### Wiring rules

1. Each subsystem owns its own VS Code API registrations. `KohDebugRegistration.register()` is the only place that calls `vscode.debug.registerDebugAdapterDescriptorFactory` (which returns a `vscode.DebugAdapterInlineImplementation` wrapping `KohInlineDapAdapter`). `BuildTaskProvider.register()` is the only place that calls `vscode.tasks.registerTaskProvider`. `KohExtension` never touches the VS Code API directly.
2. Dependencies flow one way. Webview depends on nothing. Debug depends on config + webview. LSP depends on config. Build depends on config. `KohExtension` wires them.
3. Cross-subsystem events go through typed interfaces.
4. No subsystem reads another's internal state. Ask via typed methods.
5. `DisposableStore` aggregates lifecycle disposal.

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

### `package.json` additions

```jsonc
"contributes": {
  "debuggers": [{
    "type": "koh",
    "label": "Koh Game Boy Debugger",
    "languages": ["koh-asm"],
    "configurationAttributes": {
      "launch": {
        "required": ["program"],
        "properties": {
          "program":      { "type": "string", "description": "Path to .gb ROM" },
          "debugInfo":    { "type": "string", "description": "Path to .kdbg (default: same as program with .kdbg extension)" },
          "hardwareMode": { "type": "string", "enum": ["auto", "dmg", "cgb"], "default": "auto" },
          "stopOnEntry":  { "type": "boolean", "default": false },
          "preLaunchTask":{ "type": "string" }
        }
      }
    },
    "initialConfigurations": [{
      "type": "koh",
      "request": "launch",
      "name": "Debug Koh ROM",
      "program": "${workspaceFolder}/build/${workspaceFolderBasename}.gb",
      "stopOnEntry": false
    }]
  }],
  "breakpoints": [ { "language": "koh-asm" } ],
  "commands": [
    { "command": "koh.openEmulatorPanel", "title": "Koh: Open Emulator Panel" }
  ]
}
```

### Inline DAP adapter

```ts
export class KohInlineDapAdapter implements vscode.DebugAdapter {
    private readonly messageEmitter = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this.messageEmitter.event;

    constructor(private readonly webviewHost: EmulatorPanelHost) {
        webviewHost.onMessageFromWebview(m => {
            if (m.kind === 'dap') this.messageEmitter.fire(m.payload);
        });
    }

    handleMessage(message: vscode.DebugProtocolMessage): void {
        this.webviewHost.postToWebview({ kind: 'dap', payload: message });
    }

    dispose(): void { this.messageEmitter.dispose(); }
}
```

### Configuration resolution

`KohConfigurationProvider.resolveDebugConfiguration` on F5:

1. If `config.type` is empty (no launch.json), synthesize from `koh.yaml`: read the workspace config, derive `program` (the `.gb` path) and `debugInfo` (same path with `.kdbg`), set `preLaunchTask` to the synthesized build task ID.
2. If `config.program` is missing but launch.json exists, raise a clear error pointing at the docs.
3. `hardwareMode: "auto"` is left as-is; the emulator reads the ROM header.

### Build-on-launch

Two supported paths:

1. **Implicit (zero-config):** synthesized launch config includes `preLaunchTask: "koh: build"`. `BuildTaskProvider` registers a task provider that synthesizes this task from `koh.yaml`, invoking `koh-asm` then `koh-link`.
2. **Explicit:** user-defined `preLaunchTask` runs whatever they want (Cake, npm, make).

Build failure aborts the launch with VS Code's standard "build failed" prompt.

### Blazor asset bundling

The extension's build step invokes `dotnet publish Koh.Emulator.App -c Release` and copies the `wwwroot/` + `_framework/` output into `editors/vscode/dist/emulator-app/` (gitignored). The webview HTML loader references assets via `webview.asWebviewUri(...)` so CSP accepts them. Estimated package-size impact: 5–15 MB.

### Settings

```jsonc
"koh.debuggerPath":        { "type": "string", "description": "Reserved for future native debugger override" },
"koh.emulator.showDashboard": { "type": "boolean", "default": true },
"koh.emulator.scale":      { "type": "number", "enum": [1, 2, 3, 4], "default": 3 }
```

## Testing strategy

Four layers.

### Layer 1 — `Koh.Emulator.Core.Tests`

Pure .NET unit tests against the core library. Primary development debugger.

- **Per-opcode tests.** For each SM83 instruction: tiny ROM, run one instruction, assert registers/flags/memory/cycles. Data-driven `[Theory]`. ~500 tests (one per opcode family × addressing mode).
- **Memory map tests.** Read/write each region: ROM, VRAM, WRAM (CGB banked), OAM, HRAM, I/O, echo RAM, prohibited $FEA0-$FEFF.
- **MBC tests.** MBC1/3/5 bank switching, RAM enable/disable, MBC3 RTC.
- **Interrupt tests.** IF/IE/IME handshake, vector dispatch ($40/$48/$50/$58/$60), HALT wake-up, HALT bug.
- **Timer tests.** DIV rate, TIMA per TAC, TIMA overflow reload from TMA + IRQ.
- **DMA tests.** OAM DMA 160-byte transfer, CPU stall, HDMA general + HBlank modes.
- **PPU mode transition tests.** Mode timings (OAM 80 dots, drawing ~172-289, HBlank, VBlank 10 scanlines), STAT interrupt sources.

Fixture ROMs are hand-crafted byte arrays inline in tests — no external test ROMs at this layer. Failures point directly at the instruction under test.

### Layer 2 — `Koh.Debugger.Tests`

DAP handler unit tests without a real VS Code session.

- **DAP wire tests.** Feed raw JSON to `DapDispatcher`, assert emitted responses. Covers protocol correctness, malformed requests, unknown requests, out-of-order messages.
- **Breakpoint resolution tests.** Load synthetic `.kdbg`, set breakpoints by file/line, verify `SourceMap` resolves correctly (including macro-expanded multi-address cases).
- **Stack walking tests.** Construct known stack state, verify `CallStackWalker` frames.
- **Step-over semantics.** Step-over `CALL` runs until return; step-in descends; step-out runs until caller returns. Fixture programs for each.
- **Variables provider tests.** Registers/Hardware/Symbols scope shape; evaluate expression for symbols + literals.

### Layer 3 — `Koh.Compat.Tests` extensions

New `Emulation/` subfolder that runs public-domain test ROMs and compares state to expected values.

Target suites:

- **Blargg's cpu_instrs** (11 sub-tests) — Phase 3 target.
- **Blargg's instr_timing** — Phase 3.
- **Blargg's mem_timing + mem_timing-2** — Phase 3.
- **Blargg's interrupt_time** — Phase 3.
- **Blargg's halt_bug** — Phase 3.
- **Blargg's dmg_sound** (12 sub-tests) — Phase 4.
- **Mooneye Test Suite** — `acceptance/` in Phase 3, `ppu/` in Phase 2.
- **dmg-acid2 / cgb-acid2** — pixel-perfect framebuffer comparison against reference PNGs, Phase 2.

Blargg ROMs report via serial port (writes to $FF01, trigger $FF02=$81); the harness reads the buffer and greps for "Passed"/"Failed". Mooneye ROMs signal pass via a Fibonacci register pattern (B=3, C=5, D=8, E=13, H=21, L=34); the harness checks state at a known PC.

Test ROMs are not checked in. A `scripts/download-test-roms.ps1` (and `.sh` equivalent) fetches from known URLs with SHA-256 verification. CI runs the tests conditionally on fixture presence.

### Layer 4 — Manual / visual

- Dev host browser testing for sample ROMs.
- F5 end-to-end smoke test in VS Code periodically.
- Visual PPU regression is mostly covered by Layer 3 (acid2).

### Performance testing

- `Koh.Benchmarks.EmulatorBenchmarks` measures native tick throughput (target ≥ 4.2M T-cycles/sec) and frame throughput (target ≥ 60 fps headroom).
- Phase 1 ships a benchmark page inside `Koh.Emulator.App` dev host. Run in real browser under Blazor WASM AOT. **Must sustain ≥ 8.4M T-cycles/sec (2× real-time headroom)** before proceeding to Phase 2. If it fails, escalate before committing further.

## Phased implementation plan

### Phase 0 — Project scaffolding

1. Add `Koh.Emulator.Core`, `Koh.Debugger`, `Koh.Emulator.App` to `Koh.slnx`.
2. Add `Koh.Emulator.Core.Tests`, `Koh.Debugger.Tests`.
3. Write `docs/decisions/emulator-platform-decision.md`.
4. Verify Blazor WASM AOT publish works (`dotnet publish Koh.Emulator.App -c Release`).
5. Verify dev host runs (`dotnet run --project Koh.Emulator.App`) with a placeholder page.

**Exit:** empty projects compile; dev host serves a hello-world page.

### Phase 1 — Infrastructure & F5 wiring

Get F5 to do *something* end-to-end. No CPU instructions, no PPU rendering — plumbing, skeleton, live debug dashboard showing zeroes.

1. **`Koh.Emulator.Core` skeleton:** `GameBoySystem`, `SystemClock`, empty `Cpu` and `Ppu` (Tick advances counters only), `Mmu` with memory regions wired (no MBC routing beyond `RomOnly` + `Mbc1`), `Cartridge` loader reading ROM header, `HardwareMode` detection, `StepFrame` runs 70224 T-cycles, framebuffer stays blank gray, joypad field unused.
2. **`Koh.Emulator.Core.Tests`:** construction, memory read/write (non-banked), ROM header parsing, `StepFrame` clock increment.
3. **`Koh.Linker.Core` `.kdbg` emission:** `DebugInfoBuilder`, binary format per spec, `koh-link` emits `.kdbg` by default, linker tests round-trip.
4. **`Koh.Debugger` skeleton:** `DebugSession`, `DapDispatcher`, handlers for initialize/launch/configurationDone/continue/pause/setBreakpoints (stored, no halt), `DebugInfoLoader`, `koh.cpuState` publication.
5. **`Koh.Debugger.Tests`:** DAP JSON round-trip for the implemented handlers.
6. **`Koh.Emulator.App` Phase 1 UI:** `App.razor` + runtime-mode detector + both shells, `DebugShell` with placeholder LCD + `CpuDashboard` + status bar, `StandaloneShell` with file picker, `EmulatorHost` frame loop, `DebugModeBootstrapper`, `StandaloneBootstrapper`. Sample-ROM directory in `wwwroot/sample-roms/` (gitignored, documented download script).
7. **Blazor WASM AOT benchmark page.** Critical gate before Phase 2.
8. **VS Code extension refactor + debug integration:** decompose `extension.ts`, register `koh` debug type with inline DAP adapter, configuration provider (synthesizes from `koh.yaml`), build task provider, webview host loads bundled Blazor app. Extension build step runs `dotnet publish` and copies output.

**Exit:** F5 on an `.asm` file builds the ROM, opens the webview, shows the dashboard with static zeroes, debug toolbar (pause/continue/stop) functional. Dev host works. Standalone file picker loads a ROM. Blazor WASM AOT benchmark passes the 2× real-time threshold.

### Phase 2 — PPU & rendering

LCD shows pixels. Still no CPU instructions — PPU runs against VRAM contents poked in by tests.

1. Cycle-accurate PPU state machine: modes 0/1/2/3, scanline/dot counters, LY/LYC, STAT interrupt sources.
2. Background rendering (tile map + data, scroll, window).
3. Sprite rendering (OAM, priority, 8×8 and 8×16).
4. Palettes: DMG (BGP/OBP0/OBP1) and CGB (palette RAM, auto-increment).
5. VRAM/OAM access blocking during modes 2 and 3.
6. Framebuffer pipeline: `OnFrameReady` → `LcdDisplay` → `putImageData` with nearest-neighbor scaling.
7. CGB specifics: VRAM banking ($FF4F), WRAM banking ($FF70), HDMA (general + HBlank), double-speed mode (KEY1).
8. Razor additions: `VramView`, `PaletteView`, `OamView`.
9. Layer 3 tests: dmg-acid2 + cgb-acid2 pixel-perfect match.

**Exit:** acid2 passes DMG and CGB. LCD renders correctly in all three runtime modes.

### Phase 3 — SM83 CPU instructions

Emulator runs real code.

1. Complete SM83 instruction set via micro-op tables: 256 unprefixed + 256 CB-prefixed, HALT + STOP, IME and EI delay slot, HALT bug.
2. Interrupt dispatch: 5-IRQ priority, 5-T-cycle dispatch timing, PC pushed, IME cleared, IF bit cleared, jump to vector.
3. Test ROMs in Layer 3: Blargg cpu_instrs (all 11), instr_timing, mem_timing + mem_timing-2, halt_bug, interrupt_time. Mooneye `acceptance/` subset (bits, timer, interrupts, oam_dma).
4. Debugger features become meaningful: breakpoints halt at the right places, step-over / step-in / step-out are correct, stack walking makes sense on real ROMs, variables panel shows real state, disassembly at PC works.

**Exit:** all Blargg CPU/timing test ROMs pass. Setting a breakpoint in an `.asm` file and hitting it via F5 works end-to-end.

### Phase 4 — Peripherals & polish

Everything else needed for real games.

1. **APU** — four channels, mixing, WebAudio output. Blargg dmg_sound target.
2. **Serial port** — link cable stub, Blargg serial output (minimal form already in Phase 1).
3. **Joypad edge cases** — P1 quirks, joypad interrupt.
4. **Save states** — serialize `GameBoySystem` for standalone save/load.
5. **Battery-backed SRAM persistence** — standalone: browser IndexedDB; debug: `.sav` next to ROM.
6. **Memory viewer** in VS Code variables view — hex view updating on step/break.
7. **Watchpoints** — break on memory read/write at an address.
8. **Conditional breakpoints** — expression evaluated at hit.
9. **Reverse step / time travel** (stretch) — ring buffer of state snapshots for rewind.

**Exit:** real commercial ROMs run correctly. Audio plays. Chosen test-ROM targets pass.

### Phase 5 — Optional / future

- **MAUI Blazor Hybrid desktop shell.** Native-WebView (WebView2 on Windows, WKWebView on macOS) wrapper around `Koh.Emulator.App`'s Razor components. Zero duplication across webview, dev host, standalone web, and desktop. No Chromium runtime bundled — uses the host OS's system WebView. Only the shell project pulls in the MAUI SDK; `Koh.Emulator.App` itself stays a pure Blazor WASM project that the desktop app references.
- Publish standalone site as a Koh playground.
- Link cable emulation between two instances for multiplayer debugging.
- Trace logging for profile-guided optimization of user code.
- `Koh.Lsp` integration for "where is PC in my source" highlighting during a debug session.

## Open questions and deferred items

1. **Exact Blazor WASM AOT benchmark threshold.** Proposed 2× real-time (≥ 8.4M T-cycles/sec). If we hit real-time but not 2×, we proceed but acknowledge the emulator may stutter under background load. Revisit if the benchmark fails.
2. **MBC support in Phase 1.** `RomOnly` + `Mbc1` are trivial and included in Phase 1. `Mbc3` (with RTC) and `Mbc5` deferred to Phase 4.
3. **`.kdbg` version mismatch.** Hard error with a clear message pointing at the expected version.
4. **Bundled Blazor app location.** `editors/vscode/dist/emulator-app/` (gitignored), populated by extension build step. ~5–15 MB package bloat.
5. **Dev host sample ROMs.** Downloaded by `scripts/download-test-roms.ps1` / `.sh` with SHA-256 verification, not checked in.
6. **Test ROMs in CI.** Run conditionally on fixture presence; the download script runs as part of CI setup.
7. **Keyboard mapping persistence.** Standalone mode: browser localStorage.
8. **`Koh.Lsp` debug integration.** Deferred to Phase 5.
9. **Emulator runtime errors during a debug session.** Catch at `ExecutionLoop`, convert to a DAP `stopped` event with `reason: "exception"`, include message in the Debug Console.
10. **Macro expansion stack display in the debugger UI.** `.kdbg` carries the data, but VS Code presentation needs design. Proposal: synthesize virtual stack frames for each expansion level via DAP `stackFrames`. Phase 3.
