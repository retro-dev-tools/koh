SECTION "Win Screen", ROMX, BANK[3]

WinEnter::
    ld a, GS_WIN
    ld [wGameState], a

    ; Play win jingle (CH3 wave channel) — PlayWinJingle lives in bank 0.
    call PlayWinJingle

    call WaitForVBlankFlag
    call LcdOff
    call ClearBoardArea

    ld hl, _SCRN0 + 8 * 32 + 6
    ld de, .str_banner
    call DrawString
    ld hl, _SCRN0 + 8 * 32 + 6
    ld bc, 8
    xor a
    call FillAttr

    ld hl, _SCRN0 + 13 * 32 + 7
    ld de, .str_prompt
    call DrawString
    ld hl, _SCRN0 + 13 * 32 + 7
    ld bc, 7
    xor a
    call FillAttr
    call LcdOn
    ret

.str_banner: db "YOU WIN!", STR_END
.str_prompt: db "PRESS A", STR_END

WinTick::
    ld a, [wInput+2]
    bit 0, a                   ; A button
    ret z
    ; A pressed → return to PLAYING (continue past 2048).  Win cleared the
    ; board area, so we have to repaint it before play resumes.
    farcall 1, ResumeAfterWin
    ld a, GS_PLAYING
    ld [wGameState], a
    ret
