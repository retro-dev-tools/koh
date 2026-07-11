using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Superopt;

/// <summary>
/// A concrete-execution equivalence oracle for short, straight-line, memory-free SM83 sequences, built on
/// the Koh emulator (<see cref="GameBoySystem"/>). It bakes a byte sequence into a minimal ROM-only
/// cartridge at the code entry, sets the register state, single-steps until control leaves the sequence,
/// and reads the machine state back. Two sequences are judged equivalent when they produce identical
/// live-out state across a batch of randomized inputs.
///
/// Concrete random testing is sound-for-refutation (a single disagreeing input proves inequivalence) and
/// a strong acceptance filter. A production tool would follow it with an exhaustive small-window or
/// symbolic check before trusting a mined rule blind — see the design note.
///
/// ponytail: random-input filter only; add an exhaustive small-window or symbolic check before
/// auto-trusting a mined rule.
/// </summary>
public sealed class Sm83Oracle
{
    private const ushort CodeBase = 0x0150; // first byte after the cartridge header
    private const int MaxSteps = 64; // guard: the alphabet is straight-line and short

    /// <summary>Run <paramref name="code"/> from <paramref name="input"/>; return the resulting state and
    /// the total T-cycles executed. Stepping stops when the program counter leaves the byte range.</summary>
    public (Sm83State State, ulong TCycles) Run(ReadOnlySpan<byte> code, Sm83State input)
    {
        var rom = new byte[0x8000]; // 32 KiB, zeroed header ⇒ parses as a ROM-only cartridge
        code.CopyTo(rom.AsSpan(CodeBase));
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));

        ref var r = ref gb.Registers;
        (r.A, r.F, r.B, r.C, r.D, r.E, r.H, r.L, r.Sp) = (
            input.A,
            (byte)(input.F & 0xF0),
            input.B,
            input.C,
            input.D,
            input.E,
            input.H,
            input.L,
            input.Sp
        );
        r.Pc = CodeBase;

        var end = CodeBase + code.Length;
        ulong tcycles = 0;
        for (var i = 0; i < MaxSteps && r.Pc >= CodeBase && r.Pc < end; i++)
            tcycles += gb.StepInstruction().TCyclesRan;

        return (new Sm83State(r.A, r.F, r.B, r.C, r.D, r.E, r.H, r.L, r.Sp), tcycles);
    }

    /// <summary>True if <paramref name="a"/> and <paramref name="b"/> yield identical
    /// <paramref name="live"/> state across <paramref name="trials"/> randomized inputs.</summary>
    public bool AreEquivalent(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        Live live,
        int trials = 64,
        int seed = 0x5A83
    )
    {
        var random = new Random(seed);
        for (var t = 0; t < trials; t++)
        {
            var input = RandomState(random);
            if (!SameLive(Run(a, input).State, Run(b, input).State, live))
                return false;
        }
        return true;
    }

    private static Sm83State RandomState(Random random)
    {
        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return new Sm83State(
            bytes[0],
            bytes[1],
            bytes[2],
            bytes[3],
            bytes[4],
            bytes[5],
            bytes[6],
            bytes[7],
            0xFFFE
        );
    }

    /// <summary>Compare only the live-out parts of two states; flags compared as the high nibble of F.</summary>
    public static bool SameLive(Sm83State x, Sm83State y, Live live)
    {
        if (live.HasFlag(Live.A) && x.A != y.A)
            return false;
        if (live.HasFlag(Live.B) && x.B != y.B)
            return false;
        if (live.HasFlag(Live.C) && x.C != y.C)
            return false;
        if (live.HasFlag(Live.D) && x.D != y.D)
            return false;
        if (live.HasFlag(Live.E) && x.E != y.E)
            return false;
        if (live.HasFlag(Live.H) && x.H != y.H)
            return false;
        if (live.HasFlag(Live.L) && x.L != y.L)
            return false;
        if (live.HasFlag(Live.Flags) && (x.F & 0xF0) != (y.F & 0xF0))
            return false;
        return true;
    }
}
