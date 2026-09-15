# Sprite Archetype Library — The Iconic Read

You draw creatures from geometric parts (ellipses, tapered capsules, wing fans, polygons).
Your recurring failure is **inventing anatomy from scratch**: a bat built from observed bat
anatomy reads as a moth or a dagger; a wolf built limb-by-limb reads as a scattered pile of
masses. The fix is to reproduce the ICONIC GENRE ARCHETYPE — the shorthand silhouette every
human already carries from Dragon Quest, Final Fantasy, Pokémon, Castlevania, and Zelda
bestiaries. That shorthand is almost always simpler and more exaggerated than reality.

**How to use a recipe.** Read the "Iconic read" first — it is the acceptance test. Build the
parts in listed order; each is placed as a fraction of the bounding box W×H, origin top-left,
so `(0.5W, 0.3H)` is horizontal-center, 30% down. Push the ONE feature past comfort, then run
the Composition Laws before shading. Fractions are starting points for geometry code, not
sacred — but the *proportions between* parts are load-bearing; that is what the recipe protects.

**Conventions.** `dark` = darkest shade (outline + core shadow + carved lines); `mid` = the
two middle shades as one "body" band unless noted; `accent` = the lightest/brightest shade,
spent almost only on the eye. "Fan" = filled circular sector (wing). "Capsule" =
stadium/tapered-oval limb or body. "Teardrop" = an ellipse pulled to a point at one end.

---

## bat (flying, front)

- **Iconic read:** the Halloween "bat-symbol" — a wide horizontal wingspan with 2–3 deep
  scallops per wing and a tiny body pinched in the dead center, so the whole thing reads as
  ONE spread black glyph, not a body with two attached wings.
- **Recipe (build order):**
  1. **Wingspan first, as one connected bar, RAISED.** Two fans pivoting from shoulders at
     `(0.42W, 0.42H)` and `(0.58W, 0.42H)`; each **leading (upper) edge sits at or ABOVE the
     shoulder line** — sweeping UP to an upper knuckle near the top corners `(0.06W, 0.20H)` /
     `(0.94W, 0.20H)`, then out to the side edges at `(0.0W, 0.55H)` / `(1.0W, 0.55H)`. A wing
     leading edge that sags below the body midline (`0.5H`) is forbidden — this is raised or
     level flight posture, never drooping. Wingspan = full width, edge to edge.
  2. **Scallop the trailing (lower) edge:** 3 arcs per wing biting UP into the membrane,
     each ~0.14W wide, valleys reaching `0.45H`. The scallops are the signature; make them
     deep, not shy nicks.
  3. **Body:** one small teardrop, `0.14W` wide, from `(0.5W, 0.40H)` to `(0.5W, 0.62H)`,
     tip down. It is mostly a notch between the wings — **body + head together ≤ 25% of figure
     area**; the wings own the rest.
  4. **Head:** circle `0.16W` at `(0.5W, 0.34H)`, overlapping the body top and the wing seam.
  5. **Ears:** two triangles from the head crown, `0.05W` base, tips at `~0.24H`, tilted
     slightly outward — the only place a bit of asymmetry helps (cock one 1px taller). The
     silhouette's **top edge must read ear-dip-wing-tip**: ears poke up, a dip to the head/
     shoulder, then the wings rise back to the corners.
- **The ONE feature:** the **scalloped wing membrane spanning the full width**. Exaggerate
  span and scallop depth before anything else.
- **Palette mapping:** wings + body one solid `dark` (the glyph reads flat). Keep each
  **membrane interior one flat color with at most 2 ink finger-lines** (`dark` struts on a
  `mid` fill); no other interior seams. `accent` = two eye dots.
- **Classic mistakes:**
  - Wings drooping below the body midline → reads as a moth or a cape. Leading edges stay at
    or above the shoulder line.
  - Wings drawn as separate objects that don't touch the body → floating flaps. Keep an
    unbroken filled bridge from wingtip through body.
  - Feathered/rounded wingtips, or cluttered membrane interiors → reads as a moth or bird. Bat
    wings end in ANGULAR points; interiors stay flat with ≤2 ink lines each.

## wolf — howling (profile)

- **Iconic read:** the muzzle raised to the moon — a long snout on a line from chest to
  sky at ~60°, throat stretched, one straight diagonal running nose-to-tail-tip.
- **Recipe:**
  1. **Body core:** a tapered capsule, chest at `(0.30W, 0.55H)` down to haunch, long axis
     tilted so the front is lifted; length `0.45W`, thickness `0.22H`.
  2. **Neck + head as one raised line:** neck capsule from chest `(0.34W, 0.45H)` up to
     `(0.30W, 0.18H)`; muzzle a **slender** tapered wedge from there pointing up-and-back to
     `(0.20W, 0.10H)` at a **30–45° muzzle-to-skull angle above horizontal**. Keep it thin:
     **muzzle depth (top-to-jaw) ≤ 0.18H at the tip, tapering from ≤ 0.30H at the skull** — a
     thick wedge reads as a boar. Nose is the highest point of the whole sprite.
  3. **Legs:** front pair as ONE thick foreground capsule `(0.34W→0.36W, 0.60H→0.92H)`, a
     thinner back-leg hint `1px` behind it; hind pair likewise at `~0.62W`. Two visible
     legs, not four scattered sticks.
  4. **Tail (MANDATORY — never drop it):** bushy and low, sweeping down-then-out from
     `(0.72W, 0.55H)` to `(0.88W, 0.72H)`, a tapered brush **≥ 0.15W long**. The tail is
     load-bearing for the read; if space gets tight, **shrink the body before cutting the
     tail** — a tailless wolf reads as a goat or deer.
  5. **Ears:** two back-swept triangles on the skull, laid nearly flat along the neck line.
- **The ONE feature:** the **upward muzzle line** — the unbroken chest-to-nose diagonal. If
  that read is clear, the pose says "howl" before any detail.
- **Palette mapping:** body `mid`; belly/underjaw `dark` shadow band; `dark` for legs-behind
  and the open mouth notch; `accent` = one eye + a nick of moonlit highlight on the muzzle top.
- **Classic mistakes:**
  - Thick blunt muzzle → reads as a boar or rhino. Keep the wedge slender (≤0.18H at the tip).
  - Missing tail → reads as a goat or deer. The bushy low tail is mandatory; shrink the body
    before you cut it.
  - Head turned toward the viewer → kills the howl; keep it strict profile, nose up.
  - Four evenly-spaced legs → picket-fence stiffness. Overlap front/back into two leg masses.

## wolf — standing (profile)

- **Iconic read:** the low, level predator — a long horizontal body, head carried LOW and
  forward at or below shoulder height, ears pricked, tail out straight. Menace by horizontality.
- **Recipe:**
  1. **Body:** long tapered capsule, top line level at `~0.45H`, from `0.22W` to `0.74W`,
     thickness `0.24H`; slightly deeper at the chest, tucked at the loin.
  2. **Head:** carried forward and LOW — skull circle `0.16W` at `(0.20W, 0.48H)`, muzzle
     wedge pointing forward-down to `(0.08W, 0.55H)`. The head-top is level with, or below,
     the shoulder — that low carry is the "stalking" tell.
  3. **Ears:** two pricked triangles up off the skull, `~0.10H` tall, one 1px taller/forward.
  4. **Legs:** four, but paired — near front + far front as one 3px-wide mass at `0.30W`, near
     hind + far hind at `0.66W`; hind legs angled with a visible hock bend. Ground line flat.
  5. **Tail:** straight out and slightly down from `(0.74W, 0.44H)` to `(0.92W, 0.52H)`,
     brush-tapered, NOT curled up (curl reads as a happy dog).
- **The ONE feature:** the **low-slung level topline with the head dropped below the
  shoulders**. Exaggerate the horizontal.
- **Palette mapping:** back `mid`, underbelly `dark`, legs-behind `dark` (1 step back from
  legs-front), `accent` eye; a `dark` mouth-line hints at the muzzle split.
- **Classic mistakes:**
  - Head held high like a show dog → reads friendly. Drop it.
  - Tail curled over the back → wrong genus entirely. Keep it low and straight.
  - Body too short/tall → becomes a fox or a corgi. Length ≈ 2× height of the body mass.

## slime (classic JRPG)

- **Iconic read:** the Dragon Quest teardrop/onion dome — a wide rounded base swelling up
  to a single soft peak, two dot eyes and a simple smile low on the front. Utterly bilateral
  and calm.
- **Recipe:**
  1. **Body:** one shape only — a dome: circle of radius `0.42W` centered at `(0.5W, 0.62H)`,
     then pull the top to a rounded point at `(0.5W, 0.14H)` (a soft ogee, not a spike). Base
     is flat-ish and widest at `~0.72H`, sitting on the ground line.
  2. **Base flare:** the bottom `0.1H` spreads slightly wider than the dome (`~0.90W`) with
     1–2 gentle drip bumps — the "settled blob" foot.
  3. **Eyes:** two dots at `(0.40W, 0.52H)` and `(0.60W, 0.52H)`, each `~0.06W`. Low on the
     face, close together.
  4. **Mouth:** a shallow smile arc from `(0.42W, 0.64H)` to `(0.58W, 0.64H)`.
  5. **Highlight:** one crescent/oval sheen upper-left of the dome at `(0.34W, 0.30H)` — the
     wet-glob glint that sells "goo."
- **The ONE feature:** the **single soft peak on a fat round base** — the onion-dome
  silhouette. One peak, never two horns.
- **Palette mapping:** body `mid` fill; `dark` outline + a crescent core-shadow hugging the
  lower-right inside; `accent` = the upper-left glint (bigger than the eyes — the glint is the
  charm). Eyes `dark`.
- **Classic mistakes:**
  - Multiple lumps/bubbles on top → reads as slime *mold*, not a slime. Exactly one peak.
  - Eyes high and far apart → looks like a ghost. Keep them low-center and close.
  - Perfectly circular ball → that's a bouncing ball; the pulled peak is what makes it slime.

## ghost / specter

- **Iconic read:** the bedsheet phantom — a rounded dome head-and-shoulders flowing into a
  wavy scalloped hem, two hollow oval eyes, sometimes an "OoOo" mouth. Bottom dissolves; it
  never has feet.
- **Recipe:**
  1. **Head-body dome:** one continuous shape — a half-ellipse cap, top a circle radius
     `0.34W` at `(0.5W, 0.34H)`, sides dropping straight-ish down to `~0.78H`.
  2. **Wavy hem:** the bottom edge from `0.78H` to `0.96H` is 3–4 scallops/tongues (each
     `~0.22W`), alternating lengths for a drifting feel — asymmetry is CORRECT here.
  3. **Optional arm nubs:** two short capsule stubs poking out at `(0.14W, 0.55H)` /
     `(0.86W, 0.55H)`, raised slightly ("boo" pose). Give one a different angle.
  4. **Eyes:** two tall hollow ovals `0.12W`×`0.18H` at `(0.38W, 0.40H)` / `(0.62W, 0.40H)`
     — cut as background-colored holes, not drawn dots.
  5. **Mouth:** a small vertical oval "o" at `(0.5W, 0.58H)`, also a hole.
- **The ONE feature:** the **hollow negative-space eyes** on a smooth pale dome. The dark of
  the eyes against the light body is the entire read.
- **Palette mapping:** body `accent`/lightest (a ghost is the pale thing on screen); eyes +
  mouth + hem shadow `dark` (via holes / a thin under-hem shadow); `mid` only as a faint
  core-shadow curve down one side. Inverts the usual dark-body scheme.
- **Classic mistakes:**
  - Flat straight bottom edge → reads as a bell or a tooth. The hem MUST wave.
  - Filled black eyes on a dark body → invisible; the eyes must be holes to the background.
  - Giving it legs/feet → grounds it; a ghost's lower body always trails off.

## dragon / drake (profile)

- **Iconic read:** the heraldic wyvern — a serpentine S-curve body, one big bat-wing raised
  high mid-back, a horned head on a long arched neck, jaws open, a long tapering tail. Reads
  as a diagonal S from tail-tip to snout.
- **Recipe:**
  1. **Body S-spine:** lay the whole figure on an S from tail-tip `(0.92W, 0.80H)` sweeping
     up through the haunch `(0.62W, 0.60H)`, dipping at the chest `(0.40W, 0.55H)`, then the
     neck arching up to the head at `(0.20W, 0.22H)`. Body is a tapered tube along this spine.
  2. **Wing (the hero):** one big fan from a shoulder pivot at `(0.50W, 0.45H)`, membrane
     sweeping up and back to a tip near `(0.72W, 0.06H)`; 2–3 finger-struts fanning out with
     scalloped membrane between them, like a bat wing. Wingspan reaches well above the back.
     Draw the near wing large; the far wing is at most a 1–2px sliver behind the neck.
  3. **Head:** wedge skull `0.16W` with a swept-back horn, open jaws (upper + lower wedge with
     a `dark` gap), at the neck's top.
  4. **Neck:** tapered capsule arching chest-to-head, thinner than the body.
  5. **Legs:** one visible foreleg (bent, clawed) at `~0.42W` and one hindleg (big thigh,
     bent hock) at `~0.62W`; far legs are `dark` slivers behind.
  6. **Tail:** long taper from the haunch to a point or arrow-barb at `(0.92W, 0.80H)`.
- **The ONE feature:** the **single large raised wing-fan** breaking the top of the bounding
  box. It, more than the head, says "dragon."
- **Palette mapping:** body `mid`; belly/underwing + far limbs `dark`; wing membrane a
  lighter `mid` with `dark` struts carved in; `accent` = the eye (+ optional a flame/glint at
  the jaw).
- **Classic mistakes:**
  - Straight horizontal body → reads as a lizard or newt. The S-curve is mandatory.
  - Both wings drawn equal size → they fight and flatten; commit to ONE big near wing.
  - Tiny wing → reads as a dinosaur. The wing should rival the body in silhouette area.

## skeleton (front)

- **Iconic read:** the grinning skull-and-ribcage — an oversized round skull with two black
  eye-sockets and a nasal triangle, a ribcage of horizontal bars over a gap, stick limbs.
  Read is skull + rib-bars.
- **Recipe:**
  1. **Skull:** circle `0.30W` at `(0.5W, 0.22H)` — deliberately oversized (skull dominates).
     Two round/oval `dark` eye-sockets at `(0.42W, 0.22H)`/`(0.58W, 0.22H)`, a small inverted
     nasal triangle at `(0.5W, 0.28H)`, and a row of vertical teeth notches along the jaw at
     `~0.32H`.
  2. **Spine + ribcage:** a short spine stub at `0.36H`, then 3–4 horizontal rib bars curving
     down at their ends, spanning `0.32W`→`0.68W`, stacked from `0.40H` to `0.58H`, with
     background gaps BETWEEN them (the gaps are the read).
  3. **Pelvis:** a small `dark`-holed block at `(0.5W, 0.62H)`.
  4. **Arms:** two thin bone capsules from shoulders `(0.34W, 0.40H)`/`(0.66W, 0.40H)` down
     to `~0.66H`, with a 1px elbow gap (upper + forearm bone). Pose one arm raised/bent for
     the mandatory asymmetry.
  5. **Legs:** two thin bone capsules `(0.44W, 0.64H)`/`(0.56W, 0.64H)` down to the floor,
     knee gaps at `~0.80H`.
- **The ONE feature:** the **big round eye-socketed skull**. Oversize it and darken the
  sockets hard.
- **Palette mapping:** bone `accent`/light; sockets, nasal, rib gaps, joint gaps `dark`;
  `mid` as a thin under-shadow on each bone's lower edge to round it. Note: bone is the LIGHT
  element, gaps are the dark — like the ghost, it inverts.
- **Classic mistakes:**
  - Ribs as a solid filled block → loses the ribcage read; the background gaps between bars
    are essential.
  - Symmetric arms hanging straight → puppet. Bend/raise one.
  - Skull too small/realistic → reads as a generic humanoid. Push it to 30%+ of width.

## spider

- **Iconic read:** the radial eight-legged menace — a fat round abdomen, a smaller head/
  cephalothorax, and eight bent legs fanning out symmetrically like brackets, spanning wider
  than the body is tall. A cluster of eyes.
- **Recipe:**
  1. **Abdomen:** big circle/oval radius `0.22W` at `(0.5W, 0.58H)` — the dominant mass.
  2. **Cephalothorax:** smaller circle `0.13W` at `(0.5W, 0.42H)`, touching the abdomen.
  3. **Eight legs:** four per side, radiating from the cephalothorax sides. Each leg is a
     two-segment bent line: out-and-up to a knee, then down to a foot. Knees peak ABOVE the
     body — that high-knee arch is the spider tell. Feet land on/near the floor and at the
     side edges. Angle the four so the front pair reach forward, the back pair reach back;
     don't fan them at identical angles.
  4. **Eyes:** a tight cluster of 2–4 `accent` dots on the cephalothorax front at `~0.40H`.
  5. **Optional fangs/pedipalps:** two tiny `dark` nubs under the head.
- **The ONE feature:** the **high-arched knees** of the legs breaking above the body outline.
  That, plus the count of eight, is the whole identity.
- **Palette mapping:** body `mid`/`dark` (spiders read dark); legs `dark`; a `mid` marking
  (hourglass/spot) on the abdomen; `accent` = the eye cluster.
- **Classic mistakes:**
  - Legs straight out like a sun's rays → reads as a star or asterisk. Bend them at a raised
    knee.
  - Six legs, or legs coming off the abdomen → wrong; eight, all from the cephalothorax.
  - Legs too thin (1px, disconnected) → floating hairs. Use ≥2px and keep them joined to the
    body.

## snake / serpent

- **Iconic read:** the coiled S with a raised, wedge-shaped head and a flicking forked
  tongue — one continuous tapering tube, thick coil at the base, head lifted and facing the
  viewer/forward.
- **Recipe:**
  1. **Coil body:** one tapering tube. Base coil a fat loop centered `(0.5W, 0.70H)`, outer
     radius `0.34W`; the tube narrows continuously from the coil up along an S to the neck.
     Show 1–2 stacked coil arcs with a `dark` seam between wraps.
  2. **Rising neck:** the tube lifts from the coil at `(0.48W, 0.50H)` up to the head at
     `(0.52W, 0.24H)`, thinning as it rises.
  3. **Head:** a diamond/wedge `0.16W` wide, blunt at the back, pointed at the snout,
     tilted forward. Two `accent` eyes near the top-back of the wedge.
  4. **Tongue:** a thin `dark`/`accent` forked flick, 2 tines, out the snout front to
     `(0.66W, 0.22H)` — the instant "snake" signal.
  5. **Belly banding:** optional `dark` cross-bars along the coil for scale rhythm.
- **The ONE feature:** the **raised wedge head with the forked tongue**, sitting atop a
  tapering coil. Push the head lift and the fork.
- **Palette mapping:** body `mid` with `dark` coil-seam and belly bands; head `mid`; `accent`
  eye; tongue a lone `accent`/`dark` fleck.
- **Classic mistakes:**
  - Uniform-width tube (no taper) → reads as a hose or worm. Taper head-to-tail hard.
  - Round head → looks like an eel. Snake head is a diamond wedge, wider than the neck.
  - No coil, just a wavy line → weak. The stacked coil gives it mass and menace.

## rat / mouse

- **Iconic read:** big round ears, a pointed twitchy snout, and a long thin curling tail —
  a small crouched oval body between them. The ears and tail do all the identifying.
- **Recipe:**
  1. **Body:** a crouched egg, `0.34W` long, `0.24H` tall, centered `(0.46W, 0.62H)`, low to
     the ground.
  2. **Head:** circle `0.18W` at the front `(0.28W, 0.52H)`, blending into a pointed snout
     wedge to `(0.16W, 0.58H)` with a `dark` nose dot at the tip.
  3. **Ears:** two big round discs `0.14W` each on the skull crown at `(0.26W, 0.38H)` /
     `(0.36W, 0.40H)` — oversized, the mouse tell. Give the far ear a 1px offset.
  4. **Tail:** a long thin 1–2px line from the rump `(0.62W, 0.62H)` sweeping out and curling
     up to `(0.92W, 0.44H)`. Length ≈ body length. This is the second identifier.
  5. **Legs + whiskers:** tiny foot nubs under the body; 2–3 `accent`/`dark` whisker flecks
     off the snout.
- **The ONE feature:** the **oversized round ears** (for a mouse) — or, if the pose reads
  low and long, the **curling tail**. Pick one and enlarge.
- **Palette mapping:** body `mid`; inner-ear + belly + tail-underside `dark`; `accent` = eye
  + a nose glint; whiskers thin `dark`.
- **Classic mistakes:**
  - Small/pointed ears → reads as a shrew or a generic rodent. Round and oversize them.
  - Thick or straight tail → looks like a rat's cousin only if thin AND curved; keep it a
    fine curl.
  - Body too upright/tall → becomes a squirrel. Keep it crouched and horizontal.

## goblin / imp (front)

- **Iconic read:** the gremlin — an oversized head with a huge hooked/pointed nose, giant
  pointed ears jutting sideways, a wide toothy grin, and a small hunched body. Head is the
  show; body is an afterthought.
- **Recipe:**
  1. **Head:** big circle/pear `0.34W` at `(0.5W, 0.30H)` — dominant, ~45% of figure height.
  2. **Ears:** two large pointed leaf-shapes jutting OUT and slightly up from the skull
     sides, tips at `(0.06W, 0.24H)` / `(0.94W, 0.24H)`, spanning near the full width.
  3. **Nose:** a big `mid` wedge/hook projecting from the face center `(0.5W, 0.34H)` down to
     `(0.5W, 0.42H)` — long and pointed, the imp signature. Larger than realism allows.
  4. **Eyes + grin:** two narrow `accent` slit-eyes at `(0.40W, 0.28H)`/`(0.60W, 0.28H)`
     with heavy `dark` brows; a wide grin with a couple of `dark`/`accent` fang notches at
     `(0.5W, 0.44H)`.
  5. **Body:** small hunched capsule `(0.5W, 0.66H)`, `0.28W` wide, with two short arms; give
     one arm a raised/clutching pose (asymmetry). Little clawed feet.
- **The ONE feature:** the **big hooked nose** (or the huge pointed ears — pick the one your
  pose frames best) on an oversized head. Exaggerate hard.
- **Palette mapping:** skin `mid`; nose-underside, ear-inner, brow, mouth `dark`; a loincloth/
  belt marking one `mid`/`dark` band on the body; `accent` = the slit eyes + a fang glint.
- **Classic mistakes:**
  - Proportional human head → loses all imp character. Head ≥40% of the figure; shrink the body.
  - Symmetric slack face → reads as a doll. The grin + one raised arm must break symmetry.
  - Small nose and rounded ears → generic humanoid. Point and enlarge both.

## bird (flying, profile)

- **Iconic read:** the gull-glyph in flight — a small body with wings swept into a shallow
  "M"/boomerang, a pointed beak forward, a fanned or forked tail behind. Read as the wing
  arc, not the body.
- **Recipe:**
  1. **Wings as the frame:** two wings sweeping from the shoulder `(0.5W, 0.46H)` — the near
     wing forward-up to `(0.22W, 0.22H)`, the far wing back-up to `(0.80W, 0.22H)`, each a
     tapered swept blade with a shallow bend (the "M" dip at the shoulder). Wingspan = full
     width. For a mid-flap look, drop one wingtip lower than the other.
  2. **Body:** a small horizontal teardrop `0.22W`×`0.12H` at `(0.5W, 0.48H)`, pointed toward
     the tail.
  3. **Head + beak:** circle `0.10W` at the front `(0.36W, 0.46H)`; a small `dark`/`accent`
     triangle beak jutting forward to `(0.28W, 0.47H)`.
  4. **Tail:** a short fan or forked wedge off the rear `(0.62W, 0.50H)` to `(0.74W, 0.54H)`.
  5. **Eye:** one `accent` dot on the head.
- **The ONE feature:** the **swept wing arc** (the boomerang/M). Its span and sweep are the
  entire silhouette read; the body is almost incidental.
- **Palette mapping:** wings `mid` with `dark` leading-edge and wingtip; body `mid`; `dark`
  underwing shadow; beak `accent`/`dark`; `accent` eye.
- **Classic mistakes:**
  - Both wings level and identical → reads as a cross/plane or a stiff decal. Offset the tips;
    add the shoulder bend.
  - Wings too short/stubby → reads as a bug. The span should fill the width.
  - Overlarge detailed body → grounds it; keep the body tiny relative to the wings.

## fish

- **Iconic read:** the almond body with a big triangular tail-fin and a round eye up front —
  a smooth teardrop that points into a splayed tail. One top fin, the eye near the mouth.
- **Recipe:**
  1. **Body:** a horizontal almond/teardrop, widest at `~0.38W`, tapering to the tail joint
     at `0.72W`; length `0.55W`, height `0.34H`, centered `(0.42W, 0.5H)`. Blunt round nose
     at the front `(0.16W, 0.5H)`.
  2. **Tail fin:** a triangular fan off the rear joint, splaying out to two points at
     `(0.96W, 0.30H)` and `(0.96W, 0.70H)` with a concave notch between — the caudal "V".
     Big: ~0.25W. This is the read.
  3. **Dorsal fin:** a low triangular sail on the back `(0.44W, 0.30H)`.
  4. **Pectoral fin:** one small fan on the near side at `(0.36W, 0.60H)`.
  5. **Eye + mouth:** a big `accent` eye at `(0.24W, 0.46H)`, close to the nose; a small
     `dark` mouth line at the front tip. Optional gill arc behind the eye.
- **The ONE feature:** the **splayed triangular tail-fin**. Enlarge it; a fish with a small
  tail reads as a seed or a leaf.
- **Palette mapping:** body `mid`; belly `mid`/light and back `dark` (counter-shading — the
  classic fish 2-band); fins a lighter `mid` with `dark` ray-lines carved in; `accent` eye.
- **Classic mistakes:**
  - Tail as a simple point → reads as a teardrop/tadpole. Splay it into a notched V.
  - Eye small or centered → loses the "face-forward" read. Big eye, up near the nose.
  - Symmetric top/bottom fins → looks like a kite. Dorsal on top only; belly stays clean.

## treant / living tree

- **Iconic read:** a broad trunk with a knot-hole face, two arm-branches reaching out, gnarly
  root-feet splaying at the base, and a lumpy canopy crown on top. Wider at canopy and roots,
  pinched at the "waist" — an hourglass-ish tree with a face in the bark.
- **Recipe:**
  1. **Trunk:** a thick vertical capsule/column, `0.34W` wide, from the canopy underside
     `~0.32H` down to the roots `~0.86H`, slightly waisted in the middle.
  2. **Canopy:** a lumpy cloud of 3–4 overlapping circles across the top, spanning `0.10W`→
     `0.90W`, from `0.02H` to `0.36H`. Bumpy outline, not a smooth dome.
  3. **Root-feet:** 3–4 gnarled root prongs splaying from the trunk base out to `0.10W` and
     `0.90W` at the floor `~0.94H`, with `dark` gaps between — a wide grabbing stance.
  4. **Arm-branches:** two branch-limbs from the trunk sides `(0.30W, 0.48H)`/`(0.70W, 0.48H)`
     reaching out and up, each ending in 2–3 twig-fingers. Pose them differently (one up, one
     out) for asymmetry.
  5. **Face:** a `dark` knot-hole face on the trunk at `(0.5W, 0.50H)` — two hollow eyes and a
     jagged bark mouth, carved as holes/shadow, not drawn on top.
- **The ONE feature:** the **knot-hole face in the trunk** paired with the reaching
  arm-branches — a plain tree plus a face and arms is the whole trick. Make the face read.
- **Palette mapping:** bark `mid`; face-holes, root gaps, bark seams `dark`; canopy a distinct
  `mid`/`dark` mass (clearly separate value from trunk so it doesn't merge); `accent` = the
  eyes glinting in the knot-holes.
- **Classic mistakes:**
  - Canopy and trunk the same value → merges into one blob; give the leaves their own band.
  - Straight cylinder trunk with no roots → reads as a pillar or a lamp. Splay the roots.
  - Face drawn as light marks on the bark → weak; carve it as dark hollows.

## eyeball monster

- **Iconic read:** the floating single giant eye — one huge iris/pupil filling most of the
  frame, a ring of sclera, and either tentacle-wisps trailing below or a rim of stubby lashes.
  The pupil is the subject; everything else frames it.
- **Recipe:**
  1. **Eyeball:** one big circle, radius `0.40W`, centered `(0.5W, 0.44H)` — dominates the box.
  2. **Iris + pupil:** concentric inside — iris circle radius `0.24W`, pupil `0.12W`, at
     `(0.5W, 0.44H)`. Offset the iris/pupil slightly off-center (e.g. `(0.54W, 0.46H)`) so it
     reads as LOOKING somewhere — dead-center reads as a target/logo.
  3. **Highlight:** one bright `accent` glint in the upper-left of the pupil/iris at
     `(0.42W, 0.36H)` — the wet catchlight that makes it alive.
  4. **Framing (pick ONE):** either (a) tentacle wisps — 3–4 tapering tendrils trailing down
     from the underside `0.78H` to the floor/edges, or (b) a rim of short `dark` lash-spikes
     around the sclera. Not both.
  5. **Optional veins:** a few thin `dark` vein lines from the rim into the sclera.
- **The ONE feature:** the **off-center pupil with its catchlight** — the gaze. That single
  bright cluster is where all attention goes.
- **Palette mapping:** sclera `accent`/light; iris `mid`; pupil + tendrils + veins + rim
  `dark`; the lone brightest `accent` cluster = the catchlight (not the sclera — the glint).
- **Classic mistakes:**
  - Pupil dead-center → reads as a logo/target, not a creature. Offset the gaze.
  - No catchlight → the eye looks blind/flat/dead. Always add the upper-left glint.
  - Sclera crowded with detail → competes with the pupil. Keep the white plain; the pupil wins.

## mushroom creature

- **Iconic read:** the toadstool with a face — a big domed cap (often spotted) far wider than
  the short stubby stalk beneath it, two dot eyes on the stalk or under the cap. Top-heavy: cap
  dominates.
- **Recipe:**
  1. **Cap:** a wide half-dome, radius `0.42W`, centered `(0.5W, 0.42H)`, flat-bottomed at
     `~0.52H` — spanning most of the width, clearly the biggest mass. Slight overhang lip at
     the bottom edge.
  2. **Spots:** 3–5 `accent`/light round spots scattered (unevenly!) across the cap at e.g.
     `(0.34W, 0.30H)`, `(0.58W, 0.24H)`, `(0.66W, 0.40H)` — varied sizes. The Amanita spots
     are a strong secondary tell.
  3. **Stalk:** a short fat capsule `0.24W` wide from `0.52H` to the floor `~0.92H`, narrower
     than the cap. A small `dark` skirt/ring where it meets the cap.
  4. **Face:** two `dark`/`accent` dot eyes on the upper stalk or cap underside at
     `(0.42W, 0.60H)`/`(0.58W, 0.60H)`; a tiny mouth below. Keep it low and simple.
  5. **Feet:** optional two small nubs at the stalk base if it walks.
- **The ONE feature:** the **oversized spotted cap** dwarfing the stalk. Push the cap width
  and the top-heaviness.
- **Palette mapping:** cap `mid`/`dark` with `accent` spots; stalk `accent`/light; `dark`
  cap-underside shadow + ring; `accent` eyes (or `dark` eyes if the stalk is already light).
- **Classic mistakes:**
  - Cap and stalk similar widths → reads as a lamp or a nail. The cap must dominate.
  - Evenly-gridded spots → looks manufactured. Scatter them at varied sizes/positions.
  - Face on the cap center → competes with the spots; keep the face low on the stalk.

---

## Reading poses — the stock poses that read at tiny scale

At 16–48px you cannot animate nuance; you pick a POSE ARCHETYPE that reads instantly as a
frozen glyph. Five carry almost all cases:

- **Profile walk / stand (side-on).** The default for quadrupeds and any creature defined by
  its topline or a long body (wolf, dragon, snake, fish, bird). Profile removes foreshortening
  and shows the most identifying outline. Pair legs into two masses (near+far overlap), never
  four even sticks. Use when the silhouette lives in the side view.
- **Front-facing, symmetrical WITH ONE BREAK.** The default for face-forward creatures (bat,
  slime, skeleton, goblin, spider, eyeball, mushroom). Build bilaterally for stability, then
  break exactly one thing — cock an ear, raise one arm, offset the gaze, tilt the head 1px.
  Full symmetry reads as a sticker; a single break reads as alive. Use when the face/frontal
  spread is the identity.
- **Rearing / raised (vertical emphasis).** Body lifts, front reaches up — the howling wolf,
  the rearing dragon, the snake's raised head. Signals threat/drama and fills a tall box. Use
  when the creature's story is "rising up" and you have vertical room.
- **Hovering with a motion hint.** For fliers and floaters (bat, bird, ghost, eyeball). Add a
  cue that it isn't grounded: a slight wing-tip offset (mid-flap), a trailing/wavy lower edge,
  no feet, maybe a 1px gap of "air" beneath. Use for anything that should read as airborne.
- **Lunging / attacking (diagonal thrust).** The whole mass tilts onto a diagonal — jaws
  forward, one limb thrust, weight committed. Energetic; reads as action even frozen. Use for
  an aggressive variant. The diagonal is the read; keep the rest of the body behind it.

Rule of thumb: **choose the pose that puts the ONE feature on the outline**, not buried
inside. If the feature is a horn, don't face the creature so the horn foreshortens away.

## Composition laws for 16–64px creatures

- **One dominant connected mass.** The whole creature must read as a single connected shape,
  not a constellation. Every appendage joins its parent mass with a **≥2px-wide join** (a 1px
  or diagonal-only connection reads as severed → floating limb). Before shading, fill the
  sprite solid and confirm no part is an island. Wings, legs, tails, ears all attach with a
  real bridge of pixels.
- **One-mass silhouette, then paint.** Build the ENTIRE silhouette as a single unioned mass
  with ONE continuous 1px outline; interior color regions are flat "paint" laid inside that
  outline, never their own outlined sub-shapes and never adding interior seams. Why: at 16×16
  a per-part outline around every wing/leg/ear eats the interior fill — a 1px border around a
  4px part IS the part — so the sprite turns to sludge; field tests consistently won with
  everything unioned first, outlined once, then flat-colored. (The construction tool now
  enforces the single union + single outline; this law explains why, so you don't fight it.)
- **Placement is still yours, even when survival is guaranteed.** The tool now contracts that
  a part which must survive at tiny scale (the eye, a claw tip) will not be dissolved. That
  guarantee is about existence, NOT position: it is still the artist's job to put the eye
  EXACTLY where the recipe's fraction says — `(0.40W, 0.52H)`, not "somewhere on the head".
  A surviving pixel in the wrong place reads as a blemish; the recipe coordinates are the read.
- **60 / 30 / 10 color-area rule (for a 4-color sprite).** Roughly **60%** of the covered
  (non-background) area is the dominant body value (`mid`), **~30%** the secondary/shadow
  value (`dark` masses + outline), and **~10%** the third value — with the **single brightest
  `accent` cluster ≤3–5% of the area**, spent on the eye/glint. If every color occupies a
  similar area, the sprite reads as noisy and flat. One value must clearly own the sprite.
- **Head-size ratio by canvas.** Small canvases demand big heads (the face is the fastest
  identity read):
  - **16px:** head ≈ **40–50%** of the figure's height. Chibi. The body is almost vestigial.
  - **32px:** head ≈ **30–40%**.
  - **48–64px:** head ≈ **20–30%**; you now have pixels for a real body, but still bias larger
    than realism. A "realistic" 1:7 head ratio never reads at these sizes.
- **Negative space is a design element.** Deliberately place gaps — under a wing, between legs,
  the notch of a neck, the hollow of an eye-socket — so the silhouette has readable concavities.
  Target roughly **25–60% coverage** of the bounding box; a shape that fills its whole box blobs
  together. The gaps identify the creature as much as the fills (the space between spider legs,
  the wave-gaps of a ghost hem, the rib gaps of a skeleton).
- **The ONE accent cluster goes on the eye.** The single brightest/most-saturated pixel-
  cluster is the viewer's first fixation — it must land where the identity is, and for a
  creature that is **almost always the eye** (or the pupil's catchlight). Do not scatter accent
  pixels; concentrate them into one 2–4px cluster. Override this only for a stronger single
  story hook — a fire-breather's flame, a gem on the forehead, a glowing wound — but never
  give a sprite two competing accent clusters. One eye of light, and everything else defers.
- **Build silhouette-first, feature-first.** Name the iconic read and the ONE exaggerated
  feature before placing a pixel (see each recipe's first two lines). Place the dominant mass,
  attach appendages with real joins, carve negative space, THEN shade to the 60/30/10 budget,
  THEN drop the single accent cluster on the eye. Detail before structure is the root of every
  rejected sprite.
