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

    public async Task<Result> RunPhase2WorkloadAsync(TimeSpan warmup, TimeSpan measure)
    {
        var rom = BuildPhase2SyntheticRom();
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Cgb, cart);
        SetupPhase2TestState(gb);

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
            // Trigger one OAM DMA per frame to exercise contention + copy.
            gb.Mmu.WriteByte(0xFF46, 0xC0);
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

    private static byte[] BuildPhase2SyntheticRom()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x80;  // CGB
        rom[0x147] = 0x00;
        // Insert a NOP-loop at entry so the full-CPU path doesn't blow through
        // invalid opcodes immediately.
        rom[0x100] = 0x00;          // NOP
        rom[0x101] = 0xC3;          // JP $0100
        rom[0x102] = 0x00;
        rom[0x103] = 0x01;
        return rom;
    }

    private static void SetupPhase2TestState(GameBoySystem gb)
    {
        // Populate VRAM with tile data.
        for (int i = 0; i < 0x2000; i++)
            gb.Mmu.WriteByte((ushort)(0x8000 + i), (byte)((i * 7) ^ 0xAA));

        // Populate OAM with 40 sprites.
        for (int i = 0; i < 40; i++)
        {
            gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 0), (byte)(16 + (i * 4)));
            gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 1), (byte)(8 + (i * 4)));
            gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 2), (byte)i);
            gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 3), 0);
        }

        // Enable LCD, sprites, window.
        gb.Mmu.WriteByte(0xFF40, 0b_1110_0011);
        gb.Mmu.WriteByte(0xFF4A, 50);
        gb.Mmu.WriteByte(0xFF4B, 80);
    }
}
