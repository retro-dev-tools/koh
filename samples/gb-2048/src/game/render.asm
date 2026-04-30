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

    ; --- Set window position: WY=0, WX=7 (window at x=0, y=0) ---
    ld a, 0
    ldh [rWY], a
    ld a, 7                    ; WX=7 means window starts at pixel column 0
    ldh [rWX], a

    ; --- Draw HUD on window layer ---
    call DrawHud

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
; DrawHud:: — write HUD top row to window tilemap _SCRN1 ($9C00).
; Row 0 layout (32 columns):
;   "BEST " (5) + 7 digits best score + "  SCORE " (8) + 7 digits score
;   + 5 trailing spaces = 32 total.
; Calls ScoreToDigits (HL=source, BC=output buffer) for each score.
; Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
DrawHud::
    ; HL = start of window tilemap row 0.
    ld hl, _SCRN1

    ; Write "BEST " (5 tiles).
    ld a, TILE_FONT_B
    ld [hl+], a
    ld a, TILE_FONT_E
    ld [hl+], a
    ld a, TILE_FONT_S
    ld [hl+], a
    ld a, TILE_FONT_T
    ld [hl+], a
    ld a, TILE_FONT_SPACE
    ld [hl+], a

    ; Write 7 digits of wBestScore.
    ; BC = current HL (output pointer into tilemap).
    ld b, h
    ld c, l
    ; HL = source = wBestScore.
    ld hl, wBestScore
    call ScoreToDigits
    ; ScoreToDigits wrote 7 bytes starting at old BC.
    ; Reconstruct write pointer: BC was _SCRN1+5, now advance to _SCRN1+12.
    ld hl, _SCRN1 + 12

    ; Write "  SCORE " (8 tiles).
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld [hl+], a
    ld a, TILE_FONT_S
    ld [hl+], a
    ld a, TILE_FONT_C
    ld [hl+], a
    ld a, TILE_FONT_O
    ld [hl+], a
    ld a, TILE_FONT_R
    ld [hl+], a
    ld a, TILE_FONT_E
    ld [hl+], a
    ld a, TILE_FONT_SPACE
    ld [hl+], a

    ; Write 7 digits of wScore.
    ; BC = current HL (output pointer = _SCRN1+20).
    ld b, h
    ld c, l
    ; HL = source = wScore.
    ld hl, wScore
    call ScoreToDigits
    ; Now at _SCRN1+27. Write 5 trailing spaces.
    ld hl, _SCRN1 + 27
    ld a, TILE_FONT_SPACE
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a

    ret

; -----------------------------------------------------------------------------
; DrawBoardFull:: — write 16 cells to BG tilemap at row 4, cols 12..15.
;
; Each cell renders as a single 8×8 tile (one tile per cell) using a
; logarithmic single-character representation:
;   wBoard value 0  → TILE_FONT_SPACE  (empty)
;   wBoard value 1  → TILE_DIGIT_1     (represents 2)
;   wBoard value 2  → TILE_DIGIT_2     (represents 4)
;   ...
;   wBoard value 9  → TILE_DIGIT_9     (represents 512)
;   wBoard value 10 → TILE_FONT_A      (represents 1024)
;   wBoard value 11 → TILE_FONT_B      (represents 2048 — WIN tile)
;   wBoard value 12 → TILE_FONT_C      (represents 4096)
;
; The 4×4 board occupies tilemap rows 4..7, cols 12..15 (centred on the
; 20-column visible area). Each row of 4 cells is written in order; after
; every 4 cells (col 15) the pointer advances to the next tilemap row by
; adding 28 (32 − 4 = skip the remaining 28 columns to land on col 12 of
; the next row). Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
DrawBoardFull::
    ld hl, _SCRN0 + 4*32 + 12  ; row 4, col 12
    ld c, 0                    ; cell index 0..15
.next_cell:
    ; Read wBoard[c] → A.
    push bc
    ld b, 0                    ; BC = c (zero-extend)
    ld de, wBoard
    push hl
    ld h, d
    ld l, e
    add hl, bc                 ; HL = &wBoard[c]
    ld a, [hl]
    pop hl
    pop bc

    ; Convert wBoard value to tile id.
    or a
    jr z, .write_empty         ; 0 → space
    cp 10
    jr c, .write_digit         ; 1..9 → digit tile
    ; 10..12 → font letter A/B/C
    sub 10                     ; 0=A, 1=B, 2=C
    add TILE_FONT_A
    jr .write

.write_digit:
    ; A is 1..9; TILE_DIGIT_1 = TILE_DIGIT_0 + 1.
    add TILE_DIGIT_0           ; 1 → TILE_DIGIT_1, ..., 9 → TILE_DIGIT_9
    jr .write

.write_empty:
    ld a, TILE_FONT_SPACE

.write:
    ld [hl+], a

    ; After every 4 cells (col boundary), skip to next tilemap row.
    inc c
    ld a, c
    and $03
    jr nz, .next_cell_cont
    ; End of a row of 4 cells: hl currently points col 16.
    ; Advance by 28 to reach col 12 of the next tilemap row.
    ld de, 28
    add hl, de
.next_cell_cont:
    ld a, c
    cp 16
    jr nz, .next_cell
    ret
