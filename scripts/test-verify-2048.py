import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image, ImageSequence

spec = importlib.util.spec_from_file_location("verifier", Path(__file__).with_name("verify-2048.py"))
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
HOST = Path(os.environ.get("KOH_TEST_HOST", verifier.HOST))
ROM = Path(os.environ.get("KOH_2048_ROM", verifier.ROOT / "samples/asm/gb-2048/build/2048.gbc"))


class CaptureTests(unittest.TestCase):
    def test_default_host_tracks_cargo_target_directory(self):
        for target in ("relative-output", str(Path(tempfile.gettempdir()) / "koh-custom-target")):
            with patch.dict(os.environ, CARGO_TARGET_DIR=target):
                module = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(module)
                expected = (verifier.ROOT / target).resolve() / "release" / verifier.HOST.name
                self.assertEqual(module.HOST, expected)

    def test_native_artifacts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result = subprocess.run([str(HOST), "Verify2048", str(ROM), str(root)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            manifest = json.loads((root / "manifest.json").read_text())
            self.assertEqual((manifest["passes"], manifest["failures"]), (11, 0))
            self.assertEqual(len(manifest["sheet"]), 11)
            for tile in manifest["sheet"]:
                if "name" in tile:
                    with Image.open(root / f"shot_{tile['name']}.png") as image, Image.open(root / tile["path"]) as raw:
                        self.assertEqual(image.mode, "RGB")
                        self.assertEqual(image.size, (480, 432))
                        self.assertEqual(image.tobytes(), raw.convert("RGB").resize((480, 432), Image.Resampling.NEAREST).tobytes())
            with Image.open(root / "shot_contact.png") as image:
                self.assertEqual(image.size, (1032, 1320))
            for name in ("strip", "down_strip", "up_strip", "left_strip"):
                with Image.open(root / f"shot_anim_{name}.png") as image:
                    self.assertEqual(image.size, (4316, 314))
            with Image.open(root / "shot_anim.gif") as image:
                self.assertEqual(image.size, (160, 144))
                self.assertEqual(image.info["loop"], 0)
                frames = [(frame.convert("RGB").tobytes(), frame.info["duration"]) for frame in ImageSequence.Iterator(image)]
            self.assertEqual(len(frames), 13)
            for (pixels, duration), path in zip(frames, manifest["animations"]["right"], strict=True):
                with Image.open(root / path) as raw:
                    self.assertEqual(pixels, raw.convert("RGB").tobytes())
                self.assertEqual(duration, 60)

    def test_failing_rom_removes_stale_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "manifest.json").write_text("stale success")
            rom = root / "bad.gbc"
            rom.write_bytes(b"bad")
            result = subprocess.run([str(HOST), "Verify2048", str(rom), str(root)], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((root / "manifest.json").exists())


if __name__ == "__main__":
    unittest.main()
