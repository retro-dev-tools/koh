SECTION "Input", ROM0

; ReadInput — sample joypad and store into wInput:
;   wInput+0 = current  (1 = pressed; D-pad in low nibble, buttons in high)
;   wInput+1 = previous
;   wInput+2 = edge     (current AND NOT previous) — newly-pressed bits
;   wInput+3 = repeat counter for held D-pad (frames since last edge)
;
; Bit layout:
;   7=Down 6=Up 5=Left 4=Right 3=Start 2=Select 1=B 0=A
ReadInput::
    ; D-pad
    ld a, P1F_GET_DPAD
    ldh [rP1], a
    ldh a, [rP1]
    ldh a, [rP1]               ; debounce
    cpl
    and $0F
    swap a
    ld b, a                    ; b = D-pad in high nibble (Down/Up/Left/Right)
    ; Buttons
    ld a, P1F_GET_BTN
    ldh [rP1], a
    ldh a, [rP1]
    ldh a, [rP1]
    cpl
    and $0F
    or b
    ld b, a                    ; b = full pad
    ; Reset register.
    ld a, P1F_GET_NONE
    ldh [rP1], a

    ; Shift current → previous, store new current, compute edge.
    ld a, [wInput+0]
    ld [wInput+1], a
    ld a, b
    ld [wInput+0], a
    ld c, a                    ; c = new
    ld a, [wInput+1]           ; old
    cpl
    and c
    ld [wInput+2], a           ; edge = new AND NOT old

    ; Repeat counter — increment if any D-pad bit held; reset on edge.
    ld a, [wInput+2]
    and $F0                    ; D-pad edge bits
    jr z, .no_edge
    xor a
    ld [wInput+3], a
    jr .done
.no_edge:
    ld a, [wInput+0]
    and $F0
    jr z, .reset_repeat
    ld a, [wInput+3]
    inc a
    ld [wInput+3], a
    jr .done
.reset_repeat:
    xor a
    ld [wInput+3], a
.done:
    ret
