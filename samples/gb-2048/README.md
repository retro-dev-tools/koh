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
- BG palette per value family (8 GBC palettes).
- Window-layer HUD shows BEST and SCORE in 7-digit form.
- Cell rendering uses a logarithmic single-character scheme (1..9 = digit tiles, A/B/C = 1024/2048/4096).
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
- This sample originally targeted full OAM-sprite slide animations; the shipped version uses a simpler "snap" transition between moves. The animation state machine is in place; OAM-sprite slides are a follow-up.
