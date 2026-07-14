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
        for (int i = 0; i < 0xA0; i++)
            gb.Mmu.WriteByte((ushort)(0xC000 + i), (byte)(i + 1));
        return gb;
    }

    [Test]
    public async Task Dma_Transfers_160_Bytes_To_Oam()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        // 4 start delay + 160*4 transfer = 644 T-cycles is enough.
        for (int i = 0; i < 648; i++)
            gb.OamDma.TickT();

        byte[] snapshot = new byte[0xA0];
        for (int i = 0; i < 0xA0; i++)
            snapshot[i] = gb.Mmu.OamArray[i];

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
        for (int i = 0; i < 10; i++)
            gb.OamDma.TickT();

        byte result = gb.Mmu.ReadByte(0xD000);
        await Assert.That(result).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Cpu_Read_From_Hram_Succeeds_During_Dma()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xFF90, 0x42);
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        for (int i = 0; i < 10; i++)
            gb.OamDma.TickT();

        byte result = gb.Mmu.ReadByte(0xFF90);
        await Assert.That(result).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task Cpu_Write_Outside_Hram_Is_Dropped_During_Dma()
    {
        // Mmu.WriteByte's OAM-DMA bus-contention branch: writes to external
        // memory (< $FF00) are silently dropped while the bus is locked.
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xD000, 0x11);
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        // Advance past the start delay so the bus is locked.
        for (int i = 0; i < 10; i++)
            gb.OamDma.TickT();

        gb.Mmu.WriteByte(0xD000, 0x99); // must be dropped
        // DebugRead bypasses DMA bus contention, so it reports the actual
        // underlying byte regardless of whether the write above landed.
        await Assert.That(gb.Mmu.DebugRead(0xD000)).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task Cpu_Write_To_Hram_Succeeds_During_Dma()
    {
        // HRAM stays writable while the bus is locked (only external memory
        // is contended).
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xFF90, 0x11);
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        for (int i = 0; i < 10; i++)
            gb.OamDma.TickT();

        gb.Mmu.WriteByte(0xFF90, 0x99);
        await Assert.That(gb.Mmu.DebugRead(0xFF90)).IsEqualTo((byte)0x99);
    }

    [Test]
    public async Task Retriggering_Dma_While_Running_Restarts_From_New_Source()
    {
        // OamDma.Trigger() unconditionally resets _byteIndex/countdowns, so a
        // second $FF46 write mid-transfer restarts the transfer from the new
        // source rather than continuing (or blending with) the old one.
        var gb = MakeSystem(); // $C000+i = i+1
        for (int i = 0; i < 0xA0; i++)
            gb.Mmu.WriteByte((ushort)(0xD000 + i), (byte)(i + 0x81));

        gb.Mmu.WriteByte(0xFF46, 0xC0); // start DMA from $C000
        for (int i = 0; i < 50; i++) // partway through the transfer
            gb.OamDma.TickT();

        gb.Mmu.WriteByte(0xFF46, 0xD0); // retrigger from $D000 mid-transfer

        // 4 start delay + 160*4 transfer is enough for the restarted DMA.
        for (int i = 0; i < 648; i++)
            gb.OamDma.TickT();

        for (int i = 0; i < 0xA0; i++)
        {
            await Assert.That(gb.Mmu.OamArray[i]).IsEqualTo((byte)(i + 0x81));
        }
    }

    /// <summary>
    /// Hardware quirk (Mooneye acceptance/oam_dma/sources-GS): a DMA source in
    /// $E000-$FFFF (echo/OAM/unmapped/IO/HRAM) aliases into WRAM $C000-$DFFF
    /// by dropping address bit 13, rather than reading through the normal bus
    /// decode for OAM/IO/HRAM.
    /// </summary>
    [Test]
    [Arguments((ushort)0xE000, (ushort)0xC000)] // plain echo
    [Arguments((ushort)0xFE00, (ushort)0xDE00)] // aliases into WRAM, not OAM itself
    [Arguments((ushort)0xFF00, (ushort)0xDF00)] // aliases into WRAM, not I/O
    public async Task Dma_From_High_Source_Aliases_Into_Wram(
        ushort source,
        ushort expectedWramAlias
    )
    {
        var gb = MakeSystem();
        for (int i = 0; i < 0xA0; i++)
            gb.Mmu.WriteByte((ushort)(expectedWramAlias + i), (byte)(0x80 + i));

        gb.Mmu.WriteByte(0xFF46, (byte)(source >> 8));
        for (int i = 0; i < 648; i++)
            gb.OamDma.TickT();

        for (int i = 0; i < 0xA0; i++)
        {
            await Assert.That(gb.Mmu.OamArray[i]).IsEqualTo((byte)(0x80 + i));
        }
    }
}
