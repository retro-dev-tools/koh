using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    public static int Subrange()
    {
        Video.Init();
        TileSet.Load(10, PlainData.Bytes, 1, 2);
        return 42;
    }

    public static int AlignedSubrange()
    {
        Video.Init();
        TileSet.Load(10, AlignedData.Bytes, 1, 2);
        return 42;
    }

    public static int OnSubrange()
    {
        Video.Init();
        Video.Start();
        TileSet.Load(10, PlainData.Bytes, 1, 2);
        return 42;
    }

    public static int OnAlignedSubrange()
    {
        Video.Init();
        Video.Start();
        TileSet.Load(10, AlignedData.Bytes, 1, 2);
        return 42;
    }

    public static int EmptySubrange()
    {
        Video.Init();
        TileSet.Load(10, null!, 65535, 0);
        return 42;
    }

    private static void DrawRows()
    {
        DrawTile(9);
        DrawTile(16);
        DrawTile(255);
        TileSet.SetRow(255, 7, 0x56, 0x78);
    }

    private static void DrawTile(byte tile)
    {
        TileSet.SetRow(tile, 0, 0xAB, 0xCD);
        TileSet.Clear(tile);
        TileSet.SetRow(tile, 3, 0x12, 0x34);
    }

    public static int Rows()
    {
        Video.Init();
        DrawRows();
        *(byte*)0xDF00 = Video.IsCgb ? (byte)(*(byte*)0xFF4F & 1) : (byte)0;
        return 42;
    }

    public static int BankOneRows()
    {
        Video.Init();
        if (Video.IsCgb)
            Cgb.SelectVramBank(1);
        DrawRows();
        *(byte*)0xDF00 = Video.IsCgb ? (byte)(*(byte*)0xFF4F & 1) : (byte)0;
        if (Video.IsCgb)
            Cgb.SelectVramBank(0);
        return 42;
    }
}

public static class PlainData
{
    public static readonly byte[] Bytes =
    {
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
    };
}

public static class AlignedData
{
    [KohAligned(16)]
    public static readonly byte[] Bytes =
    {
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x11,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x22,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
        0x33,
    };
}
