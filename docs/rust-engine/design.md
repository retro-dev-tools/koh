# Rust execution engine migration

## Outcome

Replace the production emulator, debugger and engine host with Rust while
preserving the accepted GPUI desktop, ROM behavior, source debugging, headless
commands, persistence and shutdown. The user authorized this next migration on
2026-11-19. The LSP, C# game-authoring library and MSBuild integration remain
separate. Keep the engine PR draft; do not merge or release it.

User clarification: all verification tooling is out of scope pending deeper
design. Leave `Koh.Verify`, the 2048 verifier and `tools/rust-asm-verify`
unchanged. Retain the C# emulator and boot libraries for existing development
tools, including superoptimizer and benchmarks. Deferred Rust tool ports stay
outside this worktree. Native compiler execution fixtures remain in scope.

Compatibility-oracle baseline: `e1336b0`. UI PR56 is merged. The user requested
rebasing the engine PR onto newest master `622770f` on 2026-11-19; only engine
commits are replayed, preserving the merged UI and ELF fixes. Worktree:
`/home/minorum/projekty/koh-rust-engine`, branch `feat/rust-engine`, PR57 base master.

## Architecture

- `rust/koh-emulator`: platform-independent SM83/DMG/CGB library. Own CPU,
  cartridge, bus, timers, serial, DMA, PPU, APU and state serialization.
- `rust/koh-debugger`: shared-core debugging, DWARF/source maps, expressions,
  stack recovery and DAP dispatch. Reuse `koh-link`'s bounded DWARF reader.
- `rust/koh-engine`: `koh-engine` binary, CLI/session/headless/DAP hosting,
  persistence, pacing and OpenAL output. No window or GPUI dependency.
- `koh-ui` continues to own its window and engine child; only sibling-engine
  executable resolution changes after the Rust replacement passes acceptance.

Keep the current process boundary and [`engine-session.md`](../engine-session.md).
Do not put emulator mutable buffers in the renderer. Commands and DAP mutations
run at instruction/frame boundaries, including while paused. EOF/quit flushes
SRAM and terminates owned threads. Stdout remains protocol-only in session mode.

The implementation choice is a native port of the existing engine with
component tests and independent C#-versus-Rust comparisons. Existing C# behavior
is the migration oracle, not a claim of perfect hardware accuracy. Preserve
intentional defaults and recorded timing; fix a discovered unrelated hardware
defect only as a separately proposed change. No replacement interpreter or
managed runtime is allowed behind the final Rust executable.

## Stable component interfaces

The shared core exposes `HardwareMode::{Dmg,Cgb}`, `Interrupts { iflag: u8,
ie: u8 }`, `Registers` and bounded little-endian `StateReader`/`StateWriter`.
State primitive order/width matches the C# raw payload. Readers return
`std::io::Result`; never panic on malformed external state.

CPU: `Cpu { registers: Registers, halted: bool, stopped: bool, ime: bool,
total_tcycles: u64, ... }`; `Cpu::step(&mut self, bus: &mut impl CpuBus)`.
`CpuBus` provides `read(u16)->u8`, `write(u16,u8)`, `internal_cycle()`,
`pending_interrupts()->u8`, `clear_interrupt(u8)`, and `stop()->bool`.
Read/write/internal-cycle each tick peripherals before the access, exactly one
M-cycle. CPU counts four T-cycles per such operation. Interrupt inspection is
untimed; the explicit dispatch cycles are timed. `stop` performs KEY1 handling
and returns whether a speed switch happened. `Machine` separates the CPU from
its bus borrow during a step; no shared mutable aliases or unsafe self-pointers.

PPU: `Ppu::new(HardwareMode)`;
`tick_dot(&mut self, vram: &[u8], oam: &[u8], interrupts: &mut Interrupts)->bool`
returns HBlank entry. VRAM/OAM stay owned by the bus. Register fields retain
their C# names in snake_case; palette and STAT helpers retain their operations.
`framebuffer: Vec<u8>` holds 160x144 opaque RGBA. Expose current mode and dot.
Port FIFO/fetcher/sprite/window/CGB behavior and state order faithfully.

APU: `Apu::new(HardwareMode)`, `tick_t()`, `advance_frame_sequencer()`,
`read(u16)->u8`, `write(u16,u8,div_apu_bit_high:bool)`. Expose a bounded
8192-slot signed-16-bit mono sample buffer with drop-oldest overflow semantics.
The timer signals DIV-APU falling edges, including writes and speed transitions.
Port all four channels, lengths, envelopes, sweep, wave restrictions, power and
44,100-Hz integer sampling. Save-state order matches C#.

Each component provides `write_state(&self, &mut StateWriter)` and
`read_state(&mut self, &mut StateReader<'_>)->std::io::Result<()>`.

Debugger/host boundary: `koh_debugger::Debugger::default()`;
`handle(&mut self, request: &serde_json::Value, machine: &mut Machine)->Reply`,
where `Reply { response: Value, events: Vec<Value>, action: Action }` and
`Action::{None,Continue,Pause,StepIn,Next,StepOut,Disconnect}`. The host owns
pause/run state and applies actions after the response, forwarding events.
`step(&mut self, machine: &mut Machine, action: Action)->Vec<Value>` performs
the requested step and produces stopped events. `run_frame(&mut self,
machine: &mut Machine)->Option<Value>` runs to the next frame or debugger stop;
the returned event pauses the host. DAP `launch` adopts the already-loaded
machine, matching the current actual host; it loads the requested ELF metadata
and accepts stopOnEntry. `load_debug_info(&mut self, path: &Path)` returns a
recoverable error for malformed/missing metadata. Root supplies the Cargo
workspace membership; each owning worker supplies its crate manifest.

## Compatibility gates

1. CPU instructions, flags, cycle ordering, interrupts/EI/HALT/STOP and wrapping.
2. Mapper banking/RTC/SRAM, MMU mirrors, CGB banks, timers, serial, OAM DMA/HDMA.
3. PPU dot/mode/STAT timing, sprite priority, window and exact DMG/CGB pixels.
4. APU power/register/channel timing and deterministic PCM; pause/resume/reset
   must preserve the existing audio warmup fix and normal pacing.
5. Boot ROMs assemble from the existing source; do not fabricate post-boot
   register state. Preserve overrides and reject family/length mismatch.
6. Existing `.sav` bytes and raw session `.state` interoperability both directions.
   The separate `SaveStateFile` utility retains its STES/version-2/hash wrapper;
   the current desktop session saves the machine payload without that wrapper.
   Corrupt restore rolls back; failed loads/writes preserve the working session.
7. Session validation: duplicate keys, UTF-16 request budget 16384, depth 8,
   signed-64-bit IDs, field types/ranges, coherent version-1 snapshots and bounded
   replies. EOF/oversized/incomplete input persists SRAM with the existing exit.
8. CLI/headless PNG/input scripts/mode3 report and DAP named-pipe/stdin behavior.
9. Real DWARF breakpoints/step/memory/watchpoints/stack recovery with GPUI and
   the actual VS Code extension; disconnect and EOF close desktop/engine.
10. Linux/Windows/macOS serial builds/tests, packaged native-window checks,
    audio evidence, notices and removal of production C# runtime dependencies.

Maintain a source-test disposition list: each managed test/case maps to a Rust
replacement or an evidenced obsolete deletion. Do not retain a managed fallback
or describe a boot-only port as complete. Existing verification, superoptimizer
and benchmark consumers retain their C# dependencies. Remove old production
debugger/host projects only after replacements and their consumers work.

## Verification and isolation

Keep the existing C# engine and source as an immutable development oracle until
cutover. Capture hashes, deterministic frame/snapshot/PCM/state fixtures and
source-test inventory before replacing implementations. Fixtures use generated
ROMs or repository sample games, never downloaded ROMs. Tests write private
temporary files, not tracked sample assets.

Use separate Cargo targets for independently owned parallel checks. Never run
two builds in one output directory. Read completed logs and real exit codes.
Keep code comments short, without prose or implementation details. Preserve
the main checkout's unrelated `battle.png` edit. Keep NuGet auditing enabled
while any C# project remains. No root LICENSE ownership claim is invented.

## Progress

`[####------] 4/10` plan checkpoints: baseline/state foundation, CPU, PPU and APU
components verified. Bus/full-machine integration, boot/persistence, engine,
debugger, removal/integration and final acceptance remain open. Detailed source
method dispositions are in `test-disposition.csv`; `review` entries have grouped
Rust/oracle coverage but are not closed until their assertions are checked.
