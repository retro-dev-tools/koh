# Native engine review and local acceptance

Baseline: `e1336b048a67549ed99a8849ac78479213f570c8`.
Scope: production emulator, debugger, engine host and their launch/package paths.
Verification tools retain their existing C# emulator/boot dependencies.

An independent read-only review covered host commands, shutdown, persistence,
DAP transport/dispatch/inspection/stepping, UI process ownership, SDK launch,
publishing and CI. It found no confirmed critical or important migration
regression in those paths. It did not establish formal CPU/PPU/APU equivalence
or replace runtime/platform acceptance.

The review checked temporary step breakpoints against the actual production
`DapServerHost`, rather than the standalone fixture session. Both implementations
overwrite and later delete a persistent breakpoint at the temporary target.
This inherited behavior is outside the rewrite; it is not a new migration fix.

Local completed evidence:

| Check | Evidence |
| --- | --- |
| Native core after review fix | 92/92, zero ignored; `/tmp/koh-review-ppu-green.log` |
| Engine process/unit/persistence after review fix | 27/27, zero ignored, reference/audio opt-ins enabled; `/tmp/koh-engine-review-ppu-host.log` |
| Debugger | 27/27; `/tmp/koh-engine-debugger-lint-tests.log` |
| Exact legacy windowless cases | Three original parameter tuples; `/tmp/koh-engine-windowless-original-matrix.log` |
| Focused strict Clippy | Four native crates, all targets, no dependencies, warnings denied; `/tmp/koh-engine-native-clippy-final.log` |
| Remaining managed build/tests | Zero warnings/errors; 504/504, zero skips; `/tmp/koh-engine-managed-{build,tests}.log` |
| Full rebased compiler suite | 2063/2063, zero skipped; `/tmp/koh-engine-rebased-compiler.log` |
| Native package after review fix | Successful root publish; `/tmp/koh-engine-review-ppu-publish.log` |
| Real VS Code/default desktop/native DAP | 45 passing, zero pending; `/tmp/koh-engine-review-ppu-vscode.log` |
| Packaged native window | Exact LCD, integer scaling, Start, pause, save, child shutdown and empty shortcuts; `/tmp/koh-engine-review-ppu-window.log` |

The packaged window's title and gameplay captures were inspected. The package
contains `koh-emulator`, `koh-engine`, native OpenAL and notices, with no managed
engine executable or DLL. These package checks include the PPU review fix in
`78e84e5`; full hosted acceptance remains pending. SHA-256:

| Artifact | SHA-256 |
| --- | --- |
| koh-emulator | `efd37c61864ef83c8367db6dfa1a87b7150481c32d3ed52c5f614e59755575c9` |
| koh-engine | `933a3a06719b9780a2783b59d29d0686639d201012976bcb91555084da7595d4` |
| libopenal.so | `7162b88c6c1fea862746f688ee0a9267a72033f5ea5b82af74d304ec5061a1dc` |

Windows/macOS runtime acceptance remains open. CI exposed inherited nonblocking
Unix accepted sockets and a Windows synchronous duplex read/write deadlock;
the transport repairs and idle-input/partial-frame response regression pass
on Linux. Hosted confirmation is required before declaring platform acceptance.

Review comment `discussion_r4628428666` reproduced a new save-state rejection
after disabling the LCD mid-sprite fetch and restarting with fewer sprites.
`78e84e5` restricts the fetch/list relationship check to Drawing, preserving
absolute bounds in every phase. The tick-driven DMG/CGB regression fails before
the fix and passes afterward, checks restored state/pixel/interrupt continuation,
and retains rejection of invalid active fetches. All 92 emulator tests and
strict Clippy pass; logs `/tmp/koh-review-ppu-{red,green,clippy}.log`.

The first compiler run stopped after 1362 passes on an abbreviated palette
guard diagnostic. The exact original BG/OBJ diagnostics are restored; the
completed rebased 2063-case no-fail-fast run passes.
The native-window harness now decodes valid filtered PNGs using its existing
Pillow dependency; IHDR, CRC, decoded length and opaque-alpha checks remain.
Neither correction changes compiler assertions or production image encoding.

Compatibility-gate audit (Linux evidence; hosted three-platform acceptance open):

| Design gate | Inspected evidence |
| --- | --- |
| 1 CPU | Core suite executes managed state/timed-bus fixtures and exhaustive CB combinations |
| 2 Bus/peripherals | Core suite executes mapper/RTC, timer, DMA and full-machine fixtures |
| 3 PPU | Managed pixels/state fixtures, mode/dot tests and DMG/CGB LCD-restart regression |
| 4 APU/audio | Managed PCM/state fixtures; explicit OpenAL null-backend warmup/reset/restart and process resume/restore tests |
| 5 Boot | Existing boot-source assembly and family/length/override process cases |
| 6 Persistence | Foreign C# raw-state continuation, STES utility tests, corrupt restore rollback, failed state/SRAM write preservation |
| 7 Session | Protocol bounds/UTF-16/depth/duplicate tests and original three-case windowless matrix |
| 8 CLI/DAP | Reference mode3/boot/headless pixels, original DAP matrix, Unix path, EOF cleanup and partial-frame async-event case |
| 9 Desktop/source debugging | Actual default GPUI/VSCode session, Rust DWARF source breakpoint/step/caller stack/read watchpoint/memory/disconnect; 45 extension tests |
| 10 Platforms/packages | Linux package/window/audio pass; hosted Windows/macOS package/window/audio/VSCode evidence still pending |

Null-backend tests establish audio queue/reset operation, not physical speaker
playback. Declaration-only upstream notice records remain a release review
requirement; this PR does not authorize a release.
