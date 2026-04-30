; game/anim.asm — Slide animation state machine (BANK 1).
;
; Game-state constants (referenced by main loop in main.asm and other modules).
; EQU constants need no SECTION.
GS_TITLE     EQU 0
GS_PLAYING   EQU 1
GS_ANIMATING EQU 2
GS_WIN       EQU 3
GS_GAMEOVER  EQU 4

; Animation lasts ANIM_TOTAL_FRAMES frames.
; Frame 8 commits the board (DrawBoardFull + SpawnTile).
; Frame ANIM_TOTAL_FRAMES triggers win/lose checks and state transition.
ANIM_TOTAL_FRAMES EQU 12

SECTION "Animation", ROMX, BANK[1]

; ----------------------------------------------------------------------------
; AnimStart:: — call after a valid move to begin the animation phase.
;   Sets wGameState = GS_ANIMATING, resets wAnimFrame to 0.
;   Clobbers AF, HL.
; ----------------------------------------------------------------------------
AnimStart::
    ld a, GS_ANIMATING
    ld [wGameState], a
    xor a
    ld [wAnimFrame], a
    ret

; ----------------------------------------------------------------------------
; AnimTick:: — call once per frame while wGameState == GS_ANIMATING.
;   Increments wAnimFrame.
;   Frame 8  : commits board (DrawBoardFull) and spawns a new tile.
;   Frame 12 : runs win/lose checks, transitions to GS_WIN / GS_GAMEOVER /
;              GS_PLAYING.
;   Clobbers AF, HL (+ whatever DrawBoardFull/SpawnTile/CheckWin/CheckLose use).
; ----------------------------------------------------------------------------
AnimTick::
    ld a, [wAnimFrame]
    inc a
    ld [wAnimFrame], a
    cp 8
    jr z, .commit
    cp ANIM_TOTAL_FRAMES
    jr z, .finalize
    ret

.commit:
    ; Commit the slid board to the tilemap and spawn the next tile.
    call DrawBoardFull
    call SpawnTile
    ret

.finalize:
    ; Check win: Z set if a cell value == 11 (represents 2048).
    call CheckWin
    jr nz, .no_win
    ; Only trigger GS_WIN once (wWonOnce guards repeated win screens).
    ld a, [wWonOnce]
    or a
    jr nz, .no_win
    ld a, 1
    ld [wWonOnce], a
    ld a, GS_WIN
    ld [wGameState], a
    ret

.no_win:
    call CheckLose
    jr nz, .no_lose
    farcall 3, GameOverEnter
    ret
.no_lose:
    ld a, GS_PLAYING
    ld [wGameState], a
    ret
