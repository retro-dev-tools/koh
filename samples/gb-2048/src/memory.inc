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
