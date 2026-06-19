; game/board.asm — 2048 board logic (BANK 1)
;
; All routines are in BANK[1]. Calls to RngNext, RngRange, ScoreAdd are
; same-bank — plain call, no farcall overhead.
;
; wMoveIntents filling is deferred to T22 (animation). For now MoveLeft only
; updates wBoard, wScore, wSlideValid, and wPrevBoard.
;
; hScratch layout used by MoveLeft/_CompactLeft:
;   hScratch+0..3  working row (4 cells), also scratch during compact
;   hScratch+4     direction table base ptr — low byte
;   hScratch+5     direction table base ptr — high byte
;   hScratch+6     outer row counter (0..3)
;   hScratch+7     merged-flag bitmask for current row (bits 0-2 = slots 0-2)

SECTION "Board Tables", ROMX, BANK[1]

DirLeft::
    db  0, 1, 2, 3,   4, 5, 6, 7,   8, 9,10,11,  12,13,14,15
DirRight::
    db  3, 2, 1, 0,   7, 6, 5, 4,  11,10, 9, 8,  15,14,13,12
DirUp::
    db  0, 4, 8,12,   1, 5, 9,13,   2, 6,10,14,   3, 7,11,15
DirDown::
    db 12, 8, 4, 0,  13, 9, 5, 1,  14,10, 6, 2,  15,11, 7, 3

SECTION "Board Logic", ROMX, BANK[1]

; ----------------------------------------------------------------------------
; BoardInit -- zero wBoard (16 bytes), then call SpawnTile twice.
; Clobbers AF, BC, HL.
; ----------------------------------------------------------------------------
BoardInit::
    xor a
    ld hl, wBoard
    ld b, 16
.clear:
    ld [hl+], a
    dec b
    jr nz, .clear
    call SpawnTile
    call SpawnTile
    ret

; ----------------------------------------------------------------------------
; StartNewGame -- single farcall entry point used by TitleTick. Resets the
;   score, populates a fresh board, and redraws the board BG (tiles + per-
;   value attribute bytes). The redraw is needed because title-screen text
;   overlaps board cells 12 and 13 on tilemap row 7.
;   Clobbers AF, BC, DE, HL.
; ----------------------------------------------------------------------------
StartNewGame::
    call ScoreReset
    call BoardInit
    ; Wipe BG tilemap + attribute map with the LCD off so the 2 KiB of
    ; writes always land (a 1 KiB FillVram alone overflows one VBlank).
    ; Then re-paint HUD divider, board, and HUD.
    xor a
    ldh [rLCDC], a
    ld hl, _SCRN0
    ld bc, $0400
    ld a, TILE_FONT_SPACE
    call FillVram
    ld a, 1
    ldh [rVBK], a
    ; HUD rows 0-2 -> palette 0 (white plate).
    ld hl, _SCRN0
    ld bc, 3 * 32
    xor a
    call FillVram
    ; Board rows 3-17 -> palette 1 (dark slate frame).
    ld hl, _SCRN0 + 3 * 32
    ld bc, 15 * 32
    ld a, 1
    call FillVram
    xor a
    ldh [rVBK], a
    ; HUD divider rule across row 2.
    ld hl, _SCRN0 + 2 * 32
    ld b, 20
    ld a, TILE_HUD_RULE
.rule_loop:
    ld [hl+], a
    dec b
    jr nz, .rule_loop
    call RecomputeScoreCache    ; refresh digits — score was just reset
    call DrawBoardFull
    call DrawHud
    ; LCD back on.
    ld a, LCDCF_ON | LCDCF_BG8000 | LCDCF_BGON | LCDCF_OBJ8 | LCDCF_OBJON
    ldh [rLCDC], a
    ret

; ----------------------------------------------------------------------------
; ResumeAfterWin -- repaint the board + HUD after the win screen cleared
;   it. Does not reset state or spawn tiles; just makes the saved board
;   state visible again so the player can keep playing toward 4096.
;   Clobbers AF, BC, DE, HL.
; ----------------------------------------------------------------------------
ResumeAfterWin::
    call DrawBoardFull
    call DrawBoardAttrs
    call DrawHud
    ret

; ----------------------------------------------------------------------------
; SpawnTile -- place a new tile on a random empty cell of wBoard.
;   Count empty cells; if zero, return immediately.
;   Choose RngRange(empty_count) to pick which empty cell (0-indexed).
;   Walk wBoard to find it. Determine value: RngNext < 26 -> write 2 (="4"),
;   else write 1 (="2").  (26/256 ≈ 10.2% chance for "4".)
;   Clobbers AF, BC, DE, HL.
; ----------------------------------------------------------------------------
SpawnTile::
    ; Count empty cells into C.
    ld hl, wBoard
    ld b, 16
    ld c, 0
.count:
    ld a, [hl+]
    or a
    jr nz, .count_next
    inc c
.count_next:
    dec b
    jr nz, .count

    ld a, c
    or a
    ret z                   ; no empty cells

    ; RngRange(C) -> A = target empty-cell index (0..C-1).
    ld b, c
    call RngRange
    ld e, a                 ; E = target index

    ; Walk wBoard to find the E-th empty cell.
    ld hl, wBoard
    ld d, 0                 ; D = seen-so-far empty count
.find:
    ld a, [hl]
    or a
    jr nz, .find_next
    ld a, d
    cp e
    jr z, .found
    inc d
.find_next:
    inc hl
    jr .find

.found:
    ; Choose value: RngNext -> if < 26 write 2 (="4"), else write 1 (="2").
    push hl
    call RngNext
    pop hl
    cp 26
    jr c, .spawn_four
    ld a, 1
    ld [hl], a
    ret
.spawn_four:
    ld a, 2
    ld [hl], a
    ret

; ----------------------------------------------------------------------------
; MoveLeft -- apply a slide in the direction described by the given table.
;   Input:  HL = pointer to one of DirLeft/DirRight/DirUp/DirDown.
;   Output: wBoard updated in-place, wPrevBoard = board before the move,
;           wScore incremented for each merge, wSlideValid set to 1 if any
;           cell value changed (else 0).
;   wMoveIntents is NOT filled here (deferred to T22).
;   Clobbers AF, BC, DE, HL (and hScratch+0..7).
; ----------------------------------------------------------------------------
MoveLeft::
    ; Save direction table base in hScratch+4..5.
    ld a, l
    ldh [hScratch+4], a
    ld a, h
    ldh [hScratch+5], a

    ; wPrevBoard = wBoard (snapshot 16 bytes).
    ld hl, wBoard
    ld de, wPrevBoard
    ld b, 16
.snap:
    ld a, [hl+]
    ld [de], a
    inc de
    dec b
    jr nz, .snap

    ; wSlideValid = 0.
    xor a
    ld [wSlideValid], a

    ; Row loop: process rows 0..3.
    xor a
    ldh [hScratch+6], a     ; row = 0

.row_loop:
    ; ── Load 4 cells from wBoard into hScratch+0..3 ──────────────────────────
    ; Table offset for this row = row * 4. Advance HL to that position.
    ldh a, [hScratch+6]
    ld b, a
    sla b
    sla b                   ; B = row * 4

    ldh a, [hScratch+4]
    ld l, a
    ldh a, [hScratch+5]
    ld h, a
    ld a, b
    or a
    jr z, .load_no_adv
.load_adv:
    inc hl
    dec a
    jr nz, .load_adv
.load_no_adv:

    ; Read 4 table entries (board indices) and load the cells.
    ; We need HL to walk through 4 table bytes. We'll read all 4 indices first,
    ; storing them in E (index0), D (index1), then handle index2 and index3
    ; with two more loads. Use stack to save/restore HL between lookups.

    ; Index 0 -> hScratch+0
    ld a, [hl+]
    ld c, a                 ; C = board index for slot 0
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]
    ldh [hScratch+0], a
    pop hl

    ; Index 1 -> hScratch+1
    ld a, [hl+]
    ld c, a
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]
    ldh [hScratch+1], a
    pop hl

    ; Index 2 -> hScratch+2
    ld a, [hl+]
    ld c, a
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]
    ldh [hScratch+2], a
    pop hl

    ; Index 3 -> hScratch+3
    ld a, [hl+]
    ld c, a
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]
    ldh [hScratch+3], a
    pop hl

    ; ── First compact_left ────────────────────────────────────────────────────
    call _CompactLeft

    ; ── Merge pass ────────────────────────────────────────────────────────────
    xor a
    ldh [hScratch+7], a     ; merged-flag bitmask = 0

    ; i=0: check tmp[0] and tmp[1].
    ldh a, [hScratch+0]
    or a
    jr z, .mi1
    ld b, a                 ; B = tmp[0]
    ldh a, [hScratch+1]
    cp b
    jr nz, .mi1
    ldh a, [hScratch+7]
    and %00000001
    jr nz, .mi1             ; slot 0 already merged
    ; Merge: tmp[0]++, tmp[1]=0, ScoreAdd(new tmp[0]).
    ldh a, [hScratch+0]
    inc a
    ldh [hScratch+0], a
    push af
    ldh a, [hScratch+7]
    or %00000001
    ldh [hScratch+7], a
    xor a
    ldh [hScratch+1], a
    pop af
    call ScoreAdd

.mi1:
    ; i=1: check tmp[1] and tmp[2].
    ldh a, [hScratch+1]
    or a
    jr z, .mi2
    ld b, a
    ldh a, [hScratch+2]
    cp b
    jr nz, .mi2
    ldh a, [hScratch+7]
    and %00000010
    jr nz, .mi2
    ldh a, [hScratch+1]
    inc a
    ldh [hScratch+1], a
    push af
    ldh a, [hScratch+7]
    or %00000010
    ldh [hScratch+7], a
    xor a
    ldh [hScratch+2], a
    pop af
    call ScoreAdd

.mi2:
    ; i=2: check tmp[2] and tmp[3].
    ldh a, [hScratch+2]
    or a
    jr z, .merge_done
    ld b, a
    ldh a, [hScratch+3]
    cp b
    jr nz, .merge_done
    ldh a, [hScratch+7]
    and %00000100
    jr nz, .merge_done
    ldh a, [hScratch+2]
    inc a
    ldh [hScratch+2], a
    push af
    ldh a, [hScratch+7]
    or %00000100
    ldh [hScratch+7], a
    xor a
    ldh [hScratch+3], a
    pop af
    call ScoreAdd

.merge_done:
    ; ── Second compact_left ───────────────────────────────────────────────────
    call _CompactLeft

    ; ── Write back, detect slide ──────────────────────────────────────────────
    ; Rebuild table pointer at this row's offset.
    ldh a, [hScratch+6]
    ld b, a
    sla b
    sla b

    ldh a, [hScratch+4]
    ld l, a
    ldh a, [hScratch+5]
    ld h, a
    ld a, b
    or a
    jr z, .wb_no_adv
.wb_adv:
    inc hl
    dec a
    jr nz, .wb_adv
.wb_no_adv:

    ; Write back cell 0.
    ld a, [hl+]
    ld c, a                 ; C = board index
    ldh a, [hScratch+0]
    ld d, a                 ; D = new value
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]              ; old value
    cp d
    jr z, .wb0_same
    ld a, 1
    ld [wSlideValid], a
.wb0_same:
    ld [hl], d
    pop hl

    ; Write back cell 1.
    ld a, [hl+]
    ld c, a
    ldh a, [hScratch+1]
    ld d, a
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]
    cp d
    jr z, .wb1_same
    ld a, 1
    ld [wSlideValid], a
.wb1_same:
    ld [hl], d
    pop hl

    ; Write back cell 2.
    ld a, [hl+]
    ld c, a
    ldh a, [hScratch+2]
    ld d, a
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]
    cp d
    jr z, .wb2_same
    ld a, 1
    ld [wSlideValid], a
.wb2_same:
    ld [hl], d
    pop hl

    ; Write back cell 3.
    ld a, [hl+]
    ld c, a
    ldh a, [hScratch+3]
    ld d, a
    push hl
    ld h, HIGH(wBoard)
    ld l, LOW(wBoard)
    ld b, 0
    add hl, bc
    ld a, [hl]
    cp d
    jr z, .wb3_same
    ld a, 1
    ld [wSlideValid], a
.wb3_same:
    ld [hl], d
    pop hl

    ; Advance row counter.
    ldh a, [hScratch+6]
    inc a
    ldh [hScratch+6], a
    cp 4
    jp nz, .row_loop

    ret

; ----------------------------------------------------------------------------
; _CompactLeft -- compact hScratch+0..3 leftward: non-zero values slide to
;   the front, zeros fill the tail.
;   Algorithm: collect all non-zero values from slots 0-3 into B,C,D,E
;   (up to 4 values), then write them back with zeros filling the tail.
;   Clobbers AF, BC, DE.
; ----------------------------------------------------------------------------
_CompactLeft:
    ; Use B=count, C/D/E/L (via A) to collect up to 4 non-zero values.
    ; We'll store collected values in an array on the stack or in registers.
    ; Since we only have 4 slots and need to write back to HRAM, use a simple
    ; approach: scan 0-3, write non-zeros sequentially to 0-3, zero the rest.
    ;
    ; Pass 1: read all 4 slots into B,C,D,E (B=count so far is tracked by
    ; which regs are used). Actually use a write pointer in B (0..3) and
    ; read each slot, placing non-zero into the write-pointer slot then inc.
    ; But reading and writing the same HRAM slots in order needs care.
    ; Since we go left-to-right and the write pointer is always <= read pointer,
    ; it is safe to write in-place as we go.
    ld b, 0                 ; B = write pointer

    ; Slot 0.
    ldh a, [hScratch+0]
    or a
    jr z, .cL1              ; zero: skip
    ; Non-zero: write to slot B.
    call _WriteToSlot       ; A=value, B=slot
    inc b
.cL1:
    ; Slot 1.
    ldh a, [hScratch+1]
    or a
    jr z, .cL2
    call _WriteToSlot
    inc b
.cL2:
    ; Slot 2.
    ldh a, [hScratch+2]
    or a
    jr z, .cL3
    call _WriteToSlot
    inc b
.cL3:
    ; Slot 3.
    ldh a, [hScratch+3]
    or a
    jr z, .cL_fill
    call _WriteToSlot
    inc b
.cL_fill:
    ; Zero-fill remaining slots B..3.
    ld a, b
    cp 4
    ret nc
.cL_zero:
    ld a, 0
    call _WriteToSlot
    inc b
    ld a, b
    cp 4
    jr c, .cL_zero
    ret

; _WriteToSlot: write A to hScratch+B (B in 0..3). Clobbers nothing extra.
_WriteToSlot:
    ld c, a                 ; save value
    ld a, b
    or a
    jr z, .ws0
    cp 1
    jr z, .ws1
    cp 2
    jr z, .ws2
    ; slot 3
    ld a, c
    ldh [hScratch+3], a
    ld a, c                 ; restore A = value
    ret
.ws0:
    ld a, c
    ldh [hScratch+0], a
    ld a, c
    ret
.ws1:
    ld a, c
    ldh [hScratch+1], a
    ld a, c
    ret
.ws2:
    ld a, c
    ldh [hScratch+2], a
    ld a, c
    ret

; ----------------------------------------------------------------------------
; CheckWin -- return Z set if any cell in wBoard equals 11 (= "2048").
;   Clobbers AF, BC, HL.
; ----------------------------------------------------------------------------
CheckWin::
    ld hl, wBoard
    ld b, 16
.win_loop:
    ld a, [hl+]
    cp 11
    ret z                   ; Z set: found a 2048 cell
    dec b
    jr nz, .win_loop
    or 1                    ; ensure NZ (no 2048 found)
    ret

; ----------------------------------------------------------------------------
; CheckLose -- return Z set if the board is a lose state: every cell occupied
;   AND no two adjacent cells have the same value (horizontally or vertically).
;   Return NZ if any move is still possible (empty cell or adjacent equal pair).
;   Clobbers AF, BC, DE, HL.
; ----------------------------------------------------------------------------
CheckLose::
    ; Check for any empty cell.
    ld hl, wBoard
    ld b, 16
.cl_empty:
    ld a, [hl+]
    or a
    jr z, .cl_not_lose      ; found empty -> not lose
    dec b
    jr nz, .cl_empty

    ; Check horizontal adjacents: for each row, compare (col, col+1).
    ; wBoard layout: [r0c0 r0c1 r0c2 r0c3 | r1c0 ... | r2c0 ... | r3c0 ...]
    ld hl, wBoard
    ld b, 4                 ; 4 rows
.cl_hrow:
    ld a, [hl+]             ; col 0
    ld d, a
    ld a, [hl]
    cp d                    ; col 1 vs col 0
    jr z, .cl_not_lose
    ld a, [hl+]             ; col 1
    ld d, a
    ld a, [hl]
    cp d                    ; col 2 vs col 1
    jr z, .cl_not_lose
    ld a, [hl+]             ; col 2
    ld d, a
    ld a, [hl]
    cp d                    ; col 3 vs col 2
    jr z, .cl_not_lose
    inc hl                  ; skip col 3, advance to next row
    dec b
    jr nz, .cl_hrow

    ; Check vertical adjacents: cell[i] vs cell[i+4], for i in 0..11.
    ld hl, wBoard
    ld b, 12
.cl_vert:
    ld a, [hl]
    ld d, a
    push hl
    inc hl
    inc hl
    inc hl
    inc hl
    ld a, [hl]
    pop hl
    cp d
    jr z, .cl_not_lose
    inc hl
    dec b
    jr nz, .cl_vert

    ; Lose state: Z set.
    cp a                    ; A == A, Z set guaranteed
    ret

.cl_not_lose:
    or 1                    ; NZ
    ret

; ----------------------------------------------------------------------------
; Move Helpers — direction-specific wrappers called via farcall from bank 0.
;   Each sets HL to the appropriate direction table, calls MoveLeft, then
;   calls AnimStart if the move changed the board (wSlideValid != 0).
; ----------------------------------------------------------------------------
SECTION "Move Helpers", ROMX, BANK[1]

MoveLeft_DirLeft::
    ld hl, DirLeft
    call MoveLeft
    ld a, [wSlideValid]
    or a
    ret z
    ld hl, DirLeft
    call FillMoveIntents
    call AnimStart
    ret

MoveLeft_DirRight::
    ld hl, DirRight
    call MoveLeft
    ld a, [wSlideValid]
    or a
    ret z
    ld hl, DirRight
    call FillMoveIntents
    call AnimStart
    ret

MoveLeft_DirUp::
    ld hl, DirUp
    call MoveLeft
    ld a, [wSlideValid]
    or a
    ret z
    ld hl, DirUp
    call FillMoveIntents
    call AnimStart
    ret

MoveLeft_DirDown::
    ld hl, DirDown
    call MoveLeft
    ld a, [wSlideValid]
    or a
    ret z
    ld hl, DirDown
    call FillMoveIntents
    call AnimStart
    ret
