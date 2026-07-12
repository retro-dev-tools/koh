namespace Koh.Compiler.Tests.Superopt;

public class SuperoptimizerTests
{
    // Byte encodings used across the cases.
    private static readonly byte[] LoadAZero = [0x3E, 0x00]; // LD A, 0
    private static readonly byte[] XorA = [0xAF]; // XOR A
    private static readonly byte[] LoadAFromB = [0x78]; // LD A, B

    [Test]
    public async Task Oracle_LoadZeroEqualsXorA_WhenFlagsDead()
    {
        // Both leave A = 0; they differ only in flags, which are not live here.
        var equivalent = new Sm83Oracle().AreEquivalent(LoadAZero, XorA, Live.A);
        await Assert.That(equivalent).IsTrue();
    }

    [Test]
    public async Task Oracle_LoadZeroDiffersFromXorA_WhenFlagsLive()
    {
        // XOR A clobbers the flags (Z set, others cleared); LD A,0 leaves them as they were. With flags
        // live-out the two are not interchangeable — the oracle must catch it.
        var equivalent = new Sm83Oracle().AreEquivalent(LoadAZero, XorA, Live.A | Live.Flags);
        await Assert.That(equivalent).IsFalse();
    }

    [Test]
    public async Task Superoptimizer_RediscoversXorAForLoadZero()
    {
        // The classic peephole: with flags dead, the 2-byte LD A,0 reduces to the 1-byte XOR A — and the
        // search finds it from first principles via the emulator, not a hand-written rule.
        var result = new Sm83Superoptimizer().Optimize(LoadAZero, Live.A);
        await Assert.That(result).IsEquivalentTo(XorA);
    }

    [Test]
    public async Task Superoptimizer_KeepsLoadZeroWhenFlagsLive()
    {
        // No 1-byte instruction zeroes A while preserving the flags, so nothing cheaper is equivalent and
        // the original survives. This is the liveness-respecting behaviour the byte-scan peephole has to
        // encode by hand.
        var result = new Sm83Superoptimizer().Optimize(LoadAZero, Live.A | Live.Flags);
        await Assert.That(result).IsEquivalentTo(LoadAZero);
    }

    [Test]
    public async Task Superoptimizer_ShrinksRedundantMove()
    {
        // Two identical moves collapse to one: LD A,B; LD A,B ≡ LD A,B for A live-out.
        var result = new Sm83Superoptimizer().Optimize([0x78, 0x78], Live.A);
        await Assert.That(result).IsEquivalentTo(LoadAFromB);
    }
}
