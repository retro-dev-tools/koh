# JRPG native acceptance

The unchanged CGB-only game is built and executed by
`tests/sdk/game.rs::sdk_jrpg_dialogue_and_battle_preserve_hero_state`, using rstest
Debug/Release cases and the existing C# GameHost/emulator. The test copies actual
C# sources and PNG assets into an isolated SDK project. It does not modify the
sample, tracked screenshots or encounter/combat code. The Roslyn DLL reaches the
native compiler, canonical ELF round-trip and Rust linker; SDK and independently
linked ROM bytes must match. The CGB-only header is checked explicitly.

## Observable checks

- Real boot ROM completes; runtime aborts and any mode-3 VRAM writes fail.
- Title/overworld scene identities, initial Hero coordinates1,1, HP20, attack5,
  level1, zero experience and maximum HP20 match independent expectations.
- A left move reaches0,1 and preserves Hero identity/stats. An independent
  16-bit xorshift calculation predicts whether that move produces an encounter
  and which enemy type is selected; native RNG state must match.
- A separate execution seeds the game RNG to2D2D through the test-only memory
  driver, then walks three cells right without encounters. Attempting the NPC's
  occupied cell is blocked and does not consume another random roll.
- All three exact dialogue strings appear in tilemap glyphs, in order. The last
  A press invokes the stored callback and returns to the overworld with the same
  Hero object, coordinates4,1 and unchanged stats.
- Seed0002 then drives an actual Slime encounter at3,1. The independent sequence
  0302,C143,93F3,2C46,4E41 implies two attacks of5 and one enemy attack of4.
  Snapshots require enemy HP8 then3, Hero HP20 then16, five experience, retained
  Hero identity and return to the overworld. Enemy name glyphs must spell SLIME.

Seed writes initialize entropy only; the game still runs its own random-roll,
collision, virtual dispatch, battle and scene code. Symbols and field offsets
come from native metadata/layout, not legacy compiler addresses. Captures include
WRAM/VRAM/palettes/framebuffer; full-image equality is not an acceptance oracle.

The expanded dialogue check found the old attribute writer racing the PPU:
157Debug/239Release dropped-write attempts. The
[guarded VRAM-store fix](vram-write.md) retains the exact zero-violation assertion;
settling budgets were not increased to conceal the defect. Its explicit interrupt
restriction is part of the current graphics contract.

## Visual and font acceptance

The native visual checks decode all seven source PNG sheets with the test-only
`png` crate. An independent search over the32 normalized RGB555 levels determines
each expected channel; palette slot ordering is not copied from the converter.
Every uploaded source pixel is checked through hardware two-plane decoding and
palette RAM. Fixed color vectors and the3C/7E bitplane vector test the oracle.
The initial truncation-based oracle was wrong for grass160/208/96; nearest
normalized quantization gives3333, not3354. Exact pixel assertions were retained.

Checks cover terrain/NPC map cells and attributes, all four Hero OAM quadrants,
OBJ transparency and unused slots, scroll and LCD mode, plus rendered Hero,
tree, NPC, water and Slime regions. Four frames after the first movement input,
the vacated16x16 region must render grass. Initial and restored HUD glyphs are
checked. Title text and mascot must appear by the300th frame from power-on,
including boot; neither the original deadline nor movement budget is increased.

That deadline exposed slow1bpp font expansion in TileSet.Load1bppCore. Hoisting
color masks and walking source/destination pointers removes repeated work;
stores use the existing guarded VRAM intrinsic. Nonempty Load1bpp and LoadDefault
calls explicitly leave interrupts disabled. A separate Rust-driven Mono fixture
checks all16 ink/paper combinations against a per-pixel bitplane oracle, LCD
on/off, both CGB VRAM banks, untouched bank contents, WRAM bank preservation,
tile255 wrapping and empty/null input in Debug/Release on DMG/CGB.

Each SDK case saves title/overworld/dialogue/battle PNGs below the test binary's
TestResults/gb-jrpg/unique-run directory. Standard PNG decoding must reproduce
the captured raw framebuffer exactly. Curated screenshot file names and bytes
are snapshotted before and after execution and must remain unchanged. These
artifacts are diagnostic outputs; the curated images are not golden oracles.

## Remaining coverage

The sample deliberately has no DMG graphics path; other games and compiler
fixtures cover DMG. These cases do not yet prove all terrain/boundary collisions,
heal/run branches,
other enemies, level-up/victory/defeat, debugger breakpoints, physical hardware
or the whole compiler migration. Existing emulator defaults and old compiler
results are not correctness specifications. Curated sample assets stay unchanged.

Isolated initial movement checks2/2 pass360.652s; cleaned dialogue checks2/2
pass178.562s; battle extension2/2 pass57.366s, all exit0. The PR-worktree dialogue
checkpoint passes12/12 selected tests in275.699s. Final PR-worktree battle rerun passes2/2 in56.038s,exit0. The broader
isolated396-case regression passes472.676s,exit0. These are selected checks,
not a new full-workspace/cross-platform result; consult progress.md for the
committed evidence checkpoint.
