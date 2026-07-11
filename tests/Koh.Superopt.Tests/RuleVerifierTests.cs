using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class RuleVerifierTests
{
    [Test]
    public async Task Confirms_a_valid_flags_dead_rule()
    {
        var rule = new RewriteRule("ld_a0_to_xor_a", [0x3E, 0x00], [0xAF], Live.AllRegs);
        var verdict = new RuleVerifier().Verify([rule]).Single();
        await Assert.That(verdict.WellFormed).IsTrue();
        await Assert.That(verdict.Holds).IsTrue();
    }

    [Test]
    public async Task Rejects_an_unsound_rule()
    {
        // Same rewrite but claiming flags are preserved — XOR A clobbers them, so it must not hold.
        var rule = new RewriteRule("bad", [0x3E, 0x00], [0xAF], Live.All);
        var verdict = new RuleVerifier().Verify([rule]).Single();
        await Assert.That(verdict.WellFormed).IsTrue();
        await Assert.That(verdict.Holds).IsFalse();
    }

    [Test]
    public async Task Flags_a_rule_outside_the_register_only_domain()
    {
        // 0x77 = LD (HL),A touches memory — outside the oracle's soundness domain.
        var rule = new RewriteRule("mem", [0x77], [0x77], Live.All);
        var verdict = new RuleVerifier().Verify([rule]).Single();
        await Assert.That(verdict.WellFormed).IsFalse();
    }
}
