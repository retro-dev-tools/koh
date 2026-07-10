SECTION "Score", ROMX, BANK[1]

; ScoreReset -- zero wScore (4 bytes, little-endian).
ScoreReset::
    xor a
    ld [wScore+0], a
    ld [wScore+1], a
    ld [wScore+2], a
    ld [wScore+3], a
    ret

; ScoreAdd -- wScore += 2^A.  A = exponent (1..12).
;   Builds 1<<A in registers DE:HL (little-endian: E=byte0, D=byte1, L=byte2,
;   H=byte3), then does a 32-bit add into wScore.
;   Clobbers AF, BC, DE, HL.
ScoreAdd::
    ld b, a                     ; save exponent in B
    xor a
    ld d, a                     ; D = 0
    ld l, a                     ; L = 0
    ld h, a                     ; H = 0
    ld e, 1                     ; E = 1  => initial value = 1
    ; Shift left B times to build 2^B.
.shift:
    sla e
    rl d
    rl l
    rl h
    dec b
    jr nz, .shift
    ; DE:HL holds 2^exponent (little-endian: E=byte0, D=byte1, L=byte2, H=byte3).
    ; 32-bit add: wScore += value.
    ld a, [wScore+0]
    add a, e
    ld [wScore+0], a
    ld a, [wScore+1]
    adc a, d
    ld [wScore+1], a
    ld a, [wScore+2]
    adc a, l
    ld [wScore+2], a
    ld a, [wScore+3]
    adc a, h
    ld [wScore+3], a
    ret

; ScoreToDigits -- convert 32-bit value to 7 tile-ID digits at buffer BC.
;   HL = pointer to 32-bit source value (little-endian, 4 bytes).
;   BC = pointer to 7-byte output buffer; tile IDs written most-significant first.
;   Each output byte = TILE_DIGIT_0 + digit.
;   Clobbers AF, BC, DE, HL.
;   HRAM usage:
;     hScratch+0..3  running 32-bit value (copy of source, decremented)
;     hScratch+4     output ptr low byte
;     hScratch+5     output ptr high byte
;     hScratch+6     current digit count
;
;   For each of the 7 pow10 entries (1000000 down to 1):
;     1. Load pow10 (4 bytes LE) from table into BCDE  (B=high, C, D, E=low).
;        HL advances past the entry; push HL to free it.
;     2. Set hScratch+6 = 0 (digit).
;     3. Compare hScratch+0..3 with BCDE; if hScratch < pow10, go to step 5.
;     4. Subtract BCDE from hScratch+0..3 (32-bit in-place); digit++; go to 3.
;     5. Pop HL (next-entry ptr).
;        Restore output ptr into HL from hScratch+4..5.
;        Write (digit+35) to [HL]; HL++.
;        Save updated output ptr back to hScratch+4..5.
;        Restore table ptr into HL (from stack... but already popped).
;
; Revised step 5 avoids double-use of HL by keeping table ptr on stack and
; output ptr in hScratch, swapping HL between the two roles only as needed.
ScoreToDigits::
    ; Copy source (HL) to hScratch+0..3.
    ld a, [hl+]
    ldh [hScratch+0], a
    ld a, [hl+]
    ldh [hScratch+1], a
    ld a, [hl+]
    ldh [hScratch+2], a
    ld a, [hl+]
    ldh [hScratch+3], a

    ; Save output ptr BC to hScratch+4..5.
    ld a, c
    ldh [hScratch+4], a
    ld a, b
    ldh [hScratch+5], a

    ; HL = start of pow10 table.
    ld hl, .pow10_table

.next_pow:
    ; End-of-table check.
    ld a, h
    cp HIGH(.pow10_end)
    jr nz, .load_pow
    ld a, l
    cp LOW(.pow10_end)
    ret z

.load_pow:
    ; Load 4 bytes little-endian from [HL..HL+3] into BCDE.
    ; E = byte0 (LSB), D = byte1, C = byte2, B = byte3 (MSB).
    ld a, [hl+]
    ld e, a
    ld a, [hl+]
    ld d, a
    ld a, [hl+]
    ld c, a
    ld a, [hl+]
    ld b, a
    ; HL now points to the next entry.  Push it so we can reuse HL.
    push hl

    ; Reset digit counter.
    xor a
    ldh [hScratch+6], a

.sub_loop:
    ; 32-bit compare: hScratch+0..3 vs BCDE.
    ; cp sets C if A < operand; sbc propagates the borrow.
    ; After all 4 bytes: C set => hScratch < pow10 => stop.
    ldh a, [hScratch+0]
    cp e
    ldh a, [hScratch+1]
    sbc a, d
    ldh a, [hScratch+2]
    sbc a, c
    ldh a, [hScratch+3]
    sbc a, b
    jr c, .digit_done

    ; Subtract BCDE from hScratch+0..3 (32-bit, little-endian, in-place).
    ldh a, [hScratch+0]
    sub e
    ldh [hScratch+0], a
    ldh a, [hScratch+1]
    sbc a, d
    ldh [hScratch+1], a
    ldh a, [hScratch+2]
    sbc a, c
    ldh [hScratch+2], a
    ldh a, [hScratch+3]
    sbc a, b
    ldh [hScratch+3], a

    ; Increment digit counter.
    ldh a, [hScratch+6]
    inc a
    ldh [hScratch+6], a
    jr .sub_loop

.digit_done:
    ; Restore table-next ptr from stack.
    pop hl

    ; Write ASCII digit to output buffer.
    ; Load output ptr into DE temporarily.
    ldh a, [hScratch+4]
    ld e, a
    ldh a, [hScratch+5]
    ld d, a
    ; A = digit + TILE_DIGIT_0.
    ldh a, [hScratch+6]
    add a, TILE_DIGIT_0
    ; Write to [DE] and advance DE.
    ld [de], a
    inc de
    ; Save updated output ptr.
    ld a, e
    ldh [hScratch+4], a
    ld a, d
    ldh [hScratch+5], a
    ; Loop for next pow10 (HL still holds next-entry ptr).
    jr .next_pow

.pow10_table:
    dl 1000000
    dl 100000
    dl 10000
    dl 1000
    dl 100
    dl 10
    dl 1
.pow10_end:
