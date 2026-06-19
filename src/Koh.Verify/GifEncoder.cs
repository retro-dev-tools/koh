// Animated GIF encoder for verification — emits a 256-colour GIF87a animation
// from a sequence of RGB frames. No external deps.
//
// We pick a per-frame local palette of up to 256 colours by quantising the
// frame to its unique colours (GB output has only a handful per palette set,
// so this trivially fits). Output is uncompressed-style LZW (Code length 8 +
// clear codes only) to keep the encoder simple.
using System.IO;

namespace Koh.Verify;

public static class GifEncoder
{
    /// <summary>Write a GIF89a animation. Each frame is RGB w*h bytes.</summary>
    public static void WriteAnimation(string path, IList<byte[]> framesRgb, int w, int h,
                                       int frameDelayCs = 5, int loopCount = 0)
    {
        using var s = File.Create(path);
        WriteHeader(s, w, h);
        WriteNetscapeLoop(s, loopCount);
        foreach (var rgb in framesRgb)
        {
            WriteGraphicControl(s, frameDelayCs);
            WriteFrame(s, rgb, w, h);
        }
        s.WriteByte(0x3B); // trailer
    }

    private static void WriteHeader(Stream s, int w, int h)
    {
        s.Write("GIF89a"u8);
        WriteLe16(s, (ushort)w);
        WriteLe16(s, (ushort)h);
        s.WriteByte(0x00);                  // no global colour table
        s.WriteByte(0x00);                  // background colour idx
        s.WriteByte(0x00);                  // pixel aspect
    }

    private static void WriteNetscapeLoop(Stream s, int loopCount)
    {
        s.WriteByte(0x21); s.WriteByte(0xFF); s.WriteByte(0x0B);
        s.Write("NETSCAPE2.0"u8);
        s.WriteByte(0x03); s.WriteByte(0x01);
        WriteLe16(s, (ushort)loopCount);
        s.WriteByte(0x00);
    }

    private static void WriteGraphicControl(Stream s, int delayCs)
    {
        s.WriteByte(0x21); s.WriteByte(0xF9); s.WriteByte(0x04);
        s.WriteByte(0x00); // no transparency, no user input
        WriteLe16(s, (ushort)delayCs);
        s.WriteByte(0x00); // no transparent colour
        s.WriteByte(0x00); // block terminator
    }

    private static void WriteFrame(Stream s, byte[] rgb, int w, int h)
    {
        // Build local palette from frame's unique colours (cap at 256).
        var paletteDict = new Dictionary<int, byte>(256);
        var paletteList = new List<int>(256);
        var indices = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int key = (rgb[i*3] << 16) | (rgb[i*3+1] << 8) | rgb[i*3+2];
            if (!paletteDict.TryGetValue(key, out var idx))
            {
                if (paletteList.Count >= 256)
                    idx = NearestPaletteIndex(rgb, i*3, paletteList);
                else
                {
                    idx = (byte)paletteList.Count;
                    paletteList.Add(key);
                    paletteDict[key] = idx;
                }
            }
            indices[i] = idx;
        }

        // Image descriptor.
        s.WriteByte(0x2C);
        WriteLe16(s, 0); WriteLe16(s, 0);
        WriteLe16(s, (ushort)w); WriteLe16(s, (ushort)h);
        // local colour table size: smallest 2^(N+1) >= paletteList.Count.
        int sizeBits = 0;
        int tableSize = 2;
        while (tableSize < paletteList.Count) { tableSize <<= 1; sizeBits++; }
        s.WriteByte((byte)(0x80 | sizeBits));
        for (int i = 0; i < tableSize; i++)
        {
            int rgb24 = i < paletteList.Count ? paletteList[i] : 0;
            s.WriteByte((byte)(rgb24 >> 16));
            s.WriteByte((byte)(rgb24 >> 8));
            s.WriteByte((byte)rgb24);
        }

        // LZW-compressed image data.
        int lzwMin = Math.Max(2, sizeBits + 1);
        s.WriteByte((byte)lzwMin);
        WriteLzw(s, indices, lzwMin);
        s.WriteByte(0x00); // block terminator
    }

    private static byte NearestPaletteIndex(byte[] rgb, int off, List<int> palette)
    {
        int r = rgb[off], g = rgb[off+1], b = rgb[off+2];
        int bestIdx = 0, bestD = int.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            int pr = (palette[i] >> 16) & 0xFF;
            int pg = (palette[i] >> 8) & 0xFF;
            int pb = palette[i] & 0xFF;
            int d = (r-pr)*(r-pr) + (g-pg)*(g-pg) + (b-pb)*(b-pb);
            if (d < bestD) { bestD = d; bestIdx = i; }
        }
        return (byte)bestIdx;
    }

    private static void WriteLzw(Stream s, byte[] indices, int lzwMin)
    {
        int clearCode = 1 << lzwMin;
        int eoiCode = clearCode + 1;
        int nextCode = eoiCode + 1;
        int codeSize = lzwMin + 1;
        int maxCode = (1 << codeSize) - 1;

        var dict = new Dictionary<long, int>(4096);
        var bits = new BitWriter(s);
        bits.Write(clearCode, codeSize);

        long current = -1;
        foreach (byte k in indices)
        {
            long combined = current < 0 ? k : (current << 8) | k;
            if (current < 0)
            {
                current = k;
                continue;
            }
            if (dict.TryGetValue(combined, out var code))
            {
                current = code;
            }
            else
            {
                bits.Write((int)current, codeSize);
                if (nextCode <= 4095)
                {
                    dict[combined] = nextCode++;
                    if (nextCode > maxCode + 1 && codeSize < 12)
                    {
                        codeSize++;
                        maxCode = (1 << codeSize) - 1;
                    }
                }
                else
                {
                    bits.Write(clearCode, codeSize);
                    dict.Clear();
                    nextCode = eoiCode + 1;
                    codeSize = lzwMin + 1;
                    maxCode = (1 << codeSize) - 1;
                }
                current = k;
            }
        }
        if (current >= 0) bits.Write((int)current, codeSize);
        bits.Write(eoiCode, codeSize);
        bits.Flush();
    }

    private static void WriteLe16(Stream s, ushort v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

    private sealed class BitWriter(Stream sink)
    {
        private uint _buf;
        private int _bitCount;
        private readonly List<byte> _sub = new(255);

        public void Write(int code, int bits)
        {
            _buf |= (uint)(code & ((1 << bits) - 1)) << _bitCount;
            _bitCount += bits;
            while (_bitCount >= 8)
            {
                _sub.Add((byte)_buf);
                _buf >>= 8;
                _bitCount -= 8;
                if (_sub.Count == 255) FlushSub();
            }
        }
        public void Flush()
        {
            if (_bitCount > 0) { _sub.Add((byte)_buf); _buf = 0; _bitCount = 0; }
            FlushSub();
        }
        private void FlushSub()
        {
            if (_sub.Count == 0) return;
            sink.WriteByte((byte)_sub.Count);
            sink.Write(_sub.ToArray());
            _sub.Clear();
        }
    }
}
