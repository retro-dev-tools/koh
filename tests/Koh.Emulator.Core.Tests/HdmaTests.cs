using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class HdmaTests
{
    private static GameBoySystem MakeCgbSystem()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x80;
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Cgb, cart);
    }

    [Test]
    public async Task GeneralPurpose_Transfers_16_Bytes()
    {
        var gb = MakeCgbSystem();

        for (int i = 0; i < 16; i++) gb.Mmu.WriteByte((ushort)(0xC000 + i), (byte)(i + 1));

        gb.Mmu.WriteByte(0xFF51, 0xC0);
        gb.Mmu.WriteByte(0xFF52, 0x00);
        gb.Mmu.WriteByte(0xFF53, 0x80);
        gb.Mmu.WriteByte(0xFF54, 0x00);
        gb.Mmu.WriteByte(0xFF55, 0x00);  // 1 block = 16 bytes, general-purpose

        for (int i = 0; i < 32; i++) gb.Hdma.TickT();

        byte[] actual = new byte[16];
        for (int i = 0; i < 16; i++) actual[i] = gb.Mmu.ReadByte((ushort)(0x8000 + i));

        for (int i = 0; i < 16; i++)
        {
            await Assert.That(actual[i]).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task HBlank_Transfer_Needs_HBlank_Trigger()
    {
        var gb = MakeCgbSystem();

        for (int i = 0; i < 16; i++) gb.Mmu.WriteByte((ushort)(0xC000 + i), 0x55);

        gb.Mmu.WriteByte(0xFF51, 0xC0);
        gb.Mmu.WriteByte(0xFF52, 0x00);
        gb.Mmu.WriteByte(0xFF53, 0x80);
        gb.Mmu.WriteByte(0xFF54, 0x00);
        gb.Mmu.WriteByte(0xFF55, 0x80);  // bit 7 = HBlank mode

        // Without HBlank trigger, no bytes should transfer.
        for (int i = 0; i < 32; i++) gb.Hdma.TickT();
        byte untransferred = gb.Mmu.ReadByte(0x8000);
        await Assert.That(untransferred).IsNotEqualTo((byte)0x55);

        // Trigger HBlank then tick enough T-cycles to move one block.
        gb.Hdma.OnHBlankEntered();
        for (int i = 0; i < 32; i++) gb.Hdma.TickT();

        byte transferred = gb.Mmu.ReadByte(0x8000);
        await Assert.That(transferred).IsEqualTo((byte)0x55);
    }
}
