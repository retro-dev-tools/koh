using System.Buffers.Binary;
using System.IO.Compression;

namespace Koh.Compiler.Tests.TestSupport;

/// <summary>
/// Tiny PNG writer for test artifacts (acceptance screenshots, synthetic fixtures) — the same
/// constrained subset <c>Koh.Build.Tasks.PngReader</c> decodes: RGB(A)8, non-interlaced, filter-0
/// rows, single zlib IDAT.
/// </summary>
internal static class TestPng
{
    /// <summary>Write an RGB24 image, nearest-neighbor upscaled by <paramref name="scale"/>.</summary>
    public static void WriteRgb(string path, int width, int height, byte[] rgb, int scale = 1)
    {
        var w = width * scale;
        var h = height * scale;
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        WriteChunk(ms, "IHDR", ihdr);

        var raw = new byte[h * (1 + w * 3)];
        var o = 0;
        for (var y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: None
            var sy = y / scale;
            for (var x = 0; x < w; x++)
            {
                var si = (sy * width + x / scale) * 3;
                raw[o++] = rgb[si];
                raw[o++] = rgb[si + 1];
                raw[o++] = rgb[si + 2];
            }
        }
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9C);
        using (
            var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true)
        )
            deflate.Write(raw);
        compressed.Write(Adler32(raw));
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", []);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static byte[] Adler32(byte[] data)
    {
        uint a = 1,
            b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, (b << 16) | a);
        return result;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = 0xFFFFFFFF;
        foreach (var chunk in new[] { typeBytes, data })
        foreach (var value in chunk)
        {
            crc ^= value;
            for (var k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, (uint)(crc ^ 0xFFFFFFFF));
        stream.Write(crcBytes);
    }
}
