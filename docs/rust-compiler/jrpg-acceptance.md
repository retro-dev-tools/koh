# JRPG acceptance status

Incomplete. The unchanged sample now builds through the ordinary SDK Release
pipeline, producing a131072-byte ROM. Existing emulator visual checks show the
title, overworld and a BAT encounter. They do not establish game acceptance.
Captures are temporary artifacts, not updated sample assets.

## Native acceptance to implement

Reuse `tests/sdk/game.rs` and its existing GameHost external-process fixture.
It already runs the real boot ROM, drives input, rejects runtime aborts, records
VRAM-write violations and dumps WRAM, VRAM, palettes and framebuffer pixels.
Resolve static symbols and field offsets from compiler metadata/layout rather
than assuming legacy addresses or inspecting private C# compiler structures.

- Build actual sample sources/assets in Debug/Release on CGB, round-trip
  canonical ELF and compare SDK ROM with independently invoked Rust linker.
  The sample declares KohCgbOnly and authors color palettes unconditionally;
  preserve that product contract rather than forcing a monochrome game variant.
  Other samples and compiler fixtures retain DMG acceptance coverage.
- Assert initial Hero coordinates1,1, HP20 and level1, title text, uploaded asset
  bytes and four hero OBJ quadrants. Add OAM capture to the fixture if necessary.
- Drive single-cell movement, checking exact coordinate deltas and unchanged
  HP/level. Check collision against bounds, trees, walls, water and NPC occupancy
  using independently specified map cells; correlate OAM with the resulting cell.
- Account for encounters explicitly: inspect state before the move and compute
  expected PRNG transition with an independent16-bit reference, or select and
  document a deterministic input schedule. Do not disable encounters in the
  sample or increase settling budgets to hide incorrect behavior.
- Reach the NPC and assert each exact dialogue string in order. After the last
  page, verify the retained callback returns to the overworld and preserves Hero
  identity, coordinates and stats. This is required sample-level callback proof.
- Assert a complete battle: independently calculate attack damage, enemy turn,
  heal/run behavior and experience/level transition for the observed enemy and
  PRNG state. A screenshot of the menu alone does not satisfy this requirement.
- Check text tile bytes and selected independently decoded source-asset pixels;
  do not use old compiler output or curated screenshots as correctness oracles.

The initial manual walk reaches an encounter before NPC dialogue. That is not a
dialogue pass. Existing postboot LCDC defaults are not hardware specifications;
keep the real boot path and explicit runtime/hardware assertions in acceptance.
