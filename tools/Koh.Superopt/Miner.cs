using System.Text;

namespace Koh.Superopt;

/// <summary>One discovered rewrite: <see cref="From"/> is equivalent to the cheaper <see cref="To"/>
/// under <see cref="Live"/>, saving <see cref="BytesSaved"/> bytes then <see cref="TCyclesSaved"/>
/// T-cycles.</summary>
public readonly record struct Rewrite(
    byte[] From,
    byte[] To,
    Live Live,
    int BytesSaved,
    int TCyclesSaved
);

/// <summary>
/// The discovery miner. Enumerates straight-line, register-only sequences up to a length bound, groups
/// them by the live-out behavior they exhibit over a fixed probe battery, and for each group reports the
/// cheapest member as the rewrite target for its costlier siblings — a bounded superoptimizer whose
/// equivalence oracle is the Koh emulator. Cost is bytes first, then T-cycles.
/// </summary>
public sealed class Miner
{
    private readonly int _maxLength;
    private readonly int _probeCount;
    private readonly int _seed;
    private readonly Sm83Oracle _oracle = new();

    public Miner(int maxLength = 2, int probeCount = 24, int seed = 0x5A83)
    {
        _maxLength = maxLength;
        _probeCount = probeCount;
        _seed = seed;
    }

    public IReadOnlyList<Rewrite> Mine(Live live)
    {
        var probes = Probes();

        // Bucket key = the concatenated live-out bytes across the probe battery. Two sequences share a
        // bucket iff they behave identically on every probe — a fast, deterministic pre-filter.
        var buckets = new Dictionary<string, List<(byte[] Code, int Bytes, ulong Cycles)>>();
        foreach (var code in Enumerator.Sequences(_maxLength))
        {
            var (signature, cycles) = Signature(code, probes, live);
            var entry = (Code: code, Bytes: code.Length, Cycles: cycles);
            if (!buckets.TryGetValue(signature, out var list))
                buckets[signature] = list = [];
            list.Add(entry);
        }

        var rewrites = new List<Rewrite>();
        foreach (var list in buckets.Values)
        {
            if (list.Count < 2)
                continue;
            // Cheapest member: fewest bytes, then fewest cycles.
            var best = list[0];
            foreach (var e in list)
                if (e.Bytes < best.Bytes || (e.Bytes == best.Bytes && e.Cycles < best.Cycles))
                    best = e;

            foreach (var e in list)
            {
                var strictlyCostlier =
                    e.Bytes > best.Bytes || (e.Bytes == best.Bytes && e.Cycles > best.Cycles);
                if (!strictlyCostlier)
                    continue;
                // Re-verify with random trials to reject a coincidental bucket collision. Use a seed
                // distinct from the probe battery's so the re-check is genuinely independent inputs,
                // not the same ones that already grouped the pair.
                if (!_oracle.AreEquivalent(e.Code, best.Code, live, seed: _seed ^ 0x3C3C))
                    continue;
                rewrites.Add(
                    new Rewrite(
                        e.Code,
                        best.Code,
                        live,
                        e.Bytes - best.Bytes,
                        (int)(e.Cycles - best.Cycles)
                    )
                );
            }
        }
        return rewrites;
    }

    /// <summary>The behavior signature of <paramref name="code"/>: live-out bytes across the probe
    /// battery, plus the (input-independent) T-cycle cost measured on the first probe.</summary>
    private (string Signature, ulong Cycles) Signature(byte[] code, Sm83State[] probes, Live live)
    {
        var sb = new StringBuilder();
        ulong cycles = 0;
        for (var i = 0; i < probes.Length; i++)
        {
            var (state, t) = _oracle.Run(code, probes[i]);
            if (i == 0)
                cycles = t;
            AppendLive(sb, state, live);
        }
        return (sb.ToString(), cycles);
    }

    private static void AppendLive(StringBuilder sb, Sm83State s, Live live)
    {
        if (live.HasFlag(Live.A))
            sb.Append((char)s.A);
        if (live.HasFlag(Live.B))
            sb.Append((char)s.B);
        if (live.HasFlag(Live.C))
            sb.Append((char)s.C);
        if (live.HasFlag(Live.D))
            sb.Append((char)s.D);
        if (live.HasFlag(Live.E))
            sb.Append((char)s.E);
        if (live.HasFlag(Live.H))
            sb.Append((char)s.H);
        if (live.HasFlag(Live.L))
            sb.Append((char)s.L);
        if (live.HasFlag(Live.Flags))
            sb.Append((char)(s.F & 0xF0));
        sb.Append('|');
    }

    private Sm83State[] Probes()
    {
        var random = new Random(_seed);
        var probes = new Sm83State[_probeCount];
        Span<byte> bytes = stackalloc byte[8];
        for (var i = 0; i < _probeCount; i++)
        {
            random.NextBytes(bytes);
            probes[i] = new Sm83State(
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
        return probes;
    }
}
