; game/intents.asm — fill wMoveIntents from wPrevBoard for sprite animation.
;
; wMoveIntents (16 entries × 4 bytes), indexed by original board cell:
;   byte 0  dst board index after slide+merge, or $FF if cell was empty
;   byte 1  pre-move value (1..12)
;   byte 2  merge flag (1 if this tile is the disappearing partner in a merge)
;   byte 3  reserved
;
; FillMoveIntents walks each row of the direction table, simulating a standard
; 2048 left-slide with merges. Mirrors MoveLeft's compact+merge+compact, but
; tracks where each source tile *ended up* rather than mutating the board.
;
; hScratch layout used here (independent of MoveLeft, which has already run):
;   +0  direction table base (low)
;   +1  direction table base (high)
;   +2  row counter (0..3)
;   +3  i within row (0..3, unused — inlined four times)
;   +4  w (write pointer 0..4)
;   +5  last projected value at slot w-1 (0 if w == 0)
;   +6  last_merged flag (0 or 1)
;   +7  unused

SECTION "Move Intents", ROMX, BANK[1]

FillMoveIntents::
    ld a, l
    ldh [hScratch+0], a
    ld a, h
    ldh [hScratch+1], a

    ; --- Clear all 16 intents to (dst=$FF, val=0, merged=0, pad=0). ---
    ld hl, wMoveIntents
    ld b, 16
.clear_intent:
    ld a, $FF
    ld [hl+], a            ; dst
    xor a
    ld [hl+], a            ; value
    ld [hl+], a            ; merged
    ld [hl+], a            ; pad
    dec b
    jr nz, .clear_intent

    xor a
    ldh [hScratch+2], a    ; row = 0

.row_loop:
    ; Per-row state.
    xor a
    ldh [hScratch+4], a    ; w
    ldh [hScratch+5], a    ; last_val
    ldh [hScratch+6], a    ; last_merged

    ; --- Process the 4 cells of this row, in order. ---
    ld a, 0
    call _IntentStep
    ld a, 1
    call _IntentStep
    ld a, 2
    call _IntentStep
    ld a, 3
    call _IntentStep

    ; Advance row counter.
    ldh a, [hScratch+2]
    inc a
    ldh [hScratch+2], a
    cp 4
    jr nz, .row_loop
    ret


; ----------------------------------------------------------------------------
; _IntentStep — process the i-th cell of the current row.
;   Input: A = i (0..3); hScratch+0..6 hold direction base, row, w, last_val,
;          last_merged.
;   Reads: wPrevBoard, direction table at (hScratch+0..1) offset row*4 + i.
;   Writes: wMoveIntents[src_idx] on non-zero values; updates w/last/lm.
;   Clobbers AF, BC, DE, HL.
; ----------------------------------------------------------------------------
_IntentStep:
    ; Save i in C.
    ld c, a

    ; HL = direction table base + row*4 + i.
    ldh a, [hScratch+2]
    add a
    add a                  ; row * 4
    add c                  ; + i
    ld e, a
    ld d, 0
    ldh a, [hScratch+0]
    ld l, a
    ldh a, [hScratch+1]
    ld h, a
    add hl, de
    ld a, [hl]             ; A = src board index for this row-slot
    ld b, a                ; B = src_idx

    ; Read wPrevBoard[src_idx] -> value in A.
    ld h, HIGH(wPrevBoard)
    ld l, LOW(wPrevBoard)
    ld a, b
    add l
    ld l, a
    ld a, 0
    adc h
    ld h, a
    ld a, [hl]             ; A = pre-move value
    or a
    ret z                  ; empty cell — no intent

    ld d, a                ; D = val

    ; --- Merge check: w > 0 AND val == last_val AND !last_merged ---
    ldh a, [hScratch+4]    ; w
    or a
    jr z, .new_slot

    ldh a, [hScratch+5]    ; last_val
    cp d
    jr nz, .new_slot

    ldh a, [hScratch+6]    ; last_merged
    or a
    jr nz, .new_slot

    ; --- Merge into slot w-1 ---
    ldh a, [hScratch+4]
    dec a                  ; dst_slot = w - 1
    ld e, a                ; E = dst_slot
    ld a, d
    inc a                  ; last_val := val + 1
    ldh [hScratch+5], a
    ld a, 1
    ldh [hScratch+6], a    ; last_merged := 1
    ld a, 1                ; A = merge_flag for the intent
    jr .write_intent

.new_slot:
    ; --- Place in new slot w ---
    ldh a, [hScratch+4]
    ld e, a                ; E = dst_slot = w
    inc a
    ldh [hScratch+4], a    ; w += 1
    ld a, d
    ldh [hScratch+5], a    ; last_val := val
    xor a
    ldh [hScratch+6], a    ; last_merged := 0
    ; A = 0 = merge_flag

.write_intent:
    ; A = merge_flag, B = src_idx, D = val, E = dst_slot (0..3 in row).
    ; Translate dst_slot back to a board index via direction table at
    ; (hScratch+0..1) + row*4 + dst_slot.
    ld c, a                ; C = merge_flag

    ldh a, [hScratch+2]
    add a
    add a                  ; row * 4
    add e                  ; + dst_slot
    ld e, a
    ld d, 0
    ldh a, [hScratch+0]
    ld l, a
    ldh a, [hScratch+1]
    ld h, a
    add hl, de
    ld a, [hl]             ; A = dst board index

    ; HL = &wMoveIntents[src_idx * 4]. src_idx is in B; *4 by two shifts.
    push af                ; save dst_idx
    ld a, b
    add a
    add a                  ; A = src_idx * 4
    ld e, a
    ld d, 0
    ld hl, wMoveIntents
    add hl, de
    pop af                 ; A = dst_idx

    ld [hl+], a            ; intent.dst = dst_idx

    ; Re-derive val: it was in D, but D may have been clobbered above.
    ; Re-read wPrevBoard[src_idx] — cheaper than juggling on the stack.
    push hl
    ld h, HIGH(wPrevBoard)
    ld l, LOW(wPrevBoard)
    ld a, b
    add l
    ld l, a
    ld a, 0
    adc h
    ld h, a
    ld a, [hl]             ; val
    pop hl
    ld [hl+], a            ; intent.value = val

    ld a, c                ; A = merge_flag
    ld [hl+], a            ; intent.merged = flag
    xor a
    ld [hl], a             ; intent.pad = 0
    ret
