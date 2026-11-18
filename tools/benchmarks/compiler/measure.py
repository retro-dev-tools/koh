#!/usr/bin/env python3
import argparse
import json
from pathlib import Path
import shutil
import statistics
import subprocess
import sys
import tempfile
import time


def sample(command):
    with tempfile.TemporaryFile() as output, tempfile.TemporaryFile() as errors:
        start = time.perf_counter_ns()
        process = subprocess.run(
            ["zsh", "-f", "-c", 'TIMEFMT="KOH_MEASURE %M %U %S"; time "$@"',
             "koh-measure", *command], stdout=output, stderr=errors)
        elapsed_ms = (time.perf_counter_ns() - start) / 1_000_000
        output.seek(0)
        errors.seek(0)
        stdout = output.read().decode("utf-8", errors="replace")
        stderr = errors.read().decode("utf-8", errors="replace")
        if process.returncode:
            raise RuntimeError(f"Exit {process.returncode}: {command!r}\n{stdout}\n{stderr}")
        stderr, marker = stderr.rsplit("KOH_MEASURE ", 1)
        rss, user, system = marker.split()
        return {"elapsed_ms": elapsed_ms, "peak_rss_kib": int(rss),
                "user_s": float(user.removesuffix("s")),
                "system_s": float(system.removesuffix("s")),
                "stdout": stdout, "stderr": stderr}


def main():
    parser = argparse.ArgumentParser(description="Measure fresh compiler processes on a warm filesystem cache")
    parser.add_argument("--runs", type=int, default=9)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if sys.platform != "linux" or shutil.which("zsh") is None:
        parser.error("Peak RSS collection requires Linux and zsh; other hosts are not measured")
    if not command or not 1 <= args.runs <= 101:
        parser.error("Provide a compiler command and between 1 and 101 runs")
    if args.output.exists():
        parser.error(f"Output already exists: {args.output}")
    warmup = sample(command)
    samples = [sample(command) for _ in range(args.runs)]
    result = {"command": command, "platform": sys.platform, "warmup": warmup,
              "samples": samples,
              "median_elapsed_ms": statistics.median(s["elapsed_ms"] for s in samples),
              "max_peak_rss_kib": max(s["peak_rss_kib"] for s in samples)}
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2)
        output.write("\n")
    print(json.dumps({key: result[key] for key in ("median_elapsed_ms", "max_peak_rss_kib")}))


if __name__ == "__main__":
    main()
