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

Core model
----------
- Work canvas: the figure is built at N*S x N*S resolution (N = target sprite size, S = the
  supersample factor, default 8), then downsampled to N x N.
- A `Part` has a mask (a set of high-res (x, y) integer pixel coordinates), a fill (palette
  char), a z-order (higher paints over lower), and a name.
- Parts are collected in a `Figure` and composited/downsampled/outlined by `Figure.render`.
- All primitive functions take coordinates and lengths in TARGET pixel units (floats), and
  scale internally by `s`; they return masks already in high-res integer coordinates, so masks
  from different primitives compose directly with the boolean ops (union/subtract/intersect are
  literally `set` union/difference/intersection).

Usage
-----
    import sys; sys.path.insert(0, "<skill>/scripts")
    from shape_kit import Figure, ellipse, capsule, polygon, preview

    fig = Figure(s=8)
    fig.add("body", ellipse(16, 16, 9, 6), "B", z=1)
    fig.shade("body", light_deg=225, highlight_fill="H", shadow_fill="S")
    fig.add("eye", ellipse(21, 14, 1.2, 1.2), "E", z=3)
    grid = fig.render((32, 32), palette, bg_char=".", outline_char="K")
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
    """One named region of the figure: a high-res mask, a palette fill char, and a z-order
    (higher z paints over lower z where masks overlap). `seam_group` controls the seam-outline
    pass in Figure.render: two parts sharing a seam_group never get an outline drawn between
    them, even if they're different Parts with different z — this is how shade()'s carved
    highlight/shadow bands avoid getting their own outline against the part they came from.
    Defaults to the part's own name, so ordinary distinct parts seam against each other."""

    __slots__ = ("name", "mask", "fill", "z", "seam_group")

    def __init__(self, name, mask, fill, z, seam_group=None):
        self.name = name
        self.mask = mask if isinstance(mask, (set, frozenset)) else set(mask)
        self.fill = fill
        self.z = z
        self.seam_group = name if seam_group is None else seam_group


class Figure:
    """A composition of Parts, rendered together into a downsampled, outlined ASCII grid."""

    def __init__(self, s=DEFAULT_S):
        self.s = s
        self.parts = []
        self._by_name = {}

    def add(self, name, mask, fill, z, seam_group=None):
        """Add a part. Later calls with a higher `z` paint over earlier/lower ones wherever
        masks overlap in the composite; ties keep insertion order (last-added wins)."""
        part = Part(name, mask, fill, z, seam_group)
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
        """Carve a highlight band and/or a shadow band out of an existing part's silhouette,
        added as new sub-parts just above it in z (so they paint over its base fill), with no
        seam outline against it (same seam_group).

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
            self.add(f"{part_name}.highlight", band, highlight_fill, sub_z, seam_group=base.seam_group)

        if shadow_fill is not None:
            k = max(shadow_frac * base_k, self.s)
            offx, offy = round(dx * k), round(dy * k)  # shift toward the light
            shifted = {(x + offx, y + offy) for (x, y) in mask}
            band = mask - shifted
            self.add(f"{part_name}.shadow", band, shadow_fill, sub_z, seam_group=base.seam_group)

    def render(
        self,
        size,
        palette=None,
        bg_char=".",
        outline_char=None,
        outline_between_parts=True,
        min_pixel_frac=0.5,
    ):
        """Composite, downsample, and outline the figure into an ASCII grid (list of strings,
        one row each) compatible with sprite_kit's render()/validate_all()/etc.

        `size` is (width, height) in target pixels (an int is treated as square). Steps:
          1. Composite: paint every part's high-res mask into an id-grid in ascending z order,
             so a higher-z part overwrites a lower-z one wherever they overlap. Ties keep
             insertion order.
          2. Downsample: each target pixel looks at its own SxS block of the id-grid and takes
             the id with the most covered cells (ties broken toward the higher z, so an
             overlapping focal part wins a coin-flip block). If the total covered fraction of
             the block is < min_pixel_frac, the target pixel is background instead — this is
             what keeps a part's fringe from leaving stray, barely-covered target pixels.
          3. Outline (only if outline_char is given): a foreground pixel becomes outline_char if
             any of its 4 neighbors is background, OR (when outline_between_parts) a different
             part with a different seam_group AND a strictly lower z — so a seam is drawn once,
             on the higher part's side, not doubled on both. Outline pixels are RECOLORED
             foreground pixels, never new ones, so the silhouette can only erode, never grow —
             every outline pixel already belonged to the figure before this pass, and this pass
             only ever looks at the original (pre-outline) id-grid, so it can't cascade into a
             second ring.

        If `palette` is given, every char actually used in the returned grid (fills, bg_char,
        outline_char) is checked against it; a missing key raises ValueError immediately rather
        than silently mismatching at PNG-render time.
        """
        width, height = (size, size) if isinstance(size, int) else size
        s = self.s
        hw, hh = width * s, height * s

        id_grid = [[-1] * hw for _ in range(hh)]
        ordered = sorted(range(len(self.parts)), key=lambda i: (self.parts[i].z, i))
        for idx in ordered:
            for (x, y) in self.parts[idx].mask:
                if 0 <= x < hw and 0 <= y < hh:
                    id_grid[y][x] = idx

        target_id = [[-1] * width for _ in range(height)]
        block_area = s * s
        for ty in range(height):
            base_y = ty * s
            for tx in range(width):
                base_x = tx * s
                counts = {}
                for dyy in range(s):
                    row = id_grid[base_y + dyy]
                    for dxx in range(s):
                        pid = row[base_x + dxx]
                        if pid != -1:
                            counts[pid] = counts.get(pid, 0) + 1
                fg = sum(counts.values())
                if fg == 0 or fg / block_area < min_pixel_frac:
                    continue
                best_id = max(counts, key=lambda pid: (counts[pid], self.parts[pid].z))
                target_id[ty][tx] = best_id

        grid = [[bg_char] * width for _ in range(height)]
        for ty in range(height):
            for tx in range(width):
                pid = target_id[ty][tx]
                if pid != -1:
                    grid[ty][tx] = self.parts[pid].fill

        if outline_char is not None:
            outline_px = []
            for ty in range(height):
                for tx in range(width):
                    pid = target_id[ty][tx]
                    if pid == -1:
                        continue
                    part = self.parts[pid]
                    marked = False
                    for ddx, ddy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        nx, ny = tx + ddx, ty + ddy
                        if not (0 <= nx < width and 0 <= ny < height):
                            marked = True
                            break
                        npid = target_id[ny][nx]
                        if npid == -1:
                            marked = True
                            break
                        if outline_between_parts and npid != pid:
                            npart = self.parts[npid]
                            if npart.seam_group != part.seam_group and npart.z < part.z:
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
    """A goldfish, built from 6 parts: body (ellipse+capsule snout, shaded), tail (fan — a
    real fish tail IS a fan: one pivot at the body, two tips, scalloped trailing edge), dorsal
    fin, pectoral fin (polygons), and an eye (ellipse). Facing right (head/snout at high x,
    tail trailing to low x)."""
    fig = Figure(s=DEFAULT_S)

    body_mask = union(
        ellipse(16.0, 16.5, 9.5, 7.0, rotation_deg=-6),
        capsule(23.5, 14.5, 27.5, 13.3, 3.4, 0.6),
    )
    fig.add("body", body_mask, "B", z=1)
    fig.shade("body", light_deg=225, highlight_frac=0.4, shadow_frac=0.4, highlight_fill="H", shadow_fill="D")

    tail_mask = fan(pivot=(9.0, 16.3), tips=[(0.5, 7.5), (1.0, 25.0)], inner_r=3.5)
    fig.add("tail", tail_mask, "F", z=0)

    dorsal_mask = polygon([(12.5, 10.0), (16.5, 2.0), (20.0, 10.0)])
    fig.add("dorsal_fin", dorsal_mask, "F", z=2)

    pectoral_mask = polygon([(17.0, 20.0), (21.5, 26.5), (22.5, 19.0)])
    fig.add("pectoral_fin", pectoral_mask, "F", z=2)

    eye_mask = ellipse(24.3, 14.3, 1.5, 1.5)
    fig.add("eye", eye_mask, "E", z=4)

    return fig


DEMO_PALETTE = {
    ".": (247, 240, 222),
    "B": (232, 142, 48),
    "H": (250, 189, 104),
    "D": (172, 84, 30),
    "F": (219, 110, 40),
    "E": (32, 22, 24),
    "K": (58, 30, 20),
}


def build_demo_grid():
    fig = build_demo_figure()
    return fig.render(
        (32, 32),
        DEMO_PALETTE,
        bg_char=".",
        outline_char="K",
        outline_between_parts=True,
        min_pixel_frac=0.5,
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
    fig_low.add("a", {(x, y) for x in range(4) for y in range(2)} - {(0, 0)}, "A", z=1)  # 7/16 < 0.5
    grid_low = fig_low.render((1, 1), bg_char=".", min_pixel_frac=0.5)
    _check(grid_low == ["."], "downsample: just-under-50% covered block falls back to background", results)

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

    # seam outline appears between overlapping parts of different z
    fig_seam = Figure(s=2)
    fig_seam.add("a", {(x, y) for x in range(0, 12) for y in range(0, 12)}, "A", z=0)
    fig_seam.add("b", {(x, y) for x in range(6, 18) for y in range(6, 18)}, "B", z=1)
    grid_seam_on = fig_seam.render((9, 9), bg_char=".", outline_char="K", outline_between_parts=True)
    grid_seam_off = fig_seam.render((9, 9), bg_char=".", outline_char="K", outline_between_parts=False)
    # These 5 target pixels sit on b's side of the a/b overlap border but are otherwise fully
    # surrounded by figure (never adjacent to background), so they isolate the seam behavior
    # specifically, rather than any legitimate silhouette-vs-background outline elsewhere.
    seam_coords = [(3, 3), (4, 3), (5, 3), (3, 4), (3, 5)]
    seam_on_marks_them_all = all(grid_seam_on[y][x] == "K" for (x, y) in seam_coords)
    seam_off_marks_none = all(grid_seam_off[y][x] == "B" for (x, y) in seam_coords)
    _check(seam_on_marks_them_all, "seam: outline_between_parts=True draws a seam at the interface of two overlapping different-z parts", results)
    _check(seam_off_marks_none, "seam: outline_between_parts=False draws no interior outline for the same figure", results)

    # shade() carves highlight/shadow with no seam against the base part
    fig_shade = Figure(s=8)
    fig_shade.add("body", ellipse(10, 10, 8, 6, s=8), "B", z=1)
    fig_shade.shade("body", light_deg=225, highlight_fill="H", shadow_fill="D")
    grid_shade = fig_shade.render((20, 20), bg_char=".", outline_char="K")
    has_highlight = any("H" in row for row in grid_shade)
    has_shadow = any("D" in row for row in grid_shade)
    _check(has_highlight and has_shadow, "shade: produces both a highlight and a shadow band", results)

    # body/highlight/shadow share one seam_group, so every 'K' pixel here must be a true
    # silhouette-vs-background boundary, never a seam between the base part and its own bands.
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
