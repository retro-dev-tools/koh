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
; AnimStart:: — call after a valid move (with wMoveIntents already filled).
;   - Sets wGameState = GS_ANIMATING, resets wAnimFrame to 0.
;   - Writes wOAMBuffer with sprites at pre-move (src) positions; the next
;     VBlank's OAM DMA will make them visible.
;   - Plays the move/merge SFX.
;
;   The BG board region is NOT cleared here. ClearBoardBg runs on the first
;   AnimTick instead, by which point the OAM DMA has already copied the src-
;   position sprites to OAM. Clearing earlier would leave one frame where the
;   board area is blank (BG cleared, sprites not yet DMA'd).
;
;   Clobbers AF, BC, DE, HL, hScratch+0..5.
; ----------------------------------------------------------------------------
AnimStart::
    ld a, GS_ANIMATING
    ld [wGameState], a
    xor a
    ld [wAnimFrame], a
    xor a                  ; frame = 0
    call SpriteRenderTick
    call PickAndPlayMoveSfx
    ret

; ----------------------------------------------------------------------------
; PickAndPlayMoveSfx -- play merge SFX if any wMoveIntents entry has its
;   merge flag set, otherwise play the plain move SFX. PlaySfx is in ROM0
;   and reachable via a plain call from BANK[1].
;   Clobbers AF, BC, HL.
; ----------------------------------------------------------------------------
PickAndPlayMoveSfx::
    ld hl, wMoveIntents + 2    ; merge-flag byte of intent 0
    ld bc, 4
    ld d, 16
.scan:
    ld a, [hl]
    or a
    jr nz, .merge
    add hl, bc
    dec d
    jr nz, .scan
    xor a                      ; sfx id 0 = move
    call PlaySfx
    ret
.merge:
    ld a, 1                    ; sfx id 1 = merge
    call PlaySfx
    ret

; ----------------------------------------------------------------------------
; AnimTick:: — call once per frame while wGameState == GS_ANIMATING.
;   Frames 1..7  : interpolate sprite positions (slide animation).
;   Frame  8     : hide sprites, commit board to BG, spawn a new tile.
;   Frames 9..11 : nothing — board is shown via BG.
;   Frame  12    : win/lose checks, transition to GS_WIN / GS_GAMEOVER /
;                  GS_PLAYING.
;   Clobbers AF, HL (+ whatever the called routines use).
; ----------------------------------------------------------------------------
AnimTick::
    ld a, [wAnimFrame]
    inc a
    ld [wAnimFrame], a

    cp ANIM_TOTAL_FRAMES
    jp z, .finalize
    cp 8
    jp z, .commit
    jr nc, .post_commit    ; frames 9..11: idle

    ; Frame 1: OAM DMA has now copied src-position sprites to OAM, so the
    ; sprites are visible. Safe to clear the BG cells they slide over.
    cp 1
    call z, ClearBoardBg

    ; Frames 1..7: advance the slide by one step.
    ld a, [wAnimFrame]
    call SpriteRenderTick
    ret

.post_commit:
    ret

.commit:
    ; Hide all sprites, commit the slid board, refresh palette attributes,
    ; spawn the next tile, and refresh the HUD (score may have grown on merge).
    call SpriteRenderClear
    call DrawBoardFull
    call DrawBoardAttrs
    call SpawnTile
    call DrawHud
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
    farcall 3, WinEnter
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
