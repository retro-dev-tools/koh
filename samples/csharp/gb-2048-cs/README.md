# 2048 — in Koh C#

A complete, bootable Game Boy build of **2048**, written in C# as an ordinary .NET project. It is the
counterpart to the hand-written-assembly [`gb-2048`](../../asm/gb-2048) sample: same game, but written as
plain C# and split by responsibility across a handful of files. The project uses the **Koh SDK**, so
building it compiles ordinary C# with Roslyn, then uses the native toolchain:

```
C# → Roslyn DLL/PDB → Rust koh-compile → ELF32/SM83 → Rust koh-link → 2048.gb/KDBG
```

The twist is that the *exact same* source also compiles under the plain .NET SDK and runs on your
desktop against the [`Koh.GameBoy`](../../../src/Compiler/Koh.GameBoy) framework — its `Hardware.*` / `Gb.*` and
the HAL below backed by real buffers instead of hardware. One source, two targets, and no preprocessor
tricks: it is just normal C#.

The reusable Game Boy surface lives in the **framework** ([`Koh.GameBoy/Hal`](../../../src/Compiler/Koh.GameBoy/Hal)) —
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
# Produces samples/csharp/gb-2048-cs/2048.gb
dotnet build samples/csharp/gb-2048-cs

# Build the ROM and play it in the Koh emulator (a window opens).
dotnet run --project samples/csharp/gb-2048-cs

# The managed reference build still ships as the project's own binary — run it directly for the
# terminal renderer (arrow keys to move). The path tracks your build config; Debug is the default.
dotnet exec samples/csharp/gb-2048-cs/bin/Debug/net10.0/Gb2048CSharp.dll
```

The project references neither the Koh compiler nor the linker — only the `Koh.GameBoy` runtime. The
`Koh.Sdk` (`src/Compiler/Koh.Sdk`) owns the build-time toolchain and, after the ordinary C# build, invokes an
small MSBuild task ([`CompileKohRom`](../../../src/Compiler/Koh.Build.Tasks)) that launches
`koh-compile` and `koh-link` to emit the ROM and optional debug information.

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
DMG shades, generated at boot. See the [native compiler contract](../../../docs/rust-compiler/progress.md)
for current supported storage and language features.

## Tests

Native [board execution tests](../../../rust/koh-compile/tests/driver.rs) compile the real
`Board` and `Tiles` sources and run ROMs in the existing emulator, checking independent 2048
outcomes in Debug/Release and DMG/CGB modes. The legacy
[`CilGame2048Tests`](../../../tests/Compiler/Koh.Compiler.Tests/Samples/CilGame2048Tests.cs)
remain pending complete migration; their presence does not establish native whole-game acceptance.
The separate scene-based sample has [SDK game tests](../../../rust/koh-compile/tests/sdk/game.rs).
