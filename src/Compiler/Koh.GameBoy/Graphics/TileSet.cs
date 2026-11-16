using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>Tile loading.</summary>
public static unsafe class TileSet
{
    private const int TileBytes = 16;

    /// <summary>Hardware ceiling for one <see cref="Cgb.CopyToVram"/> transfer (128 blocks x 16 bytes
    /// — HDMA5's 7-bit length field). Used for the LCD-OFF path, which has no vblank timing budget to
    /// respect (the PPU isn't racing the copy), so a multi-tile load just walks this ceiling as its
    /// chunk size.</summary>
    private const ushort GdmaHardwareCeiling = 2048;

    private const ushort VblankGdmaChunkBytes = 1024;

    /// <summary>Copy <paramref name="count"/> tiles (16 bytes each, 2bpp) from the START of
    /// <paramref name="data"/> into VRAM at <paramref name="firstTile"/> ($8000 addressing).
    ///
    /// LCD off: one straight copy — CGB GDMA (<see cref="Cgb.CopyToVram"/>, chunked at the 2048-byte
    /// hardware ceiling) when both the ROM source and the VRAM destination happen to be 16-byte
    /// aligned, else a CPU <see cref="Mem.Copy"/> (always correct regardless of alignment; a caller
    /// wanting the GDMA fast path aligns its data with <c>[KohAligned(16)]</c>). LCD on: vblank-chunked
    /// internally (<see cref="Ppu.WaitVBlank"/> once per chunk) — CGB GDMA in
    /// <see cref="VblankGdmaChunkBytes"/>-byte chunks when aligned, else CPU-copy drip in
    /// four-byte/vblank steps.</summary>
    public static void Load(byte firstTile, byte[] data, byte count)
    {
        if (count == 0)
            return;
        fixed (byte* source = &data[0])
            LoadCore(firstTile, source, count);
    }

    /// <summary>Sub-range: copy <paramref name="count"/> tiles from <paramref name="data"/> starting at
    /// tile <paramref name="startTile"/> within <paramref name="data"/> (i.e. byte offset
    /// <c>startTile * 16</c>) into VRAM at <paramref name="firstTile"/>. Same LCD-off/on chunking
    /// stance as the 3-argument overload.</summary>
    public static void Load(byte firstTile, byte[] data, ushort startTile, byte count)
    {
        if (count == 0)
            return;
        fixed (byte* source = &data[0])
            LoadCore(firstTile, source + startTile * TileBytes, count);
    }

    /// <summary>Expand 1bpp tiles using ink and paper colors (0-3).</summary>
    /// <remarks>Nonempty loads leave interrupts disabled. Tile indices wrap at 256.</remarks>
    public static void Load1bpp(byte firstTile, byte[] mono, byte ink, byte paper, byte count)
    {
        if (count == 0)
            return;
        fixed (byte* source = &mono[0])
            Load1bppCore(firstTile, source, ink, paper, count);
    }

    /// <summary>Passthrough to <see cref="TileData.SetRow"/> — one row (two bit-plane bytes) of a
    /// single tile, for callers building a tile procedurally instead of loading table data.</summary>
    public static void SetRow(byte tile, byte row, byte low, byte high) =>
        TileData.SetRow(tile, row, low, high);

    /// <summary>Passthrough to <see cref="TileData.Clear"/> — zero every pixel of one tile.</summary>
    public static void Clear(byte tile) => TileData.Clear(tile);

    private static void LoadCore(byte firstTile, byte* source, byte count)
    {
        byte* dest = Gb.TileData + (ushort)(firstTile * TileBytes);
        ushort total = (ushort)(count * TileBytes);
        CopyToVram(dest, source, total);
    }

    internal static void CopyToVram(byte* dest, byte* source, ushort total)
    {
        bool gdma = CanUseGdma(source, dest);
        bool lcdOn = (Hardware.LCDC & 0x80) != 0;

        if (!lcdOn)
        {
            if (gdma)
                CopyGdmaChunks(dest, source, total, GdmaHardwareCeiling);
            else
                Mem.Copy(dest, source, total);
            return;
        }

        if (gdma)
            DripGdma(dest, source, total);
        else
            DripCpu(dest, source, total);
    }

    /// <summary>LCD-on CGB path: one <see cref="VblankGdmaChunkBytes"/> GDMA transfer per vblank. Its
    /// own function (not folded into <see cref="LoadCore"/>'s dispatch) so its loop body carries no
    /// dead branch for the CPU-copy case — every instruction in it is on the hot path.</summary>
    private static void DripGdma(byte* dest, byte* source, ushort total)
    {
        ushort copied = 0;
        while (copied < total)
        {
            ushort remaining = (ushort)(total - copied);
            ushort chunk = remaining < VblankGdmaChunkBytes ? remaining : VblankGdmaChunkBytes;
            Cgb.CopyToVram(source + copied, (ushort)(dest + copied), chunk);
            copied += chunk;
        }
    }

    private static void DripCpu(byte* dest, byte* source, ushort total)
    {
        ushort copied = 0;
        while (copied < total)
        {
            uint* target = (uint*)(dest + copied);
            uint value = *(uint*)(source + copied);
            Ppu.WaitVBlank();
            *target = value;
            copied += 4;
        }
    }

    /// <summary>CGB GDMA is only correct when both ends are 16-byte aligned (HDMA1-4 ignore the low 4
    /// address bits) — a plain <c>static readonly byte[]</c> ROM literal has no alignment guarantee
    /// (see <c>CilStaticFieldSupport.ReadAlignment</c>: only honored via <c>[KohAligned(n)]</c>), so
    /// this is a RUNTIME check rather than trusting caller convention/documentation — always correct
    /// for an unaligned source, not just "correct if the caller remembered to align it".</summary>
    private static bool CanUseGdma(byte* source, byte* dest) =>
        Video.IsCgb && ((ushort)source & 0xF) == 0 && ((ushort)dest & 0xF) == 0;

    /// <summary>Walks <paramref name="ceiling"/>-byte GDMA transfers back-to-back until
    /// <paramref name="total"/> bytes are copied — used for the LCD-off path only (no vblank pacing
    /// needed; the PPU doesn't own the bus). A single loop node in its own function, matching
    /// <see cref="Mem.Copy"/>'s own "one stride-1 walk per function" shape.</summary>
    private static void CopyGdmaChunks(byte* dest, byte* source, ushort total, ushort ceiling)
    {
        ushort copied = 0;
        while (copied < total)
        {
            ushort remaining = (ushort)(total - copied);
            ushort chunk = remaining < ceiling ? remaining : ceiling;
            Cgb.CopyToVram(source + copied, (ushort)(dest + copied), chunk);
            copied += chunk;
        }
    }

    private static void Load1bppCore(byte firstTile, byte* source, byte ink, byte paper, byte count)
    {
        byte inkLo = (ink & 1) != 0 ? (byte)0xFF : (byte)0;
        byte inkHi = (ink & 2) != 0 ? (byte)0xFF : (byte)0;
        byte paperLo = (paper & 1) != 0 ? (byte)0xFF : (byte)0;
        byte paperHi = (paper & 2) != 0 ? (byte)0xFF : (byte)0;

        for (byte t = 0; t < count; t++)
        {
            byte tile = (byte)(firstTile + t);
            byte* destination = Gb.TileData + tile * TileBytes;
            for (byte row = 0; row < 8; row++)
            {
                byte mono = *source++;
                byte inverse = (byte)(mono ^ 0xFF);
                byte low = (byte)((mono & inkLo) | (inverse & paperLo));
                byte high = (byte)((mono & inkHi) | (inverse & paperHi));
                Vram.WriteByte(destination++, low);
                Vram.WriteByte(destination++, high);
            }
        }
    }
}
