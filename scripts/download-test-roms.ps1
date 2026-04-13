# Downloads test ROM fixtures with SHA-256 verification.
# Used by CI and by local developers before running compatibility tests.

param(
    [string]$OutputDir = "tests/fixtures/test-roms",
    [string]$ReferenceDir = "tests/fixtures/reference"
)

$ErrorActionPreference = 'Stop'

foreach ($dir in @($OutputDir, $ReferenceDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

function Download-WithHash {
    param(
        [string]$Url,
        [string]$Output,
        [string]$ExpectedSha256
    )

    if ((Test-Path $Output) -and $ExpectedSha256) {
        $actual = (Get-FileHash -Path $Output -Algorithm SHA256).Hash.ToLower()
        if ($actual -eq $ExpectedSha256.ToLower()) {
            Write-Host "OK  $Output"
            return
        }
        Write-Host "REDOWNLOAD $Output (hash mismatch)"
    }

    Invoke-WebRequest -Uri $Url -OutFile $Output -UseBasicParsing

    if ($ExpectedSha256) {
        $actual = (Get-FileHash -Path $Output -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $ExpectedSha256.ToLower()) {
            Write-Error "FAIL $Output: hash mismatch (expected $ExpectedSha256, got $actual)"
        }
    }
    Write-Host "DL  $Output"
}

# Phase 2: dmg-acid2 + cgb-acid2. SHA-256 values left empty until the first
# successful download.
Download-WithHash `
    -Url "https://github.com/mattcurrie/dmg-acid2/releases/download/v1.0/dmg-acid2.gb" `
    -Output "$OutputDir/dmg-acid2.gb" `
    -ExpectedSha256 ""

Download-WithHash `
    -Url "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2.gbc" `
    -Output "$OutputDir/cgb-acid2.gbc" `
    -ExpectedSha256 ""

Download-WithHash `
    -Url "https://github.com/mattcurrie/dmg-acid2/releases/download/v1.0/dmg-acid2-dmg.png" `
    -Output "$ReferenceDir/dmg-acid2.png" `
    -ExpectedSha256 ""

Download-WithHash `
    -Url "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2-cgb.png" `
    -Output "$ReferenceDir/cgb-acid2.png" `
    -ExpectedSha256 ""

Write-Host "download-test-roms: acid2 fixtures ready under $OutputDir"
