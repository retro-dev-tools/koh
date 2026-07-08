# 2048 — in Koh C#

A complete, bootable Game Boy build of **2048**, written in C# and compiled by the Koh
compiler platform. It is the counterpart to the hand-written-assembly [`gb-2048`](../gb-2048)
sample: same game, but the source is a single [`2048.cs`](2048.cs) that flows through

```
Koh C# frontend  →  typed SSA IR  →  hand-written SM83 backend  →  Koh linker  →  2048.gb
```

the exact pipeline the platform uses for any frontend/backend pair.

## Build & run

```sh
# Produces samples/gb-2048-cs/2048.gb
dotnet run --project samples/gb-2048-cs

# Play it in the Koh emulator
dotnet run --project src/Koh.Emulator.App -- samples/gb-2048-cs/2048.gb
```

## Controls

| Button | Action |
| ------ | ------ |
| D-pad  | Slide the whole board left / right / up / down |

Every slide that changes the board drops a new tile into a random empty cell (mostly a "2",
occasionally a "4"), then repaints during vertical blank.

## How it works

The board is 16 bytes storing **exponents**: `0` = empty, `1` = "2", `2` = "4", … `11` = "2048".
Merging two equal tiles is therefore just `exponent + 1`, which keeps everything in a byte and
makes the slide logic tiny:

- `SlideLine` runs the canonical *compact → merge adjacent equals → compact* pass over four
  contiguous cells. Zeroing the absorbed cell means a run like `2 2 2 2` folds into two `4`s
  (never a single `8`), exactly like the real game.
- `SrcIndex` maps a direction to board indices, so a single `SlideLine` drives all four moves —
  `MoveDir` just gathers each row/column into a temp, slides it, and scatters it back.
- `SpawnTile`, `CanMove`, and `HasWon` complete the game state.
- `GenTiles` procedurally builds the background tiles, `Render` paints the board into the
  tilemap, and `ReadButtons` / `WaitVBlank` handle input and timing through the built-in
  `Hardware` register surface.

There is no `int`, no heap, and no garbage collector: the whole game runs out of the 16-byte
board that lives in `Main`'s statically-allocated WRAM frame.

## What subset of C# is this?

"Koh C#" is a systems subset aimed at 8-bit hardware. This sample exercises most of it:

- `byte` / `sbyte` / `ushort` / `bool`, `enum`, and raw pointers (`byte*`)
- local arrays (`new byte[16]`, `{ … }` initializers), `&arr[0]`, `*p`, and pointer arithmetic
  (`*(p + i)`)
- `if` / `while` / `for` / `do` / `switch`, `break` / `continue`, `&&` / `||`, `?:`, `++`/`--`
- functions calling functions, with `ref` / `out` parameters
- the `Hardware.*` surface for memory-mapped I/O (`LCDC`, `BGP`, `JOYP`, `LY`, `DIV`, …)

Graphics are intentionally minimal — each tile value is a solid framed block in one of the four
DMG shades, generated at boot — because ROM **data** arrays are not yet part of the subset.
A richer tileset (digits per value) is the natural next step once static ROM tables land.

## Tests

The game's logic is compiled through the real pipeline and run in the emulator by
[`Game2048Tests`](../../tests/Koh.Compiler.Tests/Samples/Game2048Tests.cs): it asserts the sample
builds to a bootable ROM with verifiable IR, and checks `SlideLine`, all four `MoveDir`
directions, `SpawnTile`, `CanMove`, and `HasWon` against known 2048 outcomes.
