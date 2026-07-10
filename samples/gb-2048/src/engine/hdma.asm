SECTION "HDMA", ROM0

; HdmaCopy — GBC general-purpose DMA. CPU halts during transfer.
;   HL = source (16-byte aligned recommended)
;   DE = destination ($8000..$9FF0)
;   B  = (length / 16) - 1   (i.e. number of 16-byte blocks minus one)
HdmaCopy::
    ld a, h
    ldh [rHDMA1], a
    ld a, l
    ldh [rHDMA2], a
    ld a, d
    ldh [rHDMA3], a
    ld a, e
    ldh [rHDMA4], a
    ld a, b
    and $7F                    ; clear bit 7 = general-purpose mode
    ldh [rHDMA5], a
    ret
