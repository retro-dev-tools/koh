using Koh.Emulator.Cartridge;

namespace Koh.Emulator.Tests;

/// <summary>
/// GameBoySystem is a pure executor: it never simulates the boot ROM, so a freshly constructed
/// instance carries no fabricated register or hand-off state -- it is fed a cartridge and starts
/// executing with whatever raw power-on state that implies. Providing correct code/data at
/// $0000+ (what a real boot ROM would have left behind) is the assembler/linker's job in a later
/// pass, not the emulator's. Replaces the deleted BootHandoffTests.cs, which locked in the
/// opposite (HLE hand-off) contract: fabricated CGB-detection registers, a decompressed Nintendo
/// logo drawn into VRAM, and a skippable boot animation.
/// </summary>
public class PowerOnStateTests
{
    private static GameBoySystem MakeSystem(HardwareMode mode)
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00; // RomOnly
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(cart, mode);
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task All_Cpu_Registers_Are_Zero_At_Construction(HardwareMode mode)
    {
        // No CGB-detection A=$11 (or DMG A=$01), no fabricated flags/BC/DE/HL -- every register
        // is left at CpuRegisters' own default, in both modes.
        var gb = MakeSystem(mode);
        var r = gb.Registers;
        await Assert.That(r.A).IsEqualTo((byte)0);
        await Assert.That(r.F).IsEqualTo((byte)0);
        await Assert.That(r.B).IsEqualTo((byte)0);
        await Assert.That(r.C).IsEqualTo((byte)0);
        await Assert.That(r.D).IsEqualTo((byte)0);
        await Assert.That(r.E).IsEqualTo((byte)0);
        await Assert.That(r.H).IsEqualTo((byte)0);
        await Assert.That(r.L).IsEqualTo((byte)0);
        await Assert.That(r.Sp).IsEqualTo((ushort)0);
        await Assert.That(r.Pc).IsEqualTo((ushort)0);
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task Vram_Wram_Oam_Hram_Remain_Poisoned_At_Construction(HardwareMode mode)
    {
        // Mmu fills VRAM/WRAM/OAM/HRAM with $FF at power-on to catch reads of never-written data
        // (see the Mmu constructor comment). GameBoySystem must never overlay a corrected
        // hand-off state on top -- not even the VRAM clear the real boot ROM performs.
        var gb = MakeSystem(mode);

        foreach (var b in gb.Mmu.VramArray)
            await Assert.That(b).IsEqualTo((byte)0xFF);

        await Assert.That(gb.DebugReadByte(0xC000)).IsEqualTo((byte)0xFF); // WRAM
        await Assert.That(gb.DebugReadByte(0xFE00)).IsEqualTo((byte)0xFF); // OAM
        await Assert.That(gb.DebugReadByte(0xFF80)).IsEqualTo((byte)0xFF); // HRAM
    }

    [Test]
    [Arguments(HardwareMode.Dmg)]
    [Arguments(HardwareMode.Cgb)]
    public async Task Bg_And_Obj_Palettes_Are_Not_Faded_To_White_At_Construction(HardwareMode mode)
    {
        // The real CGB boot ROM fades every BG palette to white ($7FFF) before hand-off; the
        // deleted hack reproduced that. CgbPalette's own raw power-on default (zeroed backing
        // array, i.e. BGR555 $0000) must survive untouched instead.
        var gb = MakeSystem(mode);
        for (int pal = 0; pal < 8; pal++)
        for (int slot = 0; slot < 4; slot++)
        {
            await Assert.That(gb.Ppu.BgPalette.GetColor(pal, slot)).IsEqualTo((ushort)0x0000);
            await Assert.That(gb.Ppu.ObjPalette.GetColor(pal, slot)).IsEqualTo((ushort)0x0000);
        }
    }
}
