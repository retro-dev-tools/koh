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

You are drawing blind between renders: you place pixels as text, then see the result only
when you render. Untrained instinct produces "programmer art" — recognizable but amateur.
This skill closes that gap with three things: a strict BUILD ORDER (most amateur mistakes
are sequencing mistakes), the community's craft rules (what pros do differently, and why),
and mandatory LOOK CHECKPOINTS where you render and honestly judge before continuing.

Tooling: `scripts/sprite_kit.py` (stdlib-only) handles PNG I/O, upscaling, in-game-scale
mockups, contact sheets, and craft linting (orphan pixels, banding, coverage, palette,
symmetry stats). Import it or use its CLI; do not rewrite this boilerplate.

The full sourced canon (Saint11, Lospec, Jansson, Derek Yu, Dragonflycave) lives in
`references/canon.md` — read it when you need depth on any rule below, when a client
rejects your work and you need to diagnose why, or before designing a whole sprite SET.

## The build order (never skip a stage, never reorder)

Amateur failure has one root cause: detail before structure. Every stage below ends with a
checkpoint — render, look, and only proceed when the check passes. "Look" means actually
viewing the rendered image at BOTH a large inspection scale (6-10x) and the final display
scale (1x-3x mockup on the real background color). Work dies at display scale, so judge
there.

### 0. Intent — one sentence before any pixels

Write down: the subject, the pose/gesture, and THE ONE identifying feature you will
exaggerate past realism (an oversized head, a signature horn, a wing shape). At small
sizes the eye locks onto exactly one hook; three medium-strength features average out to
a generic blob. If you cannot name the one feature, you are not ready to draw.

Anchor proportions to a same-resolution reference if one exists (decode and upscale it).
Do not invent head-to-body ratios from imagination — small-scale proportion errors
compound. Big heads read better at tiny sizes; that is why GB-era creatures are chibi.

### 1. Silhouette — the shape IS the sprite

Draw the entire figure as ONE flat dark mass (no interior detail, no second color).
Render the mockup at display scale. Squint test: the black shape alone must identify the
subject — not a moth, box, dagger, or blob, but unmistakably THE thing. Use negative
space deliberately: the notch of a neck, the gap under a wing, the space between legs
define the read as much as filled pixels. A silhouette that fills its whole bounding box
blobs together; target roughly 25-60% coverage with real negative space.

If the silhouette fails, fix the OUTLINE — never proceed hoping interior detail will
rescue it. It will not.

Silhouette edge discipline: steps along a curve grow or shrink monotonically (1,1,2,3 —
not 1,3,1,2). A straight 45° run of single-pixel steps is already perfect; an edge that
zig-zags irregularly reads as hand jitter.

### 2. Big masses — light, then shadow, as few clusters as possible

Commit to ONE light direction (top-left is the genre default; whole sprite SETS must
share one direction) and never violate it in any later stage. Split the silhouette into
its 2-4 largest color masses: lit mass, shadow mass, maybe one material break. Think in
CLUSTERS — contiguous same-color patches — not pixels. Two large well-placed shadow
clusters beat eight scattered dark pixels: the eye groups by shape.

The cardinal sin here is pillow shading: darkening toward the outline from EVERY side
with no committed light source, so the form looks inflated. The shadow side terminates
abruptly against a hard-edged shadow shape; it does not fade radially. Flat-facing
planes get flat color; only rounded forms get a ramp.

Checkpoint: render both scales. The form should already read as lit and volumetric with
only 2-4 clusters. If it doesn't, the masses are wrong — do not paper over with detail.

### 3. Detail — carve, don't sprinkle

Add the smallest clusters last: the exaggerated feature first and boldest, then eyes/
face, then material hints. Techniques that work at this scale:

- Carve interior structure with thin lines of the darkest color INSIDE flat fills (a
  wing bone, a belly-plate seam, a fold) instead of spending a palette slot on it.
- Selective outline (sel-out): keep the darkest outline where the figure meets the
  background on the shadow side; on the lit side, lighten the outline to a color one
  step darker than the fill it borders (or drop it). The outline obeys the same light
  direction as the shading. Never lighten so far that the silhouette dissolves.
- Break symmetry deliberately. Perfectly mirrored limbs/ears/eyes read as a sticker,
  not a creature — vary a pose, cock one ear, shift the tail. (Verbatim community rule:
  "Do NOT use the same body part twice.")
- Saturation/brightness is a budget: the most eye-catching color goes on the ONE accent
  (an eye glow, a gem), never large fills.

### 4. Lint and polish

Run the linter: `validate_all` from sprite_kit (orphans, palette, coverage, banding,
background). Then apply the craft rules that separate clean from noisy:

- Orphan pixels (isolated 1px marks) are deleted or grown into real clusters — no
  exceptions. Diagonal-only connections read as disconnected; prefer edge adjacency.
- Banding: parallel same-width color stripes that ignore the form. Fix by compressing
  bands near a terminator, reorienting the gradient off the silhouette axis, or
  dithering the one boundary. The linter flags obvious cases; scan ramps yourself too.
- Anti-aliasing at these sizes: default to NONE below ~16px — redesign the edge angle
  instead. At 32px+, AA only staircases whose steps are ≥2px, in the step's own axis,
  bridge color a true value midpoint. Never AA a clean 45° or a straight edge; over-AA
  reads as blur, and blur is the one thing pixel art must never be.
- Dithering: only at a value boundary to fake an intermediate step (THE 4-color trick),
  ordered/checker pattern, never random speckle, never as blanket texture. Confirm it
  reads as a blend at display scale, not as noise.

### 5. Final look — the honest one

Render: 1x truth, display-scale mockup on the real background, and (for a set) a contact
sheet next to the other sprites. Then run the 15-point checklist at the end of
`references/canon.md`. The three highest-yield checks: squint the silhouette again (it
degrades as detail accumulates), verify one light direction still governs everything
(shading, sel-out, cast shadow), and hunt orphans introduced during detailing.

If you are on a team with an art director or client: present the display-scale mockup,
not the flattering 10x view. If they reject, diagnose against the failure table in
`references/canon.md` §5 before redrawing — name the specific fault (pillow shading,
banding, symmetric stiffness, spread emphasis...), don't just "try again".

## Tight palettes (Game Boy 4-color discipline)

With 4 colors, every color does double duty by design:

| Slot | Duties |
|---|---|
| Darkest | Outline AND core/cast shadow AND carved interior lines |
| Dark mid | The shadow-side mass |
| Light mid | The lit mass |
| Lightest | Background AND highlight (keep highlight clusters clearly interior) |

Spend the two mids on the biggest form read (lit vs shadow), never on a 2px detail. Fake
a 5th value with boundary dither. A confident flat 2-3 color block beats a muddy attempt
to shade a 4px feature. When the palette's fixed colors are given (hardware palettes),
you cannot hue-shift — fake temperature with dither density instead.

## Sprite sets (bestiaries, tile sheets)

Consistency across the set outranks per-sprite polish: one shared light direction, one
outline weight, one grounding convention (grounded figures share a contact-shadow style;
floaters share breathing room), balanced color identities (don't let three of four
monsters be the same hue — give each a distinct dominant + the shared accent). Review on
a contact sheet, never one at a time.

## sprite_kit quick reference

```python
import sys; sys.path.insert(0, "<skill>/scripts")
from sprite_kit import *
pal = {'.': (240,232,200), 'K': (40,32,48), 'V': (150,90,150), 'G': (90,160,100)}
grid = ["."*32 for _ in range(32)]          # rows of chars, one per pixel
px = render(grid, pal)
write_png("out/sprite.png", px)
write_png("out/preview.png", upscale(px, 8))
write_png("out/mockup.png", mockup(px, scale=3))          # display-scale on bg field
problems = validate_all(grid, pal, 32, 32, set(pal.values()))
print(problems, stats(grid, '.'))
```

CLI: `python3 sprite_kit.py {render,validate,sheet,preview,selftest} ...`
