using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Timer;

namespace Koh.Emulator.Core.Tests;

public class TimerTests
{
    private static void Tick(Timer.Timer timer, int tCycles, ref Interrupts interrupts)
    {
        for (int i = 0; i < tCycles; i++)
            timer.TickT(ref interrupts);
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
        timer.WriteTac(0b_0000_0100); // enable, freq 00 → 1024 T-cycles

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
        timer.WriteTac(0b_0000_0101); // enable, freq 01 → 16 T-cycles

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
        timer.WriteTac(0b_0000_0110); // enable, freq 10 → 64 T-cycles

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
        timer.WriteTac(0b_0000_0111); // enable, freq 11 → 256 T-cycles

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
        timer.WriteTac(0b_0000_0101); // enable, freq 01 (16 T-cycles per TIMA increment)
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
    public async Task Tima_Reads_Zero_During_Reload_Delay_Window()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101); // 16 T-cycles per increment
        timer.WriteTma(0x42);
        timer.WriteTima(0xFF);

        Tick(timer, 16, ref interrupts); // overflow
        // During the 4 T-cycle delay window TIMA reads 0x00 and no IRQ yet.
        for (int t = 0; t < 3; t++)
        {
            await Assert.That(timer.TIMA).IsEqualTo((byte)0);
            await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsFalse();
            Tick(timer, 1, ref interrupts);
        }
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0x42);
        await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsTrue();
    }

    [Test]
    public async Task Tima_Write_During_Reload_Delay_Cancels_Reload_And_Irq()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);
        timer.WriteTma(0x42);
        timer.WriteTima(0xFF);

        Tick(timer, 17, ref interrupts); // overflow + 1 T-cycle into the delay window
        timer.WriteTima(0x7F); // cancels the pending reload
        Tick(timer, 8, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0x7F);
        await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsFalse();
    }

    [Test]
    public async Task Tima_Write_On_Reload_Cycle_Is_Ignored()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);
        timer.WriteTma(0x42);
        timer.WriteTima(0xFF);

        Tick(timer, 20, ref interrupts); // overflow at 16, reload commits at 20
        timer.WriteTima(0x7F); // exact reload cycle: TMA reload wins
        await Assert.That(timer.TIMA).IsEqualTo((byte)0x42);
        await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsTrue();
    }

    [Test]
    public async Task Tma_Write_On_Reload_Cycle_Propagates_To_Tima()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);
        timer.WriteTma(0x42);
        timer.WriteTima(0xFF);

        Tick(timer, 20, ref interrupts); // reload commits on this cycle
        timer.WriteTma(0x99); // written on the reload cycle: copied into TIMA too
        await Assert.That(timer.TIMA).IsEqualTo((byte)0x99);
        await Assert.That(timer.TMA).IsEqualTo((byte)0x99);
    }

    [Test]
    public async Task Tma_Write_After_Reload_Cycle_Does_Not_Touch_Tima()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);
        timer.WriteTma(0x42);
        timer.WriteTima(0xFF);

        Tick(timer, 21, ref interrupts); // 1 T-cycle past the reload
        timer.WriteTma(0x99);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0x42);
        await Assert.That(timer.TMA).IsEqualTo((byte)0x99);
    }

    [Test]
    public async Task Tac_Disable_With_Selected_Bit_High_Glitch_Increments_Tima()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101); // bit 3, 16 T-cycles

        Tick(timer, 8, ref interrupts); // counter=8 → bit 3 is high
        timer.WriteTac(0b_0000_0001); // disable while selected bit is 1 → glitch increment
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tac_Enable_Right_After_Falling_Edge_Counts_The_Edge()
    {
        // MMIO writes land at the end of the M-cycle in this core, one T-cycle
        // after real hardware. Enabling the timer when the selected bit fell on
        // the very last tick must count that edge (Mooneye rapid_toggle).
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();

        Tick(timer, 16, ref interrupts); // bit 3 just fell (counter 15 → 16)
        timer.WriteTac(0b_0000_0101); // enable, freq 01 (bit 3)
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tac_Enable_Without_Recent_Falling_Edge_Does_Not_Increment()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();

        Tick(timer, 20, ref interrupts); // bit 3 fell 4 T-cycles ago
        timer.WriteTac(0b_0000_0101);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
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

    // --- DIV-APU (frame sequencer) falling-edge tests ----------------------
    // The APU frame sequencer is clocked by the falling edge of a fixed bit
    // of this SAME internal counter (bit 12 at normal speed, bit 13 in CGB
    // double-speed) rather than an independent counter, so DIV writes can
    // force a known frame-sequencer phase exactly like they can glitch TIMA.

    [Test]
    public async Task FrameSequencerFallingEdge_Fires_Once_Every_8192_TCycles_At_Normal_Speed()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        int edges = 0;
        timer.FrameSequencerFallingEdge += () => edges++;

        Tick(timer, 8191, ref interrupts);
        await Assert.That(edges).IsEqualTo(0);

        Tick(timer, 1, ref interrupts); // internal counter reaches 8192: bit 12 falls
        await Assert.That(edges).IsEqualTo(1);

        Tick(timer, 8192, ref interrupts);
        await Assert.That(edges).IsEqualTo(2);
    }

    [Test]
    public async Task FrameSequencerFallingEdge_Fires_Once_Every_16384_TCycles_In_Double_Speed()
    {
        // CGB double-speed: the DIV-APU tap moves to bit 13, so the falling
        // edge needs twice as many T-cycles (still ticked once per T-cycle
        // here — GameBoySystem is what doubles the M-cycle rate in wall time).
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        int edges = 0;
        timer.FrameSequencerFallingEdge += () => edges++;

        for (int i = 0; i < 16383; i++)
            timer.TickT(ref interrupts, doubleSpeed: true);
        await Assert.That(edges).IsEqualTo(0);

        timer.TickT(ref interrupts, doubleSpeed: true); // counter reaches 16384: bit 13 falls
        await Assert.That(edges).IsEqualTo(1);
    }

    [Test]
    public async Task WriteDiv_With_Bit_Set_Fires_An_Extra_FrameSequencer_Clock()
    {
        // Blargg dmg_sound's sync_apu-style trick: a DIV write resets the
        // whole counter, so if the DIV-APU bit (12) was set at the moment of
        // the write, that's a 1->0 falling edge and the frame sequencer gets
        // clocked immediately, ahead of its normal 8192-cycle schedule.
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        int edges = 0;
        timer.FrameSequencerFallingEdge += () => edges++;

        Tick(timer, 5000, ref interrupts); // bit 12 is set (4096 <= 5000 < 8192)
        await Assert.That(edges).IsEqualTo(0);

        timer.WriteDiv();
        await Assert.That(edges).IsEqualTo(1);
    }

    [Test]
    public async Task WriteDiv_With_Bit_Clear_Does_Not_Fire_An_Extra_FrameSequencer_Clock()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        int edges = 0;
        timer.FrameSequencerFallingEdge += () => edges++;

        Tick(timer, 2000, ref interrupts); // bit 12 is clear (2000 < 4096)
        timer.WriteDiv();
        await Assert.That(edges).IsEqualTo(0);
    }

    [Test]
    public async Task WriteDiv_Resets_FrameSequencer_Phase_So_Next_Edge_Is_A_Full_Period_Away()
    {
        var timer = new Timer.Timer();
        var interrupts = new Interrupts();
        int edges = 0;
        timer.FrameSequencerFallingEdge += () => edges++;

        Tick(timer, 5000, ref interrupts); // mid-way through the first 8192-cycle window
        timer.WriteDiv(); // resets the internal counter (and fires the extra clock above)
        edges = 0; // ignore the DIV-write's own extra clock; only care about the next one

        Tick(timer, 8191, ref interrupts);
        await Assert.That(edges).IsEqualTo(0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(edges).IsEqualTo(1);
    }
}
