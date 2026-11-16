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

## Remaining coverage

The sample deliberately has no DMG graphics path; other games and compiler
fixtures cover DMG. These cases do not yet prove all terrain/boundary collisions,
hero OAM quadrants or independently decoded art pixels, heal/run branches,
other enemies, level-up/victory/defeat, debugger breakpoints, physical hardware
or the whole compiler migration. Existing emulator defaults and old compiler
results are not correctness specifications. Curated sample assets stay unchanged.

Isolated initial movement checks2/2 pass360.652s; cleaned dialogue checks2/2
pass178.562s; battle extension2/2 pass57.366s, all exit0. The PR-worktree dialogue
checkpoint passes12/12 selected tests in275.699s. Final PR-worktree battle rerun passes2/2 in56.038s,exit0. The broader
isolated396-case regression passes472.676s,exit0. These are selected checks,
not a new full-workspace/cross-platform result; consult progress.md for the
committed evidence checkpoint.
