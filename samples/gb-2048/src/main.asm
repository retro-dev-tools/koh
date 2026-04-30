; 2048 — Game Boy Color
;
; Boot stub — Phase 0 only. Real entry replaces this in Phase 1.

SECTION "Reset", ROM0[$0100]
EntryPoint:
    nop
    jp Boot

SECTION "Boot", ROM0
Boot:
.halt:
    halt
    jr .halt
