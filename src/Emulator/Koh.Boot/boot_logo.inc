; Shared boot ROM body: draw the cartridge's header logo, check it and the header
; checksum, scroll the logo in, and chime. Inline code, not a routine.
;
; Needs: LCD off, VRAM cleared, APU on, palettes set, boot_logo_routines.inc linked.
; Leaves: LCD on, SCY=0. A bad logo or checksum locks up with the LCD on, as on hardware.

    ; Decompress $0104-$0133 into tiles 1-24. Each nibble is one 4px row, doubled both ways.
    ld de, $0104
    ld hl, $8010
.logoByte:
    ld a, [de]
    ld c, a
    call LogoNibble
    call LogoNibble
    inc de
    ld a, e
    cp $34
    jr nz, .logoByte

    ; Tiles 24-13 into row 9 ($9924-$992F), then 12-1 into row 8 ($9904-$990F).
    ld hl, $992F
    ld a, 24
.tilemap:
    ld [hl-], a
    dec a
    jr z, .tilemapDone
    cp 12
    jr nz, .tilemap
    ld l, $0F
    jr .tilemap
.tilemapDone:

    ld a, $91
    ldh [rLCDC], a

    ld de, LogoReference
    ld hl, $0104
.checkLogo:
    ld a, [de]
    inc de
    cp [hl]
    jr nz, @                  ; bad logo: lock up
    inc hl
    ld a, l
    cp $34
    jr nz, .checkLogo

    ; $19 plus $0134-$014D wraps to zero exactly when the header checksum is right.
    ld b, $19
    ld a, b
.checksum:
    add [hl]
    inc hl
    dec b
    jr nz, .checksum
    add [hl]
    jr nz, @                  ; bad header checksum: lock up

    ; SCY 100 -> 0, one step per frame: the logo descends from above the view. B is 0.
    ld c, 100
.scroll:
    ld a, c
    ldh [rSCY], a
    inc b
    call WaitFrames
    dec c
    bit 7, c
    jr z, .scroll

    ; Two-note chime as the logo lands, then hold.
    ld a, $83
    ldh [rNR13], a
    ld a, $87
    ldh [rNR14], a
    ld b, 6
    call WaitFrames
    ld a, $C1
    ldh [rNR13], a
    ld a, $87
    ldh [rNR14], a
    ld b, 40
    call WaitFrames
