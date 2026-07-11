using Koh.Compiler.Backends.Sm83;

namespace Koh.Compiler.Tests.Backends;

public class Sm83PeepholeTests
{
    private static List<Sm83Peephole.Edit> Edits(byte[] code, params int[] boundaries) =>
        Sm83Peephole.FindEdits(code, 0, code.Length, [.. boundaries], [], allowDeadStore: true);

    private static List<Sm83Peephole.Edit> EditsWithTailCalls(
        byte[] code,
        int[] safeCalls,
        params int[] boundaries
    ) =>
        Sm83Peephole.FindEdits(
            code,
            0,
            code.Length,
            [.. boundaries],
            [.. safeCalls],
            allowDeadStore: true
        );

    [Test]
    public async Task InstructionLength_MatchesTheOpcodeTable()
    {
        byte[] nop = [0x00];
        byte[] ldAd8 = [0x3E, 0x00];
        byte[] jpA16 = [0xC3, 0x00, 0x00];
        byte[] cb = [0xCB, 0x37];
        byte[] ldhFromA8 = [0xF0, 0x44]; // LDH A,(a8) — 2 bytes (mirror of 0xE0)
        byte[] ldhToA8 = [0xE0, 0x44]; // LDH (a8),A — 2 bytes
        await Assert.That(Sm83OpcodeLength.Of(nop[0])).IsEqualTo(1);
        await Assert.That(Sm83OpcodeLength.Of(ldAd8[0])).IsEqualTo(2);
        await Assert.That(Sm83OpcodeLength.Of(jpA16[0])).IsEqualTo(3);
        await Assert.That(Sm83OpcodeLength.Of(cb[0])).IsEqualTo(2);
        await Assert.That(Sm83OpcodeLength.Of(ldhFromA8[0])).IsEqualTo(2);
        await Assert.That(Sm83OpcodeLength.Of(ldhToA8[0])).IsEqualTo(2);
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

    // ---- tail call: CALL nn ; RET → JP nn ------------------------------------

    [Test]
    public async Task FoldsTailCallToJump()
    {
        // CALL 0x1234 ; RET → JP 0x1234. Opcode 0xCD → 0xC3, operand kept, the RET at offset 3 deleted.
        var edits = EditsWithTailCalls([0xCD, 0x34, 0x12, 0xC9], safeCalls: [0]);
        await Assert.That(edits).IsEquivalentTo(new List<Sm83Peephole.Edit> { new(0, 0xC3, 3) });
    }

    [Test]
    public async Task DoesNotFoldTailCall_WhenCallTargetIsNotWhitelisted()
    {
        // Same bytes, but the CALL is not in the safe set (e.g. a far-call thunk or runtime routine).
        await Assert.That(EditsWithTailCalls([0xCD, 0x34, 0x12, 0xC9], safeCalls: [])).IsEmpty();
    }

    [Test]
    public async Task DoesNotFoldTailCall_WhenCallIsNotFollowedByRet()
    {
        // CALL 0x1234 ; NOP — no RET to fold into the jump.
        await Assert.That(EditsWithTailCalls([0xCD, 0x34, 0x12, 0x00], safeCalls: [0])).IsEmpty();
    }

    [Test]
    public async Task DoesNotFoldTailCall_WhenRetIsABranchTarget()
    {
        // The RET at offset 3 is a jump target, so folding it away would drop that edge's return.
        await Assert
            .That(EditsWithTailCalls([0xCD, 0x34, 0x12, 0xC9], safeCalls: [0], boundaries: 3))
            .IsEmpty();
    }

    // ---- dead store: LD (a16),A overwritten before it is read ----------------

    [Test]
    public async Task DeletesDeadStore_WhenSameSlotIsOverwrittenAdjacently()
    {
        // LD (0xC010),A ; LD (0xC010),A — the first store's value is overwritten before any read.
        var edits = Edits([0xEA, 0x10, 0xC0, 0xEA, 0x10, 0xC0]);
        await Assert
            .That(edits)
            .IsEquivalentTo(new List<Sm83Peephole.Edit> { Sm83Peephole.Edit.DeleteRun(0, 3) });
    }

    [Test]
    public async Task DeletesDeadStore_AcrossANonReadingInstruction()
    {
        // LD (0xC010),A ; INC B ; LD (0xC010),A — INC B neither reads memory nor branches, so the first
        // store is still dead.
        var edits = Edits([0xEA, 0x10, 0xC0, 0x04, 0xEA, 0x10, 0xC0]);
        await Assert
            .That(edits)
            .IsEquivalentTo(new List<Sm83Peephole.Edit> { Sm83Peephole.Edit.DeleteRun(0, 3) });
    }

    [Test]
    public async Task KeepsStore_WhenTheSecondTargetsADifferentSlot()
    {
        // LD (0xC010),A ; LD (0xC011),A — different addresses, so the first store is live.
        await Assert.That(Edits([0xEA, 0x10, 0xC0, 0xEA, 0x11, 0xC0])).IsEmpty();
    }

    [Test]
    public async Task KeepsStore_WhenAReadIntervenes()
    {
        // LD (0xC010),A ; LD A,(HL) ; LD (0xC010),A — the load might observe the slot, so keep the store.
        await Assert.That(Edits([0xEA, 0x10, 0xC0, 0x7E, 0xEA, 0x10, 0xC0])).IsEmpty();
    }

    [Test]
    public async Task KeepsStore_WhenAPointerWriteIntervenes()
    {
        // LD (0xC010),A ; LD (HL),A ; LD (0xC010),A — the (HL) store's address is unknown and could be an
        // MMIO register (e.g. an OAM-DMA trigger that then reads WRAM asynchronously), so keep the store.
        await Assert.That(Edits([0xEA, 0x10, 0xC0, 0x77, 0xEA, 0x10, 0xC0])).IsEmpty();
    }

    [Test]
    public async Task KeepsStore_WhenAddressIsAboveWram()
    {
        // LD (0xFF80),A ; LD (0xFF80),A — HRAM/MMIO is above the tracked WRAM window; never elide it.
        await Assert.That(Edits([0xEA, 0x80, 0xFF, 0xEA, 0x80, 0xFF])).IsEmpty();
    }

    [Test]
    public async Task KeepsStore_WhenAddressIsBelowWram()
    {
        // LD (0x9800),A ; LD (0x9800),A — VRAM is below the tracked WRAM window (PPU timing); never elide.
        await Assert.That(Edits([0xEA, 0x00, 0x98, 0xEA, 0x00, 0x98])).IsEmpty();
    }

    [Test]
    public async Task KeepsStore_WhenTheDeadStoreIsABranchTarget()
    {
        // The first store is a jump target; deleting it would strand the edge that lands on it, so keep it.
        await Assert.That(Edits([0xEA, 0x10, 0xC0, 0xEA, 0x10, 0xC0], boundaries: 0)).IsEmpty();
    }

    [Test]
    public async Task KeepsStore_WhenAJoinIntervenes()
    {
        // LD (0xC010),A ; <join> INC B ; LD (0xC010),A — a path entering at the join could read the slot.
        await Assert
            .That(Edits([0xEA, 0x10, 0xC0, 0x04, 0xEA, 0x10, 0xC0], boundaries: 3))
            .IsEmpty();
    }

    [Test]
    public async Task KeepsStore_WhenDeadStoreEliminationIsDisabled()
    {
        // With an interrupt handler in the module the backend passes allowDeadStore: false — an async
        // handler could read the slot between the two stores, so the (mainline-)dead store must stay.
        var edits = Sm83Peephole.FindEdits(
            [0xEA, 0x10, 0xC0, 0xEA, 0x10, 0xC0],
            0,
            6,
            [],
            [],
            allowDeadStore: false
        );
        await Assert.That(edits).IsEmpty();
    }
}
