using Koh.Debugger.Session;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;

namespace Koh.Debugger.Tests;

public class ConditionalBreakpointTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0x18; rom[0x101] = 0xFE;   // JR -2
        return new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
    }

    [Test]
    public async Task ExpressionEvaluator_Register_Equals_Literal()
    {
        var gb = MakeSystem();
        gb.Registers.A = 0x42;
        await Assert.That(ExpressionEvaluator.Evaluate("A == $42", gb)).IsTrue();
        await Assert.That(ExpressionEvaluator.Evaluate("A != $42", gb)).IsFalse();
        await Assert.That(ExpressionEvaluator.Evaluate("A > 0x40", gb)).IsTrue();
    }

    [Test]
    public async Task ExpressionEvaluator_Memory_Deref_Via_HL()
    {
        var gb = MakeSystem();
        gb.Registers.HL = 0xC100;
        gb.Mmu.WriteByte(0xC100, 0x99);
        await Assert.That(ExpressionEvaluator.Evaluate("[HL] == $99", gb)).IsTrue();
        await Assert.That(ExpressionEvaluator.Evaluate("[$C100] == 153", gb)).IsTrue();
    }

    [Test]
    public async Task ExpressionEvaluator_Malformed_Returns_False()
    {
        var gb = MakeSystem();
        await Assert.That(ExpressionEvaluator.Evaluate("garbage", gb)).IsFalse();
        await Assert.That(ExpressionEvaluator.Evaluate("A ? 5", gb)).IsFalse();
    }

    [Test]
    public async Task BreakpointManager_HitCount_Target_Defers_Break()
    {
        var mgr = new BreakpointManager();
        var addr = new BankedAddress(0, 0x200);
        mgr.Add(addr, condition: null, hitCountTarget: 3);

        await Assert.That(mgr.ShouldBreak(addr, null)).IsFalse();
        await Assert.That(mgr.ShouldBreak(addr, null)).IsFalse();
        await Assert.That(mgr.ShouldBreak(addr, null)).IsTrue();
    }

    [Test]
    public async Task BreakpointManager_Condition_Gates_Break()
    {
        var mgr = new BreakpointManager();
        var addr = new BankedAddress(0, 0x200);
        mgr.Add(addr, condition: "A == 1", hitCountTarget: 0);

        int aValue = 0;
        Func<string, bool> eval = _ => aValue == 1;

        await Assert.That(mgr.ShouldBreak(addr, eval)).IsFalse();
        aValue = 1;
        await Assert.That(mgr.ShouldBreak(addr, eval)).IsTrue();
    }
}
