; OAM DMA must execute from HRAM because the bus is busy during the transfer.
; Source bytes live in ROM; we copy them to hOAMDMA at boot, then "call hOAMDMA"
; to kick off a DMA.

SECTION "OAM DMA Source", ROM0
OAMDMASource:
    ld a, HIGH(wOAMBuffer)
    ldh [rDMA], a
    ld a, $28                  ; 40 cycles
.wait:
    dec a
    jr nz, .wait
    ret
OAMDMASourceEnd:

OAM_DMA_LEN EQU OAMDMASourceEnd - OAMDMASource

; -----------------------------------------------------------------------------
; Install — copy OAMDMASource into hOAMDMA. Call once at boot.
; -----------------------------------------------------------------------------
SECTION "OAM DMA Install", ROM0
InstallOAMDMA::
    ld de, OAMDMASource
    ld hl, hOAMDMA
    ld bc, OAM_DMA_LEN
.loop:
    ld a, [de]
    ld [hl+], a
    inc de
    dec bc
    ld a, b
    or c
    jr nz, .loop
    ret
