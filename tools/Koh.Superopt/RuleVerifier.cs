namespace Koh.Superopt;

/// <summary>A declared rewrite rule: <see cref="From"/> may be replaced by <see cref="To"/> when only
/// <see cref="Live"/> is live-out.</summary>
public readonly record struct RewriteRule(string Name, byte[] From, byte[] To, Live Live);

/// <summary>The verdict for one rule: whether both sides are inside the oracle's register-only domain
/// (<see cref="WellFormed"/>) and, if so, whether the rewrite preserves the declared live-out
/// (<see cref="Holds"/>).</summary>
public readonly record struct RuleVerdict(RewriteRule Rule, bool Holds, bool WellFormed);

/// <summary>
/// Certifies declared rewrite rules against emulator ground truth — a regression guard for the peephole's
/// hand-written rules. A rule outside the straight-line, register-only domain is flagged
/// <see cref="RuleVerdict.WellFormed"/> = false rather than judged unsoundly.
/// </summary>
public sealed class RuleVerifier
{
    private readonly Sm83Oracle _oracle = new();

    public IReadOnlyList<RuleVerdict> Verify(IEnumerable<RewriteRule> rules)
    {
        var verdicts = new List<RuleVerdict>();
        foreach (var rule in rules)
        {
            var wellFormed =
                Sm83Alphabet.IsStraightLineRegisterOnly(rule.From)
                && Sm83Alphabet.IsStraightLineRegisterOnly(rule.To);
            var holds = wellFormed && _oracle.AreEquivalent(rule.From, rule.To, rule.Live);
            verdicts.Add(new RuleVerdict(rule, holds, wellFormed));
        }
        return verdicts;
    }
}
