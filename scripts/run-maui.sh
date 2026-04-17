#!/usr/bin/env bash
# Publish Koh.Emulator.Maui in Release and launch it.
#
# MAUI only has a Windows target in this repo — macCatalyst/Linux users will
# need Xcode or the Android workload and to extend the project's TargetFrameworks.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TFM="net10.0-windows10.0.19041.0"
RID="win-x64"
PUBLISH_DIR="$REPO_ROOT/src/Koh.Emulator.Maui/bin/Release/$TFM/$RID/publish"
EXE="$PUBLISH_DIR/Koh.Emulator.Maui.exe"

cd "$REPO_ROOT"
dotnet publish src/Koh.Emulator.Maui -f "$TFM" -r "$RID" -c Release

if [[ ! -f "$EXE" ]]; then
    echo "publish output missing: $EXE" >&2
    exit 1
fi

# On git-bash / Windows the published .exe runs directly.
"$EXE"
