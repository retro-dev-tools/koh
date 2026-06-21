; Shared tile-ID constants.
;
; Layout in VRAM ($8000+): font tiles first (30), then cell-decoration tiles
; (10), then digit tiles (10). Tile IDs are zero-based and contiguous so each
; group is computed from a base + offset.

; Font glyphs (T_FONT_SPACE @ $4000 through T_FONT_COLON @ $41D0).
TILE_FONT_SPACE  EQU 0
TILE_FONT_A      EQU 1
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
TILE_FONT_Z      EQU 26
TILE_FONT_EXCL   EQU 27
TILE_FONT_PERIOD EQU 28
TILE_FONT_COLON  EQU 29

; Cell-decoration tiles (T_CELL_FILL @ $41E0 onward).
TILE_CELL_FILL        EQU 30
TILE_CELL_TOP_EDGE    EQU 31
TILE_CELL_BOT_EDGE    EQU 32
TILE_CELL_TL          EQU 33
TILE_CELL_TR          EQU 34
TILE_CELL_BL          EQU 35
TILE_CELL_BR          EQU 36
TILE_CELL_LEFT_EDGE   EQU 37
TILE_CELL_RIGHT_EDGE  EQU 38
TILE_HUD_RULE         EQU 39

; Digit tiles (T_DIGIT_0..9). TILE_DIGIT_n = TILE_DIGIT_0 + n.
TILE_DIGIT_0     EQU 40
TILE_DIGIT_1     EQU 41
TILE_DIGIT_2     EQU 42
TILE_DIGIT_3     EQU 43
TILE_DIGIT_4     EQU 44
TILE_DIGIT_5     EQU 45
TILE_DIGIT_6     EQU 46
TILE_DIGIT_7     EQU 47
TILE_DIGIT_8     EQU 48
TILE_DIGIT_9     EQU 49

; Right-border ("_R") variants — same glyph as the base tile but with a
; 1-pixel right border drawn in colour 3. Used for the rightmost tile of
; each cell so adjacent cells get a thin visual separator. Defined after
; T_DIGIT_9 in tiles.asm so that TILE_DIGIT_X (40..49) stays canonical.
TILE_CELL_TOP_EDGE_R EQU 50
TILE_CELL_BOT_EDGE_R EQU 51
TILE_DIGIT_0_R       EQU 52
TILE_DIGIT_1_R       EQU 53
TILE_DIGIT_2_R       EQU 54
TILE_DIGIT_3_R       EQU 55
TILE_DIGIT_4_R       EQU 56
TILE_DIGIT_5_R       EQU 57
TILE_DIGIT_6_R       EQU 58
TILE_DIGIT_7_R       EQU 59
TILE_DIGIT_8_R       EQU 60
TILE_DIGIT_9_R       EQU 61

; TILE_CELL_RIGHT_EDGE (id 38) already encodes a fill-with-right-border so we
; reuse it as the right-border variant of TILE_CELL_FILL.
TILE_CELL_FILL_R     EQU TILE_CELL_RIGHT_EDGE

; Size of the tile data block (T_CELL_FILL onward — 32 tiles × 16 bytes).
TILE_DATA_SIZE   EQU 32 * 16

; -----------------------------------------------------------------------------
; Character map — lets us write strings as `db "KOH 2048", STR_END` and have
; each character emit the right tile ID. STR_END (= $FF) is the terminator
; recognised by DrawString.
; -----------------------------------------------------------------------------
STR_END EQU $FF

; Koh's CHARMAP directive expects number literals (not EQU identifiers) for
; the byte values, so we hand-roll the mappings to the TILE_* IDs above.
; Keep in sync if the tile-ID ordering changes.
CHARMAP " ", 0     ; TILE_FONT_SPACE
CHARMAP "A", 1
CHARMAP "B", 2
CHARMAP "C", 3
CHARMAP "D", 4
CHARMAP "E", 5
CHARMAP "F", 6
CHARMAP "G", 7
CHARMAP "H", 8
CHARMAP "I", 9
CHARMAP "J", 10
CHARMAP "K", 11
CHARMAP "L", 12
CHARMAP "M", 13
CHARMAP "N", 14
CHARMAP "O", 15
CHARMAP "P", 16
CHARMAP "Q", 17
CHARMAP "R", 18
CHARMAP "S", 19
CHARMAP "T", 20
CHARMAP "U", 21
CHARMAP "V", 22
CHARMAP "W", 23
CHARMAP "X", 24
CHARMAP "Y", 25
CHARMAP "Z", 26
CHARMAP "!", 27    ; TILE_FONT_EXCL
CHARMAP ".", 28    ; TILE_FONT_PERIOD
CHARMAP ":", 29    ; TILE_FONT_COLON
CHARMAP "0", 40    ; TILE_DIGIT_0
CHARMAP "1", 41
CHARMAP "2", 42
CHARMAP "3", 43
CHARMAP "4", 44
CHARMAP "5", 45
CHARMAP "6", 46
CHARMAP "7", 47
CHARMAP "8", 48
CHARMAP "9", 49
