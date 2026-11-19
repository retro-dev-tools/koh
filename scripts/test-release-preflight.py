import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


class ReleasePreflightTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location(
            "release_preflight", Path(__file__).with_name("release-preflight.py")
        )
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)

    def test_valid_toolchain_versions(self):
        for version in ("0.1.5", "1.0.0", "1.2.3-rc.1", "1.2.3+build.1"):
            with self.subTest(version=version):
                self.assertEqual(self.module.validate("tools-v" + version), version)

    def test_invalid_or_injected_tag(self):
        for tag in ("master", "ext-v01.2.3", "tools-v1.2", "tools-v1.2.3-01",
                    'tools-v1.2.3-$(touch /tmp/injected)', "tools-v1.2.3\n", "v1.2.3"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                self.module.validate(tag)

    def test_extension_version_must_match_both_manifests(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            extension = root / "editors/vscode"
            extension.mkdir(parents=True)
            (extension / "package.json").write_text(json.dumps({"version": "0.1.5"}))
            lock = extension / "package-lock.json"
            lock.write_text(json.dumps({"version": "0.1.5", "packages": {"": {"version": "0.1.5"}}}))
            self.assertEqual(self.module.validate("ext-v0.1.5", root), "0.1.5")
            with self.assertRaises(ValueError):
                self.module.validate("ext-v0.1.6", root)
            lock.write_text(json.dumps({"version": "0.1.5", "packages": {"": {"version": "0.1.4"}}}))
            with self.assertRaises(ValueError):
                self.module.validate("ext-v0.1.5", root)


if __name__ == "__main__":
    unittest.main()
