using BenchmarkDotNet.Attributes;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Benchmarks;

[MemoryDiagnoser]
public class Phase2Benchmarks
{
    private GameBoySystem _gb = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x80;  // CGB
        rom[0x147] = 0x00;
        rom[0x100] = 0x00;          // NOP
        rom[0x101] = 0xC3;          // JP $0100
        rom[0x102] = 0x00;
        rom[0x103] = 0x01;
        var cart = CartridgeFactory.Load(rom);
        _gb = new GameBoySystem(HardwareMode.Cgb, cart);

        // Populate VRAM + OAM + enable LCD/sprites/window.
        for (int i = 0; i < 0x2000; i++)
            _gb.Mmu.WriteByte((ushort)(0x8000 + i), (byte)((i * 7) ^ 0xAA));
        for (int i = 0; i < 40; i++)
        {
            _gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 0), (byte)(16 + (i * 4)));
            _gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 1), (byte)(8 + (i * 4)));
            _gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 2), (byte)i);
            _gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 3), 0);
        }
        _gb.Mmu.WriteByte(0xFF40, 0b_1110_0011);
        _gb.Mmu.WriteByte(0xFF4A, 50);
        _gb.Mmu.WriteByte(0xFF4B, 80);
    }

    [Benchmark]
    public void Phase2_Frame()
    {
        _gb.RunFrame();
        _gb.Mmu.WriteByte(0xFF46, 0xC0);  // OAM DMA once per frame
    }
}
