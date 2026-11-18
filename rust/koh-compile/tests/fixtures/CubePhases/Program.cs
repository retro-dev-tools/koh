using var output = Console.OpenStandardOutput();
foreach (var (width, height) in new (byte, byte)[] { (64, 64), (96, 80), (128, 120) })
{
    Surface.Configure(width, height);
    output.WriteByte(width);
    output.WriteByte(height);
    for (int phase = 0; phase < 256; phase++)
    {
        Surface.Clear();
        CubeRenderer.Render((byte)phase);
        output.WriteByte((byte)phase);
        output.Write(Surface.Pixels);
    }
}

static class Surface
{
    static byte width;
    static byte height;
    static byte[] pixels = [];

    internal static ReadOnlySpan<byte> Pixels => pixels;

    internal static void Configure(byte w, byte h)
    {
        width = w;
        height = h;
        pixels = new byte[w * h];
    }

    internal static byte Width() => width;

    internal static byte Height() => height;

    internal static void Clear() => Array.Clear(pixels);

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x < width && y < height)
            pixels[y * width + x] = color;
    }

    internal static void FillSpan(byte y, byte xa, byte xb, byte color)
    {
        for (int x = xa; x <= xb; x++)
            SetPixel((byte)x, y, ((x ^ y) & 3) == 0 && color > 1 ? (byte)(color - 1) : color);
    }
}
