// Minimal PNG encoder for verification snapshots — no external dependencies.
// Takes RGBA pixel data + dimensions and writes a PNG with optional integer
// upscaling (nearest neighbour). Sufficient for tests and screenshot output.
using System.IO;
using System.IO.Compression;

namespace Koh.Verify;

internal static class PngEncoder
{
    public static void WriteFromRgba(string path, byte[] rgba, int w, int h, int scale)
    {
        if (scale < 1)
            scale = 1;
        int outW = w * scale;
        int outH = h * scale;
        // Build raw image data: PNG filter byte (0 = None) + RGB rows.
        var raw = new byte[(outW * 3 + 1) * outH];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            // Scale each source row vertically by 'scale' duplicates.
            for (int sy = 0; sy < scale; sy++)
            {
                raw[p++] = 0; // filter: None
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte r = rgba[i + 0],
                        g = rgba[i + 1],
                        b = rgba[i + 2];
                    for (int sx = 0; sx < scale; sx++)
                    {
                        raw[p++] = r;
                        raw[p++] = g;
                        raw[p++] = b;
                    }
                }
            }
        }
        using var fs = File.Create(path);
        // Signature.
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        // IHDR.
        WriteChunk(
            fs,
            "IHDR",
            w =>
            {
                WriteUInt32BE(w, (uint)outW);
                WriteUInt32BE(w, (uint)outH);
                w.WriteByte(8); // bit depth
                w.WriteByte(2); // colour type 2 = truecolour
                w.WriteByte(0); // compression
                w.WriteByte(0); // filter
                w.WriteByte(0); // interlace
            }
        );
        // IDAT (zlib-wrapped deflate of raw).
        WriteChunk(
            fs,
            "IDAT",
            w =>
            {
                using var ms = new MemoryStream();
                // zlib header (CMF=0x78 default-compression, FLG=0x9C check).
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (
                    var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)
                )
                    deflate.Write(raw);
                // Adler-32 checksum trailer.
                uint adler = Adler32(raw);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                ms.Position = 0;
                ms.CopyTo(w);
            }
        );
        // IEND.
        WriteChunk(fs, "IEND", _ => { });
    }

    private static void WriteChunk(Stream s, string type, Action<Stream> writePayload)
    {
        using var payload = new MemoryStream();
        writePayload(payload);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var payloadBytes = payload.ToArray();
        WriteUInt32BE(s, (uint)payloadBytes.Length);
        s.Write(typeBytes);
        s.Write(payloadBytes);
        // CRC over type + payload.
        uint crc = Crc32(typeBytes, payloadBytes);
        WriteUInt32BE(s, crc);
    }

    private static void WriteUInt32BE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte v in a)
            c = Crc32Step(c, v);
        foreach (byte v in b)
            c = Crc32Step(c, v);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint Crc32Step(uint c, byte v)
    {
        c ^= v;
        for (int k = 0; k < 8; k++)
            c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
        return c;
    }

    private static uint Adler32(byte[] data)
    {
        const uint M = 65521;
        uint a = 1,
            b = 0;
        foreach (byte v in data)
        {
            a = (a + v) % M;
            b = (b + a) % M;
        }
        return (b << 16) | a;
    }
}
