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

## Controls

- **D-pad** — slide tiles left/right/up/down
- **START** at title screen — begin a new game
- **A** at the win screen — continue play toward 4096
- **START** at game-over — return to title

## Features

- MBC5 cartridge with 4 ROM banks; battery-backed SRAM (8 KiB) for best-score persistence.
- GBC-only (CGB flag = `$C0` at byte `$0143`). Will not boot on original DMG.
- BG palette per value family (8 GBC palettes) — applied via CGB attribute bytes after every commit.
- Window-layer HUD shows BEST and SCORE in 7-digit form; refreshed every move.
- Cell rendering uses a logarithmic single-character scheme (1..9 = digit tiles, A/B/C = 1024/2048/4096).
- OAM-sprite slide animation: tiles glide from their pre-move positions to their destinations over 8 frames, then the BG snaps back. Mergers slide into their survivor.
- Sound: move, merge, game-over, and a wave-channel win jingle.
- Save format: `"K248"` magic + best score + last board + last score + Fletcher-16 checksum, gating valid loads.

## Architecture

See `docs/superpowers/specs/2026-04-30-gb-2048-design.md` for the design spec.

ROM bank layout:
- ROM0 — engine (boot, IRQs, OAM DMA trampoline, VBlank queue, input, sound, HDMA helper).
- ROM1 — game logic (RNG, board, score, save, animation, render).
- ROM2 — graphics (font + value tile data).
- ROM3 — screens (title, game over, win).

## Notes

- Public labels use `::` syntax (Koh supports RGBDS-compatible exported-label syntax).
- Slide animation runs in two phases per move:
  - **AnimStart** populates `wMoveIntents` from the pre-move snapshot, writes sprites at source positions, and plays the move/merge SFX.
  - **AnimTick** clears the 4x4 BG region on frame 1 (after the OAM DMA has made the src-position sprites visible), interpolates sprite positions over frames 1..7, and on frame 8 hides sprites, redraws the BG with the new board state and per-value palette attributes, and refreshes the HUD.
