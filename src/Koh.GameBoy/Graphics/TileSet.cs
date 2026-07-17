using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// Bulk 2bpp tile loading — getting pixels into VRAM tile data ($8000 addressing, aliased by
/// <see cref="Gb.TileData"/>/<see cref="Gb.Vram"/>) without a per-tile <see cref="TileData.SetRow"/>
/// hand-loop (graphics-library design doc §3 "TileSet.cs"). Build ON <see cref="Ppu"/>/<see cref="Cgb"/>
/// (never re-derive PPU-mode timing here) and go straight to <see cref="Gb"/> pointers for the bulk
/// copy itself, per the architecture rule.
///
/// <b>API deviation from design §3 (documented, not silently dropped):</b> the design sketches a
/// zero-count <c>Load(byte firstTile, byte[] data)</c> that infers the tile count from
/// <c>data.Length</c>. The CIL frontend does not support <c>.Length</c>/<c>ldlen</c> on an array
/// received as a PARAMETER — only a local traced back to its own <c>newarr</c> (or a static field read
/// in the SAME method) carries a compile-time element count; a library method receiving <c>byte[]
/// data</c> has no such trace, and no runtime length header exists to fall back on (that's how
/// <c>System.String</c> now works, post "string flows across CIL call boundaries" — a length-prefixed
/// ROM blob — but retrofitting the same prefix onto every <c>byte[]</c> would shift every existing raw
/// data-blob element offset by 2 bytes, breaking unrelated code). Confirmed with a spike fixture
/// (compile -&gt; <c>CilFrontend</c> -&gt; link -&gt; <c>GameBoySystem</c>, asserted against
/// <c>tests/Koh.Compiler.Tests/Frontends/CilLoweringTests.cs</c>'s existing
/// <c>ArrayLength_OnUntraceableArray_ReportsDiagnostic_DoesNotThrow</c> coverage of the same limitation)
/// before committing to this shape — see <see cref="CilTileSetTests"/>. Every overload below therefore
/// takes an explicit tile <c>count</c> instead; the caller (which DOES have <c>data</c> traced to its
/// own <c>static readonly byte[]</c> declaration) computes it once, e.g.
/// <c>TileSet.Load(0, BoardTiles, (byte)(BoardTiles.Length / 16))</c> — the length arithmetic just moves
/// one call frame up, where the frontend can actually see it.
///
/// A <c>byte[]</c> parameter is already a raw pointer at the IR level (every reference type lowers to
/// <c>Pointer(I8)</c> — see <c>CilTypeMapper.Map</c>), so <c>fixed (byte* p = &amp;data[0])</c>
/// (the single-element-address form, which lowers to a plain <c>ldelema</c> — NOT the array form, which
/// would emit a null/empty-array <c>ldlen</c> guard and hit the same unsupported opcode) is how every
/// method below turns its <c>byte[]</c> parameter into the <c>byte*</c> <see cref="Mem.Copy"/>/
/// <see cref="Cgb.CopyToVram"/> need.
/// </summary>
public static unsafe class TileSet
{
    private const int TileBytes = 16;

    /// <summary>Hardware ceiling for one <see cref="Cgb.CopyToVram"/> transfer (128 blocks x 16 bytes
    /// — HDMA5's 7-bit length field). Used for the LCD-OFF path, which has no vblank timing budget to
    /// respect (the PPU isn't racing the copy), so a multi-tile load just walks this ceiling as its
    /// chunk size.</summary>
    private const ushort GdmaHardwareCeiling = 2048;

    /// <summary>Safe per-vblank CGB GDMA chunk while the LCD stays ON: 120 blocks (1920 bytes) at 32
    /// dots/block (2 bytes per M-cycle, CPU halted, per <see cref="Cgb.CopyToVram"/>'s remarks) =
    /// 3840 dots, against the ~4104-dot usable vblank budget (10 lines x 456 dots, minus the one line
    /// <see cref="Ppu.WaitVBlank"/>'s edge detection eats) — a 264-dot margin. Lifted verbatim from
    /// <c>samples/gb-3d/double-buffered/Surface.cs</c>'s <c>Present()</c>, which measured and verified
    /// this exact figure against its Mode3WriteGuard harness (deliberately NOT the full 2048-byte
    /// hardware ceiling above: 128 blocks x 32 dots = 4096 dots would leave only an 8-dot margin against
    /// the same budget — razor-thin, not the proven number).</summary>
    private const ushort VblankGdmaChunkBytes = 1920;

    /// <summary>Safe per-vblank CPU-copy chunk (DMG, or CGB with an unaligned source/dest — GDMA needs
    /// both 16-byte aligned). <c>Surface.cs</c>'s DMG fallback measured cost(n) = 1528 + 300n dots for
    /// a BARE <c>Mem.Copy(dst, src, n)</c> call and tuned n=7 against that isolated figure — but
    /// <see cref="DripCpu"/> reaches its <c>Mem.Copy</c> through an extra call layer
    /// (<c>Load</c> -&gt; <c>LoadCore</c> -&gt; <c>DripCpu</c>, versus Surface.cs's flat <c>Present()</c>),
    /// and empirically (this module's own <c>Mode3WriteGuard</c>-style e2e test — see
    /// <c>CilTileSetTests.Load_LcdOn_ChunksAcrossVblanksAndNeverWritesDuringMode3</c>) n=7 overruns the
    /// vblank window by a byte or two on this shape: not enough margin once the extra call/dispatch
    /// overhead is counted. Falls back to n=4 — Surface.cs's OWN prior, more conservative figure before
    /// its n=7 tuning pass ("up from the old 4-bytes/vblank manual-loop drip") — rather than re-deriving
    /// a new tuned constant from scratch; slower (a 384-tile full tileset takes well under a minute at 4
    /// bytes/vblank) but proven safe against the guard, which is what actually gates this constant.</summary>
    private const byte VblankCpuChunkBytes = 4;

    /// <summary>Copy <paramref name="count"/> tiles (16 bytes each, 2bpp) from the START of
    /// <paramref name="data"/> into VRAM at <paramref name="firstTile"/> ($8000 addressing). See the
    /// class remarks for why this takes an explicit count instead of self-measuring
    /// <paramref name="data"/>.
    ///
    /// LCD off: one straight copy — CGB GDMA (<see cref="Cgb.CopyToVram"/>, chunked at the 2048-byte
    /// hardware ceiling) when both the ROM source and the VRAM destination happen to be 16-byte
    /// aligned, else a CPU <see cref="Mem.Copy"/> (always correct regardless of alignment; a caller
    /// wanting the GDMA fast path aligns its data with <c>[KohAligned(16)]</c>). LCD on: vblank-chunked
    /// internally (<see cref="Ppu.WaitVBlank"/> once per chunk) — CGB GDMA in
    /// <see cref="VblankGdmaChunkBytes"/>-byte chunks when aligned, else CPU-copy drip in
    /// <see cref="VblankCpuChunkBytes"/>-byte chunks. Returns when the whole load is done either way.</summary>
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

    /// <summary>Expand a 1bpp source (8 bytes/tile — one bit per pixel, MSB first, same row order as
    /// 2bpp) to 2bpp planar tiles in VRAM: a set source bit becomes color <paramref name="ink"/>
    /// (0-3), a clear bit becomes color <paramref name="paper"/> (0-3). Halves a font/icon table's ROM
    /// size versus shipping it pre-expanded to 2bpp. Immediate-checked (gates on
    /// <see cref="Ppu.WaitForVramAccess"/> per row, like <see cref="Graphics.Palettes"/>'s writes) rather
    /// than vblank-chunked — a 1bpp table is small (a 96-glyph font is 768 bytes) and this is a one-time
    /// load, not a per-frame budget concern. See the class remarks for why <paramref name="count"/> is
    /// explicit rather than inferred from <paramref name="mono"/>.</summary>
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
            Ppu.WaitVBlank();
            ushort remaining = (ushort)(total - copied);
            ushort chunk = remaining < VblankGdmaChunkBytes ? remaining : VblankGdmaChunkBytes;
            Cgb.CopyToVram(source + copied, (ushort)(dest + copied), chunk);
            copied += chunk;
        }
    }

    /// <summary>LCD-on DMG (or unaligned-CGB) path: one <see cref="VblankCpuChunkBytes"/> CPU copy per
    /// vblank, per the 1528+300n dot-cost model this constant is derived from. Its own function for the
    /// same lean-hot-loop reason as <see cref="DripGdma"/> — the measured cost model assumes exactly
    /// this shape (<c>Ppu.WaitVBlank()</c>; compute a chunk; one <see cref="Mem.Copy"/> call), not one
    /// sharing a loop body with an untaken GDMA branch.</summary>
    private static void DripCpu(byte* dest, byte* source, ushort total)
    {
        ushort copied = 0;
        while (copied < total)
        {
            Ppu.WaitVBlank();
            ushort remaining = (ushort)(total - copied);
            byte chunk = remaining < VblankCpuChunkBytes ? (byte)remaining : VblankCpuChunkBytes;
            Mem.Copy(dest + copied, source + copied, chunk);
            copied += chunk;
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
        byte inkLo = (byte)(ink & 1);
        byte inkHi = (byte)((ink >> 1) & 1);
        byte paperLo = (byte)(paper & 1);
        byte paperHi = (byte)((paper >> 1) & 1);

        for (byte t = 0; t < count; t++)
        {
            byte tile = (byte)(firstTile + t);
            for (byte row = 0; row < 8; row++)
            {
                byte mono = *(source + t * 8 + row);
                byte low = ExpandPlane(mono, inkLo, paperLo);
                byte high = ExpandPlane(mono, inkHi, paperHi);
                Ppu.WaitForVramAccess();
                TileData.SetRow(tile, row, low, high);
            }
        }
    }

    /// <summary>One bit-plane byte of a 1bpp-&gt;2bpp expansion: for each bit position, a SET
    /// <paramref name="mono"/> bit selects <paramref name="inkBit"/>, a CLEAR bit selects
    /// <paramref name="paperBit"/> (each already isolated to bit 0 by the caller — this only cares
    /// whether it's 0 or 1). Built as two all-ones/all-zeros masks rather than a per-bit loop: a mask
    /// is 0xFF when its bit is 1, 0x00 when it's 0, so ANDing <paramref name="mono"/>/<c>~mono</c>
    /// against the two masks and ORing the results picks the right source for every bit position at
    /// once.</summary>
    private static byte ExpandPlane(byte mono, byte inkBit, byte paperBit)
    {
        byte inkMask = inkBit != 0 ? (byte)0xFF : (byte)0x00;
        byte paperMask = paperBit != 0 ? (byte)0xFF : (byte)0x00;
        return (byte)((mono & inkMask) | (~mono & paperMask));
    }
}
