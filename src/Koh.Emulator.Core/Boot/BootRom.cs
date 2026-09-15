namespace Koh.Emulator.Core.Boot;

/// <summary>Which overlay shape a boot ROM has, derived from its length.</summary>
public enum BootRomFamily
{
    /// <summary>256 bytes overlaying $0000-$00FF. DMG, DMG0, MGB, SGB, SGB2.</summary>
    Dmg,

    /// <summary>2304 bytes overlaying $0000-$00FF and $0200-$08FF. CGB, CGB-E, AGB.</summary>
    Cgb,
}

/// <summary>
/// A validated boot ROM image. Construction is the only validation point: past it an
/// invalid blob is unrepresentable, so <see cref="Bus.Mmu"/> never re-checks a size.
///
/// A sealed class rather than a readonly struct on purpose — a struct cannot hold a
/// ReadOnlySpan field, and a struct over a byte[] reintroduces a default(BootRom) with
/// a null array, which is exactly the invalid state this type exists to eliminate.
///
/// Family is derived from LENGTH, not from a caller-declared hardware mode, so an
/// unrecognised but correctly-sized dump (SGB, AGB, a homebrew boot ROM) works with no
/// table of known hashes to keep current.
/// </summary>
public sealed class BootRom
{
    /// <summary>DMG-family boot ROM size: overlays $0000-$00FF.</summary>
    public const int DmgSize = 0x100;

    /// <summary>CGB-family boot ROM size: overlays $0000-$00FF and $0200-$08FF.</summary>
    public const int CgbSize = 0x900;

    private readonly byte[] _bytes;

    private BootRom(byte[] bytes, BootRomFamily family)
    {
        _bytes = bytes;
        Family = family;
        Fingerprint = ComputeFingerprint(bytes);
    }

    /// <summary>The overlay shape this blob's length implies.</summary>
    public BootRomFamily Family { get; }

    /// <summary>The blob itself.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// 64-bit FNV-1a over the blob, used by save states to detect that a state was
    /// captured under different boot code. Deliberately not a cryptographic hash: this
    /// guards against confusion, not forgery, and pulling in
    /// System.Security.Cryptography for it would be gratuitous. Never 0 — save states
    /// use 0 to mean "no boot ROM was loaded".
    /// </summary>
    public ulong Fingerprint { get; }

    /// <summary>
    /// Validate and copy a boot ROM image. The copy matters: a caller that reuses its
    /// read buffer must not be able to mutate a blob the machine is already executing.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The image is neither of the two valid boot ROM sizes.
    /// </exception>
    public static BootRom FromBytes(ReadOnlySpan<byte> bytes)
    {
        var family = bytes.Length switch
        {
            DmgSize => BootRomFamily.Dmg,
            CgbSize => BootRomFamily.Cgb,
            _ => throw new ArgumentException(
                $"Boot ROM is {bytes.Length} bytes. A boot ROM must be either "
                    + $"{DmgSize} bytes (256, DMG family) or {CgbSize} bytes "
                    + $"(2304, CGB family).",
                nameof(bytes)
            ),
        };

        return new BootRom(bytes.ToArray(), family);
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }

        // 0 is the "no boot ROM" sentinel in save states. FNV-1a of an all-zero blob is
        // not 0, but pin the guarantee rather than relying on that.
        return hash == 0 ? 1 : hash;
    }
}
