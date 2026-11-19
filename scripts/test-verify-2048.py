import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

from PIL import Image, ImageSequence

spec = importlib.util.spec_from_file_location("verifier", Path(__file__).with_name("verify-2048.py"))
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


class CaptureTests(unittest.TestCase):
    def fixture(self, root, size=(160, 144)):
        paths = []
        for index in range(13):
            path = f"frame{index}.png"
            Image.new("RGBA", size, (index * 10, 30, 80, 255)).save(root / path)
            paths.append(path)
        (root / "manifest.json").write_text(json.dumps({
            "sheet": [{"name": "title", "label": "TITLE", "path": paths[0]}],
            "animations": {name: paths for name in ("right", "down", "up", "left")},
        }))

    def test_preserves_pixels_dimensions_and_animation_timing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            verifier.render(root)
            with Image.open(root / "shot_title.png") as image:
                self.assertEqual(image.size, (480, 432))
                self.assertEqual(image.getpixel((479, 431)), (0, 30, 80))
            with Image.open(root / "shot_contact.png") as image:
                self.assertEqual(image.size, (1032, 330))
                self.assertEqual(image.getpixel((0, 0)), (24, 24, 28))
                self.assertEqual(image.getpixel((12, 30)), (0, 30, 80))
            for name in ("strip", "down_strip", "up_strip", "left_strip"):
                with Image.open(root / f"shot_anim_{name}.png") as image:
                    self.assertEqual(image.size, (4316, 314))
                    self.assertEqual(image.getpixel((12 * 332 + 6, 20)), (120, 30, 80))
            with Image.open(root / "shot_anim.gif") as image:
                self.assertEqual(image.size, (160, 144))
                self.assertEqual(image.info["loop"], 0)
                frames = [(frame.convert("RGB").getpixel((0, 0)), frame.info["duration"])
                          for frame in ImageSequence.Iterator(image)]
                self.assertEqual(frames, [((index * 10, 30, 80), 60) for index in range(13)])

    def test_rejects_incorrect_frame_dimensions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root, size=(159, 144))
            with self.assertRaisesRegex(ValueError, "160x144 RGBA"):
                verifier.render(root)


if __name__ == "__main__":
    unittest.main()
