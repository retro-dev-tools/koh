using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Ppu;

public enum FetcherStep : byte
{
    GetTile = 0,
    GetTileDataLow = 1,
    GetTileDataHigh = 2,
    Push = 3,
    Sleep = 4,
}

public sealed class Fetcher
{
    public FetcherStep Step;
    public int DotBudget;           // remaining dots before the step completes

    public int TileMapX;            // which column of the tile map we're fetching (0..31)
    public int TileMapY;            // which row of the tile map we're fetching (0..31)
    public ushort TileMapBase;      // $9800 or $9C00
    public bool UsingWindow;
    public byte FetchedTileIndex;
    public byte FetchedAttributes;  // CGB attribute byte
    public byte FetchedLow;
    public byte FetchedHigh;

    public void ResetForScanline(byte scx, byte scy, byte ly, ushort bgTileMapBase, bool window)
    {
        Step = FetcherStep.GetTile;
        DotBudget = 2;
        TileMapX = (scx / 8) & 0x1F;
        TileMapY = ((ly + scy) / 8) & 0x1F;
        TileMapBase = bgTileMapBase;
        UsingWindow = window;
    }

    public void StartWindow(ushort windowTileMapBase, int windowLineCounter)
    {
        Step = FetcherStep.GetTile;
        DotBudget = 2;
        TileMapX = 0;
        TileMapY = windowLineCounter / 8;
        TileMapBase = windowTileMapBase;
        UsingWindow = true;
    }

    public void WriteState(StateWriter w)
    {
        w.WriteByte((byte)Step);
        w.WriteI32(DotBudget);
        w.WriteI32(TileMapX); w.WriteI32(TileMapY);
        w.WriteU16(TileMapBase);
        w.WriteBool(UsingWindow);
        w.WriteByte(FetchedTileIndex); w.WriteByte(FetchedAttributes);
        w.WriteByte(FetchedLow); w.WriteByte(FetchedHigh);
    }

    public void ReadState(StateReader r)
    {
        Step = (FetcherStep)r.ReadByte();
        DotBudget = r.ReadI32();
        TileMapX = r.ReadI32(); TileMapY = r.ReadI32();
        TileMapBase = r.ReadU16();
        UsingWindow = r.ReadBool();
        FetchedTileIndex = r.ReadByte(); FetchedAttributes = r.ReadByte();
        FetchedLow = r.ReadByte(); FetchedHigh = r.ReadByte();
    }
}
