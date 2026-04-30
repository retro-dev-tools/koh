SECTION "Win Screen", ROMX, BANK[3]

WinEnter::
    ld a, GS_WIN
    ld [wGameState], a

    ; Play win jingle (CH3 wave channel). PlayWinJingle is in bank 0.
    call PlayWinJingle

    ; Wait for VBlank before drawing.
    call WaitForVBlankFlag

    ; Draw "YOU WIN!" banner at row 8, col 7 (centered).
    ld hl, _SCRN0 + 8*32 + 7
    ld a, TILE_FONT_Y
    ld [hl+], a
    ld a, TILE_FONT_O
    ld [hl+], a
    ld a, TILE_FONT_U
    ld [hl+], a
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld a, TILE_FONT_W
    ld [hl+], a
    ld a, TILE_FONT_I
    ld [hl+], a
    ld a, TILE_FONT_N
    ld [hl+], a
    ld a, TILE_FONT_EXCL
    ld [hl+], a
    ret

WinTick::
    ld a, [wInput+2]           ; edge bits
    bit 0, a                   ; A button = bit 0
    ret z
    ; A pressed → return to PLAYING (continue past 2048).
    ld a, GS_PLAYING
    ld [wGameState], a
    ret
