using System.Buffers.Binary;
using System.IO.Compression;

namespace Koh.Build.Tasks;

/// <summary>
/// Minimal, dependency-free PNG decoder for the tile-sheet pipeline (see
/// <see cref="TileSheetConverter"/>). Deliberately supports only the constrained subset the Koh
/// art tooling emits — 8-bit-per-channel, non-interlaced, color type 2 (RGB), 3 (indexed + PLTE),
/// or 6 (RGBA); zlib-wrapped IDAT (inflated with <see cref="DeflateStream"/> past the 2-byte zlib
/// header); all five standard row filters. Anything outside the subset is a clean error naming
/// the file — an asset authoring problem, not a silent misread.
/// </summary>
internal static class PngReader
{
    /// <summary>Decodes to RGBA8888 pixels, row-major.</summary>
    public static (int Width, int Height, uint[] Pixels) Decode(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (
            bytes.Length < 8
            || bytes[0] != 0x89
            || bytes[1] != (byte)'P'
            || bytes[2] != (byte)'N'
            || bytes[3] != (byte)'G'
        )
            throw new InvalidDataException($"'{path}' is not a PNG file.");

        int width = 0,
            height = 0,
            bitDepth = 0,
            colorType = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();

        var offset = 8;
        while (offset + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    bitDepth = data[8];
                    colorType = data[9];
                    if (bitDepth != 8)
                        throw new InvalidDataException(
                            $"'{path}': bit depth {bitDepth} is unsupported (8 only)."
                        );
                    if (colorType is not (2 or 3 or 6))
                        throw new InvalidDataException(
                            $"'{path}': color type {colorType} is unsupported (RGB, indexed, RGBA)."
                        );
                    if (data[12] != 0)
                        throw new InvalidDataException(
                            $"'{path}': interlaced PNGs are unsupported."
                        );
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    offset = bytes.Length;
                    continue;
            }
            offset += 12 + length; // length + type + data + crc
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"'{path}': missing or empty IHDR.");

        // Inflate past the 2-byte zlib header; the trailing adler32 is ignored by DeflateStream.
        idat.Position = 2;
        using var inflate = new DeflateStream(idat, CompressionMode.Decompress);
        var bpp = colorType switch
        {
            2 => 3,
            3 => 1,
            _ => 4,
        };
        var stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        var read = 0;
        while (read < raw.Length)
        {
            var n = inflate.Read(raw, read, raw.Length - read);
            if (n == 0)
                break;
            read += n;
        }
        if (read != raw.Length)
            throw new InvalidDataException(
                $"'{path}': IDAT ended early ({read} of {raw.Length} filtered bytes)."
            );

        // Unfilter in place into a clean scanline buffer.
        var image = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var dst = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int rawByte = raw[src + x];
                int left = x >= bpp ? image[dst + x - bpp] : 0;
                int up = y > 0 ? image[dst + x - stride] : 0;
                int upLeft = (y > 0 && x >= bpp) ? image[dst + x - stride - bpp] : 0;
                image[dst + x] = filter switch
                {
                    0 => (byte)rawByte,
                    1 => (byte)(rawByte + left),
                    2 => (byte)(rawByte + up),
                    3 => (byte)(rawByte + (left + up) / 2),
                    4 => (byte)(rawByte + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException(
                        $"'{path}': unknown filter {filter} on row {y}."
                    ),
                };
            }
        }

        var pixels = new uint[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            byte r,
                g,
                b,
                a = 255;
            switch (colorType)
            {
                case 2:
                    r = image[i * 3];
                    g = image[i * 3 + 1];
                    b = image[i * 3 + 2];
                    break;
                case 3:
                {
                    if (palette is null)
                        throw new InvalidDataException($"'{path}': indexed PNG without PLTE.");
                    int pi = image[i] * 3;
                    r = palette[pi];
                    g = palette[pi + 1];
                    b = palette[pi + 2];
                    break;
                }
                default:
                    r = image[i * 4];
                    g = image[i * 4 + 1];
                    b = image[i * 4 + 2];
                    a = image[i * 4 + 3];
                    break;
            }
            pixels[i] = (uint)(r | (g << 8) | (b << 16) | (a << 24));
        }
        return (width, height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a
            : pb <= pc ? b
            : c;
    }
}
