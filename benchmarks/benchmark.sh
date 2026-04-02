#!/usr/bin/env bash
set -euo pipefail

# ===========================================================================
# KOH vs RGBDS Benchmark — Host-Side Orchestrator
#
# Publishes KOH, builds Docker image, runs benchmark container, prints results.
# ===========================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -W 2>/dev/null || pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -W 2>/dev/null || pwd)"

BENCH_IMAGE="${BENCH_IMAGE:-koh-benchmark}"
BENCH_OUTPUT="${BENCH_OUTPUT:-$SCRIPT_DIR/results}"
BENCH_CPUSET="${BENCH_CPUSET:-}"

# ---------------------------------------------------------------------------
# Prerequisites
# ---------------------------------------------------------------------------
echo "[benchmark] Checking prerequisites..."

if ! command -v docker &>/dev/null; then
  echo "[benchmark] ERROR: docker not in PATH" >&2; exit 2
fi
echo "[benchmark] Docker: $(docker --version)"

if ! command -v dotnet &>/dev/null; then
  echo "[benchmark] ERROR: dotnet not in PATH" >&2; exit 2
fi
DOTNET_SDK_VERSION=$(dotnet --version)
echo "[benchmark] .NET SDK: $DOTNET_SDK_VERSION"

# ---------------------------------------------------------------------------
# Publish KOH (separate subdirs to avoid output collisions)
# ---------------------------------------------------------------------------
echo "[benchmark] Publishing KOH for linux-x64..."
PUBLISH_DIR="$SCRIPT_DIR/.publish"
rm -rf "$PUBLISH_DIR"

dotnet publish "$REPO_ROOT/src/Koh.Asm/Koh.Asm.csproj" \
  -c Release -r linux-x64 --no-self-contained \
  -o "$PUBLISH_DIR/koh-asm" --verbosity quiet

dotnet publish "$REPO_ROOT/src/Koh.Link/Koh.Link.csproj" \
  -c Release -r linux-x64 --no-self-contained \
  -o "$PUBLISH_DIR/koh-link" --verbosity quiet

# Normalize executable names to what the container harness expects
# dotnet publish produces "Koh.Asm" and "Koh.Link" as native executables
if [[ -f "$PUBLISH_DIR/koh-asm/Koh.Asm" ]] && [[ ! -f "$PUBLISH_DIR/koh-asm/koh-asm" ]]; then
  mv "$PUBLISH_DIR/koh-asm/Koh.Asm" "$PUBLISH_DIR/koh-asm/koh-asm"
fi
if [[ -f "$PUBLISH_DIR/koh-link/Koh.Link" ]] && [[ ! -f "$PUBLISH_DIR/koh-link/koh-link" ]]; then
  mv "$PUBLISH_DIR/koh-link/Koh.Link" "$PUBLISH_DIR/koh-link/koh-link"
fi

# Verify the expected binaries exist
[[ -f "$PUBLISH_DIR/koh-asm/koh-asm" ]] || { echo "[benchmark] ERROR: koh-asm binary not found after publish" >&2; exit 2; }
[[ -f "$PUBLISH_DIR/koh-link/koh-link" ]] || { echo "[benchmark] ERROR: koh-link binary not found after publish" >&2; exit 2; }

echo "[benchmark] Published to $PUBLISH_DIR"

# ---------------------------------------------------------------------------
# Build Docker image
# ---------------------------------------------------------------------------
echo "[benchmark] Building Docker image '$BENCH_IMAGE'..."
docker build -t "$BENCH_IMAGE" "$SCRIPT_DIR"

# ---------------------------------------------------------------------------
# Run benchmark container
# ---------------------------------------------------------------------------
echo "[benchmark] Running benchmark..."
mkdir -p "$BENCH_OUTPUT"
rm -rf "${BENCH_OUTPUT:?}"/*

DOCKER_ARGS=(
  --rm
  -v "$PUBLISH_DIR/koh-asm:/opt/koh/koh-asm:ro"
  -v "$PUBLISH_DIR/koh-link:/opt/koh/koh-link:ro"
  -v "$BENCH_OUTPUT:/results"
  -e "BENCH_RUNS=${BENCH_RUNS:-5}"
  -e "BENCH_WARMUP=${BENCH_WARMUP:-2}"
  -e "BENCH_STRICT=${BENCH_STRICT:-0}"
  -e "BENCH_COVERAGE=${BENCH_COVERAGE:-0}"
  -e "DOTNET_SDK_HOST=$DOTNET_SDK_VERSION"
)
[[ -n "${POKECRYSTAL_REF:-}" ]] && DOCKER_ARGS+=(-e "POKECRYSTAL_REF=$POKECRYSTAL_REF")
[[ -n "$BENCH_CPUSET" ]] && DOCKER_ARGS+=(--cpuset-cpus "$BENCH_CPUSET")

docker run "${DOCKER_ARGS[@]}" "$BENCH_IMAGE"
exit_code=$?

# ---------------------------------------------------------------------------
# Print summary
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
