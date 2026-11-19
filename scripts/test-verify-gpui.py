import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import zlib

spec = importlib.util.spec_from_file_location("probe", Path(__file__).with_name("verify-gpui.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class PngOracleTests(unittest.TestCase):
    def test_up_filtered_rgba_and_corrupt_checksum(self):
        row = bytes([31, 63, 127, 255]) * 160
        rows = b"\x02" + row + (b"\x02" + bytes(640)) * 143
        data = (b"\x89PNG\r\n\x1a\n"
                + probe.png_chunk(b"IHDR", struct.pack(">IIBBBBB", 160, 144, 8, 6, 0, 0, 0))
                + probe.png_chunk(b"IDAT", zlib.compress(rows))
                + probe.png_chunk(b"IEND", b""))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "filtered.png"
            path.write_bytes(data)
            self.assertEqual(probe.read_oracle(path), row * 144)
            path.write_bytes(data[:-1] + bytes([data[-1] ^ 1]))
            with self.assertRaises(AssertionError):
                probe.read_oracle(path)


if __name__ == "__main__":
    unittest.main()
