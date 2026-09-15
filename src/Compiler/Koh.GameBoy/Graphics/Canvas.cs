using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// A tile-backed pixel surface — graphics-library design doc §3 "Canvas.cs", the one remaining v1
/// module (§8 resolved decision 1: "Canvas IS in v1"). Consolidates the three near-identical
/// <c>samples/gb-3d/{double-buffered,full-frame,racing-beam}/Surface.cs</c> files plus
/// <c>samples/gb-3d/shared/SpanFill.cs</c> into one type: a game/demo gets a rectangular grid of BG
/// tiles it treats as a raw 2bpp bitmap (<see cref="SetPixel"/>/<see cref="DrawLine"/>/
/// <see cref="FillTriangle"/>/...), then <see cref="Present"/> gets those bytes into VRAM the
/// hardware-appropriate way. One canvas per program, static — VRAM only holds one such surface, and
/// double-buffering consumes both tile-data pages (design doc §3 remarks).
///
/// <b>Precondition (same as every other Graphics module):</b> call <see cref="Init"/> after
/// <see cref="Video.Init"/> — this module reads <see cref="Video.IsCgb"/> rather than re-deriving
/// <see cref="Cgb.IsColor"/> (architecture rule: Graphics never re-derives the KEY1 check), and relies
/// on <c>Video.Init</c> having already turned the LCD off. Call <see cref="Init"/> before your own
/// <c>Mem.Alloc</c> calls — it is the library's ONE arena allocation (design doc §2 "Allocation
/// discipline": "the arena is used exactly once"); <c>Mem.Reset()</c> destroys the canvas.
///
/// <b>Scoped down deliberately</b> (design doc §1): one canvas per program, fixed at <see cref="Init"/>,
/// no arbitrary blitting, no interaction with <see cref="Sprites"/>/<see cref="Bg"/> — a game that wants
/// both a Canvas and a tile-based HUD lays them out on non-overlapping regions of the same 32x32 map
/// itself; this module has no awareness of what <c>Bg</c>/<c>Win</c> put elsewhere on the map.
///
/// <b>Body provenance</b> (design doc §3: "SetPixel/FillSpan bodies come verbatim from
/// racing-beam/Surface.cs + SpanFill.cs; DrawLine/FillTriangle are lifted from CubeRenderer"):
/// <see cref="SetPixel"/> is <c>racing-beam/Surface.cs</c>'s body with its bounds check removed (design
/// doc §3 explicitly changes the contract to "no bounds check, documented demo-grade") and the hardcoded
/// tile-row stride replaced by <see cref="_widthTiles"/> so one body serves every canvas size.
/// <see cref="DrawLine"/> and the triangle-sorting/scan shape of <see cref="FillTriangle"/> are
/// <c>CubeRenderer</c>'s <c>DrawLine</c>/<c>FillTriangle</c> with their vertex-array indexing replaced by
/// direct coordinate parameters. <see cref="FillSpan"/> keeps <c>SpanFill.Fill</c>'s exact span-covering
/// shape (per-byte-column cover masks, one full-byte store per interior column, partial read-modify-write
/// at the two edges) but GENERALIZES its color derivation: the original took a single 0-3 "color" whose
/// dither behavior was an artifact of which values <c>CubeRenderer</c> happened to pass (1, 2, 3) — it
/// could not express solid colors 2 or 3, or a dither between 0 and 1. This module's public contract is
/// the fuller one the design doc specifies ("shade 0-7: even = solid 0-3, odd = 2x2 ordered dither
/// between neighbors" — 7 distinct visual levels: solid0, dither(0,1), solid1, dither(1,2), solid2,
/// dither(2,3), solid3, with shade 7 folding back to solid3 since there is no color 4 to dither toward),
/// so <see cref="FillSpan"/> derives each byte-column's two plane bytes from an explicit
/// low/high-neighbor pair and a bit-select identity (<c>hi XOR (ditherMask AND (hi XOR lo))</c> — for a
/// byte that is uniformly all-0 or all-1, this picks <c>lo</c>'s bits at the dithered positions and
/// <c>hi</c>'s bits everywhere else) instead of the original's parity-quirk derivation. The original
/// values are a special case of this general formula (shade 3 -&gt; lo=1,hi=2 reproduces the original
/// "color=2" case).
/// </summary>
public static unsafe class Canvas
{
    private const byte TileBytes = 16;

    /// <summary>The whole $8000-$97FF tile-data window (3 tile "blocks" in Pan Docs terms, 6144 bytes,
    /// 384 raw tile slots): cleared unconditionally by <see cref="Init"/> regardless of canvas size or
    /// mode. A superset of what any single-buffered canvas (which only ever occupies $8000-up) or
    /// double-buffered canvas (which occupies $8000-up under $8000-addressing and $9000-up under
    /// $8800-addressing) actually touches, but computing the tight per-mode range isn't worth the extra
    /// branch: LCD is off here (see the class remarks' precondition), so the bulk clear is free of PPU
    /// timing concerns either way, and clearing the whole window guarantees no boot-logo/prior-program
    /// remnant can ever show through <see cref="BlankTile"/> or an under-filled edge tile — the same
    /// "clear more than the content range" caution every one of the three Surface.cs files documents for
    /// its own narrower slice of this window.</summary>
    private const ushort VramTileWindowBytes = 0x1800;

    /// <summary>The shared border/filler tile index. 255 resolves to the SAME physical bytes
    /// ($8FF0-$8FFF) under both $8000 and $8800 addressing (indexes 128-255 read $8800-$8FFF in either
    /// mode — Pan Docs), so one cleared tile is a safe filler for cells the canvas's own centered grid
    /// doesn't cover, whichever page a double-buffered canvas is currently showing.</summary>
    private const byte BlankTile = 255;

    /// <summary>Hardware ceiling for one <see cref="Cgb.CopyToVram"/> transfer (HDMA5's 7-bit length
    /// field: 128 blocks x 16 bytes). Used for the LCD-off present path only, matching
    /// <see cref="TileSet"/>'s own <c>GdmaHardwareCeiling</c> reasoning — no vblank budget to respect
    /// when the PPU isn't racing the copy, so a large buffer just walks this ceiling as its chunk size.</summary>
    private const ushort GdmaHardwareCeiling = 2048;

    /// <summary>Safe per-vblank CGB GDMA chunk while the LCD stays ON: 120 blocks (1920 bytes) at 32
    /// dots/block = 3840 dots against the ~4104-dot usable vblank budget — lifted verbatim from
    /// <c>samples/gb-3d/double-buffered/Surface.cs</c>'s <c>Present()</c>/<see cref="TileSet"/>'s own
    /// identical constant (same hardware, same derivation; see either's remarks for the full budget
    /// arithmetic). Deliberately not the full 2048-byte hardware ceiling — that would leave only an
    /// 8-dot margin, not the proven number.</summary>
    private const ushort VblankGdmaChunkBytes = 1920;

    /// <summary>Safe per-vblank CPU-copy chunk (DMG, or a CGB build reached before <see cref="Init"/> —
    /// this module has no unaligned-source case since <see cref="_pixels"/> is always 16-byte aligned by
    /// construction).
    ///
    /// EMPIRICALLY RE-DERIVED for this module's own call shape — do not assume it transfers to another
    /// module. An earlier version of this constant reused <see cref="TileSet"/>'s <c>VblankCpuChunkBytes</c>
    /// figure (4) on the theory that <see cref="Present"/> -&gt; <c>PresentCpu</c> -&gt; <c>Mem.Copy</c>
    /// is the same call-depth shape as <c>TileSet.Load</c> -&gt; <c>LoadCore</c> -&gt; <c>DripCpu</c> -&gt;
    /// <c>Mem.Copy</c>. That theory was never actually exercised at realistic scale by either module's own
    /// unit tests (both only drove a handful of chunks) and turned out to be WRONG at scale: retrofitting
    /// <c>samples/gb-3d/double-buffered</c> onto this module (graphics-library design doc §5, item 2) and
    /// running the real 1920-byte, ~480-chunk-per-page DMG present against
    /// <c>samples/gb-3d/verify</c>'s <c>Mode3WriteGuard</c> caught n=4 landing 295 real VRAM writes during
    /// PPU mode 3 over a 2000-frame run — this module's own extra per-iteration static-field reads
    /// (<see cref="_pixels"/>/<see cref="_bufferBytes"/>, absent from <c>Surface.cs</c>'s original
    /// all-local-variable loop that n=7 was tuned against) push the per-chunk dot cost past budget even
    /// at TileSet's more conservative n=4. Bisected directly against that same guard at full 1920-byte
    /// scale: n=4 fails (295 violations), n=3 is clean (zero violations across the full
    /// <c>samples/gb-3d/verify</c> double-buffered/dmg run, both the 1100- and 2000-frame snapshots) —
    /// n=3 is this module's own proven-safe figure, not a re-guess.</summary>
    private const byte VblankCpuChunkBytes = 3;

    private static byte* _pixels;
    private static byte _widthTiles;
    private static byte _heightTiles;
    private static ushort _bufferBytes;
    private static CanvasMode _mode;

    /// <summary>Which VRAM page is currently HIDDEN (the one the next <see cref="Present"/> writes into)
    /// in <see cref="CanvasMode.DoubleBuffered"/> mode: 0 = $8000 (visible under $8000 addressing, i.e.
    /// LCDC bit 4 set), 1 = $9000 (visible under $8800 addressing, LCDC bit 4 clear) — the classic
    /// LCDC.4-flip double-buffer trick (design doc §3; ported verbatim from
    /// <c>double-buffered/Surface.cs</c>). Unused in <see cref="CanvasMode.SingleBuffered"/> mode, where
    /// every <see cref="Present"/> always targets $8000.</summary>
    private static byte _page;

    /// <summary>Canvas width in pixels (<c>widthTiles * 8</c>), set by <see cref="Init"/>.</summary>
    public static int Width;

    /// <summary>Canvas height in pixels (<c>heightTiles * 8</c>), set by <see cref="Init"/>.</summary>
    public static int Height;

    /// <summary>Allocates the pixel buffer (<paramref name="widthTiles"/> * <paramref name="heightTiles"/>
    /// * 16 bytes, 2bpp) from the WRAM arena, 16-byte aligned by over-allocation and rounding — the SAME
    /// pattern every <c>Surface.cs</c> uses today (design doc §2: "until/unless [KohAligned] grows an
    /// arena variant"), needed because the CGB present path hands this address to
    /// <see cref="Cgb.CopyToVram"/>'s HDMA1/2 source registers, which ignore the low 4 address bits. Also
    /// clears the whole tile-data window (see <see cref="VramTileWindowBytes"/>) and lays the tile grid
    /// out CENTERED on the 20x18 visible BG window (<c>startCol = (20 - widthTiles) / 2</c>,
    /// <c>startRow = (18 - heightTiles) / 2</c> — integer division, matching exactly what all three
    /// existing <c>Surface.cs</c> files hand-picked for their own fixed sizes: e.g. 12x10 -&gt; (4, 4),
    /// 16x15 -&gt; (2, 1)), then blanks the rest of the 32x32 map to <see cref="BlankTile"/> so cells a
    /// horizontal scroll wraps into view (or any cell outside the grid) show blank rather than boot-logo
    /// or prior-program remnants.
    ///
    /// <paramref name="mode"/> = <see cref="CanvasMode.DoubleBuffered"/> reserves LCDC bit 4 for this
    /// module's own use (<see cref="Present"/> flips it every call) — a caller in that mode must not also
    /// call <see cref="Lcd.SelectTileData"/> itself, and should avoid other code that unconditionally
    /// rewrites the whole LCDC byte (e.g. repeated <see cref="Video.ShowSprites"/>/
    /// <see cref="Video.HideWindow"/> calls after this canvas starts presenting) between
    /// <see cref="Present"/> calls, since <c>Video</c>'s LCDC mirror does not track this module's flip
    /// and would stomp it back — a known cross-module seam, not fixed in this slice (Canvas is
    /// intentionally standalone: design doc §1, "no interaction with Sprites/Bg").
    ///
    /// The content-tile budget note on <see cref="CanvasMode"/> applies here, not as a runtime check
    /// (this module's write paths are documented no-bounds-check, demo-grade, matching
    /// <see cref="SetPixel"/>): a double-buffered canvas's content tiles share indexes 0-127 across BOTH
    /// addressing modes (128-255 is the shared blank/border range), so <c>widthTiles * heightTiles</c>
    /// must stay under ~120-127 in that mode; a single-buffered canvas has the fuller 0-254 range (255
    /// reserved for <see cref="BlankTile"/>).</summary>
    public static void Init(byte widthTiles, byte heightTiles, CanvasMode mode)
    {
        _widthTiles = widthTiles;
        _heightTiles = heightTiles;
        _mode = mode;
        Width = widthTiles * 8;
        Height = heightTiles * 8;
        _bufferBytes = (ushort)(widthTiles * heightTiles * TileBytes);

        _pixels = Mem.Alloc(_bufferBytes + 15);
        // Round up to a 16-byte boundary via a ushort round-trip (not Surface.cs's original `ulong`
        // cast — proven fine under the OLD CSharpFrontend, where it also had to stay valid for a
        // 64-bit desktop process pointer, but the CIL frontend's IR verifier rejects a pointer-to-i64
        // zext ('zext requires integer operand and result' — confirmed by a spike compile of this exact
        // expression). A GB address is never wider than 16 bits on either build (the desktop reference
        // build's `Gb.Base`-relative pointers fit the same space `Mem.Alloc`'s own `_heap` field does),
        // so `ushort` round-trips exactly like <see cref="Sprites"/>'s own proven `(ushort)p` pointer
        // pattern (its OAM-shadow page derivation) — no precision lost, unlike a real 64-bit process
        // pointer would.
        _pixels = (byte*)(ushort)(((ushort)_pixels + 15) & ~15);

        Mem.Fill(Gb.Vram, 0, VramTileWindowBytes);
        Tilemap.Clear(BlankTile);

        byte startCol = (byte)((20 - widthTiles) / 2);
        byte startRow = (byte)((18 - heightTiles) / 2);
        for (byte r = 0; r < heightTiles; r++)
        for (byte c = 0; c < widthTiles; c++)
            Tilemap.SetTile((byte)(startCol + c), (byte)(startRow + r), (byte)(r * widthTiles + c));

        // Lcd.On()/Video.Start() defaults to $8000 addressing (page 0 visible), so the first Present()
        // in DoubleBuffered mode should render into the HIDDEN page 1 ($9000) — matches
        // double-buffered/Surface.cs's own Initialize.
        _page = mode == CanvasMode.DoubleBuffered ? (byte)1 : (byte)0;
    }

    /// <summary>Zero every pixel to <paramref name="color"/> (0-3). Delegates to
    /// <see cref="FillRect"/> over the whole canvas at the corresponding solid shade
    /// (<c>color * 2</c> — an even shade per <see cref="FillSpan"/>'s contract, so no dithering is
    /// engaged) rather than a second hand-written fill loop: reusing the already-covered
    /// <see cref="FillSpan"/> path is both simpler and avoids ever introducing a second textually
    /// distinct stride-1 pointer-walk loop into this module (see <c>Mem.Copy</c>'s remarks on why two
    /// such loops in the SAME function corrupt each other under the SM83 backend).</summary>
    public static void Clear(byte color) => FillRect(0, 0, Width, Height, (byte)(color * 2));

    /// <summary>Set one pixel to <paramref name="color"/> (0-3). NO bounds check (design doc §3:
    /// "demo-grade") — an out-of-range <paramref name="x"/>/<paramref name="y"/> scribbles into whatever
    /// byte the tile/offset arithmetic lands on, which is still inside the canvas's own pixel buffer for
    /// any x/y within a couple of tiles of the edge, but is the caller's responsibility to avoid for a
    /// real out-of-range coordinate. Body verbatim from <c>racing-beam/Surface.cs</c>'s <c>SetPixel</c>
    /// minus its bounds check, with the hardcoded tile-row stride (8) replaced by the canvas's own
    /// <see cref="_widthTiles"/>.</summary>
    public static void SetPixel(int x, int y, byte color)
    {
        int tile = (y >> 3) * _widthTiles + (x >> 3);
        int offset = tile * TileBytes + (y & 7) * 2;
        byte mask = (byte)(0x80 >> (x & 7));
        if ((color & 1) != 0)
            *(_pixels + offset) |= mask;
        else
            *(_pixels + offset) &= (byte)~mask;
        if ((color & 2) != 0)
            *(_pixels + offset + 1) |= mask;
        else
            *(_pixels + offset + 1) &= (byte)~mask;
    }

    /// <summary>Byte-granular horizontal span fill from <paramref name="x0"/> to <paramref name="x1"/>
    /// (inclusive, either order) on row <paramref name="y"/>, at <paramref name="shade"/> (0-7: even
    /// shades are solid colors 0-3 — <c>shade / 2</c> — odd shades are a 2x2-ish ordered dither between
    /// the two neighboring solid colors, e.g. shade 3 dithers between color 1 and color 2; shade 7 has no
    /// color 4 to dither toward and folds back to solid color 3). See the class remarks for how this
    /// generalizes <c>SpanFill.Fill</c>'s span-covering shape (which this method keeps verbatim: one
    /// cover-masked read-modify-write per partially-covered edge byte-column, one direct store per fully-
    /// covered interior byte-column) to the fuller 0-7 domain the design doc specifies.</summary>
    public static void FillSpan(int y, int x0, int x1, byte shade)
    {
        if (x0 > x1)
        {
            int t = x0;
            x0 = x1;
            x1 = t;
        }

        byte colorLo = (byte)(shade >> 1);
        byte colorHi = (shade & 1) != 0 ? (byte)(colorLo + 1) : colorLo;
        if (colorHi > 3)
            colorHi = 3;

        byte dither = colorLo != colorHi ? (byte)(0x88 >> (y & 3)) : (byte)0x00;
        byte hiBit0 = (colorHi & 1) != 0 ? (byte)0xFF : (byte)0x00;
        byte loBit0 = (colorLo & 1) != 0 ? (byte)0xFF : (byte)0x00;
        byte plane0 = (byte)(hiBit0 ^ (dither & (byte)(hiBit0 ^ loBit0)));
        byte hiBit1 = (colorHi & 2) != 0 ? (byte)0xFF : (byte)0x00;
        byte loBit1 = (colorLo & 2) != 0 ? (byte)0xFF : (byte)0x00;
        byte plane1 = (byte)(hiBit1 ^ (dither & (byte)(hiBit1 ^ loBit1)));

        int firstByte = x0 >> 3;
        int lastByte = x1 >> 3;
        int tile = (y >> 3) * _widthTiles + firstByte;
        int o = tile * TileBytes + (y & 7) * 2;

        if (firstByte == lastByte)
        {
            byte cover = (byte)((0xFF >> (x0 & 7)) & (0xFF << (7 - (x1 & 7))));
            *(_pixels + o) = (byte)((*(_pixels + o) & ~cover) | (plane0 & cover));
            *(_pixels + o + 1) = (byte)((*(_pixels + o + 1) & ~cover) | (plane1 & cover));
            return;
        }

        byte coverFirst = (byte)(0xFF >> (x0 & 7));
        *(_pixels + o) = (byte)((*(_pixels + o) & ~coverFirst) | (plane0 & coverFirst));
        *(_pixels + o + 1) = (byte)((*(_pixels + o + 1) & ~coverFirst) | (plane1 & coverFirst));

        for (int b = firstByte + 1; b < lastByte; b++)
        {
            o += TileBytes;
            *(_pixels + o) = plane0;
            *(_pixels + o + 1) = plane1;
        }

        byte coverLast = (byte)(0xFF << (7 - (x1 & 7)));
        o += TileBytes;
        *(_pixels + o) = (byte)((*(_pixels + o) & ~coverLast) | (plane0 & coverLast));
        *(_pixels + o + 1) = (byte)((*(_pixels + o + 1) & ~coverLast) | (plane1 & coverLast));
    }

    /// <summary>Rect of <paramref name="w"/> x <paramref name="h"/> pixels at <paramref name="shade"/>,
    /// top-left at (<paramref name="x"/>, <paramref name="y"/>) — one <see cref="FillSpan"/> call per
    /// row.</summary>
    public static void FillRect(int x, int y, int w, int h, byte shade)
    {
        for (int row = y; row < y + h; row++)
            FillSpan(row, x, x + w - 1, shade);
    }

    /// <summary>Bresenham line from (<paramref name="x0"/>, <paramref name="y0"/>) to
    /// (<paramref name="x1"/>, <paramref name="y1"/>) at <paramref name="color"/> (0-3). Lifted verbatim
    /// from <c>CubeRenderer.DrawLine</c> (its vertex-array indexing replaced by direct coordinates and a
    /// parameterized color instead of the hardcoded 3), including its per-pixel canvas-bounds check —
    /// unlike <see cref="SetPixel"/>, this one is NOT documented as bounds-check-free, so it keeps the
    /// check its source body already had.</summary>
    public static void DrawLine(int x0, int y0, int x1, int y1, byte color)
    {
        int dx = x1 > x0 ? x1 - x0 : x0 - x1;
        int sx = x0 < x1 ? 1 : -1;
        int dy = y1 > y0 ? y0 - y1 : y1 - y0;
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            if (x0 >= 0 && x0 < Width && y0 >= 0 && y0 < Height)
                SetPixel(x0, y0, color);
            if (x0 == x1 && y0 == y1)
                break;
            int twice = error * 2;
            if (twice >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (twice <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>Filled triangle at <paramref name="shade"/> (see <see cref="FillSpan"/> for the 0-7
    /// scale), vertices in any order. Lifted verbatim from <c>CubeRenderer.FillTriangle</c>'s sort-by-y
    /// + <c>EdgeX</c> scanline shape, with its vertex-array indexing replaced by direct coordinate
    /// parameters and its hardcoded <c>Surface.FillSpan</c> color argument replaced by
    /// <paramref name="shade"/>.</summary>
    public static void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, byte shade)
    {
        if (y1 < y0)
        {
            int t = x0;
            x0 = x1;
            x1 = t;
            t = y0;
            y0 = y1;
            y1 = t;
        }
        if (y2 < y0)
        {
            int t = x0;
            x0 = x2;
            x2 = t;
            t = y0;
            y0 = y2;
            y2 = t;
        }
        if (y2 < y1)
        {
            int t = x1;
            x1 = x2;
            x2 = t;
            t = y1;
            y1 = y2;
            y2 = t;
        }

        for (int y = y0; y <= y2; y++)
        {
            int xa = EdgeX(x0, y0, x2, y2, y);
            int xb = y < y1 ? EdgeX(x0, y0, x1, y1, y) : EdgeX(x1, y1, x2, y2, y);
            if (xa > xb)
            {
                int t = xa;
                xa = xb;
                xb = t;
            }
            if (y < 0 || y >= Height)
                continue;
            if (xa < 0)
                xa = 0;
            if (xb >= Width)
                xb = Width - 1;
            if (xa <= xb)
                FillSpan(y, xa, xb, shade);
        }
    }

    private static int EdgeX(int x0, int y0, int x1, int y1, int y)
    {
        int dy = y1 - y0;
        if (dy == 0)
            return x0;
        return x0 + (x1 - x0) * (y - y0) / dy;
    }

    /// <summary>Gets the canvas's WRAM pixel buffer into VRAM, machine- and mode-appropriate (design
    /// doc §3):
    /// <list type="bullet">
    /// <item>CGB: <see cref="Cgb.CopyToVram"/> GDMA, auto-split at the 2048-byte hardware ceiling when
    /// the LCD is off (no vblank budget to respect), or in <see cref="VblankGdmaChunkBytes"/> chunks
    /// across as many vblanks as needed when the LCD is on.</item>
    /// <item>DMG single-buffered: chunked vblank CPU copy directly into the visible $8000 tiles (the
    /// "racing beam" tradeoff — a large buffer's copy spans multiple frames, so the visible surface is a
    /// mix of old and new content for those frames; the caller's explicit choice by picking
    /// <see cref="CanvasMode.SingleBuffered"/>).</item>
    /// <item>DMG/CGB double-buffered: chunked copy to the HIDDEN page, then one LCDC.4 flip once the
    /// whole buffer has landed — tear-free, at the cost of the hidden page taking as many vblanks as its
    /// size demands before it becomes visible.</item>
    /// </list>
    /// A buffer that needs more than one vblank's budget is therefore always a MULTI-FRAME present via
    /// the same chunk loop, never a silent <see cref="Lcd.Off"/> flash — that stays an explicit sample
    /// technique (<c>full-frame/Surface.cs</c>'s DMG path), not library behavior (design doc §3).</summary>
    public static void Present()
    {
        bool lcdOn = (Hardware.LCDC & 0x80) != 0;
        ushort vramDest =
            _mode == CanvasMode.DoubleBuffered
                ? (_page == 0 ? (ushort)0x8000 : (ushort)0x9000)
                : (ushort)0x8000;

        if (Video.IsCgb)
            PresentGdma(vramDest, lcdOn);
        else
            PresentCpu(vramDest, lcdOn);

        if (_mode == CanvasMode.DoubleBuffered)
        {
            Lcd.SelectTileData(_page == 0);
            _page ^= 1;
        }
    }

    private static void PresentGdma(ushort vramDest, bool lcdOn)
    {
        ushort ceiling = lcdOn ? VblankGdmaChunkBytes : GdmaHardwareCeiling;
        ushort copied = 0;
        while (copied < _bufferBytes)
        {
            if (lcdOn)
                Ppu.WaitVBlank();
            ushort remaining = (ushort)(_bufferBytes - copied);
            ushort chunk = remaining < ceiling ? remaining : ceiling;
            Cgb.CopyToVram(_pixels + copied, (ushort)(vramDest + copied), chunk);
            copied += chunk;
        }
    }

    private static void PresentCpu(ushort vramDest, bool lcdOn)
    {
        byte* dest = Gb.Vram + (vramDest - 0x8000);
        if (!lcdOn)
        {
            Mem.Copy(dest, _pixels, _bufferBytes);
            return;
        }
        ushort copied = 0;
        while (copied < _bufferBytes)
        {
            Ppu.WaitVBlank();
            ushort remaining = (ushort)(_bufferBytes - copied);
            byte chunk = remaining < VblankCpuChunkBytes ? (byte)remaining : VblankCpuChunkBytes;
            Mem.Copy(dest + copied, _pixels + copied, chunk);
            copied += chunk;
        }
    }
}

/// <summary>Buffering strategy for a <see cref="Canvas"/> (design doc §3).</summary>
public enum CanvasMode : byte
{
    /// <summary>Draws land directly on the visible tiles — simplest, but a large
    /// <see cref="Canvas.Present"/> can show a visible mix of old/new content across the frames it takes
    /// (the "racing beam" tradeoff).</summary>
    SingleBuffered,

    /// <summary>Draws land on a hidden VRAM page; <see cref="Canvas.Present"/> flips LCDC bit 4 once the
    /// whole buffer has landed, so the visible surface only ever shows a complete frame. Content tiles
    /// share indexes 0-127 across both pages (128-255 is the shared blank/border range) — keep
    /// <c>widthTiles * heightTiles</c> under ~120-127.</summary>
    DoubleBuffered,
}
