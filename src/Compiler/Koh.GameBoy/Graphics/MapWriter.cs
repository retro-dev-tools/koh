using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>Tile-map writes.</summary>
internal static unsafe class MapWriter
{
    [KohAligned(16)]
    private static byte[]? ShadowBg;

    [KohAligned(16)]
    private static byte[]? ShadowWin;

    private static byte ClearBgTile;
    private static byte ClearWinTile;

    // Single contiguous dirty range per map, over the flattened row*32+col linear index space. Written
    // only while the LCD is on (see WriteCell); consumed and cleared (fully or partially) by Flush()
    // during vblank. Dirty == 0 means no pending writes; Dirty != 0 means the inclusive linear index run
    // [MinIdx .. MaxIdx] holds pending writes (plus, harmlessly, any already-correct cells between
    // individually-written ones). BYTE flags, not bool, matching this file's pre-existing convention
    // (the old RowDirtyBg/RowDirtyWin arrays were byte[] too): a C# bool field's `!x` negation compiles to
    // a measurably more expensive branch sequence on the SM83 backend than a byte `== 0` comparison
    // (confirmed with a disassembly-driven diagnostic during this redesign — a `bool`-typed dirty flag
    // was costing enough per check, compounded across the EndFrame -> Flush -> FlushBg/FlushWin call
    // chain, that a single-cell flush was measured to burn nearly the ENTIRE vblank window before any
    // byte was even copied). Kept as a documented, load-bearing choice, not a stylistic one.
    private static byte DirtyBg;
    private static ushort MinIdxBg;
    private static ushort MaxIdxBg;
    private static byte DirtyWin;
    private static ushort MinIdxWin;
    private static ushort MaxIdxWin;

    // CGB-only: the five HDMA1-5 register bytes for each map's pending GDMA transfer, precomputed by
    // <see cref="PrepareFlushBg"/>/<see cref="PrepareFlushWin"/> OUTSIDE vblank (see the class remarks'
    // "single-vblank GDMA flush, split into PREPARE + COMMIT" section for why this split exists at all —
    // computing these five bytes is NOT free on an 8-bit CPU, and doing it inside vblank was the actual
    // bug). *Ready == 0 means nothing is prepared (either nothing is dirty, or MarkDirty invalidated a
    // stale prepare); *Ready != 0 means the five bytes below are valid and safe to commit as-is. BYTE
    // flags, matching DirtyBg/DirtyWin's own documented convention in this file.
    private static byte PreparedBgReady;
    private static byte PreparedBgSrcHi;
    private static byte PreparedBgSrcLo;
    private static byte PreparedBgDstHi;
    private static byte PreparedBgDstLo;
    private static byte PreparedBgLen;
    private static byte PreparedWinReady;
    private static byte PreparedWinSrcHi;
    private static byte PreparedWinSrcLo;
    private static byte PreparedWinDstHi;
    private static byte PreparedWinDstLo;
    private static byte PreparedWinLen;

    /// <summary>Set one cell's tile index on <paramref name="map"/> (0 = BG $9800, 1 = Win $9C00). Routes
    /// through the shadow: direct-to-VRAM when the LCD is off, dirty-marked for the next vblank flush when
    /// on. No <see cref="Ppu.WaitForVramAccess"/> gate — game code waits for nothing.</summary>
    internal static void SetTile(byte map, byte col, byte row, byte tile) =>
        WriteCell(map, col, row, tile);

    /// <summary>Rect of one tile index, <paramref name="w"/> x <paramref name="h"/> cells starting at
    /// (<paramref name="col"/>, <paramref name="row"/>).</summary>
    internal static void Fill(byte map, byte col, byte row, byte w, byte h, byte tile)
    {
        for (byte r = 0; r < h; r++)
        for (byte c = 0; c < w; c++)
            WriteCell(map, (byte)(col + c), (byte)(row + r), tile);
    }

    /// <summary>Blit a row-major <paramref name="w"/> x <paramref name="h"/> rect of ROM tile indices
    /// from <paramref name="tiles"/> starting at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    internal static void DrawMap(byte map, byte col, byte row, byte w, byte h, byte* tiles)
    {
        for (byte r = 0; r < h; r++)
        for (byte c = 0; c < w; c++)
            WriteCell(
                map,
                (byte)(col + c),
                (byte)(row + r),
                *(tiles + (ushort)((ushort)r * w + c))
            );
    }

    /// <summary>Full-map clear.</summary>
    internal static void Clear(byte map, byte tile)
    {
        bool lcdOn = (Hardware.LCDC & 0x80) != 0;
        if (map == 0)
            ClearBgTile = tile;
        else
            ClearWinTile = tile;
        byte[]? shadow = map == 0 ? ShadowBg : ShadowWin;
        if (shadow is not null)
        {
            fixed (byte* p = &shadow[0])
                Mem.Fill(p, tile, 1024);
        }
        else if (lcdOn)
        {
            InitializeShadow(map);
        }
        if (lcdOn)
        {
            MarkDirty(map, 0);
            MarkDirty(map, 1023);
            return;
        }
        byte* mapBase = map == 0 ? Gb.TileMap : Gb.TileMap1;
        Mem.Fill(mapBase, tile, 1024);
        if (map == 0)
        {
            DirtyBg = 0;
            PreparedBgReady = 0;
        }
        else
        {
            DirtyWin = 0;
            PreparedWinReady = 0;
        }
    }

    private static void InitializeShadow(byte map)
    {
        byte[] shadow;
        byte tile;
        if (map == 0)
        {
            ShadowBg = new byte[1024];
            shadow = ShadowBg;
            tile = ClearBgTile;
        }
        else
        {
            ShadowWin = new byte[1024];
            shadow = ShadowWin;
            tile = ClearWinTile;
        }
        if (tile != 0)
            fixed (byte* p = &shadow[0])
                Mem.Fill(p, tile, 1024);
    }

    /// <summary>The write dispatch: always update the shadow; then either write VRAM directly (LCD off) or
    /// mark the cell dirty for the vblank flush (LCD on). See the class remarks for the full rationale.</summary>
    private static void WriteCell(byte map, byte col, byte row, byte tile)
    {
        ushort idx = (ushort)((ushort)row * 32 + col);
        // Same-value write = no-op: the shadow (and, once flushed, VRAM) already hold this value, so
        // dirtying the cell would only drag the single dirty run's MinIdx back to this index for nothing.
        // This is load-bearing: a HUD redrawn unconditionally every frame (an unchanged score, say) sits
        // at a LOW linear index; without this skip its rewrite resets MinIdx every frame and starves every
        // higher-index cell (the board) forever — a livelock, not just slowness.
        if (map == 0)
        {
            if (ShadowBg is null)
            {
                if (tile == ClearBgTile && idx < 1024)
                    return;
                InitializeShadow(0);
            }
            if (ShadowBg![idx] == tile)
                return;
            ShadowBg[idx] = tile;
        }
        else
        {
            if (ShadowWin is null)
            {
                if (tile == ClearWinTile && idx < 1024)
                    return;
                InitializeShadow(1);
            }
            if (ShadowWin![idx] == tile)
                return;
            ShadowWin[idx] = tile;
        }

        if ((Hardware.LCDC & 0x80) == 0)
        {
            // LCD off: PPU does not own the bus, so write VRAM directly and immediately (no dirty mark).
            byte* mapBase = map == 0 ? Gb.TileMap : Gb.TileMap1;
            *(mapBase + idx) = tile;
        }
        else
        {
            MarkDirty(map, idx);
        }
    }

    /// <summary>Extend <paramref name="map"/>'s single dirty linear-index run to include
    /// <paramref name="idx"/> (<c>row*32+col</c>, already computed by <see cref="WriteCell"/>): set the
    /// dirty flag and grow the <c>[MinIdx..MaxIdx]</c> bounds. Also invalidates that map's prepared GDMA
    /// bytes (<see cref="PreparedBgReady"/>/<see cref="PreparedWinReady"/>) — defensive, not load-bearing
    /// under the normal call order (<see cref="Video.EndFrame"/> runs <see cref="PrepareFlush"/> before
    /// any game code gets another chance to write a cell), but it makes "a write widens the range after
    /// prepare already ran" provably safe instead of relying on call-order discipline alone: a widened,
    /// no-longer-prepared range just falls back to being recomputed the NEXT time <see cref="PrepareFlush"/>
    /// runs, same as the ordinary "nothing prepared yet" case.</summary>
    private static void MarkDirty(byte map, ushort idx)
    {
        if (map == 0)
        {
            PreparedBgReady = 0;
            if (DirtyBg == 0)
            {
                DirtyBg = 1;
                MinIdxBg = idx;
                MaxIdxBg = idx;
            }
            else
            {
                if (idx < MinIdxBg)
                    MinIdxBg = idx;
                if (idx > MaxIdxBg)
                    MaxIdxBg = idx;
            }
        }
        else
        {
            PreparedWinReady = 0;
            if (DirtyWin == 0)
            {
                DirtyWin = 1;
                MinIdxWin = idx;
                MaxIdxWin = idx;
            }
            else
            {
                if (idx < MinIdxWin)
                    MinIdxWin = idx;
                if (idx > MaxIdxWin)
                    MaxIdxWin = idx;
            }
        }
    }

    /// <summary>CGB-only: do ALL of the expensive arithmetic for both maps' pending GDMA transfers —
    /// the aligned range and every HDMA1-5 register byte — with NO vblank timing pressure at all. Called
    /// by <see cref="Video.EndFrame"/> as its very first statement, BEFORE <see cref="Ppu.WaitVBlank"/>
    /// (see the class remarks' "split into PREPARE + COMMIT" section for why this exists: doing this same
    /// arithmetic INSIDE vblank, right before the trigger, was the actual bug this split fixes — i32
    /// arithmetic from a <c>ushort &amp;</c>/<c>|</c>'s C# promotion costs hundreds of M-cycles on an
    /// 8-bit CPU, enough to blow the whole vblank window). A true no-op on DMG — gated on
    /// <see cref="Video.IsCgb"/> once, up front, rather than inside each per-map helper, since the DMG
    /// drip path never calls this at all (only <see cref="Video.EndFrame"/> does, unconditionally, so the
    /// gate has to live here).</summary>
    internal static void PrepareFlush()
    {
        if (!Video.IsCgb)
            return;
        PrepareFlushBg();
        PrepareFlushWin();
    }

    /// <summary>Precompute the BG map's five HDMA1-5 register bytes from its dirty range, if any. Mirrors
    /// the alignment argument in the class remarks: round <c>[MinIdxBg..MaxIdxBg]</c> out to the enclosing
    /// 16-byte-aligned block (<paramref name="alignedMin"/> onward), point the source at that offset into
    /// the (16-aligned) <see cref="ShadowBg"/> array, the dest at the same offset into $9800, and the
    /// length at <c>(count &gt;&gt; 4) - 1</c> (GDMA's block-count-minus-one encoding). idx never exceeds
    /// 1023, so count never exceeds 1024 (well under the 2048-byte GDMA ceiling). Leaves
    /// <see cref="PreparedBgReady"/> at 0 (already the state <see cref="MarkDirty"/> leaves it in, or the
    /// state a completed <see cref="FlushBg"/> reset it to) when there is nothing dirty to prepare.</summary>
    private static void PrepareFlushBg()
    {
        if (DirtyBg == 0)
            return;
        ushort alignedMin = (ushort)(MinIdxBg & ~0xF);
        ushort count = (ushort)(((MaxIdxBg | 0xF) - alignedMin) + 1);
        ushort src;
        fixed (byte* s = &ShadowBg![0])
            src = (ushort)(s + alignedMin);
        ushort dst = (ushort)((ushort)Gb.TileMap + alignedMin);
        PreparedBgSrcHi = (byte)(src >> 8);
        PreparedBgSrcLo = (byte)src;
        PreparedBgDstHi = (byte)(dst >> 8);
        PreparedBgDstLo = (byte)dst;
        PreparedBgLen = (byte)((count >> 4) - 1);
        PreparedBgReady = 1;
    }

    /// <summary>Precompute the window map's five HDMA1-5 register bytes. Structural twin of
    /// <see cref="PrepareFlushBg"/> against <see cref="Gb.TileMap1"/>/<see cref="ShadowWin"/>.</summary>
    private static void PrepareFlushWin()
    {
        if (DirtyWin == 0)
            return;
        ushort alignedMin = (ushort)(MinIdxWin & ~0xF);
        ushort count = (ushort)(((MaxIdxWin | 0xF) - alignedMin) + 1);
        ushort src;
        fixed (byte* s = &ShadowWin![0])
            src = (ushort)(s + alignedMin);
        ushort dst = (ushort)((ushort)Gb.TileMap1 + alignedMin);
        PreparedWinSrcHi = (byte)(src >> 8);
        PreparedWinSrcLo = (byte)src;
        PreparedWinDstHi = (byte)(dst >> 8);
        PreparedWinDstLo = (byte)dst;
        PreparedWinLen = (byte)((count >> 4) - 1);
        PreparedWinReady = 1;
    }

    /// <summary>Copy every dirty tile-index cell of both maps from the WRAM shadow into real VRAM. MUST run
    /// during vblank — <see cref="Video.EndFrame"/> calls this after its own <see cref="Ppu.WaitVBlank"/>,
    /// exactly as it calls <see cref="Sprites.Flush"/>. Not part of the public surface; <c>internal</c> for
    /// <see cref="Video.EndFrame"/> and for tests. A partial flush (if vblank runs out) is safe — every
    /// written cell is atomic and in vblank; the remaining rows stay dirty for next frame.</summary>
    internal static void Flush()
    {
        FlushBg();
        FlushWin();
    }

    /// <summary>Prepared background commit.</summary>
    private static void FlushBg()
    {
        if (DirtyBg == 0)
            return;
        if (Video.IsCgb)
        {
            if (PreparedBgReady == 0)
                return;
            Hardware.HDMA1 = PreparedBgSrcHi;
            Hardware.HDMA2 = PreparedBgSrcLo;
            Hardware.HDMA3 = PreparedBgDstHi;
            Hardware.HDMA4 = PreparedBgDstLo;
            if ((byte)(Hardware.LY - 144) > 3)
                Ppu.WaitVBlank();
            Hardware.HDMA5 = PreparedBgLen;
            DirtyBg = 0;
            PreparedBgReady = 0;
            return;
        }
        ushort n = (ushort)(MaxIdxBg - MinIdxBg + 1);
        ushort copied;
        fixed (byte* s = &ShadowBg![0])
            copied = FlushRun(Gb.TileMap + MinIdxBg, s + MinIdxBg, n);
        MinIdxBg = (ushort)(MinIdxBg + copied);
        if (MinIdxBg > MaxIdxBg)
            DirtyBg = 0;
    }

    /// <summary>Flush the window map's dirty run. Structural twin of <see cref="FlushBg"/> (including its
    /// CGB prepared-commit branch) against <see cref="Gb.TileMap1"/>/<see cref="ShadowWin"/>.</summary>
    private static void FlushWin()
    {
        if (DirtyWin == 0)
            return;
        if (Video.IsCgb)
        {
            if (PreparedWinReady == 0)
                return;
            Hardware.HDMA1 = PreparedWinSrcHi;
            Hardware.HDMA2 = PreparedWinSrcLo;
            Hardware.HDMA3 = PreparedWinDstHi;
            Hardware.HDMA4 = PreparedWinDstLo;
            if ((byte)(Hardware.LY - 144) > 3)
                Ppu.WaitVBlank();
            Hardware.HDMA5 = PreparedWinLen;
            DirtyWin = 0;
            PreparedWinReady = 0;
            return;
        }
        ushort n = (ushort)(MaxIdxWin - MinIdxWin + 1);
        ushort copied;
        fixed (byte* s = &ShadowWin![0])
            copied = FlushRun(Gb.TileMap1 + MinIdxWin, s + MinIdxWin, n);
        MinIdxWin = (ushort)(MinIdxWin + copied);
        if (MinIdxWin > MaxIdxWin)
            DirtyWin = 0;
    }

    /// <summary>Bounded vblank copy.</summary>
    [KohIntrinsic("vblankcopy")]
    private static ushort FlushRun(byte* dst, byte* src, ushort n) =>
        throw new PlatformNotSupportedException();

    /// <summary>Set one CGB attribute; DMG no-op.</summary>
    /// <remarks>CGB writes leave interrupts disabled.</remarks>
    internal static void SetAttr(byte map, byte col, byte row, byte attr)
    {
        if (!Video.IsCgb)
            return;
        byte* mapBase = map == 0 ? Gb.TileMap : Gb.TileMap1;
        Cgb.SelectVramBank(1);
        Vram.WriteByte(mapBase + Index(col, row), attr);
        Cgb.SelectVramBank(0);
    }

    /// <summary>Fill CGB attributes; DMG no-op.</summary>
    /// <remarks>Nonempty CGB writes leave interrupts disabled.</remarks>
    internal static void FillAttr(byte map, byte col, byte row, byte w, byte h, byte attr)
    {
        if (!Video.IsCgb)
            return;
        byte* mapBase = map == 0 ? Gb.TileMap : Gb.TileMap1;
        Cgb.SelectVramBank(1);
        for (byte r = 0; r < h; r++)
        for (byte c = 0; c < w; c++)
            Vram.WriteByte(mapBase + Index((byte)(col + c), (byte)(row + r)), attr);
        Cgb.SelectVramBank(0);
    }

    private static ushort Index(byte col, byte row) => (ushort)((ushort)row * 32 + col);
}
