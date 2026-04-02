# KOH vs RGBDS Benchmark Suite

## Goal

Two complementary benchmarking tools:

1. **Head-to-head comparison** of KOH vs RGBDS on pokecrystal, measuring wall-clock time, CPU time (user+sys), and peak RSS for assembly and linking. Correctness is validated via byte-for-byte equivalence of the post-fix ROM (both pre-fix ROMs are processed with identical `rgbfix` flags before comparison). The head-to-head benchmark measures **end-user command execution cost**, including process startup, runtime initialization, and all overhead inherent to invoking the tool as a CLI process.
2. **Internal microbenchmarks** of specific KOH pipeline phases for performance regression tracking. The benchmarked phases are: lex+parse (coupled, cannot be separated), bind+emit+include-resolution (coupled, includes disk I/O for transitive INCLUDE files), and full pipeline. The suite does not currently isolate transitive parse cost across the include graph — that gap is documented below.

---

## Reference Project

**pokecrystal** — a complete disassembly of Pokemon Crystal (GBC). A large, multi-file RGBDS project with heavy use of macros, includes, and data directives. (At time of writing: ~2400 `.asm` files, ~117k total lines — these counts are context for understanding scale, not contract terms.)

- Repository: `https://github.com/pret/pokecrystal`
- Pinned commit: recorded in `benchmarks/benchmark.sh` as `POKECRYSTAL_REF`. Selected during implementation, must not be a floating reference.
- Requires RGBDS >= 1.0.0 (enforced by `rgbdscheck.asm` in the pokecrystal repo)
- Never stored in the KOH repo — cloned at benchmark time

### Pokecrystal Build Graph

Pokecrystal's real build has five stages:

1. **Custom C tools** (setup, not measured) — `make -C tools/` builds `scan_includes`, `lzcomp`, `pokemon_animation_graphics`, etc. from C source.
2. **Graphics generation** (setup, not measured) — `.png` files are converted to `.2bpp`, `.lz`, `.gbcpal`, `.tilemap` via the tools and `rgbgfx`. Many `INCBIN` directives reference these generated files.
3. **Assembly** (measured) — 17 top-level `.asm` files, each assembled individually. Each invocation preincludes `includes.asm` (constants and macros shared across all files) via `-P`. Each file pulls in a deep include tree; the largest (`main.asm`) has hundreds of transitive includes.
4. **Linking** (measured) — all 17 `.o` files linked into a single `.gbc` ROM using a linker script (`layout.link`).
5. **Header fixup** (not measured, but applied to both ROMs before comparison) — `rgbfix` patches the ROM header with title, checksums, cartridge type, etc.

Only stages 3 and 4 are timed. Stages 1, 2, and 5 are setup/post-processing.

### ROM Comparison and `rgbfix`

The comparison must compare ROMs in the **same state**. Since `rgbfix` is an RGBDS-specific post-processing tool:

- Both `rgblink` and `koh-link` produce a pre-fix ROM.
- `rgbfix` is applied to **both** pre-fix ROMs with identical flags.
- The byte-for-byte comparison is performed on the **post-fix ROMs**.
- If `rgbfix` rejects KOH's pre-fix ROM (e.g., unexpected size or structure), that is classified as `rom_postprocess_failure` — distinct from a link failure or ROM content mismatch. If `rgbfix` rejects RGBDS's ROM, that is classified as `rgbds_failure` (unexpected, indicates broken setup).

### KOH CLI Feature Gaps

KOH's current CLI does not support the following RGBDS flags used by pokecrystal:

| Flag | Purpose | Impact | Gap Type |
|------|---------|--------|----------|
| `-P <file>` | Preinclude file | Required — every file depends on `includes.asm` | CLI |
| `-Q <value>` | Fill byte for padding | Affects ROM content | CLI |
| `-D <symbol>` | Define symbol from CLI | Used for variant builds, not needed for default `pokecrystal.gbc` | CLI |
| `-W <warning>` | Warning control | Does not affect output, only diagnostics | CLI |
| `-l <linkerscript>` | Linker script | Required for correct section placement | CLI |
| `-m <mapfile>` | Map file output | Does not affect ROM output | CLI |

The benchmark does not invent workarounds for missing flags. If KOH lacks a required flag, the benchmark reports the specific failure category (see Failure Taxonomy below).

---

## Component 1: Head-to-Head Benchmark

### Files

- `benchmarks/Dockerfile` — runner image definition
- `benchmarks/benchmark.sh` — host-side orchestrator
- `benchmarks/run-benchmark.sh` — container-side benchmark harness
- `benchmarks/results/` — output directory (gitignored)

### Makefile Target

```makefile
benchmark:
	bash benchmarks/benchmark.sh
```

### Docker Image Design

A **stable runner image** containing the runtime environment and harness. KOH binaries are **bind-mounted** at run time, not baked into the image.

**Image contents** (`benchmarks/Dockerfile`):

- **Base:** Ubuntu 24.04
- **RGBDS 1.0.1:** built from source. This is the chosen benchmark version. It satisfies pokecrystal's >= 1.0.0 requirement and matches KOH's existing compat test Dockerfile. Built from the `v1.0.1` tag at `https://github.com/gbdev/rgbds`.
- **.NET 10 runtime:** installed via Microsoft package feed. Required to run framework-dependent KOH binaries.
- **GNU time** (`/usr/bin/time`): for measurements
- **gcc, make, libpng-dev:** for building pokecrystal's custom C tools during setup
- **git:** for cloning pokecrystal
- `benchmarks/run-benchmark.sh` copied into the image

**What is NOT in the image:**
- KOH binaries — bind-mounted from host `dotnet publish` output
- pokecrystal source — cloned inside the container during setup

**Image rebuild policy:** The script always runs `docker build` and relies on Docker's layer cache to skip unchanged layers. No custom content-hash logic — Docker's built-in caching handles this correctly because it considers the full build context (Dockerfile, copied files, build args), not just the Dockerfile.

### Script Flow

`benchmarks/benchmark.sh` (host-side):

1. **Prerequisite check** — verify `docker` and `dotnet` are available. Print exact versions of both. Exit immediately with a clear error if either is missing.
2. **Publish KOH** — `dotnet publish` both `Koh.Asm` and `Koh.Link` for `linux-x64`, framework-dependent, into `benchmarks/.publish/`. Record the .NET SDK version used in the publish output.
3. **Build image** — `docker build -t $BENCH_IMAGE benchmarks/`. Docker layer cache handles skip-if-unchanged.
4. **Run container** — `docker run` with:
   - KOH binaries bind-mounted from `benchmarks/.publish/` to `/opt/koh/`
   - Optional `--cpuset-cpus` if `BENCH_CPUSET` is set
   - Environment variables passed through for configuration
   - Output directory bind-mounted to `benchmarks/results/`
5. **Print summary** — cat `benchmarks/results/results.md` to console.

`benchmarks/run-benchmark.sh` (container-side):

1. **Record environment metadata:**
   - CPU model (from `/proc/cpuinfo`, first `model name` line)
   - Kernel version (`uname -r`)
   - Container OS (from `/etc/os-release`, `PRETTY_NAME` field)
   - Available RAM (`awk '/MemTotal/ {print $2}' /proc/meminfo`)
   - .NET runtime version (`dotnet --version`)
   - RGBDS version (`rgbasm --version`, first line)
   - KOH version (`koh-asm --version`, first line)
2. **Clone pokecrystal** — `git clone --depth 1` + `git checkout` at pinned `POKECRYSTAL_REF`.
3. **Build prerequisites** — build C tools, generate graphics assets. This is setup, not measured.
4. **Pre-flight classification** — before any measured runs, determine which phases are supported using a **hardcoded capability matrix** in the harness script. The matrix lists required output-affecting CLI features per phase:
   - **KOH assembly requires:** `-P` (preinclude) AND `-Q` (fill byte). Both are required — assembly is benchmarkable only when ALL required features are present.
   - **KOH linking requires:** `-l` (linker script). Without it, a fair comparison cannot be defined.
   - The harness checks the matrix against a hardcoded list of KOH's known supported features. This list is updated manually when KOH implements new flags. `--help` output may be used as a secondary diagnostic logged for auditing, but is not the source of truth for classification.
   - If all required features for a phase are supported, the phase is classified as benchmarkable. Otherwise:
     - `unsupported_cli_feature`: used when the phase is conceptually benchmarkable and the only blocker is missing CLI surface (e.g., KOH assembly without `-P`/`-Q`).
     - `unsupported_benchmark`: used when a fair head-to-head comparison cannot be defined at all (e.g., KOH linking without linker script support — even with correct object files, the output would not be comparable without equivalent section placement).
   - Record pre-flight classifications in `$BENCH_OUTPUT/preflight.json`. This artifact includes: the hardcoded capability matrix, required features per phase, resulting classifications, and optionally captured `--help` text snippets for auditing. Having pre-flight results in a dedicated file makes support-state changes easy to review over time.
   - **Future improvement:** when KOH adds a machine-readable capability command (e.g., `koh-asm --capabilities`), the harness should consume that instead of a hardcoded matrix.
5. **Warmup runs** — `BENCH_WARMUP` iterations (default: 2) of each supported phase. Results discarded. Warmup uses the same command structure, working directory, output cleanup, and stderr/stdout redirection as measured runs — the only difference is that timing results are not recorded. Warmup primes filesystem page cache for source files and binary assets. For KOH (.NET), warmup also exercises the runtime's startup path, but JIT state does not persist across process invocations — each `koh-asm` invocation starts cold from a JIT perspective. Unsupported phases are not warmed up.
6. **Measured runs** — for each of `BENCH_RUNS` iterations (default: 5):
   - Clean output files (`.o`, `.koh.o`, `.gbc`) between runs. Source tree and generated assets are not touched.
   - Run each supported phase wrapped in `/usr/bin/time -v`. See Exact Commands below.
   - Unsupported phases are not invoked — they are recorded with `"status": "unsupported"` in raw results.
   - Record: wall time, user CPU time, system CPU time, peak RSS, exit code, exact command.
7. **ROM comparison** — performed once after all measured runs complete, using the ROMs from the **final measured run** in which both toolchains succeeded. If no such run exists, comparison is skipped.
   - Apply `rgbfix` with identical flags to both pre-fix ROMs. If `rgbfix` rejects KOH's ROM, classify as `rom_postprocess_failure`.
   - SHA256 both post-fix ROMs
   - On mismatch: file sizes, first differing byte offset, header hex dump (0x0000–0x014F) of both
8. **Generate reports** — write `results.md`, `raw.json`, preserve all logs.

### Exact Commands

**Assembly timing wrapper** — a shell script function that loops over all 17 files, identical structure for both tools:

```bash
# Timed block for RGBDS assembly (wrapped in /usr/bin/time -v)
for asm in audio home main ram data/text/common data/maps/map_data \
           data/pokemon/dex_entries data/pokemon/egg_moves data/pokemon/evos_attacks \
           engine/movie/credits engine/overworld/events gfx/misc gfx/pics \
           gfx/sprites gfx/tilesets lib/mobile/main lib/mobile/mail; do
    rgbasm -Q8 -P includes.asm -Weverything -Wtruncation=1 -o "${asm}.o" "${asm}.asm"
done
```

```bash
# Timed block for KOH assembly — attempted command (wrapped in /usr/bin/time -v)
# This command is structurally identical but uses koh-asm.
# It will fail until KOH implements -P and -Q flags.
for asm in audio home main ram data/text/common data/maps/map_data \
           data/pokemon/dex_entries data/pokemon/egg_moves data/pokemon/evos_attacks \
           engine/movie/credits engine/overworld/events gfx/misc gfx/pics \
           gfx/sprites gfx/tilesets lib/mobile/main lib/mobile/mail; do
    /opt/koh/koh-asm "${asm}.asm" -o "${asm}.koh.o" -f rgbds
done
```

Both blocks use the same loop structure, same iteration order, same stderr/stdout redirection. The only difference is the assembler binary and its arguments. The loop runs with `set -e` (or explicit exit-code checking after each invocation) so that the first assembler error terminates the phase immediately — partial success is not timed as if it were a complete run.

**Stdout/stderr capture:** each phase's combined stdout, stderr, and `/usr/bin/time -v` output are captured to a single log file per run (e.g., `logs/run-1-rgbds-assembly.log`). The `time -v` output goes to stderr, which is already part of the combined capture.

**Note:** Per the pre-flight classification (step 4), the KOH assembly block is not executed for measured runs if the required CLI features are known to be missing. The command above documents what the block would look like once support lands. **When KOH implements `-P` and `-Q`, the benchmark command must be updated to pass those actual flags** (e.g., `-P includes.asm -Q8`) — the placeholder command shown today is not the final semantically equivalent invocation.

**Linking commands:**

```bash
# RGBDS linking (wrapped in /usr/bin/time -v)
rgblink -Weverything -Wtruncation=1 -l layout.link \
    -n pokecrystal.sym -m pokecrystal.map -o pokecrystal.gbc \
    audio.o home.o main.o ram.o data/text/common.o data/maps/map_data.o \
    data/pokemon/dex_entries.o data/pokemon/egg_moves.o data/pokemon/evos_attacks.o \
    engine/movie/credits.o engine/overworld/events.o gfx/misc.o gfx/pics.o \
    gfx/sprites.o gfx/tilesets.o lib/mobile/main.o lib/mobile/mail.o
```

```bash
# KOH linking — attempted command (wrapped in /usr/bin/time -v)
# Not executed until koh-link supports -l (linker script).
# When implemented, this command must be updated to pass -l layout.link and any other
# required flags for semantically equivalent section placement.
/opt/koh/koh-link -o pokecrystal.koh.gbc \
    audio.koh.o home.koh.o main.koh.o ram.koh.o data/text/common.koh.o \
    data/maps/map_data.koh.o data/pokemon/dex_entries.koh.o data/pokemon/egg_moves.koh.o \
    data/pokemon/evos_attacks.koh.o engine/movie/credits.koh.o engine/overworld/events.koh.o \
    gfx/misc.koh.o gfx/pics.koh.o gfx/sprites.koh.o gfx/tilesets.koh.o \
    lib/mobile/main.koh.o lib/mobile/mail.koh.o
```

**ROM fixup** (applied to both ROMs if both exist, not timed):

```bash
rgbfix -Cjv -t PM_CRYSTAL -k 01 -l 0x33 -m MBC3+TIMER+RAM+BATTERY -r 3 -p 0 -i BYTE -n 0 pokecrystal.gbc
rgbfix -Cjv -t PM_CRYSTAL -k 01 -l 0x33 -m MBC3+TIMER+RAM+BATTERY -r 3 -p 0 -i BYTE -n 0 pokecrystal.koh.gbc
```

### Phase Definitions

**Assembly phase** = invoking the assembler CLI 17 times to produce 17 object files from the project's source tree, using the project's real build flags. Timed as a single batch. Includes process startup/shutdown overhead for each of the 17 invocations — this is intentional, as it reflects the real developer experience.

**Linking phase** = invoking the linker CLI once to combine all 17 object files into a single `.gbc` ROM. Includes process startup overhead.

Both tools consume the **same source tree, unmodified**. The benchmark never rewrites sources or synthesizes compatibility shims. If KOH cannot consume the project exactly as-is with equivalent CLI flags, the phase remains unsupported.

**Note on per-file invocation:** this benchmark measures compatibility with pokecrystal's current per-file assembly workflow (17 separate assembler invocations). It does not measure the best possible throughput KOH could achieve with a different batch-oriented or multi-file driver model. The numbers reflect the cost of drop-in RGBDS replacement for this project's existing build system.

### Cache and Environment Policy

**Cache policy: warm.** Warmup runs prime the filesystem page cache for source files and binary assets. Measured runs benefit from cached reads. This is explicitly stated in every report.

Rationale: cold-cache benchmarks require `echo 3 > /proc/sys/vm/drop_caches` (needs `--privileged` Docker) and add variance from I/O subsystem. Warm-cache better represents the developer experience of repeated builds.

**CPU pinning:** optional. If `BENCH_CPUSET` is set (e.g., `BENCH_CPUSET=0,1`), the container runs with `--cpuset-cpus=$BENCH_CPUSET`. If not set, the report states "CPU pinning: none".

### Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `BENCH_RUNS` | `5` | Number of measured runs per phase |
| `BENCH_WARMUP` | `2` | Number of warmup runs (results discarded) |
| `POKECRYSTAL_REF` | pinned in script | Commit SHA to clone |
| `BENCH_CPUSET` | unset | Docker `--cpuset-cpus` value |
| `BENCH_STRICT` | `0` | Exit code behavior (see Failure Handling) |
| `BENCH_COVERAGE` | `0` | If `1`, exit non-zero when any phase is unsupported (see Exit Codes) |
| `BENCH_OUTPUT` | `benchmarks/results` | Output directory |
| `BENCH_IMAGE` | `koh-benchmark` | Docker image tag |

### Measurements

Captured via GNU `/usr/bin/time -v` for each phase, each run:

- **Wall-clock time** — elapsed real time
- **User CPU time** — user-mode CPU seconds
- **System CPU time** — kernel-mode CPU seconds
- **CPU time** — derived: user + system (computed per-run, then medians computed from these per-run sums — not a sum of medians)
- **Peak RSS** — maximum resident set size (KB)
- **Exit code** — tool exit code

Only runs where the tool exited 0 contribute to aggregate statistics.

### Statistics

- **Median** — computed independently for wall time, CPU time, and peak RSS. When N is even, median is the mean of the two middle values.
- **Min/Max** — reported alongside median for all metrics.
- **Successful run count** — reported per phase per tool (e.g., `5/5` or `3/5`).
- **Speedup** — wall-time speedup = RGBDS median / KOH median. **Shown only when both tools succeeded in all measured runs for that phase.** Otherwise the speedup column shows `—`.

### Output Format

#### `results.md` — Human-Readable Summary

The sample below reflects the **expected current state** where KOH assembly and linking are both unsupported on pokecrystal due to missing CLI features:

```markdown
## KOH vs RGBDS Benchmark — pokecrystal @ <commit-sha>

### Environment
- CPU: AMD Ryzen 7 5800X
- Kernel: 6.1.0-amd64
- Container OS: Ubuntu 24.04.1 LTS
- RAM: 32768 MB
- .NET Runtime: 10.0.0
- .NET SDK (host publish): 10.0.100
- RGBDS: 1.0.1
- KOH: 0.1.0
- Cache policy: warm
- CPU pinning: none

### Configuration
- Measured runs: 5
- Warmup runs: 2

### Results

Note: CPU (median) = median of per-run (user + sys) values.

| Phase    | Tool  | Status      | Wall (median) | CPU (median) | Peak RSS (median) | Min/Max Wall | Runs OK | Speedup |
|----------|-------|-------------|---------------|-------------|-------------------|-------------|---------|---------|
| Assembly | RGBDS | ok          | 1.23s         | 1.18s       | 42 MB             | 1.21–1.26s  | 5/5     | —       |
| Assembly | KOH   | unsupported | —             | —           | —                 | —           | —       | —       |
| Linking  | RGBDS | ok          | 0.45s         | 0.41s       | 28 MB             | 0.44–0.47s  | 5/5     | —       |
| Linking  | KOH   | unsupported | —             | —           | —                 | —           | —       | —       |

### Phase Preconditions

| Phase    | Tool | Requires           | Supported |
|----------|------|--------------------|-----------|
| Assembly | KOH  | -P (preinclude)    | no        |
| Assembly | KOH  | -Q (fill byte)     | no        |
| Linking  | KOH  | -l (linker script) | no        |

### ROM Comparison
Status: SKIP (KOH did not produce a ROM)

### Failure Details
- KOH assembly: unsupported_cli_feature — missing `-P` (preinclude) and `-Q` (fill byte) CLI flags
- KOH linking: unsupported_benchmark — missing `-l` (linker script) support; phase not attempted
- See logs/ for full stderr/stdout

### Raw Data
See raw.json for per-run measurements.
```

#### `raw.json` — Machine-Readable Data

**Schema rules for phase entries:**
- `status` (required): one of `"ok"`, `"failed"`, `"unsupported"`, `"skipped"`
  - `ok` = phase executed and tool exited 0
  - `failed` = phase executed and tool exited non-zero
  - `unsupported` = pre-flight determined phase is not comparable; never attempted
  - `skipped` = phase was benchmarkable, but not attempted on this run because a prerequisite phase failed (e.g., KOH linking skipped because KOH assembly failed — no objects to link)
- `classification` (required when status is `"unsupported"`, `"failed"`, or `"skipped"`): failure taxonomy category
- `reason` (required when status is not `"ok"`): human-readable explanation
- Timing metrics (`wall_s`, `user_cpu_s`, etc.) are present only when `status` is `"ok"` or `"failed"`
- `measurement_tool` and `phase_command`: present for all executed phases (`"ok"` or `"failed"` status only)

```json
{
  "schema_version": 1,
  "timestamp": "2026-04-02T14:30:00Z",
  "environment": {
    "cpu_model": "AMD Ryzen 7 5800X",
    "kernel": "6.1.0-amd64",
    "container_os": "Ubuntu 24.04.1 LTS",
    "ram_mb": 32768,
    "dotnet_runtime": "10.0.0",
    "dotnet_sdk_host": "10.0.100",
    "rgbds_version": "1.0.1",
    "koh_version": "0.1.0"
  },
  "config": {
    "runs": 5,
    "warmup": 2,
    "cache_policy": "warm",
    "cpu_pinning": null,
    "pokecrystal_ref": "abc1234..."
  },
  "phase_preconditions": {
    "koh_assembly": {
      "requires": ["-P (preinclude)", "-Q (fill byte)"],
      "supported": false,
      "classification": "unsupported_cli_feature"
    },
    "koh_linking": {
      "requires": ["-l (linker script)"],
      "supported": false,
      "classification": "unsupported_benchmark"
    }
  },
  "runs": [
    {
      "run": 1,
      "rgbds_assembly": {
        "status": "ok",
        "measurement_tool": "/usr/bin/time -v",
        "phase_command": {
          "wrapper": "/usr/bin/time -v bash -c '...'",
          "per_file_template": "rgbasm -Q8 -P includes.asm -Weverything -Wtruncation=1 -o ${asm}.o ${asm}.asm",
          "files": ["audio", "home", "main", "ram", "data/text/common", "data/maps/map_data", "data/pokemon/dex_entries", "data/pokemon/egg_moves", "data/pokemon/evos_attacks", "engine/movie/credits", "engine/overworld/events", "gfx/misc", "gfx/pics", "gfx/sprites", "gfx/tilesets", "lib/mobile/main", "lib/mobile/mail"]
        },
        "wall_s": 1.25,
        "user_cpu_s": 1.10,
        "sys_cpu_s": 0.10,
        "cpu_s": 1.20,
        "peak_rss_kb": 43008,
        "exit_code": 0,
        "contributed_to_aggregates": true,
        "log_path": "logs/run-1-rgbds-assembly.log"
      },
      "koh_assembly": {
        "status": "unsupported",
        "classification": "unsupported_cli_feature",
        "reason": "missing -P (preinclude) and -Q (fill byte) CLI flags"
      },
      "rgbds_linking": {
        "status": "ok",
        "measurement_tool": "/usr/bin/time -v",
        "phase_command": {
          "argv": ["rgblink", "-Weverything", "-Wtruncation=1", "-l", "layout.link", "-n", "pokecrystal.sym", "-m", "pokecrystal.map", "-o", "pokecrystal.gbc", "audio.o", "home.o", "main.o", "ram.o", "data/text/common.o", "data/maps/map_data.o", "data/pokemon/dex_entries.o", "data/pokemon/egg_moves.o", "data/pokemon/evos_attacks.o", "engine/movie/credits.o", "engine/overworld/events.o", "gfx/misc.o", "gfx/pics.o", "gfx/sprites.o", "gfx/tilesets.o", "lib/mobile/main.o", "lib/mobile/mail.o"]
        },
        "wall_s": 0.45,
        "user_cpu_s": 0.38,
        "sys_cpu_s": 0.03,
        "cpu_s": 0.41,
        "peak_rss_kb": 28672,
        "exit_code": 0,
        "contributed_to_aggregates": true,
        "log_path": "logs/run-1-rgbds-linking.log"
      },
      "koh_linking": {
        "status": "unsupported",
        "classification": "unsupported_benchmark",
        "reason": "missing -l (linker script) support; fair comparison not possible"
      }
    }
  ],
  "aggregates": {
    "rgbds_assembly": {
      "wall_median_s": 1.23, "wall_min_s": 1.21, "wall_max_s": 1.26,
      "cpu_median_s": 1.18,
      "peak_rss_median_kb": 43008,
      "success_count": 5, "executed_count": 5, "planned_count": 5
    },
    "koh_assembly": {
      "status": "unsupported",
      "classification": "unsupported_cli_feature",
      "success_count": 0,
      "executed_count": 0,
      "planned_count": 5
    },
    "rgbds_linking": {
      "wall_median_s": 0.45, "wall_min_s": 0.44, "wall_max_s": 0.47,
      "cpu_median_s": 0.41,
      "peak_rss_median_kb": 28672,
      "success_count": 5, "executed_count": 5, "planned_count": 5
    },
    "koh_linking": {
      "status": "unsupported",
      "classification": "unsupported_benchmark",
      "success_count": 0,
      "executed_count": 0,
      "planned_count": 5
    },
    "speedup_assembly": null,
    "speedup_linking": null
  },
  "rom_postprocess": {
    "rgbfix_command": ["rgbfix", "-Cjv", "-t", "PM_CRYSTAL", "-k", "01", "-l", "0x33", "-m", "MBC3+TIMER+RAM+BATTERY", "-r", "3", "-p", "0", "-i", "BYTE", "-n", "0"],
    "rgbds_exit_code": 0,
    "koh_exit_code": null,
    "rgbds_log_path": "logs/rgbfix-rgbds.log",
    "koh_log_path": null
  },
  "rom_comparison": {
    "status": "skip",
    "reason": "KOH did not produce a ROM",
    "rgbds_sha256": "abcdef1234...",
    "koh_sha256": null,
    "rgbds_size_bytes": 2097152,
    "koh_size_bytes": null,
    "first_diff_offset": null,
    "rgbds_header_hex": "00C350...",
    "koh_header_hex": null
  }
}
```

### Failure Taxonomy

Failures are categorized precisely. Each category has distinct meaning:

| Category | Meaning | Example |
|----------|---------|---------|
| `harness_failure` | Benchmark infrastructure broke | Docker unavailable, clone failed, prerequisites missing |
| `unsupported_cli_feature` | KOH lacks a required CLI flag | Missing `-P` preinclude, missing `-Q` fill byte |
| `unsupported_language_feature` | KOH lacks a required language/semantic capability | Cannot process a directive or syntax that RGBDS supports |
| `unsupported_benchmark` | The phase cannot be attempted fairly due to missing infrastructure | KOH linking without linker script support |
| `assembly_failure` | Tool attempted assembly and produced errors | Parse errors, undefined symbols after INCLUDE resolution |
| `link_failure` | Tool attempted linking and failed | Unresolved symbols, section overlap |
| `rom_postprocess_failure` | `rgbfix` rejected KOH's ROM output | Unexpected ROM size, malformed header (if `rgbfix` rejects RGBDS output, that is `rgbds_failure` instead) |
| `rom_mismatch` | Both tools produced ROMs that passed `rgbfix`, but final bytes differ | Different instruction encoding, wrong section placement |
| `rgbds_failure` | RGBDS itself failed (unexpected, indicates broken setup) | Missing RGBDS binary, incompatible version |

**Key distinctions:**
- `unsupported_cli_feature` vs `unsupported_language_feature`: the former means KOH's command-line interface doesn't expose a needed capability; the latter means KOH's assembler/linker core cannot process required source constructs even with correct arguments. This distinction matters because fixing a CLI gap is trivial while fixing a language gap may require significant compiler work.
- `unsupported_*` vs `*_failure`: unsupported means the benchmark determined **before running** that the phase is not comparable. Failure means the tool was invoked and produced an error. Runtime `unsupported_language_feature` is only possible when pre-flight says the phase is benchmarkable and the tool is actually invoked — it cannot coexist with a pre-flight `unsupported_cli_feature` for the same phase.
- **Precedence:** pre-flight unsupported classifications take precedence over runtime failures. If pre-flight classifies a phase as unsupported, the phase is not attempted and no runtime failure can occur.

### Failure Handling and Exit Codes

**Log preservation:** full stderr and stdout for every executed phase of every run are saved to `$BENCH_OUTPUT/logs/`. Unsupported and skipped phases have no execution logs — their pre-flight classification and reason are recorded in `preflight.json` and `raw.json` instead.

**Exit code behavior:**

| Condition | `BENCH_STRICT=0` | `BENCH_STRICT=1` | `BENCH_COVERAGE=1` |
|-----------|-------------------|-------------------|---------------------|
| Harness failure | exit 2 | exit 2 | exit 2 |
| RGBDS failure | exit 2 | exit 2 | exit 2 |
| KOH assembly/link failure | exit 0 | exit 1 | exit 1 |
| KOH unsupported feature/benchmark | exit 0 | exit 0 | exit 3 |
| ROM mismatch (both ROMs produced) | exit 0 | exit 1 | exit 1 |
| ROM postprocess failure | exit 0 | exit 1 | exit 1 |
| Everything succeeded | exit 0 | exit 0 | exit 0 |

**`BENCH_STRICT`** controls whether KOH functional failures are treated as errors. Harness and RGBDS failures always fail (broken setup).

**`BENCH_COVERAGE`** is a separate switch for CI pipelines that want to enforce that all phases are supported. When `BENCH_COVERAGE=1`, unsupported phases cause exit 3 (distinct from exit 1 for functional failures and exit 2 for harness failures). This is not needed during development but becomes useful once KOH is expected to fully support the benchmark.

---

## Component 2: BenchmarkDotNet Microbenchmarks

### Goal

Track KOH's internal performance across the phases that can be isolated via its public API.

**Benchmarked phases and their honest names:**

1. **ParseBenchmarks** — `SyntaxTree.Parse(sourceText)`. Measures lexing and parsing combined. These are architecturally coupled: the `Parser` constructor calls `Lexer.LexAll()` internally. The `Parser` class is internal, and `LexAll()` is also internal. There is no public API to benchmark either in isolation.

   **Caveat: `ParseBenchmarks` measure parsing of the entry file text only; they are not a proxy for parsing the full transitive compilation unit.** For pokecrystal entry files, the top-level text is mostly INCLUDE directives — INCLUDE resolution happens later in the bind phase. The benchmark code and class-level doc comment must state this caveat. Benchmark method names must include `EntryFile` (e.g., `ParseEntryFileLarge`) to reduce the chance of misinterpretation when reading results without surrounding documentation.

2. **BindEmitWithIncludesBenchmarks** — `new Binder(options, fileResolver).Bind(tree)` on an already-parsed `SyntaxTree`. This phase performs:
   - INCLUDE file resolution via `AssemblyExpander` (reads included files from disk via `ISourceFileResolver.ReadAllText()`)
   - Parsing of each included file (via `SyntaxTree.Parse()` called internally by the expander)
   - Macro expansion, conditional assembly (IF/ENDC), repeat blocks (REPT/FOR)
   - Symbol collection and program counter tracking (Pass 1)
   - Instruction encoding to bytes, data directive evaluation (Pass 2)
   - Patch creation for forward references

   The `BindingResult` returned by `Bind()` contains fully assembled section bytes (`SectionBuffer.Bytes`), symbol tables, and patch lists — it is the complete assembled output, not just semantic analysis. The name includes "WithIncludes" because this benchmark **includes disk I/O for transitive INCLUDE file resolution**. After BDN warmup iterations, included files will be in the OS page cache, so the I/O cost is primarily syscall overhead rather than physical disk reads. This is stated explicitly to avoid misreading the numbers as pure CPU-bound compiler core throughput.

3. **FullPipelineBenchmarks** — `Compilation.Create(tree).Emit()`. Measures lex+parse+bind+emit+freeze as a single operation. `Emit()` internally calls `Binder.Bind()` and then `EmitModel.FromBindingResult()` — a lightweight data freeze that converts live `SectionBuffer` and `SymbolTable` objects into frozen `SectionData[]` and `SymbolData[]` arrays. It does not write files or perform any work beyond the freeze.

**Coverage gap:** there is currently no benchmark that isolates transitive parse cost across the full include graph. The entry-file parse benchmark is fast and limited; the bind+emit benchmark includes parsing of included files but cannot separate parse cost from expansion/emission cost. Isolating transitive parse cost would require either exposing `AssemblyExpander` as a benchmarkable unit or adding a "parse all transitively included files" API. Neither exists today. This is acknowledged as a gap, not a TODO.

**Phases NOT benchmarked:**
- Lexing alone — `Lexer` exposes only `NextToken()` publicly. The batch `LexAll()` is internal. Benchmarking via `NextToken()` loop would measure iterator overhead, not representative lexer throughput.
- EmitModel construction — trivial data freeze from `BindingResult`, negligible.
- Object file writing (`RgbdsObjectWriter.Write()`) — I/O-bound serialization, not a meaningful CPU benchmark.

### Files

- `benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj`
- `benchmarks/Koh.Benchmarks/Program.cs`
- `benchmarks/Koh.Benchmarks/BenchmarkConfig.cs`
- `benchmarks/Koh.Benchmarks/ParseBenchmarks.cs`
- `benchmarks/Koh.Benchmarks/BindEmitWithIncludesBenchmarks.cs`
- `benchmarks/Koh.Benchmarks/FullPipelineBenchmarks.cs`

### Project Setup

- .NET 10 console app referencing `Koh.Core` and `Koh.Emit`
- BenchmarkDotNet package (version recorded in `Directory.Packages.props` alongside other dependencies; exact version captured in BDN's environment summary output)
- Run with `dotnet run -c Release --project benchmarks/Koh.Benchmarks`

### BenchmarkDotNet Configuration

Encoded in `BenchmarkConfig.cs`:

```csharp
public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default.WithRuntime(CoreRuntime.Core100));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddExporter(CsvExporter.Default);
        AddExporter(MarkdownExporter.GitHub);
        AddExporter(JsonExporter.Full);
    }
}
```

- **Job:** `Job.Default` with explicit `CoreRuntime.Core100` (.NET 10) runtime moniker. BDN achieves process-level isolation by generating, building, and executing a new console app per benchmark (its default toolchain behavior). `InProcess` is not used — it reduces isolation.
- **`MemoryDiagnoser`** for allocation tracking.
- **Exporters:** `CsvExporter`, `MarkdownExporter`, and `JsonExporter.Full` added explicitly in `BenchmarkConfig` to ensure stable machine-readable output. JSON is the primary format for automated regression comparison. Default exporters may change across BDN versions; pinning them ensures consistent artifact formats.
- **Tiered compilation / Dynamic PGO:** left at .NET runtime defaults. Recorded in BDN's environment summary.
- **Iteration count:** BDN's default auto-pilot (adaptive). No manual warmup/iteration overrides.
- **BenchmarkDotNet version** is recorded in BDN's environment summary header, tracked via the package version in `Directory.Packages.props`, and printed by `Program.cs` at startup (e.g., `Console.WriteLine($"BenchmarkDotNet {typeof(BenchmarkRunner).Assembly.GetName().Version}")`) to avoid depending solely on BDN's generated output format.

### Input Files and INCLUDE Resolution

Pokecrystal `.asm` files loaded from a local clone. Path configured via `POKECRYSTAL_PATH` environment variable.

**Behavior when `POKECRYSTAL_PATH` is not set:**
- Default (`BENCH_STRICT` unset or `0`): print a message explaining setup and exit cleanly.
- `BENCH_STRICT=1`: exit with non-zero code.

**Fixed input file set** (paths relative to pokecrystal root):

| Label | File | Rationale |
|-------|------|-----------|
| Small | `ram.asm` | Minimal — RAM declarations only, low include depth, no instruction encoding |
| Medium | `home.asm` | Moderate — core utility routines, medium include fan-out, mix of code and data |
| Large | `main.asm` | Heaviest — deepest include tree, most macro expansion, most instruction encoding |

These paths are constants in the benchmark code. Input file selection is based on include-tree complexity (file resolution count, macro expansion volume, instruction encoding volume), not top-level line count. Approximate transitive line counts at time of selection: `ram.asm` ~4.7k, `home.asm` ~12.5k, `main.asm` ~190k — these are rationale context for understanding why these files were chosen, not verified contract values. Do not use these numbers in assertions, tests, or benchmark validation logic.

### Benchmark Method Design

Each benchmark method returns its result to prevent the JIT from eliding the work. If the result indicates failure (e.g., diagnostics with errors), the method throws, ensuring every iteration produces valid output:

```csharp
[Benchmark]
public SyntaxTree ParseEntryFileLarge() => SyntaxTree.Parse(_largeSourceText);

[Benchmark]
public BindingResult BindEmitLarge()
{
    var binder = new Binder(default, _fileResolver);
    var result = binder.Bind(_largeParsedTree);
    if (!result.Success) throw new InvalidOperationException("Bind failed");
    return result;
}

[Benchmark]
public EmitModel FullPipelineLarge()
{
    var model = Compilation.Create(_largeParsedTree).Emit();
    if (!model.Success) throw new InvalidOperationException("Pipeline failed");
    return model;
}
```

**Setup isolation and validation:**
- `[GlobalSetup]` loads file contents into `SourceText` objects and pre-parses `SyntaxTree` objects for bind benchmarks. Top-level file I/O is excluded from measurement.
- `[GlobalSetup]` also performs a **one-time validation run**: it calls `Bind()` / `Emit()` on each input using the same `Binder` options, `ISourceFileResolver`, and `Compilation` path as the actual benchmark methods, and verifies `Success == true`. This fails fast if the pinned input files are incompatible with the current compiler state, producing a clear error before any benchmarking begins.
- `BindEmitWithIncludesBenchmarks` creates a fresh `Binder` instance **inside the benchmark method**. Binder construction cost is included in the measurement — this is intentional because it represents the realistic invocation cost. The `Binder` constructor is lightweight (field initialization only); the real work happens in `Bind()`.
- `FullPipelineBenchmarks` calls `SyntaxTree.Parse()` + `Compilation.Create().Emit()` from `SourceText` — the full path.

**Per-iteration validation:** each benchmark method validates its output inline. If `Bind()` or `Emit()` returns a failure result, the method throws immediately. The `[GlobalSetup]` validation catches systematic failures early; the inline throws guard against intermittent failures during measurement.

### Output

Standard BenchmarkDotNet tables: mean, error, StdDev, allocated memory, plus BDN's environment summary (runtime version, CPU, OS, BenchmarkDotNet version). Written to `BenchmarkDotNet.Artifacts/` (gitignored).

---

## Repository Hygiene

### `.gitignore` additions

```
benchmarks/results/
benchmarks/.publish/
benchmarks/Koh.Benchmarks/BenchmarkDotNet.Artifacts/
```

The pokecrystal clone and generated assets live inside the Docker container's filesystem (for head-to-head) or in a user-specified external path (for microbenchmarks). Neither is under the KOH repo tree.

### Output Directory Structure

```
benchmarks/results/
  preflight.json              # pre-flight capability matrix and classifications
  results.md                  # human-readable summary
  raw.json                    # machine-readable per-run data
  logs/
    run-1-rgbds-assembly.log
    run-1-koh-assembly.log    # only if KOH assembly was executed
    run-1-rgbds-linking.log
    run-1-koh-linking.log     # only if KOH linking was executed
    run-2-rgbds-assembly.log
    rgbfix-rgbds.log          # only if rgbfix was applied
    rgbfix-koh.log            # only if rgbfix was applied to KOH ROM
    ...
```

The output directory is cleared at the start of each benchmark run.

---

## Scope Boundaries

**In scope:**
- Manual benchmark execution via `make benchmark` and `dotnet run`
- Structured output (markdown + JSON) suitable for future CI integration
- Linux container environment for head-to-head. Numbers are comparable only within that container environment, not across host OSes.

**Not in scope but CI-ready:**
- CI pipeline integration is not wired up, but the design supports it: configurable exit codes via `BENCH_STRICT` and `BENCH_COVERAGE`, machine-readable `raw.json` with stable schema, deterministic output paths, and prerequisite checks.

**Not in scope:**
- Historical trend tracking / graphs
- Benchmarking against other assemblers
- Cross-platform comparison
- Benchmark isolating transitive parse cost (documented gap — see Component 2 coverage gap)

---

## Version and Compatibility

- **.NET 10** — KOH targets `net10.0` (`Directory.Build.props`: `<TargetFramework>net10.0</TargetFramework>`). The Docker image installs the .NET 10 runtime; the host uses the .NET 10 SDK for publishing. Both versions are recorded in the report.
- **RGBDS 1.0.1** — chosen as the benchmark version because pokecrystal requires >= 1.0.0, and 1.0.1 is a stable release (January 1, 2025) that matches KOH's existing compat test Dockerfile. Built from the `v1.0.1` tag at `https://github.com/gbdev/rgbds`.
- **BenchmarkDotNet** — version managed in `Directory.Packages.props`. Exact version recorded in BDN's environment summary in every results file.
- **pokecrystal** — pinned to a specific commit. The benchmark script hardcodes the SHA, not a branch name.
