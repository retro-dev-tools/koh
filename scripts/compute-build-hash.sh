#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

# Collect the file set (sorted for determinism).
FILES=$(find \
    src/Koh.Emulator.App \
    src/Koh.Emulator.Core \
    src/Koh.Debugger \
    src/Koh.Linker.Core \
    -type f \
    \( -name '*.cs' -o -name '*.razor' -o -name '*.csproj' -o -name '*.js' -o -name '*.html' -o -name '*.css' -o -name '*.json' \) \
    2>/dev/null | sort)

# SDK versions.
DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo 'unknown')

# Compute per-file hashes and concatenate.
HASH_INPUT=""
for f in $FILES; do
    H=$(sha256sum "$f" | cut -d' ' -f1)
    HASH_INPUT+="${H}  ${f}"$'\n'
done
HASH_INPUT+="dotnet:${DOTNET_VERSION}"$'\n'

# Final SHA-256.
echo -n "$HASH_INPUT" | sha256sum | cut -d' ' -f1
