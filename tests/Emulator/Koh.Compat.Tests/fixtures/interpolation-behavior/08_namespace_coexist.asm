; Test: EQU constant and macro with same name coexist
SECTION "Test", ROM0
DEF TEXT_LINEBREAK EQU $8e

MACRO text_linebreak
    db TEXT_LINEBREAK
ENDM

    text_linebreak
    PRINTLN "{d:TEXT_LINEBREAK}"
