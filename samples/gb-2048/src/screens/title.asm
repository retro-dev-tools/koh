SECTION "Title Screen", ROMX, BANK[3]

; TitleEnter -- set up the title screen.
;   Writes a static "KOH 2048 / PRESS START" message to the BG tilemap.
;   Sets game state to TITLE.
TitleEnter::
    ld a, GS_TITLE
    ld [wGameState], a

    ; Wait for VBlank before writing to VRAM. Without this, BG writes happen
    ; mid-frame and some land during PPU mode 3 (silently dropped) — leaving
    ; the title text either partial or invisible.
    call WaitForVBlankFlag

    ; Write "KOH 2048" centered at row 7, col 6.
    ld hl, _SCRN0 + 7*32 + 6
    ld a, TILE_FONT_K
    ld [hl+], a
    ld a, TILE_FONT_O
    ld [hl+], a
    ld a, TILE_FONT_H
    ld [hl+], a
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld a, TILE_DIGIT_2
    ld [hl+], a
    ld a, TILE_DIGIT_0
    ld [hl+], a
    ld a, TILE_DIGIT_4
    ld [hl+], a
    ld a, TILE_DIGIT_8
    ld [hl+], a

    ; Write "PRESS START" at row 12, col 4.
    ld hl, _SCRN0 + 12*32 + 4
    ld a, TILE_FONT_P
    ld [hl+], a
    ld a, TILE_FONT_R
    ld [hl+], a
    ld a, TILE_FONT_E
    ld [hl+], a
    ld a, TILE_FONT_S
    ld [hl+], a
    ld a, TILE_FONT_S
    ld [hl+], a
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld a, TILE_FONT_S
    ld [hl+], a
    ld a, TILE_FONT_T
    ld [hl+], a
    ld a, TILE_FONT_A
    ld [hl+], a
    ld a, TILE_FONT_R
    ld [hl+], a
    ld a, TILE_FONT_T
    ld [hl+], a

    ret

; TitleTick -- called every frame while in TITLE state.
;   On START button edge -> reset board, init game, transition to PLAYING.
TitleTick::
    ld a, [wInput+2]           ; edge bits
    bit 3, a                   ; START button = bit 3
    ret z

    ; START pressed -- start a new game (reset, board init, and BG redraw).
    farcall 1, StartNewGame
    ld a, GS_PLAYING
    ld [wGameState], a
    ret
