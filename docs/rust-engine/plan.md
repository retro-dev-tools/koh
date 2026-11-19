# Rust Engine Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for inline integration;
> independent CPU, PPU and APU tasks use superpowers:dispatching-parallel-agents.

**Goal:** Replace the emulator, debugger and windowless engine host with Rust.
**Architecture:** Three native crates preserve the current UI/session boundary.
**Tech Stack:** Rust 2024, existing Cargo dependencies, existing OpenAL backend.
**Spec:** `docs/rust-engine/design.md`.

## Global constraints

Preserve the ten compatibility gates in the spec and raw version-2 states.
Keep C# game authoring/LSP supported. No managed engine fallback at cutover.
Serial builds per target; independent agent target directories only. No merge
or release. Real completed logs, test dispositions and independently captured
C# oracle results are required evidence.

## Review focus

- Malformed/truncated state and command input must not corrupt a loaded game.
- Peripheral timing must occur before bus accesses, including double speed.
- LCD-disabled/window/sprite timing must survive restore at non-frame boundaries.
- Missing device/audio endpoint must preserve headless and DAP operation.
- Disconnect, EOF and failed load must not lose SRAM or leave child processes.

## Task 1: Baseline and state foundation

Files: this design/plan; `scripts/verify-rust-engine.py`;
`rust/koh-emulator/{Cargo.toml,src/lib.rs,src/state.rs}`; root `Cargo.toml`.

- [ ] Inventory all managed source tests/cases and downstream engine consumers.
- [ ] Capture immutable C# oracle executable/hash and generated-ROM deterministic
  session/frame/state fixtures in private temporary output.
- [ ] Add failing primitive-state roundtrip/truncation tests, observe RED.
- [ ] Implement bounded LE state primitives and common mode/interrupt types.
- [ ] Run `cargo test -p koh-emulator --lib --locked`; read the completed log.
  Command syntax is standard Cargo; package is created by this task.
- [ ] Commit the verified foundation and update HANDOFF.

## Task 2: CPU and instruction bus

Files: `rust/koh-emulator/src/cpu.rs`, component tests in that module.
Consumes: state/mode/interrupt types. Produces `Cpu`, `Registers`, `CpuBus`
exactly as specified in the design.

- [ ] Add instruction/flag/cycle/EI/HALT/STOP/wrap tests from managed cases; RED.
- [ ] Port all implemented unprefixed and CB instructions and state order.
- [ ] Run CPU component tests using a separate agent target; read results.
- [ ] Differentially check generated instruction traces against C#.
- [ ] Commit, then update dispositions/HANDOFF.

## Task 3: Bus, cartridge and peripherals

Files: `rust/koh-emulator/src/{cartridge,bus,timer,dma,serial,machine}.rs`.
Consumes CPU bus, PPU/APU component interfaces. Produces `Machine` instruction,
frame, memory, input, cartridge and coherent inspection operations.

- [ ] Port mapper/MMU/timer/serial/DMA regressions as failing Rust cases.
- [ ] Port exact banking, mirrors, interrupt and double-speed tick ordering.
- [ ] Connect the components; exercise reads/writes and transfer edge cases.
- [ ] Run core tests and C# trace comparisons; read logs.
- [ ] Commit each independently working component, update dispositions/HANDOFF.

## Task 4: PPU

Files: `rust/koh-emulator/src/ppu.rs` and component-owned helpers/tests.
Consumes common state types and borrowed VRAM/OAM. Produces design's PPU API.

- [ ] Port mode/fetcher/STAT/sprite/window/CGB tests; observe RED.
- [ ] Port FIFO/pixel pipeline, palette operations and state order.
- [ ] Verify exact frame pixels and dot/mode transitions against C# fixtures.
- [ ] Run isolated PPU tests; read log, commit and update HANDOFF.

## Task 5: APU

Files: `rust/koh-emulator/src/apu.rs` and component-owned helpers/tests.
Consumes common state types; produces design's APU API and bounded PCM queue.

- [ ] Port channel/power/length/sweep/wave/DIV timing cases; observe RED.
- [ ] Port integer mixer/sampling and exact component state order.
- [ ] Compare deterministic PCM/state fixtures against C#.
- [ ] Run isolated APU tests; read log, commit and update HANDOFF.

## Task 6: Full machine, boot and persistence

Files: machine/state modules and tests; native boot build integration.

- [ ] Add bidirectional C#/Rust state and boot-family regressions; RED.
- [ ] Assemble the existing DMG/CGB boot sources and execute them normally.
- [ ] Preserve state header/hash/layout, SRAM and corrupt-restore rollback.
- [ ] Run core suite plus deterministic real sample frame/PCM/state comparisons.
- [ ] Commit and update dispositions/HANDOFF.

## Task 7: Windowless engine

Files: `rust/koh-engine/{Cargo.toml,src/main.rs,src/session.rs,src/audio.rs}`
and native process tests; `docs/engine-session.md`.

- [ ] Add failing process tests for current session/headless CLI and validation.
- [ ] Implement CLI, bounded JSON, shared boundary execution, snapshots,
  persistence, OpenAL pacing/warmup and cooperative shutdown.
- [ ] Verify malformed input, missing audio device, pause/reset/state/input and
  stdin/EOF/SRAM against C# and actual Rust processes.
- [ ] Run process tests; read completed logs, commit and update HANDOFF.

## Task 8: Debugger and DAP

Files: `rust/koh-debugger/{Cargo.toml,src/lib.rs,src/dap.rs}` and tests;
engine DAP transport/hosting.

- [ ] Port every debugger case or evidence obsolete deletion; observe RED.
- [ ] Reuse native DWARF reader, implement expressions, breakpoints/watchpoints,
  stepping, stack recovery, memory, capabilities and DAP transport limits.
- [ ] Verify real SDK DWARF source debugging and disconnect/EOF behavior using
  actual GPUI + VS Code, and test invalid requests and missing audio endpoints.
- [ ] Run debugger/process suites; read logs, commit and update HANDOFF.

## Task 9: Integration and managed engine removal

Files: UI engine path, build/publish scripts, CI/install/release integration,
solutions and dependencies; downstream emulator verification/tools/tests.

- [ ] Switch all engine consumers and port their required verification APIs.
- [ ] Remove old C# engine/debugger/host projects and obsolete dependencies only
  after native replacement cases and caller tests pass.
- [ ] Close every source-test disposition; verify no production managed engine
  invocation or stale packaged engine survives.
- [ ] Verify source-to-ROM builds, native package and UI/DAP tests serially;
  read logs, commit and update roadmap/HANDOFF.

## Task 10: Full acceptance and review

- [ ] Run full Rust/remaining-managed suites, format and focused Clippy.
- [ ] Exercise Linux/Windows/macOS packages, real game input/pixels/audio,
  state compatibility, source debugging, EOF shutdown and notices.
- [ ] Review implementation against every spec gate; fix important findings
  with reproducing RED/GREEN tests and a final suite run.
- [ ] Open/update a separate draft PR stacked on PR56 while it remains open;
  record exact validated commits, evidence, limitations and independent review.
- [ ] Keep draft; no merge/release. Claim completion only after all gates pass.
