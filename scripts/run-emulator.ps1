# Publish the Koh emulator (KohUI + GLFW + OpenAL, NativeAOT) in Release
# and launch it. Optional ROM path as arg 1; omitted → the emulator
# auto-discovers a default ROM per its FindDefaultRom logic.

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$tfm = 'net10.0'
$rid = 'win-x64'
$publishDir = Join-Path $repoRoot "src/Koh.Emulator.App/bin/Release/$tfm/$rid/publish"
$exe = Join-Path $publishDir 'Koh.Emulator.App.exe'

Push-Location $repoRoot
try {
    dotnet publish src/Koh.Emulator.App -c Release -r $rid
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    if (-not (Test-Path $exe)) { throw "publish output missing: $exe" }
    if ($args.Count -gt 0) { & $exe @args } else { & $exe }
}
finally {
    Pop-Location
}
