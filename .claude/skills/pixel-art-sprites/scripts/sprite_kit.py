#!/usr/bin/env python3
"""sprite_kit: dependency-free toolkit for drawing pixel-art sprites as ASCII index grids
and rendering/validating them as PNGs. Stdlib only (zlib + struct); no PIL.

Data model: a sprite is (palette, grid). palette maps a single char to an (r,g,b) tuple
('.' conventionally = background); grid is a list of equal-length strings of palette keys.

Usable as a library (`import sprite_kit`) or as a CLI:
    python3 sprite_kit.py render grid.txt palette.json -o sprite.png
    python3 sprite_kit.py validate sprite.png palette.json
    python3 sprite_kit.py sheet a.png b.png c.png -o sheet.png --cols 3
    python3 sprite_kit.py preview sprite.png --scale 8
    python3 sprite_kit.py selftest
"""

import argparse
import json
import math
import os
import struct
import sys
import tempfile
import zlib

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# --- PNG codec (stdlib only: zlib for DEFLATE, struct for big-endian packing) ---

def _chunk(tag, data):
    """Build one length-prefixed, CRC-checked PNG chunk (the format's only framing unit)."""
    return (
        struct.pack(">I", len(data))
        + tag
        + data
        + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    )

def write_png(path, pixels):
    """Write pixels (rows of (r,g,b)) as an 8-bit RGB PNG — the ground-truth artifact an agent hands back to a human or another tool, so it must be a real, openable PNG, not a proprietary blob."""
    height = len(pixels)
    width = len(pixels[0]) if height else 0
    raw = bytearray()
    for row in pixels:
        raw.append(0)  # filter type 0 (None) per scanline; simplest correct encoder
        for (r, g, b) in row:
            raw.extend((r & 0xFF, g & 0xFF, b & 0xFF))
    compressed = zlib.compress(bytes(raw), 9)
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(PNG_SIGNATURE)
        f.write(_chunk(b"IHDR", ihdr))
        f.write(_chunk(b"IDAT", compressed))
        f.write(_chunk(b"IEND", b""))

def _paeth(a, b, c):
    """PNG's Paeth predictor: pick whichever of left/above/upper-left best predicts a byte."""
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c

def read_png(path):
    """Read a PNG back into rows of (r,g,b), dropping alpha — round-tripping must survive an agent re-opening its own render (or a hand-authored reference PNG), so every filter/color combination a common encoder emits (0-4, RGB or RGBA) has to decode faithfully."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != PNG_SIGNATURE:
        raise ValueError(f"{path} is not a PNG file (bad signature)")
    pos = 8
    width = height = bit_depth = color_type = None
    idat = bytearray()
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        chunk_data = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b"IHDR":
            width, height, bit_depth, color_type = struct.unpack(">IIBBxxx", chunk_data)
        elif tag == b"IDAT":
            idat.extend(chunk_data)
        elif tag == b"IEND":
            break
    if width is None:
        raise ValueError(f"{path}: missing IHDR chunk")
    if bit_depth != 8:
        raise NotImplementedError(f"{path}: only 8-bit depth is supported, got {bit_depth}")
    if color_type == 2:
        bpp = 3
    elif color_type == 6:
        bpp = 4
    else:
        raise NotImplementedError(f"{path}: only RGB/RGBA (color type 2/6) is supported, got {color_type}")

    raw = zlib.decompress(bytes(idat))
    stride = width * bpp
    prior = bytearray(stride)
    pixels = []
    off = 0
    for _y in range(height):
        filt = raw[off]
        off += 1
        line = bytearray(raw[off:off + stride])
        off += stride
        if filt == 0:
            pass
        elif filt == 1:
            for i in range(len(line)):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + a) & 0xFF
        elif filt == 2:
            for i in range(len(line)):
                b = prior[i]
                line[i] = (line[i] + b) & 0xFF
        elif filt == 3:
            for i in range(len(line)):
                a = line[i - bpp] if i >= bpp else 0
                b = prior[i]
                line[i] = (line[i] + (a + b) // 2) & 0xFF
        elif filt == 4:
            for i in range(len(line)):
                a = line[i - bpp] if i >= bpp else 0
                b = prior[i]
                c = prior[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + _paeth(a, b, c)) & 0xFF
        else:
            raise NotImplementedError(f"{path}: unsupported filter type {filt}")
        row = [tuple(line[i:i + 3]) for i in range(0, stride, bpp)]
        pixels.append(row)
        prior = line
    return pixels

# --- Grid <-> pixel conversion ---

def render(grid, palette):
    """Turn the ASCII index grid into pixels — the one place a typo'd palette key surfaces as a KeyError, not a silently wrong color."""
    return [[palette[ch] for ch in row] for row in grid]

def to_grid(pixels, palette):
    """Invert render(): recover the ASCII grid from pixels, so a hand-authored or edited PNG can be re-inspected as the same index-grid an agent reasons about. Raises on any color the palette doesn't name — a silent nearest-color match would hide real drift."""
    inverse = {color: ch for ch, color in palette.items()}
    grid = []
    for y, row in enumerate(pixels):
        chars = []
        for x, px in enumerate(row):
            if px not in inverse:
                raise ValueError(f"pixel ({x},{y}) has color {px} with no matching palette entry")
            chars.append(inverse[px])
        grid.append("".join(chars))
    return grid

def upscale(pixels, n):
    """Nearest-neighbor magnify by n — sprites are tiny, and a 1:1 PNG is nearly unreadable to a human eyeballing craft quality."""
    out = []
    for row in pixels:
        wide_row = []
        for px in row:
            wide_row.extend([px] * n)
        for _ in range(n):
            out.append(list(wide_row))
    return out

def mockup(pixels, scale=3, field=None, bg=(240, 232, 200)):
    """Place the sprite, upscaled, centered on a generous parchment field — seeing it isolated at 1:1 zoom hides how much visual weight/noise it has once surrounded by empty space, which is closer to how it will actually read in-game."""
    scaled = upscale(pixels, scale)
    sh = len(scaled)
    sw = len(scaled[0]) if sh else 0
    if field is None:
        fw, fh = round(sw * 4.5), round(sh * 4.5)
    else:
        fw, fh = field
    canvas = [[bg for _ in range(fw)] for _ in range(fh)]
    ox, oy = (fw - sw) // 2, (fh - sh) // 2
    for y in range(sh):
        for x in range(sw):
            canvas[oy + y][ox + x] = scaled[y][x]
    return canvas

def contact_sheet(list_of_pixels, cols, pad=4, bg=(255, 255, 255)):
    """Lay several sprites out in a grid montage — comparing a whole set (a walk cycle, a palette family) side by side catches inconsistencies a one-at-a-time review misses."""
    if not list_of_pixels:
        return []
    cell_w = max((len(p[0]) if p else 0) for p in list_of_pixels)
    cell_h = max(len(p) for p in list_of_pixels)
    rows = math.ceil(len(list_of_pixels) / cols)
    total_w = cols * cell_w + (cols + 1) * pad
    total_h = rows * cell_h + (rows + 1) * pad
    canvas = [[bg for _ in range(total_w)] for _ in range(total_h)]
    for idx, px in enumerate(list_of_pixels):
        r, c = divmod(idx, cols)
        ox = pad + c * (cell_w + pad)
        oy = pad + r * (cell_h + pad)
        for y, row in enumerate(px):
            for x, color in enumerate(row):
                canvas[oy + y][ox + x] = color
    return canvas

# --- Validators: each returns a list of problem strings (empty = pass); lints, not a type system ---

def check_dims(pixels, w, h):
    """Catch an off-by-one canvas — a sprite one row short silently shifts every downstream tile/collision assumption."""
    problems = []
    actual_h = len(pixels)
    actual_w = len(pixels[0]) if actual_h else 0
    if actual_w != w or actual_h != h:
        problems.append(f"expected {w}x{h}, got {actual_w}x{actual_h}")
    for y, row in enumerate(pixels):
        if len(row) != actual_w:
            problems.append(f"row {y} has width {len(row)}, inconsistent with row 0's {actual_w}")
    return problems

def check_palette(pixels, allowed_colors):
    """Catch stray colors (an anti-aliased edge, a copy-pasted pixel from another sprite) that would slip past the game's fixed palette and render wrong on real hardware."""
    allowed = set(allowed_colors)
    problems = []
    seen = set()
    for y, row in enumerate(pixels):
        for x, px in enumerate(row):
            if px not in allowed and px not in seen:
                seen.add(px)
                problems.append(f"color {px} at ({x},{y}) is not in the allowed palette")
    return problems

def check_background(pixels, bg):
    """Catch a sprite that isn't actually isolated on its background — if bg doesn't reach at least two corners, the 'sprite' may be a filled rectangle or bleeding off the canvas edge."""
    problems = []
    h = len(pixels)
    w = len(pixels[0]) if h else 0
    if not any(px == bg for row in pixels for px in row):
        problems.append(f"background color {bg} does not appear anywhere in the image")
    if h and w:
        corners = [pixels[0][0], pixels[0][w - 1], pixels[h - 1][0], pixels[h - 1][w - 1]]
        count = sum(1 for c in corners if c == bg)
        if count < 2:
            problems.append(f"background color {bg} occupies only {count}/4 corners (need >= 2)")
    return problems

def check_orphans(grid, bg_char="."):
    """Catch stray single pixels and knife-edge diagonal joints — both read as noise or as a rendering glitch rather than an intentional shape once scaled up on a real screen."""
    problems = []
    h = len(grid)
    w = len(grid[0]) if h else 0

    def is_fg(x, y):
        return 0 <= x < w and 0 <= y < h and grid[y][x] != bg_char

    for y in range(h):
        for x in range(w):
            if grid[y][x] == bg_char:
                continue
            neighbors = [
                (x + dx, y + dy)
                for dx in (-1, 0, 1)
                for dy in (-1, 0, 1)
                if not (dx == 0 and dy == 0)
            ]
            fg_neighbors = [(nx, ny) for nx, ny in neighbors if is_fg(nx, ny)]
            if not fg_neighbors:
                problems.append(f"orphan pixel at ({x},{y}): no foreground neighbors at all")
                continue
            orthogonal = [(nx, ny) for nx, ny in fg_neighbors if nx == x or ny == y]
            if not orthogonal:
                problems.append(
                    f"diagonal-only connection at ({x},{y}): only touches the shape via a "
                    f"diagonal, a fragile 1px joint that can disappear at other scales"
                )
    return problems

def check_coverage(grid, bg_char=".", lo=0.25, hi=0.70):
    """Catch a figure that's lost in empty space or crammed edge-to-edge — both undersell the sprite at typical in-game display sizes."""
    h = len(grid)
    w = len(grid[0]) if h else 0
    total = w * h
    fg = sum(1 for row in grid for ch in row if ch != bg_char)
    frac = fg / total if total else 0.0
    problems = []
    if not (lo <= frac <= hi):
        problems.append(
            f"coverage {frac:.0%} is outside the recommended [{lo:.0%}, {hi:.0%}] range"
        )
    return problems

def check_banding(grid, bg_char="."):
    """Flag long straight or 45-degree-staircase runs on the silhouette outline — the tell-tale sign of a mechanically drawn rectangle/line rather than a hand-considered curve. Heuristic and conservative on purpose: this is a lint an artist should read, not a hard gate."""
    problems = []
    h = len(grid)
    w = len(grid[0]) if h else 0

    left, right = [], []
    for y in range(h):
        xs = [x for x, ch in enumerate(grid[y]) if ch != bg_char]
        if xs:
            left.append((y, min(xs)))
            right.append((y, max(xs)))
    top, bottom = [], []
    for x in range(w):
        ys = [y for y in range(h) if grid[y][x] != bg_char]
        if ys:
            top.append((x, min(ys)))
            bottom.append((x, max(ys)))

    def scan(edge, name):
        n = len(edge)
        i = 1
        while i < n:
            p0, c0 = edge[i - 1]
            p1, c1 = edge[i]
            if p1 - p0 != 1:
                i += 1
                continue
            d = c1 - c0
            if d == 0 or abs(d) == 1:
                j = i
                while j < n and edge[j][0] - edge[j - 1][0] == 1 and edge[j][1] - edge[j - 1][1] == d:
                    j += 1
                run_len = j - (i - 1)
                start, end = edge[i - 1][0], edge[j - 1][0]
                if d == 0 and run_len >= 6:
                    problems.append(f"{name}: axis-aligned outline run of {run_len}px (near {start}-{end})")
                elif abs(d) == 1 and run_len >= 4:
                    problems.append(f"{name}: 45-degree staircase run of {run_len}px (near {start}-{end})")
                i = j
            else:
                i += 1

    scan(left, "left edge")
    scan(right, "right edge")
    scan(top, "top edge")
    scan(bottom, "bottom edge")
    return problems

def validate_all(grid, palette, w, h, allowed, bg_char="."):
    """Run the full validator bundle in one call — the shape an agent (or the CLI's `validate` command) actually wants: one dict of check-name -> problems, nothing hand-wired per-check."""
    pixels = render(grid, palette)
    bg = palette.get(bg_char)
    return {
        "dims": check_dims(pixels, w, h),
        "palette": check_palette(pixels, allowed),
        "background": check_background(pixels, bg) if bg is not None else [f"bg_char {bg_char!r} not in palette"],
        "orphans": check_orphans(grid, bg_char),
        "coverage": check_coverage(grid, bg_char),
        "banding": check_banding(grid, bg_char),
    }

def stats(grid, bg_char="."):
    """Summarize a grid numerically (coverage, per-char counts, bounding box, mirror symmetry) — symmetry near 1.0 is a useful smell for a sprite that looks stiff/lifeless rather than hand-posed."""
    h = len(grid)
    w = len(grid[0]) if h else 0
    counts = {}
    for row in grid:
        for ch in row:
            counts[ch] = counts.get(ch, 0) + 1
    fg = sum(v for k, v in counts.items() if k != bg_char)
    coverage = fg / (w * h) if w * h else 0.0
    xs = [x for row in grid for x, ch in enumerate(row) if ch != bg_char]
    ys = [y for y, row in enumerate(grid) for ch in row if ch != bg_char]
    bbox = (min(xs), min(ys), max(xs), max(ys)) if xs else None
    match = 0
    for row in grid:
        mirrored = row[::-1]
        match += sum(1 for a, b in zip(row, mirrored) if a == b)
    symmetry = match / (w * h) if w * h else 0.0
    return {
        "width": w,
        "height": h,
        "coverage": coverage,
        "counts": counts,
        "bbox": bbox,
        "symmetry": symmetry,
    }

# --- CLI ---

def _load_palette(path):
    with open(path) as f:
        data = json.load(f)
    return {k: tuple(v) for k, v in data.items()}

def _load_grid(path):
    with open(path) as f:
        lines = [line.rstrip("\n").rstrip("\r") for line in f]
    while lines and lines[-1] == "":
        lines.pop()
    return lines

def _print_report(name, problems):
    if not problems:
        print(f"[PASS] {name}")
    else:
        print(f"[FAIL] {name}")
        for p in problems:
            print(f"    - {p}")

def cmd_render(args):
    palette = _load_palette(args.palette)
    grid = _load_grid(args.grid)
    pixels = render(grid, palette)
    write_png(args.out, pixels)
    print(f"wrote {args.out} ({len(pixels[0]) if pixels else 0}x{len(pixels)})")
    stem, _ = os.path.splitext(args.out)
    if not args.no_preview:
        preview_path = f"{stem}.preview.png"
        write_png(preview_path, upscale(pixels, args.preview_scale))
        print(f"wrote {preview_path}")
    if not args.no_mockup:
        mockup_path = f"{stem}.mockup.png"
        write_png(mockup_path, mockup(pixels, scale=args.mockup_scale))
        print(f"wrote {mockup_path}")

def cmd_validate(args):
    palette = _load_palette(args.palette)
    pixels = read_png(args.png)
    failed = False

    if args.width is not None and args.height is not None:
        probs = check_dims(pixels, args.width, args.height)
        _print_report("dims", probs)
        failed = failed or bool(probs)

    probs = check_palette(pixels, list(palette.values()))
    _print_report("palette", probs)
    failed = failed or bool(probs)

    bg = palette.get(args.bg_char)
    if bg is not None:
        probs = check_background(pixels, bg)
        _print_report("background", probs)
        failed = failed or bool(probs)

    try:
        grid = to_grid(pixels, palette)
    except ValueError as e:
        print(f"[SKIP] orphans/coverage/banding: {e}")
        grid = None

    if grid is not None:
        for name, fn in (
            ("orphans", check_orphans),
            ("coverage", check_coverage),
            ("banding", check_banding),
        ):
            probs = fn(grid, args.bg_char)
            _print_report(name, probs)
            failed = failed or bool(probs)

    sys.exit(1 if failed else 0)

def cmd_sheet(args):
    pixels_list = [read_png(p) for p in args.pngs]
    bg = tuple(int(x) for x in args.bg.split(","))
    canvas = contact_sheet(pixels_list, args.cols, pad=args.pad, bg=bg)
    write_png(args.out, canvas)
    print(f"wrote {args.out}")

def cmd_preview(args):
    pixels = read_png(args.png)
    if args.out:
        out = args.out
    else:
        stem, ext = os.path.splitext(args.png)
        out = f"{stem}.preview{args.scale}x{ext or '.png'}"
    write_png(out, upscale(pixels, args.scale))
    print(f"wrote {out}")

def build_argparser():
    p = argparse.ArgumentParser(
        prog="sprite_kit",
        description="Render ASCII pixel-index grids to PNG and lint them for sprite craft.",
    )
    sub = p.add_subparsers(dest="command", required=True)

    pr = sub.add_parser("render", help="Render a grid+palette to PNG, plus a preview and mockup.")
    pr.add_argument("grid", help="Path to a text file with the ASCII index grid.")
    pr.add_argument("palette", help="Path to a JSON file mapping chars to [r,g,b].")
    pr.add_argument("-o", "--out", default="sprite.png", help="Output PNG path.")
    pr.add_argument("--preview-scale", type=int, default=8, help="Upscale factor for the preview PNG.")
    pr.add_argument("--mockup-scale", type=int, default=3, help="Upscale factor used inside the mockup.")
    pr.add_argument("--no-preview", action="store_true", help="Skip the upscaled preview PNG.")
    pr.add_argument("--no-mockup", action="store_true", help="Skip the mockup PNG.")
    pr.set_defaults(func=cmd_render)

    pv = sub.add_parser("validate", help="Validate a rendered PNG against a palette and craft heuristics.")
    pv.add_argument("png", help="Path to the PNG to validate.")
    pv.add_argument("palette", help="Path to the JSON palette used to render it.")
    pv.add_argument("--bg-char", default=".", help="Palette character used as background.")
    pv.add_argument("--width", type=int, help="Expected width in pixels (enables check_dims).")
    pv.add_argument("--height", type=int, help="Expected height in pixels (enables check_dims).")
    pv.set_defaults(func=cmd_validate)

    ps = sub.add_parser("sheet", help="Combine several PNGs into a contact-sheet montage.")
    ps.add_argument("pngs", nargs="+", help="Paths to PNGs to combine.")
    ps.add_argument("-o", "--out", default="sheet.png", help="Output PNG path.")
    ps.add_argument("--cols", type=int, default=4, help="Number of columns in the montage.")
    ps.add_argument("--pad", type=int, default=4, help="Padding in pixels between cells.")
    ps.add_argument("--bg", default="255,255,255", help="Background color as 'r,g,b'.")
    ps.set_defaults(func=cmd_sheet)

    pp = sub.add_parser("preview", help="Upscale a PNG with nearest-neighbor for easier eyeballing.")
    pp.add_argument("png", help="Path to the PNG to upscale.")
    pp.add_argument("-o", "--out", help="Output PNG path (default: <input>.previewNx.png).")
    pp.add_argument("--scale", type=int, default=8, help="Upscale factor.")
    pp.set_defaults(func=cmd_preview)

    pt = sub.add_parser("selftest", help="Run the built-in self-test suite; exits nonzero on failure.")
    pt.set_defaults(func=lambda args: run_selftest())

    return p

def main(argv=None):
    args = build_argparser().parse_args(argv)
    args.func(args)

# --- Self-test ---

def _check(cond, label, results):
    print(f"[{'PASS' if cond else 'FAIL'}] {label}")
    results.append(bool(cond))

def run_selftest():
    results = []
    palette = {".": (240, 232, 200), "#": (40, 40, 40)}
    grid = [
        "........",
        "...##...",
        "..####..",
        "..####..",
        ".######.",
        ".######.",
        "..####..",
        "........",
    ]

    pixels = render(grid, palette)
    tmp_path = os.path.join(tempfile.gettempdir(), "sprite_kit_selftest.png")
    write_png(tmp_path, pixels)
    pixels2 = read_png(tmp_path)
    os.remove(tmp_path)
    _check(pixels2 == pixels, "round-trip render->write_png->read_png is pixel-exact", results)
    _check(to_grid(pixels2, palette) == grid, "round-trip to_grid recovers the original ASCII grid", results)

    _check(check_dims(pixels, 8, 8) == [], "check_dims passes on correct dimensions", results)
    _check(check_dims(pixels, 4, 4) != [], "check_dims fails on wrong dimensions", results)

    allowed = list(palette.values())
    _check(check_palette(pixels, allowed) == [], "check_palette passes with the full allowed set", results)
    bad_pixels = [row[:] for row in pixels]
    bad_pixels[0][0] = (255, 0, 255)
    _check(check_palette(bad_pixels, allowed) != [], "check_palette fails on a rogue color", results)

    bg = palette["."]
    _check(check_background(pixels, bg) == [], "check_background passes (present + >=2 corners)", results)
    all_fg = [[(40, 40, 40)] * 8 for _ in range(8)]
    _check(check_background(all_fg, bg) != [], "check_background fails when bg is entirely absent", results)

    _check(check_orphans(grid, ".") == [], "check_orphans passes on a clean connected blob", results)
    orphan_grid = [
        "........",
        "..#.....",
        "........",
        "..####..",
        "..####..",
        "........",
        "........",
        "........",
    ]
    _check(check_orphans(orphan_grid, ".") != [], "check_orphans fails on an isolated pixel", results)
    diag_grid = [
        ".....",
        "..#..",
        "...#.",
        ".....",
    ]
    _check(check_orphans(diag_grid, ".") != [], "check_orphans fails on a diagonal-only joint", results)

    _check(check_coverage(grid, ".") == [], "check_coverage passes within [25%,70%]", results)
    low_grid = ["........"] * 7 + ["...#...."]
    _check(check_coverage(low_grid, ".") != [], "check_coverage fails when far too sparse", results)
    high_grid = ["########"] * 8
    _check(check_coverage(high_grid, ".") != [], "check_coverage fails when far too dense", results)

    _check(check_banding(grid, ".") == [], "check_banding passes on the clean blob (no straight runs)", results)
    straight_grid = ["..####.."] * 8
    _check(check_banding(straight_grid, ".") != [], "check_banding fails on a long axis-aligned edge", results)
    diagonal_grid = [
        "#.......",
        ".#......",
        "..#.....",
        "...#....",
        "....#...",
        "........",
        "........",
        "........",
    ]
    _check(check_banding(diagonal_grid, ".") != [], "check_banding fails on a 45-degree staircase", results)

    report = validate_all(grid, palette, 8, 8, allowed, ".")
    _check(
        all(len(v) == 0 for v in report.values()),
        "validate_all passes end-to-end on the clean synthetic sprite",
        results,
    )

    up = upscale(pixels, 2)
    _check(len(up) == 16 and len(up[0]) == 16 and up[0][0] == pixels[0][0], "upscale doubles dimensions correctly", results)
    mock = mockup(pixels, scale=2)
    _check(len(mock) == round(16 * 4.5) and len(mock[0]) == round(16 * 4.5), "mockup centers on a 4.5x field by default", results)
    sheet = contact_sheet([pixels, pixels], cols=2, pad=2, bg=(0, 0, 0))
    _check(len(sheet[0]) == 8 * 2 + 2 * 3, "contact_sheet lays out cells with padding", results)
    s = stats(grid, ".")
    _check(0.0 < s["coverage"] < 1.0 and s["bbox"] is not None and s["symmetry"] > 0.9, "stats reports coverage/bbox/symmetry sanely", results)

    ok = all(results)
    print(f"\n{sum(results)}/{len(results)} checks passed — {'PASS' if ok else 'FAIL'}")
    sys.exit(0 if ok else 1)

if __name__ == "__main__":
    main()
