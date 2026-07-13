using Koh.Emulator.Core.Boot;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core.Tests;

/// <summary>
/// GameBoySystem skips the real boot ROM, but must still land in the memory
/// state the boot ROM leaves behind (Pan Docs "Power-Up Sequence"; SameBoy's
/// clean-room dmg_boot.asm/cgb_boot.asm confirm the behavior independently):
/// VRAM cleared to $00 (DMG additionally gets the decompressed header logo
/// tiles + a 12x2 tilemap patch at rows 8-9, which the real boot ROM never
/// clears again before hand-off), and on CGB every BG color palette faded to
/// white before hand-off (the CGB boot ROM clears VRAM a second time via
/// HDMA right before jumping to the cartridge, so no logo remnant survives
/// there). Regression guard for the bug where $FF-poisoned VRAM (fc5a251)
/// made an untouched tilemap render solid black instead of the correct
/// white.
/// </summary>
public class BootHandoffTests
{
    private static GameBoySystem MakeSystem(HardwareMode mode)
    {
        var rom = new byte[0x8000]; // all-zero header logo + $0100.. is NOP forever
        rom[0x147] = 0x00; // RomOnly
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(mode, cart);
    }

    [Test]
    public async Task Cgb_Vram_Is_Fully_Cleared_At_Construction()
    {
        var gb = MakeSystem(HardwareMode.Cgb);
        foreach (var b in gb.Mmu.VramArray)
            await Assert.That(b).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Dmg_Vram_Is_Cleared_Except_The_Logo_Tilemap_Patch()
    {
        // The synthetic ROM's header logo field ($0104-$0133) is all zero,
        // so the decompressed logo tiles are all-zero pixel data too -- the
        // only non-zero bytes anywhere in VRAM should be the 24 tilemap
        // cells (rows 8-9, cols 4-15) that reference tile indices 1-24.
        var gb = MakeSystem(HardwareMode.Dmg);
        var vram = gb.Mmu.VramArray;

        const int tilemapBase = 0x1800;
        var expectedNonZero = new HashSet<int>();
        for (int trow = 0; trow < BootLogo.TileRows; trow++)
        for (int tcol = 0; tcol < BootLogo.TileColumns; tcol++)
        {
            int screenRow = 8 + trow;
            int screenCol = 4 + tcol;
            int offset = tilemapBase + screenRow * 32 + screenCol;
            expectedNonZero.Add(offset);
            await Assert
                .That(vram[offset])
                .IsEqualTo((byte)(1 + trow * BootLogo.TileColumns + tcol));
        }

        for (int i = 0; i < vram.Length; i++)
        {
            if (expectedNonZero.Contains(i))
                continue;
            await Assert.That(vram[i]).IsEqualTo((byte)0x00);
        }
    }

    [Test]
    public async Task Cgb_Bg_Palette_Is_White_At_Construction()
    {
        var gb = MakeSystem(HardwareMode.Cgb);
        for (int pal = 0; pal < 8; pal++)
        for (int slot = 0; slot < 4; slot++)
            await Assert.That(gb.Ppu.BgPalette.GetColor(pal, slot)).IsEqualTo((ushort)0x7FFF);
    }

    [Test]
    public async Task Cgb_Obj_Palette_Is_Left_Untouched()
    {
        // The boot ROM's white-fade only applies to BG palettes (Pan Docs);
        // object palette hand-off state is undocumented/compat-dependent, so
        // we deliberately don't touch it here.
        var gb = MakeSystem(HardwareMode.Cgb);
        for (int pal = 0; pal < 8; pal++)
        for (int slot = 0; slot < 4; slot++)
            await Assert.That(gb.Ppu.ObjPalette.GetColor(pal, slot)).IsEqualTo((ushort)0x0000);
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task Wram_Oam_Hram_Remain_Poisoned_At_Construction(HardwareMode mode)
    {
        // Boundary discipline: only VRAM (+ CGB BG palette) reflect the
        // hand-off correction. WRAM/OAM/HRAM are untouched by any boot ROM
        // and must stay $FF-poisoned (fc5a251) so a compiled ROM reading
        // uninitialized RAM still shows up here, not just on real hardware.
        var gb = MakeSystem(mode);
        await Assert.That(gb.DebugReadByte(0xC000)).IsEqualTo((byte)0xFF); // WRAM
        await Assert.That(gb.DebugReadByte(0xFE00)).IsEqualTo((byte)0xFF); // OAM
        await Assert.That(gb.DebugReadByte(0xFF80)).IsEqualTo((byte)0xFF); // HRAM
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task Background_Renders_White_Not_Black_After_Boot_Handoff(HardwareMode mode)
    {
        // End-to-end: an untouched tilemap (all bytes $00 -> tile $00, which
        // is now all-zero pixel data) must render as color id 0, which is
        // white under the post-boot palette (BGP=$FC on DMG; faded-white CGB
        // BG palette 0) -- matching mGBA, not the black regression an
        // $FF-poisoned VRAM produced.
        var gb = MakeSystem(mode);
        for (int i = 0; i < 60; i++)
            gb.RunFrame();

        var fb = gb.Framebuffer.FrontArray;
        await Assert.That(fb[0]).IsEqualTo((byte)0xFF);
        await Assert.That(fb[1]).IsEqualTo((byte)0xFF);
        await Assert.That(fb[2]).IsEqualTo((byte)0xFF);

        int lastPixel = (Framebuffer.Width * Framebuffer.Height - 1) * 4;
        await Assert.That(fb[lastPixel]).IsEqualTo((byte)0xFF);
        await Assert.That(fb[lastPixel + 1]).IsEqualTo((byte)0xFF);
        await Assert.That(fb[lastPixel + 2]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Boot_Animation_Is_Off_By_Default()
    {
        // Hundreds of existing tests construct GameBoySystem and expect
        // PC=$0100 CPU-driven semantics on the very first RunFrame -- the
        // animation must never activate without an explicit ArmBootAnimation() call.
        var gb = MakeSystem(HardwareMode.Dmg);
        await Assert.That(gb.BootAnimationActive).IsFalse();
        var before = gb.Cpu.TotalTCycles;
        gb.RunFrame();
        await Assert.That(gb.Cpu.TotalTCycles).IsNotEqualTo(before); // CPU actually ran
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task Armed_Boot_Animation_Eventually_Hands_Off_To_Identical_State(
        HardwareMode mode
    )
    {
        // Skipping the animation (default path) and watching it (armed path) must land
        // in the same place: same PC, same registers, same VRAM/palette content. The
        // animation only defers *when* the CPU starts, never changes *what* it sees.
        var reference = MakeSystem(mode);
        var animated = MakeSystem(mode);
        animated.ArmBootAnimation();
        await Assert.That(animated.BootAnimationActive).IsTrue();

        // While armed, RunFrame must not advance the CPU at all.
        var tBefore = animated.Cpu.TotalTCycles;
        int guard = 0;
        while (animated.BootAnimationActive && guard++ < 200)
        {
            animated.RunFrame();
            await Assert.That(animated.Cpu.TotalTCycles).IsEqualTo(tBefore);
        }
        await Assert.That(animated.BootAnimationActive).IsFalse();

        // Now hand-off has happened: VRAM/registers must match the un-animated system.
        await Assert
            .That(animated.Mmu.VramArray.ToArray())
            .IsEquivalentTo(reference.Mmu.VramArray.ToArray());
        await Assert.That(animated.Registers.Pc).IsEqualTo(reference.Registers.Pc);
        await Assert.That(animated.Registers.A).IsEqualTo(reference.Registers.A);
        await Assert.That(animated.Ppu.SCY).IsEqualTo(reference.Ppu.SCY);

        // And the CPU actually starts running on the next frame.
        animated.RunFrame();
        await Assert.That(animated.Cpu.TotalTCycles).IsNotEqualTo(tBefore);
    }

    [Test]
    public async Task Dmg_Boot_Animation_Actually_Scrolls_The_Logo_On_Screen()
    {
        // Prove the armed animation is visible, not just an internal frame
        // counter: SCY must move from its start position down toward 0
        // across frames (the logo scrolling up into view), and at least one
        // frame partway through must show non-white pixels in the logo's
        // fixed VRAM region -- i.e. the ROM's own header logo, once
        // non-blank, is actually drawn and visible on the framebuffer.
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        // Non-trivial header logo bytes so BootLogo.Decompress produces a
        // visible (non-blank) pattern.
        for (int i = 0; i < 48; i++)
            rom[0x104 + i] = (byte)(0xAA ^ i);
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.ArmBootAnimation();

        byte firstScy = gb.Ppu.SCY;
        await Assert.That(firstScy).IsGreaterThan((byte)0);

        bool sawLower = false;
        bool sawNonWhitePixel = false;
        int guard = 0;
        while (gb.BootAnimationActive && guard++ < 200)
        {
            gb.RunFrame();
            if (gb.Ppu.SCY < firstScy)
                sawLower = true;

            var fb = gb.Framebuffer.FrontArray;
            for (int p = 0; p < Framebuffer.Width * Framebuffer.Height; p++)
            {
                if (fb[p * 4] != 0xFF)
                {
                    sawNonWhitePixel = true;
                    break;
                }
            }
        }

        await Assert.That(sawLower).IsTrue();
        await Assert.That(sawNonWhitePixel).IsTrue();
        await Assert.That(gb.Ppu.SCY).IsEqualTo((byte)0);
    }
}
