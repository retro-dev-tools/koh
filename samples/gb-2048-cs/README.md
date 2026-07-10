# 2048 — in Koh C#

A complete, bootable Game Boy build of **2048**, written in C# as an ordinary .NET project. It is the
counterpart to the hand-written-assembly [`gb-2048`](../gb-2048) sample: same game, but written as
plain C# and split by responsibility across a handful of files. The project uses the **Koh SDK**, so
building it produces a Game Boy ROM the same way any frontend/backend pair does:

```
Koh C# frontend  →  typed SSA IR  →  hand-written SM83 backend  →  Koh linker  →  2048.gb
```

The twist is that the *exact same* source also compiles under the plain .NET SDK and runs on your
desktop against the [`Koh.GameBoy`](../../src/Koh.GameBoy) framework — its `Hardware.*` / `Gb.*` and
the HAL below backed by real buffers instead of hardware. One source, two targets, and no preprocessor
tricks: it is just normal C#.

The reusable Game Boy surface lives in the **framework** ([`Koh.GameBoy/Hal`](../../src/Koh.GameBoy/Hal)) —
`Lcd`, `Joypad`, `Tilemap` / `TileData` (typed views over VRAM), `Ppu`, and `Direction` — so this
project holds only what is specific to 2048:

| File | Responsibility |
| ---- | -------------- |
| [`Board.cs`](Board.cs) | the rules and state — a 4x4 grid of tile exponents, and the slide/merge/spawn logic, behind a typed API |
| [`Tiles.cs`](Tiles.cs) | the 2048 art — build the framed-block tileset and paint the board, via the framework's `TileData` / `Tilemap` |
| [`Game.cs`](Game.cs)   | the loop tying it together (`Main`) — no raw bytes, registers, or addresses in sight |

## Build & run

```sh
# Compile to a ROM (the Koh SDK runs the compiler/linker after the normal build).
# Produces samples/gb-2048-cs/2048.gb
dotnet build samples/gb-2048-cs

# Play the reference build right here in the terminal (arrow keys to move).
dotnet run --project samples/gb-2048-cs

# Or play the real ROM in the Koh emulator.
dotnet run --project src/Koh.Emulator.App -- samples/gb-2048-cs/2048.gb
```

The project references neither the Koh compiler nor the linker — only the `Koh.GameBoy` runtime. The
`Koh.Sdk` (`sdk/Koh.Sdk`) owns the build-time toolchain and, after the ordinary C# build, invokes an
in-process MSBuild task ([`CompileKohRom`](../../src/Koh.Build.Tasks)) that emits the `.gb`.

## Controls

| Button | Action |
| ------ | ------ |
| D-pad  | Slide the whole board left / right / up / down |

Every slide that changes the board drops a new tile into a random empty cell (mostly a "2",
occasionally a "4"), then repaints during vertical blank.

## How it works

`Board` stores the grid as **exponents**: `0` = empty, `1` = "2", `2` = "4", … `11` = "2048".
Merging two equal tiles is therefore just `exponent + 1`, which keeps everything in a byte and
makes the slide logic tiny:

- `Board.Slide(Direction)` runs the canonical *compact → merge adjacent equals → compact* pass on
  each of the four lines. Zeroing the absorbed cell means a run like `2 2 2 2` folds into two `4`s
  (never a single `8`), exactly like the real game. `SrcIndex` maps a direction to board indices, so
  one line routine drives all four moves.
- `Board.SpawnTile`, `Board.CanMove`, and `Board.HasWon` complete the game state; the cells live in a
  static WRAM buffer, so nothing is threaded around by hand.
- `Tiles.GenerateTileset` builds the framed-block tileset and `Tiles.RenderBoard` paints the board, both
  through the framework's `TileData` / `Tilemap` abstractions over VRAM. `Game.Main` drives the loop
  with `Lcd`, `Joypad`, and `Ppu.WaitVBlank` — all from the framework.

There is no `int`, no heap, and no garbage collector.

## What subset of C# is this?

"Koh C#" is a systems subset aimed at 8-bit hardware. Between this sample and the framework HAL it
uses, it exercises much of it:

- `byte` / `sbyte` / `ushort` / `bool`, `enum` (`Direction`), and raw pointers (`byte*`)
- code organized as ordinary C#: `static class`es (whose static methods like `Board.Slide` /
  `Lcd.Off` and static fields become the program) in a file-scoped `namespace` — no wrapper or
  preprocessor
- `stackalloc` buffers, `*p`, and pointer arithmetic (`*(p + i)`)
- `if` / `while` / `for` / `switch`, `break` / `continue`, `&&` / `||`, `?:`, `++`/`--`
- the `Hardware.*` surface for memory-mapped I/O (`LCDC`, `BGP`, `JOYP`, `LY`, `DIV`, …) and the
  `Gb.*` memory regions (`Gb.Vram`, `Gb.TileMap`, …) as constant base pointers

Graphics are intentionally minimal — each tile value is a solid framed block in one of the four
DMG shades, generated at boot — because ROM **data** arrays are not yet part of the subset.
A richer tileset (digits per value) is the natural next step once static ROM tables land.

## Tests

The game (plus the framework HAL) is compiled through the real pipeline and run in the emulator by
[`Game2048Tests`](../../tests/Koh.Compiler.Tests/Samples/Game2048Tests.cs): it asserts the sample
builds to a bootable ROM with verifiable IR, and drives the public `Board` / `Tiles` API — slides
in all four directions, spawning, `CanMove`, `HasWon`, and rendering — against known 2048 outcomes.
