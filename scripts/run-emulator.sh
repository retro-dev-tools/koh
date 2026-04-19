#!/usr/bin/env bash
# Publish the Koh emulator (KohUI + GLFW + OpenAL, NativeAOT) in Release
# and launch it. Optional ROM path as arg 1; omitted → the emulator
# auto-discovers a default ROM per its FindDefaultRom logic.

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"

case "$(uname -s)" in
    Linux*)    rid=linux-x64 ;;
    Darwin*)   rid=osx-x64 ;;
    MINGW*|MSYS*|CYGWIN*) rid=win-x64 ;;
    *)         rid=linux-x64 ;;
esac

publish_dir="$repo_root/src/Koh.Emulator.App/bin/Release/net10.0/$rid/publish"
exe_name="Koh.Emulator.App"
[[ "$rid" == win-* ]] && exe_name="Koh.Emulator.App.exe"
exe="$publish_dir/$exe_name"

cd "$repo_root"
dotnet publish src/Koh.Emulator.App -c Release -r "$rid"
[[ -x "$exe" ]] || { echo "publish output missing: $exe" >&2; exit 1; }
"$exe" "$@"
