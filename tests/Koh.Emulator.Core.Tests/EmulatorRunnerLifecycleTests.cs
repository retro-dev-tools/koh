using Koh.Emulator.App.Services;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class EmulatorRunnerLifecycleTests
{
    private sealed class FakeSink : IAudioSink
    {
        public int Resets;
        public int PushCalls;
        public AudioIsolationLevel IsolationLevel => AudioIsolationLevel.Worklet;
        public int Buffered => 0;
        public long Underruns => 0;
        public long Overruns => 0;
        public int Push(ReadOnlySpan<short> samples) { PushCalls++; return 0; }
        public void Reset() => Resets++;
    }

    private static GameBoySystem NewTinySystem()
    {
        // Enable APU so samples are produced (matches pacing test).
        var rom = new byte[0x8000];
        rom[0x100] = 0x3E; rom[0x101] = 0x80;
        rom[0x102] = 0xE0; rom[0x103] = 0x26;
        rom[0x104] = 0x18; rom[0x105] = 0xFE;
        rom[0x147] = 0x00;
        return new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
    }

    [Test]
    public async Task SetSystem_Resets_Sink()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        await Assert.That(sink.Resets).IsEqualTo(1);

        runner.SetSystem(NewTinySystem());
        await Assert.That(sink.Resets).IsEqualTo(2);

        runner.Dispose();
    }

    [Test]
    public async Task Resume_Without_System_Is_A_Noop()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.Resume();
        await Task.Delay(100);
        await Assert.That(sink.PushCalls).IsEqualTo(0);
        await Assert.That(runner.IsPaused).IsTrue();
        runner.Dispose();
    }

    [Test]
    public async Task Dispose_Stops_Thread_Within_Timeout()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        runner.Resume();
        await Task.Delay(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        runner.Dispose();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1500);
    }

    [Test]
    public async Task FatalError_Event_Not_Fired_On_Happy_Path()
    {
        // We only verify the handler wires up and doesn't fire on normal
        // operation — the underlying cause (mis-decoded opcodes, etc.)
        // is covered by CPU tests.
        Exception? seen = null;
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.FatalError += ex => seen = ex;
        runner.SetSystem(NewTinySystem());
        runner.Resume();
        await Task.Delay(50);

        await Assert.That(seen).IsNull();
        await Assert.That(sink.PushCalls).IsGreaterThan(0);
        runner.Dispose();
    }
}
