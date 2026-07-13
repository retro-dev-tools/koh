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
        for (int i = 0; i < dots; i++)
            gb.Ppu.TickDot(ref gb.Io.Interrupts);
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

    [Test]
    public async Task Reenabling_Lcd_Starts_A_Fresh_Frame_At_OamScan()
    {
        // Advance mid-scanline, disable, tick a while (forced LY=0/HBlank per
        // Pan Docs "Enabling and disabling the LCD"), then re-enable. Real
        // hardware begins a fresh frame at line 0 in mode 2 (OAM scan) rather
        // than resuming wherever HBlank's forced state was left.
        var gb = MakeSystem();
        byte enabledLcdc = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;
        gb.Ppu.LCDC = enabledLcdc;

        Tick(gb, 200); // mid-scanline, well into Drawing/HBlank territory

        gb.Ppu.LCDC = LcdControl.BgWindowEnableOrPriority; // LCD off
        Tick(gb, 1000); // several forced-off ticks; would roll LY over if buggy

        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)0);

        gb.Ppu.LCDC = enabledLcdc; // re-enable
        Tick(gb, 1); // the rising-edge tick itself

        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)0);
        await Assert.That(gb.Ppu.Mode).IsEqualTo(PpuMode.OamScan);

        // And the fresh frame still times out normally from here.
        Tick(gb, 455);
        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)1);
    }
}
