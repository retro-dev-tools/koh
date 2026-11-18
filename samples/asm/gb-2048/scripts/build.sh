#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build
ROOT="$(git rev-parse --show-toplevel)"
cargo run --manifest-path "$ROOT/Cargo.toml" --release --package koh-asm --locked -- src/main.asm -o build/2048.o
cargo run --manifest-path "$ROOT/Cargo.toml" --release --package koh-link --locked -- build/2048.o \
  -o build/2048.gbc \
  -n build/2048.sym \
  -d build/2048.elf
echo "Built build/2048.gbc"
