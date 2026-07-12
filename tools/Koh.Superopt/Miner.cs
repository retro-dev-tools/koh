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
    private const int ProbeCount = 24; // never varied by a caller
    private const int Seed = 0x5A83; // never varied by a caller

    private readonly int _maxLength;
    private readonly Sm83Oracle _oracle = new();

    public Miner(int maxLength = 2)
    {
        _maxLength = maxLength;
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
            // Cheapest member: fewest bytes, then fewest cycles. MinBy keeps the first minimum, same as
            // a manual scan would.
            var best = list.MinBy(e => (e.Bytes, e.Cycles));

            foreach (var e in list)
            {
                var strictlyCostlier =
                    e.Bytes > best.Bytes || (e.Bytes == best.Bytes && e.Cycles > best.Cycles);
                if (!strictlyCostlier)
                    continue;
                // Re-verify with random trials to reject a coincidental bucket collision. Use a seed
                // distinct from the probe battery's so the re-check is genuinely independent inputs,
                // not the same ones that already grouped the pair.
                if (!_oracle.AreEquivalent(e.Code, best.Code, live, seed: Seed ^ 0x3C3C))
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

    /// <summary>Appends the live-out parts of <paramref name="s"/>, walking the shared <see
    /// cref="Sm83State.LiveFields"/> table, then a '|' separator.</summary>
    private static void AppendLive(StringBuilder sb, Sm83State s, Live live)
    {
        foreach (var (flag, get) in Sm83State.LiveFields)
            if (live.HasFlag(flag))
                sb.Append((char)get(s));
        sb.Append('|');
    }

    private static Sm83State[] Probes()
    {
        var random = new Random(Seed);
        var probes = new Sm83State[ProbeCount];
        for (var i = 0; i < ProbeCount; i++)
            probes[i] = Sm83Oracle.RandomState(random);
        return probes;
    }
}
