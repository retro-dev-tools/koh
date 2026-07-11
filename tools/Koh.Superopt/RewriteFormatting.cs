namespace Koh.Superopt;

/// <summary>One-line human-readable rendering of a mined <see cref="Rewrite"/> for the report.</summary>
public static class RewriteFormatting
{
    public static string Describe(Rewrite r)
    {
        var from = Hex(r.From);
        var to = r.To.Length == 0 ? "(removed)" : Hex(r.To);
        var bytes = $"-{r.BytesSaved} byte{(r.BytesSaved == 1 ? "" : "s")}";
        var cycles = r.TCyclesSaved != 0 ? $", -{r.TCyclesSaved} T" : "";
        return $"{from, -12} -> {to, -12}  [{r.Live}]  {bytes}{cycles}";
    }

    private static string Hex(byte[] bytes) =>
        string.Join(' ', bytes.Select(b => b.ToString("X2"))); // "" for an empty sequence
}
