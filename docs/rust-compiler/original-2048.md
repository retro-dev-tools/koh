# Original 2048 sample acceptance

This group covers all16 methods and32 assertion call sites in
`Samples/CilGame2048Tests.cs`. The original game sources remain unchanged.
The native tests build actual Roslyn DLLs/PDBs in Debug/Release × DMG/CGB,
use canonical ELF and the Rust linker, and execute the existing C# emulator.
They are discoverable by cargo-nextest; no compiler-owned assertion is deferred.

## Source dispositions

All source names below belong to `CilGame2048Tests`. `sdk` means
`tests/sdk/original_2048.rs::original_2048_sdk_boots_and_plays`; `rules` means
`tests/driver/original_2048.rs::original_board_rules_preserve_legacy_scenarios`.
Both targets contain four rstest cases.

| Source method | Assertions | Decision / native requirement |
| --- | ---: | --- |
| Sample_CompilesWithoutDiagnostics | 1 | REPLACE: sdk builds actual sources, verifies native compilation and ELF consumption; old managed verifier structure is private. |
| Sample_LinksToBootableRom | 4 | REPLACE: sdk checks all48 logo bytes, header/global checksums, SDK/linker ROM parity and real boot, rather than two entry opcodes/two logo bytes. |
| Sample_RomBootsIntoMainAndInitializes | 1 | PORT: sdk executes real boot and retains tile1 first-row byte FF. |
| Sample_RendersScoreLabelAndNumberToTheBackgroundMap | 10 | PORT: sdk retains all five SCORE glyphs and five right-aligned score glyphs, before and after moves. |
| Sample_CursorSpriteIsVisibleAndFlushedToOam | 2 | PORT: sdk checks exact sprite Y/X/tile/attributes against the spawned cell and rendered cursor pixels. |
| SlideLine_MergesEqualPair | 1 | PORT: rules preserves adjacent pair -> exponent2. |
| SlideLine_CompactsAcrossGap | 1 | PORT: rules preserves gapped pair -> exponent2. |
| MoveRight_MergesRowIntoRightEdge | 1 | PORT: rules preserves exact right-edge cell. |
| MoveDown_MergesColumnIntoBottomEdge | 1 | PORT: rules preserves exact bottom-edge cell. |
| Spawn_FillsExactlyOneEmptyCell | 1 | PORT: rules preserves exact occupied count1. |
| Spawn_ReturnsFalseWhenBoardFull | 1 | PORT: rules preserves false on a full board. |
| CanMove_FalseOnGridlock_TrueWithAnOpening | 2 | PORT: rules preserves both locked=false and empty-cell=true. |
| HasWon_DetectsThe2048Tile | 2 | PORT: rules preserves both exponent11=true and exponent10=false. |
| Render_PaintsBottomRowCellsToTheCorrectTilemapAddress | 1 | PORT: rules preserves value9 at map(row12,col15); sdk also checks every cell's2x2 map rectangle. |
| Optimized_SampleRomBootsIntoMainAndInitializes | 1 | REPLACE: same independent boot/initialization contract in both native IL configurations; the old optimizer switch is private. |
| Optimized_SlideLogicMatchesUnoptimized | 2 | REPLACE: rules checks the independent exponent2 result in both configurations; agreement alone is insufficient. |

Total: **4 REPLACE / 12 PORT**,16 methods,32 original assertion sites.
The equality-only optimizer assertion becomes independent expected results.
No retained case treats old emitted addresses, register returns, IR ordering,
instruction counts or the managed optimizer API as a public contract.

## Independent expectations

2048 rules require compacting a line, merging each adjacent equal pair once,
and adding the resulting tile values to the score. Native Rust computes the
expected board/score for four real joypad moves. Exactly one empty cell becomes
exponent1 or2 after a changed move; unchanged moves do not spawn. The rule fixture
reports a distinct failure code for each retained original edge condition.

The public game presentation supplies the SCORE text, cell geometry, cursor
position and authored palette. Tests derive every board/cursor pixel from the
tile exponents, mathematical border pattern and diamond mask, independently of
the sample's literal tile byte table. Palette conversion is checked through
actual rendered RGBA; tilemaps alone cannot satisfy these assertions.
Header checks use the fixed cartridge logo and checksum algorithms from
[Pan Docs](https://github.com/gbdev/pandocs/blob/master/src/The_Cartridge_Header.md).
Real boot
uses the existing Koh boot ROM, not manually assigned PC/SP or LCDC defaults.
All32 original assertion sites are retained semantically or replaced as recorded
above; the new real-input/pixel/header requirements extend their coverage.

## Verification

Initial SDK selection passes4/4 in72.977s, exit0
(`/tmp/koh-original-2048-red.log`); despite its filename this run was green.
It precedes the full header/pixel assertions and explicit rule fixture.
Final combined selection passes12/12 in125.675s, exit0
(`/tmp/koh-original-2048-final.log`),464 cases excluded. It includes all eight
new cases plus all four existing board/stack-buffer cases. No production compiler
or emulator change was needed for this group. Rust formatting and fixture
CSharpier checks pass.
This group does not establish every sample, general debugger behavior or full
compiler replacement. Legacy definitions remain until whole-project removal.
