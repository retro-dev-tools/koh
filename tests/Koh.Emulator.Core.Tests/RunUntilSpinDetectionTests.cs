using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class RunUntilSpinDetectionTests
{
    // A hand-assembled program at the entry point ($0100):
    //
    //   0x100:  01 00 20     LD BC, 0x2000        ; BC = 8192, outer counter
    //   0x103:  00           NOP                  ; loop body start (7 distinct post-step
    //   0x104:  00           NOP                  ; addresses, comfortably above
    //   0x105:  00           NOP                  ; DefaultSpinPcSetSize=4, so this "big init
    //   0x106:  0B           DEC BC               ; loop" never false-triggers spin detection no
    //   0x107:  78           LD A, B              ; matter how many of its 8192 iterations run)
    //   0x108:  B1           OR C
    //   0x109:  20 F8        JR NZ, -8            ; back to 0x103 while BC != 0
    //   0x10B:  3E 42        LD A, 0x42           ; init loop is done: write a marker byte...
    //   0x10D:  EA 00 C0     LD (0xC000), A       ; ...to WRAM $C000 (only reachable AFTER the
    //                                             ; loop)
    //   0x110:  18 FE        JR $                 ; terminal spin: self-jump, one address forever
    private static GameBoySystem MakeSpinningSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00; // RomOnly

        int pc = 0x100;
        rom[pc++] = 0x01;
        rom[pc++] = 0x00;
        rom[pc++] = 0x20; // LD BC, 0x2000
        rom[pc++] = 0x00; // NOP
        rom[pc++] = 0x00; // NOP
        rom[pc++] = 0x00; // NOP
        rom[pc++] = 0x0B; // DEC BC
        rom[pc++] = 0x78; // LD A, B
        rom[pc++] = 0xB1; // OR C
        rom[pc++] = 0x20;
        rom[pc++] = 0xF8; // JR NZ, -8 (-> 0x103)
        rom[pc++] = 0x3E;
        rom[pc++] = 0x42; // LD A, 0x42
        rom[pc++] = 0xEA;
        rom[pc++] = 0x00;
        rom[pc++] = 0xC0; // LD (0xC000), A
        rom[pc++] = 0x18;
        rom[pc++] = 0xFE; // JR $ (-> 0x110)

        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    private static GameBoySystem MakeAllNopSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00; // RomOnly
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task RunUntil_Spinning_StopsAtTerminalSpin_PastInitLoop()
    {
        var gb = MakeSpinningSystem();

        // Generous cap — comfortably more than the ~8192-iteration init loop plus the
        // ~4096-instruction spin-detection window needs.
        var result = gb.RunUntil(StopCondition.SpinningOrBudget(500_000));

        await Assert.That(result.Reason).IsEqualTo(StopReason.Spinning);
        await Assert.That(result.FinalPc).IsEqualTo((ushort)0x0110);
        await Assert.That(gb.DebugReadByte(0xC000)).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task RunUntil_Spinning_TooShortCap_ReturnsBudgetExceeded()
    {
        var gb = MakeSpinningSystem();

        // A cap that expires while still deep in the init loop, long before either the loop
        // finishes or the spin-detection window could accumulate.
        var result = gb.RunUntil(StopCondition.SpinningOrBudget(2_000));

        await Assert.That(result.Reason).IsEqualTo(StopReason.BudgetExceeded);
    }

    [Test]
    public async Task RunUntil_PlainPcCondition_WithoutSpinningOrMaxCycles_StaysSingleFrame()
    {
        var gb = MakeAllNopSystem();

        // No Spinning/MaxCycles flag: never hit, so this must preserve the pre-existing
        // single-frame contract instead of looping across frames forever.
        var condition = StopCondition.AtPc(0xFFFF);
        var result = gb.RunUntil(condition);

        await Assert.That(result.Reason).IsEqualTo(StopReason.FrameComplete);
    }
}
