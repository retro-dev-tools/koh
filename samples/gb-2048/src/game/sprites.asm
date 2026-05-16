; game/sprites.asm — OAM-sprite slide animation for the 2048 board.
;
; During the 8-frame slide phase (wAnimFrame in 0..7), each non-empty original
; cell is rendered as an 8x8 sprite at an interpolated position between its
; pre-move location and its destination. The board's BG tilemap is cleared at
; the start of the animation; DrawBoardFull restores it at the commit frame.
;
; OAM slot mapping: slot i corresponds to original board cell i (i in 0..15).
; Cells whose intent.dst == $FF (empty before the move) leave slot i hidden
; (y = 0). Slots 16..39 are kept hidden by SpriteRenderClear.
;
; hScratch layout used by SpriteRenderTick:
;   +0  frame (0..8)
;   +1  i  (current cell index 0..15)
;   +2  intent.dst for cell i
;   +3  intent.value for cell i
;   +4  OAM pointer high (saved across position math)
;   +5  OAM pointer low

SECTION "Sprites", ROMX, BANK[1]

; ----------------------------------------------------------------------------
; SpriteRenderClear -- hide all 40 OAM sprites by setting y = 0.
; Clobbers AF, B, HL.
; ----------------------------------------------------------------------------
SpriteRenderClear::
    ld hl, wOAMBuffer
    ld b, 40
    xor a
.loop:
    ld [hl+], a            ; y
    inc hl                 ; x
    inc hl                 ; tile
    inc hl                 ; attr
    dec b
    jr nz, .loop
    ret

; ----------------------------------------------------------------------------
; ClearBoardBg -- write TILE_FONT_SPACE to the 4x4 board region of the BG
;   tilemap (_SCRN0, rows 4..7, cols 12..15). Mirrors DrawBoardFull's walk.
;   Clobbers AF, BC, DE, HL.
; ----------------------------------------------------------------------------
ClearBoardBg::
    ld hl, _SCRN0 + 4*32 + 12
    ld d, 4                ; rows
.row:
    ld c, 4                ; cols
    ld a, TILE_FONT_SPACE
.col:
    ld [hl+], a
    dec c
    jr nz, .col
    ; HL now points at col 16. Advance to col 12 of the next row (+28).
    ld a, l
    add 28
    ld l, a
    jr nc, .no_carry
    inc h
.no_carry:
    dec d
    jr nz, .row
    ret

; ----------------------------------------------------------------------------
; SpriteRenderTick -- update all 16 board sprites for the given frame.
;   Input: A = frame (0..7). 0 = src positions, 7 = one step before dst,
;          8 (handled at commit) = dst positions (caller normally hides instead).
;   For each cell i:
;     If wMoveIntents[i].dst == $FF: hide slot i (y = 0).
;     Else: write y, x, tile, attr for sprite slot i.
;   Clobbers AF, BC, DE, HL, hScratch+0..5.
; ----------------------------------------------------------------------------
SpriteRenderTick::
    ldh [hScratch+0], a    ; save frame
    xor a
    ldh [hScratch+1], a    ; i = 0

.cell_loop:
    call _RenderOneCell
    ldh a, [hScratch+1]
    inc a
    ldh [hScratch+1], a
    cp 16
    jr nz, .cell_loop
    ret

; ----------------------------------------------------------------------------
; _RenderOneCell -- render OAM slot hScratch+1 from wMoveIntents.
;   Reads frame from hScratch+0, i from hScratch+1.
;   Clobbers AF, BC, DE, HL; uses hScratch+2..5.
; ----------------------------------------------------------------------------
_RenderOneCell:
    ; --- Load intent for cell i: dst into hScratch+2, value into hScratch+3 ---
    ldh a, [hScratch+1]
    add a
    add a                  ; i * 4
    ld c, a
    ld b, 0
    ld hl, wMoveIntents
    add hl, bc
    ld a, [hl+]            ; A = dst
    ldh [hScratch+2], a
    ld a, [hl]             ; A = value
    ldh [hScratch+3], a

    ; --- Sprite[i] pointer in HL ---
    ldh a, [hScratch+1]
    add a
    add a                  ; i * 4
    ld c, a
    ld b, 0
    ld hl, wOAMBuffer
    add hl, bc

    ldh a, [hScratch+2]    ; dst
    cp $FF
    jr nz, .visible

    ; Hide sprite slot.
    xor a
    ld [hl+], a            ; y = 0
    ld [hl+], a            ; x
    ld [hl+], a            ; tile
    ld [hl], a             ; attr
    ret

.visible:
    ; Save OAM pointer (currently at sprite[i].y).
    ld a, h
    ldh [hScratch+4], a
    ld a, l
    ldh [hScratch+5], a

    ; --- Compute oam_y ---
    ; origin_y = 48 + src_row * 8;  d_row = dst_row - src_row;
    ; oam_y    = origin_y + d_row * frame
    ldh a, [hScratch+1]    ; i
    srl a
    srl a                  ; src_row
    ld b, a                ; B = src_row

    ; origin_y in C.
    add a
    add a
    add a                  ; src_row * 8
    add 48
    ld c, a                ; C = origin_y

    ldh a, [hScratch+2]    ; dst
    srl a
    srl a                  ; dst_row
    sub b                  ; A = d_row signed
    ld b, a                ; B = d_row

    ldh a, [hScratch+0]    ; frame
    ld e, a                ; E = frame counter (loop)

    ld a, b                ; A = d_row
    or a
    jr z, .y_zero
    bit 7, a
    jr nz, .y_neg

    ld d, a                ; D = abs(d_row)
    xor a                  ; A = product accumulator
    or e                   ; if frame == 0, skip multiply
    jr z, .y_zero
.y_pos_loop:
    add d
    dec e
    jr nz, .y_pos_loop
    add c                  ; oam_y = origin + product
    jr .y_write

.y_neg:
    cpl
    inc a                  ; A = abs(d_row)
    ld d, a
    xor a
    or e
    jr z, .y_zero
.y_neg_loop:
    add d
    dec e
    jr nz, .y_neg_loop
    ld d, a                ; D = abs product
    ld a, c
    sub d                  ; oam_y = origin - product
    jr .y_write

.y_zero:
    ld a, c                ; just origin_y

.y_write:
    ; A = oam_y. Restore OAM ptr and write y; HL ends at sprite[i].x.
    ld c, a
    ldh a, [hScratch+4]
    ld h, a
    ldh a, [hScratch+5]
    ld l, a
    ld a, c
    ld [hl+], a            ; write y
    ld a, h
    ldh [hScratch+4], a
    ld a, l
    ldh [hScratch+5], a

    ; --- Compute oam_x ---
    ; origin_x = 104 + src_col * 8;  d_col = dst_col - src_col;
    ; oam_x    = origin_x + d_col * frame
    ldh a, [hScratch+1]
    and 3                  ; src_col
    ld b, a

    add a
    add a
    add a                  ; src_col * 8
    add 104
    ld c, a                ; C = origin_x

    ldh a, [hScratch+2]
    and 3                  ; dst_col
    sub b                  ; A = d_col signed
    ld b, a

    ldh a, [hScratch+0]
    ld e, a

    ld a, b
    or a
    jr z, .x_zero
    bit 7, a
    jr nz, .x_neg

    ld d, a
    xor a
    or e
    jr z, .x_zero
.x_pos_loop:
    add d
    dec e
    jr nz, .x_pos_loop
    add c
    jr .x_write

.x_neg:
    cpl
    inc a
    ld d, a
    xor a
    or e
    jr z, .x_zero
.x_neg_loop:
    add d
    dec e
    jr nz, .x_neg_loop
    ld d, a
    ld a, c
    sub d
    jr .x_write

.x_zero:
    ld a, c

.x_write:
    ld c, a
    ldh a, [hScratch+4]
    ld h, a
    ldh a, [hScratch+5]
    ld l, a
    ld a, c
    ld [hl+], a            ; write x; HL -> tile slot

    ; --- Tile id from value (same scheme as DrawBoardFull) ---
    ldh a, [hScratch+3]    ; value
    cp 10
    jr c, .digit
    sub 10
    add TILE_FONT_A        ; 10 -> 'A', 11 -> 'B', 12 -> 'C'
    jr .write_tile
.digit:
    add TILE_DIGIT_0       ; 1..9 -> TILE_DIGIT_1..9
.write_tile:
    ld [hl+], a

    ; --- Attribute: low 3 bits = OBJ palette index (1..7, capped) ---
    ldh a, [hScratch+3]
    cp 8
    jr c, .pal_ok
    ld a, 7
.pal_ok:
    ld [hl], a
    ret
