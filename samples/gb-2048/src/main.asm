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
    ; Header checksum — set to 0 here; T7 verifies whether the linker patches it.
    db $00
    ; Global checksum — set to 0; same T7 question.
    db $00,$00

; -----------------------------------------------------------------------------
SECTION "Boot", ROM0
Boot:
    di
    ld sp, $E000               ; top of WRAM
.halt:
    halt
    jr .halt

; -----------------------------------------------------------------------------
; Temporary interrupt handler stubs (will be replaced in T11).
; -----------------------------------------------------------------------------
SECTION "VBlank", ROM0
VBlankIRQ::
    reti

SECTION "Stat", ROM0
StatIRQ::
    reti
