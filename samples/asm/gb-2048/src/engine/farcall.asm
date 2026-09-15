; engine/farcall.asm — ROM0-resident farcall dispatcher.
;
; Lives in ROM0 so its instructions stay mapped while rROMB0 changes the
; $4000-$7FFF window. Doing the bank switch inline from a banked caller would
; re-map the window underneath PC at the moment of the CALL, leaving the CPU
; executing bytes from the new bank instead of the original CALL opcode.
;
; Inputs:
;   A  = target bank (0..N)
;   BC = target address (typically in $4000-$7FFF, but ROM0 targets work too)
;
; Side effects:
;   wCurrentBank tracks the active bank across the call (target restores its
;   own state on return, then this helper restores the caller's bank).
;   Preserves HL and DE; AF, BC clobbered by the trampoline's bookkeeping.

SECTION "Farcall Trampoline", ROM0
FarcallTrampoline::
    push hl
    push de

    ; D = target bank, E = caller bank.
    ld d, a
    ld a, [wCurrentBank]
    ld e, a

    ; Switch to target bank.
    ld a, d
    ld [wCurrentBank], a
    ld [rROMB0], a

    ; Stash the caller bank where we can grab it after the call.
    push de

    ; Synthesise "call bc": push the post-call return point and the target,
    ; then RET, which pops BC into PC.
    ld hl, .after_call
    push hl
    push bc
    ret

.after_call:
    ; Stack top here is the pushed DE from before the call: low = caller bank.
    pop de
    ld a, e
    ld [wCurrentBank], a
    ld [rROMB0], a

    pop de
    pop hl
    ret
