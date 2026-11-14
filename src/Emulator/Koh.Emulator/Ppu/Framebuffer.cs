using System.Buffers.Binary;
using System.IO.Compression;

namespace Koh.Emulator.Ppu;

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

    /// <summary>
    /// The front buffer as a raw <c>byte[]</c>. Blazor's JS interop marshals
    /// this as a <c>Uint8Array</c> efficiently; Span and ReadOnlyMemory
    /// don't travel through the JSON serializer.
    /// </summary>
    public byte[] FrontArray => _aIsFront ? _a : _b;

    public void Flip() => _aIsFront = !_aIsFront;

    internal void WritePng(Stream output)
    {
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(Front.Slice(y * Width * BytesPerPixel, Width * BytesPerPixel));
            }
        }
        WritePngChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WritePngChunk(output, "IEND"u8, []);
    }

    private static void WritePngChunk(
        Stream output,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data
    )
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, data.Length);
        output.Write(value);
        output.Write(type);
        output.Write(data);
        var crc = uint.MaxValue;
        foreach (var b in type)
            crc = UpdateCrc(crc, b);
        foreach (var b in data)
            crc = UpdateCrc(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        output.Write(value);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        return crc;
    }

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
