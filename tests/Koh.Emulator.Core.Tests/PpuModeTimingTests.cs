using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core.Tests;

public class PpuModeTimingTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    private static void Tick(GameBoySystem gb, int dots)
    {
        for (int i = 0; i < dots; i++) gb.Ppu.TickDot(ref gb.Io.Interrupts);
    }

    [Test]
    public async Task OamScan_Lasts_80_Dots_At_Scanline_Start()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;

        Tick(gb, 79);
        var modeAfter79 = gb.Ppu.Mode;
        Tick(gb, 1);
        var modeAfter80 = gb.Ppu.Mode;

        await Assert.That(modeAfter79).IsEqualTo(PpuMode.OamScan);
        await Assert.That(modeAfter80).IsEqualTo(PpuMode.Drawing);
    }

    [Test]
    public async Task Scanline_Total_Is_456_Dots()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;

        Tick(gb, 456);
        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)1);
    }

    [Test]
    public async Task VBlank_Starts_At_LY_144_And_Raises_Irq()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;

        Tick(gb, 144 * 456);
        byte ly = gb.Ppu.LY;
        bool vblankRaised = (gb.Io.Interrupts.IF & Interrupts.VBlank) != 0;

        await Assert.That(ly).IsEqualTo((byte)144);
        await Assert.That(vblankRaised).IsTrue();
    }

    [Test]
    public async Task Frame_Wraps_To_LY_0_After_VBlank()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;

        Tick(gb, 154 * 456);
        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)0);
    }
}
