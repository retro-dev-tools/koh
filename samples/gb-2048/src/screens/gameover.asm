SECTION "GameOver Screen", ROMX, BANK[3]

GameOverEnter::
    ld a, GS_GAMEOVER
    ld [wGameState], a

    ; Play game-over sfx (id 2) — PlaySfx lives in bank 0.
    ld a, 2
    call PlaySfx

    call WaitForVBlankFlag
    call LcdOff
    call ClearBoardArea

    ld hl, _SCRN0 + 8 * 32 + 5
    ld de, .str_banner
    call DrawString
    ld hl, _SCRN0 + 8 * 32 + 5
    ld bc, 9
    xor a
    call FillAttr

    ld hl, _SCRN0 + 13 * 32 + 4
    ld de, .str_prompt
    call DrawString
    ld hl, _SCRN0 + 13 * 32 + 4
    ld bc, 11
    xor a
    call FillAttr
    call LcdOn
    ret

.str_banner: db "GAME OVER", STR_END
.str_prompt: db "PRESS START", STR_END

GameOverTick::
    ld a, [wInput+2]
    bit 3, a
    ret z
    farcall 3, TitleEnter
    ret
