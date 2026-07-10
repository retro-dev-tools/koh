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

        for (int i = 0; i < 16; i++)
            gb.Mmu.WriteByte((ushort)(0xC000 + i), (byte)(i + 1));

        gb.Mmu.WriteByte(0xFF51, 0xC0);
        gb.Mmu.WriteByte(0xFF52, 0x00);
        gb.Mmu.WriteByte(0xFF53, 0x80);
        gb.Mmu.WriteByte(0xFF54, 0x00);
        gb.Mmu.WriteByte(0xFF55, 0x00); // arm: 1 block = 16 bytes, general-purpose

        // Arming a GP transfer halts the CPU; the system drives it block-by-block.
        await Assert.That(gb.Hdma.CpuHaltedByGp).IsTrue();
        gb.Hdma.TransferOneGpBlock();
        await Assert.That(gb.Hdma.CpuHaltedByGp).IsFalse();

        byte[] actual = new byte[16];
        for (int i = 0; i < 16; i++)
            actual[i] = gb.Mmu.ReadByte((ushort)(0x8000 + i));

        for (int i = 0; i < 16; i++)
        {
            await Assert.That(actual[i]).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task GeneralPurpose_Freezes_Cpu_And_Advances_Ppu_32_Dots_Per_Block()
    {
        var gb = MakeCgbSystem();

        // LCD off so VRAM is freely readable for the byte check and PPU mode
        // never gates it; the system clock still advances one tick per dot
        // regardless of LCD state.
        gb.Mmu.WriteByte(0xFF40, 0x00);

        // 16 source bytes at $C100 (clear of the code we place at $C000).
        for (int i = 0; i < 16; i++)
            gb.Mmu.WriteByte((ushort)(0xC100 + i), (byte)(i + 1));

        // HDMA: source $C100 -> dest $8000.
        gb.Mmu.WriteByte(0xFF51, 0xC1);
        gb.Mmu.WriteByte(0xFF52, 0x00);
        gb.Mmu.WriteByte(0xFF53, 0x80);
        gb.Mmu.WriteByte(0xFF54, 0x00);

        // Program at $C000:  LD A,$00 ; LDH ($55),A   (A=$00 -> 1 block, GP DMA).
        gb.Mmu.WriteByte(0xC000, 0x3E); // LD A,d8
        gb.Mmu.WriteByte(0xC001, 0x00);
        gb.Mmu.WriteByte(0xC002, 0xE0); // LDH (a8),A
        gb.Mmu.WriteByte(0xC003, 0x55);
        gb.Registers.Pc = 0xC000;

        gb.StepInstruction(); // LD A,$00
        ulong before = gb.Clock.SystemTicks;
        gb.StepInstruction(); // LDH ($55),A -> triggers GDMA
        ulong elapsed = gb.Clock.SystemTicks - before;

        // LDH (a8),A is 3 M-cycles = 12 dots; one GDMA block costs 32 dots
        // (Pan Docs: ~8 µs / 16 bytes in every speed mode). 12 + 32 = 44.
        await Assert.That(elapsed).IsEqualTo(44ul);

        // CPU stays frozen for the whole transfer: PC only advances past the LDH.
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0xC004);

        // ...and the 16 bytes actually landed in VRAM.
        for (int i = 0; i < 16; i++)
            await Assert.That(gb.Mmu.ReadByte((ushort)(0x8000 + i))).IsEqualTo((byte)(i + 1));
    }

    [Test]
    public async Task GeneralPurpose_Vram_Write_During_Mode3_Is_Dropped()
    {
        var gb = MakeCgbSystem();

        // Seed the destination sentinel with LCD off (VRAM freely writable).
        gb.Mmu.WriteByte(0xFF40, 0x00);
        gb.Mmu.WriteByte(0x8000, 0xEE);
        for (int i = 0; i < 16; i++)
            gb.Mmu.WriteByte((ushort)(0xC100 + i), 0x55);
        gb.Mmu.WriteByte(0xFF51, 0xC1);
        gb.Mmu.WriteByte(0xFF52, 0x00);
        gb.Mmu.WriteByte(0xFF53, 0x80);
        gb.Mmu.WriteByte(0xFF54, 0x00);

        // Turn the LCD on and spin (JR -2) until the PPU is mid-Drawing (mode 3).
        gb.Mmu.WriteByte(0xFF40, 0x91);
        gb.Mmu.WriteByte(0xC000, 0x18); // JR -2 (tight self-loop)
        gb.Mmu.WriteByte(0xC001, 0xFE);
        gb.Registers.Pc = 0xC000;
        int guard = 0;
        while (gb.Ppu.Mode != Koh.Emulator.Core.Ppu.PpuMode.Drawing && guard++ < 4000)
            gb.StepInstruction();
        await Assert.That(gb.Ppu.Mode).IsEqualTo(Koh.Emulator.Core.Ppu.PpuMode.Drawing);

        // Arm a 1-block GP transfer and run the block now, in mode 3.
        gb.Mmu.WriteByte(0xFF55, 0x00);
        gb.Hdma.TransferOneGpBlock();

        // Real CGB hardware drops VRAM writes that land during mode 3 (the DMA
        // engine shares the CPU bus path) — the destination keeps its old value.
        await Assert.That(gb.Mmu.VramArray[0]).IsEqualTo((byte)0xEE);
    }

    [Test]
    public async Task HBlank_Transfer_Needs_HBlank_Trigger()
    {
        var gb = MakeCgbSystem();

        for (int i = 0; i < 16; i++)
            gb.Mmu.WriteByte((ushort)(0xC000 + i), 0x55);

        gb.Mmu.WriteByte(0xFF51, 0xC0);
        gb.Mmu.WriteByte(0xFF52, 0x00);
        gb.Mmu.WriteByte(0xFF53, 0x80);
        gb.Mmu.WriteByte(0xFF54, 0x00);
        gb.Mmu.WriteByte(0xFF55, 0x80); // bit 7 = HBlank mode

        // Without HBlank trigger, no bytes should transfer.
        for (int i = 0; i < 32; i++)
            gb.Hdma.TickT();
        byte untransferred = gb.Mmu.ReadByte(0x8000);
        await Assert.That(untransferred).IsNotEqualTo((byte)0x55);

        // Trigger HBlank then tick enough T-cycles to move one block.
        gb.Hdma.OnHBlankEntered();
        for (int i = 0; i < 32; i++)
            gb.Hdma.TickT();

        byte transferred = gb.Mmu.ReadByte(0x8000);
        await Assert.That(transferred).IsEqualTo((byte)0x55);
    }
}
