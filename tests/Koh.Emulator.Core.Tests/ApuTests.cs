using Koh.Emulator.Core.Apu;

namespace Koh.Emulator.Core.Tests;

public class ApuTests
{
    [Test]
    public async Task FrameSequencer_Step_Pattern_Fires_Expected_Clocks()
    {
        var fs = new FrameSequencer();
        int lenClocks = 0, sweepClocks = 0, envClocks = 0;
        fs.LengthClock += () => lenClocks++;
        fs.SweepClock += () => sweepClocks++;
        fs.EnvelopeClock += () => envClocks++;

        // Over 8 steps: length fires 4x (steps 0,2,4,6), sweep 2x (2,6), envelope 1x (7).
        for (int i = 0; i < 8; i++) fs.Advance();

        await Assert.That(lenClocks).IsEqualTo(4);
        await Assert.That(sweepClocks).IsEqualTo(2);
        await Assert.That(envClocks).IsEqualTo(1);
    }

    [Test]
    public async Task SquareChannel_Produces_Nonzero_Output_After_Trigger()
    {
        var ch = new SquareChannel(hasSweep: false);
        // Duty 50%, volume F, increase=0, period=3, freq 0x0000.
        ch.Trigger(nrx0: 0, nrx1: 0b_10_000000, nrx2: 0xF3, nrx3: 0x00, nrx4: 0x87);
        int maxOut = 0;
        for (int i = 0; i < 20000; i++) { ch.TickT(); maxOut = Math.Max(maxOut, ch.Output()); }
        await Assert.That(maxOut).IsGreaterThan(0);
    }

    [Test]
    public async Task LengthCounter_Disables_When_Reaches_Zero()
    {
        var len = new LengthCounter(maxLength: 64) { Counter = 2, Enabled = true };
        bool disabled = false;
        len.Tick(() => disabled = true);
        await Assert.That(disabled).IsFalse();
        len.Tick(() => disabled = true);
        await Assert.That(disabled).IsTrue();
    }

    [Test]
    public async Task VolumeEnvelope_Increases_Toward_15()
    {
        var env = new VolumeEnvelope();
        env.Trigger(0x04 | 0x08);   // start at 0, increase, period=4
        for (int i = 0; i < 100; i++) env.Tick();
        await Assert.That(env.Volume).IsEqualTo(15);
    }

    [Test]
    public async Task NoiseChannel_Output_Varies()
    {
        var ch = new NoiseChannel();
        ch.Trigger(nr41: 0, nr42: 0xF0, nr43: 0x00, nr44: 0x80);
        bool sawZero = false, sawOne = false;
        for (int i = 0; i < 1000; i++)
        {
            ch.TickT();
            if (ch.Output() == 0) sawZero = true;
            else sawOne = true;
        }
        await Assert.That(sawZero).IsTrue();
        await Assert.That(sawOne).IsTrue();
    }

    [Test]
    public async Task WaveChannel_Emits_Samples_From_Pattern()
    {
        var ch = new WaveChannel();
        for (int i = 0; i < 16; i++) ch.WavePattern[i] = 0xF0;
        ch.Trigger(nr30: 0x80, nr31: 0, nr32: 0x20, nr33: 0, nr34: 0x80);
        int maxOut = 0;
        for (int i = 0; i < 20000; i++) { ch.TickT(); maxOut = Math.Max(maxOut, ch.Output()); }
        await Assert.That(maxOut).IsGreaterThan(0);
    }

    [Test]
    public async Task AudioSampleBuffer_Push_And_Drain_Roundtrip()
    {
        var buf = new AudioSampleBuffer();
        for (short i = 0; i < 100; i++) buf.Push(i);
        await Assert.That(buf.Available).IsEqualTo(100);

        var dst = new short[100];
        int n = buf.Drain(dst);
        await Assert.That(n).IsEqualTo(100);
        await Assert.That(dst[0]).IsEqualTo((short)0);
        await Assert.That(dst[99]).IsEqualTo((short)99);
        await Assert.That(buf.Available).IsEqualTo(0);
    }

    [Test]
    public async Task Apu_Disabled_Does_Not_Tick_Channels()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu { Enabled = false };
        for (int i = 0; i < 10_000; i++) apu.TickT();
        await Assert.That(apu.SampleBuffer.Available).IsEqualTo(0);
    }

    [Test]
    public async Task Apu_Enabled_Produces_Samples()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu { Enabled = true };
        apu.Ch1.Trigger(nrx0: 0, nrx1: 0b_10_000000, nrx2: 0xF3, nrx3: 0x00, nrx4: 0x87);
        for (int i = 0; i < 20_000; i++) apu.TickT();
        await Assert.That(apu.SampleBuffer.Available).IsGreaterThan(0);
    }

    [Test]
    public async Task Nr52_Power_Bit_Reports_Status()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu();
        // Off by default.
        await Assert.That(apu.Read(0xFF26) & 0x80).IsEqualTo(0);
        apu.Write(0xFF26, 0x80);
        await Assert.That(apu.Read(0xFF26) & 0x80).IsEqualTo(0x80);
        apu.Write(0xFF26, 0x00);
        await Assert.That(apu.Read(0xFF26) & 0x80).IsEqualTo(0);
    }

    [Test]
    public async Task Apu_Write_Is_Ignored_When_Disabled()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu();
        apu.Write(0xFF12, 0xF0);  // NR12 envelope while off
        // NR52 power on
        apu.Write(0xFF26, 0x80);
        // NR12 should read back as zero (previous write dropped).
        await Assert.That(apu.Read(0xFF12)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task WaveRam_Roundtrips_Through_FF30_FF3F()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu();
        apu.Write(0xFF26, 0x80);
        for (int i = 0; i < 16; i++) apu.Write((ushort)(0xFF30 + i), (byte)(0xA0 + i));
        for (int i = 0; i < 16; i++)
            await Assert.That(apu.Read((ushort)(0xFF30 + i))).IsEqualTo((byte)(0xA0 + i));
    }
}
