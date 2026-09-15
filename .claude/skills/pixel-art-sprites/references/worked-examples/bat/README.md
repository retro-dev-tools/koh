# Worked example: the flagship bat (9 first-hand rounds) and the parts extraction

`build_bat.py` is the complete geometric build (shape_kit) of a 32x32 GBC-palette bat,
refined over 9 render-critique-revise rounds. Load-bearing lessons, in build order:

1. POSE: strong pose asymmetry is illegible at 32px — use the iconic raised-V spread and
   put asymmetry in 1-2px details (ear height, wingtip, scallop count).
2. MEMBRANES: deep trailing-edge valleys + a 1px outline leave no interior — fingers need
   ~4px-wide lobes with SHALLOW (2-3px) valleys, or the wing turns into ink branches.
3. FACE: build it by hand on the grid, not with paint geometry — 2x2 accent eyes carry
   the face at display scale; a maw wider than ~4px reads as decapitation.
4. RICHNESS: ribs, rim light, trailing shade, staggered fur — every big flat field gets
   one deliberate interior break, none larger than ~5x5.

`extract_parts.py` slices the finished bat's refined components into the parts library
(assets/parts/*.json) as palette-role grids; `compose_bat.py` proves the round-trip —
the bat reassembles from parts alone via parts_kit.Composition. This seeded the
compose-and-select methodology: perfect SMALL parts once, assemble and select thereafter.
