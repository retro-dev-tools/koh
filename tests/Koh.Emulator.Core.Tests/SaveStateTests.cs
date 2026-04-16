using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Tests;

public class SaveStateTests
{
    private static (GameBoySystem gb, byte[] rom) MakeSystem(Action<byte[]>? patchRom = null)
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;  // RomOnly
        // Simple infinite loop at reset vector: JR -2 (0x18 0xFE)
        rom[0x100] = 0x18; rom[0x101] = 0xFE;
        patchRom?.Invoke(rom);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);
        return (gb, rom);
    }

    [Test]
    public async Task RoundTrip_Preserves_Cpu_Registers_And_Clock()
    {
        var (gb, rom) = MakeSystem();
        for (int i = 0; i < 50; i++) gb.StepInstruction();

        using var ms = new MemoryStream();
        SaveStateFile.Save(ms, gb, rom);

        var (gb2, _) = MakeSystem();
        ms.Position = 0;
        SaveStateFile.Load(ms, gb2, rom);

        await Assert.That(gb2.Registers.Pc).IsEqualTo(gb.Registers.Pc);
        await Assert.That(gb2.Registers.A).IsEqualTo(gb.Registers.A);
        await Assert.That(gb2.Registers.Sp).IsEqualTo(gb.Registers.Sp);
        await Assert.That(gb2.Cpu.TotalTCycles).IsEqualTo(gb.Cpu.TotalTCycles);
        await Assert.That(gb2.Clock.SystemTicks).IsEqualTo(gb.Clock.SystemTicks);
    }

    [Test]
    public async Task RoundTrip_Preserves_Memory_Contents()
    {
        var (gb, rom) = MakeSystem();
        // Stash marker bytes in WRAM + HRAM + OAM.
        gb.Mmu.WriteByte(0xC123, 0x42);
        gb.Mmu.WriteByte(0xFF85, 0xAB);
        gb.Mmu.WriteByte(0xFE10, 0x7E);

        using var ms = new MemoryStream();
        SaveStateFile.Save(ms, gb, rom);

        var (gb2, _) = MakeSystem();
        ms.Position = 0;
        SaveStateFile.Load(ms, gb2, rom);

        await Assert.That(gb2.Mmu.ReadByte(0xC123)).IsEqualTo((byte)0x42);
        await Assert.That(gb2.Mmu.ReadByte(0xFF85)).IsEqualTo((byte)0xAB);
        await Assert.That(gb2.Mmu.ReadByte(0xFE10)).IsEqualTo((byte)0x7E);
    }

    [Test]
    public async Task RoundTrip_Preserves_Io_Interrupts_And_Timer()
    {
        var (gb, rom) = MakeSystem();
        gb.Io.WriteIe(0x1F);
        gb.Io.Interrupts.IF = 0x03;
        gb.Mmu.WriteByte(0xFF07, 0x05);  // TAC enable + clock mode 1
        for (int i = 0; i < 200; i++) gb.StepInstruction();

        using var ms = new MemoryStream();
        SaveStateFile.Save(ms, gb, rom);

        var (gb2, _) = MakeSystem();
        ms.Position = 0;
        SaveStateFile.Load(ms, gb2, rom);

        await Assert.That(gb2.Io.ReadIe()).IsEqualTo(gb.Io.ReadIe());
        await Assert.That(gb2.Io.Interrupts.IF).IsEqualTo(gb.Io.Interrupts.IF);
        await Assert.That(gb2.Timer.TAC).IsEqualTo(gb.Timer.TAC);
        await Assert.That(gb2.Timer.DIV).IsEqualTo(gb.Timer.DIV);
    }

    [Test]
    public async Task Load_Rejects_Wrong_Rom_Hash()
    {
        var (gb, rom) = MakeSystem();
        using var ms = new MemoryStream();
        SaveStateFile.Save(ms, gb, rom);

        var differentRom = new byte[0x8000];
        differentRom[0x147] = 0x00;
        var (gb2, _) = MakeSystem();
        ms.Position = 0;
        await Assert.That(() =>
        {
            SaveStateFile.Load(ms, gb2, differentRom);
            return Task.CompletedTask;
        }).Throws<InvalidDataException>();
    }

    [Test]
    public async Task RoundTrip_Framebuffer_Determinism_After_10_Frames()
    {
        var (gb1, rom) = MakeSystem();
        for (int i = 0; i < 10; i++) gb1.RunFrame();

        using var ms = new MemoryStream();
        SaveStateFile.Save(ms, gb1, rom);

        var (gb2, _) = MakeSystem();
        ms.Position = 0;
        SaveStateFile.Load(ms, gb2, rom);

        gb1.RunFrame();
        gb2.RunFrame();

        var fb1 = gb1.Framebuffer.Front.ToArray();
        var fb2 = gb2.Framebuffer.Front.ToArray();
        await Assert.That(fb2.AsSpan().SequenceEqual(fb1)).IsTrue();
    }

    [Test]
    public async Task Load_Rejects_Bad_Magic()
    {
        var bad = new byte[40];
        using var ms = new MemoryStream(bad);
        var (gb2, rom) = MakeSystem();
        await Assert.That(() =>
        {
            SaveStateFile.Load(ms, gb2, rom);
            return Task.CompletedTask;
        }).Throws<InvalidDataException>();
    }
}
