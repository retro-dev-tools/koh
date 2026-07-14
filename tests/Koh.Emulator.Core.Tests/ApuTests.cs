using Koh.Emulator.Core.Apu;

namespace Koh.Emulator.Core.Tests;

public class ApuTests
{
    [Test]
    public async Task FrameSequencer_Step_Pattern_Fires_Expected_Clocks()
    {
        var fs = new FrameSequencer();
        int lenClocks = 0,
            sweepClocks = 0,
            envClocks = 0;
        fs.LengthClock += () => lenClocks++;
        fs.SweepClock += () => sweepClocks++;
        fs.EnvelopeClock += () => envClocks++;

        // Over 8 steps: length fires 4x (steps 0,2,4,6), sweep 2x (2,6), envelope 1x (7).
        for (int i = 0; i < 8; i++)
            fs.Advance();

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
        for (int i = 0; i < 20000; i++)
        {
            ch.TickT();
            maxOut = Math.Max(maxOut, ch.Output());
        }
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
        env.Trigger(0x04 | 0x08); // start at 0, increase, period=4
        for (int i = 0; i < 100; i++)
            env.Tick();
        await Assert.That(env.Volume).IsEqualTo(15);
    }

    [Test]
    public async Task NoiseChannel_Output_Varies()
    {
        var ch = new NoiseChannel();
        ch.Trigger(nr41: 0, nr42: 0xF0, nr43: 0x00, nr44: 0x80);
        bool sawZero = false,
            sawOne = false;
        for (int i = 0; i < 1000; i++)
        {
            ch.TickT();
            if (ch.Output() == 0)
                sawZero = true;
            else
                sawOne = true;
        }
        await Assert.That(sawZero).IsTrue();
        await Assert.That(sawOne).IsTrue();
    }

    [Test]
    public async Task WaveChannel_Emits_Samples_From_Pattern()
    {
        var ch = new WaveChannel();
        for (int i = 0; i < 16; i++)
            ch.WavePattern[i] = 0xF0;
        ch.Trigger(nr30: 0x80, nr31: 0, nr32: 0x20, nr33: 0, nr34: 0x80);
        int maxOut = 0;
        for (int i = 0; i < 20000; i++)
        {
            ch.TickT();
            maxOut = Math.Max(maxOut, ch.Output());
        }
        await Assert.That(maxOut).IsGreaterThan(0);
    }

    [Test]
    public async Task AudioSampleBuffer_Push_And_Drain_Roundtrip()
    {
        var buf = new AudioSampleBuffer();
        for (short i = 0; i < 100; i++)
            buf.Push(i);
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
        for (int i = 0; i < 10_000; i++)
            apu.TickT();
        await Assert.That(apu.SampleBuffer.Available).IsEqualTo(0);
    }

    [Test]
    public async Task Apu_Enabled_Produces_Samples()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu { Enabled = true };
        apu.Ch1.Trigger(nrx0: 0, nrx1: 0b_10_000000, nrx2: 0xF3, nrx3: 0x00, nrx4: 0x87);
        for (int i = 0; i < 20_000; i++)
            apu.TickT();
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
        apu.Write(0xFF12, 0xF0); // NR12 envelope while off
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
        for (int i = 0; i < 16; i++)
            apu.Write((ushort)(0xFF30 + i), (byte)(0xA0 + i));
        for (int i = 0; i < 16; i++)
            await Assert.That(apu.Read((ushort)(0xFF30 + i))).IsEqualTo((byte)(0xA0 + i));
    }

    // --- Blargg dmg_sound quirks ---------------------------------------

    [Test]
    public async Task Trigger_Does_Not_Reload_Running_Length_Counter()
    {
        // Blargg dmg_sound 02 "Trigger shouldn't affect length": a running
        // (non-zero) length counter must survive a retrigger untouched.
        var ch = new SquareChannel(hasSweep: false);
        ch.Length.Counter = 10;
        ch.Trigger(nrx0: 0, nrx1: 0, nrx2: 0xF0, nrx3: 0, nrx4: 0x80);
        await Assert.That(ch.Length.Counter).IsEqualTo(10);
    }

    [Test]
    public async Task Trigger_Reloads_Length_To_Max_When_Counter_Is_Zero()
    {
        var ch = new SquareChannel(hasSweep: false);
        ch.Length.Counter = 0;
        ch.Trigger(nrx0: 0, nrx1: 0, nrx2: 0xF0, nrx3: 0, nrx4: 0x80);
        await Assert.That(ch.Length.Counter).IsEqualTo(64);
    }

    [Test]
    public async Task Trigger_Reloads_Length_To_MaxMinusOne_When_Step_Skips_Length_And_Enabled()
    {
        var ch = new SquareChannel(hasSweep: false);
        ch.Length.Counter = 0;
        // nrx4 bit6 (0x40) enables length; lengthSkipsNext=true models the
        // extra-clock quirk firing on the very same trigger write.
        ch.Trigger(nrx0: 0, nrx1: 0, nrx2: 0xF0, nrx3: 0, nrx4: 0xC0, lengthSkipsNext: true);
        await Assert.That(ch.Length.Counter).IsEqualTo(63);
    }

    [Test]
    public async Task Nrx4_Enabling_Length_On_Skipped_Step_Extra_Clocks_And_Can_Disable()
    {
        // Blargg dmg_sound 02/07: enabling length (0->1) on a NRx4 write
        // whose next frame-sequencer step would NOT clock length steals one
        // count immediately; if that reaches zero and it's not also a
        // trigger, the channel disables right away.
        var apu = new Koh.Emulator.Core.Apu.Apu();
        apu.Write(0xFF26, 0x80); // power on
        apu.Write(0xFF12, 0xF0); // DAC on
        apu.Write(0xFF14, 0x80); // trigger, length disabled; Length.Counter -> 64
        apu.Ch1.Length.Counter = 1;
        apu.FrameSequencer.Step = 0; // even step -> next step (odd) skips length

        apu.Write(0xFF14, 0x40); // enable length only (no trigger)

        await Assert.That(apu.Ch1.Length.Counter).IsEqualTo(0);
        await Assert.That((apu.Read(0xFF26) & 0x01)).IsEqualTo(0); // channel 1 off
    }

    [Test]
    public async Task PowerOff_Resets_DutyStep_But_Not_Length_Counter()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu();
        apu.Write(0xFF26, 0x80);
        apu.Write(0xFF12, 0xF0);
        apu.Write(0xFF14, 0x80);
        apu.Ch1.DutyStep = 5;
        apu.Ch1.Length.Counter = 30;

        apu.Write(0xFF26, 0x00); // power off

        await Assert.That(apu.Ch1.DutyStep).IsEqualTo(0);
        await Assert.That(apu.Ch1.Length.Counter).IsEqualTo(30);
    }

    [Test]
    public async Task Sweep_Overflow_Check_Runs_Immediately_On_Trigger_When_Shift_NonZero()
    {
        // Blargg dmg_sound 06 "overflow on trigger": shift != 0 must run the
        // overflow check at trigger time, disabling the channel immediately
        // if the shadow frequency already overflows.
        var ch = new SquareChannel(hasSweep: true);
        // NR10 = period 1, shift 2, increase: freq 0x7FF (2047) overflows on
        // the very first shift-by-2 addition.
        ch.Trigger(nrx0: 0x12, nrx1: 0, nrx2: 0xF0, nrx3: 0xFF, nrx4: 0x87);
        await Assert.That(ch.Enabled).IsFalse();
    }

    [Test]
    public async Task Sweep_Period_Zero_Skips_Periodic_Calculation()
    {
        // Blargg dmg_sound 04 "If calculation<=$7FF, doesn't disable
        // channel"/"If period=0, doesn't calculate": a period-0 sweep still
        // clocks its (period-treated-as-8) timer but performs no calculation
        // or overflow check while doing so.
        var ch = new SquareChannel(hasSweep: true);
        // NR10 = period 0, shift 1, increase; freq 1023 is safe for the
        // trigger-time immediate check (1023 + 511 = 1534 <= 2047) but would
        // overflow after a couple of real periodic doublings if the periodic
        // clock were allowed to calculate with period == 0.
        ch.Trigger(nrx0: 0x01, nrx1: 0, nrx2: 0xF0, nrx3: 0xFF, nrx4: 0x83);
        await Assert.That(ch.Enabled).IsTrue();
        for (int i = 0; i < 64; i++)
            ch.Sweep!.Tick(nr10: 0x01, disableChannel: () => ch.Enabled = false);
        await Assert.That(ch.Enabled).IsTrue();
    }

    [Test]
    public async Task Sweep_Reads_Shift_And_Period_Live_From_Nr10_Not_Cached_At_Trigger()
    {
        // Blargg dmg_sound 05 "period and shift can be changed without
        // channel disabling": a plain NR10 write with no retrigger changes
        // subsequent periodic sweep behavior.
        var ch = new SquareChannel(hasSweep: true);
        // Trigger with shift=0 (no calculation possible at all), period=1.
        ch.Trigger(nrx0: 0x10, nrx1: 0, nrx2: 0xF0, nrx3: 0xFF, nrx4: 0x87); // freq 0x7FF
        await Assert.That(ch.Enabled).IsTrue();

        // Now a bare NR10 write raises shift to a value that WILL overflow on
        // the next periodic tick, without any new trigger.
        ch.Sweep!.Tick(nr10: 0x11, disableChannel: () => ch.Enabled = false);
        await Assert.That(ch.Enabled).IsFalse();
    }

    [Test]
    public async Task Sweep_Exiting_Negate_After_Calculation_Disables_Channel()
    {
        // Blargg dmg_sound 05 "Exiting negate mode after calculation
        // disables channel".
        var ch = new SquareChannel(hasSweep: true);
        // shift=1 (nonzero) so the immediate trigger-time calculation runs in
        // negate mode (nrx0 bit3 set).
        ch.Trigger(nrx0: 0x09, nrx1: 0, nrx2: 0xF0, nrx3: 0, nrx4: 0x80);
        await Assert.That(ch.Enabled).IsTrue();

        // Clearing the negate bit (direction bit3 -> 0) after that
        // subtraction calculation disables the channel immediately.
        ch.Sweep!.OnNr10Write(nr10: 0x10, disableChannel: () => ch.Enabled = false);
        await Assert.That(ch.Enabled).IsFalse();
    }

    [Test]
    public async Task WaveChannel_Trigger_Delays_Playback_By_One_Sample()
    {
        // Pan Docs "access order"/"playback delay": retriggering does not
        // refill the sample buffer, and the first sample actually read after
        // a trigger is index 1 (not index 0).
        var ch = new WaveChannel();
        ch.WavePattern[0] = 0xAB; // nibble0 = 0xA, nibble1 = 0xB
        ch.Trigger(nr30: 0x80, nr31: 0, nr32: 0x20, nr33: 0, nr34: 0x80);
        // Immediately after trigger, output reflects the STALE buffer (0 for
        // a freshly constructed channel), not the newly-triggered position.
        await Assert.That(ch.Output()).IsEqualTo(0);
        // First tick to complete a period reads sample index 1 (low nibble
        // of byte 0), not index 0.
        for (int i = 0; i < 4096; i++)
            ch.TickT();
        await Assert.That(ch.Output()).IsEqualTo(0x0B);
    }

    [Test]
    public async Task WaveChannel_Retrigger_While_Reading_Corrupts_First_Four_Bytes()
    {
        // Blargg dmg_sound 10 / Pan Docs DMG wave-trigger-corruption: if a
        // retrigger lands on the exact cycle CH3 is reading a byte from one
        // of the LATER 12 bytes, the corruption copies that byte's aligned
        // 4-byte block over bytes 0-3.
        var ch = new WaveChannel();
        for (int i = 0; i < 16; i++)
            ch.WavePattern[i] = (byte)(i * 0x11);
        ch.Trigger(nr30: 0x80, nr31: 0, nr32: 0x20, nr33: 0, nr34: 0x80);
        for (int i = 0; i < 4096; i++)
            ch.TickT(); // advance until a sample has just been latched

        // Force the channel to be mid-read at byte index 9 (one of the
        // "later 12 bytes"), then retrigger on that exact cycle.
        typeof(WaveChannel)
            .GetField(
                "_waveIndex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!
            .SetValue(ch, 18); // waveIndex 18 -> byte 9
        typeof(WaveChannel)
            .GetField(
                "_justReadCountdown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!
            .SetValue(ch, 1);

        ch.Trigger(nr30: 0x80, nr31: 0, nr32: 0x20, nr33: 0, nr34: 0x80);

        await Assert.That(ch.WavePattern[0]).IsEqualTo((byte)(8 * 0x11));
        await Assert.That(ch.WavePattern[1]).IsEqualTo((byte)(9 * 0x11));
        await Assert.That(ch.WavePattern[2]).IsEqualTo((byte)(10 * 0x11));
        await Assert.That(ch.WavePattern[3]).IsEqualTo((byte)(11 * 0x11));
    }

    [Test]
    public async Task WaveRam_Access_While_Channel_On_Redirects_To_Current_Byte()
    {
        var apu = new Koh.Emulator.Core.Apu.Apu();
        apu.Write(0xFF26, 0x80);
        for (int i = 0; i < 16; i++)
            apu.Write((ushort)(0xFF30 + i), (byte)(i * 0x11));
        apu.Write(0xFF1A, 0x80); // DAC on
        apu.Write(0xFF1E, 0x87); // trigger CH3

        // Immediately after trigger (not mid-read), access is outside the
        // narrow window and reads back $FF regardless of address.
        await Assert.That(apu.Read(0xFF35)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task WaveRam_Access_During_JustRead_Window_Hits_Current_Byte()
    {
        // Complement to WaveRam_Access_While_Channel_On_Redirects_To_Current_Byte
        // above, which only covers the DMG $FF MISS path. This reaches the
        // narrow HIT window organically -- the single T-cycle during which
        // JustRead is open -- by driving Apu.TickT() directly, then asserts a
        // CPU access during it redirects to WavePattern[CurrentBytePosition]
        // regardless of the address used, for both reads and writes.
        var apu = new Koh.Emulator.Core.Apu.Apu();
        apu.Write(0xFF26, 0x80); // power on
        for (int i = 0; i < 16; i++)
            apu.Write((ushort)(0xFF30 + i), (byte)(i * 0x11));
        apu.Write(0xFF1A, 0x80); // NR30: DAC on
        apu.Write(0xFF1D, 0xFF); // NR33: frequency low byte -> shortest period
        apu.Write(0xFF1E, 0x87); // NR34: frequency high bits + trigger

        bool sawWindow = false;
        for (int i = 0; i < 16 && !sawWindow; i++)
        {
            apu.TickT();
            sawWindow = apu.Ch3.JustRead;
        }
        await Assert.That(sawWindow).IsTrue();

        int pos = apu.Ch3.CurrentBytePosition;
        byte expectedRead = (byte)(pos * 0x11);
        ushort readAddr = (ushort)(0xFF30 + ((pos + 8) % 16));
        await Assert.That(apu.Read(readAddr)).IsEqualTo(expectedRead);

        ushort writeAddr = (ushort)(0xFF30 + ((pos + 3) % 16));
        apu.Write(writeAddr, 0x55);
        await Assert.That(apu.Ch3.WavePattern[pos]).IsEqualTo((byte)0x55);
    }

    // --- CGB: both DMG wave-RAM obscure-behavior quirks are fixed --------

    [Test]
    public async Task Cgb_WaveRam_Access_While_Channel_On_Always_Redirects_No_Miss()
    {
        // Pan Docs "Audio — Obscure Behavior": on CGB, wave-RAM access while
        // CH3 plays reliably redirects to the byte at the current position --
        // no narrow same-cycle window like DMG. Unlike
        // WaveRam_Access_While_Channel_On_Redirects_To_Current_Byte (DMG,
        // misses immediately after trigger), the CGB access below happens
        // right after trigger too, but must NOT miss.
        var apu = new Koh.Emulator.Core.Apu.Apu(HardwareMode.Cgb);
        apu.Write(0xFF26, 0x80);
        for (int i = 0; i < 16; i++)
            apu.Write((ushort)(0xFF30 + i), (byte)(i * 0x11));
        apu.Write(0xFF1A, 0x80); // DAC on
        apu.Write(0xFF1E, 0x87); // trigger CH3

        await Assert.That(apu.Read(0xFF35)).IsNotEqualTo((byte)0xFF);
        await Assert
            .That(apu.Read(0xFF35))
            .IsEqualTo(apu.Ch3.WavePattern[apu.Ch3.CurrentBytePosition]);

        apu.Write(0xFF36, 0x77);
        await Assert.That(apu.Ch3.WavePattern[apu.Ch3.CurrentBytePosition]).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task Cgb_WaveChannel_Retrigger_While_Reading_Does_Not_Corrupt()
    {
        // Same setup as WaveChannel_Retrigger_While_Reading_Corrupts_First_Four_Bytes
        // (DMG), but on CGB hardware fixed the retrigger-corruption quirk --
        // bytes 0-3 must be left untouched by the retrigger.
        var ch = new WaveChannel(isCgb: true);
        for (int i = 0; i < 16; i++)
            ch.WavePattern[i] = (byte)(i * 0x11);
        ch.Trigger(nr30: 0x80, nr31: 0, nr32: 0x20, nr33: 0, nr34: 0x80);
        for (int i = 0; i < 4096; i++)
            ch.TickT(); // advance until a sample has just been latched

        // Force the channel to be mid-read at byte index 9 (one of the
        // "later 12 bytes" that would corrupt bytes 0-3 on DMG), then
        // retrigger on that exact cycle.
        typeof(WaveChannel)
            .GetField(
                "_waveIndex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!
            .SetValue(ch, 18); // waveIndex 18 -> byte 9
        typeof(WaveChannel)
            .GetField(
                "_justReadCountdown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!
            .SetValue(ch, 1);

        ch.Trigger(nr30: 0x80, nr31: 0, nr32: 0x20, nr33: 0, nr34: 0x80);

        await Assert.That(ch.WavePattern[0]).IsEqualTo((byte)(0 * 0x11));
        await Assert.That(ch.WavePattern[1]).IsEqualTo((byte)(1 * 0x11));
        await Assert.That(ch.WavePattern[2]).IsEqualTo((byte)(2 * 0x11));
        await Assert.That(ch.WavePattern[3]).IsEqualTo((byte)(3 * 0x11));
    }
}
