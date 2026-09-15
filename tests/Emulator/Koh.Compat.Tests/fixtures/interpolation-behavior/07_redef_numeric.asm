; Test: REDEF numeric — sees latest value
SECTION "Test", ROM0
DEF x EQU 1
REDEF x EQU 2
PRINTLN "{d:x}"
