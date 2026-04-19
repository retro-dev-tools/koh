using System.Collections.Immutable;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;
using KohUI;
using KohUI.Widgets;

namespace Koh.Emulator.App;

/// <summary>
/// The MVU-side of the emulator. Since phase 3.5, the emulator core
/// runs on a dedicated background thread (<see cref="EmulatorLoop"/>),
/// paced against the audio sink. The MVU model no longer advances the
/// emulator — <c>Tick</c> is a lightweight "sample the live state for
/// the UI" message the GL backend dispatches once per vsync so the
/// frame-count label and title bar stay fresh.
/// </summary>
public readonly record struct EmulatorModel(
    EmulatorLoop? Loop,
    string? RomPath,
    long FrameCount,
    string Status);

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
        LoadRomSucceeded ok   => OnLoadSuccess(m, ok),
        LoadRomFailed fail    => m with { Status = $"Load failed: {fail.Error}" },
        JoypadDown d          => OnJoypadDown(m, d.Button),
        JoypadUp u            => OnJoypadUp(m, u.Button),
        LoadRom               => m,   // handled imperatively at boot
        _                     => m,
    };

    private static EmulatorModel OnTick(EmulatorModel m)
    {
        // Sample the loop's live state into the model so the UI can
        // re-render. Emulator frames advance on the EmulatorLoop
        // thread; here we just snapshot the counter.
        long live = m.Loop?.FrameCount ?? 0;
        if (live == m.FrameCount) return m;   // no-op: skip patch work
        return m with { FrameCount = live };
    }

    private static EmulatorModel OnLoadSuccess(EmulatorModel m, LoadRomSucceeded ok)
    {
        // Swap the system into the loop. Pause first so the drain
        // thread isn't mid-RunFrame on the outgoing System; Resume
        // after SetSystem installs the new one.
        m.Loop?.Pause();
        m.Loop?.SetSystem(ok.System);
        m.Loop?.Resume();
        return m with { RomPath = ok.Path, FrameCount = 0, Status = $"Loaded {Path.GetFileName(ok.Path)}" };
    }

    private static EmulatorModel OnJoypadDown(EmulatorModel m, JoypadButton button)
    {
        // We don't hold the GameBoySystem in the model anymore; reach
        // through the Loop. Loop's SetSystem is the only thing that
        // mutates _system, and it's called from this same update loop,
        // so the read is safe.
        m.Loop?.Send(sys => sys.JoypadPress(button));
        return m;
    }

    private static EmulatorModel OnJoypadUp(EmulatorModel m, JoypadButton button)
    {
        m.Loop?.Send(sys => sys.JoypadRelease(button));
        return m;
    }

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

    public static IView<EmulatorMsg> View(EmulatorModel m)
    {
        byte[] pixels = m.Loop?.CurrentFramebuffer ?? s_placeholder;

        var display = new Image<EmulatorMsg>(
            pixels, Framebuffer.Width, Framebuffer.Height, DisplayScale);

        var status = new StatusBar<EmulatorMsg>(ImmutableArray.Create(
            m.Status,
            m.Loop is null ? "No ROM" : $"Frame {m.FrameCount}"));

        var body = new ForEach<EmulatorMsg>(
            StackDirection.Vertical,
            ImmutableArray.Create<IView<EmulatorMsg>>(display, status));

        return new Window<EmulatorMsg, ForEach<EmulatorMsg>>(
            Title: m.RomPath is null ? "Koh Emulator" : $"Koh Emulator — {Path.GetFileName(m.RomPath)}",
            Child: body,
            X: 40, Y: 40,
            Width: Framebuffer.Width * DisplayScale + 16);
    }

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
