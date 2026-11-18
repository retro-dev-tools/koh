# Scattered map commit contracts

`tests/driver/bg_win.rs::scattered_map_commit_drains_and_does_not_replay_clean_state`
replaces both methods in `Frontends/CilMapWriterGdmaTests.cs`. The source has13
assertion sites across two Debug-only cases: eight CGB assertions and five DMG
assertions. Four rstest configurations now compile actual Roslyn Debug/Release
assemblies and execute eight ELF-linked ROMs, covering BG and window independently
on both devices. No compiler or game-library implementation change is required.

| Source method | Decision | Native behavioral requirement |
| --- | --- | --- |
| `Cgb_ScatteredRun_LandsCompletelyAfterOneEndFrame_ViaGdma` | PORT | The three cells at map indices98/115/325 contain7/9/4 after one EndFrame, with exactly one observed GDMA request. No later clean frame repeats that request or overwrites hardware-side edits. |
| `Dmg_ScatteredRun_StillDripsAcrossSeveralFrames_DripPathUnchanged` | REPLACE | DMG commits the same values within the existing61-frame bound, without CGB DMA or unsafe VRAM writes. A minimum latency above one frame is retired: faster safe completion is an improvement, not a regression. |

The old diagnostic and private verifier assertions become successful native
compilation, mandatory IR verification, canonical ELF round-trip and observed
execution. All2048 BG/window bytes are captured at63 ordered checkpoints per ROM.
Pending frames may contain only the old zero or the authored value at each cell;
once complete, all bytes must remain committed. CGB must complete at checkpoint1;
DMG must complete by61. The adapter changes one cell in each hardware map at
checkpoint62; checkpoint63 follows another clean EndFrame and must retain both
edits. Snapshot checks confirm the edits actually landed before testing their
persistence. This replaces the earlier one-cell CGB-only probe with both maps
and both devices, while every frame checks the cumulative DMA count.

The existing mode3 write guard and GDMA-duration guard remain enabled. OAM DMA
must execute from HRAM with IME disabled and finish outside visible scanlines.
Tests also check return42, balanced SP, reserved-memory guards, cleared OAM,
untouched CGB attribute VRAM and untouched alternate WRAM. The C# adapter only
sets up, observes and performs the explicit hardware-side edit; Rust owns all
acceptance assertions.

Authored cell values and dirty-state drainage are public graphics contracts.
The CGB one-frame deadline retains the useful existing runtime guarantee.
[Pan Docs VRAM DMA](https://github.com/gbdev/pandocs/blob/master/src/CGB_Registers.md#lcd-vram-dma-transfers)
defines CGB DMA and its timing restrictions; the DMG must use safe CPU writes.
Private ready flags, helper names and a deliberately slow DMG drip are not
requirements. These ROM tests use explicit LCD initialization and poisoned
memory rather than emulator constructor defaults.

Initial four-case run passed4/4 in16.200s, exit0,647 explicitly unselected:
`/tmp/koh-map-commit-first.log`. Final20/20 passed55.128s, exit0,631 explicitly
unselected (`/tmp/koh-map-commit-verified.log`), including BG/window operations,
pinned-bank frame, full-map, lazy-map and palette neighbors. The final run also
checks all8192 bytes of VRAM0, preserving untouched tile storage. Source ledger
closure is recorded in [progress](progress.md). This is local Linux evidence;
full migration and final cross-platform acceptance remain incomplete.

```sh
cargo nextest run -p koh-compile --test driver --locked --no-fail-fast \
  -E 'test(bg_win::) | test(full_cgb_map) | test(lazy_map) | test(palettes::)'
```
