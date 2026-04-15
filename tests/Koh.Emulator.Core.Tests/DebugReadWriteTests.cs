using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class DebugReadWriteTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x0100] = 0x42;
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task DebugReadByte_Rom()
    {
        var gb = MakeSystem();
        await Assert.That(gb.DebugReadByte(0x0100)).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task DebugWriteByte_When_Running_Returns_False()
    {
        var gb = MakeSystem();
        gb.SetRunningForTest(true);
        bool ok = gb.DebugWriteByte(0xC000, 0x55);
        await Assert.That(ok).IsFalse();
        gb.SetRunningForTest(false);
    }

    [Test]
    public async Task DebugWriteByte_Wram_When_Paused_Succeeds()
    {
        var gb = MakeSystem();
        bool ok = gb.DebugWriteByte(0xC000, 0x55);
        await Assert.That(ok).IsTrue();
        await Assert.That(gb.DebugReadByte(0xC000)).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task DebugWriteByte_Rom_Patches_Backing_Buffer()
    {
        var gb = MakeSystem();
        bool ok = gb.DebugWriteByte(0x0100, 0xAA);
        await Assert.That(ok).IsTrue();
        await Assert.That(gb.DebugReadByte(0x0100)).IsEqualTo((byte)0xAA);
    }
}
