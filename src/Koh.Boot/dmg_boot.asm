; Koh's DMG boot ROM — 256 bytes, mapped over $0000-$00FF until it unmaps itself.
;
; This is an INDEPENDENT implementation, not a transcription of Nintendo's boot ROM.
; What it owes is the observable hand-off state (the register table below), not the same
; instruction sequence. One consequence: DIV at hand-off is a function of this code's own
; cycle count and will not match a stock dump's.
;
; The logo, checks, scroll and chime are shared with cgb_boot.asm (boot_logo.inc). The
; 256-byte limit is the binding constraint: the linker fails the build on overflow.
;
; The hand-off trick, which fixes the image size at exactly 256 bytes: the last two
; instructions sit at $00FC-$00FF. Writing rBANK drops the overlay, and PC then runs off
; the end of the boot ROM straight into the cartridge entry at $0100. There is no jump.

INCLUDE "hardware.inc"

SECTION "BootRom", ROM0[$0000]
Start:
    ld sp, $FFFE              ; stack at the top of HRAM, where hardware leaves it

    ; Clear VRAM $8000-$9FFF. The Mmu $FF-poisons RAM at power-on to catch
    ; read-before-write bugs; zeroing VRAM is the boot ROM's job, not the emulator's.
    xor a
    ldh [rLCDC], a            ; LCD off: VRAM writes are dropped during mode 3
    ld hl, $9FFF
.clearVram:
    ld [hl-], a
    bit 7, h                  ; still at or above $8000?
    jr nz, .clearVram

    ; Audio on, both channels to both outputs, full volume; chime voice on channel 1.
    ld a, $80
    ldh [rNR52], a
    ldh [rNR11], a
    ld a, $F3
    ldh [rNR51], a
    ldh [rNR12], a
    ld a, $77
    ldh [rNR50], a

    ld a, $FC
    ldh [rBGP], a
    ld a, $FF
    ldh [rOBP0], a
    ldh [rOBP1], a

INCLUDE "boot_logo.inc"

    ; Hand-off register state. F cannot be loaded directly, so build it through the
    ; stack: pushing BC=$00B0 and popping AF leaves F=$B0 (Z, H and C set).
    ; A itself is set to $01 at Handoff below, which does not disturb F.
    ld bc, $00B0
    push bc
    pop af
    ld c, $13
    ld de, $00D8
    ld hl, $014D
    jr Handoff

INCLUDE "boot_logo_routines.inc"

; The last four bytes of the image. Must be at $00FC so that PC, having unmapped the
; overlay, falls into $0100.
SECTION "Handoff", ROM0[$00FC]
Handoff:
    ld a, $01
    ldh [rBANK], a
