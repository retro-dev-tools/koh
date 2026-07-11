using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class Sm83OracleTests
{
    // 3E 00 = LD A,0 (2 bytes, leaves flags untouched); AF = XOR A (1 byte, sets Z, clears NHC).
    private static readonly byte[] LdA0 = [0x3E, 0x00];
    private static readonly byte[] XorA = [0xAF];

    [Test]
    public async Task XorA_equals_LdA0_when_flags_are_dead()
    {
        var oracle = new Sm83Oracle();
        await Assert.That(oracle.AreEquivalent(LdA0, XorA, Live.A)).IsTrue();
    }

    [Test]
    public async Task XorA_differs_from_LdA0_when_flags_are_live()
    {
        var oracle = new Sm83Oracle();
        await Assert.That(oracle.AreEquivalent(LdA0, XorA, Live.A | Live.Flags)).IsFalse();
    }

    [Test]
    public async Task Run_reports_tcycles_and_final_state()
    {
        var oracle = new Sm83Oracle();
        var (state, tcycles) = oracle.Run(XorA, new Sm83State(0x42, 0, 0, 0, 0, 0, 0, 0, 0xFFFE));
        await Assert.That(state.A).IsEqualTo((byte)0);
        await Assert.That(tcycles).IsGreaterThan((ulong)0);
    }
}
