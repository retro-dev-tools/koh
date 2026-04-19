using Koh.Emulator.Core;
using Koh.Emulator.Core.Cpu;
using KohUI.Theme;

namespace Koh.Emulator.App;

/// <summary>
/// Immutable snapshot of the CPU's register state after a frame.
/// Published by <see cref="EmulatorLoop"/> per-frame via a volatile
/// reference write so the UI thread can read a consistent set without
/// reaching into the live <see cref="GameBoySystem"/>.
/// </summary>
public sealed record CpuSnapshot(
    ushort Pc,
    ushort Sp,
    byte A,
    byte F,
    ushort BC,
    ushort DE,
    ushort HL,
    ulong TotalTCycles,
    bool FlagZ,
    bool FlagN,
    bool FlagH,
    bool FlagC)
{
    public static CpuSnapshot From(GameBoySystem sys)
    {
        ref var r = ref sys.Cpu.Registers;
        return new CpuSnapshot(
            Pc:           r.Pc,
            Sp:           r.Sp,
            A:            r.A,
            F:            r.F,
            BC:           r.BC,
            DE:           r.DE,
            HL:           r.HL,
            TotalTCycles: sys.Cpu.TotalTCycles,
            FlagZ:        r.FlagSet(CpuRegisters.FlagZ),
            FlagN:        r.FlagSet(CpuRegisters.FlagN),
            FlagH:        r.FlagSet(CpuRegisters.FlagH),
            FlagC:        r.FlagSet(CpuRegisters.FlagC));
    }
}

/// <summary>
/// Snapshot of PPU palette state. DMG and CGB share this shape: DMG
/// fills the first BG / OBJ0 / OBJ1 palettes from BGP/OBP0/OBP1 byte
/// decoding, CGB fills all 8 of each from its palette RAM. Colours are
/// packed into <see cref="KohColor"/> (RGB8) at snapshot time so the
/// UI doesn't need to know about BGR555 encoding.
/// </summary>
public sealed record PaletteSnapshot(
    bool IsCgb,
    byte Bgp,
    byte Obp0,
    byte Obp1,
    KohColor[] BgColors,    // [palette * 4 + slot]; length = 4 (DMG) or 32 (CGB)
    KohColor[] ObjColors)
{
    public static PaletteSnapshot From(GameBoySystem sys, PaletteSnapshot? existing = null)
    {
        bool cgb = sys.Mode == HardwareMode.Cgb;
        int bgLen = cgb ? 32 : 4;
        int objLen = cgb ? 32 : 8;
        var bg = existing is not null && existing.BgColors.Length == bgLen ? existing.BgColors : new KohColor[bgLen];
        var obj = existing is not null && existing.ObjColors.Length == objLen ? existing.ObjColors : new KohColor[objLen];
        if (cgb)
        {
            for (int p = 0; p < 8; p++)
            for (int s = 0; s < 4; s++)
            {
                bg[p * 4 + s]  = Bgr555ToRgb(sys.Ppu.BgPalette.GetColor(p, s));
                obj[p * 4 + s] = Bgr555ToRgb(sys.Ppu.ObjPalette.GetColor(p, s));
            }
        }
        else
        {
            DmgDecodeInto(sys.Ppu.BGP, bg, 0);
            DmgDecodeInto(sys.Ppu.OBP0, obj, 0);
            DmgDecodeInto(sys.Ppu.OBP1, obj, 4);
        }
        return new PaletteSnapshot(cgb, sys.Ppu.BGP, sys.Ppu.OBP0, sys.Ppu.OBP1, bg, obj);
    }

    // Two bits per slot, 00→white, 01→light grey, 10→dark grey,
    // 11→black. Green-tinted to match a real DMG screen.
    private static readonly KohColor[] s_dmgShades =
    [
        new(0x9b, 0xbc, 0x0f),
        new(0x8b, 0xac, 0x0f),
        new(0x30, 0x62, 0x30),
        new(0x0f, 0x38, 0x0f),
    ];

    private static void DmgDecodeInto(byte palette, KohColor[] dest, int offset)
    {
        dest[offset + 0] = s_dmgShades[(palette >> 0) & 3];
        dest[offset + 1] = s_dmgShades[(palette >> 2) & 3];
        dest[offset + 2] = s_dmgShades[(palette >> 4) & 3];
        dest[offset + 3] = s_dmgShades[(palette >> 6) & 3];
    }

    private static KohColor Bgr555ToRgb(ushort v)
    {
        int r5 = v         & 0x1f;
        int g5 = (v >> 5)  & 0x1f;
        int b5 = (v >> 10) & 0x1f;
        // 5-bit → 8-bit with the standard "replicate the top bits" trick
        // so 0x1f becomes 0xff exactly.
        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g5 << 3) | (g5 >> 2));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        return new KohColor(r, g, b);
    }
}

/// <summary>
/// Snapshot of VRAM tile data as a ready-to-blit RGBA image. Tile
/// data lives at 0x8000–0x97FF within each bank (384 tiles × 16 bytes);
/// we decode all of it into a 128 × (192 or 384) RGBA buffer — 16
/// tiles per row at 8 × 8 px each. CGB stacks bank 1 below bank 0
/// so the debug panel can show both at once without swapping UI
/// state.
/// </summary>
public sealed record VramSnapshot(
    byte[] Rgba,
    int Width,
    int Height)
{
    private const int TilesPerRow = 16;
    private const int TilesPerBank = 384;
    private const int PixelsPerTile = 8;

    /// <summary>
    /// Populate <paramref name="existing"/> in place if its buffer is
    /// the right shape; allocate a fresh one otherwise. Writes
    /// ~96 KB (DMG) or ~192 KB (CGB) of RGBA data per call. Reusing
    /// across frames is the difference between 12 MB/sec of gen-0
    /// pressure (audible as audio underruns during startup) and zero
    /// steady-state allocation for this publishing path.
    /// </summary>
    public static VramSnapshot From(GameBoySystem sys, VramSnapshot? existing = null)
    {
        int banks = sys.Mode == HardwareMode.Cgb ? 2 : 1;
        int rowsPerBank = TilesPerBank / TilesPerRow;   // 24
        int width  = TilesPerRow * PixelsPerTile;        // 128
        int height = banks * rowsPerBank * PixelsPerTile; // 192 or 384
        byte[] rgba = existing is not null && existing.Width == width && existing.Height == height
            ? existing.Rgba
            : new byte[width * height * 4];

        var vram = sys.Mmu.VramArray;
        for (int bank = 0; bank < banks; bank++)
        {
            int bankBase = bank * 0x2000;
            int bankYOffset = bank * rowsPerBank * PixelsPerTile;
            for (int tile = 0; tile < TilesPerBank; tile++)
            {
                int tileByteBase = bankBase + tile * 16;
                int gridCol = tile % TilesPerRow;
                int gridRow = tile / TilesPerRow;
                int tileX = gridCol * PixelsPerTile;
                int tileY = bankYOffset + gridRow * PixelsPerTile;

                for (int r = 0; r < PixelsPerTile; r++)
                {
                    byte low  = vram[tileByteBase + r * 2];
                    byte high = vram[tileByteBase + r * 2 + 1];
                    for (int c = 0; c < PixelsPerTile; c++)
                    {
                        int bit = 7 - c;
                        int colorIndex = (((high >> bit) & 1) << 1) | ((low >> bit) & 1);
                        var shade = s_shades[colorIndex];
                        int px = ((tileY + r) * width + (tileX + c)) * 4;
                        rgba[px + 0] = shade.R;
                        rgba[px + 1] = shade.G;
                        rgba[px + 2] = shade.B;
                        rgba[px + 3] = 0xff;
                    }
                }
            }
        }

        return new VramSnapshot(rgba, width, height);
    }

    // Fixed grayscale shades for the debug view — not using BGP because
    // the same tile byte can map to any shade depending on which
    // palette the background layer uses at render time. A neutral
    // gradient lets the eye identify tiles independently of palette.
    private static readonly KohColor[] s_shades =
    [
        new(0xff, 0xff, 0xff),   // 00 → white
        new(0xc0, 0xc0, 0xc0),
        new(0x60, 0x60, 0x60),
        new(0x00, 0x00, 0x00),   // 11 → black
    ];
}

/// <summary>
/// A 256-byte window into the Game Boy's $0000-$FFFF address space for
/// the hex debug panel. The window base wraps around $FFFF so
/// PageDown / PageUp past the ends stays in-bounds. Each snapshot is
/// re-sampled from <see cref="GameBoySystem.DebugReadByte"/>, which
/// respects MBC banking — the same 0x4000-0x7FFF page shows different
/// bytes as the current ROM bank changes.
/// </summary>
/// <summary>
/// A 256-byte window into the Game Boy's $0000-$FFFF address space.
/// Naively snapshotting the full 64 KB every frame (a "just use a
/// ScrollPanel with 4096 rows" approach) costs ~65K DebugReadByte
/// calls per frame — enough to starve the emulator thread and
/// visibly stall the LCD. Sliding window costs 256 reads and keeps
/// the pacer in the clear.
/// </summary>
public sealed record MemorySnapshot(
    ushort BaseAddress,
    byte[] Bytes)
{
    public const int BytesPerRow = 16;
    public const int Rows = 16;
    public const int WindowSize = Rows * BytesPerRow;   // 256 bytes

    public static MemorySnapshot From(GameBoySystem sys, ushort baseAddress, MemorySnapshot? existing = null)
    {
        byte[] bytes = existing is not null && existing.Bytes.Length == WindowSize
            ? existing.Bytes
            : new byte[WindowSize];
        for (int i = 0; i < WindowSize; i++)
            bytes[i] = sys.DebugReadByte((ushort)(baseAddress + i));
        return new MemorySnapshot(baseAddress, bytes);
    }
}
