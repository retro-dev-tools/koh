namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Two's-complement arithmetic helpers at a given bit width, shared by the integer passes so the
/// folder and the strength reducer agree on wrapping and sign semantics (and can't drift apart).
/// </summary>
internal static class IntWidth
{
    public static ulong Mask(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    public static ulong ToUnsigned(long value, int bits) => (ulong)value & Mask(bits);

    public static long ToSigned(long value, int bits)
    {
        if (bits >= 64)
            return value;
        var masked = (long)ToUnsigned(value, bits);
        var signBit = 1L << (bits - 1);
        return (masked ^ signBit) - signBit;
    }

    /// <summary>Normalize a value to its two's-complement representation at this width.</summary>
    public static long Wrap(long value, int bits) => ToSigned(value, bits);

    public static long MinSigned(int bits) => bits >= 64 ? long.MinValue : -(1L << (bits - 1));
}
