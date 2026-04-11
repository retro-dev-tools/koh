#!/usr/bin/env bash
# Downloads test ROM fixtures with SHA-256 verification.
# Used by CI and by local developers before running compatibility tests.

set -euo pipefail

OUTPUT_DIR="${1:-tests/fixtures/test-roms}"
mkdir -p "$OUTPUT_DIR"

# Phase 0 placeholder — no ROMs to download yet.
# Phase 2 adds: dmg-acid2, cgb-acid2
# Phase 3 adds: Blargg cpu_instrs, instr_timing, mem_timing, mem_timing-2,
#               halt_bug, interrupt_time; Mooneye acceptance/
# Phase 4 adds: Blargg dmg_sound

echo "download-test-roms: no ROMs configured for the current phase"
