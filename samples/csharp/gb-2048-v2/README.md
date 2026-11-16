# 2048 scene sample

A C#2048 game with a title screen, directional slides, merges, tile spawning and
a score display. It uses scene overrides, constructors, value-returning line
operations, array-backed board storage, strings and a tile-asset value handle.
These exercise ordinary language semantics; the current framework API is not a
permanent design convention.

From the repository root:

```sh
dotnet build samples/csharp/gb-2048-v2/Gb2048V2.csproj -c Release
dotnet run --project samples/csharp/gb-2048-v2/Gb2048V2.csproj -c Release
```

The SDK invokes Roslyn, native `koh-compile`, and native `koh-link`, producing
`samples/csharp/gb-2048-v2/2048v2.gb`. Start opens the board; the directional pad
slides tiles. The existing Koh emulator runs the ROM.

Native acceptance:

```sh
cargo nextest run -p koh-compile --test sdk -E 'test(sdk_2048_scene_game)'
```

The Rust test builds these actual sources through the SDK in Debug/Release on
DMG/CGB. It boots the existing C# emulator, checks title text, presses Start and
plays eight directional moves. An independent rule implementation checks each
merge, score and valid single-tile spawn against captured WRAM. Every board pixel,
uploaded tile byte and score glyph is checked, with no mode3 write violations.
Captures and generated projects use temporary directories, preserving sample art.

This sample began as a north-star API experiment; its
[historical design notes](../../../docs/superpowers/specs/2026-07-19-ideal-game-api-design.md)
describe the old compiler and ABI. They are not specifications for native code
generation. Source breakpoints and native stack inspection remain separate
whole-migration acceptance gates.
