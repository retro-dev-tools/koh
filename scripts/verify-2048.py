#!/usr/bin/env python3
"""Verify the assembly 2048 game with Rust and render its capture artifacts."""

import argparse
import json
import os
from pathlib import Path
import subprocess

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "target/release" / ("koh-test-host.exe" if os.name == "nt" else "koh-test-host")


def render(output):
    manifest = json.loads((output / "manifest.json").read_text())

    def image(path):
        with Image.open(output / path) as source:
            if source.size != (160, 144) or source.mode != "RGBA":
                raise ValueError(f"Expected 160x144 RGBA frame: {path}")
            return source.convert("RGB")

    def grid(tiles, path, columns, padding, label_height):
        cell_width, cell_height = 320 + padding * 2, 288 + padding * 2 + label_height
        rows = (len(tiles) + columns - 1) // columns
        canvas = Image.new("RGB", (columns * cell_width, rows * cell_height), (24, 24, 28))
        draw = ImageDraw.Draw(canvas)
        for index, tile in enumerate(tiles):
            x = index % columns * cell_width + padding
            y = index // columns * cell_height + padding + label_height
            canvas.paste(image(tile["path"]).resize((320, 288), Image.Resampling.NEAREST), (x, y))
            draw.text((x, y - label_height), tile["label"], fill=(240, 240, 240))
        canvas.save(output / path)

    for tile in manifest["sheet"]:
        if "name" in tile:
            image(tile["path"]).resize((480, 432), Image.Resampling.NEAREST).save(
                output / f"shot_{tile['name']}.png"
            )
    grid(manifest["sheet"], "shot_contact.png", 3, 12, 18)
    for name, paths in manifest["animations"].items():
        tiles = [{"label": f"F{index}", "path": path} for index, path in enumerate(paths)]
        filename = "shot_anim_strip.png" if name == "right" else f"shot_anim_{name}_strip.png"
        grid(tiles, filename, len(tiles), 6, 14)
    animation = [image(path) for path in manifest["animations"]["right"]]
    animation[0].save(output / "shot_anim.gif", save_all=True, append_images=animation[1:],
                      duration=60, loop=0, disposal=2, optimize=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", type=Path, nargs="?", default=ROOT / "samples/asm/gb-2048/build/2048.gbc")
    parser.add_argument("output", type=Path, nargs="?", default=ROOT / "artifacts/verification/2048")
    parser.add_argument("--host", type=Path, default=HOST)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        subprocess.run(["cargo", "build", "--release", "-p", "koh-test-host", "--locked"], cwd=ROOT, check=True)
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = args.output / "manifest.json"
    manifest.unlink(missing_ok=True)
    result = subprocess.run([str(args.host), "Verify2048", str(args.rom), str(args.output)], cwd=ROOT)
    if manifest.exists():
        render(args.output)
    elif result.returncode == 0:
        raise RuntimeError("Verifier omitted the capture manifest")
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
