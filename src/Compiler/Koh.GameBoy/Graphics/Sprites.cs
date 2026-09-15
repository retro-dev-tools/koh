using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// A 1-byte handle into the 40-slot shadow OAM (<see cref="Sprites"/> — see that class's remarks for
/// the backing storage). Copy-by-value is fine: <see cref="Index"/> is the only field, so a
/// <c>Sprite</c> value is nothing more than "which of the 40 slots".
///
/// <b>API deviation from design doc §3 (documented, not silently dropped):</b> the design sketches
/// <c>static Sprite Get(byte index)</c> — a struct RETURNED BY VALUE. The CIL frontend has a hard
/// diagnostic against any struct-by-value return (<c>CilLoweringContext.EnsureSignature</c>: "the
/// backend's calling convention has no proven aggregate-by-value return shape"). <see cref="Sprites.Get"/>
/// therefore takes an <c>out Sprite</c> parameter instead — a one-method micro-deviation (mirrors
/// <c>TileSet</c>'s own documented deviation for the same reason: a confirmed CIL-frontend limitation,
/// not a design preference). Every OTHER member below — an ordinary struct INSTANCE method with a
/// <c>this</c> parameter, <c>Index</c> field access, calling out to <see cref="Sprites"/>'s internal
/// write helpers — needed no deviation at all: a companion CIL-frontend fix landed alongside this module
/// (<c>CilLoweringContext.EnsureSignature</c> / <c>CilMethodLowerer.Run</c> now map a struct instance
/// method's implicit <c>this</c> through the same struct-aware <c>CilTypeMapper.MapParam</c> already
/// used for ordinary struct parameters, instead of the plain <c>CilTypeMapper.Map</c>, which has no
/// struct branch and previously threw "unsupported CIL type" for ANY struct instance method — confirmed
/// with a spike fixture before and after the fix). Fixing that small, localized gap (two call sites,
/// each swapping <c>Map</c> for the already-proven <c>MapParam</c>) rather than flattening every method
/// here onto <c>Sprites</c> as <c>Set(byte index, ...)</c> free functions keeps the design's
/// <c>cursor.Set(...)</c>/<c>cursor.Move(...)</c> ergonomics intact — the whole point of a handle struct.
/// </summary>
public struct Sprite
{
    public byte Index;

    /// <summary>Position (screen pixels) + tile in one call. Adds hardware's +8 (X) / +16 (Y) offset so
    /// screen coordinates are natural: an on-screen sprite at logical (0,0) lands at the correct visible
    /// top-left. A negative <paramref name="x"/>/<paramref name="y"/> clips off the left/top edge
    /// naturally — narrowing e.g. <c>y = -1</c> to <c>(byte)(y + 16)</c> gives 15, still a valid (if
    /// nearly fully off-screen) hardware Y; further negative values wrap toward 255/254/... which the
    /// PPU's Y-16 visible-row math also treats as off the top of the 160x144 window, so no separate
    /// bounds check is needed. Does not touch the sprite's attribute byte — a caller wanting
    /// flip/priority/CGB-palette bits alongside a fresh position calls <see cref="SetAttr"/> too.</summary>
    public void Set(int x, int y, byte tile)
    {
        Sprites.WriteY(Index, (byte)(y + 16));
        Sprites.WriteX(Index, (byte)(x + 8));
        Sprites.WriteTile(Index, tile);
    }

    /// <summary>Reposition only (tile/attribute untouched). Same +8/+16 offset and negative-coordinate
    /// clipping as <see cref="Set"/>.</summary>
    public void Move(int x, int y)
    {
        Sprites.WriteY(Index, (byte)(y + 16));
        Sprites.WriteX(Index, (byte)(x + 8));
    }

    /// <summary>Swap this sprite's tile index only.</summary>
    public void SetTile(byte tile) => Sprites.WriteTile(Index, tile);

    /// <summary>Set this sprite's attribute byte — compose with <see cref="ObjAttr"/>'s flags/palette
    /// helper, e.g. <c>cursor.SetAttr((byte)(ObjAttr.FlipX | ObjAttr.CgbPalette(2)))</c>.</summary>
    public void SetAttr(byte attr) => Sprites.WriteAttr(Index, attr);

    /// <summary>Y = 0 — fully off the visible window (a real hardware Y of 0 puts the sprite's bottom
    /// row at screen row -16, entirely above row 0), regardless of X/tile/attr. The library's one hide
    /// convention — matches <see cref="Sprites.HideAll"/> and <see cref="Video.Init"/>'s own OAM
    /// clear.</summary>
    public void Hide() => Sprites.WriteY(Index, 0);
}

/// <summary>
/// The 40-entry shadow OAM: the single biggest gap the graphics-library design doc calls out ("zero C#
/// code touches Gb.Oam today"). Games mutate this WRAM shadow freely at any time (design doc §2,
/// "deferred-to-EndFrame" write-safety stance — OAM is inaccessible to the CPU in PPU modes 2 AND 3, the
/// least safe layer to write immediately); <see cref="Video.EndFrame"/> flushes it to real hardware OAM
/// exactly once per frame, during the vblank <c>EndFrame</c> already waited for.
///
/// <b>Backing store:</b> a <c>[KohAligned(256)]</c> static 160-byte array, laid out EXACTLY like real
/// OAM — 40 four-byte entries of (Y, X, Tile, Attr), Pan Docs order — so <see cref="Flush"/> can hand the
/// whole buffer to <see cref="Hardware.RunOamDma"/> as one verbatim byte-for-byte copy; no translation
/// step, no second copy (design doc §2, "Allocation discipline": "Sprite state lives IN the shadow").
/// The 256-byte alignment is exactly what <c>RunOamDma</c>'s source-PAGE argument needs (a real hardware
/// OAM DMA only ever names a page, i.e. address bits 15-8; DMA always copies 160 bytes starting at that
/// page's own offset 0) — see <see cref="Flush"/> for how the page number is derived.
///
/// <b>Confirmed ROM-only:</b> <see cref="Flush"/>'s page derivation (<c>(byte)((ushort)p &gt;&gt; 8)</c>
/// off <c>&amp;Shadow[0]</c>) is correct ONLY when this assembly is compiled by the Koh CIL frontend to a
/// ROM — the SM83 backend's static-WRAM allocator places <c>Shadow</c> at a real, page-aligned hardware
/// address (<c>CilGraphicsSlice2Tests.KohAligned_RoundsStaticWramGlobalCursorUpToAlignment</c> proves the
/// rounding; this module's own e2e coverage proves the derived page matches and the DMA'd OAM content
/// matches the shadow). On the DESKTOP reference build the SAME C# expression takes the address of an
/// ordinary CLR-heap array — unrelated to <see cref="Gb.MemoryArray"/>, the buffer <see cref="Gb.DmaOam"/>
/// actually copies from — so the derived "page" is a meaningless truncation of a real process pointer.
/// <see cref="KohAlignedAttribute"/>'s own doc comment already declares this out of scope for wave 1
/// ("the desktop host never DMAs from it directly"), and no existing test or sample calls
/// <see cref="Flush"/>/<see cref="Video.EndFrame"/> on the desktop build yet — so this is a real,
/// consciously-accepted gap, not a silently shipped one: making desktop sprites render would need the
/// shadow's storage to live inside <see cref="Gb.MemoryArray"/> itself (the <c>Mem.Alloc</c>-backed,
/// hand-aligned pattern the design doc explicitly asks this library NOT to perpetuate — see
/// <c>samples/gb-3d/double-buffered/Surface.cs</c>'s <c>pixels</c> field) or a change to
/// <see cref="KohAlignedAttribute"/>'s desktop storage model — a wave-1-level call, out of this slice's
/// narrow scope.
/// </summary>
public static unsafe class Sprites
{
    public const int Count = 40;
    private const int SlotBytes = 4;

    // Real OAM byte layout, byte for byte: Y, X, Tile, Attr (Pan Docs OAM entry order) — Flush() DMA-
    // copies this buffer verbatim into 0xFE00, so reordering these offsets would reorder real OAM the
    // same way.
    private const byte OffsetY = 0;
    private const byte OffsetX = 1;
    private const byte OffsetTile = 2;
    private const byte OffsetAttr = 3;

    [KohAligned(256)]
    private static byte[] Shadow = new byte[Count * SlotBytes];

    /// <summary>Set once by any <see cref="Sprite"/> mutator or <see cref="HideAll"/>; cleared by
    /// <see cref="Flush"/> once the DMA has actually run. Lets <see cref="Video.EndFrame"/> skip the DMA
    /// entirely on a frame where no sprite changed.</summary>
    private static bool _dirty;

    /// <summary>Returns the handle for shadow slot <paramref name="index"/> (0-39; out of range wraps
    /// into a neighboring slot's bytes rather than faulting — no bounds check, matching this library's
    /// "fixed pool, slot ownership is the game's business" stance for a 40-slot pool small enough to
    /// enumerate by hand). See <see cref="Sprite"/>'s class remarks for why this is <c>out</c> rather
    /// than a return value.</summary>
    public static void Get(byte index, out Sprite sprite) => sprite.Index = index;

    /// <summary>Zero every one of the 40 shadow entries (Y, X, Tile, Attr all 0 — Y=0 hides every
    /// sprite regardless of the other three bytes) and mark the shadow dirty so the next
    /// <see cref="Flush"/>/<see cref="Video.EndFrame"/> actually copies it to real OAM. Called by
    /// <see cref="Video.Init"/> so the WRAM shadow starts in a known (all-hidden) state — WRAM is not
    /// guaranteed zero at power-on, so without this the first sprite a game sets would DMA 39 garbage
    /// neighbor slots onto real OAM alongside it.</summary>
    public static void HideAll()
    {
        fixed (byte* p = &Shadow[0])
            Mem.Fill(p, 0, Count * SlotBytes);
        _dirty = true;
    }

    /// <summary>Fires <see cref="Hardware.RunOamDma"/> on the shadow's own page when (and only when)
    /// something changed since the last flush. Not part of the public surface — <see cref="Video.EndFrame"/>
    /// is what a game calls; this exists <c>internal</c> for tests and for a game managing vblank itself
    /// via <see cref="Ppu"/> directly (design doc §3). MUST run during vblank — the caller's
    /// responsibility, exactly like <see cref="Video.EndFrame"/> already waiting before reaching this
    /// call.
    ///
    /// The page is the shadow's own LINKED address, right-shifted 8 — <c>[KohAligned(256)]</c> already
    /// guarantees the low byte is 0, so this is exact, not an approximation. Taking <c>&amp;Shadow[0]</c>
    /// (the single-element-address form — plain <c>ldelema</c>, not the array form that would emit an
    /// unsupported <c>ldlen</c> null-check guard; see <c>TileSet</c>'s matching remark) rather than
    /// hand-writing the address is what makes this correct regardless of where in WRAM the backend
    /// happens to place <c>Shadow</c> relative to any other static in the program — no magic offset to
    /// keep in sync by hand, exactly what <c>[KohAligned]</c> exists to replace (see this class's own
    /// remarks on the desktop-only gap this same expression has).</summary>
    internal static void Flush()
    {
        if (!_dirty)
            return;
        fixed (byte* p = &Shadow[0])
        {
            byte page = (byte)((ushort)p >> 8);
            Hardware.RunOamDma(page);
        }
        _dirty = false;
    }

    internal static void WriteY(byte index, byte y) => Write(index, OffsetY, y);

    internal static void WriteX(byte index, byte x) => Write(index, OffsetX, x);

    internal static void WriteTile(byte index, byte tile) => Write(index, OffsetTile, tile);

    internal static void WriteAttr(byte index, byte attr) => Write(index, OffsetAttr, attr);

    private static void Write(byte index, byte fieldOffset, byte value)
    {
        fixed (byte* p = &Shadow[0])
            *(p + (ushort)(index * SlotBytes + fieldOffset)) = value;
        _dirty = true;
    }
}

/// <summary>Composes the OAM attribute byte written by <see cref="Sprite.SetAttr"/> — Pan Docs OAM
/// attribute layout: bit 7 = BG/window priority, bit 6 = Y flip, bit 5 = X flip, bit 4 = DMG palette
/// select (OBP0/OBP1), bits 0-2 = CGB object palette (bit 3, VRAM bank, is not exposed here — same
/// stance as <see cref="TileAttr"/>: this library sources tile data from VRAM bank 0 only).</summary>
public static class ObjAttr
{
    public const byte Priority = 0x80;
    public const byte FlipY = 0x40;
    public const byte FlipX = 0x20;
    public const byte DmgPalette1 = 0x10;

    /// <summary>Isolates the 3-bit CGB object palette index (0-7) from <paramref name="n"/>, so a
    /// caller can pass any byte and still compose a legal attribute:
    /// <c>ObjAttr.CgbPalette(2) | ObjAttr.FlipX</c>.</summary>
    public static byte CgbPalette(byte n) => (byte)(n & 7);
}
