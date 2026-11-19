import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tomllib


root = Path(__file__).resolve().parent.parent
catalog = root / "licenses" / "rust-ui"
parser = argparse.ArgumentParser(description="Verify UI notice coverage and copied text hashes")
parser.add_argument("--target")
args = parser.parse_args()
manifest = json.loads((catalog / "manifest.json").read_text(encoding="utf-8"))
assert manifest["format_version"] == 1
packages = {(p["name"], p["version"]): p for p in manifest["packages"]}
assert len(packages) == len(manifest["packages"]), "duplicate notice package"
checked = set()
for notice in manifest["standard_license_texts"] + [n for p in packages.values() for n in p["notices"]]:
    path = notice["path"]
    assert re.fullmatch(r"texts/[0-9a-f]{64}\.txt", path), "invalid notice path"
    assert hashlib.sha256((catalog / path).read_bytes()).hexdigest() == notice["sha256"], "notice hash mismatch: " + path
    checked.add(path)
lock = tomllib.loads((root / "Cargo.lock").read_text(encoding="utf-8"))
checksums = {(p["name"], p["version"]): p["checksum"] for p in lock["package"] if "checksum" in p}
for key, package in packages.items():
    assert checksums.get(key) == package["archive_sha256"], "notice archive differs from Cargo.lock: " + str(key)
target = args.target
if target is None:
    version = subprocess.check_output(["rustc", "--version", "--verbose"], text=True)
    target = next(line.removeprefix("host: ") for line in version.splitlines() if line.startswith("host: "))
assert target in manifest["targets"], "notice catalog does not cover target: " + target
metadata = json.loads(subprocess.check_output([
    "cargo", "metadata", "--manifest-path", str(root / "rust/koh-ui/Cargo.toml"),
    "--locked", "--format-version", "1", "--filter-platform", target,
], cwd=root, text=True, encoding="utf-8"))
definitions = {p["id"]: p for p in metadata["packages"]}
nodes = {n["id"]: n for n in metadata["resolve"]["nodes"]}
pending = [p["id"] for p in definitions.values() if p["name"] == "koh-ui"]
seen = set()
count = 0
while pending:
    ident = pending.pop()
    if ident in seen:
        continue
    seen.add(ident)
    package = definitions[ident]
    if package["source"]:
        key = package["name"], package["version"]
        assert key in packages, "missing UI notice package: " + str(key)
        assert package["license"] == packages[key]["license"], "changed license declaration: " + str(key)
        count += 1
    pending.extend(dep["pkg"] for dep in nodes[ident]["deps"])
review = [p["name"] + "@" + p["version"] for p in packages.values() if p["declaration_only"]]
print(f"Notice catalog: {count} active packages for {target}; {len(packages)} catalog entries; {len(checked)} verified texts")
print("Upstream declaration-only records requiring release review: " + ", ".join(review))
