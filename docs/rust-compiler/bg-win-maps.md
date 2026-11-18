# Background/window map acceptance

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
