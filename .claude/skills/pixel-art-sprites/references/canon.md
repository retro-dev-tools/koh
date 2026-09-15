# Pixel Art Craft Canon — Small Sprites, Tight Palettes

Research digest for teaching AI agents to draw good 8×8–64×64 sprites. Every rule below
is stated as an imperative + a one-to-two sentence "why" + a source. Sources are the
named community canon: Pedro Medeiros / Saint11 (Pixel Grimoire), Lospec's tutorial
catalog and contributing artists (Cyangmou, Artem Brullov), Derek Yu, Arne Niklas
Jansson (androidarts.com), Pixnote's glossary/guides, Pixel Joint's Selective Outlining
challenge, and the Cave of Dragonflies Pokémon spriting guide (the closest thing the
community has to a written spec for GB/GBA-era creature sprites).

---

## 1. Pedro Medeiros (Saint11) — the 8-part "How to Start Making Pixel Art" canon

Saint11's series (`saint11.art/pixel_art_articles/`, mirrored on Medium's *Pixel
Grimoire*) is the most-cited beginner-to-intermediate pixel art curriculum in the
community. It runs in a deliberate order: sketch → cluster → shade → anti-alias/de-band
→ color theory → line discipline → export. That order matters — each rule below is
listed under the article it comes from.

### Article 1 — An Absolute Beginner's Guide
- **Treat pixel art as art, not a filter.** It still requires "anatomy, perspective,
  light and shadow, color theory and even art history" — technical constraints don't
  substitute for fundamentals. *Why:* a tight grid punishes bad fundamentals faster
  than high-res art does; there's nowhere to hide a bad shape. (Saint11, article 1)
- **Start at 16×16.** It's "a good starting point" — small enough to force decisions,
  large enough to have a torso, head, and limbs as separate readable masses. Progress
  to 32×32, then "maybe 48×48 and 16 colors" only after statics are solid. (Saint11)
- **Keep the pencil at exactly 1px.** All line and edge decisions happen at the single-
  pixel level; a soft/anti-aliased brush destroys the grid discipline the medium runs
  on. (Saint11)
- **Constrain the palette on purpose, before drawing.** Starting from an unlimited
  palette leads to indecision; start from a 4-color palette (e.g. ARQ4) and graduate to
  ~12 colors (AAP-Micro12) once comfortable. *Why:* a tiny palette forces every color
  choice to be structural (shape/value) rather than decorative. (Saint11)
- **Draw pixel-perfect lines: 1px wide, diagonal-connected, no stray edges.** An
  "unintentional edge" — a jog in a line that wasn't a deliberate curve step — reads as
  a mistake even when nothing else is wrong. (Saint11)
- **Delete orphan pixels on sight.** A single isolated pixel that doesn't serve the
  composition is pure noise; remove it or grow it into a real cluster. (Saint11)
- **Default to warm light / cool shadow when improvising a ramp.** "Usually you will
  want the cold hues to be your shadows and warm hues to be your key light" — a safe
  default when you have no other lighting reference. (Saint11)
- **Scale for display only in whole integers, nearest-neighbor.** 200% = each pixel
  becomes a clean 2×2 block. Fractional or interpolated scaling blurs the grid the
  whole style depends on. (Saint11)

### Article 2 — Cluster Sketching and Painting (the load-bearing one)
- **A cluster is a contiguous group of pixels of one exact color.** This is the unit
  you should be thinking in — not "pixels," not "lines." (Saint11)
- **Minimize the number of clusters, not the number of pixels.** The goal during
  sketching is the *fewest distinct color masses* that still describe the shape; more
  clusters == more visual noise for the same information. (Saint11)
- **Avoid one-pixel clusters (orphan pixels) "by all means."** They are "responsible
  for the image looking noisy and confusing" — verbatim from the source. If a detail
  truly needs a single pixel of contrast, ask whether it can instead become a 2–3 pixel
  shape. (Saint11)
- **Treat diagonal-only pixel connections as weak.** A cluster that only touches its
  neighbor corner-to-corner reads as separate at a glance even if the tool call it
  "connected" — prefer edge-adjacency for anything meant to be perceived as one shape.
  (Saint11)
- **Cluster borders must step consistently, not zig-zag.** Along a curve, the pixel
  step size should grow or shrink monotonically; a border that goes short-step,
  long-step, short-step ("jaggies") looks accidental rather than intentional. (Saint11)
- **Work big-to-small: block in large color masses first, refine detail in later
  passes.** Committing to fine detail before the big shapes are locked wastes work and
  tends to produce noise because detail decisions get made before structure decisions.
  (Saint11)

### Article 4 — Basic Shading
- **Pick a light direction before you shade a single pixel, and hold it.** "Choosing a
  good light source is a skill as important as rendering the scene correctly" — shading
  without a committed direction is what produces pillow shading (see §5). (Saint11)
- **Never darken the edges of a face/form just because it's an edge.** That's the
  literal definition of the pillow-shading mistake: "a generic shadow around the
  outlines" with no real light source driving it. (Saint11)
- **Flat faces get flat color; only round forms get a ramp.** "Flat faces usually have
  a uniform color in their entirety while rounded shapes can have color ramps." A flat
  plane facing the viewer receives uniform light — shading it anyway is a tell that the
  artist reasoned about outlines, not about the underlying 3D form. If a flat face must
  vary (strong light, tight framing), keep the variation minimal. (Saint11)
- **Fewer tones, deliberately.** Saint11's own example uses two shading colors on
  purpose "to keep the banding effect... to a minimum" — every additional ramp step is
  another edge that can band (see §2). (Saint11)

### Article 5 — Anti-Alias and Banding
- **Hunt for staircases ≥2px per step; leave true 45° single-pixel steps alone.** A
  1-pixel-by-1-pixel staircase (perfect 45°) is already the cleanest diagonal a pixel
  grid can render — anti-aliasing it adds blur for no gain. Only steps 2px or longer are
  candidates for smoothing. (Saint11)
- **Match the anti-alias to the slope's own axis.** A horizontal slope gets horizontal
  half-tone strips; a vertical slope gets vertical strips. Anti-aliasing in the wrong
  axis reads as smearing, not smoothing. (Saint11)
- **Size the half-tone run to the size of the step, not a fixed length.** A longer
  staircase step needs a proportionally longer half-tone bridge; a short bridge on a
  long step under-corrects and a long bridge on a short step over-softens. (Saint11)
- **Banding = distinct parallel color bands that flatten the form.** Fix it by (a)
  compressing the bands into a smaller area near a natural terminator (a shadow edge, a
  crease) rather than spreading them evenly, or (b) rotating the gradient's direction so
  it no longer runs parallel to the silhouette edge. (Saint11 + Jansson, converging
  independently — see §5)
- **Every pixel is a decision that must improve readability.** Saint11's own governing
  principle for when to stop adding anti-aliasing/detail: if a pixel doesn't make the
  read clearer, it's not earning its place. (Saint11)

### Article 6 — Basic Color Theory
- **Shift hue and saturation along the ramp, not just value.** "Instead of using a
  simple value ramp, we will use a hue-shifting and saturation-shifting ramp" — shadows
  should cool and desaturate, highlights should warm and saturate, mirroring how real
  light behaves instead of just scaling brightness. (Saint11)
- **Default to hot light / cold shadow (or the deliberate inverse), never same-
  temperature ramps.** Opposing temperature between light and shadow is what makes a
  limited ramp feel like it's describing real light instead of a flat gradient. (Saint11)
- **Spend saturation like a budget: mostly low, with small high-saturation accents.**
  "Large high saturation areas can also make the eye tired" — reserve peak saturation
  for a small detail (an eye, a gem, a highlight), not the base fill. (Saint11)
- **"Do as much as you can with as little as possible."** Saint11's stated governing
  color philosophy — every added color must be pulling weight the existing palette
  can't. (Saint11)

### Article 7 — Working with Lines
- **At very low resolution, prefer shading/contrast boundaries over drawn lines.**
  Explicit interior lines cost proportionally more at 16–32px than at high-res; a value
  or hue break can often communicate the same edge more cheaply and more readably.
  (Saint11)
- **Never leave pure black lines against a colored fill by default — soften with a
  darker relative of the neighboring color**, especially on curves; use this sparingly
  or the shape gets blurry. (Saint11)
- **A colored (non-black) outline visually grows the shape slightly** — compensate by
  nudging the line position inward if silhouette size must stay exact. (Saint11)
- **Reserve full-black/hardest outline for true silhouette-separating edges** (chin
  against neck, character against background); use lighter lines for soft internal
  transitions. (Saint11)

---

## 2. Lospec canon — cluster theory, banding, selective outlining, AA, dithering, pillow shading, hue shift

### Cluster theory (Lospec / Artem Brullov's "Beginner's Guide to Clusters"; converges with Saint11 §1 and Jansson §5)
- **Think in clusters, not pixels or lines.** A cluster is the smallest meaningful unit
  of pixel art — a patch of same-valued pixels that reads as one thing (a highlight, a
  shadow lobe, a piece of fur). Composing in clusters instead of individual dots is what
  separates readable pixel art from "static." (Lospec/Brullov)
- **Big, few, deliberate clusters beat many small ones at the same total pixel count.**
  Two large well-placed shadow clusters read faster than eight scattered dark pixels
  covering the same area, because the eye groups by shape, not by pixel count.
  (Lospec/Brullov; Jansson independently: "imagine the pixels gravitating towards each
  other to form little patches/clusters" rather than scattering evenly)
- **Build clusters top-down: silhouette mass → big shadow/light clusters → small detail
  clusters**, never the reverse. Detail added before mass decisions locks in noise
  early that's expensive to undo. (Lospec/Brullov, Saint11 art.2 convergence)

### Banding — the "staircase" fault
- **Definition: banding is when adjacent ramp steps form a visible parallel stripe
  pattern instead of reading as continuous form.** It flattens volume — the object looks
  like it's wrapped in bands of colored tape rather than lit. (Jansson; Saint11 art.5)
- **Detect it by finding parallel same-width color stripes that don't follow the form's
  contour.** If you can trace a straight line through several same-color pixels that
  crosses independent surface features, that's banding. (Saint11)
- **Fix 1 — compress:** push the ramp's bands together near a natural terminator (a
  crease, a shadow boundary) instead of spreading them evenly across the form. (Jansson,
  Saint11 convergent)
- **Fix 2 — reorient:** rotate the direction the gradient runs so it stops running
  parallel to a silhouette edge; this both breaks the band and doubles as anti-aliasing.
  (Saint11)
- **Fix 3 — dither, sparingly:** break the band boundary with a checker or pattern
  dither rather than a hard line (see dithering discipline below). (Jansson, Pixnote)
- **Fix 4 — interrupt with a detail cluster:** a texture cluster crossing the band
  boundary breaks the stripe read without adding a new hard edge. (Jansson)

### Selective outlining (sel-out)
- **Start with a full hard (often black) outline around the whole silhouette to lock
  the shape**, then selectively lighten it. (Pixel Joint SelOut challenge; community
  convention)
- **On the lit side of the form, replace the dark outline with a lighter body-relative
  color (or remove it) — on the shadow side, keep the darkest outline** to protect
  silhouette readability where it borders the background. Sel-out is fundamentally an
  application of "the light source already chosen for shading" to the outline itself,
  not a separate decision. (Community canon, cf. Saint11 art.7's line-softening rule)
- **Pick outline color relative to the fill it borders, one step darker than the
  adjacent fill** — never a flat, palette-independent black — so the outline reads as
  part of the same lit object rather than a cutout sticker. (Saint11 art.7 + community
  sel-out convention)
- **Never sel-out so aggressively that the silhouette disappears against the
  background.** The whole point of the technique is softening *interior* reads while
  keeping the *exterior* silhouette intact — background-bordering edges are the last
  place to lighten. (Community canon)

### Anti-aliasing at tiny scales — when it helps vs. when it muddies
- **AA is a targeted fix for staircases ≥2px, not a blanket smoothing pass.** True
  1px-diagonal 45° edges and straight horizontal/vertical edges should never be
  anti-aliased — there's no jaggie to fix, only pixels to blur. (Saint11 art.5; Jansson)
- **Excessive AA reads as blur, not smoothness, at small scale.** Jansson: "Excessive
  Anti-Aliasing can make a piece look blurry and indistinct" — pixel art's whole
  advantage over raster-blur is crisp, intentional edges, and over-AA gives that up
  without gaining real smoothness at 16–32px. (Jansson/androidarts)
- **AA color must be a genuine value/hue bridge between the two colors it separates**,
  not an arbitrary gray — a wrong-value bridge breaks the transition more than no AA at
  all. (Saint11)
- **At 8×8–16×16, default to *no* AA and solve the jaggie with silhouette redesign
  instead.** Below ~16px, a half-tone bridge pixel is a large fraction of the total
  shape and usually reads as a stray color rather than a smoothing; better to change the
  angle of the edge itself.

### Dithering discipline
- **Dithering exists to break banding, not to add "texture" everywhere.** Its
  legitimate job is disguising a hard value-band boundary as a soft one when you don't
  have enough palette steps to ramp smoothly (this is *the* Game Boy 4-shade use case —
  see §4). (Pixnote; Jansson: "used sparingly")
- **Checker (1-1) dither reads as a blended mid-tone at small display scale (1× on a
  low-res screen, or heavily downscaled) but reads as visible noise/texture once the
  sprite is displayed larger or the viewer is close enough to see individual pixels.**
  Match dither pattern density to final display scale, not source-canvas scale.
  (Pixnote/community consensus)
- **Use dithering at edges/transitions (band boundaries), not as a fill texture across
  a whole large flat area**, or it stops reading as a gradient and starts reading as a
  pattern/material in its own right — which may or may not be the intent, so this is a
  deliberate choice, not a default. (Pixnote)
- **Prefer ordered/pattern dither (Bayer-style) over random scatter dither** for game
  sprites — ordered patterns read as intentional texture; random dither reads as noise.
  (Community canon / Pixnote)

### Pillow shading — why it's bad
- **Definition: shading that ramps from dark at the silhouette edge to light at the
  center from *every* direction, with no single light source driving it** — so the
  object looks inflated/padded, like a pillow, rather than lit. (Pixnote glossary;
  Saint11 art.4; Jansson)
- **The tell: value builds up smoothly toward the center and terminates the same way on
  all sides**, instead of building up in the light's direction and cutting off abruptly
  against a real shadow shape. Jansson's fix framing: build value up, then terminate it
  *abruptly* against another gradient/shape rather than fading it uniformly to the
  silhouette edge. (Jansson)
- **Fix: commit to one light direction, then let the far side/underside be flat shadow
  or a hard-edged shadow shape** — not a soft radial falloff. (Saint11 art.4 + Jansson)

### Hue shifting in limited palettes
- **Shift hue (and saturation) along the ramp, not just lightness.** A ramp that only
  scales brightness (adding black/white) looks flat and "muddy" compared to one that
  also rotates hue toward warm in the lights and cool in the shadows. (Saint11 art.6;
  Jansson: shadows lean "grayish warm purple," mids stay the base hue, lights go
  "almost yellowish" for a skin-tone example)
- **Never build a ramp's endpoints from raw black/white mixed into the base hue.**
  Jansson explicitly warns against this — it desaturates and flattens the ramp instead
  of making it feel like light. Use a genuinely different, shifted hue at each end
  instead. (Jansson)
- **Reserve fully-saturated colors for small accents, not the base ramp**, unless
  deliberately doing a neon/glow style — long full-saturation ramps fatigue the eye and
  fight the rest of a naturalistic palette. (Saint11 art.6; Jansson)

---

## 3. Small-creature readability (GB/GBC/NES-era bestiary sprites)

Synthesized from the Cave of Dragonflies Pokémon spriting guide (the community's most
detailed written spec for exactly this problem), Cyangmou/Lospec readability material,
and the general silhouette-first doctrine above, applied to the specific constraint of
identifying a creature in 16×16–64×64 px with 2–4 bits of color.

- **Silhouette must read before any interior detail is added.** If squinting at the
  flat black shape doesn't identify the creature, no amount of internal shading will
  fix it — redesign the outline, don't add detail to compensate. (General canon,
  reinforced by Dragonflycave's emphasis on outline-first construction)
- **Pick exactly one identifying feature and exaggerate it past realism.** At 16–32px
  you get one "hook" the eye locks onto — an oversized head, a signature horn, an
  unmistakable ear shape, a distinct tail silhouette. Classic GB/NES bestiary design
  (Dragon Quest slimes, FFL/SaGa monsters, Pokémon) consistently reads as "committee of
  simple shapes plus one exaggerated feature," not as a scaled-down realistic creature.
  Do not spread emphasis across three "somewhat distinct" features — pick one and push
  it further than feels natural. (General canon; converges with Saint11's "cluster
  minimization" — one big readable feature beats several medium ones)
- **Favor large head-to-body ratios for overworld/portrait-scale creature sprites.**
  A bigger head (i) buys more pixels for the face/eyes, which are the fastest thing a
  human reads for identity and mood, and (ii) reads as "creature" rather than
  "silhouette blob" at a distance. This is why GB-era overworld sprites are
  overwhelmingly big-headed even for "realistic" creatures.
- **Use negative space as a shape-defining tool, not just background.** The gaps
  between limbs, the notch of a neck, the space under an ear — these boundaries define
  the silhouette as much as the filled pixels do. A design that fills too much of its
  bounding box (no negative space) tends to blob together into an unreadable mass at
  small size. (Dragonflycave; general silhouette doctrine)
- **Never reuse the same limb/body-part shape verbatim on both sides of a creature.**
  Dragonflycave, verbatim: "Do NOT use the same body part twice" — perfect symmetry or
  duplicate parts "immediately makes a sprite look fake." A slight asymmetry (different
  arm pose, one ear larger, a scar) reads as alive; exact mirroring reads as a sticker.
- **Fix the light source direction for the whole set and never break it per-sprite.**
  Dragonflycave's documented studio convention: "All Pokémon sprites have their light
  source in the top left if they're facing left and top right if they're facing right."
  Consistency across a bestiary matters as much as correctness within one sprite —
  mixed light directions across a roster reads as visually incoherent even if each
  individual sprite is fine.
- **Keep cast shadows the single darkest value, distinct from form-shadow tones.**
  Dragonflycave distinguishes shape-based (form) shadow from cast shadow, and cast
  shadow is always the darkest step in the ramp — conflating the two flattens the
  read of "what's touching the ground" vs. "what's just the dark side of a curve."
- **Don't invent proportions from scratch — cross-check against a reference sprite at
  the same resolution.** Because "making a big pixel-over is not only harder than a
  small one, but *exponentially* so" (Dragonflycave), proportion errors compound fast;
  anchor head/limb/torso ratios to an existing same-size sprite rather than eyeballing.
- **A confident 2–3-color flat block beats a muddy 6-color attempt at the same size.**
  This is the small-creature corollary of Saint11's "do as much as you can with as
  little as possible" — at 16×16 the color budget per readable feature is often
  1 color, and spending 3 colors trying to shade a 4×4px ear usually just blurs it.

---

## 4. 4-color / limited-palette technique (Game Boy discipline)

The original DMG Game Boy hard-locks every sprite/tile to 4 shades (usually treated as:
darkest, dark-mid, light-mid, lightest/background). This is the tightest constraint in
the whole canon and forces specific, well-documented tricks.

- **The darkest shade does double duty as both outline and deepest shadow.** With only
  4 values total, dedicating a 5th "outline-only" value is a luxury most GB-style
  palettes can't afford — the same darkest shade both bounds the silhouette and reads
  as core shadow/cast shadow. Design the silhouette line and the shadow shapes to share
  that one value cleanly rather than treating them as separate concerns. (Community
  GB-style convention; consistent with Saint11's general "outline is a shading choice,
  not a separate black layer" framing in art.7)
- **The lightest shade does double duty as background *and* highlight/rim light.**
  Symmetric to the above — with 4 total values, the top of the ramp is shared between
  "empty background" and "brightest lit surface," so highlight placement must be
  deliberate about *not* reading as background gaps (keep highlight clusters clearly
  inside the silhouette, not touching the outline).
- **With only 2 "mid" steps available, spend them on the biggest shape read, not fine
  detail.** A 4-color ramp is effectively "background / shadow / highlight / outline"
  with at most one true mid-tone — use it to separate the two largest lit/shadowed
  masses of the form, not to render a 2px texture detail.
- **Use dither at the shadow/light boundary to fake a 5th intermediate value.** This is
  the canonical Game Boy trick for turning 4 hard shades into something that reads as
  smoother lighting: a checker or ordered dither straddling the boundary between the two
  ramp steps simulates an in-between value the hardware palette doesn't actually have.
  (Pixnote/Jansson dithering material, §2, applied to the 4-value case)
- **Carve interior structure with the darkest value as thin "ink lines" inside a flat
  mid-tone fill, rather than adding a whole new shade.** Because you can't afford a
  dedicated shade for a fold, seam, or scale line, GB-style sprites route that detail
  through 1px strokes of the existing outline-shade — the same value already doing
  outline/shadow duty — placed *inside* a flat color mass. This is how pros make 4
  colors read as more: not more values, but reused values applied as line-work on top
  of flat fill.
- **Never spend more than 2 of the 4 shades on background/atmosphere if the subject
  needs to read clearly against it.** Because there is no 5th "reserve" value, palette
  budget is the scarce resource — decide up front how many of the 4 shades belong to
  the subject vs. the environment, and don't let environment detail creep into the
  subject's shade budget or vice versa.
- **A ramp that only has 4 total steps must still hue/temperature-shift in effect even
  though it's usually monochrome green (DMG) or a fixed 4-tone set (Pocket/Super GB
  overlays)** — since true hue-shift isn't available, fake the "warm light / cool
  shadow" feeling with shape and dither density (denser dither = perceived cooler/
  darker) rather than actual hue. (Extrapolated from Saint11's hue-shift rule under the
  GB constraint where hue itself is fixed)

---

## 5. Common amateur failure modes and their fixes

| # | Failure mode | What it looks like | Fix |
|---|---|---|---|
| 1 | **Orphan pixels** | Isolated single pixels floating with no neighbor of the same color; reads as static/dust. | Delete, or grow into a real 2–3px cluster. "Avoid one-pixel clusters by all means" (Saint11); "usually just add noise" (community consensus, §1–2). |
| 2 | **Noisy edges / drawing with lines instead of clusters** | Silhouette or interior boundary built pixel-by-pixel with inconsistent step lengths ("jaggies"); looks hand-jittered rather than drawn. | Redraw the border following a consistent step pattern (Jansson recommends step sequences like "1,1,2,3" or "2,1,2" rather than irregular runs); think in clusters, not individual pixel placements (Saint11 art.2, Lospec/Brullov). |
| 3 | **Flat fills with no ramp, or ramps mixed from black/white** | A shape reads as a single flat color with no sense of form, or a "shaded" shape that looks chalky/muddy. | Build a ramp that shifts hue and saturation, not just adds black/white (Saint11 art.6, Jansson). Reserve true flat fill for genuinely flat-facing planes only (Saint11 art.4). |
| 4 | **Doubled or inconsistent outline weight** | Some edges 1px, others accidentally 2px, or outline color/darkness varies without reason across the same sprite. | Keep the pencil literally locked to 1px (Saint11 art.1); apply sel-out rules (§2) consistently based on light direction, not arbitrarily per-edge. |
| 5 | **Banding** | Visible parallel stripes in a ramp that flatten the form instead of describing volume. | Compress bands near a terminator, reorient the gradient off the silhouette's axis, or dither the boundary (§2, Saint11 art.5, Jansson). |
| 6 | **Pillow shading** | Shape darkens toward its own outline from every direction and lightens toward the center, like an inflated cushion. | Commit to one light direction; let the shadow side be a hard-edged flat/dark shape instead of a radial falloff (Saint11 art.4, Jansson, Pixnote glossary). |
| 7 | **Symmetric stiffness** | Perfectly mirrored body parts/features (both arms, both eyes, both ears identical) that read as a sticker/decal rather than a living creature. | Break exact symmetry deliberately — vary a pose, size, or add a one-sided detail (scar, eyepatch, tilted ear). "Perfect symmetry... immediately makes a sprite look fake" (Dragonflycave); "throw in an eyepatch... small asymmetries go a long way" (community consensus, §3). |
| 8 | **Over-anti-aliasing (blur instead of smoothing)** | Every diagonal and curve is padded with half-tone pixels regardless of step length; the sprite looks soft/out of focus. | AA only staircases ≥2px in the correct axis, sized to the step; leave true 1px-diagonals and straight edges alone (§2, Saint11 art.5, Jansson). |
| 9 | **Random/noise dithering used as a texture everywhere** | Speckled, television-static look across large flat areas instead of at value transitions. | Restrict dither to band/value boundaries; prefer ordered/pattern dither over random scatter; check it still reads at final display scale, not just at 1:1 in the editor (§2). |
| 10 | **Detail added before structure is locked** | Fine interior marks (scales, fur tufts, rivets) drawn before the big silhouette/shadow masses are settled, forcing repeated rework and leaving stray noise behind after edits. | Work big-to-small: silhouette → large light/shadow clusters → small detail clusters, in that order, every time (Saint11 art.2, Lospec/Brullov, §1–2). |
| 11 | **Spreading emphasis across too many "somewhat interesting" features** | A creature/character with three or four medium-strength distinguishing details, none of which dominates — net effect reads as generic. | Pick exactly one feature and exaggerate it further than feels comfortable; let everything else stay simple (§3). |
| 12 | **Ground-contact outline making a subject look like it's floating** | A hard line running along the base of a standing figure that reads as a drop-shadow silhouette rather than a foot planted on the ground. | Remove or soften the base-contact outline; let value/shadow do the grounding instead of a line (Jansson). |

---

## Quick checklist (the load-bearing checks — run this before calling a sprite done)

1. **Silhouette check:** Fill the shape solid black and squint/shrink it. Still
   identifiable? If not, fix the outline before touching color.
2. **One-feature check:** Can you name the single exaggerated feature that makes this
   read as *this* creature/object and not a generic blob? If every feature is medium-
   strength, pick one and push it further.
3. **Cluster check:** Are there any single-pixel or 2-pixel orphan marks not attached to
   a real cluster? Delete or grow them.
4. **Outline consistency check:** Is the outline exactly 1px everywhere, and is its
   color/darkness driven by a consistent light-direction rule (sel-out), not arbitrary?
5. **Light-direction check:** Pick one light direction before shading and confirm every
   shaded surface, every sel-out edge, and every cast shadow agrees with it.
6. **Pillow-shading check:** Does any surface darken toward its own outline on *all*
   sides with no hard shadow-side edge? If yes, that's pillow shading — fix it.
7. **Flat-vs-ramped check:** Is a genuinely flat-facing plane getting an unnecessary
   ramp, or is a rounded form getting a flat fill? Match ramp usage to actual form.
8. **Banding check:** Scan every ramp for parallel stripes that ignore the underlying
   form. Compress, reorient, or dither the boundary.
9. **AA scope check:** Is anti-aliasing applied only to staircases ≥2px in the matching
   axis? Confirm no AA sits on true 1px diagonals or straight edges.
10. **Dither scope check:** Is dithering confined to value-transition boundaries (not
    smeared across flat fills), and does it still read cleanly at the sprite's actual
    display scale?
11. **Palette budget check:** Count colors actually used. In a 4-shade (GB-style)
    palette, confirm darkest = outline+shadow, lightest = background+highlight, and no
    shade is wastefully spent on background at the expense of subject readability.
12. **Symmetry check:** Are both sides of a creature/character exact mirrors? If so,
    introduce a deliberate asymmetry.
13. **Hue-shift check:** Does the shadow end of any ramp lean cooler and the light end
    lean warmer (or vice versa, deliberately), rather than just being darker/lighter
    versions of the same hue?
14. **Ground-contact check:** Is there a stray hard outline along the base of a standing
    figure making it look like a cutout floating above the ground?
15. **Build-order check:** Was this sprite built silhouette → big masses → small detail,
    or was detail added before the big shapes were locked? If the latter, expect noise
    — re-audit for orphan pixels and inconsistent clusters introduced during detailing.

---

## Sources

- Pedro Medeiros (Saint11), *How to Start Making Pixel Art*, 8-part series:
  [saint11.art/pixel_art_articles/article1](https://saint11.art/pixel_art_articles/article1/)
  through article6, plus [Medium/Pixel Grimoire pt.7](https://medium.com/pixel-grimoire/how-to-start-making-pixel-art-7-e504bfa4ddf2)
  ("Working with lines") and pt.8 ("Saving and Exporting"). Index:
  [saint11.art/pixel_articles](https://saint11.art/pixel_articles/), tutorial gallery
  [saint11.art/blog/pixel-art-tutorials](https://saint11.art/blog/pixel-art-tutorials/).
- Lospec tutorial catalog: [Clusters tag](https://lospec.com/pixel-art-tutorials/tags/clusters),
  [Beginner's Guide to Clusters by Artem Brullov](https://lospec.com/pixel-art-tutorials/beginners-guide-clusters-by-artem-brullov),
  [Shapes and Outlines by Cyangmou](https://lospec.com/pixel-art-tutorials/pixel-art-shapes-and-outlines-by-cyangmou),
  [Readability by Cyangmou](https://lospec.com/pixel-art-tutorials/readability-pixel-art-style-possibilities-by-cyangmou),
  [Pillow Shading by Solar Lune](https://lospec.com/pixel-art-tutorials/pixel-art-tips-10-pillow-shading-youtube-by-solar-lune),
  [Hue Shifting by GDQuest](https://lospec.com/pixel-art-tutorials/picking-strong-colors-with-hue-shifting-by-gdquest),
  [Dithering tag](https://lospec.com/pixel-art-tutorials/tags/dithering).
- Arne Niklas Jansson, *Pixel Art Tutorial* (androidarts.com):
  [androidarts.com/pixtut/pixelart.htm](https://androidarts.com/pixtut/pixelart.htm).
- Derek Yu, *Pixel Art: Basics* and *Pixel Art: Common Mistakes*:
  [derekyu.com/makegames/pixelart.html](https://www.derekyu.com/makegames/pixelart.html),
  [derekyu.com/makegames/pixelart2.html](https://www.derekyu.com/makegames/pixelart2.html).
- Pixnote glossary and guides: [Glossary](https://pixnote.net/en/learn/glossary/),
  [Dithering guide](https://pixnote.net/en/learn/dithering/),
  [Outlines/Sel-Out guide](https://pixnote.net/en/learn/outlines/),
  [Character guide](https://pixnote.net/en/learn/character/).
- Pixel Joint, *Weekly Pixel Art Challenge: Selective Outlining* (2007):
  [pixeljoint.com/2007/10/15/2346/Pixel_Art_Challenge-_Selective_Outlining.htm](http://pixeljoint.com/2007/10/15/2346/Pixel_Art_Challenge-_Selective_Outlining.htm).
- The Cave of Dragonflies, *Spriting Guide* (Pokémon-style sprite construction, light
  direction, symmetry, shading, proportions):
  [dragonflycave.com/spriting-guide](https://www.dragonflycave.com/spriting-guide/).
- The Pixel Artist (Jude Buffum), *Top Ten Things New Pixel Artists Should Know*:
  [the-pixel-artist.com/articles/top-ten-things-new-pixel-artists-should-know](https://www.the-pixel-artist.com/articles/top-ten-things-new-pixel-artists-should-know).

Note on §4 (Game Boy 4-color discipline) and §3 (small-creature readability): these two
sections have fewer single canonical named essays than §1/§2/§5; they are synthesized
from the general cluster/silhouette/ramp doctrine above (all individually sourced)
applied specifically to the 4-shade DMG constraint and to GB/GBC/NES-era creature-sprite
conventions, cross-checked against the Dragonflycave guide (which is written directly
against the Pokémon GBA-era sprite spec and is the most detailed primary source found
for that domain) and general dithering/pillow-shading material above.
