"""Flagship bat — built first-hand. Round-by-round; ROUND controls variants while iterating."""
import sys, os

SKILL = "/home/user/koh/.claude/skills/pixel-art-sprites/scripts"
sys.path.insert(0, SKILL)
from shape_kit import Figure, ellipse, capsule, fan, polygon, preview
from sprite_kit import validate_all, stats, render, write_png, upscale, mockup
import sprite_kit as sk

OUT = os.path.dirname(os.path.abspath(__file__))

PAL = {
    ".": (240, 232, 200),  # parchment bg + maw gap + eye shine
    "V": (150, 90, 150),   # violet membranes + fur sheen
    "G": (90, 160, 100),   # green accents: eyes, leading-edge light
    "K": (40, 32, 48),     # ink: body, outline, ribs, fangs
}
ALLOWED = set(PAL.values())

def build():
    fig = Figure(s=8)

    # --- silhouette (add): the iconic raised-V spread (menacing hover). Gesture
    # asymmetry lives in 1-2px DETAIL differences, not in the pose — a lesson from
    # rounds 1-4: strong pose asymmetry is illegible at 32px.
    fig.add("head", ellipse(15.5, 12.0, 4.6, 4.0), "K", z=3)
    fig.add("body", ellipse(15.5, 18.6, 4.6, 5.0), "K", z=2)
    # Ears: tall triangles, left one taller and splayed a touch more (the asymmetry).
    fig.add("earL", polygon([(12.6, 9.2), (11.0, 3.2), (15.0, 8.0)]), "K", z=3)
    fig.add("earR", polygon([(16.4, 8.0), (18.8, 4.0), (19.8, 9.2)]), "K", z=3)
    # LEFT wing: leading edge from shoulder to the top-left corner, trailing edge
    # descends with shallow scallops; attach overlaps the torso (no pinch).
    fig.add(
        "wingL",
        polygon([
            (14.0, 16.5),   # attach INSIDE torso
            (11.6, 12.4),   # shoulder
            (7.0, 6.0),     # elbow on the leading edge
            (3.0, 2.4),     # wingtip (top-left)
            (2.0, 6.8),     # outer edge drops
            (4.8, 8.2),     # valley 1
            (3.4, 11.6),    # finger point
            (8.2, 13.4),    # ease into the attach along the arm line
        ]),
        "V", z=1,
    )
    # RIGHT wing: mirrored, tip 1px higher and outer edge one scallop longer (detail
    # asymmetry only).
    fig.add(
        "wingR",
        polygon([
            (17.0, 16.5),   # attach INSIDE torso
            (19.6, 12.2),   # shoulder
            (24.2, 5.8),    # elbow
            (28.4, 1.8),    # wingtip (top-right, 1px higher than left)
            (29.6, 6.4),    # outer edge drops
            (26.6, 8.0),    # valley 1
            (28.4, 11.4),   # finger point
            (22.6, 13.6),   # ease into the attach along the arm line
        ]),
        "V", z=1,
    )
    # Dangling clawed feet under the hanging body.
    fig.add("legL", capsule(14.0, 22.0, 13.0, 25.4, 0.95, 0.6), "K", z=2)
    fig.add("legR", capsule(17.0, 22.2, 18.0, 25.6, 0.95, 0.6), "K", z=2)

    # --- paint (flats + detail, never outlined) ---
    # Membrane finger-ribs: one per wing from the shoulder toward the far valley, plus a
    # short second rib toward the lower finger (thin, INSIDE the membrane).
    fig.paint("ribL1", capsule(11.2, 12.2, 4.4, 8.0, 0.36, 0.26), "K", z=5)
    fig.paint("ribR1", capsule(19.8, 12.0, 26.6, 7.8, 0.36, 0.26), "K", z=5)
    # Fur sheen crescents on the chest (violet on ink).
    fig.paint("fur1", capsule(13.6, 16.2, 16.2, 16.8, 0.5, 0.4), "V", z=6)
    fig.paint("fur2", capsule(15.0, 18.8, 17.6, 19.4, 0.5, 0.4), "V", z=6)
    fig.paint("fur3", capsule(14.2, 21.0, 16.2, 21.4, 0.45, 0.35), "V", z=6)
    # Face is hand-polished on the grid (paint geometry is too mushy at eye scale).

    return fig

def main():
    fig = build()
    grid = fig.render((32, 32), PAL, bg_char=".", outline_char="K")
    grid = [list(r) for r in grid]

    # --- stage-3 hand polish: the face (identity), then light, then texture ---
    def put(x, y, c):
        grid[y][x] = c

    # Eyes: 2x2 green blocks (they carry the face at display scale), ink brow row above.
    for x in (13, 14):
        put(x, 10, "G"); put(x, 11, "G")
    for x in (17, 18):
        put(x, 10, "G"); put(x, 11, "G")
    # Maw: a 4-wide open slit with underbite fangs — parchment mouth, two ink fangs
    # rising from the chin at its corners, solid chin below.
    for x in (14, 15, 16, 17):
        put(x, 13, ".")
    for x in (15, 16):
        put(x, 14, ".")
    # (14,14) and (17,14) stay ink = the underbite fangs.

    # Leading-edge light (top-left light source): green rim just inside the left wing's
    # leading edge, where the stroke catches the light.
    for x, y in ((5, 6), (6, 7), (7, 8)):
        if grid[y][x] in ("V", "K"):
            put(x, y, "G")
    for x, y in ((25, 6), (24, 7), (23, 8)):
        if grid[y][x] in ("V", "K"):
            put(x, y, "G")

    # Trailing shade inside the right wing (away from the light): sparse ink dither.
    for x, y in ((27, 10), (26, 12)):
        if grid[y][x] == "V":
            put(x, y, "K")

    # Recenter: drop the figure 2 rows so it sits in the canvas rather than floating high.
    empty = "." * 32
    grid = [list(empty), list(empty)] + grid[:-2]

    grid = ["".join(r) for r in grid]
    px = render(grid, PAL)
    write_png(os.path.join(OUT, "bat.png"), px)
    write_png(os.path.join(OUT, "preview_8x.png"), upscale(px, 8))
    write_png(os.path.join(OUT, "mockup_3x.png"), mockup(px, scale=3, field=(144, 144)))
    problems = validate_all(grid, PAL, 32, 32, ALLOWED)
    st = stats(grid, ".")
    print("validate:", {k: v for k, v in problems.items() if v})
    print("coverage: %.1f%%" % (st["coverage"] * 100))
    for row in grid:
        print(row)

if __name__ == "__main__":
    main()
