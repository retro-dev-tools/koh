namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// 160×144 RGBA8888 framebuffer with a double buffer. The PPU writes into
/// <see cref="Back"/>; <see cref="Flip"/> swaps buffers at VBlank.
/// </summary>
public sealed class Framebuffer
{
    public const int Width = 160;
    public const int Height = 144;
    public const int BytesPerPixel = 4;
    public const int ByteSize = Width * Height * BytesPerPixel;

    private readonly byte[] _a = new byte[ByteSize];
    private readonly byte[] _b = new byte[ByteSize];
    private bool _aIsFront;

    public Framebuffer()
    {
        _aIsFront = true;
        FillWithPlaceholderGray(_a);
        FillWithPlaceholderGray(_b);
    }

    public ReadOnlySpan<byte> Front => _aIsFront ? _a : _b;
    public Span<byte> Back => _aIsFront ? _b : _a;

    public void Flip() => _aIsFront = !_aIsFront;

    private static void FillWithPlaceholderGray(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i + 0] = 0x2e;
            buffer[i + 1] = 0x2e;
            buffer[i + 2] = 0x2e;
            buffer[i + 3] = 0xff;
        }
    }
}
