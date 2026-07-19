using Koh.GameBoy.Graphics;

namespace Koh.GameBoy.Framework;

/// <summary>
/// A handle over a ROM tile table (a <c>static readonly byte[]</c> of 16-byte 2bpp tiles), so the
/// tile count is stated ONCE, at the declaration site, and the VRAM base slot lives with the data —
/// call sites say <c>asset.Load(0)</c> and <c>asset.Tile(3)</c>, never a count or a slot sum.
///
/// Stage-0 shape (see <c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>): the ideal
/// API is <c>static TileAsset Define(byte[] data)</c> — a struct-returning factory with no count.
/// The factory needs compiler enabler E1 (struct return by value) and the count-free overload needs
/// E4 (length-carrying arrays), so until those land the binding is a mutating instance method with
/// an explicit count: <c>Tiles.Define(TileArt, 12);</c>. The E1/E4 overloads are additive — this
/// method stays.
/// </summary>
public struct TileAsset
{
    private byte[] _data;
    private byte _tileCount;
    private byte _baseTile;

    /// <summary>Tiles in the table — usable for layout math (e.g. "the font starts after us").</summary>
    public byte TileCount => _tileCount;

    /// <summary>The VRAM slot the table was loaded at (set by <see cref="Load"/>).</summary>
    public byte BaseTile => _baseTile;

    /// <summary>Bind the handle to its ROM table. Call where the array is declared.</summary>
    public void Define(byte[] data, byte tileCount)
    {
        _data = data;
        _tileCount = tileCount;
    }

    /// <summary>Copy the table into VRAM starting at <paramref name="baseTile"/> (via
    /// <see cref="TileSet.Load"/> — LCD-off fast path or vblank-chunked live path, its call).</summary>
    public void Load(byte baseTile)
    {
        _baseTile = baseTile;
        TileSet.Load(baseTile, _data, _tileCount);
    }

    /// <summary>The VRAM tile index of table entry <paramref name="i"/> — name frames, not slots.</summary>
    public byte Tile(byte i) => (byte)(_baseTile + i);
}

/// <summary>
/// A handle over a ROM tile-index rectangle (w×h cells), drawn through <see cref="Bg"/>'s deferred
/// shadow. Same stage-0 binding story as <see cref="TileAsset"/>.
/// </summary>
public struct MapAsset
{
    private byte[] _cells;
    private byte _width;
    private byte _height;

    public byte Width => _width;
    public byte Height => _height;

    public void Define(byte[] cells, byte width, byte height)
    {
        _cells = cells;
        _width = width;
        _height = height;
    }

    /// <summary>Blit the rect with its top-left at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public void Draw(byte col, byte row) => Bg.DrawMap(col, row, _width, _height, _cells);
}
