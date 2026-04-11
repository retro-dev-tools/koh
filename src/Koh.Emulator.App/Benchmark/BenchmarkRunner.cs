using System.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Benchmark;

public sealed class BenchmarkRunner
{
    public sealed record Result(double WallSeconds, ulong SystemTicks, double TicksPerSecond, double RealTimeMultiplier);

    public async Task<Result> RunAsync(TimeSpan warmup, TimeSpan measure)
    {
        var rom = BuildSyntheticRom();
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        var warmupEnd = DateTime.UtcNow + warmup;
        while (DateTime.UtcNow < warmupEnd)
        {
            gb.RunFrame();
            await Task.Yield();
        }

        var sw = Stopwatch.StartNew();
        ulong ticksStart = gb.Clock.SystemTicks;
        var measureEnd = DateTime.UtcNow + measure;
        while (DateTime.UtcNow < measureEnd)
        {
            gb.RunFrame();
            await Task.Yield();
        }
        sw.Stop();
        ulong ticksEnd = gb.Clock.SystemTicks;

        double wallSeconds = sw.Elapsed.TotalSeconds;
        ulong deltaTicks = ticksEnd - ticksStart;
        double ticksPerSec = deltaTicks / wallSeconds;
        double multiplier = ticksPerSec / 4194304.0;

        return new Result(wallSeconds, deltaTicks, ticksPerSec, multiplier);
    }

    private static byte[] BuildSyntheticRom()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x00;
        rom[0x147] = 0x00;
        for (int i = 0x150; i < rom.Length; i++)
            rom[i] = (byte)(i & 0xFF);
        return rom;
    }
}
