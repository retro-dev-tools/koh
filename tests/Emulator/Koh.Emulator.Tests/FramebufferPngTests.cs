using System.Buffers.Binary;
using System.IO.Compression;
using Koh.Emulator.Ppu;

namespace Koh.Emulator.Tests;

public class FramebufferPngTests
{
    [Test]
    public async Task WritePng_PreservesFrontPixelsAndLeavesOutputOpen()
    {
        var framebuffer = new Framebuffer();
        for (var i = 0; i < Framebuffer.ByteSize; i++)
            framebuffer.Back[i] = (byte)(i * 37);
        framebuffer.Flip();
        var expected = framebuffer.Front.ToArray();
        framebuffer.Back.Fill(255);

        using var output = new MemoryStream();
        framebuffer.WritePng(output);
        var bytes = output.ToArray();
        await Assert
            .That(bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            .IsTrue();

        using var compressed = new MemoryStream();
        var headerSeen = false;
        for (var offset = 8; offset < bytes.Length; )
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            if (type == "IHDR")
            {
                headerSeen = true;
                await Assert
                    .That(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 8, 4)))
                    .IsEqualTo(160);
                await Assert
                    .That(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 12, 4)))
                    .IsEqualTo(144);
                await Assert.That(bytes[offset + 16]).IsEqualTo((byte)8);
                await Assert.That(bytes[offset + 17]).IsEqualTo((byte)6);
                await Assert
                    .That(
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4))
                    )
                    .IsEqualTo(0x8FB56080u);
            }
            if (type == "IDAT")
                compressed.Write(bytes, offset + 8, length);
            offset += length + 12;
        }

        await Assert.That(headerSeen).IsTrue();

        compressed.Position = 0;
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        var row = new byte[1 + 160 * 4];
        for (var y = 0; y < 144; y++)
        {
            inflate.ReadExactly(row);
            await Assert.That(row[0]).IsEqualTo((byte)0);
            await Assert
                .That(row[1..].SequenceEqual(expected[(y * 640)..((y + 1) * 640)]))
                .IsTrue();
        }
        await Assert.That(inflate.ReadByte()).IsEqualTo(-1);
        await Assert
            .That(
                bytes[^12..]
                    .SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 })
            )
            .IsTrue();
        output.WriteByte(0);
    }
}
