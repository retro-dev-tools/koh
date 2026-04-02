#!/usr/bin/env bash
set -euo pipefail

# ===========================================================================
# KOH vs RGBDS Benchmark Harness (runs inside Docker container)
#
# Writes flat key=value text files. ALL JSON/markdown generation is
# delegated to generate-reports.py.
# ===========================================================================

# ---------------------------------------------------------------------------
# Configuration
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

# KOH capability matrix — update these arrays when KOH implements new flags
KOH_ASM_SUPPORTED_FEATURES=()
KOH_LINK_SUPPORTED_FEATURES=()
KOH_ASM_REQUIRED_FEATURES=("-P" "-Q")
KOH_LINK_REQUIRED_FEATURES=("-l")

# Derived state (set during pre-flight)
KOH_ASM_SUPPORTED=false
KOH_LINK_SUPPORTED=false
KOH_ASM_CLASSIFICATION=""
KOH_LINK_CLASSIFICATION=""
KOH_ASM_REASON=""
KOH_LINK_REASON=""

# Exit tracking
HIGHEST_EXIT=0

# ---------------------------------------------------------------------------
# Functions
# ---------------------------------------------------------------------------
log() { echo "[benchmark] $*"; }
die() { echo "[benchmark] ERROR: $*" >&2; exit 2; }

has_feature() {
  local needle=$1; shift
  local item
  for item in "$@"; do
    if [[ "$item" == "$needle" ]]; then return 0; fi
  done
  return 1
}

all_required_present() {
  # Usage: all_required_present "required_array_content" supported_item1 supported_item2 ...
  local required_csv=$1; shift
  local req
  for req in $required_csv; do
    if ! has_feature "$req" "$@"; then return 1; fi
  done
  return 0
}

clean_outputs() {
  local asm
  for asm in "${ASM_FILES[@]}"; do
    rm -f "${asm}.o" "${asm}.koh.o"
  done
  rm -f pokecrystal.gbc pokecrystal.koh.gbc pokecrystal.sym pokecrystal.map
}

# Parse /usr/bin/time -v output and append key=value lines to a file
# Usage: parse_time_output <time_log> <output_file> <prefix>
parse_time_output() {
  local logfile=$1
  local outfile=$2
  local prefix=$3
  local wall user_cpu sys_cpu peak_rss cpu

  wall=$(grep "Elapsed (wall clock)" "$logfile" | sed 's/.*: //' \
    | awk -F: '{if(NF==3) printf "%.2f", $1*3600+$2*60+$3; else if(NF==2) printf "%.2f", $1*60+$2; else printf "%.2f", $1}')
  user_cpu=$(grep "User time" "$logfile" | sed 's/.*: //')
  sys_cpu=$(grep "System time" "$logfile" | sed 's/.*: //')
  peak_rss=$(grep "Maximum resident" "$logfile" | sed 's/.*: //')
  cpu=$(awk "BEGIN {printf \"%.2f\", $user_cpu + $sys_cpu}")

  {
    echo "${prefix}.wall_s=$wall"
    echo "${prefix}.user_cpu_s=$user_cpu"
    echo "${prefix}.sys_cpu_s=$sys_cpu"
    echo "${prefix}.cpu_s=$cpu"
    echo "${prefix}.peak_rss_kb=$peak_rss"
  } >> "$outfile"
}

# Phase execution functions — each wraps the tool in /usr/bin/time -v
run_rgbds_assembly() {
  local logfile=$1
  /usr/bin/time -v bash -c '
    set -e
    for asm in '"$(printf '%s ' "${ASM_FILES[@]}")"'; do
      rgbasm -Q8 -P includes.asm -Weverything -Wtruncation=1 -o "${asm}.o" "${asm}.asm"
    done
  ' >"$logfile" 2>&1
}

run_koh_assembly() {
  local logfile=$1
  /usr/bin/time -v bash -c '
    set -e
    for asm in '"$(printf '%s ' "${ASM_FILES[@]}")"'; do
      /opt/koh/koh-asm/koh-asm "${asm}.asm" -o "${asm}.koh.o" -f rgbds
    done
  ' >"$logfile" 2>&1
}

run_rgbds_linking() {
  local logfile=$1
  local obj_files=""
  local asm
  for asm in "${ASM_FILES[@]}"; do obj_files+=" ${asm}.o"; done
  /usr/bin/time -v bash -c "
    set -e
    rgblink -Weverything -Wtruncation=1 -l layout.link \
      -n pokecrystal.sym -m pokecrystal.map -o pokecrystal.gbc $obj_files
  " >"$logfile" 2>&1
}

run_koh_linking() {
  local logfile=$1
  local obj_files=""
  local asm
  for asm in "${ASM_FILES[@]}"; do obj_files+=" ${asm}.koh.o"; done
  /usr/bin/time -v bash -c "
    set -e
    /opt/koh/koh-link/koh-link -o pokecrystal.koh.gbc $obj_files
  " >"$logfile" 2>&1
}

# Write an unsupported/skipped phase entry to a run file
write_phase_status() {
  local outfile=$1
  local prefix=$2
  local status=$3
  local classification=$4
  local reason=$5
  {
    echo "${prefix}.status=$status"
    echo "${prefix}.classification=$classification"
    echo "${prefix}.reason=$reason"
  } >> "$outfile"
}

# ===========================================================================
# Step 1: Record environment
# ===========================================================================
log "Recording environment..."
mkdir -p "$BENCH_OUTPUT/logs" "$BENCH_OUTPUT/run-data"

ENV_CPU=$(grep 'model name' /proc/cpuinfo | head -1 | sed 's/.*: //')
ENV_KERNEL=$(uname -r)
ENV_CONTAINER_OS=$(grep PRETTY_NAME /etc/os-release | sed 's/PRETTY_NAME="//' | sed 's/"//')
ENV_RAM_MB=$(awk '/MemTotal/ {printf "%.0f", $2/1024}' /proc/meminfo)
ENV_DOTNET=$(dotnet --version 2>/dev/null || echo "unknown")
ENV_RGBDS=$(rgbasm --version 2>&1 | head -1)
ENV_KOH=$(/opt/koh/koh-asm/koh-asm --version 2>&1 | head -1 || echo "unknown")
ENV_SDK_HOST="${DOTNET_SDK_HOST:-unknown}"

cat > "$BENCH_OUTPUT/environment.txt" <<EOF
cpu_model=$ENV_CPU
kernel=$ENV_KERNEL
container_os=$ENV_CONTAINER_OS
ram_mb=$ENV_RAM_MB
dotnet_runtime=$ENV_DOTNET
dotnet_sdk_host=$ENV_SDK_HOST
rgbds_version=$ENV_RGBDS
koh_version=$ENV_KOH
EOF

log "CPU: $ENV_CPU"
log "Kernel: $ENV_KERNEL"
log ".NET Runtime: $ENV_DOTNET"
log "RGBDS: $ENV_RGBDS"
log "KOH: $ENV_KOH"

# ===========================================================================
# Step 2: Clone pokecrystal
# ===========================================================================
log "Cloning pokecrystal @ $POKECRYSTAL_REF..."
if [[ ! -d /work/pokecrystal ]]; then
  git clone https://github.com/pret/pokecrystal.git /work/pokecrystal 2>&1 | tail -3
fi
cd /work/pokecrystal
git checkout "$POKECRYSTAL_REF" --quiet 2>/dev/null || git fetch --depth=50 origin "$POKECRYSTAL_REF" && git checkout "$POKECRYSTAL_REF" --quiet

# ===========================================================================
# Step 3: Build prerequisites (not measured)
# ===========================================================================
log "Building pokecrystal tools..."
make -C tools/ -j"$(nproc)" 2>&1 | tail -5

log "Building pokecrystal (generates graphics assets)..."
make -j"$(nproc)" 2>&1 | tail -10

# Validate critical assets exist
if [[ ! -f includes.asm ]]; then
  die "includes.asm not found — pokecrystal setup failed"
fi
# Check a representative INCBIN-referenced asset
if ! find gfx -name "*.2bpp" | head -1 | grep -q .; then
  die "No .2bpp files found — graphics asset generation failed"
fi
log "Prerequisites built and validated"

# ===========================================================================
# Step 4: Pre-flight classification
# ===========================================================================
log "Pre-flight classification..."

if all_required_present "${KOH_ASM_REQUIRED_FEATURES[*]}" "${KOH_ASM_SUPPORTED_FEATURES[@]+"${KOH_ASM_SUPPORTED_FEATURES[@]}"}"; then
  KOH_ASM_SUPPORTED=true
  KOH_ASM_CLASSIFICATION="supported"
else
  KOH_ASM_CLASSIFICATION="unsupported_cli_feature"
  missing=""
  for req in "${KOH_ASM_REQUIRED_FEATURES[@]}"; do
    if ! has_feature "$req" "${KOH_ASM_SUPPORTED_FEATURES[@]+"${KOH_ASM_SUPPORTED_FEATURES[@]}"}"; then
      missing+="$req "
    fi
  done
  KOH_ASM_REASON="missing ${missing}CLI flags"
fi

if all_required_present "${KOH_LINK_REQUIRED_FEATURES[*]}" "${KOH_LINK_SUPPORTED_FEATURES[@]+"${KOH_LINK_SUPPORTED_FEATURES[@]}"}"; then
  KOH_LINK_SUPPORTED=true
  KOH_LINK_CLASSIFICATION="supported"
else
  KOH_LINK_CLASSIFICATION="unsupported_benchmark"
  KOH_LINK_REASON="missing -l (linker script) support; fair comparison not possible"
fi

log "KOH assembly: $KOH_ASM_CLASSIFICATION"
log "KOH linking: $KOH_LINK_CLASSIFICATION"

# Capture --help for auditing
/opt/koh/koh-asm/koh-asm --help > "$BENCH_OUTPUT/logs/koh-asm-help.txt" 2>&1 || true
/opt/koh/koh-link/koh-link --help > "$BENCH_OUTPUT/logs/koh-link-help.txt" 2>&1 || true

# Write preflight as flat key=value
cat > "$BENCH_OUTPUT/preflight.txt" <<EOF
koh_asm_supported=$KOH_ASM_SUPPORTED
koh_asm_classification=$KOH_ASM_CLASSIFICATION
koh_asm_reason=$KOH_ASM_REASON
koh_asm_required=$(IFS=,; echo "${KOH_ASM_REQUIRED_FEATURES[*]}")
koh_asm_have=$(IFS=,; echo "${KOH_ASM_SUPPORTED_FEATURES[*]+"${KOH_ASM_SUPPORTED_FEATURES[*]}"}")
koh_link_supported=$KOH_LINK_SUPPORTED
koh_link_classification=$KOH_LINK_CLASSIFICATION
koh_link_reason=$KOH_LINK_REASON
koh_link_required=$(IFS=,; echo "${KOH_LINK_REQUIRED_FEATURES[*]}")
koh_link_have=$(IFS=,; echo "${KOH_LINK_SUPPORTED_FEATURES[*]+"${KOH_LINK_SUPPORTED_FEATURES[*]}"}")
EOF

# ===========================================================================
# Step 5: Warmup (mirrors measured execution)
# ===========================================================================
log "Running $BENCH_WARMUP warmup iterations..."
for (( w=1; w<=BENCH_WARMUP; w++ )); do
  # RGBDS: assembly then linking
  clean_outputs
  run_rgbds_assembly /dev/null 2>/dev/null || true
  run_rgbds_linking /dev/null 2>/dev/null || true

  # KOH: assembly then linking (same dependency chain)
  if $KOH_ASM_SUPPORTED; then
    clean_outputs
    run_koh_assembly /dev/null 2>/dev/null || true
    if $KOH_LINK_SUPPORTED; then
      run_koh_linking /dev/null 2>/dev/null || true
    fi
  fi
done

# ===========================================================================
# Step 6: Measured runs
# ===========================================================================
log "Running $BENCH_RUNS measured iterations..."
TIMESTAMP=$(date -u +%Y-%m-%dT%H:%M:%SZ)

# Assembly phase command templates (for recording in run data)
RGBDS_ASM_CMD_TEMPLATE="rgbasm -Q8 -P includes.asm -Weverything -Wtruncation=1 -o \${asm}.o \${asm}.asm"
KOH_ASM_CMD_TEMPLATE="/opt/koh/koh-asm/koh-asm \${asm}.asm -o \${asm}.koh.o -f rgbds"
ASM_FILES_CSV=$(IFS=,; echo "${ASM_FILES[*]}")

for (( run=1; run<=BENCH_RUNS; run++ )); do
  log "  Run $run/$BENCH_RUNS"
  clean_outputs
  run_file="$BENCH_OUTPUT/run-data/run-${run}.txt"
  echo "timestamp=$TIMESTAMP" > "$run_file"

  # --- RGBDS Assembly ---
  logfile="$BENCH_OUTPUT/logs/run-${run}-rgbds-assembly.log"
  rgbds_asm_exit=0
  run_rgbds_assembly "$logfile" || rgbds_asm_exit=$?

  if [[ $rgbds_asm_exit -eq 0 ]]; then
    {
      echo "rgbds_assembly.status=ok"
      echo "rgbds_assembly.measurement_tool=/usr/bin/time -v"
      echo "rgbds_assembly.phase_command=$RGBDS_ASM_CMD_TEMPLATE"
      echo "rgbds_assembly.files=$ASM_FILES_CSV"
      echo "rgbds_assembly.exit_code=0"
      echo "rgbds_assembly.contributed_to_aggregates=true"
      echo "rgbds_assembly.log_path=logs/run-${run}-rgbds-assembly.log"
    } >> "$run_file"
    parse_time_output "$logfile" "$run_file" "rgbds_assembly"
  else
    die "RGBDS assembly failed on run $run — broken setup. See $logfile"
  fi

  # --- KOH Assembly ---
  if ! $KOH_ASM_SUPPORTED; then
    write_phase_status "$run_file" "koh_assembly" "unsupported" "$KOH_ASM_CLASSIFICATION" "$KOH_ASM_REASON"
  else
    logfile="$BENCH_OUTPUT/logs/run-${run}-koh-assembly.log"
    koh_asm_exit=0
    run_koh_assembly "$logfile" || koh_asm_exit=$?

    if [[ $koh_asm_exit -eq 0 ]]; then
      {
        echo "koh_assembly.status=ok"
        echo "koh_assembly.measurement_tool=/usr/bin/time -v"
        echo "koh_assembly.phase_command=$KOH_ASM_CMD_TEMPLATE"
        echo "koh_assembly.files=$ASM_FILES_CSV"
        echo "koh_assembly.exit_code=0"
        echo "koh_assembly.contributed_to_aggregates=true"
        echo "koh_assembly.log_path=logs/run-${run}-koh-assembly.log"
      } >> "$run_file"
      parse_time_output "$logfile" "$run_file" "koh_assembly"
    else
      {
        echo "koh_assembly.status=failed"
        echo "koh_assembly.classification=assembly_failure"
        echo "koh_assembly.reason=koh-asm exited non-zero"
        echo "koh_assembly.exit_code=$koh_asm_exit"
        echo "koh_assembly.log_path=logs/run-${run}-koh-assembly.log"
      } >> "$run_file"
      HIGHEST_EXIT=1
    fi
  fi

  # --- RGBDS Linking ---
  logfile="$BENCH_OUTPUT/logs/run-${run}-rgbds-linking.log"
  rgbds_link_exit=0
  run_rgbds_linking "$logfile" || rgbds_link_exit=$?

  if [[ $rgbds_link_exit -eq 0 ]]; then
    rgbds_link_argv="rgblink -Weverything -Wtruncation=1 -l layout.link -n pokecrystal.sym -m pokecrystal.map -o pokecrystal.gbc"
    for asm in "${ASM_FILES[@]}"; do rgbds_link_argv+=" ${asm}.o"; done
    {
      echo "rgbds_linking.status=ok"
      echo "rgbds_linking.measurement_tool=/usr/bin/time -v"
      echo "rgbds_linking.phase_command=$rgbds_link_argv"
      echo "rgbds_linking.exit_code=0"
      echo "rgbds_linking.contributed_to_aggregates=true"
      echo "rgbds_linking.log_path=logs/run-${run}-rgbds-linking.log"
    } >> "$run_file"
    parse_time_output "$logfile" "$run_file" "rgbds_linking"
  else
    die "RGBDS linking failed on run $run — broken setup. See $logfile"
  fi

  # --- KOH Linking ---
  if ! $KOH_LINK_SUPPORTED; then
    write_phase_status "$run_file" "koh_linking" "unsupported" "$KOH_LINK_CLASSIFICATION" "$KOH_LINK_REASON"
  elif ! $KOH_ASM_SUPPORTED; then
    write_phase_status "$run_file" "koh_linking" "skipped" "skipped" "KOH assembly unsupported — no object files to link"
  else
    # Check if KOH assembly succeeded this run
    koh_asm_status_this_run=$(grep "^koh_assembly.status=" "$run_file" | tail -1 | cut -d= -f2)
    if [[ "$koh_asm_status_this_run" != "ok" ]]; then
      write_phase_status "$run_file" "koh_linking" "skipped" "skipped" "KOH assembly failed this run — no object files to link"
    else
      logfile="$BENCH_OUTPUT/logs/run-${run}-koh-linking.log"
      koh_link_exit=0
      run_koh_linking "$logfile" || koh_link_exit=$?

      if [[ $koh_link_exit -eq 0 ]]; then
        koh_link_argv="/opt/koh/koh-link/koh-link -o pokecrystal.koh.gbc"
        for asm in "${ASM_FILES[@]}"; do koh_link_argv+=" ${asm}.koh.o"; done
        {
          echo "koh_linking.status=ok"
          echo "koh_linking.measurement_tool=/usr/bin/time -v"
          echo "koh_linking.phase_command=$koh_link_argv"
          echo "koh_linking.exit_code=0"
          echo "koh_linking.contributed_to_aggregates=true"
          echo "koh_linking.log_path=logs/run-${run}-koh-linking.log"
        } >> "$run_file"
        parse_time_output "$logfile" "$run_file" "koh_linking"
      else
        {
          echo "koh_linking.status=failed"
          echo "koh_linking.classification=link_failure"
          echo "koh_linking.reason=koh-link exited non-zero"
          echo "koh_linking.exit_code=$koh_link_exit"
          echo "koh_linking.log_path=logs/run-${run}-koh-linking.log"
        } >> "$run_file"
        HIGHEST_EXIT=1
      fi
    fi
  fi
done

# ===========================================================================
# Step 7: ROM comparison (dedicated post-benchmark validation build)
# ===========================================================================
log "ROM comparison (dedicated validation build)..."
clean_outputs

RGBFIX_CMD=(rgbfix -Cjv -t PM_CRYSTAL -k 01 -l 0x33 -m MBC3+TIMER+RAM+BATTERY -r 3 -p 0 -i BYTE -n 0)
RGBFIX_CMD_STR="${RGBFIX_CMD[*]}"

# Fresh RGBDS build
run_rgbds_assembly /dev/null 2>/dev/null || die "RGBDS assembly failed during ROM comparison build"
run_rgbds_linking /dev/null 2>/dev/null || die "RGBDS linking failed during ROM comparison build"

# Fresh KOH build if supported
koh_produced_rom=false
if $KOH_ASM_SUPPORTED; then
  run_koh_assembly /dev/null 2>/dev/null || true
  if $KOH_LINK_SUPPORTED; then
    if run_koh_linking /dev/null 2>/dev/null; then
      koh_produced_rom=true
    fi
  fi
fi

# Write ROM comparison data
rom_file="$BENCH_OUTPUT/rom-data.txt"
echo "rgbfix_command=$RGBFIX_CMD_STR" > "$rom_file"

if [[ -f pokecrystal.gbc ]] && $koh_produced_rom && [[ -f pokecrystal.koh.gbc ]]; then
  log "Applying rgbfix to both ROMs..."

  rgbds_fix_exit=0
  "${RGBFIX_CMD[@]}" pokecrystal.gbc > "$BENCH_OUTPUT/logs/rgbfix-rgbds.log" 2>&1 || rgbds_fix_exit=$?
  echo "rgbfix_rgbds_exit=$rgbds_fix_exit" >> "$rom_file"
  echo "rgbfix_rgbds_log=logs/rgbfix-rgbds.log" >> "$rom_file"

  if [[ $rgbds_fix_exit -ne 0 ]]; then
    die "rgbfix rejected RGBDS ROM — broken setup"
  fi

  koh_fix_exit=0
  "${RGBFIX_CMD[@]}" pokecrystal.koh.gbc > "$BENCH_OUTPUT/logs/rgbfix-koh.log" 2>&1 || koh_fix_exit=$?
  echo "rgbfix_koh_exit=$koh_fix_exit" >> "$rom_file"
  echo "rgbfix_koh_log=logs/rgbfix-koh.log" >> "$rom_file"

  rgbds_sha=$(sha256sum pokecrystal.gbc | awk '{print $1}')
  rgbds_size=$(stat -c%s pokecrystal.gbc)
  echo "rgbds_sha256=$rgbds_sha" >> "$rom_file"
  echo "rgbds_size=$rgbds_size" >> "$rom_file"
  echo "rgbds_header_hex=$(xxd -l 336 -p pokecrystal.gbc | tr -d '\n')" >> "$rom_file"

  if [[ $koh_fix_exit -ne 0 ]]; then
    echo "status=rom_postprocess_failure" >> "$rom_file"
    echo "reason=rgbfix rejected KOH ROM" >> "$rom_file"
    HIGHEST_EXIT=1
  else
    koh_sha=$(sha256sum pokecrystal.koh.gbc | awk '{print $1}')
    koh_size=$(stat -c%s pokecrystal.koh.gbc)
    echo "koh_sha256=$koh_sha" >> "$rom_file"
    echo "koh_size=$koh_size" >> "$rom_file"
    echo "koh_header_hex=$(xxd -l 336 -p pokecrystal.koh.gbc | tr -d '\n')" >> "$rom_file"

    if [[ "$rgbds_sha" == "$koh_sha" ]]; then
      echo "status=match" >> "$rom_file"
    else
      echo "status=mismatch" >> "$rom_file"
      echo "reason=SHA256 differs" >> "$rom_file"
      first_diff=$(cmp -l pokecrystal.gbc pokecrystal.koh.gbc 2>/dev/null | head -1 | awk '{print $1}')
      echo "first_diff_offset=${first_diff:-}" >> "$rom_file"
      HIGHEST_EXIT=1
    fi
  fi
elif [[ -f pokecrystal.gbc ]]; then
  # Only RGBDS produced a ROM
  "${RGBFIX_CMD[@]}" pokecrystal.gbc > "$BENCH_OUTPUT/logs/rgbfix-rgbds.log" 2>&1 || true
  rgbds_sha=$(sha256sum pokecrystal.gbc | awk '{print $1}')
  rgbds_size=$(stat -c%s pokecrystal.gbc)
  {
    echo "status=skip"
    echo "reason=KOH did not produce a ROM"
    echo "rgbds_sha256=$rgbds_sha"
    echo "rgbds_size=$rgbds_size"
    echo "rgbfix_rgbds_exit=0"
    echo "rgbfix_rgbds_log=logs/rgbfix-rgbds.log"
  } >> "$rom_file"
else
  echo "status=skip" >> "$rom_file"
  echo "reason=No ROMs produced" >> "$rom_file"
fi

# ===========================================================================
# Step 8: Generate reports via Python
# ===========================================================================
log "Generating reports..."
export BENCH_OUTPUT BENCH_RUNS BENCH_WARMUP POKECRYSTAL_REF
export BENCH_CPUSET="${BENCH_CPUSET:-}"
python3 /usr/local/bin/generate-reports.py

log "Done. Results in $BENCH_OUTPUT/"

# ===========================================================================
# Exit code logic
# ===========================================================================
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
