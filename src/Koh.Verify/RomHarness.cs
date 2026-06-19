// Koh.Verify.RomHarness — headless verification harness for Koh-built ROMs.
//
// Wraps Koh.Emulator.Core.GameBoySystem with a higher-level test-DSL
// surface: boot a ROM, run frames, press buttons, snapshot framebuffer
// to PPM/PNG, read raw memory, and accumulate pass/fail assertions.
//
// Designed to be driven from a small console app per ROM. The sample
// in samples/gb-2048 uses this to verify its state transitions.
using System.IO;
using System.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Joypad;

namespace Koh.Verify;

public sealed class RomHarness
{
    public GameBoySystem System { get; }
    public int Failures { get; private set; }
    public int Passes { get; private set; }

    private readonly TextWriter _log;

    public RomHarness(string romPath, HardwareMode mode = HardwareMode.Cgb, TextWriter? log = null)
    {
        var cart = CartridgeFactory.Load(File.ReadAllBytes(romPath));
        System = new GameBoySystem(mode, cart);
        _log = log ?? Console.Out;
    }

    public void Frames(int n)
    {
        for (int i = 0; i < n; i++) System.RunFrame();
    }

    public void Press(JoypadButton button, int holdFrames = 4, int releaseFrames = 8)
    {
        var jp = default(JoypadState);
        jp.Press(button);
        System.Joypad = jp;
        Frames(holdFrames);
        System.Joypad = default;
        Frames(releaseFrames);
    }

    public byte Read(ushort addr) => System.Mmu.DebugRead(addr);
    public void Write(ushort addr, byte v) => System.Mmu.DebugWrite(addr, v);

    /// <summary>Read a tilemap entry from BG map 0 (bank 0) at (row, col).</summary>
    public byte TileAt(int row, int col) => System.Mmu.VramArray[0x1800 + row * 32 + col];

    /// <summary>Read a CGB attribute byte (bank 1) at (row, col).</summary>
    public byte AttrAt(int row, int col) => System.Mmu.VramArray[0x3800 + row * 32 + col];

    public void SaveScreenshotPpm(string path)
    {
        var fb = System.Ppu.Framebuffer.FrontArray;
        int W = Koh.Emulator.Core.Ppu.Framebuffer.Width;
        int H = Koh.Emulator.Core.Ppu.Framebuffer.Height;
        using var s = File.Create(path);
        var header = Encoding.ASCII.GetBytes($"P6\n{W} {H}\n255\n");
        s.Write(header);
        var rgb = new byte[3];
        for (int p = 0; p < W * H; p++)
        {
            int i = p * 4;
            rgb[0] = fb[i + 0];
            rgb[1] = fb[i + 1];
            rgb[2] = fb[i + 2];
            s.Write(rgb);
        }
    }

    public void SaveScreenshotPng(string path, int scale = 3)
    {
        var fb = System.Ppu.Framebuffer.FrontArray;
        int W = Koh.Emulator.Core.Ppu.Framebuffer.Width;
        int H = Koh.Emulator.Core.Ppu.Framebuffer.Height;
        PngEncoder.WriteFromRgba(path, fb, W, H, scale);
    }

    /// <summary>Snapshot the current framebuffer as an RGB byte[] (160x144 * 3).</summary>
    public byte[] CaptureRgb()
    {
        var fb = System.Ppu.Framebuffer.FrontArray;
        int W = Koh.Emulator.Core.Ppu.Framebuffer.Width;
        int H = Koh.Emulator.Core.Ppu.Framebuffer.Height;
        var rgb = new byte[W * H * 3];
        for (int p = 0; p < W * H; p++)
        {
            rgb[p * 3 + 0] = fb[p * 4 + 0];
            rgb[p * 3 + 1] = fb[p * 4 + 1];
            rgb[p * 3 + 2] = fb[p * 4 + 2];
        }
        return rgb;
    }

    public void Pass(string msg)
    {
        _log.WriteLine($"PASS: {msg}");
        Passes++;
    }

    public void Fail(string msg)
    {
        _log.WriteLine($"FAIL: {msg}");
        Failures++;
    }

    public void Assert(bool condition, string msg)
    {
        if (condition) Pass(msg); else Fail(msg);
    }

    public void Summary()
    {
        _log.WriteLine();
        _log.WriteLine(Failures == 0
            ? $"*** ALL {Passes} CHECKS PASSED ***"
            : $"*** {Failures} FAILURE(S), {Passes} pass(es) ***");
    }

    public int ExitCode => Failures == 0 ? 0 : 1;
}
