; render.asm — VRAM init, palette setup, initial board draw.
; Placed in ROMX bank 1 alongside all other game-logic routines.

SECTION "Render", ROMX, BANK[1]

; -----------------------------------------------------------------------------
; Tile-ID constants — VRAM $8000-based tile indices.
; Font section comes first (bank 2: $4000-$41DF), then Tile section ($41E0+).
; Computed as (symbol_address - FONT_DATA_START) / 16.
; -----------------------------------------------------------------------------

TILE_FONT_SPACE  EQU 0    ; T_FONT_SPACE  @ $4000
TILE_FONT_A      EQU 1    ; T_FONT_A      @ $4010
TILE_FONT_B      EQU 2
TILE_FONT_C      EQU 3
TILE_FONT_D      EQU 4
TILE_FONT_E      EQU 5
TILE_FONT_F      EQU 6
TILE_FONT_G      EQU 7
TILE_FONT_H      EQU 8
TILE_FONT_I      EQU 9
TILE_FONT_J      EQU 10
TILE_FONT_K      EQU 11
TILE_FONT_L      EQU 12
TILE_FONT_M      EQU 13
TILE_FONT_N      EQU 14
TILE_FONT_O      EQU 15
TILE_FONT_P      EQU 16
TILE_FONT_Q      EQU 17
TILE_FONT_R      EQU 18
TILE_FONT_S      EQU 19
TILE_FONT_T      EQU 20
TILE_FONT_U      EQU 21
TILE_FONT_V      EQU 22
TILE_FONT_W      EQU 23
TILE_FONT_X      EQU 24
TILE_FONT_Y      EQU 25
TILE_FONT_Z      EQU 26   ; T_FONT_Z      @ $41A0
TILE_FONT_EXCL   EQU 27   ; T_FONT_EXCLAIM@ $41B0
TILE_FONT_PERIOD EQU 28   ; T_FONT_PERIOD @ $41C0
TILE_FONT_COLON  EQU 29   ; T_FONT_COLON  @ $41D0
TILE_EMPTY       EQU 30   ; T_EMPTY       @ $41E0
TILE_CORNER_TL   EQU 31   ; T_CORNER_TL   @ $41F0
TILE_CORNER_TR   EQU 32   ; T_CORNER_TR   @ $4200
TILE_CORNER_BL   EQU 33   ; T_CORNER_BL   @ $4210
TILE_CORNER_BR   EQU 34   ; T_CORNER_BR   @ $4220
TILE_DIGIT_0     EQU 35   ; T_DIGIT_0     @ $4230
TILE_DIGIT_1     EQU 36
TILE_DIGIT_2     EQU 37
TILE_DIGIT_3     EQU 38
TILE_DIGIT_4     EQU 39
TILE_DIGIT_5     EQU 40
TILE_DIGIT_6     EQU 41
TILE_DIGIT_7     EQU 42
TILE_DIGIT_8     EQU 43
TILE_DIGIT_9     EQU 44   ; T_DIGIT_9     @ $42C0

; -----------------------------------------------------------------------------
; BG palette data — 8 GBC palettes × 4 colors × 2 bytes = 64 bytes.
; RGB555: bits 0-4=R, 5-9=G, 10-14=B.
; -----------------------------------------------------------------------------
BgPaletteData::
    ; Palette 0: blank background (white-ish, dark text).
    dw $7FFF, $4631, $2108, $0000
    ; Palette 1: value 2 — pale yellow.
    dw $7FFF, $7BFE, $5AB7, $2108
    ; Palette 2: value 4 — slightly darker yellow.
    dw $7FFF, $7BDB, $52F4, $2108
    ; Palette 3: value 8 — pale orange.
    dw $7FFF, $4FFF, $2BDF, $2108
    ; Palette 4: value 16 — orange.
    dw $7FFF, $4BFF, $237F, $2108
    ; Palette 5: value 32 — red-orange.
    dw $7FFF, $4BFF, $1B5F, $0000
    ; Palette 6: value 64 — red.
    dw $7FFF, $4F1F, $221F, $0000
    ; Palette 7: value 128+ — gold.
    dw $7FFF, $7BE0, $4B40, $1080

; -----------------------------------------------------------------------------
; RenderInit:: — called via farcall from bank 0 at startup.
; Assumes LCD is off (VRAM writes are safe).
; 1. Copies font + tile data from bank 2 into VRAM $8000+.
; 2. Writes BG palettes (BCPS/BCPD) and OBJ palettes (OCPS/OCPD).
; 3. Clears BG tilemap $9800..$9BFF with TILE_FONT_SPACE.
; 4. Calls DrawBoardFull to set the initial cell placeholders.
; 5. Sets LCDC to enable LCD with BG, OBJ, and window.
; -----------------------------------------------------------------------------
RenderInit::
    ; --- Switch to bank 2 to access tile data ---
    ld a, [wCurrentBank]
    push af
    ld a, 2
    ld [wCurrentBank], a
    ld [rROMB0], a

    ; Copy font data: FONT_DATA_START .. FONT_DATA_END -> VRAM $8000
    ld hl, FONT_DATA_START
    ld de, $8000
    ld bc, FONT_DATA_END - FONT_DATA_START
    call .copy_loop

    ; Copy tile data: TILE_DATA_START .. TILE_DATA_END -> VRAM $8000 + font size
    ld hl, TILE_DATA_START
    ld de, $8000 + (FONT_DATA_END - FONT_DATA_START)
    ld bc, TILE_DATA_END - TILE_DATA_START
    call .copy_loop

    ; Restore previous bank
    pop af
    ld [wCurrentBank], a
    ld [rROMB0], a

    ; --- Set BG palettes ---
    ld a, BCPSF_AUTOINC | 0
    ldh [rBCPS], a
    ld hl, BgPaletteData
    ld b, 64                    ; 8 palettes × 4 colors × 2 bytes
.bg_pal_loop:
    ld a, [hl+]
    ldh [rBCPD], a
    dec b
    jr nz, .bg_pal_loop

    ; --- Set OBJ palettes (same data) ---
    ld a, OCPSF_AUTOINC | 0
    ldh [rOCPS], a
    ld hl, BgPaletteData
    ld b, 64
.obj_pal_loop:
    ld a, [hl+]
    ldh [rOCPD], a
    dec b
    jr nz, .obj_pal_loop

    ; --- Clear BG tilemap $9800..$9BFF with TILE_FONT_SPACE ---
    ld hl, $9800
    ld bc, $0400                ; 1 KiB
    ld a, TILE_FONT_SPACE
.clear_map_loop:
    ld [hl+], a
    dec bc
    ld a, b
    or c
    ld a, TILE_FONT_SPACE
    jr nz, .clear_map_loop

    ; --- Draw initial board ---
    call DrawBoardFull

    ; --- Enable LCD ---
    ld a, LCDCF_ON | LCDCF_BG8000 | LCDCF_BGON | LCDCF_OBJ16 | LCDCF_OBJON | LCDCF_WINON | LCDCF_WIN9C00
    ldh [rLCDC], a

    ret

; Internal: byte-by-byte copy. HL = source, DE = dest, BC = count.
; Clobbers A, BC, DE, HL.
.copy_loop:
    ld a, b
    or c
    ret z
    ld a, [hl+]
    ld [de], a
    inc de
    dec bc
    jr .copy_loop

; -----------------------------------------------------------------------------
; DrawBoardFull:: — write 16 cells (2x2 tiles each) to BG tilemap.
; Board occupies rows 4..11, cols 6..13 of the 32-col tilemap.
; Each cell is 2 columns × 2 rows of tiles = 4 tiles.
; -----------------------------------------------------------------------------
DrawBoardFull::
    ; 4 rows of cells, each cell row = 2 tilemap rows
    ld b, 4                     ; cell rows (0..3)
    ld c, 4                     ; cell row index
.row_loop:
    ld a, c                     ; cell_row (0-based)
    ; Each cell row uses 2 tilemap rows. Base tilemap row = 4 + cell_row*2.
    ; Row offset in tilemap = row * 32. We'll compute HL for top and bottom.

    ; Compute top tilemap row address for this cell row.
    ; top_row = 4 + cell_row_idx * 2, where cell_row_idx = 4 - b.
    push bc
    ld a, 4
    sub b                       ; a = cell_row_idx (0..3)
    add a, a                    ; a = cell_row_idx * 2
    add a, 4                    ; a = top tilemap row index
    ; HL = $9800 + row * 32 + col_start(6)
    ; row * 32 = a << 5
    ld h, 0
    ld l, a
    add hl, hl
    add hl, hl
    add hl, hl
    add hl, hl
    add hl, hl                  ; HL = row * 32
    ld de, $9800 + 6
    add hl, de                  ; HL = tilemap addr of (row, col=6)

    ; Write 4 cells across, 2 tiles wide each = 8 tiles per tilemap row.
    ; Top tilemap row of this cell row:
    ld d, h
    ld e, l                     ; DE = top row base addr
    ; Write top row (8 tiles)
    ld a, TILE_FONT_SPACE
    ld [de], a
    inc de
    ld [de], a
    inc de
    ld [de], a
    inc de
    ld [de], a
    inc de
    ld [de], a
    inc de
    ld [de], a
    inc de
    ld [de], a
    inc de
    ld [de], a

    ; Advance HL to next tilemap row (add 32 bytes)
    ld de, 32
    add hl, de                  ; HL = bottom row of this cell row (same cols)
    ; Write bottom row (8 tiles)
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a

    pop bc
    dec b
    jr nz, .row_loop

    ret
