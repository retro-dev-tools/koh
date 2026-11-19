#!/usr/bin/env bash
# Build and launch the desktop.

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"

case "$(uname -s)" in
    Linux*)    rid=$([[ "$(uname -m)" == aarch64 ]] && echo linux-arm64 || echo linux-x64) ;;
    Darwin*)   rid=$([[ "$(uname -m)" == arm64 ]] && echo osx-arm64 || echo osx-x64) ;;
    MINGW*|MSYS*|CYGWIN*) rid=win-x64 ;;
    *)         rid=linux-x64 ;;
esac

# See scripts/install-vscode-extension.sh for the story — vcvarsall.bat
# calls bare `vswhere.exe` so the VS Installer dir must be on PATH.
if [[ "$rid" == win-x64 ]]; then
    vs_installer="${PROGRAMFILES_X86:-/c/Program Files (x86)}/Microsoft Visual Studio/Installer"
    [[ -x "$vs_installer/vswhere.exe" ]] && PATH="$vs_installer:$PATH"
fi

publish_dir="$repo_root/src/Emulator/Koh.Emulator.App/bin/Release/net10.0/$rid/publish"
exe_name="koh-emulator"
[[ "$rid" == win-* ]] && exe_name="koh-emulator.exe"
exe="$publish_dir/$exe_name"

cd "$repo_root"
dotnet msbuild build.proj -t:PublishEmulatorApp "-p:EmulatorRuntimeIdentifier=$rid"
[[ -x "$exe" ]] || { echo "publish output missing: $exe" >&2; exit 1; }
"$exe" "$@"
