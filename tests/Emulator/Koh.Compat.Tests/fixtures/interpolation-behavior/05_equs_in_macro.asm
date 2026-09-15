; Test: REDEF EQUS then use in same macro body — timing
MACRO test_timing
    REDEF _X EQUS "hello"
    PRINTLN "{s:_X}"
ENDM

SECTION "Test", ROM0
    test_timing
    test_timing
