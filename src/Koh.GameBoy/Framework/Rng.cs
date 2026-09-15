namespace Koh.GameBoy.Framework;

/// <summary>
/// A real PRNG for game logic, replacing the raw <c>Hardware.DIV</c> reads samples used to sprinkle
/// through their logic (DIV correlates with frame timing and repeats identically run-to-run in an
/// emulator). 16-bit xorshift with shifts 7/9/8 — the SM83-friendly triple (8 is a byte move, 7/9
/// are a byte move plus one shift), full 65535-state period, and the identical sequence on the
/// desktop reference build by construction (this is ordinary compiled code on both targets).
///
/// State is one mutable static (WRAM, zero at boot). Zero is not a valid xorshift state, so
/// <see cref="Next16"/> treats it as "unseeded" and swaps in a fixed nonzero constant — no
/// <c>.cctor</c>, and <see cref="Seed"/>(0) needs no special care. <see cref="Mix"/> folds real
/// entropy (a DIV read at a human-timed moment — <c>Game.Boot</c> seeds from DIV, and mixing again
/// on the first keypress decorrelates the fixed boot-to-input timing an emulator reproduces).
/// </summary>
public static class Rng
{
    private const ushort FallbackState = 0x2D2D;

    private static ushort _state;

    public static void Seed(ushort seed) => _state = seed != 0 ? seed : FallbackState;

    /// <summary>Fold entropy into the running state without restarting the sequence.</summary>
    public static void Mix(byte entropy)
    {
        _state ^= entropy;
        Next16(); // stir, and repair a zero state as a side effect
    }

    public static ushort Next16()
    {
        ushort s = _state;
        if (s == 0)
            s = FallbackState;
        s ^= (ushort)(s << 7);
        s ^= (ushort)(s >> 9);
        s ^= (ushort)(s << 8);
        _state = s;
        return s;
    }

    /// <summary>The next byte (the state's high byte — the better-mixed half).</summary>
    public static byte Next() => (byte)(Next16() >> 8);

    /// <summary>Uniform-ish byte in [0, <paramref name="maxExclusive"/>). Modulo bias is at most
    /// 1/256 — fine for games, documented for honesty. Returns 0 when max is 0.</summary>
    public static byte Next(byte maxExclusive) =>
        maxExclusive != 0 ? (byte)(Next() % maxExclusive) : (byte)0;

    /// <summary>True with probability <paramref name="per256"/>/256.</summary>
    public static bool Chance(byte per256) => Next() < per256;
}
