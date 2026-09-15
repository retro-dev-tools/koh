SECTION "RNG", ROMX, BANK[1]

; RngSeed - initialize wRngState. Caller passes seed in HL; if HL is zero,
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

; RngNext - advance LFSR. Returns next byte in A; full state in wRngState.
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

; RngRange - return A = RngNext mod B. B in [1..255]. Clobbers AF, BC, HL.
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
