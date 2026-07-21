"""parts_kit — compose sprites from a curated library of small, individually-refined parts.

WHY THIS EXISTS: whole-creature freehand or geometric drawing by agents plateaus at
"recognizable but amateur" (measured across 15+ attempts, three skill generations, and
multiple model tiers). Two things DO work: (1) very small components converge to genuine
quality under iteration — an 8x8 wing is a tractable perfection target where a 32x32
creature is not — and (2) agents judge renders far better than they generate them. This
module is the first half of the compose-and-select methodology: assemble sprites from a
library of verified parts. `evolve.py` is the second half (population selection).

A PART is a small ASCII grid over PALETTE ROLES, not concrete colors:
    'O' outline/darkest    'D' dark mass    'M' mid/membrane    'A' accent
    'H' highlight/lightest '.' transparent (not painted)
A composition binds roles to a concrete sprite palette at render time, so one part works
in any 4-color scheme (role->char map may send two roles to the same char — e.g. on Game
Boy, H usually binds to the background shade and A to the single accent color).

Parts carry named ANCHORS — pixel coordinates that mark attachment points (a wing's
shoulder, a head's neck). Composition places parts by aligning anchors, with optional
horizontal flip. Placement is z-ordered; later/higher-z parts overwrite.
"""

import json
import os

PARTS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "parts")
ROLES = "ODMAH."


class Part:
    """A reusable sprite component: role grid + anchors + metadata."""

    def __init__(self, name, grid, anchors, tags=()):
        self.name = name
        self.grid = [str(r) for r in grid]
        w = len(self.grid[0])
        if any(len(r) != w for r in self.grid):
            raise ValueError(f"part {name}: ragged grid")
        bad = {c for r in self.grid for c in r} - set(ROLES)
        if bad:
            raise ValueError(f"part {name}: unknown role chars {bad}")
        self.w, self.h = w, len(self.grid)
        self.anchors = {k: (int(x), int(y)) for k, (x, y) in anchors.items()}
        self.tags = tuple(tags)

    def flipped(self):
        """Horizontal mirror (anchors re-mapped) — one authored wing serves both sides."""
        grid = ["".join(reversed(r)) for r in self.grid]
        anchors = {k: (self.w - 1 - x, y) for k, (x, y) in self.anchors.items()}
        return Part(self.name + "~flip", grid, anchors, self.tags)


def load_library():
    """Load every part JSON under assets/parts/ into {name: Part}."""
    lib = {}
    if not os.path.isdir(PARTS_DIR):
        return lib
    for fn in sorted(os.listdir(PARTS_DIR)):
        if fn.endswith(".json"):
            with open(os.path.join(PARTS_DIR, fn)) as f:
                spec = json.load(f)
            p = Part(spec["name"], spec["grid"], spec.get("anchors", {}), spec.get("tags", ()))
            lib[p.name] = p
    return lib


def save_part(part):
    """Persist a Part to the library (authoring helper)."""
    os.makedirs(PARTS_DIR, exist_ok=True)
    spec = {
        "name": part.name,
        "grid": part.grid,
        "anchors": {k: list(v) for k, v in part.anchors.items()},
        "tags": list(part.tags),
    }
    with open(os.path.join(PARTS_DIR, part.name + ".json"), "w") as f:
        json.dump(spec, f, indent=1)


class Composition:
    """Assemble parts on a canvas by anchor alignment; render to a sprite grid."""

    def __init__(self, size):
        self.w, self.h = size
        self.placements = []  # (part, ox, oy, z)

    def place(self, part, at, anchor=None, z=0, flip=False):
        """Place `part` so that its `anchor` (or top-left) sits at canvas point `at`."""
        p = part.flipped() if flip else part
        ax, ay = p.anchors.get(anchor, (0, 0)) if anchor else (0, 0)
        ox, oy = at[0] - ax, at[1] - ay
        self.placements.append((p, ox, oy, z))
        return self

    def join(self, part_a_idx, part_b_idx):
        """No-op marker retained for spec readability; overlap does the joining."""
        return self

    def render_roles(self):
        """Composite all placements into one role grid ('.' = empty)."""
        canvas = [["."] * self.w for _ in range(self.h)]
        for p, ox, oy, _z in sorted(self.placements, key=lambda t: t[3]):
            for y, row in enumerate(p.grid):
                for x, c in enumerate(row):
                    if c == ".":
                        continue
                    cx, cy = ox + x, oy + y
                    if 0 <= cx < self.w and 0 <= cy < self.h:
                        canvas[cy][cx] = c
        return ["".join(r) for r in canvas]

    def render(self, role_map, bg_char="."):
        """Bind roles to sprite palette chars. role_map: e.g. {'O':'K','D':'K','M':'V','A':'G','H':'.'}"""
        roles = self.render_roles()
        out = []
        for row in roles:
            out.append("".join(bg_char if c == "." else role_map[c] for c in row))
        return out


def outline_roles(roles, bg="."):
    """Ensure a 1px 'O' ring: any non-bg cell 4-adjacent to bg becomes 'O' (erosion style,
    silhouette never grows). Run after composition so part seams inside stay untouched."""
    h, w = len(roles), len(roles[0])
    g = [list(r) for r in roles]
    for y in range(h):
        for x in range(w):
            if g[y][x] == bg:
                continue
            edge = (
                x == 0 or y == 0 or x == w - 1 or y == h - 1
                or g[y][x - 1] == bg or g[y][x + 1] == bg
                or g[y - 1][x] == bg or g[y + 1][x] == bg
            )
            if edge:
                g[y][x] = "O"
    return ["".join(r) for r in g]


if __name__ == "__main__":
    lib = load_library()
    print(f"library: {len(lib)} parts")
    for name, p in lib.items():
        print(f"  {name}: {p.w}x{p.h} anchors={list(p.anchors)} tags={p.tags}")
