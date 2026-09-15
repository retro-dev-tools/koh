namespace Koh.Benchmarks;

/// <summary>
/// Inline assembly source strings for benchmarks at three complexity levels.
/// </summary>
static class Sources
{
    /// <summary>
    /// ~10 instructions. Minimal: basic instructions, one section, one label.
    /// </summary>
    public const string Small = """
        SECTION "Main", ROM0
        main:
            nop
            ld a, $42
            ld b, a
            add a, b
            cp $84
            jr nz, main
            halt
        """;

    /// <summary>
    /// ~80 lines. Constants, multiple sections, labels, data directives,
    /// local labels, forward references, string data.
    /// </summary>
    public const string Medium = """
        ; Constants
        PLAYER_SPEED EQU 2
        MAX_HEALTH   EQU 100
        TILE_SIZE    EQU 8
        SCREEN_W     EQU 160
        SCREEN_H     EQU 144
        NUM_SPRITES  EQU 40
        OAM_SIZE     EQU NUM_SPRITES * 4

        SECTION "Header", ROM0[$0100]
        entry:
            nop
            jp start

        SECTION "Main", ROM0[$0150]
        start:
            di
            ld sp, $FFFE
            call init_hardware
            call init_game
            ei
        .main_loop:
            halt
            call read_input
            call update_game
            call render
            jr .main_loop

        init_hardware:
            ld a, $00
            ldh [$40], a
        .wait_vblank:
            ldh a, [$44]
            cp 144
            jr nz, .wait_vblank
            ret

        init_game:
            ld a, PLAYER_SPEED
            ld [player_speed], a
            ld a, MAX_HEALTH
            ld [player_hp], a
            ld hl, player_name
            ld de, default_name
            ld bc, 8
        .copy_name:
            ld a, [de]
            ld [hli], a
            inc de
            dec bc
            ld a, b
            or c
            jr nz, .copy_name
            ret

        read_input:
            ld a, $20
            ldh [$00], a
            ldh a, [$00]
            cpl
            and $0F
            ret

        update_game:
            ld a, [player_hp]
            or a
            ret z
            ld a, [player_x]
            add a, PLAYER_SPEED
            cp SCREEN_W - TILE_SIZE
            jr nc, .clamp_x
            ld [player_x], a
            ret
        .clamp_x:
            ld a, SCREEN_W - TILE_SIZE
            ld [player_x], a
            ret

        render:
            ret

        SECTION "Data", ROM0
        default_name:
            db "PLAYER", 0, 0

        tile_data:
            db $FF, $00, $FF, $00, $FF, $00, $FF, $00
            db $00, $FF, $00, $FF, $00, $FF, $00, $FF
            db $AA, $55, $AA, $55, $AA, $55, $AA, $55
            db $55, $AA, $55, $AA, $55, $AA, $55, $AA

        SECTION "Variables", WRAM0
        player_x:    ds 1
        player_y:    ds 1
        player_hp:   ds 1
        player_speed: ds 1
        player_name: ds 8
        frame_count: ds 2
        """;

    /// <summary>
    /// ~200 lines. Macros, REPT, IF/ENDC, multiple sections and banks,
    /// complex expressions, anonymous labels, extensive data tables.
    /// </summary>
    public const string Large = """
        ; System constants
        VRAM_START   EQU $8000
        OAM_START    EQU $FE00
        LCDC_REG     EQU $FF40
        STAT_REG     EQU $FF41
        SCY_REG      EQU $FF42
        SCX_REG      EQU $FF43
        LY_REG       EQU $FF44
        BGP_REG      EQU $FF47
        OBP0_REG     EQU $FF48
        OBP1_REG     EQU $FF49

        NUM_ENEMIES    EQU 8
        ENEMY_STRUCT_SIZE EQU 6
        MAX_PROJECTILES EQU 4
        PROJ_STRUCT_SIZE EQU 5

        ; Macros
        MACRO wait_vblank
            ldh a, [LY_REG]
            cp 144
            jr nz, @-3
        ENDM

        MACRO set_palette
            ld a, \1
            ldh [\2], a
        ENDM

        MACRO ld16
            ld \1, LOW(\3)
            ld \2, HIGH(\3)
        ENDM

        MACRO memcpy
            ; \1 = dest, \2 = src, \3 = length
            ld hl, \1
            ld de, \2
            ld bc, \3
        :   ld a, [de]
            ld [hli], a
            inc de
            dec bc
            ld a, b
            or c
            jr nz, :-
        ENDM

        MACRO memset
            ; \1 = dest, \2 = value, \3 = length
            ld hl, \1
            ld a, \2
            ld bc, \3
        :   ld [hli], a
            dec bc
            ld a, b
            or c
            jr nz, :-
        ENDM

        SECTION "Entry", ROM0[$0100]
            nop
            jp main

        SECTION "Code", ROM0[$0150]
        main:
            di
            ld sp, $FFFE

            wait_vblank
            xor a
            ldh [LCDC_REG], a

            set_palette $E4, BGP_REG
            set_palette $E4, OBP0_REG
            set_palette $D2, OBP1_REG

            memset OAM_START, $00, 160
            memcpy VRAM_START, tile_data, tile_data_end - tile_data
            call init_enemies
            call init_projectiles

            ld a, $91
            ldh [LCDC_REG], a
            ei

        .game_loop:
            halt
            nop
            call poll_joypad
            call update_player
            call update_enemies
            call update_projectiles
            call check_collisions
            call update_oam
            jr .game_loop

        poll_joypad:
            ld a, $20
            ldh [$00], a
            ldh a, [$00]
            ldh a, [$00]
            cpl
            and $0F
            swap a
            ld b, a
            ld a, $10
            ldh [$00], a
            ldh a, [$00]
            ldh a, [$00]
            cpl
            and $0F
            or b
            ld [joypad_state], a
            ret

        update_player:
            ld a, [joypad_state]
            bit 4, a
            jr z, .no_right
            ld a, [player_x]
            add a, 2
            cp 160
            jr nc, .no_right
            ld [player_x], a
        .no_right:
            ld a, [joypad_state]
            bit 5, a
            jr z, .no_left
            ld a, [player_x]
            sub 2
            jr c, .no_left
            ld [player_x], a
        .no_left:
            ld a, [joypad_state]
            bit 6, a
            jr z, .no_up
            ld a, [player_y]
            sub 2
            jr c, .no_up
            ld [player_y], a
        .no_up:
            ld a, [joypad_state]
            bit 7, a
            jr z, .no_down
            ld a, [player_y]
            add a, 2
            cp 144
            jr nc, .no_down
            ld [player_y], a
        .no_down:
            ret

        init_enemies:
            ld hl, enemy_data
            ld b, NUM_ENEMIES
            ld de, 20
        .init_loop:
            ld a, d
            ld [hli], a
            add a, 16
            ld d, a
            ld a, e
            ld [hli], a
            add a, 12
            ld e, a
            ld a, 3
            ld [hli], a
            ld a, 1
            ld [hli], a
            xor a
            ld [hli], a
            ld [hli], a
            dec b
            jr nz, .init_loop
            ret

        update_enemies:
            ld hl, enemy_data
            ld b, NUM_ENEMIES
        .enemy_loop:
            ld a, [hli]
            ld c, a
            ld a, [hli]
            ld d, a
            ld a, [hli]
            or a
            jr z, .enemy_dead
            ld a, [hli]
            add a, c
            ld c, a
            cp 160
            jr c, .no_bounce
            xor a
            ld c, a
        .no_bounce:
            push hl
            dec hl
            dec hl
            dec hl
            dec hl
            ld [hl], c
            pop hl
            inc hl
            inc hl
            jr .enemy_next
        .enemy_dead:
            inc hl
            inc hl
            inc hl
        .enemy_next:
            dec b
            jr nz, .enemy_loop
            ret

        init_projectiles:
            memset projectile_data, $00, MAX_PROJECTILES * PROJ_STRUCT_SIZE
            ret

        update_projectiles:
            ld hl, projectile_data
            ld b, MAX_PROJECTILES
        .proj_loop:
            ld a, [hl]
            or a
            jr z, .proj_inactive
            ld a, [hli]
            sub 4
            jr c, .proj_deactivate
            push hl
            dec hl
            ld [hl], a
            pop hl
            inc hl
            inc hl
            inc hl
            jr .proj_next
        .proj_deactivate:
            push hl
            dec hl
            xor a
            ld [hl], a
            pop hl
            inc hl
            inc hl
            inc hl
            jr .proj_next
        .proj_inactive:
            inc hl
            inc hl
            inc hl
            inc hl
        .proj_next:
            dec b
            jr nz, .proj_loop
            ret

        check_collisions:
            ret

        update_oam:
            ld hl, OAM_START
            ld a, [player_y]
            add a, 16
            ld [hli], a
            ld a, [player_x]
            add a, 8
            ld [hli], a
            xor a
            ld [hli], a
            ld [hli], a
            ret

        SECTION "TileData", ROM0
        tile_data:
            REPT 16
                db $FF, $00
            ENDR
            REPT 16
                db $AA, $55
            ENDR
            REPT 16
                db $00, $FF
            ENDR
            REPT 16
                db $55, $AA
            ENDR
        tile_data_end:

        score_digits:
            db "0123456789"

        msg_game_over:
            db "GAME OVER", 0

        msg_press_start:
            db "PRESS START", 0

        ; Level data table
        level_widths:
            REPT 10
                db 20
            ENDR

        level_heights:
            REPT 10
                db 18
            ENDR

        enemy_spawn_table:
            REPT 8
                dw $0000
            ENDR

        SECTION "WRAM", WRAM0
        player_x:       ds 1
        player_y:       ds 1
        player_hp:      ds 1
        player_score:   ds 2
        joypad_state:   ds 1
        prev_joypad:    ds 1
        frame_counter:  ds 2
        game_state:     ds 1

        enemy_data:     ds NUM_ENEMIES * ENEMY_STRUCT_SIZE
        projectile_data: ds MAX_PROJECTILES * PROJ_STRUCT_SIZE

        oam_buffer:     ds 160
        """;
}
