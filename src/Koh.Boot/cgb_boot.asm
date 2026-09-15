; Koh's CGB boot ROM — 2304 bytes, mapped over $0000-$00FF and $0200-$08FF until it
; unmaps itself. $0100-$01FF is the cartridge header, a hole in the overlay.
;
; An INDEPENDENT implementation: it owes the hand-off state, not Nintendo's instruction
; sequence. Same minimal scope as dmg_boot.asm: no logo, chime, or header validation yet.
;
; Hand-off: like DMG, the unmap write sits at $00FC-$00FF so PC falls into $0100. A
; `jp $0100` after the write would be fetched from the cartridge, not from this image.

INCLUDE "hardware.inc"

SECTION "CgbBootLow", ROM0[$0000]
    jp CgbStart

SECTION "CgbHandoff", ROM0[$00FC]
Handoff:
    ld a, $11                 ; A=$11 identifies CGB hardware; also the unmap value
    ldh [rBANK], a

SECTION "CgbBootHigh", ROM0[$0200]
CgbStart:
    ld sp, $FFFE

    xor a
    ldh [rLCDC], a            ; LCD off: VRAM writes are dropped during mode 3

    ; Clear both VRAM banks.
    ld a, $01
    ldh [rVBK], a
    call ClearVram
    xor a
    ldh [rVBK], a
    call ClearVram

    ; Audio on, both channels to both outputs, full volume.
    ld a, $80
    ldh [rNR52], a
    ld a, $F3
    ldh [rNR51], a
    ld a, $77
    ldh [rNR50], a

    ld a, $FC
    ldh [rBGP], a
    ld a, $FF
    ldh [rOBP0], a
    ldh [rOBP1], a

    ; All 8 background and 8 object palettes to a grey ramp, auto-incrementing.
    ld a, $80
    ldh [rBCPS], a
    ld c, 8
.bgPalette:
    ld hl, GreyRamp
    ld b, 8
.bgByte:
    ld a, [hl+]
    ldh [rBCPD], a
    dec b
    jr nz, .bgByte
    dec c
    jr nz, .bgPalette

    ld a, $80
    ldh [rOCPS], a
    ld c, 8
.objPalette:
    ld hl, GreyRamp
    ld b, 8
.objByte:
    ld a, [hl+]
    ldh [rOCPD], a
    dec b
    jr nz, .objByte
    dec c
    jr nz, .objPalette

    ld a, $91
    ldh [rLCDC], a

    ; B=$00 for a CGB cartridge ($0143 bit 7), B=$01 for a DMG one.
    ld b, $00
    ld a, [$0143]
    bit 7, a
    jr nz, .cgbCartridge
    inc b
.cgbCartridge:

    ; F cannot be loaded directly: AF=$1180 through the stack.
    ld de, $1180
    push de
    pop af
    ld c, $00
    ld de, $FF56
    ld hl, $000D

    jp Handoff

ClearVram:
    xor a
    ld hl, $9FFF
.loop:
    ld [hl-], a
    bit 7, h                  ; still at or above $8000?
    jr nz, .loop
    ret

; White, light grey, dark grey, black as little-endian BGR555.
GreyRamp:
    db $FF, $7F, $B5, $56, $4A, $29, $00, $00
