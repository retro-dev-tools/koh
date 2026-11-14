$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$null = New-Item -ItemType Directory -Force -Path build
$root = git rev-parse --show-toplevel
cargo run --manifest-path "$root/Cargo.toml" --release --package koh-asm --locked -- src/main.asm -o build/2048.kobj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project "$root/src/Link/Koh.Link" -- build/2048.kobj `
  -o build/2048.gbc `
  -n build/2048.sym `
  -d build/2048.kdbg
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host 'Built build/2048.gbc'
