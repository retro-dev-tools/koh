using BenchmarkDotNet.Attributes;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Benchmarks;

/// <summary>
/// Phase 4 benchmark: full CPU + PPU + timer + OAM DMA + APU + Serial running
/// a real ROM. Uses Blargg cpu_instrs/01-special.gb if available, falling back
/// to a NOP-loop ROM. APU is powered on via NR52 so the full per-M-cycle
/// pipeline (frame sequencer + 4 channels + sample mixer) is active.
/// Target: 60 frames ≤ 909 ms (≥ 1.1× real-time) per Phase 4 exit-checklist.
/// </summary>
[MemoryDiagnoser]
public class Phase4Benchmarks
{
    private GameBoySystem _gb = null!;

    [GlobalSetup]
    public void Setup()
    {
        byte[] rom = LocateBlarggRom() ?? BuildNopLoopRom();
        var cart = CartridgeFactory.Load(rom);
        _gb = new GameBoySystem(HardwareMode.Dmg, cart);
        // Power the APU on and trigger Ch1 + Ch2 so the full mixer pipeline
        // (incl. envelope / duty / frequency counters) is exercised.
        _gb.Mmu.WriteByte(0xFF26, 0x80);
        _gb.Mmu.WriteByte(0xFF11, 0x80);   // Ch1 duty 50%
        _gb.Mmu.WriteByte(0xFF12, 0xF3);   // Ch1 envelope
        _gb.Mmu.WriteByte(0xFF13, 0x00);
        _gb.Mmu.WriteByte(0xFF14, 0x87);   // trigger Ch1
        _gb.Mmu.WriteByte(0xFF16, 0x80);
        _gb.Mmu.WriteByte(0xFF17, 0xF3);
        _gb.Mmu.WriteByte(0xFF18, 0x00);
        _gb.Mmu.WriteByte(0xFF19, 0x87);   // trigger Ch2
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
        rom[0x100] = 0x00;
        rom[0x101] = 0xC3;
        rom[0x102] = 0x00;
        rom[0x103] = 0x01;
        return rom;
    }

    /// <summary>Runs one emulated real-time second (60 frames) with APU active.</summary>
    [Benchmark]
    public void Run_One_Second_With_Apu()
    {
        for (int i = 0; i < 60; i++) _gb.RunFrame();
    }
}
