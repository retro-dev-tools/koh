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

SECTION "Board Buffers", WRAM0[$C200]
; Shadow copies of the board's tilemap region (rows 3..17, 32 cols each).
; AnimTick fills these progressively during the animation, then a single
; HDMA copy lands the whole 480-byte block into VRAM in one shot — so the
; board doesn't wipe in row by row and the LCD never has to be turned off.
wBoardBgBuf:    ds 480
wBoardAttrBuf:  ds 480

; Static "empty grid" mirror of the board region: all 16 cells drawn empty
; (value 0). ClearBoardBg HDMAs these over the board during the slide so the
; grid stays put while the numbered tiles travel as OAM sprites, instead of
; the whole board blanking out. Built once at boot by BuildEmptyGridBuffers.
; Fixed at $C600 so both buffers are 16-byte aligned ($C600, $C7E0) — HDMA
; masks the low 4 bits of the source, so an unaligned source would corrupt.
SECTION "Empty Grid Buffers", WRAM0[$C600]
wEmptyBgBuf:    ds 480
wEmptyAttrBuf:  ds 480

; Cached HUD digit tiles. ScoreToDigits is too slow (~2-4 K T-cycles for
; values up to 2048) to run inside the commit VBlank without overflowing
; into PPU mode 3, so we precompute the 7 digit tile-ids once during the
; slide phase and just blit the cache in DrawHud_FromCache.
wScoreDigits:     ds 7
wBestScoreDigits: ds 7

SECTION "HRAM", HRAM
hOAMDMA::      ds 10        ; trampoline copied from ROM at boot
hFrameCounter: ds 1
hSysFlags:     ds 1
hScratch:      ds 8         ; +0..3 running value, +4..5 output ptr, +6..7 reserved

SECTION "SRAM", SRAM, BANK[0]
sMagic:        ds 4         ; "K248"
sBestScore:    ds 4
sLastBoard:    ds 16
sLastScore:    ds 4
sChecksum:     ds 2         ; Fletcher-16 over sMagic..sLastScore
