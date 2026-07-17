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
    [KohIntrinsic("register", 0xFF40)]
    public static byte LCDC { get; set; }

    [KohIntrinsic("register", 0xFF41)]
    public static byte STAT { get; set; }

    [KohIntrinsic("register", 0xFF47)]
    public static byte BGP { get; set; }

    /// <summary>DMG object palette 0 (0xFF48). Object color index 0 is always transparent regardless
    /// of the value stored there.</summary>
    [KohIntrinsic("register", 0xFF48)]
    public static byte OBP0 { get; set; }

    /// <summary>DMG object palette 1 (0xFF49). Object color index 0 is always transparent regardless
    /// of the value stored there.</summary>
    [KohIntrinsic("register", 0xFF49)]
    public static byte OBP1 { get; set; }

    [KohIntrinsic("register", 0xFF43)]
    public static byte SCX { get; set; }

    [KohIntrinsic("register", 0xFF42)]
    public static byte SCY { get; set; }

    [KohIntrinsic("register", 0xFF4B)]
    public static byte WX { get; set; }

    [KohIntrinsic("register", 0xFF4A)]
    public static byte WY { get; set; }

    [KohIntrinsic("register", 0xFF0F)]
    public static byte IF { get; set; }

    [KohIntrinsic("register", 0xFFFF)]
    public static byte IE { get; set; }

    [KohIntrinsic("register", 0xFF4D)]
    public static byte KEY1 { get; set; } = 0xFF;

    [KohIntrinsic("register", 0xFF4F)]
    public static byte VBK { get; set; }

    [KohIntrinsic("register", 0xFF68)]
    public static byte BCPS { get; set; }

    [KohIntrinsic("register", 0xFF69)]
    public static byte BCPD { get; set; }

    /// <summary>CGB object palette index (0xFF6A). Mirrors <see cref="BCPS"/> but selects into the
    /// object palette RAM that <see cref="OCPD"/> reads/writes.</summary>
    [KohIntrinsic("register", 0xFF6A)]
    public static byte OCPS { get; set; }

    /// <summary>CGB object palette data (0xFF6B). Mirrors <see cref="BCPD"/> but targets object
    /// palette RAM instead of background palette RAM.</summary>
    [KohIntrinsic("register", 0xFF6B)]
    public static byte OCPD { get; set; }

    /// <summary>OAM DMA source page (0xFF46). Writing a page value starts a hardware DMA that copies
    /// 160 bytes from <c>page * 0x100</c> into OAM (0xFE00-0xFE9F) over ~160 M-cycles while the bus is
    /// locked to everything but HRAM; the emulator (<c>Koh.Emulator.Core.Dma.OamDma</c>) models that
    /// timing on the ROM path. The desktop reference build has no cycle-accurate bus to model, so the
    /// setter performs the equivalent copy synchronously via <see cref="Gb.DmaOam"/>.</summary>
    [KohIntrinsic("register", 0xFF46)]
    public static byte DMA
    {
        get => _dma;
        set
        {
            _dma = value;
            Gb.DmaOam(value);
        }
    }

    private static byte _dma;

    // CGB HDMA/GDMA registers ($FF51-$FF55). Inert plain storage here: the desktop reference run
    // never starts a transfer (Cgb.IsColor() is false because KEY1 reads $FF), matching real DMG
    // hardware where writes to these addresses are no-ops.
    [KohIntrinsic("register", 0xFF51)]
    public static byte HDMA1 { get; set; }

    [KohIntrinsic("register", 0xFF52)]
    public static byte HDMA2 { get; set; }

    [KohIntrinsic("register", 0xFF53)]
    public static byte HDMA3 { get; set; }

    [KohIntrinsic("register", 0xFF54)]
    public static byte HDMA4 { get; set; }

    [KohIntrinsic("register", 0xFF55)]
    public static byte HDMA5 { get; set; }

    /// <summary>The joypad register. Writing selects the d-pad or button matrix; reading returns the
    /// currently-pressed keys for the selected matrix, active-low, exactly like the hardware.</summary>
    [KohIntrinsic("register", 0xFF00)]
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
    [KohIntrinsic("register", 0xFF04)]
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
    [KohIntrinsic("register", 0xFF44)]
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

    /// <summary>LY compare (0xFF45): the STAT interrupt fires when LY equals this value. Plain
    /// storage on the desktop reference build (the reference run has no STAT interrupt to raise).</summary>
    [KohIntrinsic("register", 0xFF45)]
    public static byte LYC { get; set; }

    // Interrupt/CPU control intrinsics. The compiler maps these to ei/di/halt/nop; on the desktop they
    // are no-ops (the reference run is a plain loop with no interrupt hardware).
    [KohIntrinsic("ei")]
    public static void EnableInterrupts() { }

    [KohIntrinsic("di")]
    public static void DisableInterrupts() { }

    [KohIntrinsic("halt")]
    public static void Halt() { }

    [KohIntrinsic("nop")]
    public static void Nop() { }

    [KohIntrinsic("stop")]
    public static void Stop() { }

    /// <summary>Trigger a hardware OAM DMA from <c>sourcePage * 0x100</c> and wait for it to finish —
    /// the ROM path this lowers to (a boot-installed HRAM trampoline, see the <c>[KohIntrinsic]</c>
    /// "oamdma" kind) executes the trigger+160-M-cycle wait from HRAM, since OAM DMA locks the bus to
    /// everything but HRAM for that whole window and a ROM-resident wait loop would corrupt its own
    /// instruction fetch. The desktop reference build has no such bus lock to model, so it performs the
    /// same synchronous copy as <see cref="DMA"/>'s setter (in fact, is exactly that).</summary>
    [KohIntrinsic("oamdma")]
    public static void RunOamDma(byte sourcePage) => Gb.DmaOam(sourcePage);

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
