; Routines and data for boot_logo.inc. Placed where execution never falls into them.

; C's top nibble, each bit doubled, into A; written to two tile rows at HL. C advances.
LogoNibble:
    ld b, 4
.bit:
    push bc
    rl c
    rla
    pop bc
    rl c
    rla
    dec b
    jr nz, .bit
    ld [hl+], a
    inc hl
    ld [hl+], a
    inc hl
    ret

; Wait for B frame starts.
WaitFrames:
    ldh a, [rLY]
    cp 144
    jr nz, WaitFrames
.inVBlank:
    ldh a, [rLY]
    cp 144
    jr z, .inVBlank
    dec b
    jr nz, WaitFrames
    ret

; The logo every licensed cartridge carries at $0104-$0133.
LogoReference:
    db $CE, $ED, $66, $66, $CC, $0D, $00, $0B, $03, $73, $00, $83, $00, $0C, $00, $0D
    db $00, $08, $11, $1F, $88, $89, $00, $0E, $DC, $CC, $6E, $E6, $DD, $DD, $D9, $99
    db $BB, $BB, $67, $63, $6E, $0E, $EC, $CC, $DD, $DC, $99, $9F, $BB, $B9, $33, $3E
