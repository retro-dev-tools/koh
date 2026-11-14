SECTION "Semantics", ROM0[$200]
FOR loop_value, 0, 8
    db loop_value * 2
ENDR
ASSERT loop_value == 8
FOR empty_value, 4, 4
    ASSERT 0
ENDR
ASSERT empty_value == 4
FOR backward_value, -1, -4, 1
    ASSERT 0
ENDR
ASSERT backward_value == -1

EmptyStart:
    db
    dw
    dl
EmptyEnd:
ASSERT EmptyEnd - EmptyStart == 7

PUSHO
OPT p$42
    ds 1
POPO
    ds 1

CHARMAP "de", $64, $65
    dw "de"
    dw "Hi"
DEF numerator EQU $c0000000
DEF denominator EQU $80000000
DEF quotient EQU numerator / denominator
DEF remainder EQU numerator % denominator
ASSERT denominator == -2147483648
ASSERT quotient == 0
ASSERT remainder == numerator
ASSERT quotient * denominator + remainder == numerator

LOAD "Execution", WRAM0[$c000]
ExecutionStart:
    db $a5, $5a
ENDL
ASSERT SIZEOF("Execution") == 2

SECTION "PlainRam", WRAM0
RamStart:
    db
RamSecond:
    db
ASSERT RamSecond - RamStart == 1
ASSERT SIZEOF("PlainRam") == 2
