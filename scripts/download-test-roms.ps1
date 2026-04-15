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
    -ExpectedSha256 "464e14b7d42e7feea0b7ede42be7071dc88913f75b9ffa444299424b63d1dff1"

Download-WithHash `
    -Url "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2.gbc" `
    -Output "$OutputDir/cgb-acid2.gbc" `
    -ExpectedSha256 "d24d6f38478f05567cccb96015b1479b0da6d2f70ef8966896ef0b10cd3062cf"

Download-WithHash `
    -Url "https://raw.githubusercontent.com/mattcurrie/dmg-acid2/master/img/reference-dmg.png" `
    -Output "$ReferenceDir/dmg-acid2.png" `
    -ExpectedSha256 "ca966d50895c7efef05838590d148c2cbfd7fba57dab986f25b35b4da71abb57"

Download-WithHash `
    -Url "https://raw.githubusercontent.com/mattcurrie/cgb-acid2/master/img/reference.png" `
    -Output "$ReferenceDir/cgb-acid2.png" `
    -ExpectedSha256 "9ea9c262c5383353e77d715d021a0f7c5ccbe438f88082cb225756e50c4fdf01"

Write-Host "download-test-roms: acid2 fixtures ready under $OutputDir"
