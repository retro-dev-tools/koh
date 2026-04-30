INCLUDE "hardware.inc"
INCLUDE "macros.inc"
INCLUDE "memory.inc"

; -----------------------------------------------------------------------------
; Reset vectors. RST $00..$38 are 8 bytes apart; we use $38 only.
; -----------------------------------------------------------------------------
SECTION "RST_38", ROM0[$0038]
    jp $0038                  ; default crash handler — infinite loop

; -----------------------------------------------------------------------------
; Interrupt vectors.
; -----------------------------------------------------------------------------
SECTION "IRQ_VBlank", ROM0[$0040]
    jp VBlankIRQ
SECTION "IRQ_LCD", ROM0[$0048]
    jp StatIRQ
SECTION "IRQ_Timer", ROM0[$0050]
    reti
SECTION "IRQ_Serial", ROM0[$0058]
    reti
SECTION "IRQ_Joypad", ROM0[$0060]
    reti

; -----------------------------------------------------------------------------
; Cartridge entry. The boot ROM jumps to $0100 after handing off.
; -----------------------------------------------------------------------------
SECTION "Reset", ROM0[$0100]
EntryPoint:
    nop
    jp Boot

; -----------------------------------------------------------------------------
; ROM header at $0104..$014F. Nintendo logo first.
; -----------------------------------------------------------------------------
SECTION "Header", ROM0[$0104]
    ; Nintendo logo (verbatim).
    db $CE,$ED,$66,$66,$CC,$0D,$00,$0B,$03,$73,$00,$83,$00,$0C,$00,$0D
    db $00,$08,$11,$1F,$88,$89,$00,$0E,$DC,$CC,$6E,$E6,$DD,$DD,$D9,$99
    db $BB,$BB,$67,$63,$6E,$0E,$EC,$CC,$DD,$DC,$99,$9F,$BB,$B9,$33,$3E

    ; Title (11 bytes, $0134..$013E).
    db "KOH 2048",0,0,0
    ; Manufacturer code (4 bytes) — leave zero.
    db 0,0,0,0
    ; CGB flag.
    db $C0
    ; New licensee code.
    db "00"
    ; SGB flag.
    db $00
    ; Cartridge type — MBC5 + RAM + BATTERY.
    db $1B
    ; ROM size — $01 = 64 KiB (4 banks).
    db $01
    ; RAM size — $02 = 8 KiB (1 bank).
    db $02
    ; Destination — non-Japan.
    db $01
    ; Old licensee — $33 means "see new licensee".
    db $33
    ; Mask ROM version.
    db $00
    ; Header checksum — set to 0 here; T7 verifies whether the linker patches it.
    db $00
    ; Global checksum — set to 0; same T7 question.
    db $00,$00

; -----------------------------------------------------------------------------
SECTION "Boot", ROM0
Boot:
    di
    ld sp, $E000

    ; 1. Disable LCD before touching VRAM. Wait for VBlank first to avoid the
    ;    well-known GBC LCD-off-mid-frame issue.
    ldh a, [rLY]
    cp 144
    jr c, .wait_vblank
    xor a
    ldh [rLCDC], a
    jr .lcd_off
.wait_vblank:
    ldh a, [rLY]
    cp 144
    jr c, .wait_vblank
    xor a
    ldh [rLCDC], a
.lcd_off:

    ; 2. Switch to double speed. KEY1 prepare → STOP.
    ld a, $30                  ; P1F_GET_NONE — pad disabled
    ldh [rP1], a
    ld a, %0000_0001           ; bit 0 = prepare speed switch
    ldh [rKEY1], a
    stop
    nop                        ; safety nop after STOP

    ; 3. Clear VRAM bank 0 ($8000..$9FFF) and bank 1.
    xor a
    ldh [rVBK], a
    call ClearVram
    ld a, 1
    ldh [rVBK], a
    call ClearVram
    xor a
    ldh [rVBK], a

    ; 4. Clear WRAM ($C000..$DFFF). Skip OAM buffer? Cleared anyway.
    ld hl, $C000
    ld bc, $2000
    call MemClear

    ; 5. Clear OAM (write to OAM directly; rDMA copies wOAMBuffer next VBlank).
    ld hl, _OAMRAM
    ld bc, 160
    call MemClear

    ; Install the OAM DMA trampoline.
    call InstallOAMDMA

    ; Clear the VBlank queue.
    call QueueClear

    ; Seed RNG from DIV (will be re-seeded by first input later).
    ldh a, [rDIV]
    ld h, a
    ldh a, [rDIV]
    ld l, a
    farcall 1, RngSeed
    farcall 1, SaveLoad
    call SoundInit

    ; 6. Initial state.
    xor a
    ld [wCurrentBank], a       ; bank 0 active by default

    ; Enable VBlank IRQ. STAT IRQ enabled by title screen later.
    xor a
    ldh [rIF], a
    ld a, IEF_VBLANK
    ldh [rIE], a
    ei

    ; Minimal LCD-on so VBlank fires. Real LCDC value set by render init later.
    ld a, LCDCF_ON
    ldh [rLCDC], a

    ; 7. Main loop — wait for VBlank flag then poll input.
.main_loop:
    call WaitForVBlankFlag
    call ReadInput
    jr .main_loop

WaitForVBlankFlag:
.wait:
    halt
    ld a, [wVBlankFlag]
    or a
    jr z, .wait
    xor a
    ld [wVBlankFlag], a
    ret

ClearVram:
    ld hl, $8000
    ld bc, $2000
.loop:
    xor a
    ld [hl+], a
    dec bc
    ld a, b
    or c
    jr nz, .loop
    ret

; -----------------------------------------------------------------------------
; MemClear — fill HL..HL+BC-1 with zero. Clobbers A, BC, HL.
; -----------------------------------------------------------------------------
SECTION "MemClear", ROM0
MemClear:
    xor a
.loop:
    ld [hl+], a
    dec bc
    ld a, b
    or c
    jr nz, .loop
    ret

INCLUDE "engine/oam_dma.asm"
INCLUDE "engine/vblank_queue.asm"
INCLUDE "engine/irq.asm"
INCLUDE "engine/input.asm"
INCLUDE "engine/hdma.asm"
INCLUDE "engine/rng.asm"
INCLUDE "engine/sound.asm"
INCLUDE "game/score.asm"
INCLUDE "game/board.asm"
INCLUDE "game/save.asm"
INCLUDE "gfx/tiles.asm"
INCLUDE "gfx/font.asm"
INCLUDE "game/render.asm"
INCLUDE "game/anim.asm"
