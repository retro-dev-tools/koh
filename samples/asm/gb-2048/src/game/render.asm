; render.asm — VRAM init, palette setup, board/HUD draw routines.
;
; Layout (20x18 tile screen):
;   row  0  : HUD row 1 — "BEST   nnnnnnn"
;   row  1  : HUD row 2 — "SCORE  nnnnnnn"
;   row  2  : single horizontal rule (TILE_HUD_RULE)
;   rows 3-17: board area
;     cells stacked vertically at rows 3-5, 7-9, 11-13, 15-17
;     (rows 6, 10, 14 are inter-cell gaps filled with TILE_FONT_SPACE)
;   cols 0-18 carry the 4 cells; col 19 is right margin.
;
; Each cell is 4 tiles wide × 3 tiles tall (32x24 px):
;   row 0 (top)    : TILE_CELL_TOP_EDGE × 4
;   row 1 (digits) : up to 4 digit tiles, padded with TILE_CELL_FILL
;   row 2 (bottom) : TILE_CELL_BOT_EDGE × 4
;
; The board commit phase has a hard 4560-T-cycle budget (one CGB VBlank).
; To stay within budget we use a precomputed cell-address table and a single
; pass that writes the cell's tilemap entries AND its CGB attribute bytes
; back-to-back, instead of two separate passes. Per-cell cost ~70 T
; including the bank-1 attribute writes.

SECTION "Render", ROMX, BANK[1]

; -----------------------------------------------------------------------------
; Screen-coordinate constants.
;
; Cells are 4 tiles wide × 3 tiles tall and touch directly (no gap tiles).
; The 4×4 grid spans 16 cols × 12 rows, centred at cols 2..17, rows 4..15.
; Visual separation comes from the per-value palette colours alone, so the
; empty area reads as one contiguous board surface like the web 2048.
; -----------------------------------------------------------------------------
BOARD_FIRST_ROW    EQU 4
BOARD_FIRST_COL    EQU 2

; -----------------------------------------------------------------------------
; CellAddr — precomputed tilemap base address for each of the 16 cells.
; cell (r,c) top-left = _SCRN0 + (BOARD_FIRST_ROW + r*3)*32 + (BOARD_FIRST_COL + c*4)
; -----------------------------------------------------------------------------
CellAddr::
    dw _SCRN0 + (4 + 0*3)*32 + (2 + 0*4)
    dw _SCRN0 + (4 + 0*3)*32 + (2 + 1*4)
    dw _SCRN0 + (4 + 0*3)*32 + (2 + 2*4)
    dw _SCRN0 + (4 + 0*3)*32 + (2 + 3*4)
    dw _SCRN0 + (4 + 1*3)*32 + (2 + 0*4)
    dw _SCRN0 + (4 + 1*3)*32 + (2 + 1*4)
    dw _SCRN0 + (4 + 1*3)*32 + (2 + 2*4)
    dw _SCRN0 + (4 + 1*3)*32 + (2 + 3*4)
    dw _SCRN0 + (4 + 2*3)*32 + (2 + 0*4)
    dw _SCRN0 + (4 + 2*3)*32 + (2 + 1*4)
    dw _SCRN0 + (4 + 2*3)*32 + (2 + 2*4)
    dw _SCRN0 + (4 + 2*3)*32 + (2 + 3*4)
    dw _SCRN0 + (4 + 3*3)*32 + (2 + 0*4)
    dw _SCRN0 + (4 + 3*3)*32 + (2 + 1*4)
    dw _SCRN0 + (4 + 3*3)*32 + (2 + 2*4)
    dw _SCRN0 + (4 + 3*3)*32 + (2 + 3*4)

; -----------------------------------------------------------------------------
; EmptyGridBgTemplate — the board region (rows 3..17, 32 cols) drawn with all
; 16 cells empty (value 0). BuildEmptyGridBuffers copies this into the aligned
; wEmptyBgBuf at boot; ClearBoardBg then HDMAs that buffer over the board each
; slide so the grid stays put while the numbered tiles travel as sprites.
; Byte-for-byte identical to what BuildBoardBuffers emits for value-0 cells, so
; the frame-8 commit transitions seamlessly. Layout: 1 margin row, 4 cell-rows
; (top/fill/bottom each), 2 margin rows = 15 rows × 32 = 480 bytes.
; -----------------------------------------------------------------------------
EmptyGridBgTemplate::
    FILL_BYTES 32, TILE_FONT_SPACE                       ; screen row 3 (margin)
    GRID_CELLROW TILE_CELL_TOP_EDGE, TILE_CELL_TOP_EDGE_R
    GRID_CELLROW TILE_CELL_FILL,     TILE_CELL_FILL_R
    GRID_CELLROW TILE_CELL_BOT_EDGE, TILE_CELL_BOT_EDGE_R
    GRID_CELLROW TILE_CELL_TOP_EDGE, TILE_CELL_TOP_EDGE_R
    GRID_CELLROW TILE_CELL_FILL,     TILE_CELL_FILL_R
    GRID_CELLROW TILE_CELL_BOT_EDGE, TILE_CELL_BOT_EDGE_R
    GRID_CELLROW TILE_CELL_TOP_EDGE, TILE_CELL_TOP_EDGE_R
    GRID_CELLROW TILE_CELL_FILL,     TILE_CELL_FILL_R
    GRID_CELLROW TILE_CELL_BOT_EDGE, TILE_CELL_BOT_EDGE_R
    GRID_CELLROW TILE_CELL_TOP_EDGE, TILE_CELL_TOP_EDGE_R
    GRID_CELLROW TILE_CELL_FILL,     TILE_CELL_FILL_R
    GRID_CELLROW TILE_CELL_BOT_EDGE, TILE_CELL_BOT_EDGE_R
    FILL_BYTES 64, TILE_FONT_SPACE                       ; screen rows 16-17 (margin)

; -----------------------------------------------------------------------------
; BG palette data — 8 GBC palettes × 4 colours × 2 bytes = 64 bytes.
; RGB555 word: bits 0-4 = R, 5-9 = G, 10-14 = B.
; Each palette uses colour 2 as the cell tint (or HUD background for pal 0)
; and colour 3 as the foreground (text / digit).
; -----------------------------------------------------------------------------
BgPaletteData::
    ; Classic 2048 colour scheme — pale beige board with progressively warmer
    ; cell tints. Palette slot semantics across all palettes:
    ;   colour 0 — pale-beige board surface ($577F) — kept identical across
    ;              all palettes so HUD / outside / between-cell pixels read
    ;              as one continuous tan board.
    ;   colour 1 — used by HUD-rule (palette 0) or unused (palettes 1..7)
    ;   colour 2 — cell tint (HUD label/digit "plate" for palette 0)
    ;   colour 3 — digit / shadow / HUD text — dark brown for low tiers,
    ;              white for the bright red/orange tiers so the digits stay
    ;              readable.

    ; Palette 0 — HUD strip. colour 0/2 = same beige as the board so the
    ; HUD blends with the surface; colour 1 = dark brown for the rule line;
    ; colour 3 = dark brown for label/digit text.
    dw $577F, $0884, $577F, $0884

    ; Palette 1 — outside board / empty cell.
    ;   colour 0 = beige board surface
    ;   colour 2 = empty cell tint (slightly lighter and warmer)
    dw $577F, $7FDF, $63FF, $1085

    ; Palette 2 — value 1 ("2") — light cream.
    dw $577F, $7FFF, $4BFF, $0888

    ; Palette 3 — value 2 ("4") — buttery cream.
    dw $577F, $5FFF, $2BBF, $0888

    ; Palette 4 — values 3-4 ("8", "16") — orange.
    dw $577F, $237F, $0E1F, $7FFF

    ; Palette 5 — values 5-6 ("32", "64") — bright red-orange.
    dw $577F, $11BF, $095F, $7FFF

    ; Palette 6 — values 7-9 ("128", "256", "512") — deep red.
    dw $577F, $089F, $005C, $7FFF

    ; Palette 7 — values 10+ ("1024", "2048", "4096") — gold.
    dw $577F, $239F, $137F, $7FFF

; -----------------------------------------------------------------------------
; ValueToPalette — board value (0..12) → CGB BG palette index (0..7).
; -----------------------------------------------------------------------------
ValueToPalette::
    db 1   ; val 0  — empty cell
    db 2   ; val 1  — "2"
    db 3   ; val 2  — "4"
    db 4   ; val 3  — "8"
    db 4   ; val 4  — "16"
    db 5   ; val 5  — "32"
    db 5   ; val 6  — "64"
    db 6   ; val 7  — "128"
    db 6   ; val 8  — "256"
    db 6   ; val 9  — "512"
    db 7   ; val 10 — "1024"
    db 7   ; val 11 — "2048"
    db 7   ; val 12 — "4096"

; -----------------------------------------------------------------------------
; ValueToDigits — four tile IDs to fill the digit row of a cell, per value.
; -----------------------------------------------------------------------------
ValueToDigits::
    ; Column 3 of every row uses the right-border variant tile so adjacent
    ; cells get a 1-px vertical separator without a full gap tile.
    db TILE_CELL_FILL, TILE_CELL_FILL, TILE_CELL_FILL, TILE_CELL_FILL_R  ; 0
    db TILE_CELL_FILL, TILE_DIGIT_2,   TILE_CELL_FILL, TILE_CELL_FILL_R  ; 1 "2"
    db TILE_CELL_FILL, TILE_DIGIT_4,   TILE_CELL_FILL, TILE_CELL_FILL_R  ; 2 "4"
    db TILE_CELL_FILL, TILE_DIGIT_8,   TILE_CELL_FILL, TILE_CELL_FILL_R  ; 3 "8"
    db TILE_CELL_FILL, TILE_DIGIT_1,   TILE_DIGIT_6,   TILE_CELL_FILL_R  ; 4 "16"
    db TILE_CELL_FILL, TILE_DIGIT_3,   TILE_DIGIT_2,   TILE_CELL_FILL_R  ; 5 "32"
    db TILE_CELL_FILL, TILE_DIGIT_6,   TILE_DIGIT_4,   TILE_CELL_FILL_R  ; 6 "64"
    db TILE_DIGIT_1,   TILE_DIGIT_2,   TILE_DIGIT_8,   TILE_CELL_FILL_R  ; 7 "128"
    db TILE_DIGIT_2,   TILE_DIGIT_5,   TILE_DIGIT_6,   TILE_CELL_FILL_R  ; 8 "256"
    db TILE_DIGIT_5,   TILE_DIGIT_1,   TILE_DIGIT_2,   TILE_CELL_FILL_R  ; 9 "512"
    db TILE_DIGIT_1,   TILE_DIGIT_0,   TILE_DIGIT_2,   TILE_DIGIT_4_R    ;10 "1024"
    db TILE_DIGIT_2,   TILE_DIGIT_0,   TILE_DIGIT_4,   TILE_DIGIT_8_R    ;11 "2048"
    db TILE_DIGIT_4,   TILE_DIGIT_0,   TILE_DIGIT_9,   TILE_DIGIT_6_R    ;12 "4096"

; -----------------------------------------------------------------------------
; ValueToSpriteTile — leading-digit tile for the slide animation.
; -----------------------------------------------------------------------------
ValueToSpriteTile::
    db TILE_CELL_FILL   ; val 0  — unused
    db TILE_DIGIT_2     ; val 1
    db TILE_DIGIT_4     ; val 2
    db TILE_DIGIT_8     ; val 3
    db TILE_DIGIT_1     ; val 4 ("16")
    db TILE_DIGIT_3     ; val 5
    db TILE_DIGIT_6     ; val 6
    db TILE_DIGIT_1     ; val 7
    db TILE_DIGIT_2     ; val 8
    db TILE_DIGIT_5     ; val 9
    db TILE_DIGIT_1     ; val 10
    db TILE_DIGIT_2     ; val 11
    db TILE_DIGIT_4     ; val 12

; -----------------------------------------------------------------------------
; RenderInit:: — called via farcall from bank 0 at startup.
; -----------------------------------------------------------------------------
RenderInit::
    ; --- Copy font + tile data from bank 2 into VRAM ---
    ld a, 2
    ld hl, FONT_DATA_START
    ld de, $8000
    ld bc, $01E0
    call CopyFromBank

    ld a, 2
    ld hl, TILE_DATA_START
    ld de, $8000 + $01E0
    ld bc, TILE_DATA_SIZE
    call CopyFromBank

    ; --- Set BG palettes ---
    ld a, BCPSF_AUTOINC | 0
    ldh [rBCPS], a
    ld hl, BgPaletteData
    ld b, 64
.bg_pal_loop:
    ld a, [hl+]
    ldh [rBCPD], a
    dec b
    jr nz, .bg_pal_loop

    ; --- Set OBJ palettes (same colours so sprite tints match cells) ---
    ld a, OCPSF_AUTOINC | 0
    ldh [rOCPS], a
    ld hl, BgPaletteData
    ld b, 64
.obj_pal_loop:
    ld a, [hl+]
    ldh [rOCPD], a
    dec b
    jr nz, .obj_pal_loop

    ; --- Clear BG tilemap with TILE_FONT_SPACE ---
    ld hl, _SCRN0
    ld bc, $0400
    ld a, TILE_FONT_SPACE
.clear_map_loop:
    ld [hl+], a
    dec bc
    ld a, b
    or c
    ld a, TILE_FONT_SPACE
    jr nz, .clear_map_loop

    ; --- Clear BG attribute map (VRAM bank 1) ---
    ; HUD rows 0-2 use palette 0 (white plate). Rows 3-17 use palette 1
    ; (dark frame) so the gaps between cells and any cleared area mid-
    ; slide show the same dark slate background — cells then "pop".
    ld a, 1
    ldh [rVBK], a
    ; HUD rows 0-2 -> palette 0.
    ld hl, _SCRN0
    ld bc, 3 * 32
    xor a
    call FillVram
    ; Board rows 3-17 -> palette 1.
    ld hl, _SCRN0 + 3 * 32
    ld bc, 15 * 32
    ld a, 1
    call FillVram
    xor a
    ldh [rVBK], a

    ; --- Paint HUD divider rule across row 2 ---
    ld hl, _SCRN0 + 2 * 32
    ld b, 20
    ld a, TILE_HUD_RULE
.rule_loop:
    ld [hl+], a
    dec b
    jr nz, .rule_loop

    ; --- Initialise shadow buffers + score-digit cache, then paint the
    ;     board and HUD ----------------------------------------------------
    call InitBoardBuffers
    call BuildEmptyGridBuffers
    call RecomputeScoreCache
    call DrawBoardFull
    call DrawHud

    ; --- Enable LCD: BG + OBJ, no window ---
    ld a, LCDCF_ON | LCDCF_BG8000 | LCDCF_BGON | LCDCF_OBJ8 | LCDCF_OBJON
    ldh [rLCDC], a
    ret

; -----------------------------------------------------------------------------
; DrawHud:: — write the BEST and SCORE labels + 7 cached digit tiles to BG
; rows 0-1.
;
; The digits come from a precomputed cache (wScoreDigits / wBestScoreDigits)
; that's filled by RecomputeScoreCache during the slide phase. ScoreToDigits
; is too slow (~4 K T-cycles for value 2048) to fit inside the commit's
; VBlank window — running it inside the commit drops late writes into PPU
; mode 3 on real hardware and corrupts the digits. The cache makes DrawHud
; a constant ~300 T regardless of score magnitude.
;
; Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
DrawHud::
    ld hl, _SCRN0
    ld de, .str_best
    call DrawString               ; HL now at row 0 col 7
    ld de, wBestScoreDigits
    call CopyDigitsToTilemap
    ld hl, _SCRN0 + 14
    ld de, .str_pad
    call DrawString

    ld hl, _SCRN0 + 32
    ld de, .str_score
    call DrawString               ; HL at row 1 col 7
    ld de, wScoreDigits
    call CopyDigitsToTilemap
    ld hl, _SCRN0 + 32 + 14
    ld de, .str_pad
    call DrawString
    ret

.str_best:  db "BEST   ", STR_END
.str_score: db "SCORE  ", STR_END
.str_pad:   db "      ",  STR_END

; -----------------------------------------------------------------------------
; CopyDigitsToTilemap:: — copy 7 cached digit tiles to the tilemap.
;   HL = destination tilemap pointer (advances by 7)
;   DE = source cache pointer       (advances by 7)
;   Clobbers AF, B, DE, HL.
; -----------------------------------------------------------------------------
CopyDigitsToTilemap::
    ld b, 7
.loop:
    ld a, [de]
    ld [hl+], a
    inc de
    dec b
    jr nz, .loop
    ret

; -----------------------------------------------------------------------------
; RecomputeScoreCache:: — fill wScoreDigits / wBestScoreDigits from the
; current wScore / wBestScore via the 32-bit ScoreToDigits divider. Writes
; only to WRAM (cache buffers), so this can run in any frame without VRAM
; lockout concerns. Cost ≈ 2-4 K T per score — run during slide frames.
; Clobbers AF, BC, DE, HL, hScratch+0..+6.
; -----------------------------------------------------------------------------
RecomputeScoreCache::
    ld hl, wBestScore
    ld bc, wBestScoreDigits
    call ScoreToDigits
    ld hl, wScore
    ld bc, wScoreDigits
    call ScoreToDigits
    ret

; -----------------------------------------------------------------------------
; CellBufOffset — buffer offset for each cell index 0..15.
;   cell (r,c) base = r * (CELL_H + CELL_GAP) * 32 + c * (CELL_W + CELL_GAP)
;                   = r * 128 + c * 5
; Words = pairs of (lo, hi). Use cell-index * 2 as table offset.
; -----------------------------------------------------------------------------
; Buffer offsets relative to wBoardBgBuf base (which mirrors _SCRN0 + 3*32):
;   cell (r,c) = (BOARD_FIRST_ROW + r*3 - 3) * 32 + (BOARD_FIRST_COL + c*4)
;             = (1 + r*3) * 32 + (2 + c*4)
CellBufOffset::
    dw 34, 38, 42, 46          ; row 0 (board rows 4-6)
    dw 130, 134, 138, 142      ; row 1 (board rows 7-9)
    dw 226, 230, 234, 238      ; row 2 (board rows 10-12)
    dw 322, 326, 330, 334      ; row 3 (board rows 13-15)

; -----------------------------------------------------------------------------
; InitBoardBuffers:: — fill both shadow buffers with their "frame" defaults.
;   BG: TILE_FONT_SPACE everywhere; cells overwrite their own 12 slots.
;   Attr: palette 1 (dark slate frame) everywhere.
;   Call once at boot; subsequent BuildBoardBuffers only updates cell slots.
;   Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
InitBoardBuffers::
    ld hl, wBoardBgBuf
    ld bc, 480
    ld a, TILE_FONT_SPACE
    call FillVram
    ld hl, wBoardAttrBuf
    ld bc, 480
    ld a, 1
    call FillVram
    ret

; -----------------------------------------------------------------------------
; BuildEmptyGridBuffers:: — populate the empty-grid HDMA source buffers once at
;   boot. BG = the static grid template; Attr = palette 1 (empty-cell tint)
;   everywhere. ClearBoardBg HDMAs these during every slide.
;   Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
BuildEmptyGridBuffers::
    ld hl, EmptyGridBgTemplate
    ld de, wEmptyBgBuf
    ld bc, 480
.copy:
    ld a, [hl+]
    ld [de], a
    inc de
    dec bc
    ld a, b
    or c
    jr nz, .copy
    ld hl, wEmptyAttrBuf
    ld bc, 480
    ld a, 1
    call FillVram
    ret

; -----------------------------------------------------------------------------
; BuildBoardBuffers:: — fill the shadow buffers for all 16 cells.
;
; For each cell we write 12 BG bytes and 12 attribute bytes (3 buffer rows
; × 4 cols).  No VRAM access -> no PPU mode constraints -> can run any frame.
;
; Per-cell HRAM scratch usage:
;   +0  buffer offset low byte
;   +1  buffer offset high byte
;   +2  palette index for cell
;   +3  ValueToDigits ptr low
;   +4  ValueToDigits ptr high
;   +5  current cell index (also kept in C across the loop)
;
; Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
BuildBoardBuffers::
    ld c, 0                    ; cell index
.cell_loop:
    ; Save cell index in hScratch+5 for use after BC is clobbered.
    ld a, c
    ldh [hScratch+5], a

    ; --- Buffer offset = CellBufOffset[c*2] ---
    sla c                      ; *2 (word index)
    ld b, 0
    ld hl, CellBufOffset
    add hl, bc
    ld a, [hl+]
    ldh [hScratch+0], a
    ld a, [hl]
    ldh [hScratch+1], a

    ; --- Lookup value + palette ---
    ldh a, [hScratch+5]
    ld c, a
    ld b, 0
    ld hl, wBoard
    add hl, bc
    ld a, [hl]
    cp 13
    jr c, .v_ok
    ld a, 12
.v_ok:
    ld c, a                    ; C = value
    ld b, 0
    ld hl, ValueToPalette
    add hl, bc
    ld a, [hl]
    ldh [hScratch+2], a

    ; --- Digits pointer = ValueToDigits + value*4 ---
    ld a, c
    add a
    add a                      ; A = value*4
    ld c, a
    ld b, 0
    ld hl, ValueToDigits
    add hl, bc
    ld a, l
    ldh [hScratch+3], a
    ld a, h
    ldh [hScratch+4], a

    ; ===== BG buffer writes =====
    ; HL = wBoardBgBuf + offset
    ldh a, [hScratch+0]
    ld e, a
    ldh a, [hScratch+1]
    ld d, a
    ld hl, wBoardBgBuf
    add hl, de
    ; Row 0: 3 × TOP_EDGE + 1 × TOP_EDGE_R (right border on the rightmost).
    ld a, TILE_CELL_TOP_EDGE
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld a, TILE_CELL_TOP_EDGE_R
    ld [hl+], a
    ; Advance HL by (32 - 4) = 28 to reach row 1.
    ld a, l
    add 28
    ld l, a
    jr nc, .bg_r1
    inc h
.bg_r1:
    ; Row 1: 4 digit tiles from ValueToDigits.
    ldh a, [hScratch+3]
    ld e, a
    ldh a, [hScratch+4]
    ld d, a
    ld a, [de]
    ld [hl+], a
    inc de
    ld a, [de]
    ld [hl+], a
    inc de
    ld a, [de]
    ld [hl+], a
    inc de
    ld a, [de]
    ld [hl+], a
    ; Advance HL +28 for row 2.
    ld a, l
    add 28
    ld l, a
    jr nc, .bg_r2
    inc h
.bg_r2:
    ; Row 2: 3 × BOT_EDGE + 1 × BOT_EDGE_R.
    ld a, TILE_CELL_BOT_EDGE
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld a, TILE_CELL_BOT_EDGE_R
    ld [hl+], a

    ; ===== Attr buffer writes =====
    ; HL = wBoardAttrBuf + offset
    ldh a, [hScratch+0]
    ld e, a
    ldh a, [hScratch+1]
    ld d, a
    ld hl, wBoardAttrBuf
    add hl, de
    ldh a, [hScratch+2]
    ld b, a                    ; B = palette byte
    ; Row 0
    ld a, b
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld a, l
    add 28
    ld l, a
    jr nc, .at_r1
    inc h
.at_r1:
    ld a, b
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld a, l
    add 28
    ld l, a
    jr nc, .at_r2
    inc h
.at_r2:
    ld a, b
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a

    ; --- Next cell ---
    ldh a, [hScratch+5]
    inc a
    ld c, a
    cp 16
    jp nz, .cell_loop
    ret

; -----------------------------------------------------------------------------
; CommitBoardHdma:: — HDMA both shadow buffers to VRAM.
;   bank 0: wBoardBgBuf  -> _SCRN0 + 3*32  (480 bytes = 30 blocks)
;   bank 1: wBoardAttrBuf -> _SCRN0 + 3*32 (480 bytes = 30 blocks)
;   CPU is halted during each transfer (~250 T per HDMA in 2x mode).
;   Clobbers AF.
; -----------------------------------------------------------------------------
CommitBoardHdma::
    ; --- BG tiles (bank 0) ---
    ld a, HIGH(wBoardBgBuf)
    ldh [rHDMA1], a
    ld a, LOW(wBoardBgBuf)
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
    ld a, HIGH(wBoardAttrBuf)
    ldh [rHDMA1], a
    ld a, LOW(wBoardAttrBuf)
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

DrawBoardFull::
    xor a
    ld b, 16
    ; fallthrough to DrawBoardCells

; -----------------------------------------------------------------------------
; DrawBoardCells:: — paint <B> cells starting at index <A> (0..15).
;
; Splits the heavy commit across multiple VBlanks: the animation runs the
; top half on frame 8 and the bottom half on frame 9, so we never need to
; toggle the LCD off (which would flash the entire screen between every
; move). Each cell writes 12 tilemap bytes (bank 0) and 12 attribute bytes
; (bank 1). The bank-1 writes share HL with the bank-0 writes because the
; tilemap and attribute map live at the same address in their respective
; banks; we just flip rVBK around the writes.
;
; Inputs:
;   A = starting cell index (0..15)
;   B = number of cells to draw (1..16)
; Clobbers AF, BC, DE, HL, hScratch+0..4.
; -----------------------------------------------------------------------------
DrawBoardCells::
    ld c, a                    ; C = current cell index
    ld a, b
    add c                      ; A = end (inclusive upper bound, exclusive)
    ldh [hScratch+5], a        ; save end
.cell_loop:
    ; --- Fetch tilemap address from CellAddr table ---
    ld a, c
    add a                      ; *2 (word offset)
    ld e, a
    ld d, 0
    ld hl, CellAddr
    add hl, de
    ld a, [hl+]                ; lo
    ld e, a
    ld a, [hl]                 ; hi
    ld d, a
    ld a, e
    ldh [hScratch+0], a
    ld a, d
    ldh [hScratch+1], a

    ; --- Lookup cell value and derive palette byte ---
    ld b, 0
    ld hl, wBoard
    add hl, bc
    ld a, [hl]                 ; A = value (0..12)
    cp 13
    jr c, .v_ok
    ld a, 12
.v_ok:
    ld e, a                    ; save value
    ; Palette lookup.
    ld d, 0
    ld hl, ValueToPalette
    add hl, de
    ld a, [hl]
    ldh [hScratch+2], a        ; palette byte
    ; Digits pointer = ValueToDigits + value*4.
    ld a, e
    add a
    add a                      ; *4
    ld e, a
    ld d, 0
    ld hl, ValueToDigits
    add hl, de
    ld a, l
    ldh [hScratch+3], a
    ld a, h
    ldh [hScratch+4], a

    ; ===== Row 0: top edge (BG tiles) — 3 × TOP_EDGE + 1 × TOP_EDGE_R =====
    ldh a, [hScratch+0]
    ld l, a
    ldh a, [hScratch+1]
    ld h, a
    ld a, TILE_CELL_TOP_EDGE
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld a, TILE_CELL_TOP_EDGE_R
    ld [hl+], a

    ; ===== Row 0: top edge (attribute bank 1) =====
    ld a, 1
    ldh [rVBK], a
    ldh a, [hScratch+0]
    ld l, a
    ldh a, [hScratch+1]
    ld h, a
    ldh a, [hScratch+2]
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    xor a
    ldh [rVBK], a

    ; ===== Row 1: digit tiles (BG) =====
    ldh a, [hScratch+0]
    add 32
    ld l, a
    ldh a, [hScratch+1]
    adc 0
    ld h, a
    ; reload digits pointer
    ldh a, [hScratch+3]
    ld e, a
    ldh a, [hScratch+4]
    ld d, a
    ld a, [de]
    ld [hl+], a
    inc de
    ld a, [de]
    ld [hl+], a
    inc de
    ld a, [de]
    ld [hl+], a
    inc de
    ld a, [de]
    ld [hl+], a

    ; ===== Row 1: attributes =====
    ld a, 1
    ldh [rVBK], a
    ldh a, [hScratch+0]
    add 32
    ld l, a
    ldh a, [hScratch+1]
    adc 0
    ld h, a
    ldh a, [hScratch+2]
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    xor a
    ldh [rVBK], a

    ; ===== Row 2: bottom edge (BG) =====
    ldh a, [hScratch+0]
    add 64
    ld l, a
    ldh a, [hScratch+1]
    adc 0
    ld h, a
    ld a, TILE_CELL_BOT_EDGE
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld a, TILE_CELL_BOT_EDGE_R
    ld [hl+], a

    ; ===== Row 2: attributes =====
    ld a, 1
    ldh [rVBK], a
    ldh a, [hScratch+0]
    add 64
    ld l, a
    ldh a, [hScratch+1]
    adc 0
    ld h, a
    ldh a, [hScratch+2]
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    xor a
    ldh [rVBK], a

    ; --- Next cell ---
    inc c
    ldh a, [hScratch+5]
    cp c
    jp nz, .cell_loop
    ret

; -----------------------------------------------------------------------------
; DrawBoardAttrs:: — back-compat stub. The combined DrawBoardFull above
;   already writes attribute bytes, so DrawBoardAttrs is a no-op now. We
;   keep the symbol so anim.asm / save.asm can call it without changes.
; -----------------------------------------------------------------------------
DrawBoardAttrs::
    ret
