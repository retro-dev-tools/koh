# GPUI desktop migration

Status: implementation pending. Baseline: `9ebb6e5`.

## Whole-goal checkpoints

Draft [PR #56](https://github.com/retro-dev-tools/koh/pull/56) tracks this work.
The bar counts completed checkpoints, not estimated effort.

```text
[######>---] 6/10 complete; current: windowless engine session
```

| # | Checkpoint | State / evidence |
| --- | --- | --- |
| 1 | Baseline, scope and draft PR | Done: master updated, parity/removal inventory, PR56 |
| 2 | Initial DWARF writer and bounded reader | Done: banked source/symbol/constant and malformed-input tests |
| 3 | Assembly expansion provenance | Done: nested range/symbol ancestry; exact real 2048 metadata roundtrip; LLVM verification |
| 4 | Managed debugger cutover | Done: native reader binding, neutral records, 42 debugger tests in the passing fast suite |
| 5 | Producers, consumers and KDBG removal | Done: CLI/SDK/editor/sample/fixture cutover; old writer/reader/builder removed; seven SDK sidecars verified |
| 6 | Real DWARF debugging acceptance | Done: 37 compiler/debugger integration cases; Debug/Release SDK 2048, source breakpoints, framework stack recovery and machine state |
| 7 | Windowless engine session | Pending: coherent commands/snapshots, paused operation, saves and shutdown |
| 8 | Complete GPUI Kit interface | Pending: display, controls, picker, four inspectors, keyboard/accessibility and polish |
| 9 | Complete KohUI removal and integration | Pending: both backends/demo/tests/dependencies/references; launch/package/CI |
| 10 | Final native acceptance | Pending: real-game visual/input/audio, headless regressions and platform validation |

Keep progress reports and this ledger aligned with completed evidence.

## Required outcome

All desktop interface code is Rust using GPUI Kit. Completely remove KohUI,
both backends, demo, tests, dependencies and active build/release references.
Keep the C# emulator, boot ROMs and DAP debugger as the execution engine.
VS Code retains its native editor integration. A shell window or mocked
debugger does not satisfy parity.

## Architecture and appearance

`rust/koh-ui` owns the native window, input, file picker, display, inspector
and status. Use published `gpui-kit` with its matched GPUI version, without
porting the reconciler or introducing another widget framework.

The emulator executable becomes a windowless engine with a private stdio
session for commands and coherent snapshots. Preserve existing DAP transport
and headless screenshot/report modes. Reserve stdout for session messages in
the new mode; route diagnostics/pacing to stderr. The Rust parent owns the
child lifetime. EOF/window close must shut down cooperatively and flush SRAM.
Process commands at frame boundaries, including while paused. Never read live
mutable arrays from the renderer. Bound messages and validate shapes/enums.
Ship the engine beside the UI and resolve it relative to the executable.

A compact development instrument: LCD left, inspector right, transport above,
status below. Use GPUI Kit theme/controls/focus behavior. Cool slate surfaces,
blue actions, amber paused emphasis, system light/dark support; platform UI
font for controls and monospace for addresses/registers. No decorative chrome
competing with the game. Keep 160x144 nearest integer scaling and scroll the
inspector within the available space. Use an actionable empty state instead
of a private default ROM path. Failed loads preserve the working session.
Typing into fields must not trigger joypad input; focus loss releases held
joypad/fast-forward keys.

```text
Open ROM    [Pause/Resume] [Reset] [Save] [Load]       [Inspector]
+--------------------------------+-----------------------------+
|                                | CPU / Palettes / Tiles / Hex|
|         Game Boy LCD           |                             |
|     nearest / integer scale    | scrollable inspector        |
|                                |                             |
+--------------------------------+-----------------------------+
ROM / status                     Frame count / fast-forward
```

## Acceptance inventory

Read from `EmulatorApp.cs`, `Program.cs`, `EmulatorLoop.cs` and
`CpuSnapshot.cs` at baseline. None is yet proven through GPUI.

| Surface | Required behavior | Evidence needed |
| --- | --- | --- |
| ROM | Native picker, explicit path, cancel, missing/invalid ROM, DMG/CGB boot selection, reset | Real load/reset; failed load retains current session |
| LCD | 160x144 RGBA, opaque alpha, nearest integer scale, no tearing | Known pixel fixture and real DMG/CGB captures at multiple sizes |
| Transport | Pause/resume, reset, held Tab fast-forward | Frames freeze/resume; release/focus loss clears fast-forward |
| Joypad | Arrows, Z=B, X=A, Enter=Start, right Shift=Select | Real-game press/release; fields/focus loss do not stick buttons |
| Shortcuts | P pause, R reset, D inspector, F5 save, F9 load | Keyboard and buttons invoke same engine operation |
| CPU | PC, SP, A, F, BC, DE, HL, T-cycles, Z/N/H/C | Match paused engine; stepping refreshes snapshots |
| Palettes | DMG BGP/OBP0/OBP1; CGB 8 BG and 8 OBJ palettes | Known colors and real snapshots, all swatches visible |
| Tiles | 384 per bank; 128x192 DMG, 128x384 CGB | Bank/bit order, scrolling and crisp rendering |
| Memory | 256 bytes, 16 columns, address/hex/ASCII, bank-aware reads | PgUp/PgDn wrap at 16 bits; Home=0; paused refresh |
| Persistence | `<rom>.sav` battery SRAM, `<rom>.state` state | Real save/relaunch/load; report success after completion |
| Audio | Existing OpenAL pacing | Audible run; pause/reset/load/fast-forward |
| DAP | Launch/attach, breakpoints, step, memory, shutdown | Real VS Code session alongside new native window |
| Headless | DAP, PNG, input script, mode3 report, boot overrides | Existing process/framebuffer tests without GUI initialization |
| Access | Keyboard focus, labels, disabled states, contrast | Native keyboard pass and accessibility-tree inspection |

## Removal and integration inventory

- Delete `src/UI/KohUI`, `src/UI/KohUI.Backends.Sdl3`,
  `src/UI/KohUI.Backends.Dom`, `tests/UI`, `samples/ui/KohUI.Demo`,
  `scripts/run-kohui-demo.sh` and `scripts/run-kohui-demo.ps1`.
- Replace `EmulatorApp.cs` widget/model bindings and `Program.cs` backend
  startup. Remove `CpuSnapshot.cs`'s `KohColor` dependency; preserve decoding.
- Remove UI project references from `Koh.Emulator.App.csproj`, `Koh.slnx`,
  `Koh.Ci.slnf` and `Koh.Fast.slnf`.
- Remove SDL3-CS and UI-only packages/assets after checking every consumer
  in `Directory.Packages.props` and project files.
- Update Cargo workspace/lockfile, `build.proj`, launch scripts, PublishDev,
  release-toolchain and CI to build/package the UI and windowless engine.
  Preserve engine filename used by compiler integration tests or update every
  consumer together.
- Replace renderer/input/UI coverage with GPUI/session regressions, without
  keeping the DOM preview to satisfy old tests.
- Update README, CONTRIBUTING, AGENTS/CLAUDE maps and SDL3 documentation.
- Audit tracked references with `git grep -n -i kohui`. Classify history;
  no live namespace, project, dependency or launcher may retain the framework.
- Record Linux, Windows and macOS native evidence independently.

## Implementation order

Prerequisite selected by the user: migrate KDBG to ELF/DWARF first. Preserve
banked source/debugger behavior and prove real source breakpoints and stack
inspection before continuing the UI. See [DWARF migration](dwarf-migration.md).

1. Engine session and real-process regression: commands, coherent snapshots,
   paused operation, errors, EOF and SRAM shutdown.
2. Connected GPUI app: picker, LCD, transport, input and status. Execute a real
   ROM before adding inspector rendering.
3. Four inspector views, navigation and save/load feedback; visual review.
4. Complete legacy removal; update callers, packaging, docs and CI.
5. Full acceptance table and final removal audit.

## Initial evidence

- Fetch and fast-forward completed at `9ebb6e5` on 2026-11-18.
- Pre-existing `battle.png` edit retains SHA256
  `fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`.
- `cargo info gpui-kit@0.10.0`: exit 0. Downloaded metadata declares Apache-2.0
  and pins `gpui-pre = 0.3.12`. No application build/runtime proof yet.
- Upstream [getting started](https://gpui-kit.com/docs/getting-started/) and
  [installation](https://gpui-kit.com/docs/installation/) describe startup,
  single-dependency configuration and platform prerequisites.
