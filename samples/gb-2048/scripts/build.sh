#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build
ROOT="$(git rev-parse --show-toplevel)"
dotnet run --project "$ROOT/src/Koh.Asm" -- src/main.asm -o build/2048.kobj
dotnet run --project "$ROOT/src/Koh.Link" -- build/2048.kobj \
  -o build/2048.gbc \
  -n build/2048.sym
echo "Built build/2048.gbc"
