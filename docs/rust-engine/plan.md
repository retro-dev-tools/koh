# Rust Engine Implementation Plan

**Goal:** Replace the emulator, debugger and windowless production host with Rust.
**Architecture:** Three native crates preserve the accepted GPUI/session boundary.
**Spec:** [design.md](design.md).

## Scope and evidence

All product verification tooling is excluded: existing Koh.Verify, sample
verifiers and rust-asm-verify remain unchanged. C# emulator/boot and their tests
remain development dependencies for verification, superoptimizer and benchmarks.
LSP, C# game authoring and MSBuild compiler algorithms are outside this migration.
No managed production fallback, merge or release is authorized.

Checked items record specific implementation or observed local evidence, not
blanket hardware accuracy or platform acceptance. The existing Cargo component,
debugger and engine process suites are the migration checks; no additional
product verification tool is planned. Reads of completed logs
and committed artifacts below were refreshed on 2026-11-19.

## Task 1: Baseline and state foundation

Files: `docs/rust-engine/{baseline.json,test-disposition.csv}`,
`rust/koh-emulator/{Cargo.toml,src/lib.rs,src/state.rs}`, root Cargo workspace.

- [x] Inventory all 224 managed source methods and preserve inline arguments.
- [x] Capture the immutable C# engine and record its hash and fixture provenance.
- [x] Implement bounded LE primitives and mode/interrupt types.
- [x] Observe primitive-state RED and GREEN; commit the foundation.

Evidence: `2b9c248`, `14e0148`;
`/tmp/koh-engine-state-{red,green}.log` (initial 0 passed/2 failed,
then 5 passed/0 failed). The reference executable hash was re-read and matches
`baseline.json`: `3b5069460020f225b107bec72b9da7f8cb7368c4afdc4ffc0d0d24c1c8b74e35`.

## Task 2: CPU and instruction bus

Files: `rust/koh-emulator/src/cpu.rs` and its tests/fixtures.

- [x] Port instructions, flags, EI/HALT/STOP, interrupt dispatch, wrapping and state.
- [x] Check independent C# CPU state and timed bus traces.
- [x] Commit the implementation; preserve source-test dispositions.

Evidence: `ae1b49a`; `/tmp/koh-engine-cpu-oracle.log` reports 16 passed,
0 failed; current full core run also executes the CPU fixtures.
Original managed core tests remain retained-dev, rather than declared obsolete.

## Task 3: Bus, cartridge and peripherals

Files: `rust/koh-emulator/src/{cartridge,bus,timer,dma,serial,machine}.rs`.

- [x] Port banking, mirrors, RTC, timer reload/edge timing, serial and DMA.
- [x] Connect instruction/frame execution and double-speed peripheral ticking.
- [x] Check integrated core and independent machine state/bus/PCM fixtures.
- [x] Commit the working components.

Evidence: `53f965d`, `85cd0c6`, `3d4a56a`;
`/tmp/koh-engine-core-final-current.log`: 91 passed, 0 failed, 0 ignored.

## Task 4: PPU

Files: `rust/koh-emulator/src/ppu.rs` and its fixtures.

- [x] Port FIFO/fetcher, STAT, sprites, window, palettes and CGB state.
- [x] Check exact managed pixels, mode/dot timing and state continuation.
- [x] Run isolated tests and commit the component.

Evidence: `85cd0c6`; `/tmp/koh-engine-ppu-tests.log`: 13 passed, 0 failed.
The initial `ppu-red.log` is a missing-type compile failure, not runtime parity
evidence. Current 91-case core run includes PPU oracle tests.

## Task 5: APU

Files: `rust/koh-emulator/src/apu.rs` and its fixtures.

- [x] Port channels, power, lengths, sweep, wave restrictions and integer sampling.
- [x] Check deterministic managed PCM/state and bounded drop-oldest sample queue.
- [x] Run isolated tests and commit the component.

Evidence: `85cd0c6`; `/tmp/koh-engine-apu-final.log`: 25 passed, 0 failed.
The initial `apu-red.log` is a missing-type compile failure. PCM equality is
not evidence of audible playback through speakers.

## Task 6: Full machine, boot and persistence

Files: `rust/koh-emulator/{build.rs,src/boot.rs,src/machine.rs,src/state_file.rs}`;
`rust/koh-engine/src/persistence.rs` and persistence/process tests.

- [x] Assemble existing DMG/CGB boot sources and execute their instructions.
- [x] Preserve separate raw-session and STES/version-2 utility state formats.
- [x] Check bidirectional foreign raw-state restore/continuation and boot identity.
- [x] Check corrupt restore rollback and failed state/SRAM write preservation.
- [x] Commit machine/state implementation; record local sample comparison.

Evidence: `3d4a56a`; current core 91/91 and host matrix tests below.
`/tmp/koh-engine-state-file-red.log` records 1 passed/1 failed before the fix;
`state-file-green.log` records 91 passed/0 failed.
`/tmp/koh-engine-realgame-480/result.json` records C# and Rust exits 0 and
identical 480-frame RGBA hash
`7a1a3b642496972ffbc31cca514b2739a0cc724d424ad8a318ee1c00689123b0`.
This is a recorded build comparison, not acceptance of the latest package.

## Task 7: Windowless engine

Files: `rust/koh-engine/src/{main,options,protocol,snapshot,persistence,audio,headless,dap,platform}.rs`,
`src/tests.rs`, `tests/{session,persistence}.rs` and
`tests/support/dap_transport.rs`; `docs/engine-session.md`.

- [x] Implement CLI/session framing, bounded JSON, snapshots and shared execution.
- [x] Implement persistence, audio warmup/pacing and cooperative shutdown.
- [x] Exercise process validation, no-audio fallback, state/input and SRAM endings.
- [x] Preserve exact paused command/save/restore assertions.
- [x] Commit the host and paused-command increments.
- [ ] Close the remaining exact windowless source-test assertion gaps.

Evidence: `7e451fb`, `a63f41e`;
`/tmp/koh-engine-paused-acceptance.log`: 2 passed, 0 failed.
`/tmp/koh-engine-host-matrix.log` has completed summaries of 8 binary,
1 persistence and 16 process tests, all passed and zero ignored. This includes
explicit C# reference/audio opt-ins, the six DAP argument combinations and
CGB/quit SRAM. It was run on Linux; Windows/macOS execution remains unproved.

## Task 8: Debugger and DAP

Files: `rust/koh-debugger/src/{lib,metadata,expression,inspect,disassemble,tests}.rs`,
oracle fixtures; engine DAP transport/hosting.

- [x] Port DWARF/source maps, expressions, breakpoints/watchpoints, memory,
  stepping, stack recovery, capabilities and request validation.
- [x] Reconcile 37 debugger methods to exact Rust assertions; obsolete only
  the private arithmetic scaffold.
- [x] Exercise generated DWARF source debugging and DAP process lifecycle.
- [x] Commit debugger implementation and record reviewed assertion coverage.
- [ ] Exercise actual GPUI plus the real VS Code extension against this engine.

Evidence: `ddbd1df`; `/tmp/koh-engine-debugger-review-green.log`:
27 passed, 0 failed; host matrix covers original six lifecycle parameter cases.
The review RED log failed because a new assertion was initially placed in the
wrong test; it is not a discovered production defect. See the CSV for host-owned
mappings, including the completed original three-case windowless matrix.

## Task 9: Production integration and managed host retirement

Files: UI engine path, build/publish scripts, CI/install/release integration,
SDK launch targets and native compiler execution hosts.

- [x] Commit native compiler execution hosts and fixture caller migration.
- [x] Retain C# emulator/boot for excluded development tools.
- [ ] Finish and validate production consumer/build/publish/SDK cutover.
- [ ] Remove old C# debugger/host projects only after replacement callers pass.
- [ ] Close every in-scope source-test disposition and inspect package contents.
- [ ] Validate final source-to-ROM, package and UI/DAP behavior serially.

Evidence: `d8b0317` supplies native fixture hosts. Production integration is
under active validation. The full compiler regression log has not reached a
terminal summary and is not counted as a pass. Existing managed verifier,
superoptimizer and benchmark callers must remain unchanged.

## Task 10: Full acceptance and review

- [ ] Finish full Rust/remaining-managed suites and final format/Clippy checks.
- [ ] Exercise native packages on Linux, Windows and macOS.
- [ ] Verify real game input/pixels/audio, source debugging and shutdown in GPUI.
- [ ] Complete notices, package-content and all ten compatibility-gate review.
- [ ] Update the separate draft PR with exact commits, evidence and limitations.
- [ ] Keep draft; no merge/release; claim completion only after acceptance.

Component/process Linux evidence above does not close these remaining product
and platform gates. Earlier accepted C#-engine GPUI results are not proof for
the new Rust engine.
