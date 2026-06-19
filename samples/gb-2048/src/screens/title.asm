SECTION "Title Screen", ROMX, BANK[3]

; TitleEnter -- show a clean title screen.
;   The HUD (rows 0-2) stays visible from RenderInit; we wipe the board area
;   below so the title text reads cleanly on a white background.
TitleEnter::
    ld a, GS_TITLE
    ld [wGameState], a

    call WaitForVBlankFlag
    call LcdOff                 ; keep LCD off through ALL of our VRAM writes
    call ClearBoardArea

    ld hl, _SCRN0 + 8 * 32 + 6
    ld de, .str_title
    call DrawString
    ; Set palette 0 (white plate, black text) for the 8 title tiles so they
    ; contrast against the dark frame they sit on.
    ld hl, _SCRN0 + 8 * 32 + 6
    ld bc, 8
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

.str_title:  db "KOH 2048", STR_END
.str_prompt: db "PRESS START", STR_END

; TitleTick -- on START edge: reset board, init game, transition to PLAYING.
TitleTick::
    ld a, [wInput+2]
    bit 3, a
    ret z

    farcall 1, StartNewGame
    ld a, GS_PLAYING
    ld [wGameState], a
    ret
