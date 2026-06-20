SECTION "Tiles", ROMX, BANK[2]

TILE_DATA_START::

; Tile bit-plane convention — see font.asm for full discussion.
; All tiles in this file use bp1 = $FF so they render with colour 2 (cell
; tint) as the background; bp0 selects colour 3 (text) for the pixels that
; should appear in the foreground.

; T_CELL_FILL — solid cell tint (colour 2 everywhere). Used for cell body
; rows (top/bottom of each 4x3 cell) and as padding around digits in the
; centre row.
T_CELL_FILL::
    REPT 8
    db $00, $FF
    ENDR

; T_CELL_TOP_EDGE — top row in colour 1 (highlight bevel), rest in colour 2
; (cell tint). Gives every cell a 1-px lighter rim at the top so it looks
; lit-from-above rather than a flat block.
T_CELL_TOP_EDGE::
    db $FF, $00
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF

; T_CELL_BOT_EDGE — fill, then a 1-px board-tone (colour 0) separator on the
; bottom row so vertically-touching cells get a consistent tan gap regardless
; of tile value. (Colour 3 here read as white on the bright 8/16/32+ tiles,
; making them look clipped.)
T_CELL_BOT_EDGE::
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $00

; T_CELL_TL — top-left corner: top row + left column in colour 3.
T_CELL_TL::
    db $FF, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF

; T_CELL_TR — top-right corner.
T_CELL_TR::
    db $FF, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF

; T_CELL_BL — bottom-left.
T_CELL_BL::
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $80, $FF
    db $FF, $FF

; T_CELL_BR — bottom-right.
T_CELL_BR::
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $01, $FF
    db $FF, $FF

; T_CELL_LEFT_EDGE — left column in colour 3, used by the digit row of cells
; in column 0..3 (always — every cell has a left edge to separate it from
; its neighbour or outside area).
T_CELL_LEFT_EDGE::
    REPT 8
    db $80, $FF
    ENDR

; T_CELL_RIGHT_EDGE — right column in colour 0 (board-tone separator) so
; horizontally-touching cells get a consistent tan gap on every tile value.
T_CELL_RIGHT_EDGE::
    REPT 8
    db $00, $FE
    ENDR

; T_HUD_RULE — single horizontal rule pixel row, drawn in colour 3, used as
; the divider between the HUD (rows 0-1) and the board (rows 3+).
T_HUD_RULE::
    db $00, $00
    db $00, $00
    db $00, $00
    db $FF, $FF
    db $00, $00
    db $00, $00
    db $00, $00
    db $00, $00

; Digit tiles 0-9. Glyphs are 5x7 in an 8x8 tile, centred horizontally
; (cols 1-5) and bottom-aligned with a 1-pixel base gap. bp1 = $FF
; throughout so the non-digit pixels sit on colour 2 (cell tint).

T_DIGIT_0::
    db $00, $FF
    db %00111100, $FF
    db %01100110, $FF
    db %01100110, $FF
    db %01100110, $FF
    db %01100110, $FF
    db %00111100, $FF
    db $00, $FF

T_DIGIT_1::
    db $00, $FF
    db %00011000, $FF
    db %00111000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %01111110, $FF
    db $00, $FF

T_DIGIT_2::
    db $00, $FF
    db %00111100, $FF
    db %01100110, $FF
    db %00000110, $FF
    db %00111100, $FF
    db %01100000, $FF
    db %01111110, $FF
    db $00, $FF

T_DIGIT_3::
    db $00, $FF
    db %00111100, $FF
    db %01100110, $FF
    db %00001100, $FF
    db %00000110, $FF
    db %01100110, $FF
    db %00111100, $FF
    db $00, $FF

T_DIGIT_4::
    db $00, $FF
    db %00001100, $FF
    db %00011100, $FF
    db %00111100, $FF
    db %01101100, $FF
    db %01111110, $FF
    db %00001100, $FF
    db $00, $FF

T_DIGIT_5::
    db $00, $FF
    db %01111110, $FF
    db %01100000, $FF
    db %01111100, $FF
    db %00000110, $FF
    db %01100110, $FF
    db %00111100, $FF
    db $00, $FF

T_DIGIT_6::
    db $00, $FF
    db %00111100, $FF
    db %01100000, $FF
    db %01111100, $FF
    db %01100110, $FF
    db %01100110, $FF
    db %00111100, $FF
    db $00, $FF

T_DIGIT_7::
    db $00, $FF
    db %01111110, $FF
    db %00000110, $FF
    db %00001100, $FF
    db %00011000, $FF
    db %00110000, $FF
    db %00110000, $FF
    db $00, $FF

T_DIGIT_8::
    db $00, $FF
    db %00111100, $FF
    db %01100110, $FF
    db %00111100, $FF
    db %01100110, $FF
    db %01100110, $FF
    db %00111100, $FF
    db $00, $FF

T_DIGIT_9::
    db $00, $FF
    db %00111100, $FF
    db %01100110, $FF
    db %01100110, $FF
    db %00111110, $FF
    db %00000110, $FF
    db %00111100, $FF
    db $00, $FF

; -----------------------------------------------------------------------------
; "_R" variants — same as the base tile but with pixel column 7 forced to
; colour 3 (a 1-px right border). Used for the rightmost tile of every cell
; so that horizontally-touching cells get a 1-px separator without needing a
; full gap tile between them. Defined after the regular digits so the
; canonical TILE_DIGIT_X ids stay 40..49.
; -----------------------------------------------------------------------------

; T_CELL_TOP_EDGE_R — top-row highlight (colour 1) + 1-px board-tone (colour 0)
; right separator.
T_CELL_TOP_EDGE_R::
    db $FE, $00
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE

; T_CELL_BOT_EDGE_R — fill + 1-px board-tone (colour 0) right separator, plus a
; board-tone bottom row. Bottom-right corner of every cell.
T_CELL_BOT_EDGE_R::
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $FE
    db $00, $00

; Right-border digit variants — identical glyph to the base digit, but bp1 has
; column 7 cleared ($FE) so the rightmost pixel renders as colour 0 (board
; tone): a consistent tan separator instead of the colour-3 strip that read as
; white on the bright 1024/2048/4096 palettes.

T_DIGIT_0_R::
    db $00, $FE
    db %00111100, $FE
    db %01100110, $FE
    db %01100110, $FE
    db %01100110, $FE
    db %01100110, $FE
    db %00111100, $FE
    db $00, $FE

T_DIGIT_1_R::
    db $00, $FE
    db %00011000, $FE
    db %00111000, $FE
    db %00011000, $FE
    db %00011000, $FE
    db %00011000, $FE
    db %01111110, $FE
    db $00, $FE

T_DIGIT_2_R::
    db $00, $FE
    db %00111100, $FE
    db %01100110, $FE
    db %00000110, $FE
    db %00111100, $FE
    db %01100000, $FE
    db %01111110, $FE
    db $00, $FE

T_DIGIT_3_R::
    db $00, $FE
    db %00111100, $FE
    db %01100110, $FE
    db %00001100, $FE
    db %00000110, $FE
    db %01100110, $FE
    db %00111100, $FE
    db $00, $FE

T_DIGIT_4_R::
    db $00, $FE
    db %00001100, $FE
    db %00011100, $FE
    db %00111100, $FE
    db %01101100, $FE
    db %01111110, $FE
    db %00001100, $FE
    db $00, $FE

T_DIGIT_5_R::
    db $00, $FE
    db %01111110, $FE
    db %01100000, $FE
    db %01111100, $FE
    db %00000110, $FE
    db %01100110, $FE
    db %00111100, $FE
    db $00, $FE

T_DIGIT_6_R::
    db $00, $FE
    db %00111100, $FE
    db %01100000, $FE
    db %01111100, $FE
    db %01100110, $FE
    db %01100110, $FE
    db %00111100, $FE
    db $00, $FE

T_DIGIT_7_R::
    db $00, $FE
    db %01111110, $FE
    db %00000110, $FE
    db %00001100, $FE
    db %00011000, $FE
    db %00110000, $FE
    db %00110000, $FE
    db $00, $FE

T_DIGIT_8_R::
    db $00, $FE
    db %00111100, $FE
    db %01100110, $FE
    db %00111100, $FE
    db %01100110, $FE
    db %01100110, $FE
    db %00111100, $FE
    db $00, $FE

T_DIGIT_9_R::
    db $00, $FE
    db %00111100, $FE
    db %01100110, $FE
    db %01100110, $FE
    db %00111110, $FE
    db %00000110, $FE
    db %00111100, $FE
    db $00, $FE

TILE_DATA_END::
