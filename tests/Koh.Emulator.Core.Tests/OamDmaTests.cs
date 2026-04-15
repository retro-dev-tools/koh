using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class OamDmaTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);
        for (int i = 0; i < 0xA0; i++) gb.Mmu.WriteByte((ushort)(0xC000 + i), (byte)(i + 1));
        return gb;
    }

    [Test]
    public async Task Dma_Transfers_160_Bytes_To_Oam()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        // 4 start delay + 160*4 transfer = 644 T-cycles is enough.
        for (int i = 0; i < 648; i++) gb.OamDma.TickT();

        byte[] snapshot = new byte[0xA0];
        for (int i = 0; i < 0xA0; i++) snapshot[i] = gb.Mmu.OamArray[i];

        for (int i = 0; i < 0xA0; i++)
        {
            await Assert.That(snapshot[i]).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task Cpu_Read_Outside_Hram_Returns_FF_During_Dma()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xD000, 0x42);
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        // Advance past start delay.
        for (int i = 0; i < 10; i++) gb.OamDma.TickT();

        byte result = gb.Mmu.ReadByte(0xD000);
        await Assert.That(result).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Cpu_Read_From_Hram_Succeeds_During_Dma()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xFF90, 0x42);
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        for (int i = 0; i < 10; i++) gb.OamDma.TickT();

        byte result = gb.Mmu.ReadByte(0xFF90);
        await Assert.That(result).IsEqualTo((byte)0x42);
    }
}
