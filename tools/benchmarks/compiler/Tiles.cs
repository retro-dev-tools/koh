public static class Program
{
    private static unsafe void Tile()
    {
        byte* pattern = (byte*)0x8000;
        for (int row = 0; row < 8; row++)
        {
            pattern[row * 2] = (byte)((row & 1) == 0 ? 0xAA : 0x55);
            pattern[row * 2 + 1] = (byte)((row & 1) == 0 ? 0x55 : 0xAA);
        }
    }

    private static unsafe void Map()
    {
        byte* map = (byte*)0x9800;
        for (int index = 0; index < 32 * 32; index++)
            map[index] = 0;
    }

    public static unsafe void Main()
    {
        *(byte*)0xFF40 = 0;
        Tile();
        Map();
        *(byte*)0xFF47 = 0xE4;
        *(byte*)0xFF40 = 0x91;
        *(int*)0xC900 = 90;
        while (true) { }
    }
}
