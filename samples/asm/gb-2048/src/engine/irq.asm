SECTION "IRQ Handlers", ROM0

; VBlank IRQ:
;   1. OAM DMA from wOAMBuffer.
;   2. Drain VBlank queue.
;   3. Set wVBlankFlag so the main loop knows a frame elapsed.
;   4. Increment hFrameCounter.
;   5. Re-enable interrupts and return.
VBlankIRQ::
    push af
    push bc
    push de
    push hl

    ; OAM DMA — call HRAM trampoline.
    call hOAMDMA

    ; Drain VBlank queue.
    call QueueDrain

    ; Set frame flag.
    ld a, 1
    ld [wVBlankFlag], a
    ldh a, [hFrameCounter]
    inc a
    ldh [hFrameCounter], a

    pop hl
    pop de
    pop bc
    pop af
    reti

; STAT IRQ — used only on title screen for palette band split. Other states
; leave STAT IRQ disabled. T24 fills in the body.
StatIRQ::
    push af
    push hl
    pop hl
    pop af
    reti
