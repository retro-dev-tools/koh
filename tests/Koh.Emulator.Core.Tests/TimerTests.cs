using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Timer;

namespace Koh.Emulator.Core.Tests;

public class TimerTests
{
    private static void Tick(Timer.Timer timer, int tCycles, ref Interrupts interrupts)
    {
        for (int i = 0; i < tCycles; i++) timer.TickT(ref interrupts);
    }

    [Test]
    public async Task Div_Increments_Every_256_TCycles()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();

        // Initial DIV is 0.
        await Assert.That(timer.DIV).IsEqualTo((byte)0);

        Tick(timer, 255, ref interrupts);
        await Assert.That(timer.DIV).IsEqualTo((byte)0);

        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.DIV).IsEqualTo((byte)1);

        Tick(timer, 256, ref interrupts);
        await Assert.That(timer.DIV).IsEqualTo((byte)2);
    }

    [Test]
    public async Task Tima_Tac00_Increments_Every_1024_TCycles()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0100);  // enable, freq 00 → 1024 T-cycles

        Tick(timer, 1023, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tima_Tac01_Increments_Every_16_TCycles()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);  // enable, freq 01 → 16 T-cycles

        Tick(timer, 15, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tima_Tac10_Increments_Every_64_TCycles()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0110);  // enable, freq 10 → 64 T-cycles

        Tick(timer, 63, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tima_Tac11_Increments_Every_256_TCycles()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0111);  // enable, freq 11 → 256 T-cycles

        Tick(timer, 255, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tima_Overflow_Reloads_From_Tma_After_Delay_And_Raises_Irq()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);  // enable, freq 01 (16 T-cycles per TIMA increment)
        timer.WriteTma(0x42);
        timer.WriteTima(0xFF);

        // 16 T-cycles to trigger TIMA overflow (TIMA → 0, reload-delay = 4)
        Tick(timer, 16, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsFalse();

        // 4 more T-cycles: reload happens, IRQ raised.
        Tick(timer, 4, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0x42);
        await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsTrue();
    }

    [Test]
    public async Task WriteDiv_Resets_Internal_Counter()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();

        Tick(timer, 500, ref interrupts);
        await Assert.That(timer.DIV).IsGreaterThan((byte)0);

        timer.WriteDiv();
        await Assert.That(timer.DIV).IsEqualTo((byte)0);
    }
}
