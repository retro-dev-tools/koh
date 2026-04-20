#!/usr/bin/env bash
# Build + install the Koh VS Code extension locally. See the
# PowerShell counterpart for the chain of steps; this is the POSIX
# port with macOS / Linux RID auto-selection.

set -euo pipefail

require() { command -v "$1" >/dev/null 2>&1 || { echo "$1 not found on PATH" >&2; exit 1; }; }
require dotnet
require npm

# VS Code CLI is called `code` everywhere except Windows-native
# installs, where the PATH often has the GUI `code.exe` ahead of
# the CLI `code.cmd` wrapper (git-bash resolves `code` to the
# former). Prefer the explicit .cmd when present.
CODE_CLI=code
if [[ -n "${LOCALAPPDATA:-}" ]]; then
    cand="$LOCALAPPDATA/Programs/Microsoft VS Code/bin/code.cmd"
    [[ -x "$cand" ]] && CODE_CLI="$cand"
fi
command -v "$CODE_CLI" >/dev/null 2>&1 || { echo "VS Code CLI not found (looked for '$CODE_CLI')" >&2; exit 1; }

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"
ext_dir="$repo_root/editors/vscode"

case "$(uname -s)" in
    Linux*)              rid=linux-x64 ;;
    Darwin*)             rid=$([[ "$(uname -m)" == arm64 ]] && echo osx-arm64 || echo osx-x64) ;;
    MINGW*|MSYS*|CYGWIN*) rid=win-x64 ;;
    *)                   rid=linux-x64 ;;
esac

# NativeAOT on Windows links via MSVC. The MSBuild target runs
# vcvarsall.bat, which in turn invokes `vswhere.exe` (bare name)
# to locate the VS install — so vswhere has to be reachable on
# PATH. It ships in the VS Installer directory, which is rarely
# on a default PATH. Prepend it so vcvarsall.bat finds it cleanly;
# otherwise the "vswhere.exe is not recognized" stderr leaks into
# _FindVCVarsallOutput and corrupts the CppLinker path the target
# hands back to the linker invocation.
if [[ "$rid" == win-x64 ]]; then
    vs_installer="${PROGRAMFILES_X86:-/c/Program Files (x86)}/Microsoft Visual Studio/Installer"
    [[ -x "$vs_installer/vswhere.exe" ]] && PATH="$vs_installer:$PATH"
fi

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
"$CODE_CLI" --install-extension "$vsix" --force

exe_name="Koh.Emulator.App"
[[ "$rid" == win-* ]] && exe_name="Koh.Emulator.App.exe"
emu_exe="$repo_root/src/Koh.Emulator.App/bin/Release/net10.0/$rid/publish/$exe_name"

# Convert MSYS / git-bash POSIX paths (/c/foo) to Windows form
# (C:/foo) so the output is copy-pasteable into VS Code's
# settings.json on Windows hosts.
if [[ "$rid" == win-* ]] && command -v cygpath >/dev/null 2>&1; then
    emu_exe=$(cygpath -m "$emu_exe")
fi

echo
echo "Installed: $(basename "$vsix")"
echo "Reload any open VS Code windows for the new build to take effect."
echo
echo "Set this in your VS Code settings so F5 can find the emulator:"
echo "  \"koh.emulator.exePath\": \"$emu_exe\""
