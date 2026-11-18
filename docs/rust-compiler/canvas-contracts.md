# Canvas pixel, raster and presentation acceptance

All seven methods in `Frontends/CilCanvasTests.cs` (36 lexical assertion sites)
have native replacements in `tests/driver/canvas.rs`, using real Roslyn
`MapTransfers` entries and the actual Koh.GameBoy library.

| Source method | Fixture and independent expectation | Decision |
| --- | --- | --- |
| `Init_Clear_SetPixel_Present_LcdOff_MatchesExpectedBuffer` | `CanvasPixels`: original three pixels/colors in the2x2-tile surface, every plane byte checked | PORT |
| `FillRect_SolidShade_FillsEveryPlaneByteFF` | `CanvasRect`: all64 pixels color3, every one-tile byte FF | PORT |
| `FillSpan_DitherShade_ProducesExpectedPlaneBytesPerRow` | `CanvasDither`: original88/77 and44/BB row pairs; other rows zero | PORT |
| `DrawLine_LcdOn_NeverWritesDuringMode3_AndDrawsExpectedDiagonal` | `CanvasLine`: exactly16 color3 diagonal pixels, LCD on, no mode3 writes | PORT |
| `FillTriangle_LcdOn_NeverWritesDuringMode3_AndFillsExpectedSpans` | `CanvasTriangle`: independent half-plane x>=y over the8x8 surface | PORT |
| `Present_DoubleBuffered_LcdOn_FlipsLcdcBit4_NeverWritesDuringMode3` | `CanvasDouble`:80 planes at9000,40 at8000, observed LCDC81 then91 | PORT |
| `Present_Cgb_UsesGdma_NeverWritesDuringMode3_AndMatchesExpectedBuffer` | `CanvasCgb`: original pixel and actual GDMA start, with full bytes and transfer-window guards | PORT |

Eight entries in Debug/Release × DMG/CGB execute32 ROMs. The additional
`CanvasClippedLine` runs a diagonal from(-3,-3) to(18,18); its expected surface
contains only the16 in-bounds diagonal pixels. This tests DrawLine's documented
clipping, not nonexistent bounds checks on the lower-level FillSpan/SetPixel API.

The pixel oracle maps coordinates through the independently specified
[2bpp tile layout](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Tile_Data.md).
Rectangle and triangle masks come from pixel membership, not copied rasterizer
loops. Every available VRAM byte is checked, including the cleared tile window,
centered grid, border map, untouched window map and poisoned CGB attribute bank.
The real initialization must perform those clears; no emulator/boot initial
graphics state is used as an oracle. OAM, LCD/palette state, result, SP and
reserved-memory guards remain checked.

Test-only `--native-canvas` appends the existing video observer's GDMA start
count. The expected aligned CGB path issues one transfer per presentation, or
two for the double-buffer fixture; DMG issues none. The existing mode3 and
remaining-vblank DMA-budget guards stay active. Both page-flip states are saved
by the game fixture, so final LCDC alone cannot hide a missing first flip.
Each object passes canonical ELF write/read before Rust linking and execution
in the existing C# emulator. No production compiler/emulator/API changes occur.

The first run passed both DMG cases but failed the new CGB LCD-off count assertion:
the test incorrectly expected no DMA there. The actual library documents aligned
LCD-off GDMA, which is permitted by the
[hardware transfer contract](https://raw.githubusercontent.com/gbdev/pandocs/master/src/CGB_Registers.md).
The assertion was corrected to require the actual transfer, not relaxed on
content, page flips, LCD-on safety, timing or any legacy behavioral requirement.

Final compiler-library/canvas/background/font/framework validation237/237 passed
46.810s, exit0,522 explicitly unselected (`/tmp/koh-canvas-final.log`). Stable
Rust formatting, CSharpier and diff checks passed.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --locked --no-fail-fast \
  -E 'binary(koh_compile) | test(canvas::) | test(bg_win::) | test(font_text::) | test(framework::)'
```
