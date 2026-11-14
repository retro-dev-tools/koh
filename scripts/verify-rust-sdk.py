#!/usr/bin/env python3
"""Verify native SDK paths, incremental inputs, debug selection and failures."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "samples/csharp/gb-2048-cs/Gb2048CSharp.csproj"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    directory = (parser.parse_args().output or Path(tempfile.mkdtemp(prefix="koh SDK verification "))).resolve()
    if directory.exists() and any(directory.iterdir()):
        parser.error("Output directory must be empty")
    directory.mkdir(parents=True, exist_ok=True)
    records = []

    def build(name, properties=(), success=True):
        command = ["dotnet", "build", str(PROJECT), "-c", "Release", "-m:1", *properties]
        log = directory / f"{name}.log"
        with log.open("w") as stream:
            result = subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
        text = log.read_text(encoding="utf-8")
        records.append(dict(name=name, command=command, exit=result.returncode))
        (directory / "commands.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
        assert (result.returncode == 0) == success, text
        print(f"{name}: EXIT={result.returncode}", flush=True)
        return text

    build("prepare")
    suffix = ".exe" if os.name == "nt" else ""
    source = ROOT / f"src/Compiler/Koh.Build.Tasks/bin/Release/net10.0/koh-link{suffix}"
    tool = directory / f"native linker{suffix}"
    shutil.copy2(source, tool)
    output = directory / "ROM output" / "game.gb"

    def properties(executable=tool, target=output, color=False, debug=True):
        return [f"-p:KohLinkExecutable={executable}", f"-p:KohRomPath={target}",
                f"-p:KohCgbCompatible={str(color).lower()}", f"-p:KohEmitDebugInfo={str(debug).lower()}"]

    build("baseline", properties())
    assert output.read_bytes()[0x143] == 0 and output.with_suffix(".kdbg").is_file()
    before = output.stat().st_mtime_ns
    text = build("unchanged", properties())
    assert output.stat().st_mtime_ns == before and f"Koh: built {output}" not in text
    build("cgb-property", properties(color=True))
    expected = output.read_bytes()
    assert expected[0x143] == 0x80 and output.stat().st_mtime_ns != before
    before = output.stat().st_mtime_ns
    time.sleep(1.1)
    os.utime(tool, None)
    build("tool-timestamp", properties(color=True))
    assert output.stat().st_mtime_ns != before and output.read_bytes() == expected
    second = directory / f"second native linker{suffix}"
    shutil.copy2(source, second)
    before = output.stat().st_mtime_ns
    assert second.stat().st_mtime_ns < before
    build("tool-selection", properties(executable=second, color=True))
    assert output.stat().st_mtime_ns != before and output.read_bytes() == expected
    no_debug = directory / "no debug output" / "game.gb"
    build("debug-disabled", properties(executable=second, target=no_debug, debug=False))
    assert no_debug.is_file() and not no_debug.with_suffix(".kdbg").exists()
    failing = directory / f"failing native linker{suffix}"
    if os.name != "nt":
        failing.write_text("#!/bin/sh\ncat >/dev/null\nexit 7\n", encoding="utf-8")
        failing.chmod(0o755)
    before = hashlib.sha256(output.read_bytes()).hexdigest()
    text = build("failed-link", properties(executable=failing, color=True), success=False)
    assert "error" in text.lower() and hashlib.sha256(output.read_bytes()).hexdigest() == before
    if os.name != "nt":
        assert "exited 7" in text
    else:
        assert str(failing) in text
    build("restore-defaults")
    print(f"PASS SDK verification; artifacts: {directory}", flush=True)


if __name__ == "__main__":
    main()
