# gb-2048-v2 — the north-star sample

2048 written as the **ideal** Koh C# game: the code was authored first, with no regard for what the
compiler or runtime support today, and the framework API + compiler enablers are being built to make
this exact code compile unmodified to a ROM. The program of work is
[`docs/superpowers/specs/2026-07-19-ideal-game-api-design.md`](../../docs/superpowers/specs/2026-07-19-ideal-game-api-design.md).

**This sample does not build yet — by design.** It is excluded from `Koh.Ci.slnf` and joins the
build at the program's final milestone (M5), at which point `dotnet build` emits `2048v2.gb` and
`dotnet run` opens it in the Koh emulator, exactly like `gb-2048-cs`.

## What this code demands (and where each demand is tracked)

| Construct used here | Status | Milestone |
|---|---|---|
| `Input.Pressed/Repeated`, `Rng`, `TileAsset` handles, `Game` frame bracketing | Framework code, compiles today | M1 |
| `Line ReadLine(...)` — struct returned by value (`Board.cs`) | Compiler enabler E1: sret lowering | M2 |
| `class TitleScene : Scene`, `override Update()`, `Game.Run(scene)` (`Scenes.cs`) | Compiler enabler E2: inheritance layout fix + closed-world devirtualization | M3 |
| `TileAsset.Define(TileArt)` with no count (`Assets.cs`) | Compiler enabler E4: length-carrying arrays | M4 |
| Everything else (indexers, auto-props, switch expressions, ctor state, string fields) | Standard C#, lowers today | — |

No `unsafe`, no registers, no addresses, no tile counts at call sites — the one deliberate raw-layer
touch is `Rng.Mix(Hardware.DIV)` (human-timing entropy), demonstrating the escape hatch stays open.
