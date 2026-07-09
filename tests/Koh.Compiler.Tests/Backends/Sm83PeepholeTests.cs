using Koh.Compiler.Backends.Sm83;

namespace Koh.Compiler.Tests.Backends;

public class Sm83PeepholeTests
{
    [Test]
    public async Task InstructionLength_MatchesTheOpcodeTable()
    {
        byte[] nop = [0x00];
        byte[] ldAd8 = [0x3E, 0x00];
        byte[] jpA16 = [0xC3, 0x00, 0x00];
        byte[] cb = [0xCB, 0x37];
        byte[] ldhFromA8 = [0xF0, 0x44]; // LDH A,(a8) — 2 bytes (mirror of 0xE0)
        byte[] ldhToA8 = [0xE0, 0x44]; // LDH (a8),A — 2 bytes
        await Assert.That(Sm83Peephole.InstructionLength(nop, 0)).IsEqualTo(1);
        await Assert.That(Sm83Peephole.InstructionLength(ldAd8, 0)).IsEqualTo(2);
        await Assert.That(Sm83Peephole.InstructionLength(jpA16, 0)).IsEqualTo(3);
        await Assert.That(Sm83Peephole.InstructionLength(cb, 0)).IsEqualTo(2);
        await Assert.That(Sm83Peephole.InstructionLength(ldhFromA8, 0)).IsEqualTo(2);
        await Assert.That(Sm83Peephole.InstructionLength(ldhToA8, 0)).IsEqualTo(2);
    }

    [Test]
    public async Task ConvertsZeroLoad_WhenFollowedByAFlagRedefiningOp()
    {
        // LD A,0 ; SUB B  — SUB redefines all flags and reads none, so the zero-load's flags are dead.
        byte[] code = [0x3E, 0x00, 0x90];
        var edits = Sm83Peephole.FindZeroLoadEdits(code, 0, code.Length, []);
        await Assert.That(edits).IsEquivalentTo(new List<int> { 0 });
    }

    [Test]
    public async Task KeepsZeroLoad_WhenFollowedByCarryConsumer()
    {
        // LD A,0 ; ADC A,B — ADC reads carry, so XOR A (which clears it) would be unsound. Leave it.
        byte[] code = [0x3E, 0x00, 0x88];
        var edits = Sm83Peephole.FindZeroLoadEdits(code, 0, code.Length, []);
        await Assert.That(edits).IsEmpty();
    }

    [Test]
    public async Task KeepsZeroLoad_WhenFollowedByAControlFlowBoundary()
    {
        // LD A,0 ; RET — flags may be live across the return; be conservative.
        byte[] code = [0x3E, 0x00, 0xC9];
        var edits = Sm83Peephole.FindZeroLoadEdits(code, 0, code.Length, []);
        await Assert.That(edits).IsEmpty();
    }

    [Test]
    public async Task KeepsZeroLoad_WhenNextInstructionIsABranchTarget()
    {
        // LD A,0 ; XOR C — but the XOR C is a branch target (join), so flags are conservatively live.
        byte[] code = [0x3E, 0x00, 0xA9];
        var edits = Sm83Peephole.FindZeroLoadEdits(code, 0, code.Length, [2]);
        await Assert.That(edits).IsEmpty();
    }
}
