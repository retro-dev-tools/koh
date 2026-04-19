using System.Collections.Immutable;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;
using KohUI;
using KohUI.Widgets;

namespace Koh.Emulator.App;

/// <summary>
/// The MVU-side of the emulator app. The model holds a reference to
/// the live <see cref="GameBoySystem"/> (mutable — an emulator core is
/// not functional) and a frame counter for diff-detection. The update
/// reacts to a wall-clock <c>Tick</c> message that the GL backend
/// dispatches once per vsync; each tick advances one emulated frame.
/// </summary>
public readonly record struct EmulatorModel(
    GameBoySystem? System,
    string? RomPath,
    long FrameCount,
    string Status,
    AudioSink? Audio);

public abstract record EmulatorMsg;
public sealed record Tick : EmulatorMsg;
public sealed record LoadRom(string Path) : EmulatorMsg;
public sealed record LoadRomSucceeded(GameBoySystem System, string Path) : EmulatorMsg;
public sealed record LoadRomFailed(string Path, string Error) : EmulatorMsg;
public sealed record JoypadDown(JoypadButton Button) : EmulatorMsg;
public sealed record JoypadUp(JoypadButton Button) : EmulatorMsg;

public static class EmulatorApp
{
    public const int DisplayScale = 3;   // 160×144 → 480×432

    public static EmulatorModel Update(EmulatorMsg msg, EmulatorModel m) => msg switch
    {
        Tick                  => OnTick(m),
        LoadRomSucceeded ok   => m with { System = ok.System, RomPath = ok.Path, Status = $"Loaded {Path.GetFileName(ok.Path)}" },
        LoadRomFailed fail    => m with { Status = $"Load failed: {fail.Error}" },
        JoypadDown d          => OnJoypadDown(m, d.Button),
        JoypadUp u            => OnJoypadUp(m, u.Button),
        // LoadRom is handled imperatively at the boot site; keeping the
        // msg in the discriminated union so the dispatcher still accepts
        // it without the compiler complaining.
        LoadRom               => m,
        _                     => m,
    };

    private static EmulatorModel OnJoypadDown(EmulatorModel m, JoypadButton button)
    {
        m.System?.JoypadPress(button);
        return m;
    }

    private static EmulatorModel OnJoypadUp(EmulatorModel m, JoypadButton button)
    {
        m.System?.JoypadRelease(button);
        return m;
    }

    /// <summary>
    /// Keyboard → joypad mapping following the arcade-style default
    /// (WASD / arrows for the d-pad, Z/X for B/A, Enter/RShift for
    /// Start/Select). Returns null for unmapped keys so the runner's
    /// default handling (focus, menus) still works for them.
    /// </summary>
    public static JoypadButton? MapKey(string keyName) => keyName switch
    {
        "ArrowUp"     => JoypadButton.Up,
        "ArrowDown"   => JoypadButton.Down,
        "ArrowLeft"   => JoypadButton.Left,
        "ArrowRight"  => JoypadButton.Right,
        "KeyZ"        => JoypadButton.B,
        "KeyX"        => JoypadButton.A,
        "Enter"       => JoypadButton.Start,
        "ShiftRight"  => JoypadButton.Select,
        _             => null,
    };

    private static EmulatorModel OnTick(EmulatorModel m)
    {
        if (m.System is null) return m;
        m.System.RunFrame();
        m.Audio?.Push(m.System.Apu.SampleBuffer);
        return m with { FrameCount = m.FrameCount + 1 };
    }

    public static IView<EmulatorMsg> View(EmulatorModel m)
    {
        // When no ROM is loaded yet (or load failed) the Image carries a
        // placeholder grey buffer from Framebuffer's ctor. This keeps
        // the widget tree shape stable across load events so the layout
        // doesn't reflow every time the user picks a new ROM.
        byte[] pixels = m.System?.Ppu.Framebuffer.FrontArray ?? s_placeholder;

        var display = new Image<EmulatorMsg>(
            pixels, Framebuffer.Width, Framebuffer.Height, DisplayScale);

        var status = new StatusBar<EmulatorMsg>(ImmutableArray.Create(
            m.Status,
            m.System is null ? "No ROM" : $"Frame {m.FrameCount}"));

        var body = new ForEach<EmulatorMsg>(
            StackDirection.Vertical,
            ImmutableArray.Create<IView<EmulatorMsg>>(display, status));

        return new Window<EmulatorMsg, ForEach<EmulatorMsg>>(
            Title: m.System is null ? "Koh Emulator" : $"Koh Emulator — {Path.GetFileName(m.RomPath)}",
            Child: body,
            X: 40, Y: 40,
            Width: Framebuffer.Width * DisplayScale + 16);
    }

    /// <summary>
    /// Placeholder grey block used before the first ROM is loaded —
    /// same (0x2e, 0x2e, 0x2e, 0xff) fill the PPU's Framebuffer uses so
    /// the window doesn't flash between "initial paint" and "emulator
    /// actually running".
    /// </summary>
    private static readonly byte[] s_placeholder = BuildPlaceholder();

    private static byte[] BuildPlaceholder()
    {
        var buf = new byte[Framebuffer.Width * Framebuffer.Height * Framebuffer.BytesPerPixel];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i + 0] = 0x2e;
            buf[i + 1] = 0x2e;
            buf[i + 2] = 0x2e;
            buf[i + 3] = 0xff;
        }
        return buf;
    }

    /// <summary>
    /// Side-effecting ROM load. Called off the UI path at startup (and
    /// later, from a file-open dialog in phase 2). Produces a success
    /// or failure message for the update loop.
    /// </summary>
    public static EmulatorMsg LoadRomFromDisk(string path, HardwareMode mode)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var cart = CartridgeFactory.Load(bytes);
            var system = new GameBoySystem(mode, cart);
            return new LoadRomSucceeded(system, path);
        }
        catch (Exception ex)
        {
            return new LoadRomFailed(path, ex.Message);
        }
    }
}
