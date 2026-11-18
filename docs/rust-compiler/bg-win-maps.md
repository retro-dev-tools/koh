# Background/window map acceptance

## CGB commit source-bank lifetime

The prepared GDMA source is a bus address, not a retained managed pin. Previously
PrepareFlush derived it while selecting the shadow's WRAM bank, then released the
pin before FlushBg/FlushWin used it. A game entering EndFrame with SVBK2 therefore
transferred bank2 bytes instead of the bank1 shadow. Both Debug and Release
reproduced6B at tile0 instead of authored42, despite retaining the caller's bank
(`/tmp/koh-banked-map-red.log`, two failures, exit100).

Each prepared commit now pins its owning shadow through register setup, the
VBlank wait and the blocking GDMA trigger. Pin cleanup restores the caller's bank.
The existing compiler pin lifetime/effects machinery supplies this behavior in
both modes even though the local pointer is otherwise unused. Preparation,
transfer length, source offsets, public APIs and the late-trigger timing guard
remain unchanged. There is no new intrinsic, compiler fallback or bank1-only
caller restriction.

`driver::bg_win::cgb_frame_reads_the_pinned_shadow_bank_and_restores_the_caller`
builds real Roslyn DLLs and checks canonical ELF/linker/emulator execution. The
fixture selects bank2, queues background tile42 and last window tile77, then runs
EndFrame. Full VRAM maps and OAM must match the bank1 shadows; VRAM1 remains
poisoned. Register readback and a post-frame raw store independently verify the
restored caller mapping: A3 must appear only at bank2 DF00, with bank1's corresponding
packet untouched and every other bank2 byte still6B. These are actual physical-bank
snapshots, not merely agreement between compiled reads and writes.

The first focused fix passed2/2 in9.971s. The affected graphics/pin selection then
passed24/24 in91.148s, exit0,593 explicitly unselected
(`/tmp/koh-banked-map-regression.log`), including the real showcase in all four
Debug/Release × DMG/CGB cases. Final physical-store assertions are recorded below.
The strengthened physical-store cases passed2/2 in8.516s, exit0
(`/tmp/koh-banked-map-final.log`). Rust formatting, CSharpier on the changed C#
files and `git diff --check` passed. Results are local Linux evidence, not a
current-head full-workspace or three-platform claim.
This corrects a runtime bank-lifetime bug; no additional original source-method
disposition is claimed. The ledger remains521/565 with44 outstanding. Broader
banked-data and capacity acceptance remain open.

## Original source-method replacements

All five methods in `Frontends/CilBgWinTests.cs` (50 lexical assertion sites)
now have native replacements. `tests/driver/bg_win.rs` builds the ordinary
Roslyn `MapTransfers` fixture and resolves the actual Koh.GameBoy library.
Four reached entry points execute in Debug/Release × DMG/CGB:16 ROMs.

| Source method | Requirement and independent oracle | Decision / fixture |
| --- | --- | --- |
| `BgFillAndDrawMap_WriteExpectedMapBytes` | Original4x2 fill,3x2 row-major blit and tile42 in the9800 map | PORT / `BackgroundContract` |
| `WinFillAndDrawMap_WriteExpectedMapBytes` | Original3x2 fill,2x2 blit and tile77 in the9C00 map | PORT / `WindowContract` |
| `SetAttrFillAttr_OnCgb_WriteVramBank1NotTileIndexMap` | Palette3/X flip23, priority80 rectangle, window Y flip40; tile indexes untouched | PORT / `AttributeContract` on CGB |
| `SetAttrFillAttr_OnDmg_IsTrueNoOp` | Attribute calls leave actual DMG VRAM unchanged; unavailable VBK readsFF | REPLACE / same fixture on DMG; remove assertions on an emulator-only phantom second bank |
| `BatchedFullBoardRedraw_WithLcdOn_ProducesZeroMode3Violations` | Original sixteen2x2 rectangles, all64 cells tile7, LCD on and zero mode3 violations | PORT / `BoardContract` |

[Pan Docs tile maps](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Tile_Maps.md)
define the32x32 layouts and CGB attribute bits. The
[CGB register contract](https://raw.githubusercontent.com/gbdev/pandocs/master/src/CGB_Registers.md)
defines bank selection/readback and the CGB-only second VRAM bank. The old DMG
test indexed the emulator's backing array as if a physical second bank existed.
That private storage is not DMG hardware behavior; useful no-op and register
checks remain, with all available VRAM checked instead.

The existing host poisons bank0 with A7 and CGB bank1 with B8. Every available
8KiB bank is compared byte-for-byte against an independently constructed map.
This checks original assertions plus every outside cell and untouched tile byte.
The fixture's real `Video.Init` must clear both index maps and OAM; neither a
boot ROM nor the emulator constructor supplies zero-filled graphics memory.
Unchanged attribute bytes must retain B8, not a fabricated zero initial state.
The CGB attribute fixture also requires restored bank0/VBK FE readback.

Board redraw performs the original five warm-up frames and sixteen fills, then
120 ordinary `Video.EndFrame` calls before returning. The existing8-million-step
host limit remains unchanged, below the old20-million-step acceptance loop.
Every byte must be committed before completion. LCDC91 is checked explicitly so
an accidentally disabled LCD cannot make the mode3 guard vacuously pass; other
fixtures require LCDC11. LCDC and all three palette registers, OAM, full result, stack
and reserved-memory guard checks remain active. This is memory/timing acceptance,
not a framebuffer-rendering or interactive-debugger claim.

The test uses existing APIs to preserve their useful behavior; it does not make
their design a permanent convention. No production compiler/emulator/API change,
fallback or extra linker is introduced. Legacy compiler/tests remain until final
replacement acceptance.

Validation: initial4/4 passed12.483s. Final stricter register/LCD checks plus
compiler-library/map/comparison/conversion regressions300/300 passed27.286s,
exit0,812 explicitly unselected (`/tmp/koh-bg-win-final.log`). The existing
comparison/conversion source rows were already counted complete; stale table
markers were corrected after this rerun, without extra completion credit.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --test lowering --test sm83 --locked \
  -E 'binary(koh_compile) | test(bg_win::) | test(lazy_maps_preserve) | test(full_cgb_map_transfers) | test(comparison_chain_) | test(conversion_chain_)'
```
