using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// The one internal helper <see cref="Bg"/> and <see cref="Win"/> both delegate to (graphics-library
/// design doc §3 "Bg.cs / Win.cs"), so the rect-fill/blit/attribute logic exists exactly once regardless
/// of which of the two 32x32 tile maps (<see cref="Gb.TileMap"/> at $9800 or <see cref="Gb.TileMap1"/> at
/// $9C00) it targets. Not public — the map selector is an implementation seam, not API surface; games call
/// <see cref="Bg"/>/<see cref="Win"/>.
///
/// <b>Impossible-by-construction write safety (shadow + vblank flush).</b> Game code never writes a
/// tilemap VRAM byte directly, so the mode-3 VRAM-write hazard (a CPU write into $9800-$9FFF while the PPU
/// owns the bus in mode 3, silently dropped on real hardware) is not even expressible from a game. Every
/// tile-INDEX write here lands in a full WRAM shadow of each map (<see cref="ShadowBg"/>/
/// <see cref="ShadowWin"/>, an always-consistent 1024-byte mirror of intended VRAM state), and is copied
/// into real VRAM later, during vblank, by <see cref="Flush"/> — which <see cref="Video.EndFrame"/> calls
/// after its own <see cref="Ppu.WaitVBlank"/>, exactly as it already calls <see cref="Sprites.Flush"/>.
/// This mirrors how <see cref="Sprites"/> shadows OAM in WRAM and flushes it in vblank.
///
/// The one branch that decides shadow-only vs. direct is the LCD on/off rule (<see cref="WriteCell"/>):
/// <list type="bullet">
/// <item><b>LCD off</b> (<c>Hardware.LCDC &amp; 0x80 == 0</c>): the PPU does not own the bus, so a write
/// updates the shadow AND writes VRAM directly and immediately, with no dirty marking. This is why an
/// init-time full-map paint (<see cref="Video.Init"/>'s clears, a game's LCD-off authoring) lands in VRAM
/// instantly with no drip, while still populating the shadow.</item>
/// <item><b>LCD on</b>: a write updates the shadow and marks the cell dirty ONLY; it does not touch VRAM.
/// The change reaches VRAM at the next <see cref="Flush"/>.</item>
/// </list>
/// Dirty state is tracked as ONE contiguous linear-index range per map: a dirty flag plus
/// <c>[MinIdx..MaxIdx]</c> bounds over the flattened <c>row*32+col</c> index space
/// (<see cref="DirtyBg"/>/<see cref="MinIdxBg"/>/<see cref="MaxIdxBg"/> and the Win equivalents), rather
/// than tracking each of the 32 rows separately. Because the shadow is a FULL mirror (LCD-off writes and
/// <see cref="Video.Init"/> populated every cell), flushing the whole <c>[MinIdx..MaxIdx]</c> run in one
/// shot is always valid — every cell in the run holds a real value, never garbage — even the cells inside
/// the range that were never individually written this frame. A flush that runs out of vblank time before
/// covering the whole range resumes the rest next frame by advancing <c>MinIdx</c> past what was copied
/// and leaving the map dirty. This replaces an earlier per-row <c>[minCol..maxCol]</c> design: scanning
/// all 32 rows every flush (even to skip clean ones) cost enough SM83 cycles on its own that <c>LY</c>
/// could leave the vblank window before later dirty rows were reached, silently dropping them for the
/// rest of the program's life. A single range with a single flush call has no per-row scan to pay for.
///
/// <b>Single-vblank GDMA flush on CGB, split into PREPARE (outside vblank) + COMMIT (inside vblank).</b>
/// <see cref="FlushBg"/>/<see cref="FlushWin"/> branch on <see cref="Video.IsCgb"/> (a per-flush runtime
/// branch, not a per-byte one — it sits outside <see cref="FlushRun"/>'s loop, so the DMG drip's hot loop
/// carries no extra state or check). On CGB the whole dirty run lands in ONE GDMA transfer instead of
/// draining over several frames — but the naive version of this (compute the aligned range, the register
/// bytes, AND fire the transfer all inside vblank, right where the DMG drip does its work) turned out to
/// be a real bug, not just a missed optimization: computing <c>alignedMin</c>/<c>count</c> from
/// <c>ushort</c> fields hits C#'s usual-arithmetic-conversion promotion to <c>int</c> for <c>&amp;</c>/
/// <c>|</c> (ECMA-334 — see <c>NarrowPass</c>'s own remarks on why this doesn't always narrow back down),
/// and the resulting i32 arithmetic on an 8-bit CPU with no 32-bit ALU costs HUNDREDS of M-cycles via the
/// generic width-N memory routine, not the few cycles a 16-bit round-to-alignment "should" cost. Measured
/// on the <c>gb-jrpg</c> sample's dialogue-paging flush: ~130 M-cycles computing the aligned source/dest
/// bytes plus ~386 M-cycles computing the aligned length byte — burning almost the ENTIRE vblank window
/// before the GDMA trigger (HDMA5) was even written, so the trigger landed at LY≈3 in mode 3 (Drawing) on
/// the FOLLOWING frame. <see cref="Emulator.Core.Bus.Mmu.WriteByteHdma"/> respects the mode-3 VRAM lock —
/// exactly like a real transfer that overruns vblank — so EVERY byte of that transfer was silently
/// dropped, and because the old code cleared the dirty flag unconditionally right after arming the
/// transfer, the drop was never retried: a dialogue page that never updated.
///
/// The fix: <see cref="PrepareFlush"/> does ALL the expensive arithmetic (the aligned range, and every
/// HDMA1-5 register BYTE) OUTSIDE vblank — called by <see cref="Video.EndFrame"/> BEFORE
/// <see cref="Ppu.WaitVBlank"/>, with no timing pressure at all — and stashes the five precomputed bytes
/// per map (<c>Prepared*Src/Dst/Len</c> below). <see cref="FlushBg"/>/<see cref="FlushWin"/>'s CGB branch,
/// running AFTER <see cref="Ppu.WaitVBlank"/> like the DMG drip, then does nothing but a safety-gated
/// five-register-store trigger: no arithmetic, no <see cref="Cgb.CopyToVram"/> call (which itself computes
/// the same bytes from raw pointers — this flush writes <see cref="Hardware"/>'s HDMA registers directly
/// instead so the commit path is provably just stores). The one thing the commit path DOES check is
/// whether there's still enough vblank left for the transfer (<c>(byte)(Hardware.LY - 144) &gt; 5</c> —
/// outside <c>[144,149]</c> leaves less than ~3.5 lines, worse than 400 M-cycles of margin for a
/// &lt;=1024-byte transfer at GDMA's ~2 bytes/M-cycle): if the window's too tight (e.g. a slow frame ate
/// into vblank before <see cref="Video.EndFrame"/> even ran), the commit is skipped and BOTH the dirty
/// flag and the prepared bytes are left exactly as they are, so next frame's flush retries the same
/// (already-computed, still outside-vblank-cheap-to-recompute) transfer rather than silently losing it.
///
/// A write between <see cref="PrepareFlush"/> and the matching <see cref="Flush"/> could otherwise widen
/// the dirty range out from under the already-prepared bytes; <see cref="MarkDirty"/> defensively clears
/// the relevant map's <c>Prepared*Ready</c> flag on every new dirty mark to make that impossible to get
/// wrong, though in practice it never fires here — <see cref="Video.EndFrame"/> calls
/// <see cref="PrepareFlush"/> as its very first statement, after all of that frame's game-code writes and
/// before anything else runs, so nothing dirties a cell between prepare and commit.
///
/// The run's <c>[MinIdx..MaxIdx]</c> bounds are widened to the enclosing 16-byte-aligned block range
/// (GDMA's source/dest alignment precondition), and because the shadow is a full mirror, copying the extra
/// cells the widening pulls in is exactly as safe as the existing <c>[MinIdx..MaxIdx]</c> over-copy above
/// — every cell in the wider range holds a real value too, never garbage. The shadow arrays
/// (<see cref="ShadowBg"/>/<see cref="ShadowWin"/>) carry <c>[KohAligned(16)]</c> so their WRAM address is
/// itself a multiple of 16, and $9800/$9C00 already are; since both sides of the copy are offset by the
/// identical <c>alignedMin</c>, source and dest stay 16-aligned together. Worst case, a single LCD-on
/// frame that dirties BOTH full maps costs ~2x1024 = 2048 bytes of GDMA at ~2 bytes/M-cycle = ~1024
/// M-cycles of TRANSFER time (the arithmetic itself no longer counts against this budget, having moved
/// outside vblank), against a ~1140 M-cycle vblank window — tight but still inside budget, and only
/// reachable by an LCD-on full-map rewrite (no current caller does this; full re-authors run with the LCD
/// off, which takes the direct-write path in <see cref="WriteCell"/> instead of ever marking dirty). A
/// per-frame GDMA byte cap could be added if such a caller ever appears, mirroring
/// <see cref="Graphics.TileSet"/>'s <c>VblankGdmaChunkBytes</c>. The DMG drip path (<see cref="FlushRun"/>)
/// is untouched by any of this — same byte-at-a-time, LY-polled copy as before.
///
/// <b>CGB attributes (bank 1) are NOT shadow-flushed in this pass.</b> <see cref="SetAttr"/>/
/// <see cref="FillAttr"/> keep the direct-VRAM path, still gated on <see cref="Ppu.WaitForVramAccess"/>
/// (which is a no-op the moment the LCD is off, so an init/LCD-off attribute pass writes freely). The
/// impossible-by-construction guarantee above covers tile-INDEX writes; per-frame CGB attribute writes with
/// the LCD on are not yet shadow-flushed — in practice attributes are written at init / with the LCD off,
/// so this is a documented, scoped gap, not a regression. Likewise <see cref="TileSet"/> (tile data) and
/// <see cref="Palettes"/> keep their existing, already-vblank-safe paths — this redesign is about the
/// tilemap only. Attribute writes are also a true no-op on DMG (<see cref="Video.IsCgb"/> is false): DMG
/// has no bank 1 to switch into, and writing through the map base with the bank still at 0 would clobber
/// the tile-index map at the same physical $9800/$9C00 address.
/// </summary>
internal static unsafe class MapWriter
{
    /// <summary>Full WRAM shadow of the BG map ($9800, <c>map</c> = 0): the always-consistent 1024-byte
    /// mirror of intended VRAM tile-index state, flushed to real VRAM in vblank by <see cref="Flush"/>. Not
    /// guaranteed zero at power-on (WRAM never is) — <see cref="Video.Init"/> clears it (LCD off, so shadow
    /// + direct together) before anything reads it. <see cref="KohAlignedAttribute"/>(16): on CGB,
    /// <see cref="PrepareFlushBg"/> takes a slice of this array's address as a GDMA SOURCE, which requires
    /// a 16-byte-aligned address (the HDMA1/2 registers ignore the low 4 bits) — the backend rounds this
    /// static's WRAM address up to a multiple of 16 so every slice offset by a multiple of 16 (see
    /// <see cref="PrepareFlushBg"/>'s <c>alignedMin</c>) stays 16-aligned too. No-op marker on the desktop
    /// reference build (see the attribute's own doc comment).</summary>
    [KohAligned(16)]
    private static byte[] ShadowBg = new byte[1024];

    /// <summary>Full WRAM shadow of the window map ($9C00, <c>map</c> = 1). Same contract as
    /// <see cref="ShadowBg"/>, including the <see cref="KohAlignedAttribute"/>(16) GDMA-source
    /// requirement.</summary>
    [KohAligned(16)]
    private static byte[] ShadowWin = new byte[1024];

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

    /// <summary>Set every cell of the full 32x32 map to one tile index (used by <see cref="Video.Init"/>
    /// to bring both the shadow and VRAM into a known blank state while the LCD is off). A bulk
    /// <see cref="Mem.Fill"/> straight into VRAM and the shadow array, not 1024 individual
    /// <see cref="WriteCell"/> dispatches — this only ever runs with the LCD off (<see cref="Video.Init"/>
    /// is its only caller), so it writes VRAM directly with no dirty marking needed, same as any other
    /// LCD-off write.</summary>
    internal static void Clear(byte map, byte tile)
    {
        byte* mapBase = map == 0 ? Gb.TileMap : Gb.TileMap1;
        byte[] shadow = map == 0 ? ShadowBg : ShadowWin;
        Mem.Fill(mapBase, tile, 1024);
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
            if (ShadowBg[idx] == tile)
                return;
            ShadowBg[idx] = tile;
        }
        else
        {
            if (ShadowWin[idx] == tile)
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
        fixed (byte* s = &ShadowBg[0])
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
        fixed (byte* s = &ShadowWin[0])
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

    /// <summary>Flush the BG map's dirty run into VRAM. On CGB, <see cref="PrepareFlushBg"/> already did
    /// every byte of arithmetic OUTSIDE vblank; this commit path is deliberately just a safety gate plus
    /// five back-to-back register stores (no <see cref="Cgb.CopyToVram"/> call, no shifts, no widening —
    /// see the class remarks for why that split exists). The gate reads <see cref="Hardware.LY"/> once:
    /// <c>(byte)(Hardware.LY - 144) &gt; 5</c> is true whenever LY is NOT in <c>[144,149]</c>, i.e. less
    /// than ~3.5 lines (&lt;400 M-cycles) of vblank remain — not enough margin for a &lt;=1024-byte GDMA
    /// transfer at ~2 bytes/M-cycle. If the gate fails, this returns WITHOUT clearing
    /// <see cref="DirtyBg"/> or <see cref="PreparedBgReady"/>, so the exact same already-computed bytes
    /// commit on the very next frame's <see cref="Flush"/> instead of being silently dropped by the
    /// hardware's own mode-3 VRAM lock (<see cref="Emulator.Core.Bus.Mmu.WriteByteHdma"/>) the way the
    /// original bug lost them. On DMG (and CGB is decided per-flush, not compiled away, since
    /// <see cref="Video.IsCgb"/> is a runtime flag set once by <see cref="Video.Init"/>), the original
    /// per-byte <see cref="FlushRun"/> drip runs as much as fits this vblank (it stops before mode 3 and
    /// reports how much landed); <see cref="MinIdxBg"/> advances by that, and <see cref="DirtyBg"/> clears
    /// once it catches up to <see cref="MaxIdxBg"/>, else the remainder resumes next frame.</summary>
    private static void FlushBg()
    {
        if (DirtyBg == 0)
            return;
        if (Video.IsCgb)
        {
            if (PreparedBgReady == 0 || (byte)(Hardware.LY - 144) > 5)
                return; // not (yet) prepared, or too little vblank left this frame — retry next frame
            Hardware.HDMA1 = PreparedBgSrcHi;
            Hardware.HDMA2 = PreparedBgSrcLo;
            Hardware.HDMA3 = PreparedBgDstHi;
            Hardware.HDMA4 = PreparedBgDstLo;
            Hardware.HDMA5 = PreparedBgLen;
            DirtyBg = 0;
            PreparedBgReady = 0;
            return;
        }
        ushort n = (ushort)(MaxIdxBg - MinIdxBg + 1);
        ushort copied;
        fixed (byte* s = &ShadowBg[0])
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
            if (PreparedWinReady == 0 || (byte)(Hardware.LY - 144) > 5)
                return;
            Hardware.HDMA1 = PreparedWinSrcHi;
            Hardware.HDMA2 = PreparedWinSrcLo;
            Hardware.HDMA3 = PreparedWinDstHi;
            Hardware.HDMA4 = PreparedWinDstLo;
            Hardware.HDMA5 = PreparedWinLen;
            DirtyWin = 0;
            PreparedWinReady = 0;
            return;
        }
        ushort n = (ushort)(MaxIdxWin - MinIdxWin + 1);
        ushort copied;
        fixed (byte* s = &ShadowWin[0])
            copied = FlushRun(Gb.TileMap1 + MinIdxWin, s + MinIdxWin, n);
        MinIdxWin = (ushort)(MinIdxWin + copied);
        if (MinIdxWin > MaxIdxWin)
            DirtyWin = 0;
    }

    /// <summary>Copy up to <paramref name="n"/> bytes shadow-&gt;VRAM, stopping the instant the PPU leaves
    /// the vblank window so a write can NEVER land in mode 3 (returns the count actually copied; the caller
    /// leaves the rest dirty for next frame). A plain stride-1 pointer walk bounded by a <c>dst != end</c>
    /// pointer compare — no separate byte counter, so only the two pointers <paramref name="dst"/>/
    /// <paramref name="src"/> are loop-carried (they map onto the two register pairs the loop-residency pass
    /// can hold). The ONLY pointer loop in this file, called once per map per frame (never inside a loop),
    /// so it can't collide with a sibling stride-1 loop under the backend register allocator.</summary>
    private static ushort FlushRun(byte* dst, byte* src, ushort n)
    {
        byte* start = dst;
        byte* end = dst + n;
        while (dst != end)
        {
            // (byte)(LY-144) is 0..8 inside vblank [144,152] and wraps large outside it — one MMIO read and
            // one compare for the whole `LY < 144 || LY > 152` test. Stop before writing if we've left the
            // window, reporting how much landed so a write can NEVER touch VRAM in mode 3.
            if ((byte)(Hardware.LY - 144) > 8)
                return (ushort)(dst - start);
            *dst = *src;
            dst++;
            src++;
        }
        return n;
    }

    /// <summary>Set one cell's CGB attribute byte (VRAM bank 1). Silent no-op on DMG. Direct-to-VRAM (see
    /// the class remarks: per-frame attribute shadowing is out of scope for this pass); still gated on
    /// <see cref="Ppu.WaitForVramAccess"/>, which returns immediately once the LCD is off, so an
    /// init/LCD-off attribute pass writes freely.</summary>
    internal static void SetAttr(byte map, byte col, byte row, byte attr)
    {
        if (!Video.IsCgb)
            return;
        byte* mapBase = map == 0 ? Gb.TileMap : Gb.TileMap1;
        Cgb.SelectVramBank(1);
        WriteAttrVerified(mapBase + Index(col, row), attr);
        Cgb.SelectVramBank(0);
    }

    /// <summary>Rect of one CGB attribute byte (VRAM bank 1). Silent no-op on DMG. Selects bank 1 once for
    /// the whole rect and restores bank 0 afterward. Same direct-VRAM / out-of-scope-for-shadow stance as
    /// <see cref="SetAttr"/>.</summary>
    internal static void FillAttr(byte map, byte col, byte row, byte w, byte h, byte attr)
    {
        if (!Video.IsCgb)
            return;
        byte* mapBase = map == 0 ? Gb.TileMap : Gb.TileMap1;
        Cgb.SelectVramBank(1);
        for (byte r = 0; r < h; r++)
        for (byte c = 0; c < w; c++)
            WriteAttrVerified(mapBase + Index((byte)(col + c), (byte)(row + r)), attr);
        Cgb.SelectVramBank(0);
    }

    /// <summary>One live attribute write that actually LANDS. "WaitForVramAccess then store" races
    /// mode 3: the wait can return at the tail of an accessible window and the store arrive after
    /// rendering resumed — the PPU (real hardware and the emulator alike) silently DROPS such a
    /// write, which showed up as deterministic attribute holes (a whole column of wall cells stuck
    /// on the wrong palette in the JRPG sample). Write-then-verify converges instead: a dropped
    /// store reads back 0xFF (mode-3 VRAM reads) or the old byte, and the loop re-gates and
    /// rewrites in the next window. With the LCD off both the gate and the race are no-ops and the
    /// first store sticks.</summary>
    private static void WriteAttrVerified(byte* cell, byte attr)
    {
        do
        {
            Ppu.WaitForVramAccess();
            *cell = attr;
        } while (*cell != attr);
    }

    private static ushort Index(byte col, byte row) => (ushort)((ushort)row * 32 + col);
}
