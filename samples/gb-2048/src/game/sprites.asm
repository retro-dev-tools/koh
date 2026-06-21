; game/sprites.asm — OAM-sprite slide animation for the 2048 board.
;
; Cell layout (must match render.asm's gapless board): each cell is 4 tiles
; wide × 3 tiles tall and the cells touch with no gap. Cell (r,c)'s top-left
; tile is at tilemap (row 4 + r*3, col 2 + c*4), so the strides are 4 cols =
; 32 px horizontally and 3 rows = 24 px vertically.
;
; During the slide phase we render each non-empty pre-move cell as a single
; representative 8x8 sprite (its leading digit) that travels from its src cell
; to its destination over 8 frames. We aim the sprite at where the BG renders
; that digit — col 1 of the cell (pixel x = 24 + c*32) and the middle tile-row
; (pixel y = 40 + r*24) — so it lands seamlessly when the BG commits at frame 8.
;
; OAM coordinates are pixel + 16 (Y) / pixel + 8 (X), giving the origin/stride
; constants below; the per-frame loop linearly interpolates between src and dst.
;
; Slot mapping: slot i corresponds to original cell i.  Slots 16..39 are
; kept hidden by SpriteRenderClear.
;
; hScratch layout used by SpriteRenderTick:
;   +0  frame (0..8)
;   +1  i  (current cell index 0..15)
;   +2  intent.dst for cell i
;   +3  intent.value for cell i
;   +4  OAM pointer high (saved across position math)
;   +5  OAM pointer low

SECTION "Sprites", ROMX, BANK[1]

; OAM origin of cell (0,0)'s leading digit = pixel (24, 40) + OAM offsets
; (8, 16) = (32, 56). Per-cell stride: 32 px X (4 tiles), 24 px Y (3 tiles).
SPRITE_ORIGIN_X EQU 32
SPRITE_ORIGIN_Y EQU 56
SPRITE_STRIDE_X EQU 32
SPRITE_STRIDE_Y EQU 24

; ----------------------------------------------------------------------------
; SpriteRenderClear -- hide all 40 OAM sprites by setting y = 0.
; Clobbers AF, B, HL.
; ----------------------------------------------------------------------------
SpriteRenderClear::
    ; --- Clear wOAMBuffer (the OAM DMA source) ---
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

    ; --- Push the cleared buffer to OAM *this* frame so the freshly-committed
    ;     board never shows the slide sprites on top of it. We trigger the OAM
    ;     DMA directly instead of writing OAM with the CPU: the DMA engine
    ;     reaches OAM regardless of PPU mode, whereas CPU OAM writes are dropped
    ;     when OAM is locked (modes 2/3) — which on real hardware left a stray
    ;     slide digit parked over the committed board for a frame. (The IRQ's
    ;     per-frame OAM DMA would otherwise only catch up 1-2 frames later.)
    call hOAMDMA
    ret

; ----------------------------------------------------------------------------
; ClearBoardBg -- snap the board region to the static empty grid for the slide.
;   HDMAs wEmptyBgBuf (tiles, bank 0) and wEmptyAttrBuf (palette 1, bank 1)
;   over rows 3..17 so the grid frame stays visible while the numbered tiles
;   travel as OAM sprites — instead of the whole board blanking out. GDMA
;   bypasses PPU mode-3 lockout, so this is safe and ~3x cheaper than the old
;   per-byte CPU clear. Mirrors CommitBoardHdma's transfer (480 bytes = 29+1
;   blocks each). Clobbers AF.
; ----------------------------------------------------------------------------
ClearBoardBg::
    ; --- BG tiles (bank 0) ---
    ld a, HIGH(wEmptyBgBuf)
    ldh [rHDMA1], a
    ld a, LOW(wEmptyBgBuf)
    ldh [rHDMA2], a
    ld a, HIGH(_SCRN0 + 3 * 32)
    ldh [rHDMA3], a
    ld a, LOW(_SCRN0 + 3 * 32)
    ldh [rHDMA4], a
    ld a, 29                   ; (480 / 16) - 1 = 29; bit 7 = 0 -> GDMA
    ldh [rHDMA5], a

    ; --- Attr bytes (bank 1) ---
    ld a, 1
    ldh [rVBK], a
    ld a, HIGH(wEmptyAttrBuf)
    ldh [rHDMA1], a
    ld a, LOW(wEmptyAttrBuf)
    ldh [rHDMA2], a
    ld a, HIGH(_SCRN0 + 3 * 32)
    ldh [rHDMA3], a
    ld a, LOW(_SCRN0 + 3 * 32)
    ldh [rHDMA4], a
    ld a, 29
    ldh [rHDMA5], a
    xor a
    ldh [rVBK], a
    ret

; ----------------------------------------------------------------------------
; SpriteRenderTick -- update all 16 board sprites for the given frame.
;   Input: A = frame (0..7). 0 = src positions, 7 = one step before dst.
;   For each cell i:
;     If wMoveIntents[i].dst == $FF: hide slot i (y = 0).
;     Else: write y, x, tile, attr for sprite slot i.
;   Clobbers AF, BC, DE, HL, hScratch+0..5.
; ----------------------------------------------------------------------------
SpriteRenderTick::
    ldh [hScratch+0], a
    xor a
    ldh [hScratch+1], a

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
;   Position formula:
;     oam_x = origin_x_src + (d_col * SPRITE_STRIDE_X * frame) / 8
;     oam_y = origin_y_src + (d_row * SPRITE_STRIDE_Y * frame) / 8
;   We compute (d * frame) once (max 3 * 7 = 21 cells*frames) then multiply
;   by stride / 8 (= 5 for X, 4 for Y) via add-loops.
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
    ld a, [hl+]
    ldh [hScratch+2], a
    ld a, [hl]
    ldh [hScratch+3], a

    ; --- Sprite[i] pointer in HL ---
    ldh a, [hScratch+1]
    add a
    add a
    ld c, a
    ld b, 0
    ld hl, wOAMBuffer
    add hl, bc

    ldh a, [hScratch+2]
    cp $FF
    jr nz, .visible

    ; Hide sprite.
    xor a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl], a
    ret

.visible:
    ; Save OAM pointer.
    ld a, h
    ldh [hScratch+4], a
    ld a, l
    ldh [hScratch+5], a

    ; --- Compute Y ---
    ; src_row = i >> 2, dst_row = dst >> 2, d_row = dst_row - src_row.
    ldh a, [hScratch+1]
    srl a
    srl a
    ld b, a                ; B = src_row

    ; origin_y = SPRITE_ORIGIN_Y + src_row * SPRITE_STRIDE_Y.
    ld c, 0
    or a
    jr z, .y_origin_done
    ld d, a                ; D = src_row
.y_origin_loop:
    ld a, c
    add SPRITE_STRIDE_Y
    ld c, a
    dec d
    jr nz, .y_origin_loop
.y_origin_done:
    ld a, c
    add SPRITE_ORIGIN_Y
    ld c, a                ; C = origin_y

    ldh a, [hScratch+2]
    srl a
    srl a                  ; dst_row
    sub b                  ; A = signed d_row

    ; Pixel delta = d_row * SPRITE_STRIDE_Y * frame / 8 = d_row * 4 * frame.
    ld b, a
    or a
    jr z, .y_apply_zero
    bit 7, a
    jr nz, .y_neg

    ; Positive: pixel_delta = b * 4 * frame.
    ldh a, [hScratch+0]
    ld e, a                ; E = frame (0..7)
    or a
    jr z, .y_apply_zero
    ld d, b                ; D = |d_row|
    xor a
.y_pos_loop:
    add d
    dec e
    jr nz, .y_pos_loop
    ; A = |d_row| * frame. Multiply by 3 (SPRITE_STRIDE_Y / 8 = 3). E = 0 here.
    ld e, a
    add a
    add e                  ; *3
    add c                  ; + origin_y
    jr .y_write

.y_neg:
    cpl
    inc a
    ld d, a                ; D = |d_row|
    ldh a, [hScratch+0]
    ld e, a
    or a
    jr z, .y_apply_zero
    xor a
.y_neg_loop:
    add d
    dec e
    jr nz, .y_neg_loop
    ld e, a
    add a
    add e                  ; *3 (SPRITE_STRIDE_Y / 8 = 3). E = 0 before this.
    ld d, a
    ld a, c
    sub d
    jr .y_write

.y_apply_zero:
    ld a, c

.y_write:
    ld c, a
    ldh a, [hScratch+4]
    ld h, a
    ldh a, [hScratch+5]
    ld l, a
    ld a, c
    ld [hl+], a
    ld a, h
    ldh [hScratch+4], a
    ld a, l
    ldh [hScratch+5], a

    ; --- Compute X ---
    ldh a, [hScratch+1]
    and 3
    ld b, a                ; B = src_col

    ; origin_x = SPRITE_ORIGIN_X + src_col * SPRITE_STRIDE_X (40).
    ld c, 0
    or a
    jr z, .x_origin_done
    ld d, a
.x_origin_loop:
    ld a, c
    add SPRITE_STRIDE_X
    ld c, a
    dec d
    jr nz, .x_origin_loop
.x_origin_done:
    ld a, c
    add SPRITE_ORIGIN_X
    ld c, a                ; C = origin_x

    ldh a, [hScratch+2]
    and 3                  ; dst_col
    sub b                  ; A = signed d_col

    ; Pixel delta = d_col * 5 * frame (SPRITE_STRIDE_X / 8 = 5).
    ld b, a
    or a
    jr z, .x_apply_zero
    bit 7, a
    jr nz, .x_neg

    ldh a, [hScratch+0]
    ld e, a
    or a
    jr z, .x_apply_zero
    ld d, b
    xor a
.x_pos_loop:
    add d
    dec e
    jr nz, .x_pos_loop
    ; A = |d_col| * frame. Multiply by 4 (SPRITE_STRIDE_X / 8 = 4).
    add a
    add a                  ; *4
    add c                  ; + origin_x
    jr .x_write

.x_neg:
    cpl
    inc a
    ld d, a
    ldh a, [hScratch+0]
    ld e, a
    or a
    jr z, .x_apply_zero
    xor a
.x_neg_loop:
    add d
    dec e
    jr nz, .x_neg_loop
    add a
    add a                  ; *4 (SPRITE_STRIDE_X / 8 = 4)
    ld d, a
    ld a, c
    sub d
    jr .x_write

.x_apply_zero:
    ld a, c

.x_write:
    ld c, a
    ldh a, [hScratch+4]
    ld h, a
    ldh a, [hScratch+5]
    ld l, a
    ld a, c
    ld [hl+], a            ; write x, HL -> tile slot

    ; --- Tile id from value via ValueToSpriteTile lookup (leading digit) ---
    ldh a, [hScratch+3]        ; A = value (1..12)
    ld e, a
    ld d, 0
    push hl
    ld hl, ValueToSpriteTile
    add hl, de
    ld a, [hl]
    pop hl
    ld [hl+], a

    ; --- Attribute: ValueToPalette[value] for matching cell colour ---
    ldh a, [hScratch+3]
    cp 13
    jr c, .val_ok
    ld a, 12
.val_ok:
    ld d, 0
    ld e, a
    push hl
    ld hl, ValueToPalette
    add hl, de
    ld a, [hl]
    pop hl
    ld [hl], a
    ret
