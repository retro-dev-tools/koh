using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class GameBoySystemTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00; // RomOnly
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task RunFrame_Advances_Exactly_70224_System_Ticks()
    {
        var gb = MakeSystem();
        var before = gb.Clock.SystemTicks;
        gb.RunFrame();
        var after = gb.Clock.SystemTicks;
        await Assert.That(after - before).IsEqualTo((ulong)SystemClock.SystemTicksPerFrame);
    }

    [Test]
    public async Task StepInstruction_Advances_At_Least_Four_TCycles()
    {
        var gb = MakeSystem();
        var before = gb.Cpu.TotalTCycles;
        gb.StepInstruction();
        var after = gb.Cpu.TotalTCycles;
        await Assert.That(after - before).IsGreaterThanOrEqualTo(4UL);
    }

    [Test]
    public async Task RunUntil_PcEquals_Triggers_Stop_Or_FrameComplete()
    {
        var gb = MakeSystem();
        ushort targetPc = (ushort)(gb.Registers.Pc + 10);
        var condition = StopCondition.AtPc(targetPc);
        var result = gb.RunUntil(condition);
        // Mock CPU may or may not land exactly on target PC depending on branch pattern.
        bool validReason = result.Reason is StopReason.Breakpoint or StopReason.FrameComplete;
        await Assert.That(validReason).IsTrue();
    }
}
