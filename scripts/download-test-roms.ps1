# Downloads test ROM fixtures with SHA-256 verification.
# Used by CI and by local developers before running compatibility tests.

param(
    [string]$OutputDir = "tests/fixtures/test-roms"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Phase 0 placeholder — no ROMs to download yet.
# Phase 2 adds: dmg-acid2, cgb-acid2
# Phase 3 adds: Blargg cpu_instrs, instr_timing, mem_timing, mem_timing-2,
#               halt_bug, interrupt_time; Mooneye acceptance/
# Phase 4 adds: Blargg dmg_sound

Write-Host "download-test-roms: no ROMs configured for the current phase"
exit 0
