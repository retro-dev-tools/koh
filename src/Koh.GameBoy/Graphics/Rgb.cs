namespace Koh.GameBoy.Graphics;

/// <summary>
/// RGB555 color packing for the CGB palette RAM path (<see cref="Palettes"/>). The Game Boy Color's
/// palette registers store 15 bits per color: red in bits 0-4, green in bits 5-9, blue in bits 10-14
/// (bit 15 unused) — see <see cref="Koh.GameBoy.Cgb.SetBackgroundColor"/>, which already writes this
/// exact layout to BCPS/BCPD. Each channel is 5 bits (0-31); values outside that range are masked, not
/// clamped, matching how every other width boundary in this codebase truncates rather than saturates.
/// </summary>
public static class Rgb
{
    /// <summary>Packs three 0-31 channels into an RGB555 value: r | (g &lt;&lt; 5) | (b &lt;&lt; 10).</summary>
    public static ushort Make(byte r, byte g, byte b) =>
        (ushort)((r & 0x1F) | ((g & 0x1F) << 5) | ((b & 0x1F) << 10));

    public const ushort White = 0x7FFF; // Make(31, 31, 31)
    public const ushort Black = 0x0000; // Make(0, 0, 0)
    public const ushort Red = 0x001F; // Make(31, 0, 0)
    public const ushort Green = 0x03E0; // Make(0, 31, 0)
    public const ushort Blue = 0x7C00; // Make(0, 0, 31)
    public const ushort Yellow = 0x03FF; // Make(31, 31, 0)
    public const ushort Cyan = 0x7FE0; // Make(0, 31, 31)
    public const ushort Magenta = 0x7C1F; // Make(31, 0, 31)
}
