#!/usr/bin/env python3
"""shape_kit: construct small pixel sprites from geometric parts, at high resolution, and
downsample them into clean pixel art. Stdlib only (uses sprite_kit's write_png/upscale/mockup
for PNG I/O; no PIL/numpy). Complements sprite_kit.py (which draws/validates a hand-authored
ASCII grid) by generating that grid FROM shapes instead.

Why this exists: an agent placing pixels freehand, one at a time, produces wobbly outlines and
boxy anatomy — there's no feedback loop cheap enough to catch a curve that's 1px off until it's
rendered, and by then the whole limb needs redrawing. Smooth geometry sidesteps this: draw an
ellipse/capsule/polygon at 8x the target resolution (so a curve is a few dozen samples wide
instead of a handful), then downsample by area coverage. The antialiasing-free "majority wins"
downsample is what turns smooth high-res curves into a single, deliberate-looking pixel step at
target scale, instead of a jagged staircase. An erosion pass then yields a consistent 1px ink
outline (silhouette-preserving: it recolors existing figure pixels, it never adds new ones), and
a light-vector pass yields consistent, one-light-direction shading. This is the "part-ID +
erosion" technique used ad hoc to produce this skill's best hand-made sprite; this module is
that technique made systematic and reusable.

The module's story, end to end: SILHOUETTE (every add()ed part unions into one mass, one 1px
outline around it, no automatic interior seams) + PAINT (color regions clipped to that mass,
pure flats, can't touch the outline) + SHADE (an automatic pair of paint bands per light
direction) + POLISH (hand pixel edits on the rendered grid). Earlier versions gave every add()ed
part its own outline against its neighbors by default; in practice that ate almost the entire
interior at small sizes (a seam between every pair of touching limbs), forced an undocumented
seam_group workaround to keep a tiny part like an eye from getting inked as if it were a
boundary, and offered no protection for a part too small to win its downsample block or for a
fragile diagonal-only joint. Silhouette/paint separation, tiny-part survival, and the
fix_joints connectivity pass exist specifically to fix those three failures; see Figure.add,
Figure.paint, and Figure.render below.

Core model
----------
- Work canvas: the figure is built at N*S x N*S resolution (N = target sprite size, S = the
  supersample factor, default 8), then downsampled to N x N.
- A `Part` has a mask (a set of high-res (x, y) integer pixel coordinates), a fill (palette
  char), a z-order (higher paints over lower), a name, and a `kind` — "add" (a SILHOUETTE part:
  its mask joins the union that defines the figure's single outline) or "paint" (a color flat
  clipped to that union at render time, invisible to the outline/seam/erosion logic entirely).
  Figure.add() makes "add" parts; Figure.paint() and Figure.shade() make "paint" parts.
- Parts are collected in a `Figure` and composited/downsampled/outlined by `Figure.render`.
- All primitive functions take coordinates and lengths in TARGET pixel units (floats), and
  scale internally by `s`; they return masks already in high-res integer coordinates, so masks
  from different primitives compose directly with the boolean ops (union/subtract/intersect are
  literally `set` union/difference/intersection).

Usage
-----
    import sys; sys.path.insert(0, "<skill>/scripts")
    from shape_kit import Figure, ellipse, capsule, preview

    fig = Figure(s=8)
    fig.add("body", ellipse(16, 16, 9, 6), "B", z=1)            # SILHOUETTE: joins the union
    fig.paint("belly", ellipse(16, 19, 6, 3), "P", z=1.2)       # PAINT: a flat, clipped to it
    fig.shade("body", light_deg=225, highlight_fill="H", shadow_fill="S")  # more paint, auto
    fig.add("eye", ellipse(21, 14, 1.2, 1.2), "E", z=3)         # tiny parts survive by contract
    grid = fig.render((32, 32), palette, bg_char=".", outline_char="K")  # fix_joints=True default
    preview(grid, palette, "out/critter")   # writes sprite.png / preview_8x.png / mockup.png

CLI:
    python3 shape_kit.py demo <outdir>   # builds and previews a demo creature
    python3 shape_kit.py selftest        # unit checks; exits nonzero on failure
"""

import argparse
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sprite_kit

DEFAULT_S = 8

# --- Primitives -------------------------------------------------------------------------
# Every primitive samples at high-res PIXEL CENTERS (x+0.5, y+0.5), not corners or raw
# thresholds: centered sampling is what keeps a curve's downsampled edge free of a systematic
# one-pixel bias in a consistent direction. All coordinates/lengths in the *args* are TARGET
# pixel units; masks returned are sets of high-res (x, y) integers (already multiplied by `s`).


def ellipse(cx, cy, rx, ry, rotation_deg=0.0, s=DEFAULT_S):
    """Filled ellipse centered at (cx, cy) with radii (rx, ry), optionally rotated about its
    own center. A pixel is included iff its center lies within the (rotated) ellipse boundary.
    rx == ry gives a circle; used for heads, eyes, bellies, blobs."""
    hcx, hcy = cx * s, cy * s
    hrx, hry = max(rx * s, 0.5), max(ry * s, 0.5)
    theta = math.radians(-rotation_deg)  # rotate the SAMPLE into the ellipse's own frame
    cos_t, sin_t = math.cos(theta), math.sin(theta)
    extent = math.hypot(hrx, hry) + 1.0
    x0, x1 = int(math.floor(hcx - extent)), int(math.ceil(hcx + extent))
    y0, y1 = int(math.floor(hcy - extent)), int(math.ceil(hcy + extent))
    mask = set()
    for y in range(y0, y1 + 1):
        py = (y + 0.5) - hcy
        for x in range(x0, x1 + 1):
            px = (x + 0.5) - hcx
            rx_ = px * cos_t - py * sin_t
            ry_ = px * sin_t + py * cos_t
            if (rx_ / hrx) ** 2 + (ry_ / hry) ** 2 <= 1.0:
                mask.add((x, y))
    return mask


def capsule(x0, y0, x1, y1, r0, r1, s=DEFAULT_S):
    """Tapered limb between (x0, y0) and (x1, y1): a 'circle swept' distance-to-segment test,
    with the radius lerped from r0 (at the (x0,y0) end) to r1 (at the (x1,y1) end) by the
    clamped projection parameter t. r0 == r1 gives a uniform-width limb; r0 != r1 tapers, e.g.
    a snout, tail base, or neck. Covers legs, necks, tails, snouts, horns."""
    hx0, hy0, hx1, hy1 = x0 * s, y0 * s, x1 * s, y1 * s
    hr0, hr1 = max(r0 * s, 0.5), max(r1 * s, 0.5)
    dx, dy = hx1 - hx0, hy1 - hy0
    seg_len2 = dx * dx + dy * dy
    maxr = max(hr0, hr1)
    x0b = int(math.floor(min(hx0, hx1) - maxr - 1))
    x1b = int(math.ceil(max(hx0, hx1) + maxr + 1))
    y0b = int(math.floor(min(hy0, hy1) - maxr - 1))
    y1b = int(math.ceil(max(hy0, hy1) + maxr + 1))
    mask = set()
    for y in range(y0b, y1b + 1):
        py = y + 0.5
        for x in range(x0b, x1b + 1):
            px = x + 0.5
            if seg_len2 <= 1e-9:
                t = 0.0
                cxp, cyp = hx0, hy0
            else:
                t = ((px - hx0) * dx + (py - hy0) * dy) / seg_len2
                t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
                cxp, cyp = hx0 + t * dx, hy0 + t * dy
            r = hr0 + (hr1 - hr0) * t
            ddx, ddy = px - cxp, py - cyp
            if ddx * ddx + ddy * ddy <= r * r:
                mask.add((x, y))
    return mask


def polygon(points, s=DEFAULT_S):
    """Filled polygon via scanline even-odd fill on pixel centers. `points` is a sequence of
    (x, y) TARGET-unit vertices describing a closed ring — do NOT repeat the first point at the
    end, the closing edge (last -> first) is implied. Good for wing membranes, horns, jags, fins;
    self-intersecting input fills by the even-odd rule."""
    if len(points) < 3:
        return set()
    hp = [(px * s, py * s) for px, py in points]
    n = len(hp)
    ys = [p[1] for p in hp]
    y0, y1 = int(math.floor(min(ys))), int(math.ceil(max(ys)))
    mask = set()
    for y in range(y0, y1 + 1):
        py = y + 0.5
        xs = []
        for i in range(n):
            ax, ay = hp[i]
            bx, by = hp[(i + 1) % n]
            if ay == by:
                continue
            if (ay <= py < by) or (by <= py < ay):
                t = (py - ay) / (by - ay)
                xs.append(ax + t * (bx - ax))
        xs.sort()
        for i in range(0, len(xs) - 1, 2):
            xa, xb = xs[i], xs[i + 1]
            for x in range(int(math.floor(xa)), int(math.ceil(xb)) + 1):
                if xa <= x + 0.5 < xb:
                    mask.add((x, y))
    return mask


def fan(pivot, tips, inner_r, s=DEFAULT_S, arc_samples=6):
    """Wing/tail membrane: a filled shape fanning out from `pivot` through each point in `tips`
    (in order along the rim), with the straight edge between every CONSECUTIVE pair of tips
    replaced by a scallop — an inward dip toward `pivot`, of depth `inner_r` (TARGET units), at
    the edge's midpoint. This is what makes a wing/tail read as membrane stretched between
    fingers/rays rather than a solid triangle. The scallop is approximated (per spec) as a
    quadratic Bezier whose control point is the tip-to-tip midpoint pulled `inner_r` toward the
    pivot, sampled at `arc_samples` points — plenty smooth for a shape read at a few dozen
    pixels, and far simpler than a true circular arc. Needs >= 2 tips."""
    if len(tips) < 2:
        raise ValueError("fan needs at least 2 tips")
    px0, py0 = pivot
    verts = [pivot, tips[0]]
    for i in range(len(tips) - 1):
        ax, ay = tips[i]
        bx, by = tips[i + 1]
        mx, my = (ax + bx) / 2.0, (ay + by) / 2.0
        dirx, diry = px0 - mx, py0 - my
        dist = math.hypot(dirx, diry)
        if dist < 1e-6:
            cx, cy = mx, my
        else:
            # A quadratic Bezier only reaches HALF its control point's offset from the chord
            # at t=0.5 (midpoint = 0.25*A + 0.5*C + 0.25*B); placing the control point at
            # 2*inner_r past the chord midpoint is what makes the arc's actual sag AT the
            # midpoint equal exactly inner_r, matching the docstring's promised depth.
            cx, cy = mx + dirx / dist * inner_r * 2, my + diry / dist * inner_r * 2
        for k in range(1, arc_samples):
            t = k / arc_samples
            bxp = (1 - t) ** 2 * ax + 2 * (1 - t) * t * cx + t ** 2 * bx
            byp = (1 - t) ** 2 * ay + 2 * (1 - t) * t * cy + t ** 2 * by
            verts.append((bxp, byp))
        verts.append((bx, by))
    return polygon(verts, s=s)


def mirror_x(mask, axis_x, s=DEFAULT_S):
    """Mirror a mask across the vertical line x = axis_x (TARGET units). Pixel-center exact:
    pixel x (center x+0.5) maps to floor(2*axis_x*s - x - 0.5), the pixel whose center is the
    true reflection of x's center about the axis. This makes mirror_x(mirror_x(m, a), a) == m
    exactly when axis_x*s is an integer (pick the axis at a multiple of 1/s — e.g. the canvas
    center — to guarantee that); a non-grid-aligned axis still mirrors, just without the
    round-trip guarantee."""
    haxis = axis_x * s
    return {(int(math.floor(2 * haxis - x - 0.5)), y) for (x, y) in mask}


def union(*masks):
    """Set union of any number of masks (order-independent)."""
    out = set()
    for m in masks:
        out |= m
    return out


def subtract(a, b):
    """Pixels in `a` that are not in `b`."""
    return a - b


def intersect(a, b):
    """Pixels in both `a` and `b`."""
    return a & b


# --- Part / Figure -----------------------------------------------------------------------


class Part:
    """One named region of the figure: a high-res mask, a palette fill char, a z-order (higher
    z paints over lower z where masks overlap), and a `kind`:

      - "add" parts (from Figure.add) join the SILHOUETTE union — the figure's outline and
        foreground/background decision derive from the union of every "add" part's mask alone.
      - "paint" parts (from Figure.paint / Figure.shade) are pure color flats: at render time
        their mask is clipped to the "add" union, and they never contribute to the outline, a
        seam, or the foreground decision — they can only recolor pixels that were already part
        of the figure, never add or remove any.

    `seam`, only meaningful on an "add" part, opts it INTO an interior outline line against
    whichever other "add" part it borders (see Figure.add's docstring for when to reach for
    this — it's the rare exception; the default for every part is no interior seam at all)."""

    __slots__ = ("name", "mask", "fill", "z", "kind", "seam")

    def __init__(self, name, mask, fill, z, kind="add", seam=False):
        self.name = name
        self.mask = mask if isinstance(mask, (set, frozenset)) else set(mask)
        self.fill = fill
        self.z = z
        self.kind = kind
        self.seam = seam


def _mask_centroid_pixel(mask, s):
    """The TARGET-pixel coordinate of a high-res mask's centroid (its own average x/y, floor-
    divided down to target units) — where Figure.render's tiny-part survival rule guarantees a
    part a pixel even when ordinary area-majority downsampling would have dropped it."""
    if not mask:
        return None
    n = len(mask)
    cx = sum(x for x, y in mask) / n
    cy = sum(y for x, y in mask) / n
    return int(cx // s), int(cy // s)


def _find_diagonal_bridges(is_fg, w, h):
    """Find every place a smaller foreground component touches the sprite's LARGEST foreground
    component only via a diagonal step: 4-connectivity keeps them separate components, even
    though 8-connectivity would merge them into one — exactly the fragile 1px joint that can
    disappear (or read as a rendering glitch) at another scale. `is_fg(x, y)` is any predicate
    over pixel coordinates, so this one implementation backs both check_joints (grid characters)
    and Figure._fix_joints (a pre-outline id-grid). Returns a list of dicts, one per diagonal
    touch found: {"offshoot": (x,y) in the smaller component, "main": (x,y) in the largest
    component, "promote_candidates": [(x,y), (x,y)]} — the two orthogonal cells that would each,
    alone, turn that touch into a real 4-connected join."""
    visited = [[False] * w for _ in range(h)]
    components = []
    for y in range(h):
        for x in range(w):
            if not is_fg(x, y) or visited[y][x]:
                continue
            comp = []
            stack = [(x, y)]
            visited[y][x] = True
            while stack:
                cx, cy = stack.pop()
                comp.append((cx, cy))
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = cx + dx, cy + dy
                    if 0 <= nx < w and 0 <= ny < h and is_fg(nx, ny) and not visited[ny][nx]:
                        visited[ny][nx] = True
                        stack.append((nx, ny))
            components.append(comp)
    if len(components) <= 1:
        return []
    components.sort(key=len, reverse=True)
    main = set(components[0])
    bridges = []
    for comp in components[1:]:
        for (cx, cy) in comp:
            for dx, dy in ((1, 1), (1, -1), (-1, 1), (-1, -1)):
                nx, ny = cx + dx, cy + dy
                if (nx, ny) in main:
                    bridges.append({
                        "offshoot": (cx, cy),
                        "main": (nx, ny),
                        "promote_candidates": [(cx + dx, cy), (cx, cy + dy)],
                    })
    return bridges


def check_joints(grid, bg_char="."):
    """Detect fragile diagonal-only joints in a rendered ASCII grid (shape_kit's own output, or
    a hand-authored one). Unlike sprite_kit.check_orphans's diagonal-only check — a purely
    LOCAL, single-pixel lint — this is a GLOBAL connectivity check: it 4-connects the whole
    foreground into components first, so a multi-pixel offshoot (a whole claw, not just its
    tip) that dangles off the main mass by one diagonal step is caught too, not only a lone
    pixel. Returns a list of problem dicts (empty = no fragile joints); see
    _find_diagonal_bridges for the dict shape. Gate a build on `not check_joints(grid, bg_char)`,
    or just render with fix_joints=True (Figure.render's default) and never see the problem."""
    h = len(grid)
    w = len(grid[0]) if h else 0

    def is_fg(x, y):
        return grid[y][x] != bg_char

    return _find_diagonal_bridges(is_fg, w, h)


class Figure:
    """A composition of Parts, rendered together into a downsampled, outlined ASCII grid."""

    def __init__(self, s=DEFAULT_S):
        self.s = s
        self.parts = []
        self._by_name = {}

    def add(self, name, mask, fill, z=0, seam=False):
        """Add a SILHOUETTE part: its mask joins the union of every add()ed part, and that
        union ALONE defines the figure's outline (a 1px erosion border of the union against the
        background) and its foreground/background decision. There is no automatic seam between
        two add()ed parts anymore, no matter how they overlap or touch — build the whole
        anatomy with add() calls the way you'd union clay masses, and by default it reads as
        ONE mass with ONE outline, not a seam-hatched patchwork.

        Pass seam=True on the rare part that genuinely wants a deliberate interior line against
        whatever else it borders — e.g. a wing membrane against a body, where the hard
        separation IS the read. Everything else, including a tiny part like an eye, blends into
        the mass with no line by default; there is no more seam_group workaround to reach for.

        Later calls with a higher `z` paint over earlier/lower ones wherever masks overlap in
        the composite FILL; ties keep insertion order (last-added wins). z only affects which
        color wins where add()ed parts overlap — it has no effect on the silhouette/outline."""
        part = Part(name, mask, fill, z, kind="add", seam=seam)
        self.parts.append(part)
        self._by_name[name] = part
        return part

    def paint(self, name, mask, fill, z=0):
        """Add a PAINT region: a pure color flat, CLIPPED at render time to the union of every
        add()ed part. Paint can only recolor pixels that already belong to the silhouette — it
        never grows the figure, never shrinks it (no erosion), and never draws a seam or moves
        the outline by even one pixel, regardless of where its mask reaches. This is how a
        color zone gets to freely follow its own mask (an ellipse, a band, whatever reads well)
        without worrying about spilling past the silhouette or leaving a stray line behind.

        Later calls with a higher `z` paint over earlier/lower ones (including over the add()ed
        part beneath them) wherever masks overlap; ties keep insertion order."""
        part = Part(name, mask, fill, z, kind="paint", seam=False)
        self.parts.append(part)
        self._by_name[name] = part
        return part

    def part(self, name):
        """Look up a previously added Part by name (e.g. to read/mutate its mask)."""
        return self._by_name[name]

    def shade(
        self,
        part_name,
        light_deg=225.0,
        highlight_frac=0.35,
        shadow_frac=0.35,
        highlight_fill=None,
        shadow_fill=None,
    ):
        """Paint a highlight band and/or a shadow band over an existing part's silhouette, as
        new PAINT regions (Figure.paint) just above it in z. Because paint is always clipped to
        the silhouette union and never contributes an outline or a seam, a highlight/shadow band
        can no longer pick up a stray interior line against its own base part the way it could
        when shade() built add()ed parts — the old seam_group workaround this used to require
        doesn't exist anymore because there's nothing left for it to opt out of.

        Light model, precisely: `light_deg` is a screen-space angle (x right, y down) with
        dx = cos(light_deg), dy = sin(light_deg); since y grows downward, increasing degrees
        sweeps clockwise as normally viewed, and the default 225 deg points up-and-left — the
        genre-standard top-left light source. (dx, dy) is the unit vector pointing FROM the
        surface TOWARD the light.

        For each requested band: let k = max(frac * sqrt(area(part)) / 2, s) pixels (area in
        high-res pixels; the `s`-pixel floor guarantees at least one target pixel of band width
        even for a tiny part). Highlight = part.mask minus (part.mask shifted AWAY from the
        light by k); this leaves exactly the band nearest the light (the shifted-away copy no
        longer covers the near-light edge, so subtracting it keeps only that edge). Shadow is
        the mirror: part.mask minus (part.mask shifted TOWARD the light by k), leaving the band
        farthest from the light. Both are simple translate + set-difference — no radial falloff,
        no per-pixel light math — by design: predictable, and 2-4 clusters is what the craft
        canon wants, not a smooth ramp (see SKILL.md's "cardinal sin: pillow shading").
        """
        base = self._by_name[part_name]
        mask = base.mask
        area = len(mask)
        if area == 0:
            return
        theta = math.radians(light_deg)
        dx, dy = math.cos(theta), math.sin(theta)
        base_k = math.sqrt(area) / 2.0
        sub_z = base.z + 0.5

        if highlight_fill is not None:
            k = max(highlight_frac * base_k, self.s)
            offx, offy = -round(dx * k), -round(dy * k)  # shift away from the light
            shifted = {(x + offx, y + offy) for (x, y) in mask}
            band = mask - shifted
            self.paint(f"{part_name}.highlight", band, highlight_fill, z=sub_z)

        if shadow_fill is not None:
            k = max(shadow_frac * base_k, self.s)
            offx, offy = round(dx * k), round(dy * k)  # shift toward the light
            shifted = {(x + offx, y + offy) for (x, y) in mask}
            band = mask - shifted
            self.paint(f"{part_name}.shadow", band, shadow_fill, z=sub_z)

    def _downsample_majority(self, id_grid_hires, width, height, min_pixel_frac):
        """Shared area-majority downsample: each target pixel looks at its own SxS block of a
        high-res id-grid and takes the id with the most covered cells (ties broken toward the
        higher z). If the block's total covered fraction is < min_pixel_frac, the target pixel
        is background (-1) instead. Used for both the structural (silhouette) grid and the
        color (fill) grid — same algorithm, different input grid."""
        s = self.s
        block_area = s * s
        target_id = [[-1] * width for _ in range(height)]
        for ty in range(height):
            base_y = ty * s
            for tx in range(width):
                base_x = tx * s
                counts = {}
                for dyy in range(s):
                    row = id_grid_hires[base_y + dyy]
                    for dxx in range(s):
                        pid = row[base_x + dxx]
                        if pid != -1:
                            counts[pid] = counts.get(pid, 0) + 1
                fg = sum(counts.values())
                if fg == 0 or fg / block_area < min_pixel_frac:
                    continue
                best_id = max(counts, key=lambda pid: (counts[pid], self.parts[pid].z))
                target_id[ty][tx] = best_id
        return target_id

    def _block_coverage_best(self, tx, ty, indices):
        """High-res coverage of each candidate part's mask inside target pixel (tx, ty)'s SxS
        block, even far below min_pixel_frac. Used by _fix_joints to pick which of a diagonal
        joint's two orthogonal promote_candidates was actually closest to being there already —
        "closest" meaning the candidate whose real geometry covers more of that block, not an
        arbitrary pick."""
        s = self.s
        base_x, base_y = tx * s, ty * s
        counts = {}
        for idx in indices:
            c = 0
            for (x, y) in self.parts[idx].mask:
                if base_x <= x < base_x + s and base_y <= y < base_y + s:
                    c += 1
            if c:
                counts[idx] = c
        if not counts:
            return None, 0
        best = max(counts, key=lambda i: (counts[i], self.parts[i].z))
        return best, counts[best]

    def _fix_joints(self, structural_target_id, color_target_id, width, height, structural_indices):
        """Connectivity fix-up: promote every diagonal-only touch between the main mass and a
        smaller offshoot (found by _find_diagonal_bridges) into a real orthogonal join, by
        filling whichever of the two candidate cells had the higher actual high-res coverage
        (see _block_coverage_best) — i.e. whichever candidate the original geometry came
        closest to reaching anyway. Mutates both id-grids in place; runs before the outline
        pass, so the promoted pixel gets outlined normally like any other figure pixel."""
        def is_fg(x, y):
            return structural_target_id[y][x] != -1

        for bridge in _find_diagonal_bridges(is_fg, width, height):
            candidates = []
            for (cx, cy) in bridge["promote_candidates"]:
                if 0 <= cx < width and 0 <= cy < height and structural_target_id[cy][cx] == -1:
                    best_id, cov = self._block_coverage_best(cx, cy, structural_indices)
                    candidates.append(((cx, cy), best_id, cov))
            if not candidates:
                continue
            candidates.sort(key=lambda c: c[2], reverse=True)
            (px, py), best_id, cov = candidates[0]
            if best_id is None:
                ox, oy = bridge["offshoot"]
                best_id = structural_target_id[oy][ox]
            structural_target_id[py][px] = best_id
            if color_target_id[py][px] == -1:
                color_target_id[py][px] = best_id

    def render(
        self,
        size,
        palette=None,
        bg_char=".",
        outline_char=None,
        min_pixel_frac=0.5,
        fix_joints=True,
    ):
        """Composite, downsample, and outline the figure into an ASCII grid (list of strings,
        one row each) compatible with sprite_kit's render()/validate_all()/etc.

        `size` is (width, height) in target pixels (an int is treated as square). Steps:
          1. Composite two high-res id-grids: a STRUCTURAL one from "add" parts only (this
             alone is the silhouette), and a COLOR one from "add" + "paint" parts together in
             true z order, where a "paint" pixel is only kept if the structural grid already
             has something there — the clip that keeps paint from ever growing, shrinking, or
             seaming the silhouette.
          2. Downsample both grids by area-majority (see _downsample_majority); a structural
             block below min_pixel_frac forces that target pixel to background in BOTH grids
             (color can never show where the silhouette doesn't).
          3. Tiny-part survival: any part (add or paint) whose high-res mask covers >= 0.4 of
             its own mask-centroid's target-pixel block is guaranteed to show there — an "add"
             part can win a target pixel outright even from background; a "paint" part only
             recolors a pixel the structural grid already claims. This is what keeps a small
             but deliberate part (an eye, a claw tip) from vanishing under ordinary majority
             rule just because a neighboring part's mask dominates the same block, or because
             its own coverage sits under the default 0.5 min_pixel_frac.
          4. If fix_joints (default True): promote every diagonal-only touch between the main
             mass and a smaller offshoot into a real orthogonal join (see _fix_joints /
             check_joints).
          5. Fill: each foreground target pixel takes its color id's fill char.
          6. Outline (only if outline_char is given): a foreground pixel becomes outline_char if
             any of its 4 neighbors is background or off-canvas (the silhouette's own 1px
             erosion border — the ONLY source of outline by default), OR if its own part has
             seam=True and a neighbor belongs to a different add()ed part (the opt-in interior
             line). Outline pixels are RECOLORED foreground pixels, never new ones, so the
             silhouette can only erode, never grow.

        If `palette` is given, every char actually used in the returned grid (fills, bg_char,
        outline_char) is checked against it; a missing key raises ValueError immediately rather
        than silently mismatching at PNG-render time.
        """
        width, height = (size, size) if isinstance(size, int) else size
        s = self.s
        hw, hh = width * s, height * s

        structural_indices = [i for i, p in enumerate(self.parts) if p.kind == "add"]
        all_ordered = sorted(range(len(self.parts)), key=lambda i: (self.parts[i].z, i))
        structural_ordered = [i for i in all_ordered if self.parts[i].kind == "add"]

        # Structural id-grid: ADD parts only. Their union alone is the silhouette.
        hires_structural = [[-1] * hw for _ in range(hh)]
        for idx in structural_ordered:
            for (x, y) in self.parts[idx].mask:
                if 0 <= x < hw and 0 <= y < hh:
                    hires_structural[y][x] = idx

        # Color id-grid: ADD + PAINT, true z order. A PAINT pixel only lands where the
        # structural grid already has a part — the clip that keeps paint() from ever creating,
        # growing, or eroding the silhouette.
        hires_color = [[-1] * hw for _ in range(hh)]
        for idx in all_ordered:
            part = self.parts[idx]
            for (x, y) in part.mask:
                if not (0 <= x < hw and 0 <= y < hh):
                    continue
                if part.kind == "paint" and hires_structural[y][x] == -1:
                    continue
                hires_color[y][x] = idx

        structural_target_id = self._downsample_majority(hires_structural, width, height, min_pixel_frac)
        color_target_id = self._downsample_majority(hires_color, width, height, min_pixel_frac)

        # A block below min_pixel_frac in the STRUCTURAL grid is background, full stop — this
        # keeps the two grids from ever disagreeing about what's foreground.
        for ty in range(height):
            for tx in range(width):
                if structural_target_id[ty][tx] == -1:
                    color_target_id[ty][tx] = -1
                elif color_target_id[ty][tx] == -1:
                    color_target_id[ty][tx] = structural_target_id[ty][tx]

        # Tiny-part survival (see render()'s docstring, step 3).
        block_area = s * s
        for idx, part in enumerate(self.parts):
            centroid = _mask_centroid_pixel(part.mask, s)
            if centroid is None:
                continue
            tx, ty = centroid
            if not (0 <= tx < width and 0 <= ty < height):
                continue
            base_x, base_y = tx * s, ty * s
            covered = sum(
                1 for (x, y) in part.mask
                if base_x <= x < base_x + s and base_y <= y < base_y + s
            )
            if covered / block_area < 0.4:
                continue
            if part.kind == "add":
                structural_target_id[ty][tx] = idx
                color_target_id[ty][tx] = idx
            elif structural_target_id[ty][tx] != -1:
                color_target_id[ty][tx] = idx

        if fix_joints:
            self._fix_joints(structural_target_id, color_target_id, width, height, structural_indices)

        grid = [[bg_char] * width for _ in range(height)]
        for ty in range(height):
            for tx in range(width):
                pid = color_target_id[ty][tx]
                if pid != -1:
                    grid[ty][tx] = self.parts[pid].fill

        if outline_char is not None:
            outline_px = []
            for ty in range(height):
                for tx in range(width):
                    pid = structural_target_id[ty][tx]
                    if pid == -1:
                        continue
                    part = self.parts[pid]
                    marked = False
                    for ddx, ddy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        nx, ny = tx + ddx, ty + ddy
                        if not (0 <= nx < width and 0 <= ny < height):
                            marked = True
                            break
                        npid = structural_target_id[ny][nx]
                        if npid == -1:
                            marked = True
                            break
                        if part.seam and npid != pid:
                            marked = True
                            break
                    if marked:
                        outline_px.append((tx, ty))
            for (tx, ty) in outline_px:
                grid[ty][tx] = outline_char

        result = ["".join(row) for row in grid]
        if palette is not None:
            used = {bg_char}
            for row in result:
                used.update(row)
            missing = used - set(palette.keys())
            if missing:
                raise ValueError(f"shape_kit: grid uses palette keys not in palette: {sorted(missing)}")
        return result


def preview(grid, palette, path_prefix, scale_display=3):
    """Render a grid+palette through sprite_kit's PNG pipeline and write the three standard
    artifacts an agent should look at, inside the `path_prefix` directory (created if missing):
    sprite.png (1x truth), preview_8x.png (nearest-neighbor 8x, for inspecting linework), and
    mockup.png (display-scale `scale_display`x, centered on sprite_kit's parchment field)."""
    os.makedirs(path_prefix, exist_ok=True)
    pixels = sprite_kit.render(grid, palette)
    sprite_kit.write_png(os.path.join(path_prefix, "sprite.png"), pixels)
    sprite_kit.write_png(os.path.join(path_prefix, "preview_8x.png"), sprite_kit.upscale(pixels, 8))
    sprite_kit.write_png(os.path.join(path_prefix, "mockup.png"), sprite_kit.mockup(pixels, scale=scale_display))


# --- Demo creature ------------------------------------------------------------------------


def build_demo_figure():
    """A goldfish, built the new way: add() the anatomy (body, tail, dorsal fin, pectoral fin,
    eye — all unioned into ONE silhouette, ONE 1px outline, no automatic interior seams), paint()
    a belly patch (a pure color flat clipped to that silhouette), then shade() the body (more
    paint, automatic). The two fins opt into seam=True: a real fin reads as a distinct membrane
    against the body, so this is exactly the rare case Figure.add's seam kwarg exists for.
    Everything else — including the eye, which used to need an undocumented seam_group
    workaround to avoid getting inked as a boundary — gets no seam and blends into the body's
    single mass by default. Facing right (head/snout at high x, tail trailing to low x)."""
    fig = Figure(s=DEFAULT_S)

    body_mask = union(
        ellipse(16.0, 16.5, 9.5, 7.0, rotation_deg=-6),
        capsule(23.5, 14.5, 27.5, 13.3, 3.4, 0.6),
    )
    fig.add("body", body_mask, "B", z=1)

    tail_mask = fan(pivot=(9.0, 16.3), tips=[(0.5, 7.5), (1.0, 25.0)], inner_r=3.5)
    fig.add("tail", tail_mask, "F", z=0)

    dorsal_mask = polygon([(12.5, 10.0), (16.5, 2.0), (20.0, 10.0)])
    fig.add("dorsal_fin", dorsal_mask, "F", z=2, seam=True)

    pectoral_mask = polygon([(17.0, 20.0), (21.5, 26.5), (22.5, 19.0)])
    fig.add("pectoral_fin", pectoral_mask, "F", z=2, seam=True)

    eye_mask = ellipse(24.3, 14.3, 1.5, 1.5)
    fig.add("eye", eye_mask, "E", z=4)

    belly_mask = ellipse(17.0, 20.5, 7.0, 3.0, rotation_deg=-6)
    fig.paint("belly", belly_mask, "P", z=1.2)

    fig.shade("body", light_deg=225, highlight_frac=0.4, shadow_frac=0.4, highlight_fill="H", shadow_fill="D")

    return fig


DEMO_PALETTE = {
    ".": (247, 240, 222),
    "B": (232, 142, 48),
    "H": (250, 189, 104),
    "D": (172, 84, 30),
    "F": (219, 110, 40),
    "E": (32, 22, 24),
    "P": (247, 200, 130),
    "K": (58, 30, 20),
}


def build_demo_grid():
    fig = build_demo_figure()
    return fig.render(
        (32, 32),
        DEMO_PALETTE,
        bg_char=".",
        outline_char="K",
        min_pixel_frac=0.5,
        fix_joints=True,
    )


def cmd_demo(args):
    grid = build_demo_grid()
    preview(grid, DEMO_PALETTE, args.outdir, scale_display=args.mockup_scale)
    print(f"wrote {args.outdir}/sprite.png, preview_8x.png, mockup.png")
    for row in grid:
        print(row)
    problems = sprite_kit.validate_all(grid, DEMO_PALETTE, 32, 32, set(DEMO_PALETTE.values()), bg_char=".")
    for name, probs in problems.items():
        sprite_kit._print_report(name, probs)
    joint_problems = check_joints(grid, ".")
    sprite_kit._print_report(
        "joints",
        [f"{b['offshoot']} touches {b['main']} only diagonally" for b in joint_problems],
    )


# --- Self-test -----------------------------------------------------------------------------


def _check(cond, label, results):
    print(f"[{'PASS' if cond else 'FAIL'}] {label}")
    results.append(bool(cond))


def run_selftest():
    results = []

    # ellipse symmetry: a non-rotated ellipse centered on a grid-aligned axis mirrors onto itself
    m = ellipse(16, 16, 10, 7, rotation_deg=0, s=4)
    _check(mirror_x(m, 16, s=4) == m, "ellipse: non-rotated ellipse is exactly mirror-symmetric about its center", results)
    _check(len(m) > 0, "ellipse: produces a non-empty mask", results)

    # capsule taper monotonic: cross-sectional height should grow with radius along the axis
    cap = capsule(2, 10, 20, 10, 1, 6, s=4)
    heights = []
    for x in (10, 20, 30, 40, 50, 60, 70):  # high-res columns strictly inside the segment
        ys = [y for (cx, y) in cap if cx == x]
        heights.append(max(ys) - min(ys) + 1 if ys else 0)
    _check(all(a <= b for a, b in zip(heights, heights[1:])), "capsule: cross-section height is non-decreasing along the taper", results)
    _check(heights[0] < heights[-1], "capsule: taper actually widens end-to-end", results)

    # downsample majority correctness on a synthetic half-covered block
    fig_half = Figure(s=4)
    fig_half.add("a", {(x, y) for x in range(4) for y in range(2)}, "A", z=1)  # 8/16 = 0.5
    grid_half = fig_half.render((1, 1), bg_char=".", min_pixel_frac=0.5)
    _check(grid_half == ["A"], "downsample: exactly-50% covered block is filled at the default 0.5 threshold", results)

    fig_low = Figure(s=4)
    # 6/16 = 0.375: below BOTH the 0.5 majority threshold and the 0.4 tiny-part survival floor,
    # so this exercises plain background fallback without tripping the new survival guarantee
    # (a part in the [0.4, 0.5) band is now *supposed* to survive -- see the tiny-part-survival
    # checks below).
    fig_low.add("a", {(x, y) for x in range(4) for y in range(2)} - {(0, 0), (1, 0)}, "A", z=1)
    grid_low = fig_low.render((1, 1), bg_char=".", min_pixel_frac=0.5)
    _check(grid_low == ["."], "downsample: below-40% covered block falls back to background (no majority, no survival)", results)

    # outline is exactly 1px and never grows the silhouette
    fig_sq = Figure(s=2)
    square = {(x, y) for x in range(4, 16) for y in range(4, 16)}  # a 6x6 target-pixel square
    fig_sq.add("sq", square, "Q", z=1)
    grid_plain = fig_sq.render((10, 10), bg_char=".")
    grid_outlined = fig_sq.render((10, 10), bg_char=".", outline_char="K")

    def fg_cells(grid, bg="."):
        return {(x, y) for y, row in enumerate(grid) for x, ch in enumerate(row) if ch != bg}

    silhouette_plain = fg_cells(grid_plain)
    silhouette_outlined = fg_cells(grid_outlined)
    _check(silhouette_plain == silhouette_outlined, "outline: adding an outline does not change the foreground silhouette (erosion, not dilation)", results)

    ring = {(x, y) for (x, y) in silhouette_outlined if grid_outlined[y][x] == "K"}
    interior = silhouette_outlined - ring
    expected_ring = {(x, y) for (x, y) in silhouette_outlined if x in (2, 7) or y in (2, 7)}
    _check(ring == expected_ring, "outline: ring is exactly the square's 1px border", results)
    _check(len(interior) > 0 and all(3 <= x <= 6 and 3 <= y <= 6 for (x, y) in interior), "outline: interior pixels (no bg neighbor) keep the fill color, confirming the ring is exactly 1px thick", results)

    # mirror_x exactness (round trip on a grid-aligned axis)
    fig_mask = capsule(3, 3, 3, 15, 2, 2, s=4) | ellipse(8, 5, 3, 3, s=4)
    axis = 16
    mirrored_once = mirror_x(fig_mask, axis, s=4)
    mirrored_twice = mirror_x(mirrored_once, axis, s=4)
    _check(mirrored_twice == fig_mask, "mirror_x: mirroring twice about a grid-aligned axis is an exact round trip", results)
    _check(mirrored_once != fig_mask, "mirror_x: a single mirror actually moves an asymmetric mask", results)

    # silhouette union: two overlapping add()ed parts get NO automatic interior seam by default
    fig_union = Figure(s=2)
    fig_union.add("a", {(x, y) for x in range(0, 12) for y in range(0, 12)}, "A", z=0)
    fig_union.add("b", {(x, y) for x in range(6, 18) for y in range(6, 18)}, "B", z=1)
    grid_union = fig_union.render((9, 9), bg_char=".", outline_char="K")
    # These 5 target pixels sit on b's side of the a/b overlap border but are otherwise fully
    # surrounded by figure (never adjacent to background), so they isolate seam behavior
    # specifically, rather than any legitimate silhouette-vs-background outline elsewhere.
    seam_coords = [(3, 3), (4, 3), (5, 3), (3, 4), (3, 5)]
    no_interior_seam = all(grid_union[y][x] == "B" for (x, y) in seam_coords)
    _check(no_interior_seam, "silhouette: two overlapping add()ed parts get NO automatic interior seam by default", results)

    # seam=True opts a part INTO an interior line against another add()ed part
    fig_seam_opt = Figure(s=2)
    fig_seam_opt.add("a", {(x, y) for x in range(0, 12) for y in range(0, 12)}, "A", z=0)
    fig_seam_opt.add("b", {(x, y) for x in range(6, 18) for y in range(6, 18)}, "B", z=1, seam=True)
    grid_seam_opt = fig_seam_opt.render((9, 9), bg_char=".", outline_char="K")
    seam_opt_marks_them_all = all(grid_seam_opt[y][x] == "K" for (x, y) in seam_coords)
    _check(seam_opt_marks_them_all, "silhouette: opt-in seam=True draws an interior line at the border against another add()ed part", results)

    # paint() never moves the outline, only recolors what's already inside the silhouette
    fig_paint = Figure(s=8)
    fig_paint.add("body", ellipse(10, 10, 8, 6, s=8), "B", z=1)
    grid_no_paint = fig_paint.render((20, 20), bg_char=".", outline_char="K")
    fig_paint.paint("patch", ellipse(10, 10, 3, 2, s=8), "P", z=2)
    grid_with_paint = fig_paint.render((20, 20), bg_char=".", outline_char="K")

    def outline_coords(grid):
        return {(x, y) for y, row in enumerate(grid) for x, ch in enumerate(row) if ch == "K"}

    _check(outline_coords(grid_no_paint) == outline_coords(grid_with_paint), "paint: adding a paint() region does not move a single outline pixel", results)
    _check(any("P" in row for row in grid_with_paint) and grid_with_paint != grid_no_paint, "paint: the paint() region actually recolors its clipped interior", results)

    # tiny-part survival: a part covering >= 0.4 (but < the 0.5 majority threshold) of its own
    # centroid's target-pixel block still survives; below 0.4 it does not (a real threshold).
    fig_tiny = Figure(s=3)
    fig_tiny.add("speck", {(0, 0), (1, 0), (0, 1), (1, 1)}, "E", z=1)  # 4/9 = 0.444
    grid_tiny = fig_tiny.render((2, 2), bg_char=".")
    _check(grid_tiny[0][0] == "E", "tiny-part survival: a part covering 44% of its block survives (below the 50% majority threshold, above the 40% survival guarantee)", results)

    fig_below = Figure(s=3)
    fig_below.add("speck", {(0, 0), (1, 0), (0, 1)}, "E", z=1)  # 3/9 = 0.333
    grid_below = fig_below.render((2, 2), bg_char=".")
    _check(grid_below[0][0] == ".", "tiny-part survival: a part covering only 33% of its block is NOT force-kept (the 40% guarantee has a real floor)", results)

    # fix_joints: a constructed diagonal-only touch gets promoted to a real orthogonal join
    fig_joint = Figure(s=2)
    main_mask = {(x, y) for x in range(0, 6) for y in range(0, 6)}  # target pixels (0,0)-(2,2)
    main_mask.add((5, 6))  # a faint extra hi-res pixel biasing the (2,3) promote-candidate
    fig_joint.add("main", main_mask, "M", z=1)
    fig_joint.add("tip", {(x, y) for x in range(6, 8) for y in range(6, 8)}, "T", z=1)  # target (3,3)
    grid_broken = fig_joint.render((4, 4), bg_char=".", fix_joints=False)
    grid_fixed = fig_joint.render((4, 4), bg_char=".", fix_joints=True)

    broken_problems = check_joints(grid_broken, ".")
    fixed_problems = check_joints(grid_fixed, ".")
    _check(len(broken_problems) > 0, "check_joints: detects a constructed diagonal-only touch between two components", results)
    _check(len(fixed_problems) == 0, "fix_joints: promotes the diagonal touch to a real orthogonal join (default fix_joints=True)", results)
    _check(grid_fixed[3][2] == "M" or grid_fixed[2][3] == "M", "fix_joints: the promoted pixel is filled from the candidate with the highest actual high-res coverage", results)

    # shade() carves highlight/shadow with no seam against the base part
    fig_shade = Figure(s=8)
    fig_shade.add("body", ellipse(10, 10, 8, 6, s=8), "B", z=1)
    fig_shade.shade("body", light_deg=225, highlight_fill="H", shadow_fill="D")
    grid_shade = fig_shade.render((20, 20), bg_char=".", outline_char="K")
    has_highlight = any("H" in row for row in grid_shade)
    has_shadow = any("D" in row for row in grid_shade)
    _check(has_highlight and has_shadow, "shade: produces both a highlight and a shadow band", results)

    # shade()'s bands are paint() now, so every 'K' pixel here must be a true silhouette-vs-
    # background boundary — a seam against the base part isn't even possible anymore.
    only_bg_boundaries = True
    for y in range(20):
        for x in range(20):
            if grid_shade[y][x] != "K":
                continue
            touches_bg = False
            for ddx, ddy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + ddx, y + ddy
                if not (0 <= nx < 20 and 0 <= ny < 20) or grid_shade[ny][nx] == ".":
                    touches_bg = True
                    break
            if not touches_bg:
                only_bg_boundaries = False
                break
    _check(only_bg_boundaries, "shade: highlight/shadow bands draw no seam outline against their own base part", results)

    ok = all(results)
    print(f"\n{sum(results)}/{len(results)} checks passed — {'PASS' if ok else 'FAIL'}")
    sys.exit(0 if ok else 1)


# --- CLI -----------------------------------------------------------------------------------


def build_argparser():
    p = argparse.ArgumentParser(prog="shape_kit", description="Construct pixel sprites from geometric parts.")
    sub = p.add_subparsers(dest="command", required=True)

    pd = sub.add_parser("demo", help="Build a demo creature and write its PNGs to <outdir>.")
    pd.add_argument("outdir", help="Directory to write sprite.png/preview_8x.png/mockup.png into.")
    pd.add_argument("--mockup-scale", type=int, default=3, help="Upscale factor used inside the mockup.")
    pd.set_defaults(func=cmd_demo)

    pt = sub.add_parser("selftest", help="Run the built-in self-test suite; exits nonzero on failure.")
    pt.set_defaults(func=lambda args: run_selftest())

    return p


def main(argv=None):
    args = build_argparser().parse_args(argv)
    args.func(args)


if __name__ == "__main__":
    main()
