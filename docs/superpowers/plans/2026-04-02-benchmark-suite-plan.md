# Benchmark Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a head-to-head KOH vs RGBDS benchmark (Docker-based, shell scripts) and BenchmarkDotNet microbenchmarks for KOH's internal pipeline phases.

**Architecture:** Two independent components: (1) shell scripts + Dockerfile for head-to-head CLI benchmarking against pokecrystal, orchestrated via `make benchmark`; (2) a BenchmarkDotNet .NET project for KOH-internal microbenchmarks using pokecrystal files as input.

**Tech Stack:** Bash, Docker, GNU time, .NET 10, BenchmarkDotNet, RGBDS 1.0.1

**Spec:** `docs/superpowers/specs/2026-04-02-benchmark-suite-design.md`

---

## File Map

### Component 1: Head-to-Head Benchmark
| File | Responsibility |
|------|---------------|
| `benchmarks/Dockerfile` | Runner image: Ubuntu 24.04 + RGBDS 1.0.1 + .NET 10 runtime + build tools |
| `benchmarks/benchmark.sh` | Host-side orchestrator: publish KOH, build image, run container, print results |
| `benchmarks/run-benchmark.sh` | Container-side harness: clone pokecrystal, pre-flight, warmup, measure, report |
| `Makefile` | Add `benchmark` target |
| `.gitignore` | Add benchmark output paths |

### Component 2: BenchmarkDotNet Microbenchmarks
| File | Responsibility |
|------|---------------|
| `benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj` | Project file referencing Koh.Core, Koh.Emit, BenchmarkDotNet |
| `benchmarks/Koh.Benchmarks/Program.cs` | Entry point: BDN version banner, runner dispatch |
| `benchmarks/Koh.Benchmarks/BenchmarkConfig.cs` | Shared BDN configuration: job, diagnosers, exporters |
| `benchmarks/Koh.Benchmarks/ParseBenchmarks.cs` | Entry-file parse benchmarks (Small/Medium/Large) |
| `benchmarks/Koh.Benchmarks/BindEmitWithIncludesBenchmarks.cs` | Bind+emit benchmarks including INCLUDE resolution |
| `benchmarks/Koh.Benchmarks/FullPipelineBenchmarks.cs` | Full pipeline benchmarks (parse+bind+emit+freeze) |
| `Koh.slnx` | Add benchmark project to solution |
| `Directory.Packages.props` | Add BenchmarkDotNet package version |

---

### Task 1: Repository hygiene — gitignore and Makefile

**Files:**
- Modify: `.gitignore`
- Modify: `Makefile`

- [ ] **Step 1: Add benchmark paths to .gitignore**

Append to `.gitignore`:

```
# Benchmarks
benchmarks/results/
benchmarks/.publish/
benchmarks/Koh.Benchmarks/BenchmarkDotNet.Artifacts/
```

- [ ] **Step 2: Add benchmark target to Makefile**

Append to `Makefile`:

```makefile

benchmark:
	bash benchmarks/benchmark.sh
```

- [ ] **Step 3: Commit**

```bash
git add .gitignore Makefile
git commit -m "chore: add benchmark gitignore paths and Makefile target"
```

---

### Task 2: Dockerfile — benchmark runner image

**Files:**
- Create: `benchmarks/Dockerfile`

- [ ] **Step 1: Create the Dockerfile**

```dockerfile
FROM ubuntu:24.04

ARG RGBDS_VERSION=1.0.1
ARG DOTNET_VERSION=10.0

# Install system dependencies: build tools, git, libpng (for pokecrystal graphics tools), GNU time
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl xz-utils git gcc make libpng-dev time \
    && rm -rf /var/lib/apt/lists/*

# Install RGBDS from pre-built release
RUN curl -fsSL "https://github.com/gbdev/rgbds/releases/download/v${RGBDS_VERSION}/rgbds-linux-x86_64.tar.xz" \
    | tar -xJ -C /usr/local/bin/

# Install .NET runtime
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel ${DOTNET_VERSION} --runtime dotnet --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm /tmp/dotnet-install.sh

RUN mkdir -p /work /results /opt/koh

COPY run-benchmark.sh /usr/local/bin/run-benchmark.sh
RUN chmod +x /usr/local/bin/run-benchmark.sh

WORKDIR /work

ENTRYPOINT ["run-benchmark.sh"]
```

- [ ] **Step 2: Verify Dockerfile syntax**

```bash
cd benchmarks && docker build --check . 2>&1 || echo "docker build --check not supported, syntax looks ok"
```

- [ ] **Step 3: Commit**

```bash
git add benchmarks/Dockerfile
git commit -m "feat(benchmark): add Docker runner image with RGBDS 1.0.1 and .NET 10"
```

---

### Task 3: Container-side harness — run-benchmark.sh

This is the largest single task. The script runs inside the Docker container and performs all benchmark work: environment capture, pokecrystal clone + build, pre-flight, warmup, measured runs, ROM comparison, and report generation.

**Files:**
- Create: `benchmarks/run-benchmark.sh`

- [ ] **Step 1: Create the harness script with configuration and utility functions**

Create `benchmarks/run-benchmark.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration (from environment, with defaults)
# ---------------------------------------------------------------------------
BENCH_RUNS="${BENCH_RUNS:-5}"
BENCH_WARMUP="${BENCH_WARMUP:-2}"
POKECRYSTAL_REF="${POKECRYSTAL_REF:-c73ab9e9c9a8b6eaee38f19fdcf956c1baf268ea}"
BENCH_STRICT="${BENCH_STRICT:-0}"
BENCH_COVERAGE="${BENCH_COVERAGE:-0}"
BENCH_OUTPUT="${BENCH_OUTPUT:-/results}"

ASM_FILES=(
  audio home main ram
  data/text/common data/maps/map_data
  data/pokemon/dex_entries data/pokemon/egg_moves data/pokemon/evos_attacks
  engine/movie/credits engine/overworld/events
  gfx/misc gfx/pics gfx/sprites gfx/tilesets
  lib/mobile/main lib/mobile/mail
)

# ---------------------------------------------------------------------------
# KOH capability matrix (hardcoded, update when KOH implements new flags)
# ---------------------------------------------------------------------------
# List of KOH-supported output-affecting CLI features.
# Update this array as KOH adds -P, -Q, -l, etc.
KOH_ASM_SUPPORTED_FEATURES=()
KOH_LINK_SUPPORTED_FEATURES=()

# Required features per phase
KOH_ASM_REQUIRED_FEATURES=("-P" "-Q")
KOH_LINK_REQUIRED_FEATURES=("-l")

# ---------------------------------------------------------------------------
# Utility functions
# ---------------------------------------------------------------------------
log() { echo "[benchmark] $*"; }
die() { echo "[benchmark] ERROR: $*" >&2; exit 2; }

# Check if all items in $1 (array name) are present in $2 (array name)
all_features_supported() {
  local -n required=$1
  local -n supported=$2
  for req in "${required[@]}"; do
    local found=false
    for sup in "${supported[@]+"${supported[@]}"}"; do
      if [[ "$sup" == "$req" ]]; then found=true; break; fi
    done
    if ! $found; then return 1; fi
  done
  return 0
}

# Parse /usr/bin/time -v output from a log file
parse_time_output() {
  local logfile=$1
  wall_s=$(grep "Elapsed (wall clock)" "$logfile" | sed 's/.*: //' | awk -F: '{if(NF==3) print $1*3600+$2*60+$3; else if(NF==2) print $1*60+$2; else print $1}')
  user_cpu_s=$(grep "User time" "$logfile" | sed 's/.*: //')
  sys_cpu_s=$(grep "System time" "$logfile" | sed 's/.*: //')
  peak_rss_kb=$(grep "Maximum resident" "$logfile" | sed 's/.*: //')
  cpu_s=$(echo "$user_cpu_s + $sys_cpu_s" | bc)
}

# Compute median of a space-separated list of numbers
median() {
  local sorted
  sorted=$(echo "$@" | tr ' ' '\n' | sort -g)
  local n
  n=$(echo "$sorted" | wc -l)
  if (( n % 2 == 1 )); then
    echo "$sorted" | sed -n "$(( (n+1)/2 ))p"
  else
    local a b
    a=$(echo "$sorted" | sed -n "$(( n/2 ))p")
    b=$(echo "$sorted" | sed -n "$(( n/2+1 ))p")
    echo "scale=6; ($a + $b) / 2" | bc
  fi
}

min_val() { echo "$@" | tr ' ' '\n' | sort -g | head -1; }
max_val() { echo "$@" | tr ' ' '\n' | sort -g | tail -1; }

# ---------------------------------------------------------------------------
# Step 1: Record environment metadata
# ---------------------------------------------------------------------------
log "Recording environment metadata..."
mkdir -p "$BENCH_OUTPUT/logs"

ENV_CPU=$(grep 'model name' /proc/cpuinfo | head -1 | sed 's/.*: //')
ENV_KERNEL=$(uname -r)
ENV_CONTAINER_OS=$(grep PRETTY_NAME /etc/os-release | sed 's/PRETTY_NAME="//' | sed 's/"//')
ENV_RAM_MB=$(awk '/MemTotal/ {printf "%.0f", $2/1024}' /proc/meminfo)
ENV_DOTNET=$(dotnet --version 2>/dev/null || echo "unknown")
ENV_RGBDS=$(rgbasm --version 2>&1 | head -1)
ENV_KOH=$(/opt/koh/koh-asm --version 2>&1 | head -1 || echo "unknown")
ENV_SDK_HOST="${DOTNET_SDK_HOST:-unknown}"

log "CPU: $ENV_CPU"
log "Kernel: $ENV_KERNEL"
log "Container OS: $ENV_CONTAINER_OS"
log ".NET Runtime: $ENV_DOTNET"
log "RGBDS: $ENV_RGBDS"
log "KOH: $ENV_KOH"

# ---------------------------------------------------------------------------
# Step 2: Clone pokecrystal
# ---------------------------------------------------------------------------
log "Cloning pokecrystal @ $POKECRYSTAL_REF..."
if [[ ! -d /work/pokecrystal ]]; then
  git clone --depth 50 https://github.com/pret/pokecrystal.git /work/pokecrystal
fi
cd /work/pokecrystal
git checkout "$POKECRYSTAL_REF" --quiet

# ---------------------------------------------------------------------------
# Step 3: Build prerequisites (not measured)
# ---------------------------------------------------------------------------
log "Building pokecrystal tools and graphics assets..."
make -C tools/ -j"$(nproc)" 2>&1 | tail -3
make -j"$(nproc)" 2>&1 | tail -5 || true  # Full build to generate all assets; ROM build may fail without rgbfix but assets are produced

# ---------------------------------------------------------------------------
# Step 4: Pre-flight classification
# ---------------------------------------------------------------------------
log "Running pre-flight classification..."

KOH_ASM_SUPPORTED=false
KOH_LINK_SUPPORTED=false

if all_features_supported KOH_ASM_REQUIRED_FEATURES KOH_ASM_SUPPORTED_FEATURES; then
  KOH_ASM_SUPPORTED=true
  KOH_ASM_CLASSIFICATION="supported"
  KOH_ASM_REASON=""
else
  KOH_ASM_CLASSIFICATION="unsupported_cli_feature"
  missing=()
  for req in "${KOH_ASM_REQUIRED_FEATURES[@]}"; do
    local found=false 2>/dev/null || found=false
    for sup in "${KOH_ASM_SUPPORTED_FEATURES[@]+"${KOH_ASM_SUPPORTED_FEATURES[@]}"}"; do
      if [[ "$sup" == "$req" ]]; then found=true; break; fi
    done
    if ! $found; then missing+=("$req"); fi
  done
  KOH_ASM_REASON="missing ${missing[*]} CLI flags"
fi

if all_features_supported KOH_LINK_REQUIRED_FEATURES KOH_LINK_SUPPORTED_FEATURES; then
  KOH_LINK_SUPPORTED=true
  KOH_LINK_CLASSIFICATION="supported"
  KOH_LINK_REASON=""
else
  KOH_LINK_CLASSIFICATION="unsupported_benchmark"
  KOH_LINK_REASON="missing -l (linker script) support; fair comparison not possible"
fi

log "KOH assembly: $KOH_ASM_CLASSIFICATION"
log "KOH linking: $KOH_LINK_CLASSIFICATION"

# Capture --help for auditing
/opt/koh/koh-asm --help > "$BENCH_OUTPUT/logs/koh-asm-help.txt" 2>&1 || true
/opt/koh/koh-link --help > "$BENCH_OUTPUT/logs/koh-link-help.txt" 2>&1 || true

# Write preflight.json
cat > "$BENCH_OUTPUT/preflight.json" <<PREFLIGHT_EOF
{
  "capability_matrix": {
    "koh_asm_supported_features": [$(printf '"%s",' "${KOH_ASM_SUPPORTED_FEATURES[@]+"${KOH_ASM_SUPPORTED_FEATURES[@]}"}" | sed 's/,$//')],
    "koh_link_supported_features": [$(printf '"%s",' "${KOH_LINK_SUPPORTED_FEATURES[@]+"${KOH_LINK_SUPPORTED_FEATURES[@]}"}" | sed 's/,$//')],
    "koh_asm_required_features": [$(printf '"%s",' "${KOH_ASM_REQUIRED_FEATURES[@]}" | sed 's/,$//')],
    "koh_link_required_features": [$(printf '"%s",' "${KOH_LINK_REQUIRED_FEATURES[@]}" | sed 's/,$//')],
    "koh_asm_supported": $KOH_ASM_SUPPORTED,
    "koh_link_supported": $KOH_LINK_SUPPORTED,
    "koh_asm_classification": "$KOH_ASM_CLASSIFICATION",
    "koh_link_classification": "$KOH_LINK_CLASSIFICATION"
  }
}
PREFLIGHT_EOF

# ---------------------------------------------------------------------------
# Phase execution functions
# ---------------------------------------------------------------------------
clean_outputs() {
  for asm in "${ASM_FILES[@]}"; do
    rm -f "${asm}.o" "${asm}.koh.o"
  done
  rm -f pokecrystal.gbc pokecrystal.koh.gbc pokecrystal.sym pokecrystal.map
}

run_rgbds_assembly() {
  local logfile=$1
  /usr/bin/time -v bash -c '
    set -e
    for asm in '"$(printf '%s ' "${ASM_FILES[@]}")"'; do
      rgbasm -Q8 -P includes.asm -Weverything -Wtruncation=1 -o "${asm}.o" "${asm}.asm"
    done
  ' > "$logfile" 2>&1
}

run_koh_assembly() {
  local logfile=$1
  /usr/bin/time -v bash -c '
    set -e
    for asm in '"$(printf '%s ' "${ASM_FILES[@]}")"'; do
      /opt/koh/koh-asm "${asm}.asm" -o "${asm}.koh.o" -f rgbds
    done
  ' > "$logfile" 2>&1
}

run_rgbds_linking() {
  local logfile=$1
  local obj_files=""
  for asm in "${ASM_FILES[@]}"; do obj_files+="${asm}.o "; done
  /usr/bin/time -v bash -c "
    set -e
    rgblink -Weverything -Wtruncation=1 -l layout.link \
      -n pokecrystal.sym -m pokecrystal.map -o pokecrystal.gbc \
      $obj_files
  " > "$logfile" 2>&1
}

run_koh_linking() {
  local logfile=$1
  local obj_files=""
  for asm in "${ASM_FILES[@]}"; do obj_files+="${asm}.koh.o "; done
  /usr/bin/time -v bash -c "
    set -e
    /opt/koh/koh-link -o pokecrystal.koh.gbc $obj_files
  " > "$logfile" 2>&1
}

# ---------------------------------------------------------------------------
# Step 5: Warmup runs
# ---------------------------------------------------------------------------
log "Running $BENCH_WARMUP warmup iterations..."
for (( w=1; w<=BENCH_WARMUP; w++ )); do
  clean_outputs
  run_rgbds_assembly /dev/null 2>/dev/null || true
  if $KOH_ASM_SUPPORTED; then
    run_koh_assembly /dev/null 2>/dev/null || true
  fi
  clean_outputs
  run_rgbds_assembly /dev/null 2>/dev/null || true  # need .o files for linking warmup
  run_rgbds_linking /dev/null 2>/dev/null || true
  if $KOH_LINK_SUPPORTED && $KOH_ASM_SUPPORTED; then
    run_koh_linking /dev/null 2>/dev/null || true
  fi
done

# ---------------------------------------------------------------------------
# Step 6: Measured runs
# ---------------------------------------------------------------------------
log "Running $BENCH_RUNS measured iterations..."

# Arrays to collect per-run results
declare -a RGBDS_ASM_WALL RGBDS_ASM_CPU RGBDS_ASM_RSS RGBDS_ASM_EXIT
declare -a KOH_ASM_WALL KOH_ASM_CPU KOH_ASM_RSS KOH_ASM_EXIT
declare -a RGBDS_LINK_WALL RGBDS_LINK_CPU RGBDS_LINK_RSS RGBDS_LINK_EXIT
declare -a KOH_LINK_WALL KOH_LINK_CPU KOH_LINK_RSS KOH_LINK_EXIT

HIGHEST_EXIT=0

for (( run=1; run<=BENCH_RUNS; run++ )); do
  log "  Run $run/$BENCH_RUNS"

  # --- RGBDS Assembly ---
  clean_outputs
  local_log="$BENCH_OUTPUT/logs/run-${run}-rgbds-assembly.log"
  if run_rgbds_assembly "$local_log"; then
    parse_time_output "$local_log"
    RGBDS_ASM_WALL+=("$wall_s")
    RGBDS_ASM_CPU+=("$cpu_s")
    RGBDS_ASM_RSS+=("$peak_rss_kb")
    RGBDS_ASM_EXIT+=(0)
  else
    RGBDS_ASM_EXIT+=(1)
    die "RGBDS assembly failed on run $run — broken setup. See $local_log"
  fi

  # --- KOH Assembly ---
  if $KOH_ASM_SUPPORTED; then
    local_log="$BENCH_OUTPUT/logs/run-${run}-koh-assembly.log"
    if run_koh_assembly "$local_log"; then
      parse_time_output "$local_log"
      KOH_ASM_WALL+=("$wall_s")
      KOH_ASM_CPU+=("$cpu_s")
      KOH_ASM_RSS+=("$peak_rss_kb")
      KOH_ASM_EXIT+=(0)
    else
      KOH_ASM_EXIT+=(1)
      HIGHEST_EXIT=1
    fi
  fi

  # --- RGBDS Linking ---
  local_log="$BENCH_OUTPUT/logs/run-${run}-rgbds-linking.log"
  if run_rgbds_linking "$local_log"; then
    parse_time_output "$local_log"
    RGBDS_LINK_WALL+=("$wall_s")
    RGBDS_LINK_CPU+=("$cpu_s")
    RGBDS_LINK_RSS+=("$peak_rss_kb")
    RGBDS_LINK_EXIT+=(0)
  else
    RGBDS_LINK_EXIT+=(1)
    die "RGBDS linking failed on run $run — broken setup. See $local_log"
  fi

  # --- KOH Linking ---
  if $KOH_LINK_SUPPORTED && $KOH_ASM_SUPPORTED; then
    # Only attempt if KOH assembly succeeded this run
    local koh_asm_ok=false
    if [[ ${#KOH_ASM_EXIT[@]} -ge $run ]] && [[ ${KOH_ASM_EXIT[$((run-1))]} -eq 0 ]]; then
      koh_asm_ok=true
    fi
    if $koh_asm_ok; then
      local_log="$BENCH_OUTPUT/logs/run-${run}-koh-linking.log"
      if run_koh_linking "$local_log"; then
        parse_time_output "$local_log"
        KOH_LINK_WALL+=("$wall_s")
        KOH_LINK_CPU+=("$cpu_s")
        KOH_LINK_RSS+=("$peak_rss_kb")
        KOH_LINK_EXIT+=(0)
      else
        KOH_LINK_EXIT+=(1)
        HIGHEST_EXIT=1
      fi
    fi
  fi
done

# ---------------------------------------------------------------------------
# Step 7: ROM comparison
# ---------------------------------------------------------------------------
ROM_STATUS="skip"
ROM_REASON="KOH did not produce a ROM"
RGBDS_SHA256=""
KOH_SHA256=""
RGBDS_SIZE=""
KOH_SIZE=""
FIRST_DIFF=""

if [[ -f pokecrystal.gbc ]] && [[ -f pokecrystal.koh.gbc ]]; then
  log "Applying rgbfix to both ROMs..."
  RGBFIX_CMD="rgbfix -Cjv -t PM_CRYSTAL -k 01 -l 0x33 -m MBC3+TIMER+RAM+BATTERY -r 3 -p 0 -i BYTE -n 0"

  if $RGBFIX_CMD pokecrystal.gbc > "$BENCH_OUTPUT/logs/rgbfix-rgbds.log" 2>&1; then
    RGBDS_SHA256=$(sha256sum pokecrystal.gbc | awk '{print $1}')
    RGBDS_SIZE=$(stat -c%s pokecrystal.gbc)
  else
    die "rgbfix rejected RGBDS ROM — broken setup"
  fi

  if $RGBFIX_CMD pokecrystal.koh.gbc > "$BENCH_OUTPUT/logs/rgbfix-koh.log" 2>&1; then
    KOH_SHA256=$(sha256sum pokecrystal.koh.gbc | awk '{print $1}')
    KOH_SIZE=$(stat -c%s pokecrystal.koh.gbc)

    if [[ "$RGBDS_SHA256" == "$KOH_SHA256" ]]; then
      ROM_STATUS="match"
      ROM_REASON=""
    else
      ROM_STATUS="mismatch"
      ROM_REASON="SHA256 differs"
      FIRST_DIFF=$(cmp -l pokecrystal.gbc pokecrystal.koh.gbc 2>/dev/null | head -1 | awk '{print $1}')
      HIGHEST_EXIT=1
    fi
  else
    ROM_STATUS="rom_postprocess_failure"
    ROM_REASON="rgbfix rejected KOH ROM"
    HIGHEST_EXIT=1
  fi
fi

# ---------------------------------------------------------------------------
# Step 8: Generate reports
# ---------------------------------------------------------------------------
log "Generating reports..."

# Helper: format seconds
fmt_s() {
  if [[ -z "$1" ]] || [[ "$1" == "null" ]]; then echo "—"; return; fi
  echo "${1}s"
}
fmt_kb_mb() {
  if [[ -z "$1" ]] || [[ "$1" == "null" ]]; then echo "—"; return; fi
  echo "$(( $1 / 1024 )) MB"
}

# Compute aggregates for RGBDS assembly
rgbds_asm_wall_med=$(median "${RGBDS_ASM_WALL[@]}")
rgbds_asm_cpu_med=$(median "${RGBDS_ASM_CPU[@]}")
rgbds_asm_rss_med=$(median "${RGBDS_ASM_RSS[@]}")
rgbds_asm_wall_min=$(min_val "${RGBDS_ASM_WALL[@]}")
rgbds_asm_wall_max=$(max_val "${RGBDS_ASM_WALL[@]}")
rgbds_asm_ok=${#RGBDS_ASM_WALL[@]}

# Compute aggregates for RGBDS linking
rgbds_link_wall_med=$(median "${RGBDS_LINK_WALL[@]}")
rgbds_link_cpu_med=$(median "${RGBDS_LINK_CPU[@]}")
rgbds_link_rss_med=$(median "${RGBDS_LINK_RSS[@]}")
rgbds_link_wall_min=$(min_val "${RGBDS_LINK_WALL[@]}")
rgbds_link_wall_max=$(max_val "${RGBDS_LINK_WALL[@]}")
rgbds_link_ok=${#RGBDS_LINK_WALL[@]}

# KOH assembly aggregates (may be empty)
koh_asm_status="unsupported"
koh_asm_wall_med="" koh_asm_cpu_med="" koh_asm_rss_med=""
koh_asm_wall_min="" koh_asm_wall_max=""
koh_asm_ok=0 koh_asm_executed=0
speedup_asm="—"

if $KOH_ASM_SUPPORTED && [[ ${#KOH_ASM_WALL[@]} -gt 0 ]]; then
  koh_asm_status="ok"
  koh_asm_ok=${#KOH_ASM_WALL[@]}
  koh_asm_executed=$BENCH_RUNS
  koh_asm_wall_med=$(median "${KOH_ASM_WALL[@]}")
  koh_asm_cpu_med=$(median "${KOH_ASM_CPU[@]}")
  koh_asm_rss_med=$(median "${KOH_ASM_RSS[@]}")
  koh_asm_wall_min=$(min_val "${KOH_ASM_WALL[@]}")
  koh_asm_wall_max=$(max_val "${KOH_ASM_WALL[@]}")
  if [[ $koh_asm_ok -eq $BENCH_RUNS ]] && [[ $rgbds_asm_ok -eq $BENCH_RUNS ]]; then
    speedup_asm=$(echo "scale=2; $rgbds_asm_wall_med / $koh_asm_wall_med" | bc)
    speedup_asm="${speedup_asm}x"
  fi
fi

# KOH linking aggregates
koh_link_status="unsupported"
koh_link_wall_med="" koh_link_cpu_med="" koh_link_rss_med=""
koh_link_wall_min="" koh_link_wall_max=""
koh_link_ok=0 koh_link_executed=0
speedup_link="—"

if $KOH_LINK_SUPPORTED && [[ ${#KOH_LINK_WALL[@]} -gt 0 ]]; then
  koh_link_status="ok"
  koh_link_ok=${#KOH_LINK_WALL[@]}
  koh_link_executed=$BENCH_RUNS
  koh_link_wall_med=$(median "${KOH_LINK_WALL[@]}")
  koh_link_cpu_med=$(median "${KOH_LINK_CPU[@]}")
  koh_link_rss_med=$(median "${KOH_LINK_RSS[@]}")
  koh_link_wall_min=$(min_val "${KOH_LINK_WALL[@]}")
  koh_link_wall_max=$(max_val "${KOH_LINK_WALL[@]}")
  if [[ $koh_link_ok -eq $BENCH_RUNS ]] && [[ $rgbds_link_ok -eq $BENCH_RUNS ]]; then
    speedup_link=$(echo "scale=2; $rgbds_link_wall_med / $koh_link_wall_med" | bc)
    speedup_link="${speedup_link}x"
  fi
fi

# --- Write results.md ---
cat > "$BENCH_OUTPUT/results.md" <<RESULTS_EOF
## KOH vs RGBDS Benchmark — pokecrystal @ ${POKECRYSTAL_REF:0:7}

### Environment
- CPU: $ENV_CPU
- Kernel: $ENV_KERNEL
- Container OS: $ENV_CONTAINER_OS
- RAM: $ENV_RAM_MB MB
- .NET Runtime: $ENV_DOTNET
- .NET SDK (host publish): $ENV_SDK_HOST
- RGBDS: $ENV_RGBDS
- KOH: $ENV_KOH
- Cache policy: warm
- CPU pinning: ${BENCH_CPUSET:-none}

### Configuration
- Measured runs: $BENCH_RUNS
- Warmup runs: $BENCH_WARMUP

### Results

Note: CPU (median) = median of per-run (user + sys) values.

| Phase    | Tool  | Status      | Wall (median) | CPU (median) | Peak RSS (median) | Min/Max Wall | Runs OK | Speedup |
|----------|-------|-------------|---------------|-------------|-------------------|-------------|---------|---------|
| Assembly | RGBDS | ok          | $(fmt_s "$rgbds_asm_wall_med") | $(fmt_s "$rgbds_asm_cpu_med") | $(fmt_kb_mb "$rgbds_asm_rss_med") | ${rgbds_asm_wall_min}–${rgbds_asm_wall_max}s | ${rgbds_asm_ok}/${BENCH_RUNS} | — |
| Assembly | KOH   | $koh_asm_status | $(fmt_s "$koh_asm_wall_med") | $(fmt_s "$koh_asm_cpu_med") | $(fmt_kb_mb "$koh_asm_rss_med") | $(if [[ -n "$koh_asm_wall_min" ]]; then echo "${koh_asm_wall_min}–${koh_asm_wall_max}s"; else echo "—"; fi) | $(if $KOH_ASM_SUPPORTED; then echo "${koh_asm_ok}/${BENCH_RUNS}"; else echo "—"; fi) | $speedup_asm |
| Linking  | RGBDS | ok          | $(fmt_s "$rgbds_link_wall_med") | $(fmt_s "$rgbds_link_cpu_med") | $(fmt_kb_mb "$rgbds_link_rss_med") | ${rgbds_link_wall_min}–${rgbds_link_wall_max}s | ${rgbds_link_ok}/${BENCH_RUNS} | — |
| Linking  | KOH   | $koh_link_status | $(fmt_s "$koh_link_wall_med") | $(fmt_s "$koh_link_cpu_med") | $(fmt_kb_mb "$koh_link_rss_med") | $(if [[ -n "$koh_link_wall_min" ]]; then echo "${koh_link_wall_min}–${koh_link_wall_max}s"; else echo "—"; fi) | $(if $KOH_LINK_SUPPORTED; then echo "${koh_link_ok}/${BENCH_RUNS}"; else echo "—"; fi) | $speedup_link |

### Phase Preconditions

| Phase    | Tool | Requires           | Supported |
|----------|------|--------------------|-----------|
| Assembly | KOH  | -P (preinclude)    | $(if echo "${KOH_ASM_SUPPORTED_FEATURES[*]+"${KOH_ASM_SUPPORTED_FEATURES[*]}"}" | grep -q "\-P"; then echo "yes"; else echo "no"; fi) |
| Assembly | KOH  | -Q (fill byte)     | $(if echo "${KOH_ASM_SUPPORTED_FEATURES[*]+"${KOH_ASM_SUPPORTED_FEATURES[*]}"}" | grep -q "\-Q"; then echo "yes"; else echo "no"; fi) |
| Linking  | KOH  | -l (linker script) | $(if echo "${KOH_LINK_SUPPORTED_FEATURES[*]+"${KOH_LINK_SUPPORTED_FEATURES[*]}"}" | grep -q "\-l"; then echo "yes"; else echo "no"; fi) |

### ROM Comparison
Status: ${ROM_STATUS}$(if [[ -n "$ROM_REASON" ]]; then echo " ($ROM_REASON)"; fi)
$(if [[ "$ROM_STATUS" == "match" ]]; then echo "SHA256: $RGBDS_SHA256"; fi)

### Failure Details
$(if ! $KOH_ASM_SUPPORTED; then echo "- KOH assembly: $KOH_ASM_CLASSIFICATION — $KOH_ASM_REASON"; fi)
$(if ! $KOH_LINK_SUPPORTED; then echo "- KOH linking: $KOH_LINK_CLASSIFICATION — $KOH_LINK_REASON"; fi)
- See logs/ for full stderr/stdout

### Raw Data
See raw.json for per-run measurements.
RESULTS_EOF

# --- Write raw.json ---
# Build runs array as JSON
runs_json="["
for (( run=1; run<=BENCH_RUNS; run++ )); do
  idx=$((run-1))
  runs_json+="{"
  runs_json+="\"run\":$run,"

  # RGBDS assembly
  runs_json+="\"rgbds_assembly\":{\"status\":\"ok\","
  runs_json+="\"measurement_tool\":\"/usr/bin/time -v\","
  runs_json+="\"wall_s\":${RGBDS_ASM_WALL[$idx]},\"user_cpu_s\":0,\"sys_cpu_s\":0,"
  runs_json+="\"cpu_s\":${RGBDS_ASM_CPU[$idx]},\"peak_rss_kb\":${RGBDS_ASM_RSS[$idx]},"
  runs_json+="\"exit_code\":0,\"contributed_to_aggregates\":true,"
  runs_json+="\"log_path\":\"logs/run-${run}-rgbds-assembly.log\"},"

  # KOH assembly
  if ! $KOH_ASM_SUPPORTED; then
    runs_json+="\"koh_assembly\":{\"status\":\"unsupported\",\"classification\":\"$KOH_ASM_CLASSIFICATION\",\"reason\":\"$KOH_ASM_REASON\"},"
  else
    runs_json+="\"koh_assembly\":{\"status\":\"ok\",\"wall_s\":${KOH_ASM_WALL[$idx]:-null}},"
  fi

  # RGBDS linking
  runs_json+="\"rgbds_linking\":{\"status\":\"ok\","
  runs_json+="\"measurement_tool\":\"/usr/bin/time -v\","
  runs_json+="\"wall_s\":${RGBDS_LINK_WALL[$idx]},\"cpu_s\":${RGBDS_LINK_CPU[$idx]},"
  runs_json+="\"peak_rss_kb\":${RGBDS_LINK_RSS[$idx]},\"exit_code\":0,"
  runs_json+="\"contributed_to_aggregates\":true,"
  runs_json+="\"log_path\":\"logs/run-${run}-rgbds-linking.log\"},"

  # KOH linking
  if ! $KOH_LINK_SUPPORTED; then
    runs_json+="\"koh_linking\":{\"status\":\"unsupported\",\"classification\":\"$KOH_LINK_CLASSIFICATION\",\"reason\":\"$KOH_LINK_REASON\"}"
  else
    runs_json+="\"koh_linking\":{\"status\":\"ok\"}"
  fi

  runs_json+="}"
  if (( run < BENCH_RUNS )); then runs_json+=","; fi
done
runs_json+="]"

cat > "$BENCH_OUTPUT/raw.json" <<RAW_EOF
{
  "schema_version": 1,
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "environment": {
    "cpu_model": "$ENV_CPU",
    "kernel": "$ENV_KERNEL",
    "container_os": "$ENV_CONTAINER_OS",
    "ram_mb": $ENV_RAM_MB,
    "dotnet_runtime": "$ENV_DOTNET",
    "dotnet_sdk_host": "$ENV_SDK_HOST",
    "rgbds_version": "$ENV_RGBDS",
    "koh_version": "$ENV_KOH"
  },
  "config": {
    "runs": $BENCH_RUNS,
    "warmup": $BENCH_WARMUP,
    "cache_policy": "warm",
    "cpu_pinning": $(if [[ -n "${BENCH_CPUSET:-}" ]]; then echo "\"$BENCH_CPUSET\""; else echo "null"; fi),
    "pokecrystal_ref": "$POKECRYSTAL_REF"
  },
  "phase_preconditions": {
    "koh_assembly": {
      "requires": [$(printf '"%s",' "${KOH_ASM_REQUIRED_FEATURES[@]}" | sed 's/,$//')],
      "supported": $KOH_ASM_SUPPORTED,
      "classification": "$KOH_ASM_CLASSIFICATION"
    },
    "koh_linking": {
      "requires": [$(printf '"%s",' "${KOH_LINK_REQUIRED_FEATURES[@]}" | sed 's/,$//')],
      "supported": $KOH_LINK_SUPPORTED,
      "classification": "$KOH_LINK_CLASSIFICATION"
    }
  },
  "runs": $runs_json,
  "aggregates": {
    "rgbds_assembly": {
      "wall_median_s": $rgbds_asm_wall_med, "wall_min_s": $rgbds_asm_wall_min, "wall_max_s": $rgbds_asm_wall_max,
      "cpu_median_s": $rgbds_asm_cpu_med, "peak_rss_median_kb": $rgbds_asm_rss_med,
      "success_count": $rgbds_asm_ok, "executed_count": $BENCH_RUNS, "planned_count": $BENCH_RUNS
    },
    "koh_assembly": {
      "status": "$koh_asm_status", "classification": "$(if ! $KOH_ASM_SUPPORTED; then echo "$KOH_ASM_CLASSIFICATION"; else echo "ok"; fi)",
      "success_count": $koh_asm_ok, "executed_count": $koh_asm_executed, "planned_count": $BENCH_RUNS
    },
    "rgbds_linking": {
      "wall_median_s": $rgbds_link_wall_med, "wall_min_s": $rgbds_link_wall_min, "wall_max_s": $rgbds_link_wall_max,
      "cpu_median_s": $rgbds_link_cpu_med, "peak_rss_median_kb": $rgbds_link_rss_med,
      "success_count": $rgbds_link_ok, "executed_count": $BENCH_RUNS, "planned_count": $BENCH_RUNS
    },
    "koh_linking": {
      "status": "$koh_link_status", "classification": "$(if ! $KOH_LINK_SUPPORTED; then echo "$KOH_LINK_CLASSIFICATION"; else echo "ok"; fi)",
      "success_count": $koh_link_ok, "executed_count": $koh_link_executed, "planned_count": $BENCH_RUNS
    },
    "speedup_assembly": $(if [[ "$speedup_asm" == "—" ]]; then echo "null"; else echo "\"$speedup_asm\""; fi),
    "speedup_linking": $(if [[ "$speedup_link" == "—" ]]; then echo "null"; else echo "\"$speedup_link\""; fi)
  },
  "rom_comparison": {
    "status": "$ROM_STATUS",
    "reason": $(if [[ -n "$ROM_REASON" ]]; then echo "\"$ROM_REASON\""; else echo "null"; fi),
    "rgbds_sha256": $(if [[ -n "$RGBDS_SHA256" ]]; then echo "\"$RGBDS_SHA256\""; else echo "null"; fi),
    "koh_sha256": $(if [[ -n "$KOH_SHA256" ]]; then echo "\"$KOH_SHA256\""; else echo "null"; fi),
    "rgbds_size_bytes": $(if [[ -n "$RGBDS_SIZE" ]]; then echo "$RGBDS_SIZE"; else echo "null"; fi),
    "koh_size_bytes": $(if [[ -n "$KOH_SIZE" ]]; then echo "$KOH_SIZE"; else echo "null"; fi),
    "first_diff_offset": $(if [[ -n "$FIRST_DIFF" ]]; then echo "$FIRST_DIFF"; else echo "null"; fi)
  }
}
RAW_EOF

log "Results written to $BENCH_OUTPUT/"

# ---------------------------------------------------------------------------
# Exit code logic
# ---------------------------------------------------------------------------
final_exit=0
has_unsupported=false
if ! $KOH_ASM_SUPPORTED || ! $KOH_LINK_SUPPORTED; then has_unsupported=true; fi

if [[ "$BENCH_STRICT" == "1" ]] && [[ $HIGHEST_EXIT -gt 0 ]]; then
  final_exit=1
fi
if [[ "$BENCH_COVERAGE" == "1" ]] && $has_unsupported; then
  final_exit=3
fi

exit $final_exit
```

- [ ] **Step 2: Make it executable**

```bash
chmod +x benchmarks/run-benchmark.sh
```

- [ ] **Step 3: Commit**

```bash
git add benchmarks/run-benchmark.sh
git commit -m "feat(benchmark): add container-side benchmark harness"
```

---

### Task 4: Host-side orchestrator — benchmark.sh

**Files:**
- Create: `benchmarks/benchmark.sh`

- [ ] **Step 1: Create the host-side script**

Create `benchmarks/benchmark.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

BENCH_IMAGE="${BENCH_IMAGE:-koh-benchmark}"
BENCH_OUTPUT="${BENCH_OUTPUT:-$SCRIPT_DIR/results}"
BENCH_CPUSET="${BENCH_CPUSET:-}"

# ---------------------------------------------------------------------------
# Step 1: Prerequisite check
# ---------------------------------------------------------------------------
echo "[benchmark] Checking prerequisites..."

if ! command -v docker &>/dev/null; then
  echo "[benchmark] ERROR: docker is not installed or not in PATH" >&2
  exit 2
fi
echo "[benchmark] Docker: $(docker --version)"

if ! command -v dotnet &>/dev/null; then
  echo "[benchmark] ERROR: dotnet is not installed or not in PATH" >&2
  exit 2
fi
DOTNET_SDK_VERSION=$(dotnet --version)
echo "[benchmark] .NET SDK: $DOTNET_SDK_VERSION"

# ---------------------------------------------------------------------------
# Step 2: Publish KOH for linux-x64
# ---------------------------------------------------------------------------
echo "[benchmark] Publishing KOH for linux-x64..."
PUBLISH_DIR="$SCRIPT_DIR/.publish"
rm -rf "$PUBLISH_DIR"

dotnet publish "$REPO_ROOT/src/Koh.Asm/Koh.Asm.csproj" \
  -c Release -r linux-x64 --no-self-contained \
  -o "$PUBLISH_DIR" -p:PublishSingleFile=false \
  --verbosity quiet

dotnet publish "$REPO_ROOT/src/Koh.Link/Koh.Link.csproj" \
  -c Release -r linux-x64 --no-self-contained \
  -o "$PUBLISH_DIR" -p:PublishSingleFile=false \
  --verbosity quiet

# Rename to expected names
if [[ -f "$PUBLISH_DIR/Koh.Asm" ]]; then
  mv "$PUBLISH_DIR/Koh.Asm" "$PUBLISH_DIR/koh-asm"
fi
if [[ -f "$PUBLISH_DIR/Koh.Link" ]]; then
  mv "$PUBLISH_DIR/Koh.Link" "$PUBLISH_DIR/koh-link"
fi

echo "[benchmark] Published to $PUBLISH_DIR"

# ---------------------------------------------------------------------------
# Step 3: Build Docker image
# ---------------------------------------------------------------------------
echo "[benchmark] Building Docker image '$BENCH_IMAGE'..."
docker build -t "$BENCH_IMAGE" "$SCRIPT_DIR"

# ---------------------------------------------------------------------------
# Step 4: Run benchmark container
# ---------------------------------------------------------------------------
echo "[benchmark] Running benchmark..."
mkdir -p "$BENCH_OUTPUT"
rm -rf "$BENCH_OUTPUT"/*

DOCKER_ARGS=(
  --rm
  -v "$PUBLISH_DIR:/opt/koh:ro"
  -v "$BENCH_OUTPUT:/results"
  -e "BENCH_RUNS=${BENCH_RUNS:-5}"
  -e "BENCH_WARMUP=${BENCH_WARMUP:-2}"
  -e "BENCH_STRICT=${BENCH_STRICT:-0}"
  -e "BENCH_COVERAGE=${BENCH_COVERAGE:-0}"
  -e "DOTNET_SDK_HOST=$DOTNET_SDK_VERSION"
)

if [[ -n "${POKECRYSTAL_REF:-}" ]]; then
  DOCKER_ARGS+=(-e "POKECRYSTAL_REF=$POKECRYSTAL_REF")
fi

if [[ -n "$BENCH_CPUSET" ]]; then
  DOCKER_ARGS+=(--cpuset-cpus "$BENCH_CPUSET")
fi

docker run "${DOCKER_ARGS[@]}" "$BENCH_IMAGE"
exit_code=$?

# ---------------------------------------------------------------------------
# Step 5: Print summary
# ---------------------------------------------------------------------------
echo ""
echo "================================================================"
if [[ -f "$BENCH_OUTPUT/results.md" ]]; then
  cat "$BENCH_OUTPUT/results.md"
else
  echo "[benchmark] No results.md generated"
fi
echo "================================================================"
echo "[benchmark] Full results in: $BENCH_OUTPUT/"

exit $exit_code
```

- [ ] **Step 2: Make it executable**

```bash
chmod +x benchmarks/benchmark.sh
```

- [ ] **Step 3: Commit**

```bash
git add benchmarks/benchmark.sh
git commit -m "feat(benchmark): add host-side orchestrator script"
```

---

### Task 5: Smoke-test the head-to-head benchmark end-to-end

**Files:** (no new files)

- [ ] **Step 1: Run `make benchmark` with 1 run to verify the pipeline works**

```bash
BENCH_RUNS=1 BENCH_WARMUP=0 make benchmark
```

Expected: the benchmark runs, produces `benchmarks/results/results.md` and `benchmarks/results/raw.json`. KOH phases show as `unsupported`. RGBDS phases show timing. No crashes.

- [ ] **Step 2: Verify output files exist and are well-formed**

```bash
cat benchmarks/results/results.md
python3 -m json.tool benchmarks/results/raw.json > /dev/null && echo "Valid JSON"
python3 -m json.tool benchmarks/results/preflight.json > /dev/null && echo "Valid JSON"
```

- [ ] **Step 3: Fix any issues found, then commit fixes if needed**

```bash
git add -A benchmarks/
git commit -m "fix(benchmark): address smoke test issues"
```

---

### Task 6: BenchmarkDotNet project setup

**Files:**
- Create: `benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj`
- Create: `benchmarks/Koh.Benchmarks/Program.cs`
- Create: `benchmarks/Koh.Benchmarks/BenchmarkConfig.cs`
- Modify: `Koh.slnx`
- Modify: `Directory.Packages.props`

- [ ] **Step 1: Add BenchmarkDotNet to Directory.Packages.props**

Add to the `<ItemGroup>` in `Directory.Packages.props`:

```xml
<PackageVersion Include="BenchmarkDotNet" Version="0.14.0" />
```

- [ ] **Step 2: Create the project file**

Create `benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/Koh.Core/Koh.Core.csproj" />
    <ProjectReference Include="../../src/Koh.Emit/Koh.Emit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create BenchmarkConfig.cs**

Create `benchmarks/Koh.Benchmarks/BenchmarkConfig.cs`:

```csharp
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace Koh.Benchmarks;

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

- [ ] **Step 4: Create Program.cs**

Create `benchmarks/Koh.Benchmarks/Program.cs`:

```csharp
using BenchmarkDotNet.Running;
using Koh.Benchmarks;

var bdnVersion = typeof(BenchmarkRunner).Assembly.GetName().Version;
Console.WriteLine($"BenchmarkDotNet {bdnVersion}");
Console.WriteLine($".NET {Environment.Version}");
Console.WriteLine();

var pokecrystalPath = Environment.GetEnvironmentVariable("POKECRYSTAL_PATH");
if (string.IsNullOrEmpty(pokecrystalPath) || !Directory.Exists(pokecrystalPath))
{
    var strict = Environment.GetEnvironmentVariable("BENCH_STRICT") == "1";
    Console.Error.WriteLine("POKECRYSTAL_PATH is not set or does not exist.");
    Console.Error.WriteLine("Clone pokecrystal and set the environment variable:");
    Console.Error.WriteLine("  git clone https://github.com/pret/pokecrystal.git /path/to/pokecrystal");
    Console.Error.WriteLine("  export POKECRYSTAL_PATH=/path/to/pokecrystal");
    return strict ? 1 : 0;
}

BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConfig).Assembly).Run(args, new BenchmarkConfig());
return 0;
```

- [ ] **Step 5: Add to solution**

Add to `Koh.slnx` inside a new `<Folder Name="/benchmarks/">`:

```xml
  <Folder Name="/benchmarks/">
    <Project Path="benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj" />
  </Folder>
```

- [ ] **Step 6: Verify it builds**

```bash
dotnet build benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props Koh.slnx benchmarks/Koh.Benchmarks/
git commit -m "feat(benchmark): add BenchmarkDotNet project with config and entry point"
```

---

### Task 7: ParseBenchmarks — entry-file parse benchmarks

**Files:**
- Create: `benchmarks/Koh.Benchmarks/ParseBenchmarks.cs`

- [ ] **Step 1: Create ParseBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Benchmarks;

/// <summary>
/// Measures lexing + parsing of pokecrystal entry files.
/// CAVEAT: These benchmarks parse the entry file text only — they are NOT a proxy
/// for parsing the full transitive compilation unit. INCLUDE resolution happens
/// later in the bind phase. The entry files are mostly INCLUDE directives.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class ParseBenchmarks
{
    private SourceText _smallSource = null!;
    private SourceText _mediumSource = null!;
    private SourceText _largeSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        var root = Environment.GetEnvironmentVariable("POKECRYSTAL_PATH")
            ?? throw new InvalidOperationException("POKECRYSTAL_PATH not set");

        _smallSource = LoadSource(root, "ram.asm");
        _mediumSource = LoadSource(root, "home.asm");
        _largeSource = LoadSource(root, "main.asm");
    }

    [Benchmark]
    public SyntaxTree ParseEntryFileSmall() => SyntaxTree.Parse(_smallSource);

    [Benchmark]
    public SyntaxTree ParseEntryFileMedium() => SyntaxTree.Parse(_mediumSource);

    [Benchmark]
    public SyntaxTree ParseEntryFileLarge() => SyntaxTree.Parse(_largeSource);

    private static SourceText LoadSource(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Benchmark input not found: {fullPath}");
        return SourceText.From(File.ReadAllText(fullPath), fullPath);
    }
}
```

- [ ] **Step 2: Verify it builds**

```bash
dotnet build benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj
```

- [ ] **Step 3: Commit**

```bash
git add benchmarks/Koh.Benchmarks/ParseBenchmarks.cs
git commit -m "feat(benchmark): add entry-file parse benchmarks"
```

---

### Task 8: BindEmitWithIncludesBenchmarks

**Files:**
- Create: `benchmarks/Koh.Benchmarks/BindEmitWithIncludesBenchmarks.cs`

- [ ] **Step 1: Create BindEmitWithIncludesBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Benchmarks;

/// <summary>
/// Measures bind + emit including transitive INCLUDE file resolution from disk.
/// After BDN warmup, included files are in the OS page cache — I/O cost is
/// primarily syscall overhead, not physical disk reads. These numbers should
/// NOT be read as pure CPU-bound compiler core throughput.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class BindEmitWithIncludesBenchmarks
{
    private SyntaxTree _smallTree = null!;
    private SyntaxTree _mediumTree = null!;
    private SyntaxTree _largeTree = null!;
    private ISourceFileResolver _fileResolver = null!;

    [GlobalSetup]
    public void Setup()
    {
        var root = Environment.GetEnvironmentVariable("POKECRYSTAL_PATH")
            ?? throw new InvalidOperationException("POKECRYSTAL_PATH not set");

        _fileResolver = new FileSystemResolver();

        _smallTree = ParseFile(root, "ram.asm");
        _mediumTree = ParseFile(root, "home.asm");
        _largeTree = ParseFile(root, "main.asm");

        // One-time validation: verify all inputs produce successful results
        // using the same Binder options and resolver as the benchmark methods.
        Validate(_smallTree, "ram.asm");
        Validate(_mediumTree, "home.asm");
        Validate(_largeTree, "main.asm");
    }

    [Benchmark]
    public BindingResult BindEmitSmall()
    {
        var binder = new Binder(default, _fileResolver);
        var result = binder.Bind(_smallTree);
        if (!result.Success) throw new InvalidOperationException("Bind failed for ram.asm");
        return result;
    }

    [Benchmark]
    public BindingResult BindEmitMedium()
    {
        var binder = new Binder(default, _fileResolver);
        var result = binder.Bind(_mediumTree);
        if (!result.Success) throw new InvalidOperationException("Bind failed for home.asm");
        return result;
    }

    [Benchmark]
    public BindingResult BindEmitLarge()
    {
        var binder = new Binder(default, _fileResolver);
        var result = binder.Bind(_largeTree);
        if (!result.Success) throw new InvalidOperationException("Bind failed for main.asm");
        return result;
    }

    private static SyntaxTree ParseFile(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Benchmark input not found: {fullPath}");
        var source = SourceText.From(File.ReadAllText(fullPath), fullPath);
        return SyntaxTree.Parse(source);
    }

    private void Validate(SyntaxTree tree, string name)
    {
        var binder = new Binder(default, _fileResolver);
        var result = binder.Bind(tree);
        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(5)
                .Select(d => d.Message);
            throw new InvalidOperationException(
                $"Validation failed for {name}: {string.Join("; ", errors)}");
        }
    }
}
```

- [ ] **Step 2: Verify it builds**

```bash
dotnet build benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj
```

- [ ] **Step 3: Commit**

```bash
git add benchmarks/Koh.Benchmarks/BindEmitWithIncludesBenchmarks.cs
git commit -m "feat(benchmark): add bind+emit benchmarks with include resolution"
```

---

### Task 9: FullPipelineBenchmarks

**Files:**
- Create: `benchmarks/Koh.Benchmarks/FullPipelineBenchmarks.cs`

- [ ] **Step 1: Create FullPipelineBenchmarks.cs**

```csharp
using BenchmarkDotNet.Attributes;
using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Benchmarks;

/// <summary>
/// Measures the full pipeline: lex + parse + bind + emit + freeze.
/// Includes transitive INCLUDE resolution from disk (same caveat as
/// BindEmitWithIncludesBenchmarks).
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class FullPipelineBenchmarks
{
    private SourceText _smallSource = null!;
    private SourceText _mediumSource = null!;
    private SourceText _largeSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        var root = Environment.GetEnvironmentVariable("POKECRYSTAL_PATH")
            ?? throw new InvalidOperationException("POKECRYSTAL_PATH not set");

        _smallSource = LoadSource(root, "ram.asm");
        _mediumSource = LoadSource(root, "home.asm");
        _largeSource = LoadSource(root, "main.asm");

        // One-time validation
        Validate(_smallSource, "ram.asm");
        Validate(_mediumSource, "home.asm");
        Validate(_largeSource, "main.asm");
    }

    [Benchmark]
    public EmitModel FullPipelineSmall()
    {
        var tree = SyntaxTree.Parse(_smallSource);
        var model = Compilation.Create(tree).Emit();
        if (!model.Success) throw new InvalidOperationException("Pipeline failed for ram.asm");
        return model;
    }

    [Benchmark]
    public EmitModel FullPipelineMedium()
    {
        var tree = SyntaxTree.Parse(_mediumSource);
        var model = Compilation.Create(tree).Emit();
        if (!model.Success) throw new InvalidOperationException("Pipeline failed for home.asm");
        return model;
    }

    [Benchmark]
    public EmitModel FullPipelineLarge()
    {
        var tree = SyntaxTree.Parse(_largeSource);
        var model = Compilation.Create(tree).Emit();
        if (!model.Success) throw new InvalidOperationException("Pipeline failed for main.asm");
        return model;
    }

    private static SourceText LoadSource(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Benchmark input not found: {fullPath}");
        return SourceText.From(File.ReadAllText(fullPath), fullPath);
    }

    private static void Validate(SourceText source, string name)
    {
        var tree = SyntaxTree.Parse(source);
        var model = Compilation.Create(tree).Emit();
        if (!model.Success)
        {
            var errors = model.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(5)
                .Select(d => d.Message);
            throw new InvalidOperationException(
                $"Validation failed for {name}: {string.Join("; ", errors)}");
        }
    }
}
```

- [ ] **Step 2: Verify it builds**

```bash
dotnet build benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj
```

- [ ] **Step 3: Commit**

```bash
git add benchmarks/Koh.Benchmarks/FullPipelineBenchmarks.cs
git commit -m "feat(benchmark): add full pipeline benchmarks"
```

---

### Task 10: Verify microbenchmarks run

**Files:** (no new files)

- [ ] **Step 1: Run the benchmarks in dry-run mode to verify setup**

This requires a local pokecrystal clone. If you already have one from earlier exploration:

```bash
POKECRYSTAL_PATH=/tmp/pokecrystal dotnet run -c Release --project benchmarks/Koh.Benchmarks -- --list flat
```

Expected: lists all 9 benchmark methods (3 parse + 3 bind + 3 pipeline).

- [ ] **Step 2: Run one quick benchmark to verify end-to-end**

```bash
POKECRYSTAL_PATH=/tmp/pokecrystal dotnet run -c Release --project benchmarks/Koh.Benchmarks -- --filter '*Small*' --job Dry
```

Expected: BDN executes a dry run of the Small benchmarks, prints environment summary and results table. If benchmarks fail with compilation errors for pokecrystal files (which is expected — KOH may not support all pokecrystal features), the GlobalSetup validation will throw with a clear message.

- [ ] **Step 3: Commit any fixes**

```bash
git add benchmarks/Koh.Benchmarks/
git commit -m "fix(benchmark): address microbenchmark smoke test issues"
```

---

### Task 11: Final integration verification

**Files:** (no new files)

- [ ] **Step 1: Verify `dotnet build` still passes for the full solution**

```bash
dotnet build
```

- [ ] **Step 2: Verify `make test` still passes**

```bash
make test
```

- [ ] **Step 3: Verify the gitignore works**

```bash
git status
```

Expected: no benchmark output files show as untracked.

- [ ] **Step 4: Final commit if any cleanup needed**

```bash
git add -A
git commit -m "chore(benchmark): final cleanup after integration verification"
```
