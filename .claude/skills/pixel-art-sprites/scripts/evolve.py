"""evolve — population-based variant selection for part compositions.

WHY: agents judge renders far better than they generate them. Instead of iterating one
candidate (whose flaws anchor every revision), expand a creature SPEC into a deterministic
POPULATION of variants, render them all on one contact sheet, pick winners visually at
display scale, and breed the next generation from the winners' genomes. Selection pressure
substitutes for the continuous visual feedback loop an agent lacks while drawing.

A SPEC is a dict:
    {
      "size": [32, 32],
      "slots": [
        {"name": "body",  "parts": ["body_furred"],            "at": [15, 18], "anchor": "neck", "z": 1},
        {"name": "head",  "parts": ["head_horned"],            "at": [15, 18], "anchor": "neck", "z": 3},
        {"name": "wingL", "parts": ["wing_raised"],            "at": [12, 16], "anchor": "shoulder", "z": 0},
        {"name": "wingR", "parts": ["wing_raised"],            "at": [19, 16], "anchor": "shoulder", "z": 0, "flip": true},
        ...
      ],
      "jitter": {"wingL": [2, 2], "head": [1, 1]}   # per-slot max |dx|,|dy| mutation
    }
Genome = per-slot (part_index, dx, dy). Mutations move placements within the slot's
jitter box and swap among the slot's allowed parts. Everything is seeded — same spec,
seed, and generation always reproduce the same population (reviewable, re-runnable).
"""

import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parts_kit import load_library, Composition, outline_roles
import sprite_kit as sk


def _rng(seed, *salt):
    """Tiny deterministic PRNG stream from sha256 — no global random state."""
    h = hashlib.sha256((str(seed) + ":" + ":".join(map(str, salt))).encode()).digest()
    i = 0
    while True:
        yield h[i % 32] / 255.0
        i += 1
        if i % 32 == 0:
            h = hashlib.sha256(h).digest()


def _mutate(genome, spec, seed, gen, idx):
    r = _rng(seed, gen, idx)
    out = []
    for slot, (pi, dx, dy) in zip(spec["slots"], genome):
        jx, jy = spec.get("jitter", {}).get(slot["name"], (0, 0))
        step = lambda j: int(round((next(r) * 2 - 1) * j))
        npi = pi
        if len(slot.get("parts", [])) > 1 and next(r) < 0.25:
            npi = int(next(r) * len(slot["parts"])) % len(slot["parts"])
        out.append((npi, max(-jx, min(jx, dx + step(jx))), max(-jy, min(jy, dy + step(jy)))))
    return out


def base_genome(spec):
    return [(0, 0, 0) for _ in spec["slots"]]


def compose(spec, genome, lib):
    c = Composition(tuple(spec["size"]))
    for slot, (pi, dx, dy) in zip(spec["slots"], genome):
        part = lib[slot["parts"][pi]]
        at = (slot["at"][0] + dx, slot["at"][1] + dy)
        c.place(part, at=at, anchor=slot.get("anchor"), z=slot.get("z", 0),
                flip=slot.get("flip", False))
    return c


def population(spec, seed=1, gen=0, parents=None, n=8):
    """Generation `gen`: variant 0 is the (first) parent unchanged; the rest are mutants
    bred round-robin from `parents` (or from the base genome at gen 0)."""
    parents = parents or [base_genome(spec)]
    pop = [parents[0]]
    i = 1
    while len(pop) < n:
        pop.append(_mutate(parents[i % len(parents)], spec, seed, gen, i))
        i += 1
    return pop


def render_variant(spec, genome, lib, role_map, palette, bg_char="."):
    roles = outline_roles(compose(spec, genome, lib).render_roles())
    return ["".join(bg_char if ch == "." else role_map[ch] for ch in row) for row in roles]


def contact_sheet(spec, genomes, lib, role_map, palette, out_path, scale=3, cols=4):
    """Render every variant at display scale onto one labeled sheet; returns pixel grids."""
    cells = []
    for g in genomes:
        grid = render_variant(spec, g, lib, role_map, palette)
        cells.append(sk.upscale(sk.render(grid, palette), scale))
    bg = palette.get(".", (240, 232, 200))
    sheet = sk.contact_sheet(cells, cols, pad=8, bg=bg)
    sk.write_png(out_path, sheet)
    return cells


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ("gen",):
        print("usage: evolve.py gen <spec.json> [--seed N] [--gen N] [--n N] "
              "[--parents idx,idx] [--state state.json] [--out sheet.png]")
        return 1
    with open(sys.argv[2]) as f:
        spec = json.load(f)
    args = dict(zip(sys.argv[3::2], sys.argv[4::2]))
    seed = int(args.get("--seed", 1))
    gen = int(args.get("--gen", 0))
    n = int(args.get("--n", 8))
    state_path = args.get("--state", os.path.splitext(sys.argv[2])[0] + ".state.json")
    out = args.get("--out", os.path.splitext(sys.argv[2])[0] + f".gen{gen}.png")

    parents = None
    if gen > 0 and os.path.exists(state_path):
        with open(state_path) as f:
            state = json.load(f)
        prev = state[str(gen - 1)]["population"]
        picks = [int(x) for x in args.get("--parents", "0").split(",")]
        parents = [[tuple(t) for t in prev[p]] for p in picks]

    pop = population(spec, seed=seed, gen=gen, parents=parents, n=n)
    lib = load_library()
    role_map = spec.get("role_map", {"O": "K", "D": "K", "M": "V", "A": "G", "H": "."})
    palette = {k: tuple(v) for k, v in spec["palette"].items()}
    contact_sheet(spec, pop, lib, role_map, palette, out)

    state = {}
    if os.path.exists(state_path):
        with open(state_path) as f:
            state = json.load(f)
    state[str(gen)] = {"population": [[list(t) for t in g] for g in pop], "seed": seed}
    with open(state_path, "w") as f:
        json.dump(state, f)
    print(f"gen {gen}: {len(pop)} variants -> {out}; state -> {state_path}")
    print("LOOK at the sheet, pick winner indices (row-major from 0), then run with "
          f"--gen {gen + 1} --parents <picks>.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
