using BenchmarkDotNet.Attributes;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Benchmarks;

/// <summary>
/// Phase 3 benchmark: full CPU + PPU + timer + OAM DMA running a real ROM.
/// Uses Blargg cpu_instrs/01-special.gb if available, falling back to a
/// NOP-loop ROM so the benchmark is self-contained even without fixtures.
/// Target: 60 frames &lt;= 770 ms (≥ 1.3× real-time) per §12.9.
/// </summary>
[MemoryDiagnoser]
public class Phase3Benchmarks
{
    private GameBoySystem _gb = null!;

    [GlobalSetup]
    public void Setup()
    {
        byte[] rom = LocateBlarggRom() ?? BuildNopLoopRom();
        var cart = CartridgeFactory.Load(rom);
        _gb = new GameBoySystem(HardwareMode.Dmg, cart);
    }

    private static byte[]? LocateBlarggRom()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "test-roms", "blargg",
                "cpu_instrs", "individual", "01-special.gb");
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static byte[] BuildNopLoopRom()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0x00;  // NOP
        rom[0x101] = 0xC3;  // JP $0100
        rom[0x102] = 0x00;
        rom[0x103] = 0x01;
        return rom;
    }

    /// <summary>Runs one emulated real-time second (60 frames).</summary>
    [Benchmark]
    public void Run_One_Second()
    {
        for (int i = 0; i < 60; i++) _gb.RunFrame();
    }
}
