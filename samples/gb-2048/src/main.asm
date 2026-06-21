INCLUDE "hardware.inc"
INCLUDE "macros.inc"
INCLUDE "memory.inc"
INCLUDE "gfx/tiles.inc"

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

    ; 4. Clear WRAM ($C000..$DFFF). MemClear correctly preserves A inside the
    ; loop, so it doesn't matter that the stack lives near the end of WRAM:
    ; each iteration writes 0 (not the BC count), so even though the cleared
    ; range includes the active stack slot holding MemClear's return address,
    ; the byte being written *is* 0 and the next pop will read it correctly...
    ; *but* the return-address bytes would still be overwritten with 0, so
    ; `ret` would jump to $0000. Stop just shy of the stack top to keep the
    ; live return address intact.
    ld hl, $C000
    ld bc, $1F00
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

    ; LCD stays OFF until RenderInit finishes. RenderInit assumes the LCD is
    ; off so its VRAM writes (font/tile copy, palette load, BG clear) all
    ; succeed; it re-enables the LCD with the final LCDC value at the end.
    farcall 1, RenderInit
    farcall 3, TitleEnter

    ; 7. Main loop — dispatch on wGameState each frame.
.main_loop:
    call WaitForVBlankFlag
    call ReadInput

    ld a, [wGameState]
    cp GS_TITLE
    jr z, .title
    cp GS_PLAYING
    jr z, .playing
    cp GS_ANIMATING
    jr z, .animating
    cp GS_WIN
    jr z, .win
    cp GS_GAMEOVER
    jr z, .gameover
    jr .main_loop
.title:
    farcall 3, TitleTick
    jr .main_loop
.playing:
    call PlayingTick
    jr .main_loop
.animating:
    farcall 1, AnimTick
    jr .main_loop
.win:
    farcall 3, WinTick
    jr .main_loop
.gameover:
    farcall 3, GameOverTick
    jp .main_loop

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
; DrawString — write a CHARMAP-encoded string into the tilemap.
;   HL = destination tilemap address (advanced as bytes are written).
;   DE = source string (terminated by STR_END / $FF).
;   Clobbers AF, DE, HL.
; -----------------------------------------------------------------------------
SECTION "DrawString", ROM0
DrawString::
.loop:
    ld a, [de]
    cp STR_END
    ret z
    ld [hl+], a
    inc de
    jr .loop

; -----------------------------------------------------------------------------
; FillVram — fill BC bytes at HL with value A.
;   Doesn't touch rVBK: the caller chooses bank 0 or bank 1.
;   Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
SECTION "FillVram", ROM0
FillVram::
    ld d, a                    ; preserve value across the B/C test below
.loop:
    ld a, b
    or c
    ret z
    ld a, d
    ld [hl+], a
    dec bc
    jr .loop

; -----------------------------------------------------------------------------
; ClearBoardArea — clear the 15-row board region (rows 3..17, all 32 cols
;   of the tilemap line including the off-screen columns) of both BG tiles
;   and CGB attribute bytes. Restores VBK to bank 0 on return.
;
;   Turns the LCD off for the duration of the clear so the writes always
;   land. Two 480-byte fills at the FillVram cost (~11 T-cycles/byte) total
;   roughly 10K T-cycles, more than twice a CGB VBlank window — without
;   LCD off the late writes happen during PPU mode 3 and are silently
;   dropped on real hardware (and accurate emulators like mGBA / BGB),
;   leaving the title text / banner partially blank.
;
;   Re-enables the LCD with the standard game LCDC at the end. Only safe
;   to call while in VBlank (main loop runs only after WaitForVBlankFlag).
;   Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
SECTION "ClearBoardArea", ROM0
ClearBoardArea::
    ; Caller is responsible for turning the LCD off + on around this routine
    ; (LcdOff / LcdOn). The board area attribute defaults to palette 1
    ; (dark slate frame) so the gap region between cells still reads as
    ; the frame even after a clear.
    ld hl, _SCRN0 + 3 * 32
    ld bc, 15 * 32
    ld a, TILE_FONT_SPACE
    call FillVram

    ld a, 1
    ldh [rVBK], a
    ld hl, _SCRN0 + 3 * 32
    ld bc, 15 * 32
    ld a, 1                     ; palette 1 = frame
    call FillVram
    xor a
    ldh [rVBK], a
    ret

; -----------------------------------------------------------------------------
; LcdOff / LcdOn — utility entry points for screen transitions that need to
; freely write VRAM. Only safe while we're already in VBlank (main loop only
; runs after WaitForVBlankFlag); turning the LCD off mid-render can damage
; real GBC hardware.
; -----------------------------------------------------------------------------
SECTION "Lcd Utils", ROM0
LcdOff::
    xor a
    ldh [rLCDC], a
    ret
LcdOn::
    ld a, LCDCF_ON | LCDCF_BG8000 | LCDCF_BGON | LCDCF_OBJ8 | LCDCF_OBJON
    ldh [rLCDC], a
    ret

; -----------------------------------------------------------------------------
; FillAttr — write BC bytes at HL in VRAM bank 1 with value A. Used to apply
; an explicit palette to a tilemap region (the title and banner text need
; palette 0 so their white plates contrast with the dark frame they sit on).
; Restores VBK to bank 0. Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
SECTION "FillAttr", ROM0
FillAttr::
    ld d, a
    ld a, 1
    ldh [rVBK], a
    ld a, d
    call FillVram
    xor a
    ldh [rVBK], a
    ret

; -----------------------------------------------------------------------------
; MemClear — fill HL..HL+BC-1 with zero. Clobbers A, BC, HL.
; -----------------------------------------------------------------------------
SECTION "MemClear", ROM0
MemClear:
.loop:
    ; A must be reset to 0 each iteration: `ld a, b` below clobbers it, so
    ; reading A from outside the loop would leave us writing the BC-count
    ; pattern as garbage from byte 1 onward (every odd byte $FF, every even
    ; $FE, etc.) — observed empirically against the gb-2048 boot.
    xor a
    ld [hl+], a
    dec bc
    ld a, b
    or c
    jr nz, .loop
    ret

; -----------------------------------------------------------------------------
; CopyFromBank — read bytes from a banked ROM window into VRAM (or WRAM).
;   A  = source ROM bank (the $4000-$7FFF window will be mapped to it)
;   HL = source address ($4000-$7FFF)
;   DE = destination address
;   BC = byte count
;
; This helper lives in ROM0 because rROMB0 changes the $4000-$7FFF mapping —
; code that executes from the banked region while rROMB0 changes underneath
; it ends up fetching garbage bytes from the new bank. ROM0 is always mapped,
; so the loop instructions stay accessible.
;
; rROMB0 is restored to wCurrentBank on return.
; Clobbers AF, BC, DE, HL.
; -----------------------------------------------------------------------------
SECTION "CopyFromBank", ROM0
CopyFromBank::
    ld [rROMB0], a
.loop:
    ld a, b
    or c
    jr z, .done
    ld a, [hl+]
    ld [de], a
    inc de
    dec bc
    jr .loop
.done:
    ld a, [wCurrentBank]
    ld [rROMB0], a
    ret

INCLUDE "engine/oam_dma.asm"
INCLUDE "engine/vblank_queue.asm"
INCLUDE "engine/irq.asm"
INCLUDE "engine/input.asm"
INCLUDE "engine/hdma.asm"
INCLUDE "engine/farcall.asm"
INCLUDE "engine/rng.asm"
INCLUDE "engine/sound.asm"
INCLUDE "game/score.asm"
INCLUDE "game/board.asm"
INCLUDE "game/intents.asm"
INCLUDE "game/save.asm"
INCLUDE "gfx/tiles.asm"
INCLUDE "gfx/font.asm"
INCLUDE "game/render.asm"
INCLUDE "game/sprites.asm"
INCLUDE "game/anim.asm"
INCLUDE "screens/title.asm"
INCLUDE "screens/gameover.asm"
INCLUDE "screens/win.asm"

; -----------------------------------------------------------------------------
; Playing Logic — D-pad input dispatches to direction-specific move wrappers.
; -----------------------------------------------------------------------------
SECTION "Playing Logic", ROM0
PlayingTick:
    ld a, [wInput+2]           ; edge bits
    bit 4, a                   ; Right
    jr nz, .move_right
    bit 5, a                   ; Left
    jr nz, .move_left
    bit 6, a                   ; Up
    jr nz, .move_up
    bit 7, a                   ; Down
    jr nz, .move_down
    ret
.move_left:
    farcall 1, MoveLeft_DirLeft
    ret
.move_right:
    farcall 1, MoveLeft_DirRight
    ret
.move_up:
    farcall 1, MoveLeft_DirUp
    ret
.move_down:
    farcall 1, MoveLeft_DirDown
    ret
