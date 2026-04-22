#!/usr/bin/env bash
# Build + install the Koh VS Code extension locally against a dev
# toolchain. See install-vscode-extension.ps1 for the narrative; this
# is the POSIX port with RID + canonical-path auto-selection.
#
# Lays out binaries at the same per-user canonical path the installer
# and the extension's auto-installer use:
#   Linux  : $XDG_DATA_HOME/koh/toolchain   (default ~/.local/share)
#   macOS  : ~/Library/Application Support/Koh/toolchain
#   Windows: %LOCALAPPDATA%/Koh/toolchain   (git-bash / WSL users)

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
    Linux*)
        rid=linux-x64
        toolchain_root="${XDG_DATA_HOME:-$HOME/.local/share}/koh/toolchain"
        ;;
    Darwin*)
        rid=$([[ "$(uname -m)" == arm64 ]] && echo osx-arm64 || echo osx-x64)
        toolchain_root="$HOME/Library/Application Support/Koh/toolchain"
        ;;
    MINGW*|MSYS*|CYGWIN*)
        rid=win-x64
        toolchain_root="${LOCALAPPDATA:-$HOME/AppData/Local}/Koh/toolchain"
        ;;
    *)
        echo "unsupported OS: $(uname -s)" >&2
        exit 1
        ;;
esac

dev_version=dev
dev_bin="$toolchain_root/$dev_version/bin"
mkdir -p "$dev_bin"

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

echo "── Publishing toolchain into $dev_bin ($rid) ──"
dotnet publish src/Koh.Lsp          -c Release -r "$rid" --self-contained -o "$dev_bin"
dotnet publish src/Koh.Asm          -c Release -r "$rid" -o "$dev_bin"
dotnet publish src/Koh.Link         -c Release -r "$rid" -o "$dev_bin"
dotnet publish src/Koh.Emulator.App -c Release -r "$rid" -o "$dev_bin"

cat > "$toolchain_root/$dev_version/version.json" <<EOF
{"version":"$dev_version","rid":"$rid","installedAt":""}
EOF
printf '%s' "$dev_version" > "$toolchain_root/current"

cd "$ext_dir"

if [[ ! -d node_modules ]]; then
    echo "── npm ci ──"
    npm ci
fi

echo "── tsc compile ──"
npm run compile

echo "── vsce package ──"
rm -f ./*.vsix
npx vsce package

vsix=$(ls -t ./*.vsix 2>/dev/null | head -n1)
[[ -f "$vsix" ]] || { echo "vsce package produced no .vsix" >&2; exit 1; }

echo "── code --install-extension $vsix ──"
"$CODE_CLI" --install-extension "$vsix" --force

echo
echo "Installed: $(basename "$vsix")"
echo "Dev toolchain at: $dev_bin"
echo "Reload any open VS Code windows for the new build to take effect."
