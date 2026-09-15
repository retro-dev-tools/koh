SECTION "Save", ROMX, BANK[1]

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

; Fletcher-16 over a buffer.
;   HL = buffer, BC = length. Returns checksum in DE (D=high, E=low).
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

; SaveLoad — return A=0 on success, 1 on failure.
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
    ; OK — copy sBestScore -> wBestScore.
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

SaveStore::
    call SaveEnable
    ; Write magic.
    ld hl, .magic
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
    ; Last board: copy wBoard -> sLastBoard (16 bytes).
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
.magic:
    db "K248"
