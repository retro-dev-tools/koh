using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class MinerTests
{
    private static bool Has(IReadOnlyList<Rewrite> rs, byte[] from, byte[] to) =>
        rs.Any(r => r.From.AsSpan().SequenceEqual(from) && r.To.AsSpan().SequenceEqual(to));

    [Test]
    public async Task Rediscovers_LdA0_to_XorA_when_flags_are_dead()
    {
        var rewrites = new Miner(maxLength: 2).Mine(Live.AllRegs); // flags dead
        await Assert.That(Has(rewrites, [0x3E, 0x00], [0xAF])).IsTrue();
    }

    [Test]
    public async Task Declines_LdA0_to_XorA_when_flags_are_live()
    {
        var rewrites = new Miner(maxLength: 2).Mine(Live.All); // flags live
        await Assert.That(Has(rewrites, [0x3E, 0x00], [0xAF])).IsFalse();
    }

    [Test]
    public async Task Shrinks_double_move_to_single()
    {
        var rewrites = new Miner(maxLength: 2).Mine(Live.All);
        await Assert.That(Has(rewrites, [0x78, 0x78], [0x78])).IsTrue();
    }

    [Test]
    public async Task Every_rewrite_is_a_strict_improvement()
    {
        foreach (var r in new Miner(maxLength: 2).Mine(Live.All))
        {
            var betterBytes = r.To.Length < r.From.Length;
            var sameBytesFewerCycles = r.To.Length == r.From.Length && r.TCyclesSaved > 0;
            await Assert.That(betterBytes || sameBytesFewerCycles).IsTrue();
        }
    }
}
