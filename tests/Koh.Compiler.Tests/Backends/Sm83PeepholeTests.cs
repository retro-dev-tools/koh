using Koh.Compiler.Backends.Sm83;

namespace Koh.Compiler.Tests.Backends;

public class Sm83PeepholeTests
{
    private static List<Sm83Peephole.Edit> Edits(byte[] code, params int[] boundaries) =>
        Sm83Peephole.FindEdits(code, 0, code.Length, [.. boundaries]);

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

    // ---- LD A,0 → XOR A (flag-liveness aware) ---------------------------------

    [Test]
    public async Task ConvertsZeroLoad_WhenFollowedByAFlagRedefiningOp()
    {
        // LD A,0 ; SUB B — SUB rewrites all flags and reads none, so the zero-load's flags are dead.
        var edits = Edits([0x3E, 0x00, 0x90]);
        await Assert.That(edits).IsEquivalentTo(new List<Sm83Peephole.Edit> { new(0, 0xAF) });
    }

    [Test]
    public async Task ConvertsZeroLoad_WhenFollowedByAFlagRewritingCbRotate()
    {
        // LD A,0 ; SLA B (CB 0x20) — reads no carry but rewrites all flags. The old hand-rolled scan
        // treated every CB op as a flag reader and missed this; the MIR footprint proves the flags dead.
        var edits = Edits([0x3E, 0x00, 0xCB, 0x20]);
        await Assert.That(edits).IsEquivalentTo(new List<Sm83Peephole.Edit> { new(0, 0xAF) });
    }

    [Test]
    public async Task KeepsZeroLoad_WhenFollowedByCarryConsumer()
    {
        // LD A,0 ; ADC A,B — ADC reads carry, so XOR A (which clears it) would be unsound. Leave it.
        await Assert.That(Edits([0x3E, 0x00, 0x88])).IsEmpty();
    }

    [Test]
    public async Task KeepsZeroLoad_WhenFollowedByACbCarryRotate()
    {
        // LD A,0 ; RL B (CB 0x10) — a rotate-through-carry reads C, so the flags are live.
        await Assert.That(Edits([0x3E, 0x00, 0xCB, 0x10])).IsEmpty();
    }

    [Test]
    public async Task KeepsZeroLoad_WhenFollowedByAControlFlowBoundary()
    {
        // LD A,0 ; RET — flags may be live across the return; be conservative.
        await Assert.That(Edits([0x3E, 0x00, 0xC9])).IsEmpty();
    }

    [Test]
    public async Task KeepsZeroLoad_WhenNextInstructionIsABranchTarget()
    {
        // LD A,0 ; XOR C — but the XOR C is a branch target (join), so flags are conservatively live.
        await Assert.That(Edits([0x3E, 0x00, 0xA9], boundaries: 2)).IsEmpty();
    }

    // ---- (HL) load/store + INC/DEC HL folding --------------------------------

    [Test]
    public async Task FoldsHlLoadWithFollowingIncrement()
    {
        // LD A,(HL) ; INC HL → LD A,(HL+)
        await Assert
            .That(Edits([0x7E, 0x23]))
            .IsEquivalentTo(new List<Sm83Peephole.Edit> { new(0, 0x2A) });
    }

    [Test]
    public async Task FoldsHlStoreWithFollowingDecrement()
    {
        // LD (HL),A ; DEC HL → LD (HL-),A
        await Assert
            .That(Edits([0x77, 0x2B]))
            .IsEquivalentTo(new List<Sm83Peephole.Edit> { new(0, 0x32) });
    }

    [Test]
    public async Task DoesNotFoldWhenIncrementIsABranchTarget()
    {
        // A jump can land on the INC HL, so folding it away would break that edge.
        await Assert.That(Edits([0x7E, 0x23], boundaries: 1)).IsEmpty();
    }

    [Test]
    public async Task DoesNotFoldAnUnrelatedInstruction()
    {
        // LD A,(HL) ; INC B — the step is not INC/DEC HL, so there is nothing to fold.
        await Assert.That(Edits([0x7E, 0x04])).IsEmpty();
    }
}
