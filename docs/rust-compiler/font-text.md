# Font and text acceptance

All five `Frontends/CilFontTextTests.cs` methods (31 lexical assertion sites)
have native PORT replacements in `tests/driver/font_text.rs`, using the actual
Koh.GameBoy library and ordinary Roslyn `MapTransfers` entry points.

| Source method | Fixture | Independently expected behavior |
| --- | --- | --- |
| `Draw_WritesExpectedMapCellsAndExpandsGlyphPixels` | `FontScore` | SCORE at BG column1/row0; S begins3C/40, duplicated into low/high planes |
| `DrawToWindow_WritesGlyphIndicesToWindowMap` | `FontWindow` | HI at window column2/row3, BG unchanged |
| `DrawNumber_RightAligned_SpacePadsToWidth` | `FontRight` | Literal expected string ` 1234` at BG origin |
| `DrawNumber_LeftAligned_NoPadding_HandlesZero` | `FontLeft` | Literal42 and0 at the original two rows, no extra padding |
| `Draw_ByteArrayOverload_WritesExpectedGlyphIndices` | `FontBytes` | HI! at original BG column4/row2 |

Six entry points run in Debug/Release × DMG/CGB:24 ROM executions. The extra
`FontOffset` case loads at tile7 and verifies shifted SCORE indices,65535 in a
width3 field without truncation, and window zero padded to three columns.
Decimal expectations are literal strings, independent of the runtime's division,
remainder and stack-buffer implementation.

The bitmap table in `Font.cs` is treated as authored input artwork, not compiler
output or an arithmetic oracle. The test reads its768 input rows and independently
expands each byte into both color3 bitplanes, using the independent
[SM83/PPU tile layout](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Tile_Data.md). It checks the original independently
specified S rows3C/40 too. All1536 resulting tile bytes must match, at the selected
font base; every other VRAM byte is checked against poison or the expected maps.
This validates the actual array initializer, reference reachability,1bpp loader,
memory access, glyph indexing and graphics storage together.

The real `Video.Init` must clear index maps and OAM; the host supplies A7/B8 poison,
not boot-ROM zero graphics memory. CGB attribute VRAM remains unchanged. LCDC,
palette controls, OAM, full result, SP and reserved-memory guards remain checked.
The existing mode3 guard stays active. No production compiler/emulator/API code,
timing assertion, sample asset or legacy test definition is changed.

Initial4/4 passed17.010s. Expanded compiler-library/font/map/string regressions
238/238 passed38.961s, exit0,512 explicitly unselected
(`/tmp/koh-font-text-final.log`). Stable Rust formatting, CSharpier and diff
checks passed. No ignored test or extra .NET compiler test project is introduced.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --locked \
  -E 'binary(koh_compile) | test(font_text::) | test(bg_win::) | test(lazy_maps_preserve) | test(full_cgb_map_transfers) | test(strings_preserve_utf16_calls)'
```
