# Sprite, palette and frame-commit acceptance

Rust-driven Roslyn fixtures now replace the remaining seven methods in the three
sprite test files. Native compilation, canonical ELF round trips and Rust linking
produce the tested ROMs. Koh.GameBoy remains ordinary C# target-runtime code;
neither the C# compiler nor a new emulator implementation participates.

## Independently chosen requirements

[Pan Docs OAM](https://raw.githubusercontent.com/gbdev/pandocs/master/src/OAM.md)
defines40 entries at FE00–FE9F, ordered Y/X/tile/attributes, with screen offsets
16/8. Tests compare all160 bytes, including untouched entries. Negative-coordinate
cases check C# byte truncation, not a promise that wrapping implements arbitrary
screen clipping. The existing unchecked slot API is not a design convention.

[Pan Docs palettes](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Palettes.md)
defines separate64-byte object palette RAM, little-endian colors and mode3 access
restrictions. Tests poison that RAM, then observe it independently of game getters.
They cover slots0/1/7 and exact authored colors. Register getter checks execute with
the LCD off; the unused palette-index bit is excluded rather than inheriting the
legacy exact-zero assumption. DMG shade arguments apply only to the DMG branch.

## Corrected runtime bugs

The stricter observer found Debug DMG OAM DMA overlapping visible scanlines.
Waiting in Video.EndFrame before entering Sprites.Flush left buffer preparation,
pinning and page calculation inside the limited blanking window. Sprites.Flush
now performs that work before waiting, immediately preceding the blocking DMA.
Its clean branch also waits; EndFrame removes its earlier wait. The public frame
API, dirty behavior, bank pin and compiler-generated HRAM routine are retained.
No extra unconditional frame wait or compiler-specific special case is added.

Moving that phase exposed a separate CGB map bug: the old LY check preceded four
DMA register writes and admitted lines144–149. A reproduced transfer began at
LY152/dot276 with704 dots of work and only636 remaining. Both prepared map commits
now configure HDMA1–4 first, then admit lines144–147 immediately before the trigger
or wait for the next blank. Their maximum range remains1KiB. The observer checks
actual transfer duration against remaining dots, not the check's source shape.
This is tested runtime timing, not a general worst-case or IRQ-reentrancy proof.

The first broader run passed33/36 and failed three (`/tmp/koh-sprites-regression.log`): one real
GDMA timing failure and two mismatches from editing a fixture while its test binary
was running. The final run uses a consistent source snapshot; do not treat those
two fixture/binary mismatches as compiler defects or that earlier run as green.

## Source disposition matrix

The seven methods contain57 assertion sites and nine original expanded cases.
Source directory: `tests/Compiler/Koh.Compiler.Tests/Frontends/`.
Counting their original fixed160-byte desktop loop gives242 expanded assertions.
Two raw-DMA methods in CilSpritePaletteHardwareTests were already replaced by
[OAM DMA tests](oam-dma.md) and receive no additional credit here.

| Source file / method | Requirement and independent expectation | Contract or detail | Decision; native nextest location |
| --- | --- | --- | --- |
| CilSpritesTests / EndFrame_FlushesShadowToOam_MatchingSetMoveHideAndClipping | Sprite writes, wrapped byte coordinates, hide and untouched slots; expected first12 bytes48/24/5/32,252/4/9/0,0/58/3/0 | Observable OAM; replace C# frontend/verifier APIs | PORT; `driver::sprites::referenced_sprites_flush_real_oam_and_palette_memory`, Mutate |
| CilSpritesTests / HideAll_ForcesEveryShadowSlotYToZero | Clear all40 previously modified entries, not just three sampled Y bytes | Library clear behavior | PORT; same target, HideAll, all160 bytes |
| CilSpritesTests / Move_ChangesPositionOnly_TileAndAttrSurvive | Slot3 becomes46/28/42/128; other slots unchanged | Value and memory semantics | PORT; same target, Move |
| CilSpritesTests / EndFrame_WithNoSpriteMutation_LeavesOamAllZero | Initialization and first flush leave all160 bytes zero from poisoned hardware memory | Observable initialization | PORT; same target, NoMutation; CleanFrame additionally checks counter2 and one DMA; Tile checks tile255 without position loss |
| CilSpritesCgbTests / CgbPaletteSprite_FlushesRealOam_AndWritesRealObjPaletteRam | Slot0 becomes46/28/5/0; authored palette bytes1F00/E003/007C/3412 reach real RAM | Hardware writes; explicit return replaces arbitrary SCX completion marker | REPLACE; same target, ColorPalette, extended to slot39 and palettes1/7 |
| CilSpritePaletteHardwareTests / RegisterRoundTrip_NewSpritePaletteRegistersReachRealAddresses | LYC5A, OBP0E4, OBP11B and selected object palette byte2C; supported index bits read zero outside mode3 | MMIO writes/getters; replace unqualified reserved-bit readback | REPLACE; same target, Registers, including game getter output and independent hardware snapshots |
| CilSpritePaletteHardwareTests / DesktopHost_HardwareDmaSetterCopiesWramPageIntoGbOam | Hardware.DMA=C0 copies authored bytes1..160 to the desktop library's actual OAM | Still-live library contract | PORT; `driver::sprites::desktop_dma_library_copies_the_actual_page`; Rust invokes ManagedHost and asserts the complete returned hex snapshot |

Five PORT and two REPLACE decisions; none DELETE or DEFER. All assertions are
owned by Rust. The desktop fixture returns observations; it contains no test
assertions or compiler algorithms. Legacy definitions remain until whole-project
removal. The ledger advances521/565, leaving44 outstanding, zero physically retired.

## Verification

Eight new rstest cases execute32 normal ROMs, two deliberately unsafe palette ROMs
and two desktop-library processes across Debug/Release and DMG/CGB where applicable.
The observer requires HRAM execution with IME0 during OAM DMA, no visible-scanline
overlap, one actual DMA per normal case, and no mode3 palette writes. The negative
ROM must fail specifically on that palette guard. Tests check return42, restored
SP, memory guards, full OAM/palette bytes and unchanged result-packet tails.

The final affected selection passed36/36 in129.702s, exit0,579 explicitly unselected
(`/tmp/koh-sprites-final-regression.log`). It includes all new cases, existing OAM
DMA/signature tests, full map transfers, background/window, canvas, font, framework
and the real graphics showcase. Production emulator code and sample assets are
unchanged. These are local Linux results, not current-head full or three-OS proof.
Workspace all-features discovery reports2091 cases,1914 compiler cases, zero
ignored and all eight new sprite cases (`/tmp/koh-sprites-discovery.json`, exit0).
The existing cubes dead-code warning remains. CSharpier checked all five changed
C# files; Rust formatting and `git diff --check` passed.

```sh
cargo nextest run -p koh-compile --test driver --locked -E 'test(sprites::)'
```
