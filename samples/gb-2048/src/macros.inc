; Common macros.
;
; Note: The repository uses WaitForVBlankFlag (a regular subroutine) for
; VBlank waits, not a macro. A WAIT_VBLANK macro using the \@ unique-suffix
; idiom triggers a Koh tokenizer bug that corrupts subsequent macro
; definitions, so we omit it here.

; farcall <bank>, <symbol>
;   Switch to bank \1, call \2 (a label in that bank), restore the previous
;   bank. The actual bank-switch + call is performed by FarcallTrampoline in
;   ROM0 (engine/farcall.asm) — doing it inline from a banked section would
;   re-map the $4000-$7FFF window underneath PC at the moment of the CALL,
;   leaving the CPU executing bytes from the wrong bank.
;
;   Caller stack: pushes 6 bytes (return addr + 2 saves inside the helper).
;   Clobbers A, BC.
farcall: MACRO
    ld a, \1
    ld bc, \2
    call FarcallTrampoline
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
