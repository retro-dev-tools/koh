#!/usr/bin/env bash
# Build + install the Koh VS Code extension locally. See the
# PowerShell counterpart for the chain of steps; this is the POSIX
# port with macOS / Linux RID auto-selection.

set -euo pipefail

require() { command -v "$1" >/dev/null 2>&1 || { echo "$1 not found on PATH" >&2; exit 1; }; }
require dotnet
require npm
require code

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"
ext_dir="$repo_root/editors/vscode"

case "$(uname -s)" in
    Linux*)              rid=linux-x64 ;;
    Darwin*)             rid=$([[ "$(uname -m)" == arm64 ]] && echo osx-arm64 || echo osx-x64) ;;
    MINGW*|MSYS*|CYGWIN*) rid=win-x64 ;;
    *)                   rid=linux-x64 ;;
esac

cd "$repo_root"

echo "── Publishing koh-lsp ──"
dotnet publish src/Koh.Lsp -c Release -o "$ext_dir/server"

echo "── Publishing Koh.Emulator.App (NativeAOT, $rid) ──"
dotnet publish src/Koh.Emulator.App -c Release -r "$rid"

cd "$ext_dir"

if [[ ! -d node_modules ]]; then
    echo "── npm ci ──"
    npm ci
fi

echo "── tsc compile ──"
npm run compile

echo "── vsce package ──"
rm -f ./*.vsix
npx vsce package --no-dependencies

vsix=$(ls -t ./*.vsix 2>/dev/null | head -n1)
[[ -f "$vsix" ]] || { echo "vsce package produced no .vsix" >&2; exit 1; }

echo "── code --install-extension $vsix ──"
code --install-extension "$vsix" --force

exe_name="Koh.Emulator.App"
[[ "$rid" == win-* ]] && exe_name="Koh.Emulator.App.exe"
emu_exe="$repo_root/src/Koh.Emulator.App/bin/Release/net10.0/$rid/publish/$exe_name"

echo
echo "Installed: $(basename "$vsix")"
echo "Reload any open VS Code windows for the new build to take effect."
echo
echo "Set this in your VS Code settings so F5 can find the emulator:"
echo "  \"koh.emulator.exePath\": \"$emu_exe\""
