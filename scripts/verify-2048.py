#!/usr/bin/env python3
"""Launch the native assembly 2048 verifier and artifact writer."""

import argparse
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
TARGET = (ROOT / os.environ.get("CARGO_TARGET_DIR", "target")).resolve()
HOST = TARGET / "release" / ("koh-test-host.exe" if os.name == "nt" else "koh-test-host")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", type=Path, nargs="?", default=ROOT / "samples/asm/gb-2048/build/2048.gbc")
    parser.add_argument("output", type=Path, nargs="?", default=ROOT / "artifacts/verification/2048")
    parser.add_argument("--host", type=Path, default=HOST)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        subprocess.run(["cargo", "build", "--release", "-p", "koh-test-host", "--locked"], cwd=ROOT, check=True)
    return subprocess.run([str(args.host), "Verify2048", str(args.rom), str(args.output)]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
