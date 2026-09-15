; VBlank queue — VRAM-write commands buffered for the VBlank ISR.
;
; Queue lives at wVBlankQueue (64 bytes in WRAM).
; Entry format (packed, contiguous):
;   [cmd:1] [dst_lo:1] [dst_hi:1] [len:1] [data:len]
;
;   cmd $00 = terminator (queue empty / end of queue)
;   cmd $01 = copy len bytes to VRAM address (dst_hi << 8 | dst_lo)
;
; Producers append via QueueCopy.
; The VBlank ISR drains via QueueDrain, then resets via QueueClear.

; -----------------------------------------------------------------------------
; QueueClear — mark queue empty by writing $00 at wVBlankQueue+0.
; Clobbers A, HL.
; -----------------------------------------------------------------------------
SECTION "VBlank Queue", ROM0
QueueClear::
    ld hl, wVBlankQueue
    xor a
    ld [hl], a
    ret

; -----------------------------------------------------------------------------
; QueueCopy — append a copy entry to the queue.
;
; Inputs:
;   [hScratch+0] = dst low byte
;   [hScratch+1] = dst high byte
;   [hScratch+2] = length (1..32)
;   HL            = source data pointer (ROM or RAM)
;
; Entry written: [$01][dst_lo][dst_hi][len][data:len]
; A terminator ($00) is placed after the entry.
;
; Clobbers A, BC, DE, HL.
; -----------------------------------------------------------------------------
QueueCopy::
    ; Save source pointer — HL is needed for the queue walk.
    push hl

    ; --- Walk the queue to find the terminator ---
    ld hl, wVBlankQueue
.walk:
    ld a, [hl]
    or a
    jr z, .found           ; $00 = terminator, stop here
    ; Skip over this entry: header is 4 bytes + data len bytes.
    ; Read len from [HL+3].
    inc hl                 ; dst_lo
    inc hl                 ; dst_hi
    inc hl                 ; len
    ld a, [hl]             ; a = len
    inc hl                 ; now HL points to first data byte
    ; Advance HL by len (to skip data bytes).
    ld b, 0
    ld c, a
    add hl, bc
    ; HL now points to the next entry's cmd byte.
    jr .walk

.found:
    ; HL points at the terminator slot — write the entry here.
    ; Write cmd = $01.
    ld a, $01
    ld [hl+], a

    ; Write dst_lo from [hScratch+0].
    ldh a, [hScratch+0]
    ld [hl+], a

    ; Write dst_hi from [hScratch+1].
    ldh a, [hScratch+1]
    ld [hl+], a

    ; Write len from [hScratch+2].
    ldh a, [hScratch+2]
    ld [hl+], a            ; HL now points to first data slot
    ld c, a                ; save len in C

    ; Pop source pointer into DE so we can copy.
    pop de                 ; DE = source

    ; Copy len bytes from DE to [HL+].
    ld b, 0
.copy_loop:
    ld a, c
    or a
    jr z, .copy_done
    ld a, [de]
    ld [hl+], a
    inc de
    dec c
    jr .copy_loop
.copy_done:
    ; Write terminator after the entry.
    xor a
    ld [hl], a
    ret

; -----------------------------------------------------------------------------
; QueueDrain — drain all entries from the queue (called from VBlank ISR).
;
; Walks from wVBlankQueue; for each cmd $01 entry, copies data to VRAM.
; Stops on terminator. Resets the queue (writes $00 at offset 0) when done.
;
; Clobbers A, BC, DE, HL.
; -----------------------------------------------------------------------------
QueueDrain::
    ld hl, wVBlankQueue
.dispatch:
    ld a, [hl+]            ; read cmd; HL advances to dst_lo
    or a
    jr z, .drain_done      ; terminator — stop

    cp $01
    jr z, .cmd_copy
    ; Unknown cmd — skip header+data: read len from [HL+2].
    inc hl                 ; skip dst_hi
    inc hl                 ; now at len
    ld a, [hl+]            ; a = len; HL -> first data byte
    ld b, 0
    ld c, a
    add hl, bc             ; skip data bytes
    jr .dispatch

.cmd_copy:
    ; Read dst low byte.
    ld e, [hl]             ; dst_lo -> E
    inc hl
    ; Read dst high byte.
    ld d, [hl]             ; dst_hi -> D
    inc hl
    ; Read len.
    ld a, [hl+]            ; len -> A; HL -> first data byte
    ld c, a                ; save len
    ; Now copy C bytes from [HL] to VRAM at [DE].
.vram_copy_loop:
    ld a, c
    or a
    jr z, .vram_copy_done
    ld a, [hl+]            ; read byte from queue
    ld [de], a             ; write to VRAM
    inc de
    dec c
    jr .vram_copy_loop
.vram_copy_done:
    ; HL now points to the next entry's cmd byte.
    jr .dispatch

.drain_done:
    ; Reset the queue.
    call QueueClear
    ret
