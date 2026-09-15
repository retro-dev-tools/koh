using Koh.GameBoy.Graphics;

namespace Koh.GameBoy.Framework;

/// <summary>
/// A handle over a ROM tile table (a <c>static readonly byte[]</c> of 16-byte 2bpp tiles), so the
/// tile count is stated ONCE, at the declaration site, and the VRAM base slot lives with the data —
/// call sites say <c>asset.Load(0)</c> and <c>asset.Tile(3)</c>, never a count or a slot sum.
///
/// The factory returns the struct by value (compiler enabler E1, the static-slot struct-return
/// convention); the count-free <c>Define(byte[] data)</c> overload arrives with enabler E4
/// (length-carrying arrays) — see <c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>.
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

    /// <summary>Bind a handle to its ROM table — the ideal form, no count anywhere:
    /// <c>Tiles = TileAsset.Define(TileArt);</c>. The tile count is the array's real length
    /// (length-carrying arrays, enabler E4) divided by 16 bytes per 2bpp tile.</summary>
    public static TileAsset Define(byte[] data) => Define(data, (byte)(data.Length / 16));

    /// <summary>Explicit-count overload — for a table deliberately loaded partially, or art arrays
    /// carrying trailing non-tile data.</summary>
    public static TileAsset Define(byte[] data, byte tileCount)
    {
        TileAsset asset = default;
        asset._data = data;
        asset._tileCount = tileCount;
        return asset;
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
/// shadow. Same factory binding story as <see cref="TileAsset"/>.
/// </summary>
public struct MapAsset
{
    private byte[] _cells;
    private byte _width;
    private byte _height;

    public byte Width => _width;
    public byte Height => _height;

    public static MapAsset Define(byte[] cells, byte width, byte height)
    {
        MapAsset asset = default;
        asset._cells = cells;
        asset._width = width;
        asset._height = height;
        return asset;
    }

    /// <summary>Blit the rect with its top-left at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public void Draw(byte col, byte row) => Bg.DrawMap(col, row, _width, _height, _cells);
}
