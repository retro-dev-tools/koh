"""Extract the round-9 flagship bat's refined components into the parts library."""
import sys, os

SKILL = "/home/user/koh/.claude/skills/pixel-art-sprites/scripts"
sys.path.insert(0, SKILL)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parts_kit import Part, save_part
import build_bat

# Rebuild the final grid exactly as build_bat.main does (without writing files).
fig = build_bat.build()
grid = fig.render((32, 32), build_bat.PAL, bg_char=".", outline_char="K")
grid = [list(r) for r in grid]
def put(x, y, c):
    grid[y][x] = c
for x in (13, 14):
    put(x, 10, "G"); put(x, 11, "G")
for x in (17, 18):
    put(x, 10, "G"); put(x, 11, "G")
for x in (14, 15, 16, 17):
    put(x, 13, ".")
for x in (15, 16):
    put(x, 14, ".")
for x, y in ((5, 6), (6, 7), (7, 8)):
    if grid[y][x] in ("V", "K"):
        put(x, y, "G")
for x, y in ((25, 6), (24, 7), (23, 8)):
    if grid[y][x] in ("V", "K"):
        put(x, y, "G")
for x, y in ((27, 10), (26, 12)):
    if grid[y][x] == "V":
        put(x, y, "K")
empty = ["."] * 32
grid = [list(empty), list(empty)] + grid[:-2]

# Char -> role. Interior parchment (maw) becomes H; exterior '.' stays transparent.
MAW = {(14, 15), (15, 15), (16, 15), (17, 15), (15, 16), (16, 16)}

def to_role(x, y):
    c = grid[y][x]
    if c == ".":
        return "H" if (x, y) in MAW else "."
    return {"K": "D", "V": "M", "G": "A"}[c]

def slice_part(name, x0, y0, x1, y1, anchors, tags):
    rows = []
    for y in range(y0, y1 + 1):
        rows.append("".join(to_role(x, y) for x in range(x0, x1 + 1)))
    # anchors given in canvas coords -> part-local
    local = {k: (x - x0, y - y0) for k, (x, y) in anchors.items()}
    p = Part(name, rows, local, tags)
    save_part(p)
    print(f"saved {name}: {p.w}x{p.h} anchors={p.anchors}")
    return p

# Left (raised) wing: canvas cols 1-13, rows 3-18. Shoulder anchor where it meets torso.
slice_part("wing_raised", 1, 3, 13, 18, {"shoulder": (12, 16)}, ("wing", "raised", "membrane"))
# Head with ears, eyes, maw: cols 10-21, rows 4-18 (ears start ~row 4).
slice_part("head_horned", 10, 4, 21, 17, {"neck": (15, 17)}, ("head", "eared", "menacing"))
# Furred torso: cols 11-20, rows 16-25.
slice_part("body_furred", 11, 16, 20, 25, {"neck": (15, 16), "hipL": (14, 25), "hipR": (17, 25)}, ("body", "furred"))
# One dangling clawed foot (left): cols 12-15, rows 24-27.
slice_part("foot_claw", 12, 24, 15, 27, {"hip": (14, 24)}, ("foot", "claw"))
