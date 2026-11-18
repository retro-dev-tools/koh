# Tile subranges, row writes and clearing

The two remaining `Frontends/CilTileSetTests.cs` methods now have native PORT
replacements. They contain10 source assertion sites, two original executions and
40 expanded assertions:32 copied bytes plus frontend/verifier checks, and four
row/clear bytes plus frontend/verifier checks. The other four methods already
have native acceptance and are not counted again.

## Independent requirements and matrix

[Pan Docs tile data](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Tile_Data.md)
defines16 bytes per8×8 tile, with consecutive low/high plane bytes for each row.
For unsigned8000 addressing the test oracle is `8000 + tile*16 + row*2`.
Expected bytes are computed in Rust from authored values, not old compiler output.

| Source method | Decision and native location | Requirement retained |
|---|---|---|
| Load_SubRange_CopiesFromStartTileNotFromDataStart | PORT complete; `driver::tile_set::tile_subranges_rows_and_clear_preserve_exact_vram`, Subrange roots | Source tiles1/2 contain22/33; destination tiles10/11 contain every expected byte, never source tile0's11 |
| SetRowAndClear_PassThroughToTileData | PORT complete; same Rust target, Rows roots | Row0 AB/CD is cleared, row3 retains12/34 and all other tile bytes are zero |

Four rstest cases compile actual Roslyn DLLs in Debug/Release and execute seven
roots on DMG/CGB:28 ROMs. Source images include real Koh.GameBoy references.
Native compilation verifies IR; canonical ELF round-trips and the Rust linker
precede existing-emulator execution. The same resolver is reused across roots.

Subrange cases cover aligned/unaligned sources with LCD off/on. The existing
mode3 and DMA timing guards remain enabled. Aligned CGB copies issue exactly one
observed GDMA request; DMG, row edits and zero-count loading issue none. A plain
source may choose either correct CPU or aligned transfer behavior, without a test
requiring a particular incidental allocation address. Transfer completion and all
copied bytes are checked; final internal DMA address representation is not a
contract. A zero-count load accepts an unused null source/65535 offset and leaves
VRAM unchanged. Nonzero invalid source ranges and invalid row indices are not
defined by these tests.

Row cases add tile16 and255, plus the final row of tile255, protecting widened
address arithmetic. The CGB bank1 case writes only the selected bank, observes
that selection through FF4F and restores bank0. Complete snapshots cover8192 bytes
per available VRAM bank, OAM, result-packet tails and alternate WRAM-bank poison.
Result42, restored SP and guard bytes are asserted. Initialization uses authored
Video.Init and explicit test-memory poison; constructor memory values are not
expected results. This is byte/storage acceptance, alongside existing rendered
tile/game tests, not a new tile API or a general graphics compatibility claim.

## Evidence

The first four cases passed19.278s, exit0 (`/tmp/koh-tiles-contract-first.log`).
The final selection replaced a private final-DMA-address assertion with an
observed request count and passed12/12 in46.984s, exit0
(`/tmp/koh-tiles-contract-final.log`),627 explicitly unselected. It includes the
existing real tile/video initialization, rendered tile and full1bpp mask cases.
No production compiler, game library or emulator changes were needed.

Ledger536/565 complete (535 native behavioral replacements, one earlier obsolete
private-parser DELETE),29 outstanding; zero legacy definitions physically retired.
Full migration acceptance and legacy compiler deletion remain open.

All-feature discovery reports2127 workspace/1950 compiler cases, zero ignored and
all four new cases (`/tmp/koh-tiles-contract-discovery.json`, exit0). Separate
workspace rustdoc (currently no cases), rustfmt, CSharpier and diff checks passed.
The pre-existing cubes `build_host` warning remains; discovery is not execution.

```sh
cargo nextest run -p koh-compile --test driver --locked -E 'test(tile_set::) | test(mono::) | test(actual_tileset_load)'
```
