# Game Boy 2048 Sample

A polished 2048 game for Game Boy Color, built with the Koh toolchain.

## Build

```sh
./scripts/build.sh        # bash / git-bash
./scripts/build.ps1       # Windows PowerShell
```

Output: `build/2048.gbc`. Open in BGB, SameBoy, or mGBA.

## Controls

(Filled in once gameplay is wired up.)

## Notes

- Public labels use `::` syntax (Koh supports RGBDS-compatible exported-label syntax).
- `koh-link` automatically patches the GB header checksum at `$014D` and global checksum at `$014E–$014F`.
