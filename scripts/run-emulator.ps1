# Build and launch the desktop.

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$tfm = 'net10.0'
$rid = 'win-x64'
$publishDir = Join-Path $repoRoot "src/Emulator/Koh.Emulator.App/bin/Release/$tfm/$rid/publish"
$exe = Join-Path $publishDir 'koh-emulator.exe'

Push-Location $repoRoot
try {
    dotnet msbuild build.proj -t:PublishEmulatorApp "-p:EmulatorRuntimeIdentifier=$rid"
    if ($LASTEXITCODE -ne 0) { throw "desktop publish failed ($LASTEXITCODE)" }
    if (-not (Test-Path $exe)) { throw "publish output missing: $exe" }
    if ($args.Count -gt 0) { & $exe @args } else { & $exe }
}
finally {
    Pop-Location
}
