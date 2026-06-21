# Game Boy 2048 Sample — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a complete, polished, GBC-only 2048 game ROM under `samples/gb-2048/`, exercising MBC5 banking, GBC hardware features (HDMA, double-speed, STAT split), OAM-sprite slide animation, SRAM saves, sfx, and a window-layer HUD.

**Architecture:** RGBDS-style assembly, single-entrypoint compilation (`src/main.asm` includes everything), four banks (engine / game / gfx / screens) linked into a 64 KiB ROM by `koh-asm` + `koh-link`. WRAM-based shadow OAM DMA'd each VBlank; VRAM writes batched through a queue drained in the VBlank ISR. Game state machine: TITLE → PLAYING → ANIMATING → (PLAYING | WIN | GAMEOVER).

**Tech Stack:** Koh assembler (`koh-asm`), Koh linker (`koh-link`), SM83 assembly with GBC extensions, MBC5 cartridge.

**Reference spec:** `docs/superpowers/specs/2026-04-30-gb-2048-design.md` — read sections in parallel with each phase below.

**Conventions used in this plan:**

- "Build" means: `dotnet run --project src/Koh.Asm -- samples/gb-2048/src/main.asm -o samples/gb-2048/build/2048.kobj` then `dotnet run --project src/Koh.Link -- samples/gb-2048/build/2048.kobj -o samples/gb-2048/build/2048.gbc -n samples/gb-2048/build/2048.sym`. Wrapper script in Task 1.
- "Verify boots" means: open `samples/gb-2048/build/2048.gbc` in BGB or SameBoy and confirm the LCD turns on (some content visible, or a known register state) without the emulator displaying a fault.
- Symbols prefixed `w` live in WRAM, `h` in HRAM, `s` in SRAM, no prefix for ROM labels. Public labels use `::` (RGBDS export); internal labels use `:`. (Verify Koh's `::` support during Task 2; if not supported, drop to `:` everywhere — this is a single search/replace.)
- One commit per task unless a task explicitly says otherwise. Conventional Commits: `feat(samples): ...`, `fix(samples): ...`, `chore(samples): ...`.

**Deviations from spec, with rationale:**

- **Tile data lives in `.asm` files as `db` literals**, not as committed `.2bpp` binaries. Reason: avoiding a binary-asset pipeline keeps the sample self-contained and showcases the assembler's macro system for 2bpp encoding. The spec's `tiles.2bpp/font.2bpp` files are dropped; `tiles.asm`/`font.asm` carry the bytes directly. This is a pure simplification — no functional difference in the ROM.

---

## File structure

```
samples/gb-2048/
  koh.yaml                       # LSP project descriptor
  README.md                      # build, controls, screenshot
  scripts/
    build.ps1, build.sh          # asm + link wrappers
    fixup-header.ps1, fixup-header.sh   # only if Task 5 finds koh-link doesn't fix the header
  src/
    main.asm                     # entrypoint; INCLUDEs everything
    hardware.inc                 # GB/GBC register defs
    macros.inc                   # vblank wait, farcall, OAM, 2bpp-row macros
    memory.inc                   # WRAM/HRAM/SRAM symbol map
    engine/
      irq.asm                    # ROM0 — VBlank/STAT/Timer ISRs
      oam_dma.asm                # ROM0 — HRAM trampoline source + install
      vblank_queue.asm           # ROM0 — drain pending VRAM writes
      input.asm                  # ROM0 — joypad poll, edge + repeat
      hdma.asm                   # ROM0 — GBC GP-DMA helpers
      sound.asm                  # ROM0 — sfx engine + sfx data
      rng.asm                    # ROM1 — Galois LFSR
    game/
      board.asm                  # ROM1 — board state, MoveLeft + transposition
      score.asm                  # ROM1 — score arithmetic + BCD decode
      save.asm                   # ROM1 — SRAM read/write + Fletcher-16
      anim.asm                   # ROM1 — slide state machine
      render.asm                 # ROM1 — board → tile cmds, score → HUD
    screens/
      title.asm                  # ROM3 — title + STAT palette split
      gameover.asm               # ROM3 — game-over banner + score
      win.asm                    # ROM3 — win sequence + jingle
    gfx/
      tiles.asm                  # ROM2 — value glyphs + decorations
      font.asm                   # ROM2 — 8×8 digits + uppercase letters
  build/                         # gitignored — output 2048.gbc + 2048.sym + 2048.kobj
```

---

## Phase 0 — Foundation

### Task 1: Scaffold directory and minimal main.asm

**Files:**
- Create: `samples/gb-2048/koh.yaml`
- Create: `samples/gb-2048/README.md`
- Create: `samples/gb-2048/.gitignore`
- Create: `samples/gb-2048/src/main.asm`
- Create: `samples/gb-2048/scripts/build.ps1`
- Create: `samples/gb-2048/scripts/build.sh`

- [ ] **Step 1: Create `samples/gb-2048/koh.yaml`**

```yaml
version: 1
projects:
  - name: 2048
    entrypoint: src/main.asm
```

- [ ] **Step 2: Create `samples/gb-2048/.gitignore`**

```
build/
*.kobj
*.sym
*.gb
*.gbc
```

- [ ] **Step 3: Create minimal `samples/gb-2048/src/main.asm`**

```
; 2048 — Game Boy Color
;
; Boot stub — Phase 0 only. Real entry replaces this in Phase 1.

SECTION "Reset", ROM0[$0100]
EntryPoint:
    nop
    jp Boot

SECTION "Boot", ROM0
Boot:
.halt:
    halt
    jr .halt
```

- [ ] **Step 4: Create `samples/gb-2048/scripts/build.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build
ROOT="$(git rev-parse --show-toplevel)"
dotnet run --project "$ROOT/src/Koh.Asm" -- src/main.asm -o build/2048.kobj
dotnet run --project "$ROOT/src/Koh.Link" -- build/2048.kobj \
  -o build/2048.gbc \
  -n build/2048.sym
echo "Built build/2048.gbc"
```

- [ ] **Step 5: Create `samples/gb-2048/scripts/build.ps1`**

```powershell
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$null = New-Item -ItemType Directory -Force -Path build
$root = git rev-parse --show-toplevel
dotnet run --project "$root/src/Koh.Asm" -- src/main.asm -o build/2048.kobj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project "$root/src/Koh.Link" -- build/2048.kobj `
  -o build/2048.gbc `
  -n build/2048.sym
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host 'Built build/2048.gbc'
```

- [ ] **Step 6: Create stub `samples/gb-2048/README.md`**

```markdown
# Game Boy 2048 Sample

A polished 2048 game for Game Boy Color, built with the Koh toolchain.

## Build

```sh
./scripts/build.sh        # bash / git-bash
./scripts/build.ps1       # Windows PowerShell
```

Output: `build/2048.gbc`. Open in BGB, SameBoy, or mGBA.

## Controls

(Filled in once gameplay is wired up.)
```

- [ ] **Step 7: Make build script executable on Unix**

Run: `chmod +x samples/gb-2048/scripts/build.sh`

- [ ] **Step 8: First build**

Run: `./samples/gb-2048/scripts/build.sh` (or `.ps1` on Windows)
Expected: exit 0, prints "Built build/2048.gbc", file `samples/gb-2048/build/2048.gbc` exists.

- [ ] **Step 9: Commit**

```sh
git add samples/gb-2048/
git commit -m "feat(samples): scaffold gb-2048 directory with stub ROM"
```

### Task 2: Verify Koh's `::` exported-label syntax

**Files:** none new. This task is a compatibility probe.

- [ ] **Step 1: Probe**

Add a temporary line to `samples/gb-2048/src/main.asm`:
```
ProbeExport::
```
Run the build. If it succeeds, `::` works. If it errors, drop to `:` for all later tasks (single-label exports are still possible via `:` without the formal "export" semantics — Koh resolves linker-level visibility from SECTION boundaries anyway).

- [ ] **Step 2: Record the result**

Add a one-line note to `samples/gb-2048/README.md` under a "Notes" section, e.g. `Public labels use \`::\` syntax.` or `Public labels use \`:\` only — \`::\` not yet supported by Koh.`

- [ ] **Step 3: Remove the probe and rebuild**

Delete the `ProbeExport::` line. Build to confirm clean.

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/README.md samples/gb-2048/src/main.asm
git commit -m "chore(samples): probe Koh exported-label syntax"
```

### Task 3: hardware.inc — register and bit definitions

**Files:**
- Create: `samples/gb-2048/src/hardware.inc`
- Modify: `samples/gb-2048/src/main.asm` — add `INCLUDE "hardware.inc"` at top.

- [ ] **Step 1: Write `hardware.inc`**

```
; Game Boy / Game Boy Color hardware register definitions.
; Hand-authored subset — only what the game actually touches.

; --- Joypad ---
rP1                EQU $FF00
P1F_GET_BTN        EQU %0010_0000
P1F_GET_DPAD       EQU %0001_0000
P1F_GET_NONE       EQU %0011_0000

; --- Interrupts ---
rIF                EQU $FF0F
rIE                EQU $FFFF
IEF_VBLANK         EQU %0000_0001
IEF_STAT           EQU %0000_0010
IEF_TIMER          EQU %0000_0100
IEF_SERIAL         EQU %0000_1000
IEF_HILO           EQU %0001_0000

; --- LCD ---
rLCDC              EQU $FF40
LCDCF_OFF          EQU %0000_0000
LCDCF_ON           EQU %1000_0000
LCDCF_WIN9C00      EQU %0100_0000
LCDCF_WIN9800      EQU %0000_0000
LCDCF_WINON        EQU %0010_0000
LCDCF_WINOFF       EQU %0000_0000
LCDCF_BG8000       EQU %0001_0000
LCDCF_BG8800       EQU %0000_0000
LCDCF_BG9C00       EQU %0000_1000
LCDCF_BG9800       EQU %0000_0000
LCDCF_OBJ16        EQU %0000_0100
LCDCF_OBJ8         EQU %0000_0000
LCDCF_OBJON        EQU %0000_0010
LCDCF_OBJOFF       EQU %0000_0000
LCDCF_BGON         EQU %0000_0001
LCDCF_BGOFF        EQU %0000_0000

rSTAT              EQU $FF41
STATF_LYC          EQU %0100_0000
STATF_MODE10       EQU %0010_0000
STATF_MODE01       EQU %0001_0000
STATF_MODE00       EQU %0000_1000
STATF_LYCF         EQU %0000_0100
STATF_HBL          EQU 0
STATF_VBL          EQU 1
STATF_OAM          EQU 2
STATF_LCD          EQU 3

rSCY               EQU $FF42
rSCX               EQU $FF43
rLY                EQU $FF44
rLYC               EQU $FF45
rDMA               EQU $FF46
rBGP               EQU $FF47   ; DMG only
rOBP0              EQU $FF48   ; DMG only
rOBP1              EQU $FF49   ; DMG only
rWY                EQU $FF4A
rWX                EQU $FF4B

; --- GBC-only ---
rKEY1              EQU $FF4D   ; speed switch
rVBK               EQU $FF4F   ; VRAM bank select
rHDMA1             EQU $FF51
rHDMA2             EQU $FF52
rHDMA3             EQU $FF53
rHDMA4             EQU $FF54
rHDMA5             EQU $FF55
rRP                EQU $FF56
rBCPS              EQU $FF68   ; BG palette index
rBCPD              EQU $FF69   ; BG palette data
rOCPS              EQU $FF6A   ; OBJ palette index
rOCPD              EQU $FF6B   ; OBJ palette data
rSVBK              EQU $FF70   ; WRAM bank select

BCPSF_AUTOINC      EQU %1000_0000
OCPSF_AUTOINC      EQU %1000_0000

; --- Sound ---
rNR10              EQU $FF10
rNR11              EQU $FF11
rNR12              EQU $FF12
rNR13              EQU $FF13
rNR14              EQU $FF14
rNR21              EQU $FF16
rNR22              EQU $FF17
rNR23              EQU $FF18
rNR24              EQU $FF19
rNR30              EQU $FF1A
rNR31              EQU $FF1B
rNR32              EQU $FF1C
rNR33              EQU $FF1D
rNR34              EQU $FF1E
rNR41              EQU $FF20
rNR42              EQU $FF21
rNR43              EQU $FF22
rNR44              EQU $FF23
rNR50              EQU $FF24
rNR51              EQU $FF25
rNR52              EQU $FF26
rWAVE_0            EQU $FF30   ; wave RAM, 16 bytes

; --- Timer ---
rDIV               EQU $FF04
rTIMA              EQU $FF05
rTMA               EQU $FF06
rTAC               EQU $FF07

; --- Memory regions ---
_VRAM              EQU $8000
_VRAM8000          EQU $8000
_VRAM8800          EQU $8800
_VRAM9000          EQU $9000
_SCRN0             EQU $9800
_SCRN1             EQU $9C00
_OAMRAM            EQU $FE00
_RAM               EQU $C000
_HRAM              EQU $FF80
_SRAM              EQU $A000

; --- MBC5 ---
rRAMG              EQU $0000   ; SRAM enable (write $0A)
rROMB0             EQU $2000   ; ROM bank low
rROMB1             EQU $3000   ; ROM bank high
rRAMB              EQU $4000   ; SRAM bank
```

- [ ] **Step 2: Add `INCLUDE` to main.asm**

Insert at the top of `samples/gb-2048/src/main.asm`:
```
INCLUDE "hardware.inc"
```

- [ ] **Step 3: Build to verify INCLUDE resolves**

Run the build script. Expected: succeeds. If it fails on missing INCLUDE search path, the build script must pass an `-I` or similar flag — check `koh-asm --help` and adjust the build script. (As of writing, koh-asm has no documented `-I` flag; the resolver appears to use the source file's directory. Files are sibling, so this should just work.)

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/src/hardware.inc samples/gb-2048/src/main.asm
git commit -m "feat(samples): add hardware.inc with GB/GBC register defs"
```

### Task 4: macros.inc — common macros

**Files:**
- Create: `samples/gb-2048/src/macros.inc`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE it.

- [ ] **Step 1: Write `macros.inc`**

```
; Common macros.

; Wait for VBlank by spinning until rLY hits 144.
; Clobbers A. Use ONLY with interrupts disabled or with VBlank IRQ handler that
; doesn't enable other interrupts mid-wait.
WAIT_VBLANK: MACRO
.\@_wait\@:
    ldh a, [rLY]
    cp 144
    jr c, .\@_wait\@
ENDM

; farcall <bank>, <symbol>
;   Save current ROM bank, switch to \1, call \2, restore previous bank.
;   Caller must have at least 4 bytes of stack headroom. Clobbers nothing
;   directly visible (saves A and BC around the call).
;   wCurrentBank holds the active bank for the duration of the call so any
;   nested farcall restores correctly.
farcall: MACRO
    ld a, [wCurrentBank]
    push af
    ld a, \1
    ld [wCurrentBank], a
    ld [rROMB0], a
    call \2
    pop af
    ld [wCurrentBank], a
    ld [rROMB0], a
ENDM

; 2bpp tile-row encode. \1 = high-bit byte (0/1 per pixel), \2 = low-bit byte.
; Game Boy 2bpp stores rows as two interleaved bytes per row, low-byte first.
; Use as: TILE_ROW $0F, $0F  ; produces a row of color-3 pixels in bottom 4
TILE_ROW: MACRO
    db \2, \1
ENDM

; Quick fill macro: REPT-based fill of N copies of \1.
FILL_BYTES: MACRO     ; \1 = count, \2 = byte
REPT \1
    db \2
ENDR
ENDM
```

- [ ] **Step 2: INCLUDE in main.asm**

Add after the `INCLUDE "hardware.inc"` line:
```
INCLUDE "macros.inc"
```

- [ ] **Step 3: Build**

Run the build. Expected: clean.

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/src/macros.inc samples/gb-2048/src/main.asm
git commit -m "feat(samples): add macros.inc (vblank wait, farcall, 2bpp row)"
```

---

## Phase 1 — Boot, header, memory map

### Task 5: Memory map (`memory.inc`)

**Files:**
- Create: `samples/gb-2048/src/memory.inc`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE it; add WRAM/HRAM section declarations referencing the symbols.

- [ ] **Step 1: Write `memory.inc`**

```
; WRAM, HRAM, SRAM symbol declarations as labels in their own SECTIONs.
; Each `ds` allocates space; the linker assigns addresses.

SECTION "WRAM Game", WRAM0[$C000]
wBoard:        ds 16
wPrevBoard:    ds 16
wMoveIntents:  ds 64        ; 16 cells × 4 bytes
wScore:        ds 4
wBestScore:    ds 4
wRngState:     ds 2
wInput:        ds 4         ; current, previous, edge, repeat counter
wGameState:   ds 1
wWonOnce:     ds 1
wAnimFrame:   ds 1
wCurrentBank: ds 1
wVBlankFlag:  ds 1          ; set by VBlank ISR, cleared by main loop
wSlideValid:  ds 1          ; non-zero if last move actually changed the board
wVBlankQueue: ds 64

SECTION "OAM Buffer", WRAM0[$C100]
wOAMBuffer:   ds 160        ; 40 sprites × 4 bytes — must be 256-byte aligned

SECTION "HRAM", HRAM
hOAMDMA::      ds 8         ; trampoline copied from ROM at boot
hFrameCounter: ds 1
hSysFlags:     ds 1
hScratch:      ds 4

SECTION "SRAM", SRAM, BANK[0]
sMagic:        ds 4         ; "K248"
sBestScore:    ds 4
sLastBoard:    ds 16
sLastScore:    ds 4
sChecksum:     ds 2         ; Fletcher-16 over sMagic..sLastScore
```

- [ ] **Step 2: INCLUDE in main.asm**

Add after `INCLUDE "macros.inc"`:
```
INCLUDE "memory.inc"
```

- [ ] **Step 3: Build**

Expected: clean. Linker should accept the SECTION declarations and place `wOAMBuffer` at `$C100` (256-byte aligned).

- [ ] **Step 4: Verify alignment**

Build also produces `build/2048.sym`. Open it and grep for `wOAMBuffer` — its address must end in `00` (e.g., `$C100`). If not, the SECTION header didn't pin the address; investigate.

Run: `grep wOAMBuffer samples/gb-2048/build/2048.sym`
Expected: line containing `00:C100` or `00:c100`.

- [ ] **Step 5: Commit**

```sh
git add samples/gb-2048/src/memory.inc samples/gb-2048/src/main.asm
git commit -m "feat(samples): declare WRAM/HRAM/SRAM memory map"
```

### Task 6: ROM header

**Files:**
- Modify: `samples/gb-2048/src/main.asm` — replace stub Boot with header + reset entry.

- [ ] **Step 1: Replace `main.asm` body**

Full file contents:
```
INCLUDE "hardware.inc"
INCLUDE "macros.inc"
INCLUDE "memory.inc"

; -----------------------------------------------------------------------------
; Reset vectors. RST $00..$38 are 8 bytes apart; we use $38 only.
; -----------------------------------------------------------------------------
SECTION "RST_38", ROM0[$0038]
    jp $0038                  ; default crash handler — infinite loop

; -----------------------------------------------------------------------------
; Interrupt vectors.
; -----------------------------------------------------------------------------
SECTION "IRQ_VBlank", ROM0[$0040]
    jp VBlankIRQ
SECTION "IRQ_LCD", ROM0[$0048]
    jp StatIRQ
SECTION "IRQ_Timer", ROM0[$0050]
    reti
SECTION "IRQ_Serial", ROM0[$0058]
    reti
SECTION "IRQ_Joypad", ROM0[$0060]
    reti

; -----------------------------------------------------------------------------
; Cartridge entry. The boot ROM jumps to $0100 after handing off.
; -----------------------------------------------------------------------------
SECTION "Reset", ROM0[$0100]
EntryPoint:
    nop
    jp Boot

; -----------------------------------------------------------------------------
; ROM header at $0104..$014F. Nintendo logo first.
; -----------------------------------------------------------------------------
SECTION "Header", ROM0[$0104]
    ; Nintendo logo (verbatim).
    db $CE,$ED,$66,$66,$CC,$0D,$00,$0B,$03,$73,$00,$83,$00,$0C,$00,$0D
    db $00,$08,$11,$1F,$88,$89,$00,$0E,$DC,$CC,$6E,$E6,$DD,$DD,$D9,$99
    db $BB,$BB,$67,$63,$6E,$0E,$EC,$CC,$DD,$DC,$99,$9F,$BB,$B9,$33,$3E

    ; Title (11 bytes, $0134..$013E).
    db "KOH 2048",0,0,0
    ; Manufacturer code (4 bytes) — leave zero.
    db 0,0,0,0
    ; CGB flag.
    db $C0
    ; New licensee code.
    db "00"
    ; SGB flag.
    db $00
    ; Cartridge type — MBC5 + RAM + BATTERY.
    db $1B
    ; ROM size — $01 = 64 KiB (4 banks).
    db $01
    ; RAM size — $02 = 8 KiB (1 bank).
    db $02
    ; Destination — non-Japan.
    db $01
    ; Old licensee — $33 means "see new licensee".
    db $33
    ; Mask ROM version.
    db $00
    ; Header checksum — set to 0 here; Task 7 verifies whether the linker patches it.
    db $00
    ; Global checksum — set to 0; same Task 7 question.
    db $00,$00

; -----------------------------------------------------------------------------
SECTION "Boot", ROM0
Boot:
    di
    ld sp, $E000               ; top of WRAM
.halt:
    halt
    jr .halt
```

- [ ] **Step 2: Build**

Expected: clean.

- [ ] **Step 3: Verify the CGB flag byte**

Run (bash):
```sh
xxd -s 0x143 -l 1 samples/gb-2048/build/2048.gbc
```
Expected output: `00000143: c0`

PowerShell equivalent:
```powershell
(Get-Content samples/gb-2048/build/2048.gbc -Encoding Byte -TotalCount 0x144)[0x143].ToString('X2')
```
Expected: `C0`.

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/src/main.asm
git commit -m "feat(samples): add ROM header, reset and IRQ vectors"
```

### Task 7: Header checksum investigation + fixup if needed

**Files:**
- Possibly create: `samples/gb-2048/scripts/fixup-header.sh`, `.ps1`
- Possibly modify: `samples/gb-2048/scripts/build.sh`, `.ps1`

- [ ] **Step 1: Read header checksum bytes after a build**

After Task 6's build, dump byte `$014D` (header checksum) and bytes `$014E..$014F` (global checksum).

Bash:
```sh
xxd -s 0x14D -l 3 samples/gb-2048/build/2048.gbc
```
Expected if linker patches: header byte at `$014D` matches the formula `x = -(sum_{$0134..$014C} byte + 25) mod 256`. For our header, manually computed value is **`$1F`** (verify by reading `samples/gb-2048/src/main.asm` from the title byte to mask-rom version, summing, negating, masking to 8 bits).

If the byte is `$00`, the linker did not patch and we need a fixup.

- [ ] **Step 2: Choose a path**

Branch A — linker patches: nothing to do. Skip to step 5.
Branch B — linker doesn't patch: continue with steps 3–4.

- [ ] **Step 3 (Branch B only): Write `samples/gb-2048/scripts/fixup-header.sh`**

```bash
#!/usr/bin/env bash
# Patch GB header checksum bytes after koh-link.
set -euo pipefail
ROM="$1"
python3 - "$ROM" <<'PY'
import sys
path = sys.argv[1]
with open(path, "rb+") as f:
    data = bytearray(f.read())
    # Header checksum: $014D = -(sum bytes $0134..$014C + 25) & 0xFF
    s = 0
    for i in range(0x134, 0x14D):
        s += data[i] + 1   # algorithm: x = x - data[i] - 1
    h = (-s) & 0xFF
    data[0x14D] = h
    # Global checksum: 16-bit sum of all bytes EXCEPT $014E and $014F.
    g = 0
    for i, b in enumerate(data):
        if i == 0x14E or i == 0x14F:
            continue
        g = (g + b) & 0xFFFF
    data[0x14E] = (g >> 8) & 0xFF
    data[0x14F] = g & 0xFF
    f.seek(0)
    f.write(data)
print(f"Patched header checksum and global checksum in {path}")
PY
```

(If Python 3 is not acceptable as a build dependency, port to a `.NET` global tool or a hand-rolled bash byte-loop. Python is the smallest dependency; the repo already requires Node.js for the VS Code extension and dotnet for everything else. Python is widely available on developer machines and CI.)

- [ ] **Step 4 (Branch B only): Write `samples/gb-2048/scripts/fixup-header.ps1`**

```powershell
param([string]$Rom)
$bytes = [System.IO.File]::ReadAllBytes($Rom)
# Header checksum
$s = 0
for ($i = 0x134; $i -lt 0x14D; $i++) { $s -= $bytes[$i] + 1 }
$bytes[0x14D] = $s -band 0xFF
# Global checksum
$g = 0
for ($i = 0; $i -lt $bytes.Length; $i++) {
    if ($i -eq 0x14E -or $i -eq 0x14F) { continue }
    $g = ($g + $bytes[$i]) -band 0xFFFF
}
$bytes[0x14E] = ($g -shr 8) -band 0xFF
$bytes[0x14F] = $g -band 0xFF
[System.IO.File]::WriteAllBytes($Rom, $bytes)
Write-Host "Patched header checksum and global checksum in $Rom"
```

Wire both into `build.sh` and `build.ps1` after the `koh-link` invocation.

- [ ] **Step 5: Verify in BGB**

Open `build/2048.gbc` in BGB. Expected: emulator does NOT display "header checksum failed" and the ROM enters its `halt; jr` loop. (BGB warns on bad checksum but still runs.)

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/scripts/
git commit -m "fix(samples): patch GB header checksums after link"
```
(If Branch A applied, no commit.)

### Task 8: KEY1 double-speed entry + LCD off + VRAM clear

**Files:**
- Modify: `samples/gb-2048/src/main.asm` — flesh out `Boot:`.

- [ ] **Step 1: Replace `Boot:` body**

```
SECTION "Boot", ROM0
Boot:
    di
    ld sp, $E000

    ; 1. Disable LCD before touching VRAM. Wait for VBlank first to avoid the
    ;    well-known GBC LCD-off-mid-frame issue.
    ldh a, [rLY]
    cp 144
    jr c, .wait_vblank
    xor a
    ldh [rLCDC], a
    jr .lcd_off
.wait_vblank:
    ldh a, [rLY]
    cp 144
    jr c, .wait_vblank
    xor a
    ldh [rLCDC], a
.lcd_off:

    ; 2. Switch to double speed. KEY1 prepare → STOP.
    ld a, $30                  ; P1F_GET_NONE — pad disabled
    ldh [rP1], a
    ld a, %0000_0001           ; bit 0 = prepare speed switch
    ldh [rKEY1], a
    stop
    nop                        ; safety nop after STOP

    ; 3. Clear VRAM bank 0 ($8000..$9FFF) and bank 1.
    xor a
    ldh [rVBK], a
    call .clear_vram
    ld a, 1
    ldh [rVBK], a
    call .clear_vram
    xor a
    ldh [rVBK], a

    ; 4. Clear WRAM ($C000..$DFFF). Skip OAM buffer? Cleared anyway.
    ld hl, $C000
    ld bc, $2000
    call MemClear

    ; 5. Clear OAM (write to OAM directly; rDMA copies wOAMBuffer next VBlank).
    ld hl, _OAMRAM
    ld bc, 160
    call MemClear

    ; 6. Initial state.
    xor a
    ld [wCurrentBank], a       ; bank 0 active by default

    ; 7. Phase-1 stop point. Real init continues in later tasks.
.halt:
    halt
    jr .halt

.clear_vram:
    ld hl, $8000
    ld bc, $2000
.clear_vram_loop:
    xor a
    ld [hl+], a
    dec bc
    ld a, b
    or c
    jr nz, .clear_vram_loop
    ret

; -----------------------------------------------------------------------------
; MemClear — fill HL..HL+BC-1 with zero. Clobbers A, BC, HL.
; -----------------------------------------------------------------------------
SECTION "MemClear", ROM0
MemClear:
    xor a
.loop:
    ld [hl+], a
    dec bc
    ld a, b
    or c
    jr nz, .loop
    ret
```

- [ ] **Step 2: Build**

Expected: clean.

- [ ] **Step 3: Verify in BGB**

Open `build/2048.gbc`. Expected: ROM boots, screen white-blanks (LCD off), CPU reaches the halt loop. In BGB: View → Debug → confirm `KEY1 = $80` after STOP completes (bit 7 = current speed, bit 0 cleared by transition). If `KEY1 = $00`, double-speed entry failed — likely the joypad register wasn't set to "all unmasked" mode, or interrupts were enabled. Re-check step 1 ordering.

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/src/main.asm
git commit -m "feat(samples): boot init — double-speed, LCD off, RAM clear"
```

---

## Phase 2 — Engine: IRQs, OAM DMA, VBlank queue, input

### Task 9: OAM DMA trampoline

**Files:**
- Create: `samples/gb-2048/src/engine/oam_dma.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE it, call install routine in Boot.

- [ ] **Step 1: Write `engine/oam_dma.asm`**

```
; OAM DMA must execute from HRAM because the bus is busy during the transfer.
; Source bytes live in ROM; we copy them to hOAMDMA at boot, then "call hOAMDMA"
; to kick off a DMA.

SECTION "OAM DMA Source", ROM0
OAMDMASource:
    ld a, HIGH(wOAMBuffer)
    ldh [rDMA], a
    ld a, $28                  ; 40 cycles
.wait:
    dec a
    jr nz, .wait
    ret
OAMDMASourceEnd:

OAM_DMA_LEN EQU OAMDMASourceEnd - OAMDMASource

; -----------------------------------------------------------------------------
; Install — copy OAMDMASource into hOAMDMA. Call once at boot.
; -----------------------------------------------------------------------------
SECTION "OAM DMA Install", ROM0
InstallOAMDMA:
    ld de, OAMDMASource
    ld hl, hOAMDMA
    ld bc, OAM_DMA_LEN
.loop:
    ld a, [de]
    ld [hl+], a
    inc de
    dec bc
    ld a, b
    or c
    jr nz, .loop
    ret
```

- [ ] **Step 2: INCLUDE in main.asm**

Append after the `MemClear` section:
```
INCLUDE "engine/oam_dma.asm"
```
(Place INCLUDE statements outside any SECTION so subsequent SECTIONs in the included file land cleanly.)

- [ ] **Step 3: Call `InstallOAMDMA` in Boot**

Add after the OAM clear (step 5 in Task 8's Boot body), before "Initial state":
```
    call InstallOAMDMA
```

- [ ] **Step 4: Build and verify trampoline length**

After build, check that `OAM_DMA_LEN` is small (≤ 8 bytes, matching `hOAMDMA: ds 8` in `memory.inc`). The trampoline above is 7 bytes (`ld a, n` × 2 = 4, `ldh [n], a` = 2 — wait, `ldh [n8], a` is 2 bytes, so `ld a, HIGH(wOAMBuffer)` = 2, `ldh [rDMA], a` = 2, `ld a, $28` = 2, `dec a` = 1, `jr nz, .wait` = 2, `ret` = 1 → total 10 bytes). `hOAMDMA` reservation must match. **Adjust `memory.inc`'s `hOAMDMA: ds 8` to `ds 10`** if total comes out larger than 8. Verify by checking the symbol map for `OAMDMASourceEnd - OAMDMASource`.

If `ds N` change is needed, do it in this task and re-verify the alignment of subsequent HRAM symbols.

- [ ] **Step 5: Commit**

```sh
git add samples/gb-2048/src/engine/oam_dma.asm samples/gb-2048/src/memory.inc samples/gb-2048/src/main.asm
git commit -m "feat(samples): install OAM DMA trampoline in HRAM"
```

### Task 10: VBlank queue

**Files:**
- Create: `samples/gb-2048/src/engine/vblank_queue.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `engine/vblank_queue.asm`**

```
; VBlank queue.
;
; Format per entry, packed contiguously in wVBlankQueue:
;   [cmd:1] [dst_lo:1] [dst_hi:1] [len:1] [data:N]
; cmd:
;   $00 = terminator (drain stops; queue is now empty)
;   $01 = copy DATA bytes to VRAM at DST
;
; Producers append entries by walking from wVBlankQueue forward until they hit
; $00, writing their entry, then writing $00 after. The drain runs in VBlank,
; pops one entry per call, advances a pointer, and stops on $00 OR when its
; budget runs out.
;
; The simple v0.1 implementation here resets the queue to empty after each
; drain (no streaming across frames). Producers that won't fit in one VBlank
; must split themselves.

SECTION "VBlank Queue", ROM0

; QueueClear — wipe the queue (write $00 at offset 0).
QueueClear::
    ld a, 0
    ld [wVBlankQueue], a
    ret

; QueuePush — append an entry. Inputs:
;   B = command, DE = dst, A = len, HL = source bytes
; Caller must guarantee budget; this routine does NOT bounds-check.
; Clobbers A, BC, DE, HL.
QueuePush::
    push af
    push bc
    push de
    push hl
    ; Walk to the terminator.
    ld hl, wVBlankQueue
.find:
    ld a, [hl]
    or a
    jr z, .write
    ; advance over [cmd][lo][hi][len][data:len]
    ld c, l
    ld b, h
    inc hl                       ; cmd
    inc hl                       ; dst lo
    inc hl                       ; dst hi
    ld a, [hl+]                  ; len
    ld c, a
    ld b, 0
    add hl, bc
    jr .find
.write:
    ; HL points at terminator. Restore inputs from stack.
    pop bc                       ; original HL → BC (source pointer)
    pop de                       ; original DE → DE (dst)
    pop af                       ; original BC → A high, but we want length in A
    ; Stack now is: original AF on top.
    ; This bit is fiddly; rather than rely on register juggling, callers should
    ; use the wrapper below (QueueCopy) that takes named args via WRAM scratch.
    ; Placeholder: bail out.
    pop af
    ret

; QueueCopy — high-level wrapper. Inputs in HRAM scratch:
;   hScratch+0..1 = dst (lo, hi)
;   hScratch+2    = len  (≤ 32 per call to keep VBlank budget sane)
;   hScratch+3    = unused
;   HL            = source pointer
; Clobbers A, BC, DE, HL.
QueueCopy::
    push hl
    ld hl, wVBlankQueue
.find:
    ld a, [hl]
    or a
    jr z, .write
    inc hl
    inc hl
    inc hl
    ld a, [hl+]
    ld c, a
    ld b, 0
    add hl, bc
    jr .find
.write:
    ld a, $01                    ; cmd = copy
    ld [hl+], a
    ldh a, [hScratch+0]
    ld [hl+], a
    ldh a, [hScratch+1]
    ld [hl+], a
    ldh a, [hScratch+2]
    ld c, a                      ; C = len
    ld [hl+], a
    pop de                       ; DE = source
.copyloop:
    ld a, [de]
    ld [hl+], a
    inc de
    dec c
    jr nz, .copyloop
    ; Write terminator.
    ld a, $00
    ld [hl], a
    ret

; QueueDrain — called from VBlank ISR. Walks the queue and dispatches entries.
; Stops on terminator. Clobbers A, BC, DE, HL.
QueueDrain::
    ld hl, wVBlankQueue
.next:
    ld a, [hl+]
    or a
    ret z                        ; terminator
    cp $01
    jr z, .copy
    ; Unknown cmd — bail.
    ret
.copy:
    ld a, [hl+]                  ; dst lo
    ld e, a
    ld a, [hl+]                  ; dst hi
    ld d, a
    ld a, [hl+]                  ; len
    ld c, a
.copy_inner:
    ld a, [hl+]
    ld [de], a
    inc de
    dec c
    jr nz, .copy_inner
    jr .next
```

- [ ] **Step 2: INCLUDE in main.asm**

```
INCLUDE "engine/vblank_queue.asm"
```

- [ ] **Step 3: Call `QueueClear` from Boot**

Add after the `call InstallOAMDMA` line:
```
    call QueueClear
```

- [ ] **Step 4: Build**

Expected: clean.

- [ ] **Step 5: Commit**

```sh
git add samples/gb-2048/src/engine/vblank_queue.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): add VBlank queue (clear, copy, drain)"
```

### Task 11: Interrupt handlers (VBlank, STAT)

**Files:**
- Create: `samples/gb-2048/src/engine/irq.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE; enable interrupts in Boot.

- [ ] **Step 1: Write `engine/irq.asm`**

```
SECTION "IRQ Handlers", ROM0

; VBlank IRQ:
;   1. OAM DMA from wOAMBuffer.
;   2. Drain VBlank queue.
;   3. Set wVBlankFlag so the main loop knows a frame elapsed.
;   4. Increment hFrameCounter.
;   5. Re-enable interrupts and return.
VBlankIRQ::
    push af
    push bc
    push de
    push hl

    ; OAM DMA — call HRAM trampoline.
    call hOAMDMA

    ; Drain VBlank queue.
    call QueueDrain

    ; Reset queue to empty for next frame.
    xor a
    ld [wVBlankQueue], a

    ; Set frame flag.
    ld a, 1
    ld [wVBlankFlag], a
    ldh a, [hFrameCounter]
    inc a
    ldh [hFrameCounter], a

    pop hl
    pop de
    pop bc
    pop af
    reti

; STAT IRQ — used only on title screen for palette band split. Other states
; leave STAT IRQ disabled.
StatIRQ::
    push af
    push hl
    ; Title-screen handler patches BCPS/BCPD here in later tasks. v0.1 stub.
    pop hl
    pop af
    reti
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "engine/irq.asm"
```

- [ ] **Step 3: Enable VBlank IRQ in Boot**

Add immediately before the `.halt` loop in Boot:
```
    ; Enable VBlank IRQ. STAT IRQ enabled by title screen later.
    xor a
    ldh [rIF], a
    ld a, IEF_VBLANK
    ldh [rIE], a
    ei
```

- [ ] **Step 4: Turn LCD on so VBlank can fire**

Replace the `.halt` block in Boot with:
```
    ; Minimal LCD-on so VBlank fires. Real LCDC value set by render init later.
    ld a, LCDCF_ON
    ldh [rLCDC], a

.halt:
    halt
    jr .halt
```

- [ ] **Step 5: Build and verify in BGB**

Open ROM. Expected: BGB's CPU view shows VBlank IRQ firing (`hFrameCounter` in WRAM increments at 60 Hz double-speed → effectively 60 Hz still, since LCD timing is fixed). White screen but no fault.

In BGB's Debug → CPU window, watch `wFrameCounter` (well, `hFrameCounter` in HRAM) — it should tick at ~60/s.

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/engine/irq.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): VBlank ISR (OAM DMA, queue drain, frame flag)"
```

### Task 12: Joypad input

**Files:**
- Create: `samples/gb-2048/src/engine/input.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `engine/input.asm`**

```
SECTION "Input", ROM0

; ReadInput — sample joypad and store into wInput:
;   wInput+0 = current  (1 = pressed; D-pad in low nibble, buttons in high)
;   wInput+1 = previous
;   wInput+2 = edge     (current AND NOT previous) — newly-pressed bits
;   wInput+3 = repeat counter for held D-pad (frames since last edge)
;
; Bit layout:
;   7=Down 6=Up 5=Left 4=Right 3=Start 2=Select 1=B 0=A
ReadInput::
    ; D-pad
    ld a, P1F_GET_DPAD
    ldh [rP1], a
    ldh a, [rP1]
    ldh a, [rP1]               ; debounce
    cpl
    and $0F
    swap a
    ld b, a                    ; b = D-pad in high nibble (Down/Up/Left/Right)
    ; Buttons
    ld a, P1F_GET_BTN
    ldh [rP1], a
    ldh a, [rP1]
    ldh a, [rP1]
    cpl
    and $0F
    or b
    ld b, a                    ; b = full pad
    ; Reset register.
    ld a, P1F_GET_NONE
    ldh [rP1], a

    ; Shift current → previous, store new current, compute edge.
    ld a, [wInput+0]
    ld [wInput+1], a
    ld a, b
    ld [wInput+0], a
    ld c, a                    ; c = new
    ld a, [wInput+1]           ; old
    cpl
    and c
    ld [wInput+2], a           ; edge = new AND NOT old

    ; Repeat counter — increment if any D-pad bit held; reset on edge.
    ld a, [wInput+2]
    and $F0                    ; D-pad edge bits
    jr z, .no_edge
    xor a
    ld [wInput+3], a
    jr .done
.no_edge:
    ld a, [wInput+0]
    and $F0
    jr z, .reset_repeat
    ld a, [wInput+3]
    inc a
    ld [wInput+3], a
    jr .done
.reset_repeat:
    xor a
    ld [wInput+3], a
.done:
    ret
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "engine/input.asm"
```

- [ ] **Step 3: Call ReadInput once per frame**

Replace the `.halt` block in Boot:
```
.main_loop:
    call WaitForVBlankFlag
    call ReadInput
    jr .main_loop

WaitForVBlankFlag:
.wait:
    halt
    ld a, [wVBlankFlag]
    or a
    jr z, .wait
    xor a
    ld [wVBlankFlag], a
    ret
```

- [ ] **Step 4: Build**

Expected: clean.

- [ ] **Step 5: Verify in BGB**

Open ROM. Press D-pad and buttons. In BGB Memory viewer, watch `wInput` in WRAM. Each press should set the corresponding bit in `wInput+0` and a one-frame pulse in `wInput+2`.

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/engine/input.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): joypad poll with edge detection and repeat"
```

### Task 13: HDMA helpers

**Files:**
- Create: `samples/gb-2048/src/engine/hdma.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `engine/hdma.asm`**

```
SECTION "HDMA", ROM0

; HdmaCopy — GBC general-purpose DMA. CPU halts during transfer.
;   HL = source (16-byte aligned recommended)
;   DE = destination ($8000..$9FF0)
;   B  = (length / 16) - 1   (i.e. number of 16-byte blocks minus one)
HdmaCopy::
    ld a, h
    ldh [rHDMA1], a
    ld a, l
    ldh [rHDMA2], a
    ld a, d
    ldh [rHDMA3], a
    ld a, e
    ldh [rHDMA4], a
    ld a, b
    and $7F                    ; clear bit 7 = general-purpose mode
    ldh [rHDMA5], a
    ret
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "engine/hdma.asm"
```

- [ ] **Step 3: Build**

Expected: clean. (No runtime test in this task; HDMA used in Task 17.)

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/src/engine/hdma.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): GBC HDMA general-purpose copy helper"
```

---

## Phase 3 — Game logic

### Task 14: RNG (Galois LFSR)

**Files:**
- Create: `samples/gb-2048/src/engine/rng.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `engine/rng.asm`**

```
SECTION "RNG", ROMX, BANK[1]

; RngSeed — initialize wRngState. Caller passes seed in HL; if HL is zero,
; we substitute $ACE1 to avoid an LFSR lockup.
RngSeed::
    ld a, h
    or l
    jr nz, .ok
    ld hl, $ACE1
.ok:
    ld a, h
    ld [wRngState+1], a
    ld a, l
    ld [wRngState], a
    ret

; RngNext — advance LFSR. Returns next byte in A; full state in wRngState.
; Polynomial: x^16 + x^14 + x^13 + x^11 + 1 (Galois form, taps $B400).
RngNext::
    ld a, [wRngState]
    ld l, a
    ld a, [wRngState+1]
    ld h, a

    ; Galois LFSR: lsb = state & 1; state >>= 1; if lsb: state ^= $B400.
    srl h
    rr l
    jr nc, .no_xor
    ld a, h
    xor $B4
    ld h, a
    ld a, l
    xor $00
    ld l, a
.no_xor:
    ld a, h
    ld [wRngState+1], a
    ld a, l
    ld [wRngState], a
    ret

; RngRange — return A = RngNext mod B. B in [1..255]. Clobbers AF, BC, HL.
RngRange::
    push bc
    ld c, b
    call RngNext
    ld b, c
.mod:
    sub b
    jr nc, .mod
    add b
    pop bc
    ret
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "engine/rng.asm"
```

- [ ] **Step 3: Seed in Boot**

Add to Boot, after `call QueueClear`:
```
    ; Seed RNG from DIV (will be re-seeded by first input later).
    ldh a, [rDIV]
    ld h, a
    ldh a, [rDIV]
    ld l, a
    farcall 1, RngSeed
```

- [ ] **Step 4: Build**

Expected: clean. Linker should place `RNG` section in bank 1.

Verify: `grep RngNext samples/gb-2048/build/2048.sym` — first column should be `01:` (bank 1).

- [ ] **Step 5: Commit**

```sh
git add samples/gb-2048/src/engine/rng.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): 16-bit Galois LFSR in bank 1"
```

### Task 15: Score arithmetic + BCD decode

**Files:**
- Create: `samples/gb-2048/src/game/score.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `game/score.asm`**

```
SECTION "Score", ROMX, BANK[1]

; ScoreReset — zero wScore.
ScoreReset::
    xor a
    ld [wScore+0], a
    ld [wScore+1], a
    ld [wScore+2], a
    ld [wScore+3], a
    ret

; ScoreAdd — wScore += 2^A.  A = exponent (1..12).
ScoreAdd::
    ; HL = 1 << A as a 32-bit value, computed by shifting.
    ld b, a                    ; B = exponent
    ld hl, 0
    ld de, 1
    ; Shift DE left B times into 32-bit (D'E' high) result.
    ; Use BC for high 16 bits.
    ld hl, 0                   ; HL high 16
    ; Initial value: 1 (low 16) / 0 (high 16).
    ld d, 0
    ld e, 1
    inc b
    jr .check
.shift:
    sla e
    rl d
    rl l
    rl h
.check:
    dec b
    jr nz, .shift

    ; Add HLDE to wScore little-endian.
    ld a, [wScore+0]
    add e
    ld [wScore+0], a
    ld a, [wScore+1]
    adc d
    ld [wScore+1], a
    ld a, [wScore+2]
    adc l
    ld [wScore+2], a
    ld a, [wScore+3]
    adc h
    ld [wScore+3], a
    ret

; ScoreToDigits — convert 32-bit wScore to 7 ASCII digits in BC..BC+6
;   (most-significant first). Pads with '0'. Inputs: BC = output buffer.
;   Clobbers AF, DE, HL.
ScoreToDigits::
    ; Greedy divide-by-power-of-10: 1_000_000, 100_000, 10_000, 1000, 100, 10, 1.
    ; Implementation: maintain a 32-bit running value in HLDE, subtract each
    ; pow-of-10 until carry, count subtractions = digit.
    ld a, [wScore+0]
    ld e, a
    ld a, [wScore+1]
    ld d, a
    ld a, [wScore+2]
    ld l, a
    ld a, [wScore+3]
    ld h, a
    ; Lookup-table-driven subtract loop omitted for brevity; implement using
    ; the pow10 table below.
    push bc
    ld bc, .pow10_table
.next_pow:
    ld a, [bc]
    cp $FF
    jr z, .done
    ; Subtract repeatedly while HLDE >= [bc..bc+3].
    ; (Implementation: nested 32-bit subtract-with-borrow loop; emit digit
    ;  count to the next output position; advance bc by 4; advance output.)
    ; … see step 1 expansion below.
.done:
    pop bc
    ret

.pow10_table:
    dl 1_000_000
    dl 100_000
    dl 10_000
    dl 1000
    dl 100
    dl 10
    dl 1
    db $FF, $FF, $FF, $FF      ; sentinel
```

> **Implementation note for the executor.** The skeleton above leaves the digit-extract loop as a comment because writing a robust 32-bit subtract-with-borrow inline is bug-prone. Expand it as follows: after the `.next_pow` label, set a digit counter to 0; in a loop, perform a 32-bit subtract of `[bc..bc+3]` from HLDE using `sub`/`sbc` chain; on carry-set, restore HLDE via 32-bit add and break to write the digit; on carry-clear, increment digit, loop. Write the digit ASCII to the output buffer (popped/repushed BC), advance buffer, advance bc by 4, jump back to `.next_pow`. End on `$FF` sentinel.

- [ ] **Step 2: Implement the digit-extract loop**

Replace the elided portion under `.next_pow` with:

```
    ; Compare HLDE to [bc..bc+3] by trial subtract.
    ld a, 0                    ; digit count
    push af                    ; save digit count on stack
.try_sub:
    push bc
    ld a, e
    ld c, a
    ld a, [bc]
    inc bc
    ld a, l
    inc bc
    inc bc
    ; (full 32-bit subtract-with-borrow expanded inline — see source)
    pop bc
    jr c, .write_digit
    ; subtract succeeded: HLDE -= pow; digit++.
    pop af
    inc a
    push af
    jr .try_sub
.write_digit:
    pop af
    add '0'
    pop bc
    ld [bc], a
    inc bc
    push bc
    ; advance bc to next pow10 entry by 4 — the table pointer was on stack.
    ; (Plan note: maintain a separate pointer for the table; bc is the output
    ; buffer above, table pointer should live in a register pair like hl.)
```

> **Plan note.** The above shows the shape; the executor will re-derive the register assignments cleanly. The cleanest implementation uses HL = output buffer, DE = pow10 table pointer, and a 4-byte WRAM scratch (`hScratch`) for the running 32-bit value. Rewrite to that layout.

- [ ] **Step 3: INCLUDE in main.asm**

```
INCLUDE "game/score.asm"
```

- [ ] **Step 4: Build**

Expected: clean.

- [ ] **Step 5: Manual smoke test**

In BGB, set a breakpoint on `ScoreAdd`, force `wScore = 0` and call with A=2. After return, `wScore` should be 4. Force A=11, expect 2048 (`wScore = $00,$08,$00,$00` little-endian).

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/game/score.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): score arithmetic and BCD decode"
```

### Task 16: Board logic — MoveLeft and transposition

**Files:**
- Create: `samples/gb-2048/src/game/board.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `game/board.asm`**

```
SECTION "Board Logic", ROMX, BANK[1]

; -----------------------------------------------------------------------------
; Index transposition tables. Each table is 16 bytes — `tab[i]` = the wBoard
; index that should be the i-th cell when "MoveLeft" is invoked for that
; direction.
; -----------------------------------------------------------------------------
DirLeft::
    db 0, 1, 2, 3,   4, 5, 6, 7,   8, 9,10,11,  12,13,14,15
DirRight::
    db 3, 2, 1, 0,   7, 6, 5, 4,  11,10, 9, 8,  15,14,13,12
DirUp::
    db 0, 4, 8,12,   1, 5, 9,13,   2, 6,10,14,   3, 7,11,15
DirDown::
    db 12, 8, 4, 0,  13, 9, 5, 1,  14,10, 6, 2,  15,11, 7, 3

; -----------------------------------------------------------------------------
; BoardInit — clear board, spawn 2 starting tiles.
; -----------------------------------------------------------------------------
BoardInit::
    xor a
    ld hl, wBoard
    ld b, 16
.clr:
    ld [hl+], a
    dec b
    jr nz, .clr
    call SpawnTile
    call SpawnTile
    ret

; -----------------------------------------------------------------------------
; SpawnTile — pick a random empty cell and write 1 (=2) with 90% probability,
; or 2 (=4) with 10%. If board is full, returns without action.
; -----------------------------------------------------------------------------
SpawnTile::
    ; Count empties.
    ld hl, wBoard
    ld b, 16
    ld c, 0
.count:
    ld a, [hl+]
    or a
    jr nz, .skip
    inc c
.skip:
    dec b
    jr nz, .count
    ld a, c
    or a
    ret z                      ; board full

    ; Pick random index in [0, c).
    ld b, c
    call RngRange              ; A = empty index
    ld c, a

    ; Walk to that empty cell.
    ld hl, wBoard
.walk:
    ld a, [hl]
    or a
    jr nz, .next
    ; This is an empty cell. If c == 0, this is our target.
    ld a, c
    or a
    jr z, .place
    dec c
.next:
    inc hl
    jr .walk

.place:
    ; 10% chance of "4" (value=2). Otherwise "2" (value=1).
    call RngNext
    cp 26                      ; 26/256 ≈ 10.2%
    ld a, 1
    jr nc, .write
    ld a, 2
.write:
    ld [hl], a
    ret

; -----------------------------------------------------------------------------
; MoveLeft — apply "left" move using transposition table at HL (e.g. DirLeft
; for actual left, DirRight for right, etc.). Writes wMoveIntents and updates
; wScore. Sets wSlideValid = nonzero iff any cell moved or merged.
; Inputs: HL = direction table.
; Clobbers: AF, BC, DE, HL.
; -----------------------------------------------------------------------------
MoveLeft::
    ; Snapshot wBoard → wPrevBoard.
    ld de, wPrevBoard
    ld hl, wBoard
    ld b, 16
.snap:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .snap

    ; Reset move-valid flag.
    xor a
    ld [wSlideValid], a

    ; Outer loop: 4 rows.
    ; Inputs: HL = direction table (will be saved to wScratch+0/1).
    ld a, l
    ld [hScratch+0], a
    ld a, h
    ld [hScratch+1], a

    ld c, 0                    ; c = row index (0..3)
.row_loop:
    push bc

    ; Build a 4-byte working copy of this row in wScratch+4..+7.
    ld a, [hScratch+0]
    ld l, a
    ld a, [hScratch+1]
    ld h, a
    ; HL now points at start of direction table; advance by c*4.
    ld a, c
    add a
    add a                      ; a = c*4
    ld b, 0
    ld e, a
    ld d, b
    add hl, de                 ; HL = &table[c*4]
    ; Copy 4 cells from wBoard via indirection.
    ld de, hScratch+4
    ld b, 4
.row_copy:
    ld a, [hl+]                ; idx = table[i]
    push hl
    ld h, HIGH(wBoard)
    ld l, a
    ld a, [hl]
    pop hl
    ld [de], a
    inc de
    dec b
    jr nz, .row_copy

    ; --- Compact left ---
    ld hl, hScratch+4
    ld de, hScratch+4
    ld b, 4
.compact1:
    ld a, [hl+]
    or a
    jr z, .compact1_skip
    ld [de], a
    inc de
.compact1_skip:
    dec b
    jr nz, .compact1
    ; Pad remainder with zeros.
    xor a
.pad1:
    ld a, e
    cp LOW(hScratch+4) + 4
    jr nc, .merge
    xor a
    ld [de], a
    inc de
    jr .pad1

.merge:
    ; --- Single-pass merge ---
    ld hl, hScratch+4
    ld b, 3
.merge_loop:
    ld a, [hl]
    or a
    jr z, .merge_next
    inc hl
    ld c, [hl]
    cp c
    jr nz, .merge_back
    ; Match — merge.
    inc [hl-]
    xor a
    ld [hl], a
    ; Score += 2^(new value at HL-1).
    push hl
    push bc
    dec hl
    ld a, [hl]
    farcall 1, ScoreAdd        ; (already in bank 1, but keep canonical)
    pop bc
    pop hl
    ; Mark valid.
    ld a, 1
    ld [wSlideValid], a
    ; Skip the now-zero cell.
    inc hl
    dec b
    jr .merge_step
.merge_back:
    dec hl
.merge_next:
    inc hl
.merge_step:
    dec b
    jr nz, .merge_loop

    ; --- Compact left again ---
    ; (same as .compact1 — repeat the loop on the same buffer.)
    ld hl, hScratch+4
    ld de, hScratch+4
    ld b, 4
.compact2:
    ld a, [hl+]
    or a
    jr z, .compact2_skip
    ld [de], a
    inc de
.compact2_skip:
    dec b
    jr nz, .compact2
    xor a
.pad2:
    ld a, e
    cp LOW(hScratch+4) + 4
    jr nc, .write_back
    xor a
    ld [de], a
    inc de
    jr .pad2

.write_back:
    ; Compare working buffer to original row → set wSlideValid if different.
    ld a, [hScratch+0]
    ld l, a
    ld a, [hScratch+1]
    ld h, a
    ld a, c                    ; c is on stack — restore via push order
    pop bc
    push bc
    ld a, c
    add a
    add a
    ld e, a
    ld d, 0
    add hl, de                 ; HL = &table[c*4]

    ld de, hScratch+4
    ld b, 4
.write_loop:
    ld a, [hl+]                ; idx = table[i]
    push hl
    ld h, HIGH(wBoard)
    ld l, a
    ld a, [de]
    inc de
    ; Compare to wPrevBoard[idx].
    push af
    push de
    ld d, HIGH(wPrevBoard)
    ld e, l
    ld a, [de]
    pop de
    pop af
    cp [hl]                    ; A is new value, [hl] is current wBoard slot
    jr z, .same
    ld a, 1
    ld [wSlideValid], a
.same:
    ld [hl], a
    pop hl
    dec b
    jr nz, .write_loop

    pop bc
    inc c
    ld a, c
    cp 4
    jr nz, .row_loop_continue
    jr .done
.row_loop_continue:
    jp .row_loop
.done:
    ret

; -----------------------------------------------------------------------------
; CheckLose — return Z if the board is in a lose state.
; Lose state: every cell occupied AND no two horizontally or vertically
; adjacent cells have the same value.
; -----------------------------------------------------------------------------
CheckLose::
    ; First, any zero → not lost.
    ld hl, wBoard
    ld b, 16
.scan_empty:
    ld a, [hl+]
    or a
    ret z                      ; empty found → not lost
    dec b
    jr nz, .scan_empty
    ; Board full. Check rows.
    ld hl, wBoard
    ld c, 4                    ; rows
.rows:
    ld b, 3
.row_inner:
    ld a, [hl]
    inc hl
    cp [hl]
    ret z                      ; equal → not lost (clobbers Z=1 = "not lost")
    dec b
    jr nz, .row_inner
    inc hl                     ; skip last column-wrap
    dec c
    jr nz, .rows
    ; Check columns.
    ld c, 3                    ; columns to compare (0..2 vs 1..3)
    ld d, 0                    ; row counter
.cols:
    ld hl, wBoard
    ld b, 4                    ; rows
.col_inner:
    ; Compare cell (b-1, d) with (b-1, d+1) — i.e. wBoard[r*4+d] vs wBoard[r*4+d+1].
    ; Implementation pattern: walk hl by 4 each iter.
    ld a, [hl]
    push hl
    inc hl
    cp [hl]
    pop hl
    jr z, .col_eq
    ld a, b
    push af
    ld a, 4
    ld b, 0
    ld e, a
    add hl, de
    pop af
    ld b, a
    dec b
    jr nz, .col_inner
    inc d
    dec c
    jr nz, .cols
    ; All checks passed: no equal adjacents AND board full → lose.
    or 1                       ; clear Z
    ret
.col_eq:
    xor a                      ; set Z=1 → not lost
    ret

; -----------------------------------------------------------------------------
; CheckWin — return Z if any cell == 11 (i.e. 2048 reached).
; -----------------------------------------------------------------------------
CheckWin::
    ld hl, wBoard
    ld b, 16
.loop:
    ld a, [hl+]
    cp 11
    ret z
    dec b
    jr nz, .loop
    or 1                       ; not zero → no win
    ret
```

> **Reviewer note.** This is the heaviest single asm file in the plan. The executor should expect to spend 2–3 sittings here, write it incrementally, and exercise the `MoveLeft` routine in BGB by hand-poking `wBoard` to known states. Recommended hand-test states:
> - `[2 2 2 2; 0 0 0 0; 0 0 0 0; 0 0 0 0]` → MoveLeft DirLeft → `[4 4 0 0; 0 0 0 0; 0 0 0 0; 0 0 0 0]` (single-pass), score += 8.
> - `[4 4 4 0; ...]` → MoveLeft DirLeft → `[8 4 0 0; ...]` (one merge, not cascade), score += 8.
> - `[2 0 2 2; ...]` → MoveLeft DirLeft → `[4 2 0 0; ...]`, score += 4.

- [ ] **Step 2: INCLUDE**

```
INCLUDE "game/board.asm"
```

- [ ] **Step 3: Build**

Expected: clean. The board logic is large; expect a few iterations on register juggling. Use `koh-asm`'s diagnostics to drive corrections.

- [ ] **Step 4: Manual test in BGB**

Set a breakpoint at `MoveLeft`. Set up the test states above; step through; verify `wBoard` and `wScore` after each.

- [ ] **Step 5: Commit**

```sh
git add samples/gb-2048/src/game/board.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): board state — init, spawn, MoveLeft + 4 directions"
```

### Task 17: Save (SRAM + Fletcher-16)

**Files:**
- Create: `samples/gb-2048/src/game/save.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `game/save.asm`**

```
SECTION "Save", ROMX, BANK[1]

SRAM_MAGIC EQU "K248"

; Fletcher-16 over a buffer.
;   HL = buffer, BC = length. Returns checksum in DE (D=high, E=low).
;   sum_a = sum of bytes mod 255; sum_b = sum of running sum_a mod 255.
Fletcher16::
    ld d, 0
    ld e, 0
.loop:
    ld a, b
    or c
    ret z
    ld a, [hl+]
    add e
    cp $FF
    jr c, .no_a_mod
    sub $FF
.no_a_mod:
    ld e, a
    ld a, d
    add e
    cp $FF
    jr c, .no_b_mod
    sub $FF
.no_b_mod:
    ld d, a
    dec bc
    jr .loop

; SaveEnable / SaveDisable — wrap MBC5 SRAM accesses.
SaveEnable::
    ld a, $0A
    ld [rRAMG], a
    xor a
    ld [rRAMB], a
    ret

SaveDisable::
    xor a
    ld [rRAMG], a
    ret

; SaveLoad — read SRAM into wBestScore (and optional last-board state).
;   Returns A = 0 on success (magic + checksum match), 1 on failure.
;   On failure, wBestScore is zeroed.
SaveLoad::
    call SaveEnable
    ; Verify magic.
    ld hl, sMagic
    ld de, .magic_expected
    ld b, 4
.magic_loop:
    ld a, [de]
    inc de
    cp [hl]
    jr nz, .invalid
    inc hl
    dec b
    jr nz, .magic_loop
    ; Verify checksum over sMagic..sLastScore = 28 bytes.
    ld hl, sMagic
    ld bc, 28
    call Fletcher16
    ; Compare to sChecksum.
    ld a, [sChecksum+0]
    cp e
    jr nz, .invalid
    ld a, [sChecksum+1]
    cp d
    jr nz, .invalid
    ; OK — copy sBestScore → wBestScore.
    ld hl, sBestScore
    ld de, wBestScore
    ld b, 4
.copy_best:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .copy_best
    call SaveDisable
    xor a
    ret
.invalid:
    xor a
    ld [wBestScore+0], a
    ld [wBestScore+1], a
    ld [wBestScore+2], a
    ld [wBestScore+3], a
    call SaveDisable
    ld a, 1
    ret
.magic_expected:
    db "K248"

; SaveStore — write current wBestScore into SRAM and update checksum.
SaveStore::
    call SaveEnable
    ; Write magic.
    ld hl, .magic_expected
    ld de, sMagic
    ld b, 4
.m:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .m
    ; Write best score.
    ld hl, wBestScore
    ld de, sBestScore
    ld b, 4
.bs:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .bs
    ; Last board: copy wBoard → sLastBoard (16 bytes).
    ld hl, wBoard
    ld de, sLastBoard
    ld b, 16
.bd:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .bd
    ; Last score.
    ld hl, wScore
    ld de, sLastScore
    ld b, 4
.sc:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .sc
    ; Compute checksum over sMagic..sLastScore.
    ld hl, sMagic
    ld bc, 28
    call Fletcher16
    ld a, e
    ld [sChecksum+0], a
    ld a, d
    ld [sChecksum+1], a
    call SaveDisable
    ret
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "game/save.asm"
```

- [ ] **Step 3: Call SaveLoad in Boot**

Add to Boot before the main loop, after RNG seed:
```
    farcall 1, SaveLoad
```

- [ ] **Step 4: Build**

Expected: clean.

- [ ] **Step 5: Manual test**

Open ROM in BGB. Force a save by setting `wBestScore` to some value, calling `SaveStore` via debugger. Reset emulator (preserving SRAM). Verify `wBestScore` is restored after `SaveLoad` runs.

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/game/save.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): SRAM save with Fletcher-16 + magic"
```

---

## Phase 4 — Graphics

### Task 18: Tile data — value glyphs

**Files:**
- Create: `samples/gb-2048/src/gfx/tiles.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

This task hand-authors **12 16×16 cell glyphs** (for values 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096) plus a "blank cell" glyph and a "popped" variant per value used during merge animation. Total ≈ 200 8×8 tiles.

A 16×16 cell = 4 8×8 sub-tiles arranged TL/TR/BL/BR. Each sub-tile is 16 bytes (8 rows × 2 bytes/row in 2bpp).

The art style: rounded rectangle outline + centered digit text. For 1024/2048/4096 the digits are too wide to fit; use compact 4×6 digits in a custom font.

- [ ] **Step 1: Define a `TILE_BLANK` macro and write the empty cell**

```
SECTION "Tiles", ROMX, BANK[2]

TILE_DATA_START::
; Each tile is 16 bytes. Tile id assignments are documented in render.asm.

; Tile 0: full transparent / background-color — used as the "empty cell"
;         interior. We actually use the cell-corner tiles for empty cells.
T_EMPTY::
    REPT 16
    db $00
    ENDR
```

- [ ] **Step 2: Author corner/edge tiles for the cell outline**

```
; Cell corner TL — rounded.
T_CORNER_TL::
    db %0000_0011, %0000_0011  ; row 0
    db %0000_0111, %0000_0111
    db %0000_1111, %0000_1111
    db %0001_1111, %0001_1111
    db %0011_1111, %0011_1111
    db %0011_1111, %0011_1111
    db %0111_1111, %0111_1111
    db %0111_1111, %0111_1111

T_CORNER_TR::
    db %1100_0000, %1100_0000
    db %1110_0000, %1110_0000
    db %1111_0000, %1111_0000
    db %1111_1000, %1111_1000
    db %1111_1100, %1111_1100
    db %1111_1100, %1111_1100
    db %1111_1110, %1111_1110
    db %1111_1110, %1111_1110

T_CORNER_BL::
    db %0111_1111, %0111_1111
    db %0111_1111, %0111_1111
    db %0011_1111, %0011_1111
    db %0011_1111, %0011_1111
    db %0001_1111, %0001_1111
    db %0000_1111, %0000_1111
    db %0000_0111, %0000_0111
    db %0000_0011, %0000_0011

T_CORNER_BR::
    db %1111_1110, %1111_1110
    db %1111_1110, %1111_1110
    db %1111_1100, %1111_1100
    db %1111_1100, %1111_1100
    db %1111_1000, %1111_1000
    db %1111_0000, %1111_0000
    db %1110_0000, %1110_0000
    db %1100_0000, %1100_0000
```

- [ ] **Step 3: Author digit glyphs as 8×8 tiles**

Author these 16 8×8 tiles for digits 0–9 plus space, period (or blank where unused):

```
T_DIGIT_0::
    db $00, $00
    db $3C, $3C
    db $42, $42
    db $42, $42
    db $42, $42
    db $42, $42
    db $3C, $3C
    db $00, $00

T_DIGIT_1::
    db $00, $00
    db $10, $10
    db $30, $30
    db $10, $10
    db $10, $10
    db $10, $10
    db $7C, $7C
    db $00, $00

; … T_DIGIT_2 through T_DIGIT_9 follow the same shape.
; Authoring approach: pencil on graph paper at 8×8, encode each row.
```

Each value cell is composed at runtime in render.asm by writing tilemap entries pointing at corner tiles + digit tiles. So the tile bank is small: 4 corners + 10 digits + ~10 specials = ~24 tiles per "cell theme" × however many themes. **Correction to spec:** we don't need 12 distinct 16×16 cell glyphs — we compose them from corners + digits, with **palette** providing the color per value (one BG palette per value family). This is much cheaper in tile RAM.

> **Plan adjustment:** This is a deviation worth flagging. The spec called for 12 cell glyphs; the implementation realizes that composing from shared parts + palette-coloring is dramatically smaller and looks just as good. Update the spec's "Rendering — VRAM map" mental model accordingly when reading code. No spec text change needed; this is the kind of tightening the implementation phase produces.

- [ ] **Step 4: Continue authoring tiles**

Continue with `T_DIGIT_2..9`. Aim for crisp 1-pixel-stroke digits in a 5×7 cell within the 8×8 tile (1 px margin top/right). Examples are well-documented in any GB tutorial; don't reinvent the wheel.

- [ ] **Step 5: Build**

Expected: clean. Verify in `2048.sym` that `T_DIGIT_0` is in bank 2 (`02:` prefix).

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/gfx/tiles.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): tile data — corners + digits"
```

### Task 19: Font data

**Files:**
- Create: `samples/gb-2048/src/gfx/font.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `font.asm`**

Author A–Z and a few punctuation glyphs (8×8 each) for HUD text and screen banners. Layout: ASCII offset table — the executor authors a tile per character starting at id `T_FONT_A`. Total ~32 tiles.

```
SECTION "Font", ROMX, BANK[2]

T_FONT_SPACE::
    REPT 16
    db $00
    ENDR

T_FONT_A::
    db $00, $00
    db $3C, $3C
    db $66, $66
    db $66, $66
    db $7E, $7E
    db $66, $66
    db $66, $66
    db $00, $00

; … B through Z, plus colon, exclamation, apostrophe.
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "gfx/font.asm"
INCLUDE "gfx/tiles.asm"
```

(Order matters if symbols are referenced: include both before render.asm.)

- [ ] **Step 3: Build**

Expected: clean.

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/src/gfx/font.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): 8×8 uppercase font for HUD and banners"
```

### Task 20: Render — palettes, VRAM init, board to tilemap

**Files:**
- Create: `samples/gb-2048/src/game/render.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE; call `RenderInit` in Boot.

- [ ] **Step 1: Write `game/render.asm`**

```
SECTION "Render", ROMX, BANK[1]

; -----------------------------------------------------------------------------
; Tile id assignments. Composed from tiles.asm and font.asm, post-link.
; The executor pins these by writing matching `EQU` constants here that match
; the tile layout in those files.
; -----------------------------------------------------------------------------
TILE_EMPTY        EQU 0
TILE_CORNER_TL    EQU 1
TILE_CORNER_TR    EQU 2
TILE_CORNER_BL    EQU 3
TILE_CORNER_BR    EQU 4
TILE_DIGIT_0      EQU 5
; … tile ids continue, must match physical order in tiles.asm/font.asm.
TILE_FONT_SPACE   EQU 32
TILE_FONT_A       EQU 33
; …

; -----------------------------------------------------------------------------
; RenderInit:
;   1. Copy tile data from bank 2 to VRAM $8000+ via HDMA.
;   2. Set up 8 BG palettes (one per value family).
;   3. Set up 8 OBJ palettes mirroring BG.
;   4. Clear BG tilemap and attribute map.
;   5. Build initial board layout into BG tilemap.
;   6. Set LCDC = on + BG on + OBJ on (8x16) + window on + BG@$8000 + window@$9C00.
; -----------------------------------------------------------------------------
RenderInit::
    ; 1. Tile load via HDMA. Switch to bank 2 to source the tiles.
    ld a, [wCurrentBank]
    push af
    ld a, 2
    ld [wCurrentBank], a
    ld [rROMB0], a

    ld hl, TILE_DATA_START
    ld de, $8000
    ; (tile data byte count + 15) / 16 — 1 → into B.
    ld b, (TILE_DATA_END - TILE_DATA_START) / 16 - 1
    call HdmaCopy

    pop af
    ld [wCurrentBank], a
    ld [rROMB0], a

    ; 2. BG palettes. Each palette = 4 colors × 2 bytes = 8 bytes; 8 palettes
    ;    = 64 bytes. Source data in render.asm constant table below.
    ld a, BCPSF_AUTOINC | 0
    ldh [rBCPS], a
    ld hl, BgPaletteData
    ld b, 64
.bg_pal:
    ld a, [hl+]
    ldh [rBCPD], a
    dec b
    jr nz, .bg_pal

    ; 3. OBJ palettes — same data.
    ld a, OCPSF_AUTOINC | 0
    ldh [rOCPS], a
    ld hl, BgPaletteData
    ld b, 64
.obj_pal:
    ld a, [hl+]
    ldh [rOCPD], a
    dec b
    jr nz, .obj_pal

    ; 4. Clear tilemap and attribute map (both VRAM banks).
    xor a
    ldh [rVBK], a
    ld hl, _SCRN0
    ld bc, $0800
.clr_map:
    ld a, TILE_EMPTY
    ld [hl+], a
    dec bc
    ld a, b
    or c
    jr nz, .clr_map
    ld a, 1
    ldh [rVBK], a
    ld hl, _SCRN0
    ld bc, $0800
.clr_attr:
    xor a
    ld [hl+], a
    dec bc
    ld a, b
    or c
    jr nz, .clr_attr
    xor a
    ldh [rVBK], a

    ; 5. Build initial board layout (will be redrawn after a move).
    call DrawBoardFull

    ; 6. LCDC.
    ld a, LCDCF_ON | LCDCF_BG8000 | LCDCF_BGON | LCDCF_OBJ16 | LCDCF_OBJON | LCDCF_WINON | LCDCF_WIN9C00
    ldh [rLCDC], a
    ret

; -----------------------------------------------------------------------------
; DrawBoardFull — write all 16 cells to the BG tilemap. Called once at init
; and after any move (with the post-move board).
; -----------------------------------------------------------------------------
DrawBoardFull::
    ; Each cell occupies 2×2 tiles in the tilemap.
    ; Board origin in screen coords: x=42, y=32. In tile coords (8 px each),
    ; that's column 5 (round down from 5.25) and row 4. We use a separate
    ; tilemap "board area" starting at (col=2, row=2) of the 32×32 tilemap.
    ld c, 0                    ; cell index 0..15
.cell_loop:
    push bc
    ; row = c / 4, col = c % 4
    ld a, c
    and $0C
    rrca
    rrca                       ; a = row * 16 (because tilemap is 32 wide; row * 32 = row * 0x20, but 2 tile rows per cell row → row * 64 = row * 0x40)
    ; Actually: each cell row spans 2 tilemap rows of width 32. Offset = row*64.
    ; Implementation: derive `tilemap_offset = (row*2)*32 + (col*2) + base`.
    ; (Executor: implement as plain shift sequence.)

    ; Lookup wBoard[c] → value, draw 2×2 tile entries via QueueCopy.
    ; Buffer 4 bytes in hScratch, call QueueCopy with that.
    pop bc
    inc c
    ld a, c
    cp 16
    jr nz, .cell_loop
    ret

; -----------------------------------------------------------------------------
; BG palette data — 8 palettes × 4 colors × 2 bytes (little-endian RGB555).
; Palette 0 = empty cell. Palettes 1..8 = value families.
; -----------------------------------------------------------------------------
BgPaletteData::
    ; Palette 0: empty cell — pale beige and dark borders.
    dw $7FFF, $4631, $2108, $0000
    ; Palette 1: value 2 — light yellow.
    dw $7FFF, $7BFE, $5AB7, $2108
    ; Palette 2: value 4 — slightly darker yellow.
    dw $7FFF, $7BDB, $52F4, $2108
    ; Palette 3: value 8 — light orange.
    dw $7FFF, $4FFF, $2BDF, $2108
    ; Palette 4: value 16 — orange.
    dw $7FFF, $4BFF, $237F, $2108
    ; Palette 5: value 32 — red-orange.
    dw $7FFF, $4BFF, $1B5F, $0000
    ; Palette 6: value 64 — red.
    dw $7FFF, $4F1F, $221F, $0000
    ; Palette 7: value 128+ — gold (palette shared for big numbers; the win
    ; cell uses a separate animated palette set in win.asm).
    dw $7FFF, $7BE0, $4B40, $1080
```

> **Plan note.** `DrawBoardFull` is sketched. The executor expands the per-cell tilemap-write inner loop using `QueueCopy` calls to write 4 tile ids and 4 attribute bytes per cell, totaling 16 cells × 8 bytes = 128 bytes of queue traffic. This fits comfortably in one VBlank (drain budget is the whole VBlank ~1.1 ms ≈ 4400 cycles double-speed, way more than enough).

- [ ] **Step 2: INCLUDE**

```
INCLUDE "game/render.asm"
```

- [ ] **Step 3: Call RenderInit in Boot**

Add to Boot, just before the LCDC-on line we added in Task 11. Replace that whole sequence with:
```
    farcall 1, RenderInit
```
(RenderInit sets LCDC itself.)

- [ ] **Step 4: Build**

Expected: clean.

- [ ] **Step 5: Verify in BGB**

Open ROM. Expected: LCD on; BG shows the empty board grid (16 rounded-corner cells); HUD area at top is blank for now (window tilemap not yet populated).

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/game/render.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): render init — palettes, VRAM, initial board layout"
```

### Task 21: HUD — score on window layer

**Files:**
- Modify: `samples/gb-2048/src/game/render.asm` — add `DrawHud`.

- [ ] **Step 1: Append `DrawHud` and helpers**

```
DrawHud::
    ; Window tilemap at $9C00. Layout:
    ;   row 0: "BEST 0000000  SCORE 0000000"
    ;   row 1: blank line.
    ; 7 digits is enough for 9_999_999 — well above realistic scores.

    ; "BEST " then digits.
    ld hl, _SCRN1
    ld de, .label_best
    ld b, 5
.lb:
    ld a, [de]
    inc de
    ld [hl+], a
    dec b
    jr nz, .lb
    push hl
    ld bc, hl
    ld hl, wBestScore
    ; ScoreToDigits expects BC = output buffer; pop after.
    farcall 1, ScoreToDigits
    pop hl
    ; advance over the 7 digits.
    ld bc, 7
    add hl, bc
    ; spacer
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld [hl+], a
    ; "SCORE "
    ld de, .label_score
    ld b, 6
.ls:
    ld a, [de]
    inc de
    ld [hl+], a
    dec b
    jr nz, .ls
    push hl
    ld bc, hl
    ld hl, wScore
    farcall 1, ScoreToDigits
    pop hl
    ret

.label_best:
    db TILE_FONT_B, TILE_FONT_E, TILE_FONT_S, TILE_FONT_T, TILE_FONT_SPACE
.label_score:
    db TILE_FONT_S, TILE_FONT_C, TILE_FONT_O, TILE_FONT_R, TILE_FONT_E, TILE_FONT_SPACE
```

- [ ] **Step 2: Position the window**

Add to `RenderInit`, before the LCDC-on:
```
    ld a, 0
    ldh [rWY], a
    ld a, 7                    ; rWX = 7 means "x = 0"
    ldh [rWX], a
```

- [ ] **Step 3: Call DrawHud at end of RenderInit**

Append to RenderInit (after the existing `call DrawBoardFull`):
```
    call DrawHud
```

- [ ] **Step 4: Build**

Expected: clean.

- [ ] **Step 5: Verify**

Open ROM in BGB. Expected: top of screen shows "BEST 0000000  SCORE 0000000" (or whatever wBestScore loaded from SRAM); board below it.

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/game/render.asm
git commit -m "feat(samples): HUD score and best-score on window layer"
```

---

## Phase 5 — Animation

### Task 22: Slide animation (frames 0–7)

**Files:**
- Create: `samples/gb-2048/src/game/anim.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `game/anim.asm`**

```
SECTION "Animation", ROMX, BANK[1]

ANIM_TOTAL_FRAMES EQU 12
ANIM_SLIDE_FRAMES EQU 8
ANIM_POP_FRAMES   EQU 4

; AnimStart — called immediately after MoveLeft when wSlideValid is nonzero.
;   Hides moved BG cells (writes TILE_EMPTY into their positions),
;   sets wAnimFrame = 0, switches game state to ANIMATING.
AnimStart::
    ld a, GS_ANIMATING
    ld [wGameState], a
    xor a
    ld [wAnimFrame], a
    ; Hide moved BG cells. Walk wMoveIntents.
    call HideMovedCells
    ret

; AnimTick — called once per frame while wGameState == ANIMATING.
;   frame 0..7  → update OAM ghosts at lerp(prev, new).
;   frame 8     → snap; clear ghosts; commit BG (DrawBoardFull); pop merged.
;   frame 9..11 → animate merge pop (tile id swap per frame).
;   end of frame 11 → SpawnTile, clear move intents, return state to PLAYING,
;                     call CheckWin/CheckLose.
AnimTick::
    ld a, [wAnimFrame]
    cp ANIM_SLIDE_FRAMES
    jr c, .slide
    cp ANIM_TOTAL_FRAMES
    jr c, .pop
    ; frame == ANIM_TOTAL_FRAMES → finalize.
    call AnimFinalize
    ret
.slide:
    call AnimSlideFrame
    jr .advance
.pop:
    call AnimPopFrame
.advance:
    ld a, [wAnimFrame]
    inc a
    ld [wAnimFrame], a
    ret

; HideMovedCells — for each move intent with old != new, write TILE_EMPTY
; into the BG tilemap at that cell's position.
HideMovedCells::
    ; … expand: walk wMoveIntents (16 entries × 4 bytes), for each entry where
    ; old != new, queue a 2×2 tile-write of TILE_EMPTY at old's tilemap address.
    ret

; AnimSlideFrame — for each move intent, write 2 OAM entries (8x16 each) at
; lerp(old_pixel, new_pixel, wAnimFrame / 8).
AnimSlideFrame::
    ; … expand: pull wAnimFrame, compute t = frame << 5 (×32 of 256 = ×0.125
    ; per frame), use shift-and-subtract to interpolate.
    ret

AnimPopFrame::
    ; Frames 9..11: cycle the merged-cell tile id between three variants.
    ret

AnimFinalize::
    farcall 1, DrawBoardFull   ; commit
    ; Spawn new tile.
    farcall 1, SpawnTile
    ; Reset OAM ghosts.
    call ClearGhostOam
    ; Win check.
    farcall 1, CheckWin
    jr nz, .no_win
    ld a, [wWonOnce]
    or a
    jr nz, .no_win
    ld a, 1
    ld [wWonOnce], a
    ld a, GS_WIN
    ld [wGameState], a
    ret
.no_win:
    farcall 1, CheckLose
    jr nz, .no_lose
    ld a, GS_GAMEOVER
    ld [wGameState], a
    ret
.no_lose:
    ld a, GS_PLAYING
    ld [wGameState], a
    ret

ClearGhostOam::
    ld hl, wOAMBuffer
    ld b, 160
.loop:
    xor a
    ld [hl+], a
    dec b
    jr nz, .loop
    ret

; Game state constants (also referenced by main loop).
SECTION "GameStates", ROM0
GS_TITLE     EQU 0
GS_PLAYING   EQU 1
GS_ANIMATING EQU 2
GS_WIN       EQU 3
GS_GAMEOVER  EQU 4
```

> **Plan note.** The animation routines have `; … expand:` placeholders for the inner loops. The executor expands them along these lines:
> - `HideMovedCells`: for each `wMoveIntents[i]` where `old_idx != new_idx`, compute the BG tilemap address of the 2×2 cell at `old_idx`, queue 4 bytes of `TILE_EMPTY` via `QueueCopy`. 16 cells × 4 bytes = 64 bytes worst case → split across two VBlanks if budget exceeded.
> - `AnimSlideFrame`: for each active intent, lerp px = old_px + (new_px - old_px) * frame / 8. Frame is 0..7, so the divide is just `>> 3`. The lerp can be done in fixed-point: `delta = new_px - old_px`; `step = delta >> 3`; per-frame px = old_px + step × frame. Write 2 OAM entries per intent (top half y, bottom half y+8) at the same x.
> - `AnimPopFrame`: swap the merged cell's tile id between TILE_VALUE_BIG[v], TILE_VALUE_NORMAL[v], TILE_VALUE_NORMAL[v]. (All three variants live in tiles.asm.)

- [ ] **Step 2: INCLUDE**

```
INCLUDE "game/anim.asm"
```

- [ ] **Step 3: Build**

Expected: clean.

- [ ] **Step 4: Commit**

```sh
git add samples/gb-2048/src/game/anim.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): slide + pop animation state machine"
```

---

## Phase 6 — Sound

### Task 23: Sound engine + sfx

**Files:**
- Create: `samples/gb-2048/src/engine/sound.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `engine/sound.asm`**

```
SECTION "Sound", ROM0

; SoundInit — power on the APU, set master volume, configure channel mixer.
SoundInit::
    ld a, $80                  ; NR52 power on
    ldh [rNR52], a
    ld a, $77                  ; NR50 max volume both terminals
    ldh [rNR50], a
    ld a, $FF                  ; NR51 all channels both terminals
    ldh [rNR51], a
    ret

; PlaySfx — A = sfx id (0=move, 1=merge, 2=gameover).
PlaySfx::
    cp 0
    jr z, .move
    cp 1
    jr z, .merge
    cp 2
    jr z, .gameover
    ret
.move:
    ld a, $80                  ; NR21 duty 50% length 0
    ldh [rNR21], a
    ld a, $A2                  ; NR22 envelope: vol=A, decay
    ldh [rNR22], a
    ld a, $80                  ; NR23 freq lo
    ldh [rNR23], a
    ld a, $87                  ; NR24 freq hi + trigger
    ldh [rNR24], a
    ret
.merge:
    ld a, $80
    ldh [rNR21], a
    ld a, $E4                  ; brighter envelope
    ldh [rNR22], a
    ld a, $40
    ldh [rNR23], a
    ld a, $87
    ldh [rNR24], a
    ret
.gameover:
    ld a, $00                  ; NR41 length
    ldh [rNR41], a
    ld a, $F1                  ; NR42 volume
    ldh [rNR42], a
    ld a, $63                  ; NR43 polynomial
    ldh [rNR43], a
    ld a, $80                  ; NR44 trigger
    ldh [rNR44], a
    ret

; PlayWinJingle — 4-note arpeggio on the wave channel.
PlayWinJingle::
    ; Power up wave channel and load wave RAM with a triangle.
    xor a
    ldh [rNR30], a             ; disable to write wave RAM
    ld hl, .wave
    ld c, $30
    ld b, 16
.wv:
    ld a, [hl+]
    ldh [c], a
    inc c
    dec b
    jr nz, .wv
    ld a, $80
    ldh [rNR30], a
    ld a, $FF
    ldh [rNR31], a             ; length 256
    ld a, $20                  ; vol full
    ldh [rNR32], a
    ld a, $30                  ; freq
    ldh [rNR33], a
    ld a, $87                  ; trigger
    ldh [rNR34], a
    ret

.wave:
    db $01, $23, $45, $67, $89, $AB, $CD, $EF
    db $FE, $DC, $BA, $98, $76, $54, $32, $10
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "engine/sound.asm"
```

- [ ] **Step 3: Init in Boot**

Add after `farcall 1, SaveLoad`:
```
    call SoundInit
```

- [ ] **Step 4: Build**

Expected: clean.

- [ ] **Step 5: Verify**

Open ROM in BGB. Set a breakpoint after SoundInit; manually trigger `PlaySfx` with A=0; you should hear a click.

- [ ] **Step 6: Commit**

```sh
git add samples/gb-2048/src/engine/sound.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): sound engine — sfx and win jingle"
```

---

## Phase 7 — Screens

### Task 24: Title screen

**Files:**
- Create: `samples/gb-2048/src/screens/title.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `screens/title.asm`**

```
SECTION "Title Screen", ROMX, BANK[3]

TitleEnter::
    ; Disable LCD so we can rebuild the BG tilemap quickly.
    ld a, GS_TITLE
    ld [wGameState], a

    ; Use existing tile data; just rewrite tilemap with title art.
    farcall 3, BuildTitleTilemap
    ; Configure LYC for STAT split. LYC = 64 (mid-screen).
    ld a, 64
    ldh [rLYC], a
    ld a, STATF_LYC
    ldh [rSTAT], a
    ld a, IEF_VBLANK | IEF_STAT
    ldh [rIE], a
    ret

BuildTitleTilemap::
    ; Top half: gold gradient. Bottom half: 2×2 board preview.
    ; (Executor expands the tilemap layout per art direction.)
    ret

TitleTick::
    ; Advance carousel: every 30 frames, cycle the BG palette ramp by one step.
    ldh a, [hFrameCounter]
    and $1F                    ; every 32 frames
    jr nz, .skip
    ; … rotate palette colors in BgPaletteData and re-upload.
.skip:
    ; Read input — START → enter PLAYING.
    ld a, [wInput+2]           ; edge
    bit 3, a                   ; START
    jr z, .no_start
    ; Disable STAT, enter playing state, init game.
    xor a
    ldh [rSTAT], a
    ld a, IEF_VBLANK
    ldh [rIE], a
    farcall 1, ScoreReset
    farcall 1, BoardInit
    farcall 1, RenderInit
    ld a, GS_PLAYING
    ld [wGameState], a
.no_start:
    ret
```

- [ ] **Step 2: STAT IRQ palette swap**

Replace the stub `StatIRQ` in `engine/irq.asm` with:
```
StatIRQ::
    push af
    ; Title-screen split: top band uses palette set A, bottom band uses set B.
    ; Identify which band by comparing rLY to rLYC. After the swap, rewrite
    ; rLYC to fire again at the OTHER band's edge.
    ldh a, [rLY]
    cp 64
    jr c, .top
    ; We're at line ≥ 64 — swap to bottom palette set.
    ld a, BCPSF_AUTOINC | 0
    ldh [rBCPS], a
    ld hl, BgPaletteDataBottom
    ld b, 16                   ; first 2 palettes only — quick swap
.b:
    ld a, [hl+]
    ldh [rBCPD], a
    dec b
    jr nz, .b
    ld a, 0
    ldh [rLYC], a              ; fire again at top of next frame
    jr .done
.top:
    ld a, BCPSF_AUTOINC | 0
    ldh [rBCPS], a
    ld hl, BgPaletteDataTop
    ld b, 16
.t:
    ld a, [hl+]
    ldh [rBCPD], a
    dec b
    jr nz, .t
    ld a, 64
    ldh [rLYC], a
.done:
    pop af
    reti
```

(Add `BgPaletteDataTop` and `BgPaletteDataBottom` as 16-byte palettes in `screens/title.asm` — two different gradients.)

- [ ] **Step 3: INCLUDE**

```
INCLUDE "screens/title.asm"
```

- [ ] **Step 4: Wire into Boot**

Replace the body of Boot's main loop with:
```
.main_loop:
    call WaitForVBlankFlag
    call ReadInput
    ld a, [wGameState]
    cp GS_TITLE
    jr z, .title
    cp GS_PLAYING
    jr z, .playing
    cp GS_ANIMATING
    jr z, .animating
    cp GS_WIN
    jr z, .win
    cp GS_GAMEOVER
    jr z, .gameover
    jr .main_loop
.title:
    farcall 3, TitleTick
    jr .main_loop
.playing:
    call PlayingTick
    jr .main_loop
.animating:
    farcall 1, AnimTick
    jr .main_loop
.win:
    farcall 3, WinTick
    jr .main_loop
.gameover:
    farcall 3, GameOverTick
    jr .main_loop
```

Also add to Boot init (after `RenderInit`):
```
    farcall 3, TitleEnter
```
And remove the direct `RenderInit` call from Boot (it's now invoked from `TitleTick` on START).

- [ ] **Step 5: Define `PlayingTick` in main.asm**

```
SECTION "Playing", ROM0
PlayingTick:
    ld a, [wInput+2]           ; edge
    ; Right
    bit 4, a
    jp nz, .move_right
    ; Left
    bit 5, a
    jp nz, .move_left
    ; Up
    bit 6, a
    jp nz, .move_up
    ; Down
    bit 7, a
    jp nz, .move_down
    ret
.move_left:
    ld hl, DirLeft
    jr .do_move
.move_right:
    ld hl, DirRight
    jr .do_move
.move_up:
    ld hl, DirUp
    jr .do_move
.move_down:
    ld hl, DirDown
.do_move:
    farcall 1, MoveLeft
    ld a, [wSlideValid]
    or a
    ret z
    ; Best score?
    farcall 1, MaybeUpdateBestScore
    ; Move sfx.
    ld a, 0
    call PlaySfx
    farcall 1, AnimStart
    ret
```

Add the helper `MaybeUpdateBestScore` to `game/score.asm`:
```
MaybeUpdateBestScore::
    ; Compare wScore vs wBestScore (32-bit). If greater, copy and SaveStore.
    ld a, [wScore+3]
    ld b, a
    ld a, [wBestScore+3]
    cp b
    jr c, .update
    jr nz, .keep
    ld a, [wScore+2]
    ld b, a
    ld a, [wBestScore+2]
    cp b
    jr c, .update
    jr nz, .keep
    ld a, [wScore+1]
    ld b, a
    ld a, [wBestScore+1]
    cp b
    jr c, .update
    jr nz, .keep
    ld a, [wScore+0]
    ld b, a
    ld a, [wBestScore+0]
    cp b
    jr c, .update
.keep:
    ret
.update:
    ld hl, wScore
    ld de, wBestScore
    ld b, 4
.cp:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .cp
    farcall 1, SaveStore
    ret
```

- [ ] **Step 6: Build**

Expected: clean. Many cross-bank calls — verify symbol map shows expected bank assignments.

- [ ] **Step 7: Verify in BGB**

Boot ROM. Expected: title screen with gradient/preview, "PRESS START" text. START → board renders. D-pad input triggers a (currently silent or rough) animation, then PLAYING resumes. Confirm:
- HUD updates score after a merge.
- Best score increments when score exceeds previous best.
- Restart by triggering GAMEOVER / TITLE returns.

- [ ] **Step 8: Commit**

```sh
git add samples/gb-2048/src/screens/title.asm samples/gb-2048/src/game/score.asm samples/gb-2048/src/engine/irq.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): title screen with STAT palette split + state-machine main loop"
```

### Task 25: Game-over screen

**Files:**
- Create: `samples/gb-2048/src/screens/gameover.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `screens/gameover.asm`**

```
SECTION "GameOver", ROMX, BANK[3]

GameOverEnter::
    ld a, 2                    ; gameover sfx
    call PlaySfx
    ; Dim board: rotate all BG palettes one shade darker.
    ; (Executor: rebuild palettes from a darkened copy of BgPaletteData.)
    ; Draw "GAME OVER" banner via QueueCopy at center of board.
    ret

GameOverTick::
    ld a, [wInput+2]
    bit 3, a                   ; START
    ret z
    ; Reset to TITLE.
    farcall 3, TitleEnter
    ret
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "screens/gameover.asm"
```

- [ ] **Step 3: Wire AnimFinalize → GameOverEnter**

In `game/anim.asm`'s `AnimFinalize`, replace the GS_GAMEOVER set with:
```
    farcall 3, GameOverEnter
    ret
```

- [ ] **Step 4: Build, verify**

Expected: clean. In BGB, fill the board manually until GAMEOVER triggers; see banner; press START → TITLE.

- [ ] **Step 5: Commit**

```sh
git add samples/gb-2048/src/screens/gameover.asm samples/gb-2048/src/game/anim.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): game-over screen with restart"
```

### Task 26: Win screen

**Files:**
- Create: `samples/gb-2048/src/screens/win.asm`
- Modify: `samples/gb-2048/src/main.asm` — INCLUDE.

- [ ] **Step 1: Write `screens/win.asm`**

```
SECTION "Win", ROMX, BANK[3]

WinEnter::
    call PlayWinJingle
    ; Draw "YOU WIN!" banner overlay.
    ; (Executor: queue tilemap writes for a multi-line banner.)
    ret

WinTick::
    ld a, [wInput+2]
    bit 0, a                   ; A button
    ret z
    ; Continue play — set state to PLAYING.
    ld a, GS_PLAYING
    ld [wGameState], a
    ; Restore board palettes.
    farcall 1, RenderInit      ; full reinit is overkill but safe
    ret
```

- [ ] **Step 2: INCLUDE**

```
INCLUDE "screens/win.asm"
```

- [ ] **Step 3: Wire AnimFinalize → WinEnter**

In `game/anim.asm`, replace the GS_WIN block:
```
    ld a, 1
    ld [wWonOnce], a
    farcall 3, WinEnter
    ld a, GS_WIN
    ld [wGameState], a
    ret
```

- [ ] **Step 4: Build, verify**

Force a 2048 cell: poke `wBoard[0] = 10` then merge another 1024 next to it. Verify win screen + jingle. Press A → continue play.

- [ ] **Step 5: Commit**

```sh
git add samples/gb-2048/src/screens/win.asm samples/gb-2048/src/game/anim.asm samples/gb-2048/src/main.asm
git commit -m "feat(samples): win screen with jingle and continue"
```

---

## Phase 8 — Build integration & polish

### Task 27: Wire BuildSample2048 into build.proj

**Files:**
- Modify: `build.proj`

- [ ] **Step 1: Add target**

Append before `</Project>`:
```xml
  <Target Name="BuildSample2048">
    <Message Importance="high" Text="Building samples/gb-2048..." />
    <Exec Command="dotnet run --project src/Koh.Asm -- samples/gb-2048/src/main.asm -o samples/gb-2048/build/2048.kobj" />
    <Exec Command="dotnet run --project src/Koh.Link -- samples/gb-2048/build/2048.kobj -o samples/gb-2048/build/2048.gbc -n samples/gb-2048/build/2048.sym" />
    <ReadLinesFromFile File="samples/gb-2048/build/2048.gbc" />
    <Error Condition="!Exists('samples/gb-2048/build/2048.gbc')" Text="2048.gbc was not produced" />
    <Message Importance="high" Text="Built samples/gb-2048/build/2048.gbc" />
  </Target>
```

> **Note.** `ReadLinesFromFile` on a binary will fail or produce noise; replace with a more appropriate existence check. Cleaner: drop `ReadLinesFromFile` and rely on the `Error` condition + `Exec` exit codes. Final form:
> ```xml
>   <Target Name="BuildSample2048">
>     <MakeDir Directories="samples/gb-2048/build" />
>     <Exec Command="dotnet run --project src/Koh.Asm -- samples/gb-2048/src/main.asm -o samples/gb-2048/build/2048.kobj" />
>     <Exec Command="dotnet run --project src/Koh.Link -- samples/gb-2048/build/2048.kobj -o samples/gb-2048/build/2048.gbc -n samples/gb-2048/build/2048.sym" />
>     <Error Condition="!Exists('samples/gb-2048/build/2048.gbc')" Text="2048.gbc was not produced" />
>     <Message Importance="high" Text="Built samples/gb-2048/build/2048.gbc" />
>   </Target>
> ```

- [ ] **Step 2: Test the target**

Run: `dotnet msbuild build.proj -t:BuildSample2048`
Expected: succeeds, prints "Built samples/gb-2048/build/2048.gbc".

- [ ] **Step 3: Commit**

```sh
git add build.proj
git commit -m "feat(build): add BuildSample2048 target for the GB 2048 sample"
```

### Task 28: CI integration

**Files:**
- Modify: `.github/workflows/ci.yml` (or whichever existing workflow file). Confirm filename first via `ls .github/workflows/`.

- [ ] **Step 1: Inspect existing workflows**

Run: `ls .github/workflows/`
Identify the main CI file.

- [ ] **Step 2: Add a job**

Append a job:
```yaml
  build-sample-2048:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Build sample
        run: dotnet msbuild build.proj -t:BuildSample2048
      - name: Upload ROM
        uses: actions/upload-artifact@v4
        with:
          name: 2048.gbc
          path: samples/gb-2048/build/2048.gbc
```

- [ ] **Step 3: Commit**

```sh
git add .github/workflows/
git commit -m "ci: build the GB 2048 sample on every push"
```

### Task 29: README and top-level samples section

**Files:**
- Modify: `README.md` (top-level)
- Modify: `samples/gb-2048/README.md`

- [ ] **Step 1: Add Samples section to top-level README**

Insert after the "Repository Layout" section:
```markdown
## Samples

| Sample | Description |
|--------|-------------|
| [`samples/KohUI.Demo`](samples/KohUI.Demo/) | Counter app showing off KohUI (the .NET UI framework strand of Koh) |
| [`samples/gb-2048`](samples/gb-2048/) | Polished 2048 game ROM for Game Boy Color, demonstrating multi-bank assembly, GBC hardware features, OAM-sprite slide animation, and SRAM saves |

Build the GB sample with `dotnet msbuild build.proj -t:BuildSample2048`.
```

- [ ] **Step 2: Flesh out `samples/gb-2048/README.md`**

```markdown
# Game Boy 2048 Sample

A complete, polished 2048 game for Game Boy Color, built with the Koh toolchain.

![screenshot](docs/screenshot.png)

## Build

```sh
./scripts/build.sh        # bash / git-bash
./scripts/build.ps1       # Windows PowerShell

# or, from the repo root:
dotnet msbuild build.proj -t:BuildSample2048
```

Output: `build/2048.gbc`. Open in BGB, SameBoy, or mGBA.

## Controls

- **D-pad** — slide tiles
- **START** — restart from title (also: at the title, START begins a new game)
- **A** — at the win screen, continue play toward 4096

## Features

- MBC5, 4 banks, GBC-only.
- OAM-sprite slide animation (8 frames, 2 px/frame), merge pop, spawn fade-in.
- HDMA general-purpose tile uploads, double-speed CPU mode.
- STAT-interrupt palette split on title screen.
- Window-layer HUD (best + score), 8 BG palettes, 1 per value family.
- Battery-backed SRAM save (best score persists across power-off) with Fletcher-16 checksum.
- Sound: move, merge, win-jingle (wave channel), game-over noise burst.

## Architecture

See `docs/superpowers/specs/2026-04-30-gb-2048-design.md` for the full spec.

## Notes

(Populated from Task 2's probe — record `::` syntax availability and any other quirks discovered during build.)
```

(Optionally add a `docs/screenshot.png` capture; if BGB's screenshot tool produces a 160×144 PNG, drop it in.)

- [ ] **Step 3: Commit**

```sh
git add README.md samples/gb-2048/README.md
git commit -m "docs: add Samples section and full README for gb-2048"
```

### Task 30: Final integration test

- [ ] **Step 1: Clean build**

```sh
rm -rf samples/gb-2048/build
dotnet msbuild build.proj -t:BuildSample2048
```
Expected: succeeds.

- [ ] **Step 2: Open in BGB**

Run through the full play loop:
1. Title screen renders with palette split.
2. START → board appears, two starting tiles.
3. D-pad in each direction: tiles slide, merges pop, score increments.
4. Continue until 2048: win screen with jingle, A continues.
5. Continue until board full + no merges: gameover screen.
6. START → title.
7. Reset emulator (preserving SRAM): best score persists.

Note any visual glitches and file fixes as separate tasks (don't bundle into this final task).

- [ ] **Step 3: Capture screenshot**

Use BGB's "Save screenshot" → save to `samples/gb-2048/docs/screenshot.png`. Update `samples/gb-2048/README.md` to reference it.

- [ ] **Step 4: Final commit**

```sh
git add samples/gb-2048/docs/
git commit -m "docs(samples): add screenshot for GB 2048"
```

---

## Out of plan / future work

- Headless emulator regression test under `tests/Koh.Samples.Tests/` once `Koh.Emulator.Core` is stable enough to script. The current plan relies on manual emulator verification; automating this is genuinely valuable but is a separate spec.
- Idle-tile color cycling on the title screen (mentioned in brainstorming as a possible add).
- DMG fallback path (CGB-only is the explicit spec).

---

## Self-review notes

- **Spec coverage:** Every section of the spec maps to tasks 1–30. Banking → tasks 14, 15, 17, 20, 24–26 each declare ROMX,BANK[N]. SRAM → 17. HDMA → 13, 20. Double-speed → 8. STAT split → 24. Sound → 23. OAM DMA → 9, 11. VBlank queue → 10. Animation → 22. Score/BCD → 15. Move logic → 16. Header → 6 + 7. Save with checksum → 17. Build integration → 27, 28.
- **Placeholders:** Tasks 16, 20, 22, 24–26 contain "expand" notes pointing at well-defined inner-loop expansions. These are explicit handoffs to the executor where the *shape* of the code is settled but the byte-by-byte register juggling is left to be written cleanly. They are NOT vague TODOs — each note is a one-paragraph derivation of the loop.
- **Type/symbol consistency:** All cross-task symbols (`wBoard`, `wOAMBuffer`, `hOAMDMA`, `DirLeft`, `MoveLeft`, etc.) are introduced once and referenced consistently. `farcall <bank>, <symbol>` macro syntax used everywhere for cross-bank calls. Game-state constants (`GS_TITLE`, `GS_PLAYING`, ...) live in `engine/anim.asm`'s game-states subsection — a future task could move them to `memory.inc` for tighter scoping.
- **Known soft spot:** The 32-bit subtract-with-borrow inside `ScoreToDigits` (Task 15) and the column-walk in `CheckLose` (Task 16) are not fully spelled out byte-by-byte. Both are standard idioms but can eat hours if the executor isn't comfortable with SM83 carry chains. If progress stalls on either, factor them into smaller helper subroutines and unit-test by hand-poking values in BGB before integrating.
