# gb-2048-v2 — the north-star sample

2048 written as the **ideal** Koh C# game: this code was authored FIRST, with no regard for what the
compiler or runtime supported at the time, and the framework API + compiler enablers were built to
make exactly this code compile — the program of work in
[`docs/superpowers/specs/2026-07-19-ideal-game-api-design.md`](../../docs/superpowers/specs/2026-07-19-ideal-game-api-design.md).
It now compiles **unmodified**: `dotnet build` emits `2048v2.gb`, `dotnet run` opens it in the Koh
emulator, and `tests/Koh.Compiler.Tests/Samples/Gb2048V2Tests.cs` boots it on the emulator and
plays a scripted game (title → Start → d-pad slides → spawns) as the program's acceptance test.

## What this code demanded (each was a compiler/framework gap when it was written)

| Construct used here | What it forced |
|---|---|
| `Game.Run(new TitleScene())`, `class TitleScene : Scene`, `override Update()` (`Scenes.cs`, `Program.cs`) | Framework `Scene`/`Game.Run` + compiler enabler E2: prefix inheritance field layout (derived fields used to silently OVERLAP the base's) and closed-world devirtualization (type tag at offset 0 + jump-table switch of direct calls) |
| `Line ReadLine(...)` — struct returned by value (`Board.cs`) | Enabler E1: struct returns via per-function static return slots (a hidden result-pointer parameter was tried first and rejected — a recursive callee's frame restore clobbers it) |
| `TileAsset.Define(TileArt)` with no count (`Assets.cs`) | Enabler E4: length-carrying arrays — u16 element count at payload−2, so `array.Length` works across call boundaries |
| `static TileAsset Tiles` / `Timer` fields | Static fields of user struct type (WRAM blob + address semantics) — found and fixed in M1 |
| `Rng.Next()` + `Rng.Next(byte)` overloads | IR symbol names collided for C# method overloads (duplicate linker symbol) — found and fixed in M1 |
| `Input.Repeated`, `Rng`, ctor-carried scene state, indexers, switch expressions, `string` fields | Framework Stage 0 + standard C# the frontend already lowered |

No `unsafe`, no registers, no addresses, no tile counts at call sites — the one deliberate raw-layer
touch is `Rng.Mix(Hardware.DIV)` (human-timing entropy), demonstrating the escape hatch stays open.
