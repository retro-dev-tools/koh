# Build + install the Koh VS Code extension locally. Does the full
# chain end-to-end so a fresh clone becomes a working install with
# one invocation:
#
#   1. Publish koh-lsp into editors/vscode/server (the extension's
#      LSP-auto-detect path).
#   2. NativeAOT-publish Koh.Emulator.App so F5 in a koh-asm file
#      can spawn the binary for the DAP session.
#   3. npm ci + tsc → editors/vscode/out/
#   4. vsce package → editors/vscode/koh-asm-<version>.vsix
#   5. code --install-extension <vsix> --force
#
# Rerun after any extension / emulator / LSP change to pick the
# rebuild up in VS Code.

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

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$extDir   = Join-Path $repoRoot 'editors/vscode'

Push-Location $repoRoot
try {
    Write-Host '─── Publishing koh-lsp ───' -ForegroundColor Cyan
    dotnet publish src/Koh.Lsp -c Release -o (Join-Path $extDir 'server')
    if ($LASTEXITCODE -ne 0) { throw "koh-lsp publish failed ($LASTEXITCODE)" }

    Write-Host '─── Publishing Koh.Emulator.App (NativeAOT) ───' -ForegroundColor Cyan
    # The extension's KohDapAdapterFactory resolves the emulator
    # under src/Koh.Emulator.App/bin/Release/net10.0/win-x64/publish
    # by default, matching what this target produces.
    dotnet publish src/Koh.Emulator.App -c Release -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "emulator publish failed ($LASTEXITCODE)" }
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
    npx vsce package --no-dependencies
    if ($LASTEXITCODE -ne 0) { throw "vsce package failed ($LASTEXITCODE)" }

    $vsix = Get-ChildItem -Filter '*.vsix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $vsix) { throw 'vsce package produced no .vsix' }

    Write-Host "─── code --install-extension $($vsix.Name) ───" -ForegroundColor Cyan
    code --install-extension $vsix.FullName --force
    if ($LASTEXITCODE -ne 0) { throw "code install failed ($LASTEXITCODE)" }

    $emuExe = Join-Path $repoRoot 'src/Koh.Emulator.App/bin/Release/net10.0/win-x64/publish/Koh.Emulator.App.exe'
    Write-Host ''
    Write-Host "Installed: $($vsix.Name)" -ForegroundColor Green
    Write-Host 'Reload any open VS Code windows for the new build to take effect.'
    Write-Host ''
    Write-Host 'Set this in your VS Code settings so F5 can find the emulator:' -ForegroundColor Yellow
    Write-Host "  `"koh.emulator.exePath`": `"$($emuExe -replace '\\', '/')`""
}
finally {
    Pop-Location
}
