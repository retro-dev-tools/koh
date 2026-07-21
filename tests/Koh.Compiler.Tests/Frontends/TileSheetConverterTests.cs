using System.Buffers.Binary;
using System.IO.Compression;
using Koh.Build.Tasks;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// The PNG tile pipeline's own regression: a synthetic PNG (encoded here with the same constrained
/// subset the art tooling emits — RGBA8, non-interlaced, filter-0 rows, single zlib IDAT) round-
/// trips through <see cref="PngReader"/>-backed <see cref="TileSheetConverter.Convert"/> into
/// exact 2bpp bitplanes, a luminance-ordered RGB555 palette, and generated source that names the
/// sheet's members.
/// </summary>
public class TileSheetConverterTests
{
    private static readonly string ScratchDir = Path.Combine(
        Path.GetTempPath(),
        "koh-tilesheet-tests"
    );

    // A 8x8 single-tile sheet: row y uses color (y % 4), so every 2bpp row is a known constant.
    private static string WriteTestPng()
    {
        // Lightest → darkest, deliberately unsorted in pixel order to prove luminance sorting.
        uint[] colors =
        [
            0xFF_20_20_20, // dark (ABGR in-memory: A=FF B=20 G=20 R=20)
            0xFF_FF_FF_FF, // white
            0xFF_60_A0_60, // greenish mid-light
            0xFF_40_40_A0, // reddish mid-dark (R=A0)
        ];
        var pixels = new uint[64];
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            pixels[y * 8 + x] = colors[y % 4];

        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"sheet_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, EncodePng(8, 8, pixels));
        return path;
    }

    [Test]
    public async Task SyntheticPng_RoundTripsToPlanesPaletteAndSource()
    {
        var path = WriteTestPng();
        var sheet = TileSheetConverter.Convert(path);

        await Assert.That(sheet.TileCount).IsEqualTo(1);
        await Assert.That(sheet.Tiles.Length).IsEqualTo(16);

        // Luminance order: white(idx0), greenish(1), reddish(2), dark(3).
        // Row colors cycle dark, white, green, red = indexes 3, 0, 1, 2 repeating.
        // 2bpp planes per row: idx3 -> low FF high FF; idx0 -> 00 00; idx1 -> FF 00; idx2 -> 00 FF.
        byte[] expectedPlanes =
        [
            0xFF,
            0xFF,
            0x00,
            0x00,
            0xFF,
            0x00,
            0x00,
            0xFF,
            0xFF,
            0xFF,
            0x00,
            0x00,
            0xFF,
            0x00,
            0x00,
            0xFF,
        ];
        await Assert.That(sheet.Tiles).IsEquivalentTo(expectedPlanes);

        // RGB555 of white and of the dark gray bookend the palette.
        await Assert.That(sheet.Rgb555Palette[0]).IsEqualTo((ushort)0x7FFF);
        await Assert.That(sheet.Rgb555Palette[3]).IsEqualTo((ushort)((4 << 10) | (4 << 5) | 4));

        var source = TileSheetConverter.GenerateSource("Some.Ns", [sheet]);
        await Assert.That(source.Contains($"{sheet.Name}Tiles")).IsTrue();
        await Assert.That(source.Contains($"{sheet.Name}TileCount = 1")).IsTrue();
        await Assert.That(source.Contains("namespace Some.Ns;")).IsTrue();
    }

    [Test]
    public async Task FiveColorSheet_IsARejectedAuthoringError()
    {
        var pixels = new uint[64];
        for (var i = 0; i < 64; i++)
            pixels[i] = 0xFF_00_00_00 | (uint)(i % 5 * 40); // five distinct reds
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"bad_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, EncodePng(8, 8, pixels));

        await Assert.That(() => TileSheetConverter.Convert(path)).Throws<InvalidDataException>();
    }

    // ---- Minimal constrained-subset PNG encoder (the same shape the art tooling writes) --------

    private static byte[] EncodePng(int width, int height, uint[] pixels)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(ms, "IHDR", ihdr);

        var raw = new byte[height * (1 + width * 4)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: None
            for (var x = 0; x < width; x++)
            {
                var p = pixels[y * width + x];
                raw[o++] = (byte)(p & 0xFF); // R
                raw[o++] = (byte)((p >> 8) & 0xFF); // G
                raw[o++] = (byte)((p >> 16) & 0xFF); // B
                raw[o++] = (byte)((p >> 24) & 0xFF); // A
            }
        }
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78); // zlib header
        compressed.WriteByte(0x9C);
        using (
            var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true)
        )
            deflate.Write(raw);
        compressed.Write([0, 0, 0, 0]); // adler32 placeholder (the reader ignores it)
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        // CRC over type+data — the reader doesn't verify it, but write a real one anyway.
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var chunk in new[] { a, b })
        foreach (var value in chunk)
        {
            crc ^= value;
            for (var k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
        return crc ^ 0xFFFFFFFF;
    }
}
