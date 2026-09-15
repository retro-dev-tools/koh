SECTION "Sound", ROM0

; SoundInit — power on the APU, set master volume, configure channel mixer.
SoundInit::
    ld a, $80                  ; NR52 power on
    ldh [rNR52], a
    ld a, $77                  ; NR50 max volume both terminals
    ldh [rNR50], a
    ld a, $FF                  ; NR51 all channels both terminals
    ldh [rNR51], a
    ret

; PlaySfx — A = sfx id (0=move, 1=merge, 2=gameover).
; Clobbers AF.
PlaySfx::
    cp 0
    jr z, .move
    cp 1
    jr z, .merge
    cp 2
    jr z, .gameover
    ret
.move:
    ld a, $80                  ; NR21 duty 50% length 0
    ldh [rNR21], a
    ld a, $A2                  ; NR22 envelope
    ldh [rNR22], a
    ld a, $80                  ; NR23 freq lo
    ldh [rNR23], a
    ld a, $87                  ; NR24 freq hi + trigger
    ldh [rNR24], a
    ret
.merge:
    ld a, $80
    ldh [rNR21], a
    ld a, $E4                  ; brighter envelope
    ldh [rNR22], a
    ld a, $40
    ldh [rNR23], a
    ld a, $87
    ldh [rNR24], a
    ret
.gameover:
    ld a, $00                  ; NR41 length
    ldh [rNR41], a
    ld a, $F1                  ; NR42 volume
    ldh [rNR42], a
    ld a, $63                  ; NR43 polynomial
    ldh [rNR43], a
    ld a, $80                  ; NR44 trigger
    ldh [rNR44], a
    ret

; PlayWinJingle — load a triangle wave into wave RAM and trigger CH3.
PlayWinJingle::
    ; Power down wave channel before writing wave RAM.
    xor a
    ldh [rNR30], a
    ld hl, .wave
    ld c, LOW(rWAVE_0)         ; rWAVE_0 = $FF30
    ld b, 16
.wv:
    ld a, [hl+]
    ldh [c], a
    inc c
    dec b
    jr nz, .wv
    ld a, $80
    ldh [rNR30], a
    ld a, $FF
    ldh [rNR31], a             ; length 256
    ld a, $20                  ; volume full
    ldh [rNR32], a
    ld a, $30                  ; freq lo
    ldh [rNR33], a
    ld a, $87                  ; trigger + freq hi
    ldh [rNR34], a
    ret
.wave:
    db $01, $23, $45, $67, $89, $AB, $CD, $EF
    db $FE, $DC, $BA, $98, $76, $54, $32, $10
