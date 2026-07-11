namespace Koh.Compiler.Tests.Superopt;

/// <summary>
/// A bounded, enumerative SM83 superoptimizer proof-of-concept. Given a short straight-line sequence
/// and a live-out set, it enumerates candidate sequences over a small instruction alphabet up to a
/// length bound, and returns the cheapest candidate that <see cref="Sm83Oracle"/> judges equivalent —
/// or the original if nothing cheaper survives. Cost is bytes first (ROM is the scarce resource on the
/// SM83), then cycles.
///
/// This is the "bounded superoptimizer for short, loop-free SM83 sequences … an SM83 semantics/emulator
/// oracle for equivalence checking" the optimization review flags as the clearest greenfield opportunity
/// for Koh — realizable precisely because Koh ships an emulator to use as the oracle. It is deliberately
/// small: a curated alphabet and a length bound, enough to rediscover canonical peephole wins (e.g.
/// <c>LD A,0 → XOR A</c> when flags are dead) and to shrink redundant sequences, and to demonstrate that
/// the search respects liveness (it will not take a flag-clobbering rewrite when flags are live-out).
/// Productionizing means driving candidates and cost from the MIR layer (item #1) and following the
/// concrete-execution filter with an exhaustive or symbolic check.
/// </summary>
public sealed class Sm83Superoptimizer
{
    /// <summary>One candidate instruction: its encoding and cycle cost.</summary>
    private readonly record struct MicroOp(byte[] Bytes, int Cycles);

    // A small straight-line alphabet spanning the moves this PoC reasons about. Immediate loads are
    // kept to the few useful constants so the search space stays tiny.
    private static readonly MicroOp[] Alphabet =
    [
        new([0x00], 1), // NOP
        new([0xAF], 1), // XOR A        (A = 0, clobbers flags)
        new([0xB7], 1), // OR A, A      (flags from A, A unchanged)
        new([0xA7], 1), // AND A, A     (flags from A, A unchanged)
        new([0x3C], 1), // INC A
        new([0x3D], 1), // DEC A
        new([0x78], 1), // LD A, B
        new([0x79], 1), // LD A, C
        new([0x47], 1), // LD B, A
        new([0x4F], 1), // LD C, A
        new([0x7F], 1), // LD A, A      (no-op move)
        new([0x3E, 0x00], 2), // LD A, 0
    ];

    private readonly Sm83Oracle _oracle = new();

    /// <summary>Return the cheapest sequence equivalent to <paramref name="input"/> over
    /// <paramref name="live"/>, searching candidates up to <paramref name="maxLength"/> instructions.
    /// Falls back to <paramref name="input"/> when no cheaper equivalent is found.</summary>
    public byte[] Optimize(byte[] input, Live live, int maxLength = 2)
    {
        var inputCost = Cost(input);
        byte[]? best = null;
        var bestCost = inputCost;

        foreach (var candidate in Enumerate(maxLength))
        {
            var cost = Cost(candidate);
            if (!Cheaper(cost, bestCost))
                continue; // only interested in something strictly cheaper than the best so far
            if (_oracle.AreEquivalent(input, candidate, live))
            {
                best = candidate;
                bestCost = cost;
            }
        }
        return best ?? input;
    }

    /// <summary>All sequences of 0..<paramref name="maxLength"/> alphabet ops, as flat byte arrays.</summary>
    private static IEnumerable<byte[]> Enumerate(int maxLength)
    {
        var frontier = new List<MicroOp[]> { Array.Empty<MicroOp>() };
        yield return Array.Empty<byte>();
        for (var length = 1; length <= maxLength; length++)
        {
            var next = new List<MicroOp[]>();
            foreach (var prefix in frontier)
            foreach (var op in Alphabet)
            {
                var seq = new MicroOp[prefix.Length + 1];
                prefix.CopyTo(seq, 0);
                seq[^1] = op;
                next.Add(seq);
                yield return Flatten(seq);
            }
            frontier = next;
        }
    }

    private static byte[] Flatten(MicroOp[] ops)
    {
        var bytes = new List<byte>();
        foreach (var op in ops)
            bytes.AddRange(op.Bytes);
        return bytes.ToArray();
    }

    /// <summary>Cost of a raw byte sequence: total bytes, then total cycles.</summary>
    private static (int Bytes, int Cycles) Cost(ReadOnlySpan<byte> code)
    {
        var bytes = 0;
        var cycles = 0;
        for (var i = 0; i < code.Length; )
        {
            var op = Match(code, i);
            bytes += op.Bytes.Length;
            cycles += op.Cycles;
            i += op.Bytes.Length;
        }
        return (bytes, cycles);
    }

    /// <summary>The alphabet op encoded at <paramref name="offset"/>, so an arbitrary input sequence
    /// (assembled from the same alphabet) can be costed. Falls back to a 1-byte, 1-cycle charge for an
    /// unrecognized byte so costing is total.</summary>
    private static MicroOp Match(ReadOnlySpan<byte> code, int offset)
    {
        foreach (var op in Alphabet)
            if (
                offset + op.Bytes.Length <= code.Length
                && code.Slice(offset, op.Bytes.Length).SequenceEqual(op.Bytes)
            )
                return op;
        return new MicroOp([code[offset]], 1);
    }

    private static bool Cheaper((int Bytes, int Cycles) a, (int Bytes, int Cycles) b) =>
        a.Bytes < b.Bytes || (a.Bytes == b.Bytes && a.Cycles < b.Cycles);
}
