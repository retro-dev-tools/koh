# Managed PPU fixtures

`oracle.cs` generated these fixtures on 2026-11-19 with .NET SDK 10.0.115,
using an assembly reference to the accepted managed engine. No project reference
or Rust code participates in generation.

Managed checkout baseline: `e1336b048a67549ed99a8849ac78479213f570c8`.

Assembly: `/home/minorum/projekty/koh/src/Emulator/Koh.Emulator/bin/Release/net10.0/Koh.Emulator.dll`

Assembly SHA-256: `973a5dbe4f4ede0893a4adbde947e56835823b7afdb9e2292902576ed8512281`.

Create a temporary net10.0 executable project with `oracle.cs` as its program,
and a `Reference` whose `HintPath` names that assembly. Run it with the fixture
output directory as its sole argument. The generator creates its own VRAM/OAM
patterns, registers and palettes; no ROM or boot defaults are synthesized.

Each file contains twelve records: little-endian signed 32-bit elapsed dot,
one-byte IF, little-endian signed 32-bit HBlank event count, then the exact
344-byte managed PPU state. Records cover dots 0, 79, 80, 81, 107, 128, 247,
455, 456, 1000, 65664 and 70224. The final 92160-byte front buffer and
92160-byte back buffer follow, both RGBA8888. File size is 188556 bytes.

The Rust test compares every state byte and every final framebuffer byte,
including both DMG/CGB modes and both OPRI settings. The shared fixture enables
window, 8x16 sprites, scrolling, both VRAM banks and varied CGB attributes.
Focused tests cover individual priority, clipping, timing and palette cases.

SHA-256:

| File | Hash |
| --- | --- |
| Cgb-0.bin | `76abc4f3210a84b058361d53ba77835882d75da8bec92f78f2e0dd1129ddad70` |
| Cgb-1.bin | `7c6ad0ef1f685a0c72e58827ef0698a3f60474c82f203b3dba7c4727829e4d3e` |
| Dmg-0.bin | `58282356c31d08702b7a3937ce069c563fe94151f095d20d1e2817bc94e8e44b` |
| Dmg-1.bin | `15c37e87fde43f2fbecbe8d165efa131fbdc149c871fedff0bc4b3f8f54abf4f` |

Framebuffers are deliberately absent from raw save states, matching the managed
format. The owning machine must preserve rollback on failed restore.
