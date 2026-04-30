SECTION "GameOver Screen", ROMX, BANK[3]

GameOverEnter::
    ld a, GS_GAMEOVER
    ld [wGameState], a

    ; Play game-over sfx (id 2). PlaySfx is in bank 0.
    ld a, 2
    call PlaySfx

    ; Wait for VBlank before drawing.
    call WaitForVBlankFlag

    ; Draw "GAME OVER" banner at row 8, col 6 (centered).
    ld hl, _SCRN0 + 8*32 + 6
    ld a, TILE_FONT_G
    ld [hl+], a
    ld a, TILE_FONT_A
    ld [hl+], a
    ld a, TILE_FONT_M
    ld [hl+], a
    ld a, TILE_FONT_E
    ld [hl+], a
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld a, TILE_FONT_O
    ld [hl+], a
    ld a, TILE_FONT_V
    ld [hl+], a
    ld a, TILE_FONT_E
    ld [hl+], a
    ld a, TILE_FONT_R
    ld [hl+], a
    ret

GameOverTick::
    ld a, [wInput+2]           ; edge bits
    bit 3, a                   ; START
    ret z
    ; START pressed -> return to title.
    farcall 3, TitleEnter
    ret
