import importlib.util
from pathlib import Path
import unittest

from PIL import Image, ImageDraw

spec = importlib.util.spec_from_file_location("probe", Path(__file__).with_name("verify-gpui-platform.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class LcdCaptureTests(unittest.TestCase):
    def setUp(self):
        self.oracle = Image.new("RGB", (160, 144), "white")
        ImageDraw.Draw(self.oracle).rectangle((20, 30, 25, 60), fill="black")

    def test_finds_integer_replicas_without_including_white_controls(self):
        for scale in [1, 2, 3, 6]:
            with self.subTest(scale=scale):
                screen = Image.new("RGB", (160 * scale + 400, 144 * scale + 120), (15, 23, 42))
                screen.paste(self.oracle.resize((160 * scale, 144 * scale), Image.Resampling.NEAREST), (30, 60))
                screen.paste("white", (300, 0, 460, 30))
                self.assertEqual(probe.find_lcd(screen, self.oracle), (30, 60, 30 + 160 * scale, 60 + 144 * scale))

    def test_rejects_changed_pixels_inside_a_candidate(self):
        screen = Image.new("RGB", (600, 600), (15, 23, 42))
        screen.paste(self.oracle.resize((480, 432), Image.Resampling.NEAREST), (30, 60))
        screen.putpixel((35, 65), (255, 0, 0))
        self.assertIsNone(probe.find_lcd(screen, self.oracle))

    def test_rejects_a_blank_capture(self):
        self.assertIsNone(probe.find_lcd(Image.new("RGB", (800, 600), "white"), self.oracle))


if __name__ == "__main__":
    unittest.main()
