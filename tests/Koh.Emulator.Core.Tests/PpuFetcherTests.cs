using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core.Tests;

public class PpuFetcherTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);
        gb.Ppu.LCDC = (byte)(LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority | LcdControl.BgWindowTileDataArea);
        gb.Ppu.BGP = 0b_11_10_01_00;  // identity palette
        return gb;
    }

    private static void WriteVram(GameBoySystem gb, ushort address, byte value)
        => gb.Mmu.WriteByte(address, value);

    [Test]
    public async Task Single_Tile_Produces_Expected_Pixels_On_Scanline_0()
    {
        var gb = MakeSystem();

        // Tile 0 at $8000 — row 0 = colors 0,1,2,3,0,1,2,3
        WriteVram(gb, 0x8000, 0b01010101);
        WriteVram(gb, 0x8001, 0b00110011);

        // BG tile map at $9800 — entry 0 points to tile 0
        WriteVram(gb, 0x9800, 0);

        for (int i = 0; i < 456; i++) gb.Ppu.TickDot(ref gb.Io.Interrupts);

        byte[] expectedShades = { 0xE0, 0xA8, 0x58, 0x08, 0xE0, 0xA8, 0x58, 0x08 };
        byte[] actual = new byte[8];
        for (int x = 0; x < 8; x++)
        {
            actual[x] = gb.Framebuffer.Back[x * 4];
        }

        for (int x = 0; x < 8; x++)
        {
            await Assert.That(actual[x]).IsEqualTo(expectedShades[x]);
        }
    }
}
