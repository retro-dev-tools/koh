# Build + install the Koh VS Code extension locally against a dev
# toolchain. Does the full chain end-to-end so a fresh clone becomes
# a working install with one invocation:
#
#   1. Publish koh-lsp (self-contained), koh-asm, koh-link, and
#      Koh.Emulator.App into the canonical per-user toolchain path
#      (%LOCALAPPDATA%\Koh\toolchain\dev\bin). This mirrors what the
#      Windows installer would do — extension auto-discovers it.
#   2. Point %LOCALAPPDATA%\Koh\toolchain\current at the "dev" version.
#   3. npm ci + tsc → editors/vscode/out/
#   4. vsce package → editors/vscode/koh-asm-<version>.vsix
#   5. code --install-extension <vsix> --force
#
# Rerun after any toolchain or extension change to pick the rebuild
# up in VS Code.

$ErrorActionPreference = 'Stop'

function Require-Tool($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name not found on PATH. Install it and re-run."
    }
}

Require-Tool dotnet
Require-Tool npm
Require-Tool code

# NativeAOT on Windows links via MSVC. The MSBuild target runs
# vcvarsall.bat, which invokes bare `vswhere.exe` to locate the
# VS install. vswhere ships in the VS Installer dir which is
# rarely on PATH; when it isn't, the "not recognized" stderr
# leaks into the captured tools-dir string and corrupts the
# CppLinker path. Prepend the Installer dir for this process.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $vsInstaller 'vswhere.exe')) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

$repoRoot       = Resolve-Path (Join-Path $PSScriptRoot '..')
$extDir         = Join-Path $repoRoot 'editors/vscode'
$toolchainRoot  = Join-Path $env:LOCALAPPDATA 'Koh\toolchain'
$devVersion     = 'dev'
$devBin         = Join-Path $toolchainRoot "$devVersion\bin"

New-Item -ItemType Directory -Path $devBin -Force | Out-Null

Push-Location $repoRoot
try {
    Write-Host "─── Publishing toolchain into $devBin ───" -ForegroundColor Cyan

    dotnet publish src/Koh.Lsp -c Release -r win-x64 --self-contained -o $devBin
    if ($LASTEXITCODE -ne 0) { throw "koh-lsp publish failed ($LASTEXITCODE)" }

    dotnet publish src/Koh.Asm -c Release -r win-x64 -o $devBin
    if ($LASTEXITCODE -ne 0) { throw "koh-asm publish failed ($LASTEXITCODE)" }

    dotnet publish src/Koh.Link -c Release -r win-x64 -o $devBin
    if ($LASTEXITCODE -ne 0) { throw "koh-link publish failed ($LASTEXITCODE)" }

    dotnet publish src/Koh.Emulator.App -c Release -r win-x64 -o $devBin
    if ($LASTEXITCODE -ne 0) { throw "emulator publish failed ($LASTEXITCODE)" }

    # Metadata file + current pointer — same layout the Inno Setup
    # installer and the extension's auto-installer write.
    $meta = '{"version":"' + $devVersion + '","rid":"win-x64","installedAt":""}'
    Set-Content -Path (Join-Path (Split-Path $devBin -Parent) 'version.json') -Value $meta -Encoding utf8 -NoNewline
    Set-Content -Path (Join-Path $toolchainRoot 'current') -Value $devVersion -Encoding utf8 -NoNewline
}
finally {
    Pop-Location
}

Push-Location $extDir
try {
    if (-not (Test-Path 'node_modules')) {
        Write-Host '─── npm ci ───' -ForegroundColor Cyan
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)" }
    }

    Write-Host '─── tsc compile ───' -ForegroundColor Cyan
    npm run compile
    if ($LASTEXITCODE -ne 0) { throw "tsc failed ($LASTEXITCODE)" }

    Write-Host '─── vsce package ───' -ForegroundColor Cyan
    # Remove any stale .vsix so we pick the freshly-built one by
    # glob below without having to track vsce's output name.
    Get-ChildItem -Filter '*.vsix' | Remove-Item -ErrorAction SilentlyContinue
    npx vsce package
    if ($LASTEXITCODE -ne 0) { throw "vsce package failed ($LASTEXITCODE)" }

    $vsix = Get-ChildItem -Filter '*.vsix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $vsix) { throw 'vsce package produced no .vsix' }

    Write-Host "─── code --install-extension $($vsix.Name) ───" -ForegroundColor Cyan
    code --install-extension $vsix.FullName --force
    if ($LASTEXITCODE -ne 0) { throw "code install failed ($LASTEXITCODE)" }

    Write-Host ''
    Write-Host "Installed: $($vsix.Name)" -ForegroundColor Green
    Write-Host "Dev toolchain at: $devBin"
    Write-Host 'Reload any open VS Code windows for the new build to take effect.'
}
finally {
    Pop-Location
}
