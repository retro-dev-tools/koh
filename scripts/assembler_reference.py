"""Build the historical behavioral reference outside the current source tree."""
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
COMMIT = "bf0a5fc1792b6022328af080cd50f7b4d7e143c5"
REFERENCE = Path(os.environ.get("KOH_CSHARP_REFERENCE_ROOT", Path(tempfile.gettempdir()) / "koh-csharp-reference-bf0a5fc"))
PROJECT = ROOT / "tools/rust-asm-verify/reference/Reference.csproj"
HELPER = PROJECT.parent / "bin/Release/net10.0/koh-csharp-reference.dll"


def command(args, **kwargs):
    result = subprocess.run([str(arg) for arg in args], text=True, capture_output=True, **kwargs)
    if result.returncode:
        raise RuntimeError(f"Reference command exited {result.returncode}: {args}\n{result.stdout}{result.stderr}")
    return result.stdout.strip()


def ensure_reference(build=True, dotnet="dotnet"):
    if not REFERENCE.exists():
        present = subprocess.run(["git", "cat-file", "-e", COMMIT], cwd=ROOT, capture_output=True).returncode == 0
        if not present:
            command(["git", "fetch", "origin", COMMIT], cwd=ROOT)
        command(["git", "worktree", "add", "--detach", REFERENCE, COMMIT], cwd=ROOT)
    actual = command(["git", "rev-parse", "HEAD"], cwd=REFERENCE)
    if actual != COMMIT:
        raise RuntimeError(f"Reference checkout is {actual}, expected {COMMIT}")
    dirty = command(["git", "status", "--porcelain", "--untracked-files=no"], cwd=REFERENCE)
    if dirty:
        raise RuntimeError(f"Reference checkout has tracked changes; preserve it and select a clean KOH_CSHARP_REFERENCE_ROOT:\n{dirty}")
    if build:
        env = dict(os.environ, HUSKY="0")
        command([dotnet, "build", PROJECT, "-c", "Release", "-m:1", f"-p:KohReferenceRoot={REFERENCE}"], cwd=ROOT, env=env)
    if not HELPER.is_file():
        raise RuntimeError("Historical reference helper is absent; run verification with builds enabled")
    return REFERENCE, HELPER
