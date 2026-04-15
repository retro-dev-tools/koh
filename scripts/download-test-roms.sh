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
    "464e14b7d42e7feea0b7ede42be7071dc88913f75b9ffa444299424b63d1dff1"

download_with_hash \
    "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2.gbc" \
    "$OUTPUT_DIR/cgb-acid2.gbc" \
    "d24d6f38478f05567cccb96015b1479b0da6d2f70ef8966896ef0b10cd3062cf"

download_with_hash \
    "https://raw.githubusercontent.com/mattcurrie/dmg-acid2/master/img/reference-dmg.png" \
    "$REFERENCE_DIR/dmg-acid2.png" \
    "ca966d50895c7efef05838590d148c2cbfd7fba57dab986f25b35b4da71abb57"

download_with_hash \
    "https://raw.githubusercontent.com/mattcurrie/cgb-acid2/master/img/reference.png" \
    "$REFERENCE_DIR/cgb-acid2.png" \
    "9ea9c262c5383353e77d715d021a0f7c5ccbe438f88082cb225756e50c4fdf01"

# Phase 3: Blargg test suite zips (extracted into blargg/ subtree).
BLARGG_DIR="$OUTPUT_DIR/blargg"
mkdir -p "$BLARGG_DIR"

fetch_blargg_zip() {
    local zipname="$1"
    local url="https://gbdev.gg8.se/files/roms/blargg-gb-tests/$zipname.zip"
    if [ ! -d "$BLARGG_DIR/$zipname" ] && [ ! -f "$BLARGG_DIR/$zipname.gb" ]; then
        echo "DL  $zipname"
        curl -fsSL -o "$BLARGG_DIR/$zipname.zip" "$url"
        (cd "$BLARGG_DIR" && unzip -oq "$zipname.zip" && rm -f "$zipname.zip")
    else
        echo "OK  $zipname"
    fi
}

fetch_blargg_zip cpu_instrs
fetch_blargg_zip instr_timing
fetch_blargg_zip mem_timing
fetch_blargg_zip mem_timing-2
fetch_blargg_zip halt_bug
fetch_blargg_zip interrupt_time

# Phase 3: Mooneye test suite — used for acceptance/{bits,timer,interrupts,oam_dma}.
MOONEYE_DIR="$OUTPUT_DIR/mooneye"
MOONEYE_URL="https://gekkio.fi/files/mooneye-test-suite/mts-20240127-1204-74ae166/mts-20240127-1204-74ae166.tar.xz"
if [ ! -d "$MOONEYE_DIR/mts-20240127-1204-74ae166" ]; then
    mkdir -p "$MOONEYE_DIR"
    echo "DL  mooneye-test-suite"
    curl -fsSL -o "$MOONEYE_DIR/mts.tar.xz" "$MOONEYE_URL"
    (cd "$MOONEYE_DIR" && tar -xJf mts.tar.xz && rm -f mts.tar.xz)
else
    echo "OK  mooneye-test-suite"
fi

# Phase 4 will add: Blargg dmg_sound.

echo "download-test-roms: acid2 + Blargg + Mooneye fixtures ready under $OUTPUT_DIR"
