# Publish Koh.Emulator.Maui in Release and launch it. Mirrors
# `dotnet msbuild build.proj -t:RunMaui` but is a bit friendlier to type.

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$tfm = 'net10.0-windows10.0.19041.0'
$rid = 'win-x64'
$publishDir = Join-Path $repoRoot "src/Koh.Emulator.Maui/bin/Release/$tfm/$rid/publish"
$exe = Join-Path $publishDir 'Koh.Emulator.Maui.exe'

Push-Location $repoRoot
try {
    dotnet publish src/Koh.Emulator.Maui -f $tfm -r $rid -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    if (-not (Test-Path $exe)) { throw "publish output missing: $exe" }
    & $exe
}
finally {
    Pop-Location
}
