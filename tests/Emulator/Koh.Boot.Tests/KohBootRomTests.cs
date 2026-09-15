using Koh.Emulator;
using Koh.Emulator.Boot;
using Koh.Emulator.Cartridge;

namespace Koh.Boot.Tests;

/// <summary>
/// The hand-off contract for Koh's own boot ROMs.
///
/// These assert observable state at $0100, not Nintendo's instruction sequence — Koh's
/// boot ROMs are an independent implementation. DIV at hand-off is deliberately NOT
/// asserted: it is a function of this boot ROM's own cycle count and will not match a
/// stock dump's.
/// </summary>
public class KohBootRomTests
{
    /// <summary>The 48-byte logo every cartridge carries at $0104-$0133.</summary>
    private static readonly byte[] NintendoLogo =
    [
        0xCE,
        0xED,
        0x66,
        0x66,
        0xCC,
        0x0D,
        0x00,
        0x0B,
        0x03,
        0x73,
        0x00,
        0x83,
        0x00,
        0x0C,
        0x00,
        0x0D,
        0x00,
        0x08,
        0x11,
        0x1F,
        0x88,
        0x89,
        0x00,
        0x0E,
        0xDC,
        0xCC,
        0x6E,
        0xE6,
        0xDD,
        0xDD,
        0xD9,
        0x99,
        0xBB,
        0xBB,
        0x67,
        0x63,
        0x6E,
        0x0E,
        0xEC,
        0xCC,
        0xDD,
        0xDC,
        0x99,
        0x9F,
        0xBB,
        0xB9,
        0x33,
        0x3E,
    ];

    /// <summary>A valid cartridge with one byte flipped after the header is built.</summary>
    private static Emulator.Cartridge.Cartridge MakeCorruptCart(int offset, bool cgbFlag)
    {
        var rom = MakeValidCart(cgbFlag).Rom.ToArray();
        rom[offset] ^= 0xFF;
        return CartridgeFactory.Load(rom);
    }

    /// <summary>A minimal valid cartridge: correct logo, correct header checksum.</summary>
    private static Emulator.Cartridge.Cartridge MakeValidCart(bool cgbFlag = false)
    {
        var rom = new byte[0x8000];
        rom[0x100] = 0x00; // nop
        rom[0x101] = 0xC3; // jp $0150
        rom[0x102] = 0x50;
        rom[0x103] = 0x01;
        NintendoLogo.CopyTo(rom.AsSpan(0x104));
        rom[0x143] = cgbFlag ? (byte)0x80 : (byte)0x00;
        rom[0x147] = 0x00; // RomOnly

        byte checksum = 0;
        for (int i = 0x0134; i <= 0x014C; i++)
            checksum = (byte)(checksum - rom[i] - 1);
        rom[0x14D] = checksum;

        rom[0x150] = 0x18; // jr -2: spin at the entry point
        rom[0x151] = 0xFE;
        return CartridgeFactory.Load(rom);
    }

    /// <summary>
    /// Step until the instant the boot ROM unmaps itself, and stop there.
    ///
    /// Instruction-stepping rather than RunFrame: the unmap happens mid-frame, and
    /// finishing the frame would let the cartridge run, so PC would read $0150 (where
    /// this fixture spins) instead of the $0100 the hand-off actually produces. What is
    /// under test is the state at hand-off, not one frame later.
    /// </summary>
    private static GameBoySystem RunToHandoff(
        HardwareMode mode = HardwareMode.Dmg,
        bool cgbCart = false,
        int maxInstructions = 5_000_000
    )
    {
        var gb = new GameBoySystem(MakeValidCart(cgbCart), mode);
        gb.LoadBootRom(
            BootRom.FromBytes(mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg)
        );

        for (int i = 0; i < maxInstructions; i++)
        {
            gb.StepInstruction();
            if (!gb.BootRomMapped)
                return gb;
        }

        throw new TimeoutException(
            $"Koh's {mode} boot ROM did not unmap within {maxInstructions} instructions "
                + $"(PC=${gb.Registers.Pc:X4}, SP=${gb.Registers.Sp:X4})."
        );
    }

    [Test]
    public async Task Dmg_Boot_Rom_Is_Exactly_256_Bytes()
    {
        // Not a style check. The final instruction must land at $00FE-$00FF so that PC,
        // having unmapped the overlay, falls into $0100. A 255- or 257-byte image cannot
        // hand off at all.
        await Assert.That(KohBootRoms.Dmg.Length).IsEqualTo(0x100);
    }

    [Test]
    public async Task Dmg_Boot_Rom_Ends_With_The_Ff50_Handoff()
    {
        // ld a,$01 ; ldh [$50],a — the last four bytes, in that order.
        await Assert.That(KohBootRoms.Dmg[0xFC]).IsEqualTo((byte)0x3E);
        await Assert.That(KohBootRoms.Dmg[0xFD]).IsEqualTo((byte)0x01);
        await Assert.That(KohBootRoms.Dmg[0xFE]).IsEqualTo((byte)0xE0);
        await Assert.That(KohBootRoms.Dmg[0xFF]).IsEqualTo((byte)0x50);
    }

    [Test]
    public async Task Dmg_Handoff_Leaves_The_Canonical_Register_State()
    {
        var gb = RunToHandoff();
        var r = gb.Registers;
        await Assert.That(r.Pc).IsEqualTo((ushort)0x0100);
        await Assert.That(r.Sp).IsEqualTo((ushort)0xFFFE);
        await Assert.That(r.A).IsEqualTo((byte)0x01);
        await Assert.That(r.F).IsEqualTo((byte)0xB0);
        await Assert.That(r.B).IsEqualTo((byte)0x00);
        await Assert.That(r.C).IsEqualTo((byte)0x13);
        await Assert.That(r.D).IsEqualTo((byte)0x00);
        await Assert.That(r.E).IsEqualTo((byte)0xD8);
        await Assert.That(r.H).IsEqualTo((byte)0x01);
        await Assert.That(r.L).IsEqualTo((byte)0x4D);
    }

    [Test]
    public async Task Dmg_Handoff_Leaves_The_Overlay_Unmapped()
    {
        var gb = RunToHandoff();
        await Assert.That(gb.BootRomMapped).IsFalse();
        // $0000 now reads the cartridge, not the boot ROM.
        await Assert.That(gb.DebugReadByte(0x0000)).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Dmg_Handoff_Leaves_The_Lcd_On_And_Vram_Cleared()
    {
        var gb = RunToHandoff();
        await Assert.That(gb.DebugReadByte(0xFF40) & 0x80).IsEqualTo(0x80); // LCDC bit 7
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)0xFC); // BGP
        await AssertVramClearedOutsideLogo(gb.Mmu.VramArray.AsSpan(0, 0x2000).ToArray());
    }

    /// <summary>
    /// VRAM was $FF-poisoned at power-on; the boot ROM cleared it and wrote only logo tiles
    /// 1-24 ($8010-$818F) and tilemap rows 8-9 ($9900-$993F).
    /// </summary>
    private static async Task AssertVramClearedOutsideLogo(byte[] vram)
    {
        await Assert.That(vram.AsSpan(0, 0x10).IndexOfAnyExcept((byte)0)).IsEqualTo(-1);
        await Assert
            .That(vram.AsSpan(0x190, 0x1900 - 0x190).IndexOfAnyExcept((byte)0))
            .IsEqualTo(-1);
        await Assert.That(vram.AsSpan(0x1940).IndexOfAnyExcept((byte)0)).IsEqualTo(-1);
    }

    private static async Task AssertNeverHandsOff(
        Emulator.Cartridge.Cartridge cart,
        HardwareMode mode
    )
    {
        var gb = new GameBoySystem(cart, mode);
        gb.LoadBootRom(
            BootRom.FromBytes(mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg)
        );
        for (int frame = 0; frame < 600; frame++)
            gb.RunFrame();
        // Still mapped after ~10s: the boot ROM locked up, as hardware does.
        await Assert.That(gb.BootRomMapped).IsTrue();
        await Assert.That(gb.DebugReadByte(0xFF40) & 0x80).IsEqualTo(0x80); // lock-up is visible
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task A_Bad_Logo_Locks_Up_Instead_Of_Booting(HardwareMode mode)
    {
        // A test ROM that would hang on a Game Boy must hang here too.
        await AssertNeverHandsOff(MakeCorruptCart(0x0104, mode == HardwareMode.Cgb), mode);
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task A_Bad_Header_Checksum_Locks_Up_Instead_Of_Booting(HardwareMode mode)
    {
        await AssertNeverHandsOff(MakeCorruptCart(0x014D, mode == HardwareMode.Cgb), mode);
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task The_Logo_Reaches_Vram_Because_Code_Put_It_There(HardwareMode mode)
    {
        var gb = RunToHandoff(mode, cgbCart: mode == HardwareMode.Cgb);
        var vram = gb.Mmu.VramArray;
        await Assert.That(vram.AsSpan(0x10, 0x180).IndexOfAnyExcept((byte)0)).IsNotEqualTo(-1);
        await Assert.That(vram[0x1904]).IsEqualTo((byte)1); // $9904: first logo tile
        await Assert.That(vram[0x192F]).IsEqualTo((byte)24); // $992F: last logo tile
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)0); // SCY landed
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task The_Scroll_Takes_Roughly_The_Hardware_Duration(HardwareMode mode)
    {
        // ~2.5s at ~60fps. Wide bounds: "there is an animation" and "not absurdly long".
        var gb = new GameBoySystem(MakeValidCart(mode == HardwareMode.Cgb), mode);
        gb.LoadBootRom(
            BootRom.FromBytes(mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg)
        );
        int frames = 0;
        while (gb.BootRomMapped && frames < 400)
        {
            gb.RunFrame();
            frames++;
        }
        await Assert.That(frames).IsGreaterThan(100);
        await Assert.That(frames).IsLessThan(250);
    }

    [Test]
    public async Task The_Chime_Leaves_The_Apu_Enabled_At_Handoff()
    {
        var gb = RunToHandoff();
        await Assert.That(gb.DebugReadByte(0xFF26) & 0x80).IsEqualTo(0x80); // NR52 on
    }

    [Test]
    public async Task Dmg_Boot_Rom_Is_A_Valid_BootRom_Blob()
    {
        // The size/family contract Koh.Emulator enforces, checked against the real
        // artifact rather than a stub.
        var rom = BootRom.FromBytes(KohBootRoms.Dmg);
        await Assert.That(rom.Family).IsEqualTo(BootRomFamily.Dmg);
    }

    [Test]
    public async Task Cgb_Boot_Rom_Is_A_Valid_Cgb_Blob()
    {
        var rom = BootRom.FromBytes(KohBootRoms.Cgb);
        await Assert.That(rom.Family).IsEqualTo(BootRomFamily.Cgb);
    }

    [Test]
    public async Task Cgb_Handoff_Leaves_The_Canonical_Register_State()
    {
        var gb = RunToHandoff(HardwareMode.Cgb, cgbCart: true);
        var r = gb.Registers;
        await Assert.That(r.Pc).IsEqualTo((ushort)0x0100);
        await Assert.That(r.Sp).IsEqualTo((ushort)0xFFFE);
        await Assert.That(r.A).IsEqualTo((byte)0x11);
        await Assert.That(r.F).IsEqualTo((byte)0x80);
        await Assert.That(r.B).IsEqualTo((byte)0x00);
        await Assert.That(r.C).IsEqualTo((byte)0x00);
        await Assert.That(r.D).IsEqualTo((byte)0xFF);
        await Assert.That(r.E).IsEqualTo((byte)0x56);
        await Assert.That(r.H).IsEqualTo((byte)0x00);
        await Assert.That(r.L).IsEqualTo((byte)0x0D);
    }

    [Test]
    public async Task Cgb_Running_A_Dmg_Cartridge_Sets_B_To_One()
    {
        // A=$11 with B=$01 is how a CGB game learns it was handed a DMG cartridge.
        var gb = RunToHandoff(HardwareMode.Cgb, cgbCart: false);
        await Assert.That(gb.Registers.A).IsEqualTo((byte)0x11);
        await Assert.That(gb.Registers.B).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task Cgb_Handoff_Leaves_Palettes_Set_And_Both_Vram_Banks_Cleared()
    {
        var gb = RunToHandoff(HardwareMode.Cgb, cgbCart: true);
        await Assert.That(gb.Ppu.BgPalette.GetColor(7, 0)).IsEqualTo((ushort)0x7FFF);
        await Assert.That(gb.Ppu.ObjPalette.GetColor(7, 1)).IsEqualTo((ushort)0x294A);
        await Assert.That(gb.Mmu.VramArray.AsSpan(0x2000).IndexOfAnyExcept((byte)0)).IsEqualTo(-1);
        await AssertVramClearedOutsideLogo(gb.Mmu.VramArray.AsSpan(0, 0x2000).ToArray());
        await Assert.That(gb.DebugReadByte(0xFF40)).IsEqualTo((byte)0x91);
    }

    [Test]
    public async Task Cgb_Machine_Rejects_Koh_Dmg_Boot_Rom()
    {
        var gb = new GameBoySystem(MakeValidCart(cgbFlag: true), HardwareMode.Cgb);
        await Assert
            .That(() => gb.LoadBootRom(BootRom.FromBytes(KohBootRoms.Dmg)))
            .Throws<ArgumentException>();
        await Assert.That(gb.BootRomMapped).IsFalse();
    }
}
