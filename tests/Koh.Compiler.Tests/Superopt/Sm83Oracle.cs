using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Compiler.Tests.Superopt;

/// <summary>The 8-bit registers plus flags and SP — the observable machine state a straight-line SM83
/// sequence can read or write. Enough to define input states and compare outputs for the equivalence
/// oracle; memory is out of scope for this proof-of-concept alphabet.</summary>
public readonly record struct Sm83State(
    byte A,
    byte F,
    byte B,
    byte C,
    byte D,
    byte E,
    byte H,
    byte L,
    ushort Sp
);

/// <summary>Which parts of <see cref="Sm83State"/> a caller cares about after a sequence runs — the
/// live-out set. A rewrite only has to preserve these, so a smaller live-out admits more (and cheaper)
/// equivalents. Flags are compared as a set (the high nibble of F).</summary>
[Flags]
public enum Live : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    C = 1 << 2,
    D = 1 << 3,
    E = 1 << 4,
    H = 1 << 5,
    L = 1 << 6,
    Flags = 1 << 7,
    AllRegs = A | B | C | D | E | H | L,
}

/// <summary>
/// A concrete-execution equivalence oracle for short, straight-line SM83 sequences, built on the Koh
/// emulator (<see cref="GameBoySystem"/>). It runs a byte sequence from a chosen register state by
/// baking it into a minimal ROM-only cartridge at the code entry, single-stepping until control leaves
/// the sequence, and reading the machine state back. Two sequences are judged equivalent when they
/// produce identical live-out state across a batch of randomized inputs.
///
/// This is exactly the oracle the SM83 optimization review names as the missing piece for a superopti-
/// mizer (item #5) — and the piece Koh, uniquely, already has in its emulator. Concrete testing over
/// random inputs is sound-for-refutation (a single disagreeing input proves inequivalence) and a strong
/// filter for acceptance; a production tool would follow it with an exhaustive or symbolic check.
/// </summary>
public sealed class Sm83Oracle
{
    private const ushort CodeBase = 0x0150; // first byte after the cartridge header
    private const int MaxSteps = 64; // guard: the PoC alphabet is straight-line and short

    /// <summary>Run <paramref name="code"/> from <paramref name="input"/> and return the resulting
    /// state. Stepping stops when the program counter leaves the sequence's byte range.</summary>
    public Sm83State Run(ReadOnlySpan<byte> code, Sm83State input)
    {
        var rom = new byte[0x8000]; // 32 KiB, zeroed header ⇒ parses as a ROM-only cartridge
        code.CopyTo(rom.AsSpan(CodeBase));
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));

        ref var r = ref gb.Registers;
        (r.A, r.F, r.B, r.C, r.D, r.E, r.H, r.L, r.Sp) = (
            input.A,
            (byte)(input.F & 0xF0), // only the flag nibble is meaningful
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
        for (var i = 0; i < MaxSteps && r.Pc >= CodeBase && r.Pc < end; i++)
            gb.StepInstruction();

        return new Sm83State(r.A, r.F, r.B, r.C, r.D, r.E, r.H, r.L, r.Sp);
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
            if (!SameLive(Run(a, input), Run(b, input), live))
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

    private static bool SameLive(Sm83State x, Sm83State y, Live live)
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
