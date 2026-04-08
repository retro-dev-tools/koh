; Test: nested interpolation — inner resolves first
SECTION "Test", ROM0
DEF meaning EQUS "answer"
DEF answer EQU 42
PRINTLN "{d:{meaning}}"
