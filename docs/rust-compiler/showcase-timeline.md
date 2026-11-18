# Graphics showcase timeline

The five remaining `Samples/GbGfxDemoTests.cs` methods cover24 original assertion
sites,36 assertions after expanding the four-sprite loop. They now map to the
existing four `driver::full_graphics_showcase_boots_and_animates` rstest cases.
The unmodified sample is Roslyn-built in Debug/Release and run as a native ROM
with actual DMG/CGB boot in the existing C# emulator.

## Disposition matrix

| Source method | Sites / expanded assertions | Requirement and independent justification | Decision / native assertions |
| --- | ---: | --- | --- |
| Oam_AfterFirstPaintedIteration_MatchesExpectedYXTileAttr | 4 / 16 | Four authored sprites must occupy their phase-specific orbit positions with hardware Y/X offsets, tile2 and independent flip flags. Public game presentation, not backend layout. | PORT: all16 OAM bytes at every coherent completed phase through79, including bounded first observation. |
| Oam_AfterMoreIterations_PositionsChangedAsExpected | 6 / 6 | Exact sprite0 state after more than ten additional updates, with observable progress. | PORT: every intervening phase appears in order; the first and first+11 states differ and both have exact bytes. |
| Palette_TogglesAcrossASixtyIterationBoundary | 5 / 5 | A real60-update boundary changes the DMG theme, with exact BGP values before/after and fewer than256 updates. | PORT: bounded first phase2..10 and every phase through79 include first+61; phase60 uses E4 and phase61 uses93. |
| Dmg_Run_HasQuantizedShadesInBgpAndObp | 2 / 2 | Authored DMG BG/OBJ shade mappings must reach hardware registers. | PORT: exact BGP/OBP0 pair E4/1C or93/E0 for every observed phase. |
| Cgb_Run_HasRgb555InPaletteRam | 7 / 7 | Authored RGB555 BG colors0..3 and OBJ colors1..3 must reach their palette slots. | PORT: all eight bytes of both BG/OBJ palette0 at every observed phase, including both themes. |

All expectations concern observable presentation. The orbit lookup follows the
authored animation; explicit cardinal positions and the existing independent
quarter-turn geometry checks prevent a matching phase mistake from satisfying
the whole test. Palette packets are literal little-endian RGB555 expectations,
independent of compiler intrinsic tables or generated code. Existing framebuffer,
window text, checkerboard, unused-sprite and mode3 assertions remain intact.

## Capture and coherence

The small C# host records raw HUD glyphs, four OAM entries, BGP/OBP0 and BG/OBJ
palette bytes once per hardware frame. Its only score decoding bounds capture;
all behavioral assertions remain Rust-owned. Capture stops at score1360
(80 logical updates), with an explicit6000-frame ceiling and zero mode3 writes.
No production emulator, game or game-facing API changes are involved.

HUD flushing and OAM DMA are not atomic together. Rust requires a coherent
observation of **every** completed phase from the bounded first phase through79,
in order, matching all OAM/palette bytes simultaneously. Transient partial
updates may be present between observations; this is not an assertion that
every hardware frame is atomic. A missing phase, permanently wrong sprite byte,
wrong palette slot or missing theme transition fails. Chronology and exact
numeric phases replace the weaker previous any-scroll/any-sprite progress proxy.

## Evidence

The first host build failed because palette RawData is already a ReadOnlySpan;
using a range fixes the capture API without changing assertions. The initial
64-update selection passes4/4 in46.646s, exit0
(`/tmp/koh-showcase-timeline-first.log`). Final80-update execution passes4/4 in47.303s, exit0
(`/tmp/koh-showcase-timeline-final.log`),440 other driver cases excluded. Rust
formatting and CSharpier pass. No additional test cases are added: the four
existing discoverable cases are strengthened. No test is deferred or physically removed; the original four
showcase methods were already closed separately.
