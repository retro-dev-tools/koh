"""Prove the parts library round-trips: rebuild the flagship bat purely from parts."""
import sys, os

SKILL = "/home/user/koh/.claude/skills/pixel-art-sprites/scripts"
sys.path.insert(0, SKILL)
from parts_kit import load_library, Composition, outline_roles
from sprite_kit import render, write_png, upscale, mockup, validate_all

OUT = os.path.dirname(os.path.abspath(__file__))
PAL = {".": (240, 232, 200), "V": (150, 90, 150), "G": (90, 160, 100), "K": (40, 32, 48)}
ROLE_MAP = {"O": "K", "D": "K", "M": "V", "A": "G", "H": "."}

lib = load_library()
comp = Composition((32, 32))
comp.place(lib["body_furred"], at=(15, 18), anchor="neck", z=1)
comp.place(lib["head_horned"], at=(15, 18), anchor="neck", z=3)
comp.place(lib["wing_raised"], at=(12, 16), anchor="shoulder", z=0)
comp.place(lib["wing_raised"], at=(19, 16), anchor="shoulder", z=0, flip=True)
comp.place(lib["foot_claw"], at=(14, 27), anchor="hip", z=1)
comp.place(lib["foot_claw"], at=(17, 27), anchor="hip", z=1, flip=True)

roles = outline_roles(comp.render_roles())
grid = ["".join(ROLE_MAP[c] if c != "." else "." for c in row) for row in roles]
px = render(grid, PAL)
write_png(os.path.join(OUT, "composed_preview_8x.png"), upscale(px, 8))
write_png(os.path.join(OUT, "composed_mockup_3x.png"), mockup(px, scale=3, field=(144, 144)))
problems = validate_all(grid, PAL, 32, 32, set(PAL.values()))
print("validate:", {k: v for k, v in problems.items() if v})
for r in grid:
    print(r)
