using System.Security.Cryptography;

namespace Koh.Emulator.Core.State;

public static class SaveStateFile
{
    private const uint Magic = 0x53455453;  // "STES"
    public const ushort Version = 1;

    public static void Save(Stream output, GameBoySystem gb, ReadOnlySpan<byte> originalRomBytes)
    {
        using var w = new StateWriter(output);
        w.WriteU32(Magic);
        w.WriteU16(Version);
        w.WriteU16(0);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(originalRomBytes, hash);
        w.WriteBytes(hash);

        gb.WriteState(w);
    }

    public static void Load(Stream input, GameBoySystem gb, ReadOnlySpan<byte> expectedRomBytes)
    {
        using var r = new StateReader(input);
        uint magic = r.ReadU32();
        if (magic != Magic) throw new InvalidDataException("bad save-state magic");
        ushort version = r.ReadU16();
        if (version != Version) throw new InvalidDataException($"unsupported save-state version {version}");
        r.ReadU16();

        Span<byte> storedHash = stackalloc byte[32];
        r.ReadBytes(storedHash);
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(expectedRomBytes, expectedHash);
        if (!storedHash.SequenceEqual(expectedHash))
            throw new InvalidDataException("ROM hash mismatch — save-state is from a different ROM");

        gb.ReadState(r);
    }
}
