# Game Boy 2048 Sample

A 2048 game for Game Boy Color, built with the Koh toolchain.

## Build

```sh
./scripts/build.sh        # bash / git-bash
./scripts/build.ps1       # Windows PowerShell

# or, from the repo root:
dotnet msbuild build.proj -t:BuildSample2048
```

Output: `build/2048.gbc`. Open in BGB, SameBoy, or mGBA.

## Verify

The sample ships with `verify/Gb2048Verify`, a console app that boots the
ROM in `Koh.Emulator`, exercises every game state, asserts board-level
invariants, and writes PNG snapshots of each state to
`build/verify-shots/`.

```sh
# Build the ROM first, then:
dotnet run --project samples/gb-2048/verify -- \
    samples/gb-2048/build/2048.gbc samples/gb-2048/build/verify-shots
```

Exits non-zero on the first assertion failure. PNG output uses an
in-process encoder so no Python / ImageMagick is required.

The harness lives in `src/Koh.Verify/RomHarness.cs` and can be reused
by any other Koh-built ROM that wants headless verification + snapshot
capture.

## Controls

- **D-pad** — slide tiles left/right/up/down
- **START** at title screen — begin a new game
- **A** at the win screen — continue play toward 4096
- **START** at game-over — return to title

## Features

- MBC5 cartridge with 4 ROM banks; battery-backed SRAM (8 KiB) for best-score persistence.
- GBC-only (CGB flag = `$C0` at byte `$0143`). Will not boot on original DMG.
- **4×4 board of 4-tile-wide × 3-tile-tall cells** (32×24 px each), centred on screen, each cell rendered with its top/bottom border edge and a per-value palette tint. Values are drawn as actual decimal numbers ("2", "16", "256", "2048") using 8×8 digit tiles, with the digit count centred horizontally inside each cell.
- **8 GBC palettes** map cell values to colours (yellow → orange → red → gold), giving an instant read of progress.
- **Always-visible HUD** on BG rows 0-1 (`BEST nnnnnnn` / `SCORE nnnnnnn`) plus a 1-pixel rule on row 2 separating it from the board.
- **String pipeline:** `CHARMAP` directives in `gfx/tiles.inc` map ASCII letters/digits to their tile IDs, so the screens write `db "PRESS START", STR_END` and the runtime helper `DrawString` blits them into the tilemap.
- **OAM-sprite slide animation:** non-empty pre-move tiles glide from their src cell-centre to their destination over 8 frames using a leading-digit sprite. The BG board area is cleared during the slide and snaps back at the commit frame with the final colours.
- Sound: move, merge, game-over, and a wave-channel win jingle.
- Save format: `"K248"` magic + best score + last board + last score + Fletcher-16 checksum, gating valid loads.

## Architecture

See `docs/superpowers/specs/2026-04-30-gb-2048-design.md` for the design spec.

ROM bank layout:
- ROM0 — engine (boot, IRQs, OAM DMA trampoline, VBlank queue, input, sound, HDMA helper, `DrawString`, `FillVram`, `ClearBoardArea`).
- ROM1 — game logic (RNG, board, score, save, animation, render).
- ROM2 — graphics (font + cell/digit tile data).
- ROM3 — screens (title, game over, win).

Visual layout (20×18 tiles, 160×144 px):
- Rows 0-1: HUD ("BEST nnnnnnn" / "SCORE nnnnnnn").
- Row 2: `TILE_HUD_RULE` (single horizontal divider line).
- Rows 3-17 (15 rows): four cell-rows at rows 3-5, 7-9, 11-13, 15-17, separated by 1-tile gaps (rows 6/10/14).
- Cells at cols 0-3, 5-8, 10-13, 15-18 (cols 4/9/14 are inter-cell gaps; col 19 is the right margin).

## Notes

- Public labels use `::` syntax (Koh supports RGBDS-compatible exported-label syntax).
- Slide animation runs in two phases per move:
  - **AnimStart** populates `wMoveIntents` from the pre-move snapshot, writes sprites at source cell-centres, and plays the move/merge SFX.
  - **AnimTick** clears the board BG region on frame 1 (after the OAM DMA has made the src-position sprites visible), interpolates sprite positions over frames 1..7 with per-cell strides (40 px X, 32 px Y), and on frame 8 hides sprites, repaints the BG with the new board state and per-value palette attributes, and refreshes the HUD.
- `gfx/tiles.inc` is the single source of truth for tile IDs and the `CHARMAP`. Koh's `CHARMAP` directive requires number-literal values (not EQU identifiers), so the mappings duplicate the IDs as raw integers; keep them in sync with the `TILE_*` EQUs above when reordering tiles.
