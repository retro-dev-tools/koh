#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build
ROOT="$(git rev-parse --show-toplevel)"
cargo run --manifest-path "$ROOT/Cargo.toml" --release --package koh-asm --locked -- src/main.asm -o build/2048.kobj
dotnet run --project "$ROOT/src/Link/Koh.Link" -- build/2048.kobj \
  -o build/2048.gbc \
  -n build/2048.sym \
  -d build/2048.kdbg
echo "Built build/2048.gbc"
