; Common macros.
;
; Note: The repository uses WaitForVBlankFlag (a regular subroutine) for
; VBlank waits, not a macro. A WAIT_VBLANK macro using the \@ unique-suffix
; idiom triggers a Koh tokenizer bug that corrupts subsequent macro
; definitions, so we omit it here.

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
