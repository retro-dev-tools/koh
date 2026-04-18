using Koh.Emulator.App.Services;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class EmulatorRunnerPacingTests
{
    private sealed class FakeSink : IAudioSink
    {
        public int BufferedValue;
        public int PushCalls;
        public int LastPushLength;
        public AudioIsolationLevel IsolationLevel => AudioIsolationLevel.Worklet;
        public int Buffered => BufferedValue;
        public long Underruns => 0;
        public long Overruns => 0;
        public int Push(ReadOnlySpan<short> samples)
        {
            PushCalls++;
            LastPushLength = samples.Length;
            return BufferedValue;
        }
        public void Reset() { }
    }

    private static GameBoySystem NewTinySystem()
    {
        // Minimal 32 KB ROM that enables the APU (so samples are produced)
        // and then loops forever via JR $FE.
        //   ld a, $80        ; 3E 80
        //   ldh [$26], a     ; E0 26    (NR52 = enable APU)
        //   jr -2            ; 18 FE
        var rom = new byte[0x8000];
        rom[0x100] = 0x3E;
        rom[0x101] = 0x80;
        rom[0x102] = 0xE0;
        rom[0x103] = 0x26;
        rom[0x104] = 0x18;
        rom[0x105] = 0xFE;
        rom[0x147] = 0x00;   // MapperKind.RomOnly
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task Runner_Pushes_Samples_On_Every_Frame_When_Below_Target()
    {
        var sink = new FakeSink { BufferedValue = 0 };   // consumer always starving
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        runner.Resume();

        // Runner should keep pushing continuously while the sink reports
        // starvation. Pick a low bound that works in Debug on a loaded host
        // but is still far from the "sleep when full" cap (<5).
        await Task.Delay(500);
        runner.Pause();
        await Task.Delay(50);

        await Assert.That(sink.PushCalls).IsGreaterThan(10);
        await Assert.That(sink.LastPushLength).IsGreaterThan(500);

        runner.Dispose();
    }

    [Test]
    public async Task Runner_Sleeps_When_Buffer_Above_High_Water()
    {
        var sink = new FakeSink { BufferedValue = 6000 };   // well above HIGH_WATER
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        runner.Resume();

        await Task.Delay(250);
        int pushesWhileHigh = sink.PushCalls;
        runner.Pause();
        await Task.Delay(50);

        await Assert.That(pushesWhileHigh).IsLessThan(5);

        runner.Dispose();
    }

    [Test]
    public async Task Pause_Then_Resume_Is_Idempotent_And_Resumes_Cleanly()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());

        runner.Pause();
        runner.Pause();   // duplicate — no-op
        runner.Resume();
        await Task.Delay(100);
        int pushesAfterFirstResume = sink.PushCalls;
        runner.Resume();   // duplicate — no-op
        await Task.Delay(100);

        await Assert.That(sink.PushCalls).IsGreaterThan(pushesAfterFirstResume);

        runner.Dispose();
    }
}
