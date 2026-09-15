#!/usr/bin/env python3
"""Generate benchmark reports from raw run data.

Reads flat key=value text files produced by run-benchmark.sh and generates:
  - preflight.json
  - raw.json
  - results.md
  - rom-comparison.json
"""
import json
import os
import sys
from pathlib import Path
from statistics import median


def main():
    output_dir = Path(os.environ.get("BENCH_OUTPUT", "/results"))
    runs_count = int(os.environ.get("BENCH_RUNS", "5"))
    warmup_count = int(os.environ.get("BENCH_WARMUP", "2"))

    env = parse_kv_file(output_dir / "environment.txt")
    preflight_kv = parse_kv_file(output_dir / "preflight.txt")
    rom_kv = parse_kv_file(output_dir / "rom-data.txt") if (output_dir / "rom-data.txt").exists() else {}

    # --- Build preflight.json ---
    preflight = build_preflight(preflight_kv)
    write_json(output_dir / "preflight.json", preflight)

    # --- Build rom-comparison.json ---
    rom_comparison = build_rom_comparison(rom_kv)
    write_json(output_dir / "rom-comparison.json", rom_comparison)

    # --- Load per-run data ---
    run_data_dir = output_dir / "run-data"
    runs = []
    for run_num in range(1, runs_count + 1):
        run_file = run_data_dir / f"run-{run_num}.txt"
        if run_file.exists():
            runs.append(parse_run_file(run_file, run_num))
        else:
            runs.append({"run": run_num})

    # --- Compute aggregates ---
    phases = ["rgbds_assembly", "koh_assembly", "rgbds_linking", "koh_linking"]
    aggregates = {}
    for phase in phases:
        aggregates[phase] = compute_aggregate(phase, runs, runs_count)

    # Speedups
    for name, rgbds_key, koh_key in [
        ("assembly", "rgbds_assembly", "koh_assembly"),
        ("linking", "rgbds_linking", "koh_linking"),
    ]:
        rgbds_agg = aggregates[rgbds_key]
        koh_agg = aggregates[koh_key]
        speedup = None
        if (rgbds_agg.get("success_count", 0) == runs_count
                and koh_agg.get("success_count", 0) == runs_count
                and koh_agg.get("wall_median_s", 0) > 0):
            speedup = round(rgbds_agg["wall_median_s"] / koh_agg["wall_median_s"], 2)
        aggregates[f"speedup_{name}"] = speedup

    # --- Build raw.json ---
    config = {
        "runs": runs_count,
        "warmup": warmup_count,
        "cache_policy": "warm",
        "cpu_pinning": os.environ.get("BENCH_CPUSET") or None,
        "pokecrystal_ref": os.environ.get("POKECRYSTAL_REF", ""),
    }

    timestamp = ""
    if runs and "timestamp" in runs[0]:
        timestamp = runs[0]["timestamp"]

    raw = {
        "schema_version": 1,
        "timestamp": timestamp,
        "environment": coerce_env_types(env),
        "config": config,
        "phase_preconditions": preflight.get("capability_matrix", {}),
        "runs": runs,
        "aggregates": aggregates,
        "rom_postprocess": rom_comparison.get("postprocess", {}),
        "rom_comparison": {k: v for k, v in rom_comparison.items() if k != "postprocess"},
    }
    write_json(output_dir / "raw.json", raw)

    # --- Build results.md ---
    md = generate_markdown(
        coerce_env_types(env), config, aggregates, preflight,
        rom_comparison, runs_count, warmup_count,
    )
    (output_dir / "results.md").write_text(md)

    print(f"[report] Written preflight.json, raw.json, rom-comparison.json, results.md to {output_dir}")


# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------
def build_preflight(kv: dict) -> dict:
    return {
        "capability_matrix": {
            "koh_asm_supported_features": split_csv(kv.get("koh_asm_have", "")),
            "koh_link_supported_features": split_csv(kv.get("koh_link_have", "")),
            "koh_asm_required_features": split_csv(kv.get("koh_asm_required", "")),
            "koh_link_required_features": split_csv(kv.get("koh_link_required", "")),
            "koh_asm_supported": kv.get("koh_asm_supported") == "true",
            "koh_link_supported": kv.get("koh_link_supported") == "true",
            "koh_asm_classification": kv.get("koh_asm_classification", ""),
            "koh_link_classification": kv.get("koh_link_classification", ""),
        }
    }


# ---------------------------------------------------------------------------
# ROM comparison
# ---------------------------------------------------------------------------
def build_rom_comparison(kv: dict) -> dict:
    if not kv:
        return {"status": "skip", "reason": "KOH did not produce a ROM"}

    result = {
        "status": kv.get("status", "skip"),
        "reason": kv.get("reason") or None,
        "rgbds_sha256": kv.get("rgbds_sha256") or None,
        "koh_sha256": kv.get("koh_sha256") or None,
        "rgbds_size_bytes": safe_int(kv.get("rgbds_size")),
        "koh_size_bytes": safe_int(kv.get("koh_size")),
        "first_diff_offset": safe_int(kv.get("first_diff_offset")),
        "rgbds_header_hex": kv.get("rgbds_header_hex") or None,
        "koh_header_hex": kv.get("koh_header_hex") or None,
    }

    postprocess = {}
    if kv.get("rgbfix_command"):
        postprocess["rgbfix_command"] = kv["rgbfix_command"]
    if kv.get("rgbfix_rgbds_exit") is not None:
        postprocess["rgbds_exit_code"] = safe_int(kv["rgbfix_rgbds_exit"])
    if kv.get("rgbfix_koh_exit") is not None:
        postprocess["koh_exit_code"] = safe_int(kv["rgbfix_koh_exit"])
    for key in ("rgbfix_rgbds_log", "rgbfix_koh_log"):
        if kv.get(key):
            postprocess[key.replace("rgbfix_", "")] = kv[key]
    if postprocess:
        result["postprocess"] = postprocess

    return result


# ---------------------------------------------------------------------------
# Per-run data
# ---------------------------------------------------------------------------
PHASE_KEYS = ["rgbds_assembly", "koh_assembly", "rgbds_linking", "koh_linking"]

TIMING_FIELDS = [
    "wall_s", "user_cpu_s", "sys_cpu_s", "cpu_s", "peak_rss_kb",
    "exit_code",
]

PHASE_METADATA_FIELDS = [
    "status", "classification", "reason", "measurement_tool",
    "contributed_to_aggregates", "log_path",
    "phase_command", "files",
]


def parse_run_file(path: Path, run_num: int) -> dict:
    kv = parse_kv_file(path)
    result = {"run": run_num, "timestamp": kv.get("timestamp", "")}

    for phase in PHASE_KEYS:
        phase_data = {}
        prefix = f"{phase}."

        for key, val in kv.items():
            if not key.startswith(prefix):
                continue
            field = key[len(prefix):]

            if field in ("wall_s", "user_cpu_s", "sys_cpu_s", "cpu_s"):
                phase_data[field] = safe_float(val)
            elif field in ("peak_rss_kb", "exit_code"):
                phase_data[field] = safe_int(val)
            elif field == "contributed_to_aggregates":
                phase_data[field] = val == "true"
            elif field == "files":
                phase_data[field] = [f for f in val.split(",") if f]
            else:
                phase_data[field] = val

        # Build structured phase_command if template + files present
        if "phase_command" in phase_data and "files" in phase_data:
            phase_data["phase_command"] = {
                "per_file_template": phase_data.pop("phase_command"),
                "files": phase_data.pop("files"),
            }
        elif "phase_command" in phase_data:
            phase_data["phase_command"] = {"argv": phase_data["phase_command"]}

        if phase_data:
            result[phase] = phase_data

    return result


# ---------------------------------------------------------------------------
# Aggregates
# ---------------------------------------------------------------------------
def compute_aggregate(phase: str, runs: list, planned_count: int) -> dict:
    phase_runs = [r.get(phase, {}) for r in runs]
    ok_runs = [r for r in phase_runs if r.get("status") == "ok"]
    executed = [r for r in phase_runs if r.get("status") in ("ok", "failed")]
    unsupported = [r for r in phase_runs if r.get("status") == "unsupported"]
    skipped = [r for r in phase_runs if r.get("status") == "skipped"]

    if unsupported and len(unsupported) == len(phase_runs):
        return {
            "status": "unsupported",
            "classification": unsupported[0].get("classification", ""),
            "success_count": 0,
            "executed_count": 0,
            "planned_count": planned_count,
        }

    if not ok_runs:
        return {
            "status": "no_data",
            "success_count": 0,
            "executed_count": len(executed),
            "planned_count": planned_count,
        }

    walls = [r["wall_s"] for r in ok_runs if "wall_s" in r]
    cpus = [r["cpu_s"] for r in ok_runs if "cpu_s" in r]
    rsses = [r["peak_rss_kb"] for r in ok_runs if "peak_rss_kb" in r]

    status = "ok" if len(ok_runs) == planned_count else "partial"

    agg = {
        "status": status,
        "success_count": len(ok_runs),
        "executed_count": len(executed),
        "planned_count": planned_count,
    }

    if walls:
        agg["wall_median_s"] = round(median(walls), 4)
        agg["wall_min_s"] = round(min(walls), 4)
        agg["wall_max_s"] = round(max(walls), 4)
    if cpus:
        agg["cpu_median_s"] = round(median(cpus), 4)
    if rsses:
        agg["peak_rss_median_kb"] = round(median(rsses))

    return agg


# ---------------------------------------------------------------------------
# Markdown report
# ---------------------------------------------------------------------------
def generate_markdown(env, config, aggregates, preflight, rom_comp, runs_count, warmup_count):
    ref = config.get("pokecrystal_ref", "?")[:7]
    lines = [
        f"## KOH vs RGBDS Benchmark — pokecrystal @ {ref}",
        "",
        "### Environment",
        f"- CPU: {env.get('cpu_model', '?')}",
        f"- Kernel: {env.get('kernel', '?')}",
        f"- Container OS: {env.get('container_os', '?')}",
        f"- RAM: {env.get('ram_mb', '?')} MB",
        f"- .NET Runtime: {env.get('dotnet_runtime', '?')}",
        f"- .NET SDK (host publish): {env.get('dotnet_sdk_host', '?')}",
        f"- RGBDS: {env.get('rgbds_version', '?')}",
        f"- KOH: {env.get('koh_version', '?')}",
        "- Cache policy: warm",
        f"- CPU pinning: {config.get('cpu_pinning') or 'none'}",
        "",
        "### Configuration",
        f"- Measured runs: {runs_count}",
        f"- Warmup runs: {warmup_count}",
        "",
        "### Results",
        "",
        "Note: CPU (median) = median of per-run (user + sys) values.",
        "",
        "| Phase | Tool | Status | Wall (median) | CPU (median) | Peak RSS (median) | Min/Max Wall | Runs OK | Speedup |",
        "|-------|------|--------|---------------|-------------|-------------------|-------------|---------|---------|",
    ]

    for phase_name, rgbds_key, koh_key, speedup_key in [
        ("Assembly", "rgbds_assembly", "koh_assembly", "speedup_assembly"),
        ("Linking", "rgbds_linking", "koh_linking", "speedup_linking"),
    ]:
        for tool, key in [("RGBDS", rgbds_key), ("KOH", koh_key)]:
            agg = aggregates.get(key, {})
            status = agg.get("status", "no_data")

            if status in ("unsupported", "no_data"):
                lines.append(f"| {phase_name} | {tool} | {status} | — | — | — | — | — | — |")
            else:
                wall = fmt_s(agg.get("wall_median_s"))
                cpu = fmt_s(agg.get("cpu_median_s"))
                rss = fmt_kb(agg.get("peak_rss_median_kb"))
                w_min = agg.get("wall_min_s", "?")
                w_max = agg.get("wall_max_s", "?")
                ok = f"{agg.get('success_count', 0)}/{agg.get('planned_count', runs_count)}"
                sp_val = aggregates.get(speedup_key)
                speedup = f"{sp_val}x" if sp_val and tool == "KOH" else "—"
                status_label = "ok" if status == "ok" else f"partial ({agg.get('success_count', 0)}/{agg.get('planned_count', runs_count)})"
                lines.append(
                    f"| {phase_name} | {tool} | {status_label} | {wall} | {cpu} | {rss} | {w_min}–{w_max}s | {ok} | {speedup} |"
                )

    # Phase preconditions
    cap = preflight.get("capability_matrix", {})
    lines += ["", "### Phase Preconditions", "",
              "| Phase | Tool | Requires | Supported |",
              "|-------|------|----------|-----------|"]
    for req in cap.get("koh_asm_required_features", []):
        sup = "yes" if req in cap.get("koh_asm_supported_features", []) else "no"
        lines.append(f"| Assembly | KOH | {req} | {sup} |")
    for req in cap.get("koh_link_required_features", []):
        sup = "yes" if req in cap.get("koh_link_supported_features", []) else "no"
        lines.append(f"| Linking | KOH | {req} | {sup} |")

    # ROM comparison
    rom_status = rom_comp.get("status", "skip")
    rom_reason = rom_comp.get("reason")
    lines += ["", "### ROM Comparison"]

    if rom_status == "match":
        lines.append(f"Status: match")
        lines.append(f"SHA256: {rom_comp.get('rgbds_sha256', '?')}")
    elif rom_status == "mismatch":
        lines.append(f"Status: mismatch")
        lines.append(f"- RGBDS SHA256: {rom_comp.get('rgbds_sha256', '?')}")
        lines.append(f"- KOH SHA256: {rom_comp.get('koh_sha256', '?')}")
        lines.append(f"- RGBDS size: {rom_comp.get('rgbds_size_bytes', '?')} bytes")
        lines.append(f"- KOH size: {rom_comp.get('koh_size_bytes', '?')} bytes")
        if rom_comp.get("first_diff_offset"):
            lines.append(f"- First differing offset: {rom_comp['first_diff_offset']}")
    elif rom_status == "rom_postprocess_failure":
        lines.append(f"Status: rom_postprocess_failure")
        lines.append(f"- Reason: {rom_reason or 'rgbfix rejected KOH ROM'}")
    else:
        lines.append(f"Status: {rom_status}" + (f" ({rom_reason})" if rom_reason else ""))

    lines += ["", "### Raw Data", "See raw.json for per-run measurements.", ""]
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def parse_kv_file(path: Path) -> dict:
    result = {}
    if not path.exists():
        return result
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            continue
        key, _, val = line.partition("=")
        result[key.strip()] = val.strip()
    return result


def split_csv(s: str) -> list:
    return [x.strip() for x in s.split(",") if x.strip()] if s else []


def safe_float(s):
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def safe_int(s):
    try:
        return int(s)
    except (TypeError, ValueError):
        return None


def coerce_env_types(env: dict) -> dict:
    """Coerce known numeric fields in environment dict."""
    result = dict(env)
    if "ram_mb" in result:
        result["ram_mb"] = safe_int(result["ram_mb"]) or result["ram_mb"]
    return result


def fmt_s(val):
    if val is None:
        return "—"
    return f"{val}s"


def fmt_kb(val):
    if val is None:
        return "—"
    return f"{val // 1024} MB"


def write_json(path: Path, data):
    path.write_text(json.dumps(data, indent=2, default=str) + "\n")


if __name__ == "__main__":
    main()
