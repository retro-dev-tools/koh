SECTION "Font", ROMX, BANK[2]

FONT_DATA_START::

; 30 tiles × 16 bytes = 480 bytes ($1E0).
;
; Tile bit-plane convention used by this sample:
;   bp0 byte (first ds byte)  : foreground mask — set bits = colour 3
;   bp1 byte (second ds byte) : background mask — $FF means the entire tile
;                               sits on colour 2 (cell tint); $00 means
;                               colour 0 (outside / HUD white).
;
; Result mapping per pixel:
;   bp0=0, bp1=0 -> colour 0   (outside / HUD background)
;   bp0=0, bp1=1 -> colour 2   (cell tint)
;   bp0=1, bp1=1 -> colour 3   (foreground / text)
;   bp0=1, bp1=0 -> colour 1   (unused)
;
; Font glyphs use bp1 = $FF so they read text on cell tint. T_FONT_SPACE keeps
; bp1 = $00 so it renders as pure outside-colour wherever it appears (HUD
; padding, outside-the-board gaps).

T_FONT_SPACE::
    REPT 8
    db $00, $00
    ENDR

T_FONT_A::
    db %00011000, $FF
    db %00100100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01111110, $FF
    db %01000010, $FF
    db %01000010, $FF
    db $00, $FF

T_FONT_B::
    db %01111100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01111100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01111100, $FF
    db $00, $FF

T_FONT_C::
    db %00111110, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %00111110, $FF
    db $00, $FF

T_FONT_D::
    db %01111000, $FF
    db %01000100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000100, $FF
    db %01111000, $FF
    db $00, $FF

T_FONT_E::
    db %01111110, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01111100, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01111110, $FF
    db $00, $FF

T_FONT_F::
    db %01111110, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01111100, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db $00, $FF

T_FONT_G::
    db %00111110, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01001110, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %00111110, $FF
    db $00, $FF

T_FONT_H::
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01111110, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db $00, $FF

T_FONT_I::
    db %00111110, $FF
    db %00001000, $FF
    db %00001000, $FF
    db %00001000, $FF
    db %00001000, $FF
    db %00001000, $FF
    db %00111110, $FF
    db $00, $FF

T_FONT_J::
    db %00011110, $FF
    db %00000100, $FF
    db %00000100, $FF
    db %00000100, $FF
    db %01000100, $FF
    db %01000100, $FF
    db %00111000, $FF
    db $00, $FF

T_FONT_K::
    db %01000100, $FF
    db %01001000, $FF
    db %01010000, $FF
    db %01100000, $FF
    db %01010000, $FF
    db %01001000, $FF
    db %01000100, $FF
    db $00, $FF

T_FONT_L::
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01111110, $FF
    db $00, $FF

T_FONT_M::
    db %01000010, $FF
    db %01100110, $FF
    db %01011010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db $00, $FF

T_FONT_N::
    db %01000010, $FF
    db %01100010, $FF
    db %01010010, $FF
    db %01001010, $FF
    db %01000110, $FF
    db %01000010, $FF
    db %01000010, $FF
    db $00, $FF

T_FONT_O::
    db %00111100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %00111100, $FF
    db $00, $FF

T_FONT_P::
    db %01111100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01111100, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %01000000, $FF
    db $00, $FF

T_FONT_Q::
    db %00111100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01010010, $FF
    db %01001010, $FF
    db %00110110, $FF
    db $00, $FF

T_FONT_R::
    db %01111100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01111100, $FF
    db %01010000, $FF
    db %01001000, $FF
    db %01000100, $FF
    db $00, $FF

T_FONT_S::
    db %00111110, $FF
    db %01000000, $FF
    db %01000000, $FF
    db %00111100, $FF
    db %00000010, $FF
    db %00000010, $FF
    db %01111100, $FF
    db $00, $FF

T_FONT_T::
    db %01111110, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db $00, $FF

T_FONT_U::
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %00111100, $FF
    db $00, $FF

T_FONT_V::
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %00100100, $FF
    db %00100100, $FF
    db %00011000, $FF
    db %00011000, $FF
    db $00, $FF

T_FONT_W::
    db %01000010, $FF
    db %01000010, $FF
    db %01000010, $FF
    db %01011010, $FF
    db %01100110, $FF
    db %01000010, $FF
    db %01000010, $FF
    db $00, $FF

T_FONT_X::
    db %01000010, $FF
    db %00100100, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00100100, $FF
    db %01000010, $FF
    db %01000010, $FF
    db $00, $FF

T_FONT_Y::
    db %01000010, $FF
    db %01000010, $FF
    db %00100100, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db %00011000, $FF
    db $00, $FF

T_FONT_Z::
    db %01111110, $FF
    db %00000100, $FF
    db %00001000, $FF
    db %00011000, $FF
    db %00100000, $FF
    db %01000000, $FF
    db %01111110, $FF
    db $00, $FF

T_FONT_EXCL::
    db %00001000, $FF
    db %00001000, $FF
    db %00001000, $FF
    db %00001000, $FF
    db %00001000, $FF
    db $00, $FF
    db %00001000, $FF
    db $00, $FF

T_FONT_PERIOD::
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db $00, $FF
    db %00001000, $FF
    db $00, $FF

T_FONT_COLON::
    db $00, $FF
    db %00001000, $FF
    db %00001000, $FF
    db $00, $FF
    db %00001000, $FF
    db %00001000, $FF
    db $00, $FF
    db $00, $FF

FONT_DATA_END::
