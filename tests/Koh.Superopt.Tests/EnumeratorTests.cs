using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class EnumeratorTests
{
    [Test]
    public async Task Every_alphabet_op_is_straight_line_and_register_only()
    {
        foreach (var op in Sm83Alphabet.Ops)
            await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly(op)).IsTrue();
    }

    [Test]
    public async Task Rejects_a_memory_op()
    {
        // 0x77 = LD (HL),A — a memory write.
        await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly([0x77])).IsFalse();
    }

    [Test]
    public async Task Rejects_a_control_op()
    {
        // 0x18 = JR r8 — an unconditional jump.
        await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly([0x18, 0x00])).IsFalse();
    }

    [Test]
    public async Task Rejects_an_sp_writing_op()
    {
        // 0x33 = INC SP — memory-free, falls through, but reads and writes SP, which is outside the
        // oracle's observed state (Sm83State.Live has no SP bit).
        await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly([0x33])).IsFalse();
    }

    [Test]
    public async Task Rejects_an_sp_reading_op()
    {
        // 0xE8 = ADD SP,r8 — memory-free, falls through, no side effect, but reads and writes SP.
        await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly([0xE8, 0x00])).IsFalse();
    }

    [Test]
    public async Task Sequences_include_empty_and_grow_to_bound()
    {
        var seqs = Enumerator.Sequences(2).ToList();
        var n = Sm83Alphabet.Ops.Count;
        // empty + N singletons + N*N pairs
        await Assert.That(seqs.Count).IsEqualTo(1 + n + n * n);
        await Assert.That(seqs[0].Length).IsEqualTo(0);
    }
}
