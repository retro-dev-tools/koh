using System.Text;

namespace Koh.GameBoy;

/// <summary>
/// The Game Boy memory-mapped register surface. On hardware these are bytes at 0xFF00.. that the CPU
/// reads and writes; the Koh compiler lowers <c>Hardware.LCDC</c> etc. to those addresses. Under the
/// plain .NET SDK the same names resolve here, and the runtime turns them into a real, playable game:
/// reading <see cref="LY"/> paces the frame, <see cref="JOYP"/> reads the keyboard, and each frame the
/// tile map is drawn to the console. So one source both compiles to a ROM and runs with <c>dotnet run</c>.
/// </summary>
public static class Hardware
{
    // Plain register storage. The game writes these to configure the display; the renderer reads LCDC
    // (bit 7 = LCD on) and BGP (the palette) to decide what and how to draw.
    public static byte LCDC { get; set; }
    public static byte STAT { get; set; }
    public static byte BGP { get; set; }
    public static byte SCX { get; set; }
    public static byte SCY { get; set; }
    public static byte WX { get; set; }
    public static byte WY { get; set; }
    public static byte IF { get; set; }
    public static byte IE { get; set; }
    public static byte KEY1 { get; set; } = 0xFF;
    public static byte VBK { get; set; }
    public static byte BCPS { get; set; }
    public static byte BCPD { get; set; }

    // CGB HDMA/GDMA registers ($FF51-$FF55). Inert plain storage here: the desktop reference run
    // never starts a transfer (Cgb.IsColor() is false because KEY1 reads $FF), matching real DMG
    // hardware where writes to these addresses are no-ops.
    public static byte HDMA1 { get; set; }
    public static byte HDMA2 { get; set; }
    public static byte HDMA3 { get; set; }
    public static byte HDMA4 { get; set; }
    public static byte HDMA5 { get; set; }

    /// <summary>The joypad register. Writing selects the d-pad or button matrix; reading returns the
    /// currently-pressed keys for the selected matrix, active-low, exactly like the hardware.</summary>
    public static byte JOYP
    {
        get
        {
            // A game that isn't moving spins reading JOYP, so pace/render/poll here too (not only on
            // LY reads) — otherwise the idle loop would busy-spin with nothing drawn.
            Host.Beat();
            return Host.ReadJoypad(_joypSelect);
        }
        set => _joypSelect = value;
    }

    private static byte _joypSelect = 0x30;

    /// <summary>The divider register: a free-running counter games use as cheap entropy. Each read
    /// advances it, so successive reads give different values (as on hardware, where it ticks fast).</summary>
    public static byte DIV
    {
        get
        {
            _div = unchecked((byte)(_div * 137 + 29));
            return _div;
        }
    }

    private static byte _div = (byte)Environment.TickCount;

    /// <summary>The LCD scanline. Reads cycle 0..153; a game spins on <c>LY == 144</c> to wait for
    /// vertical blank, so each read also advances the host clock (drawing a frame and polling input at
    /// the frame boundary). This is what makes a <c>while (Hardware.LY != 144) {}</c> loop actually run.</summary>
    public static byte LY
    {
        get
        {
            Host.Beat();
            _ly = (byte)((_ly + 1) % 154);
            return _ly;
        }
    }

    private static byte _ly;

    // Interrupt/CPU control intrinsics. The compiler maps these to ei/di/halt/nop; on the desktop they
    // are no-ops (the reference run is a plain loop with no interrupt hardware).
    public static void EnableInterrupts() { }

    public static void DisableInterrupts() { }

    public static void Halt() { }

    public static void Nop() { }

    public static void Stop() { }

    /// <summary>Host-side pacing, input, and rendering for the desktop reference run.</summary>
    private static class Host
    {
        private const int BeatsPerFrame = 512; // busy-loop iterations between rendered frames
        private static long _beats;
        private static byte _dpad; // active-high: bit0 Right, bit1 Left, bit2 Up, bit3 Down
        private static byte _buttons; // active-high: bit3 Start
        private static int _holdFrames;
        private static bool _consoleUsable = true;

        internal static void Beat()
        {
            PollKey();
            if (++_beats % BeatsPerFrame != 0)
                return;
            if (_holdFrames > 0 && --_holdFrames == 0)
            {
                _dpad = 0;
                _buttons = 0;
            }
            if ((LCDC & 0x80) != 0)
                Render();
            Thread.Sleep(16); // ~60 fps, and keeps an idle loop off the CPU
        }

        internal static byte ReadJoypad(byte select)
        {
            // Active-low: 0 = pressed. Bits 4/5 select which matrix the low nibble reports.
            byte low;
            if ((select & 0x10) == 0)
                low = (byte)(~_dpad & 0x0F); // d-pad selected (P14 low)
            else if ((select & 0x20) == 0)
                low = (byte)(~_buttons & 0x0F); // buttons selected (P15 low)
            else
                low = 0x0F;
            return (byte)(0xC0 | (select & 0x30) | low);
        }

        private static void PollKey()
        {
            if (!_consoleUsable)
                return;
            try
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    switch (key)
                    {
                        case ConsoleKey.RightArrow:
                            _dpad = 0x01;
                            _holdFrames = 2;
                            break;
                        case ConsoleKey.LeftArrow:
                            _dpad = 0x02;
                            _holdFrames = 2;
                            break;
                        case ConsoleKey.UpArrow:
                            _dpad = 0x04;
                            _holdFrames = 2;
                            break;
                        case ConsoleKey.DownArrow:
                            _dpad = 0x08;
                            _holdFrames = 2;
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Input is redirected (e.g. a CI smoke run): no interactive keys.
                _consoleUsable = false;
            }
        }

        private static void Render()
        {
            var sb = new StringBuilder();
            sb.Append("\x1b[H\x1b[2J"); // cursor home + clear screen
            sb.Append("  2048 - Koh C# reference run\n\n");
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    // The game paints each cell as a 2x2 block of its value's tile at this map slot;
                    // the stored tile index is the exponent (0 = empty, n = 2^n).
                    int slot = (3 + r * 3) * 32 + (3 + c * 4);
                    int exp = Gb.Peek(0x9800 + slot);
                    sb.Append((exp == 0 ? "." : (1 << exp).ToString()).PadLeft(6));
                }
                sb.Append('\n');
            }
            sb.Append("\n  Arrow keys move - Ctrl+C to quit.\n");
            Console.Write(sb.ToString());
        }
    }
}
