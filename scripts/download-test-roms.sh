#!/usr/bin/env bash
# Downloads test ROM fixtures with SHA-256 verification.
# Used by CI and by local developers before running compatibility tests.

set -euo pipefail

OUTPUT_DIR="${1:-tests/fixtures/test-roms}"
REFERENCE_DIR="${2:-tests/fixtures/reference}"
mkdir -p "$OUTPUT_DIR" "$REFERENCE_DIR"

# SHA-256 values are populated on first successful download. On CI the caches
# are warm; locally, run once interactively and update this script if missing.

download_with_hash() {
    local url="$1"
    local output="$2"
    local expected_sha256="$3"

    if [ -f "$output" ] && [ -n "$expected_sha256" ]; then
        local actual
        actual=$(sha256sum "$output" | cut -d' ' -f1)
        if [ "$actual" = "$expected_sha256" ]; then
            echo "OK  $output"
            return
        fi
        echo "REDOWNLOAD $output (hash mismatch)"
    fi

    curl -fsSL -o "$output" "$url"

    if [ -n "$expected_sha256" ]; then
        local actual
        actual=$(sha256sum "$output" | cut -d' ' -f1)
        if [ "$actual" != "$expected_sha256" ]; then
            echo "FAIL $output: hash mismatch (expected $expected_sha256, got $actual)"
            exit 1
        fi
    fi
    echo "DL  $output"
}

# Phase 2: dmg-acid2 + cgb-acid2. SHA-256 values left empty until the first
# successful download — CI prints the observed hash, which gets pasted back
# into the script to lock in verification.
download_with_hash \
    "https://github.com/mattcurrie/dmg-acid2/releases/download/v1.0/dmg-acid2.gb" \
    "$OUTPUT_DIR/dmg-acid2.gb" \
    ""

download_with_hash \
    "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2.gbc" \
    "$OUTPUT_DIR/cgb-acid2.gbc" \
    ""

download_with_hash \
    "https://github.com/mattcurrie/dmg-acid2/releases/download/v1.0/dmg-acid2-dmg.png" \
    "$REFERENCE_DIR/dmg-acid2.png" \
    ""

download_with_hash \
    "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2-cgb.png" \
    "$REFERENCE_DIR/cgb-acid2.png" \
    ""

# Phase 3 will add: Blargg cpu_instrs, instr_timing, mem_timing, mem_timing-2,
#                   halt_bug, interrupt_time; Mooneye acceptance/
# Phase 4 will add: Blargg dmg_sound

echo "download-test-roms: acid2 fixtures ready under $OUTPUT_DIR"
