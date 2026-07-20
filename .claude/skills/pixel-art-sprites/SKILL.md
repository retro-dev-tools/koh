---
name: pixel-art-sprites
description: >
  Expert craft workflow for designing small pixel-art sprites (8x8 to 64x64) under tight
  palettes (2-16 colors, e.g. 4-color Game Boy palettes), for agents that draw
  programmatically (index grids rendered to PNG). Use this skill WHENEVER drawing, designing,
  revising, or reviewing pixel art of any kind — sprites, tiles, monsters, characters, icons,
  tile sheets, retro/Game Boy/NES-style art, "draw me a goblin at 32x32", art for a ROM or
  game jam — even if the request doesn't say "pixel art" but the output is a small
  fixed-palette raster image. Also use it when JUDGING or giving revision notes on existing
  sprites.
---

# Pixel Art Sprites

You are drawing blind between renders, and freehand per-pixel placement produces wobbly,
assembled-from-blocks "programmer art" no matter how careful you are. This skill's method,
proven against exactly that failure: CONSTRUCT the sprite from smooth geometry at high
resolution and let the tooling downsample it into clean pixels — curves, volume, and outline
come from math — then hand-polish only the last few pixels where taste is cheap and safe.

Three resources, in the order you'll use them:

1. `references/archetypes.md` — the TASTE library: geometric recipes (proportions as
   bounding-box fractions) for 16 common creatures, stock poses that read at tiny scale,
   and composition laws (mass connectivity, 60/30/10 color areas, head ratios). ALWAYS
   check it before drawing a creature; if your subject is there, build the recipe — do not
   invent anatomy from scratch. The iconic genre archetype beats original anatomy at this
   scale, every time it has been tested.
2. `scripts/shape_kit.py` — the CONSTRUCTION engine: parts as primitives (`ellipse`,
   `capsule` for tapered limbs/necks/tails, `polygon`, `fan` for wing/tail membranes,
   `mirror_x`), composed in a `Figure`, supersampled 8x, downsampled by area-majority.
   The model is SILHOUETTE + PAINT: everything you `add()` unions into ONE mass with ONE
   automatic 1px outline (no interior seams unless you opt in with `seam=True`);
   everything you `paint()` is a flat color region clipped inside that mass, never
   outlined, never eroded. Tiny features (eyes, claw tips) are guaranteed to survive
   downsampling by contract, and fragile diagonal-only joints are auto-fixed at render.
   Build EVERYTHING through it; never place raw silhouette pixels by hand.
3. `scripts/sprite_kit.py` — I/O and LINT: PNG read/write, upscale, display-scale mockups,
   contact sheets, validators (palette, orphans, coverage, background, banding heuristics),
   stats. Never hand-roll PNG code.

The full sourced craft canon (Saint11, Lospec, Jansson, Derek Yu, Dragonflycave) is in
`references/canon.md` — read it for the polish pass, for diagnosing a rejection, and before
designing a sprite SET.

## The build order

Every stage ends with a checkpoint: render, LOOK at both an inspection scale (6-10x) and
the final display scale (1x-3x mockup on the real background), and proceed only when the
check passes. Work dies at display scale, so judge there.

### 0. Intent and recipe

One sentence: subject, pose, and THE ONE feature you will exaggerate (the eye locks onto
exactly one hook at small sizes; three medium features average to a generic blob). Then
open `references/archetypes.md`: if the creature is covered, use its recipe's proportions
and build order directly — the recipe numbers are meant to be fed straight into shape_kit
calls. If it isn't covered, pick the closest covered creature and the "reading pose" that
fits, and adapt. Anchor to a same-resolution reference sprite if one exists (decode and
upscale it).

### 1. Construct the silhouette geometrically — TWO candidates, pick one

Build the figure's `add()` parts in the recipe's order (large masses first) — they union
into one mass with one outline. Then build a SECOND silhouette candidate cheaply: a
different stock pose or a different interpretation of the same recipe (wings higher, a
rearing stance, a tighter crouch). Render both as flat single-color mockups side by side
and pick the one that squints better. Selection is the cheapest taste you can buy — a
single-shot silhouette locks in whatever you happened to try first.

Judge the winner hard: the flat shape must read as ONLY the subject — not a moth, dagger,
box, or blob. Composition laws from the archetypes file apply: one dominant connected
mass, appendages joined by at least 2px, 25-60% coverage, deliberate negative space (the
notch of a neck, the gap under a wing define the read as much as filled pixels).

Geometry gives you smooth, confident curves for free — if an edge looks wobbly at this
stage, fix the part shapes or proportions, never individual pixels. Do not proceed until
the silhouette passes; interior detail cannot rescue a failed outline.

### 2. Light and mass

Commit to ONE light direction (top-left is the genre default; a sprite SET shares one
direction everywhere). Lay the color zones with `paint()` (flats clipped to the
silhouette — they can reuse the same masks as your `add()` parts) and apply `shade()` to
the large regions: a lit band toward the light, a shadow band away, hard-edged.
Flat-facing planes stay flat. The cardinal sin is pillow shading — darkening toward the
outline from every side; the shadow side must terminate abruptly against a real shadow
shape, which is exactly what the light-vector bands give you. Palette mapping follows the
recipe's 60/30/10 rule: dominant color ~60% of the figure, secondary ~30%, accent ~10%
max.

### 3. Hand polish — the last 10%, on the grid

Take the rendered grid and edit PIXELS now, and only now. THE FACE COMES FIRST and gets
its own sub-checkpoint: at these sizes identity lives in the face — place the eye exactly
where the recipe says (the accent cluster goes on the eye unless the recipe says
otherwise), set the jaw/snout mark, then render the mockup and confirm the face reads
before touching anything else. Then: fangs/claws as 1-3px marks, carve
interior structure with thin darkest-color lines inside flat fills (wing bones, belly
seams), break symmetry deliberately (vary one ear, cock the tail — mirrored halves read
as a sticker; chaos reads as noise; ONE break is right), selective outline (lighten the
outline one step where the lit side faces the background — never so far the silhouette
dissolves). Keep every edit to a handful of pixels; if you find yourself redrawing a
region, go back to the geometry.

Dithering: only at a value boundary to fake an intermediate step (THE 4-color trick),
ordered/checker, never random speckle, never blanket texture. Anti-aliasing: default NONE
below 16px (redesign the edge angle instead); at 32px+ only staircases with steps ≥2px,
in the step's own axis. Never AA a clean 45° or straight edge — blur is the one
unforgivable sin.

### 4. Lint

Run `validate_all` + `stats` + `check_joints`. These findings are a HARD GATE — fix and
re-lint until clean, never ship over them: palette strays, orphan pixels (isolated 1px
marks — delete or grow into real clusters, no exceptions), diagonal-only joins, coverage
out of range, missing background corners. Only BANDING findings are advisory: read each
one and either fix it (compress bands near a terminator, reorient off the silhouette
axis, dither the boundary) or consciously waive it (clean intentional 45° edges and
straight limb edges are fine) — and say which, per finding, in your notes.

### 5. Final look — the honest one

Render 1x truth + display-scale mockup on the real background (+ contact sheet beside the
rest of the set). Run the 15-point checklist at the end of `references/canon.md`. The
three highest-yield checks: squint the silhouette again (it degrades as detail
accumulates), verify the one light direction still governs shading + sel-out + cast
shadow, hunt orphans introduced during polish. Present the display-scale mockup to
reviewers, never the flattering 10x. On rejection, diagnose against canon.md §5's failure
table and the creature's "classic mistakes" list in archetypes.md — name the fault before
redrawing.

## Tight palettes (Game Boy 4-color discipline)

| Slot | Duties |
|---|---|
| Darkest | Outline AND core/cast shadow AND carved interior lines |
| Dark mid | The shadow-side mass |
| Light mid | The lit mass |
| Lightest | Background AND highlight (keep highlight clusters clearly interior) |

Spend the two mids on the biggest form read (lit vs shadow), never on a 2px detail. Fake a
5th value with boundary dither. A confident flat 2-3 color block beats a muddy attempt to
shade a 4px feature. Fixed hardware colors can't hue-shift — fake temperature with dither
density instead.

## Sprite sets (bestiaries, tile sheets)

Consistency outranks per-sprite polish: one shared light direction, one outline weight,
one grounding convention (grounded figures share a contact-shadow style; floaters share
breathing room), balanced color identities across the set (each member gets a distinct
dominant; don't let three of four monsters share a hue). Review on a contact sheet, never
one at a time.

## Quick reference

```python
import sys; sys.path.insert(0, "<skill>/scripts")
from shape_kit import Figure, ellipse, capsule, fan, polygon, mirror_x, preview
from sprite_kit import validate_all, stats, render, write_png, upscale, mockup

fig = Figure(s=8)                                   # 8x supersample
body = ellipse(16, 18, 8, 6)                        # coords in target-pixel units
wing = fan(pivot=(10, 14), tips=[(1, 4), (6, 2)], inner_r=3)
fig.add("body", body, "B")                          # add() = SILHOUETTE (one mass, one outline)
fig.add("wingL", wing, "W")
fig.add("leg", capsule(13, 22, 12, 27, 1.6, 1.0), "B")
fig.paint("wing_flat", wing, "W")                   # paint() = flats, never outlined
fig.paint("eye", ellipse(19, 15, 0.9, 0.9), "K")    # tiny parts survive by contract
fig.shade("body", light_deg=225, highlight_fill="H", shadow_fill="D")
pal = {'.': (240,232,200), 'B': (150,90,150), 'H': (170,120,170),
       'D': (110,60,110), 'W': (90,160,100), 'K': (40,32,48)}
grid = fig.render((32, 32), pal, bg_char='.', outline_char='K')  # fix_joints on by default
# ... stage-3 hand polish edits grid rows directly ...
preview(grid, pal, "out/bat")                       # sprite + preview + display mockup
print(validate_all(grid, pal, 32, 32, set(pal.values())), stats(grid, '.'))
```

CLI: `python3 shape_kit.py {demo,selftest}` · `python3 sprite_kit.py {render,validate,sheet,preview,selftest}`
