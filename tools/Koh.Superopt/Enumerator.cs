namespace Koh.Superopt;

/// <summary>Bounded enumeration of candidate sequences: the empty sequence, then every concatenation of
/// 1..maxLength alphabet ops, flattened to raw bytes. The empty sequence lets the miner discover
/// deletions (e.g. a flags-only op removed when flags are dead).</summary>
public static class Enumerator
{
    public static IEnumerable<byte[]> Sequences(int maxLength)
    {
        yield return [];
        var frontier = new List<byte[]> { Array.Empty<byte>() };
        for (var length = 1; length <= maxLength; length++)
        {
            var next = new List<byte[]>();
            foreach (var prefix in frontier)
            foreach (var op in Sm83Alphabet.Ops)
            {
                var seq = new byte[prefix.Length + op.Length];
                prefix.CopyTo(seq, 0);
                op.CopyTo(seq, prefix.Length);
                next.Add(seq);
                yield return seq;
            }
            frontier = next;
        }
    }
}
