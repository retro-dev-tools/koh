using System.Linq;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Backends;

/// <summary>
/// <see cref="Sm83Backend.EmitContext.FusedCompareBranch"/>: a <c>compare</c> whose result has exactly one
/// use, which is the immediately following <see cref="CondBrInstruction"/> in the same block, is lowered
/// directly at that branch instead of round-tripping through a WRAM slot. The generic (unfused) path is
/// <see cref="Sm83Backend.ArithmeticEmitter.EmitCompare"/>: it emits the comparison's flag-setting sequence
/// via <see cref="Sm83Backend.ArithmeticEmitter.EmitCompareCore"/>, then <c>MaterializeBoolean</c> turns
/// those flags into a 0/1 byte and stores it to a WRAM slot — after which
/// <see cref="Sm83Backend.ControlFlowEmitter.EmitCondBr"/> reloads that byte and re-derives a Z/NZ test from
/// it. When the compare feeds nothing but the very next branch, all of that is pure waste: the CPU already
/// holds the exact flags the branch needs. The fused path
/// (<see cref="Sm83Backend.ControlFlowEmitter.EmitCondBr"/>'s <c>CompareInstruction</c> check, delegating to
/// <c>EmitFusedCompareBranch</c>) calls <see cref="Sm83Backend.ArithmeticEmitter.EmitCompareCore"/> for the
/// SAME flag-setting sequence, then branches on the flags-if-false opcode it returns (JP NC/C/NZ/Z, per
/// predicate) directly to the false edge — skipping the store, the reload, and the AND-A re-test entirely.
///
/// Eligibility (<see cref="Sm83Backend.EmitContext.FusedCompareBranch"/>'s doc comment) mirrors
/// <see cref="Sm83Backend.EmitContext.FusedGep"/>'s gep fusion:
/// <list type="bullet">
/// <item>Single use, function-wide: a compare feeding a branch AND anything else (another arithmetic op, a
/// phi incoming) is used twice and must keep its slot so the second use can still read it back.</item>
/// <item>Immediately next, same block: nothing may be emitted between the compare's flag-setting sequence
/// and the branch that tests those flags — the SM83 has no way to save/restore flags except through a
/// register, so any intervening instruction can silently clobber Z/C/N/H before the branch reads them.</item>
/// </list>
/// See <see cref="Sm83GepFusionTests"/> for the analogous gep-fusion test suite this file is modeled on.
/// </summary>
public class Sm83CompareBranchFusionTests
{
    private static EmitModel Compile(IrModule m) =>
        new Sm83Backend().Compile(m, new DiagnosticBag());

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("mvp", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("link produced no ROM");
        start = Sm83Backend.CodeBase;
        length = model.Sections[0].Data.Length;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static void Run(GameBoySystem gb, int start, int length)
    {
        for (int steps = 0; steps < 100_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }
    }

    private static GameBoySystem RunToExit(IrModule module, Action<GameBoySystem>? setup = null)
    {
        var gb = Load(Compile(module), out int s, out int l);
        setup?.Invoke(gb);
        Run(gb, s, l);
        return gb;
    }

    private static IrConstInt I8(int v) => IrBuilder.ConstInt(IrType.I8, v);

    private static IrType WidthOf(int bytes) =>
        bytes switch
        {
            1 => IrType.I8,
            2 => IrType.I16,
            4 => IrType.I32,
            _ => throw new ArgumentOutOfRangeException(nameof(bytes)),
        };

    /// <summary>Writes <paramref name="value"/>'s low <paramref name="widthBytes"/> bytes, little-endian, to
    /// WRAM starting at <paramref name="addr"/> — the byte pattern a genuinely dynamic (runtime, not
    /// compile-time-constant) operand has once poked into the entry function's fixed WRAM parameter slot.</summary>
    private static void WriteLE(GameBoySystem gb, int addr, long value, int widthBytes)
    {
        for (int k = 0; k < widthBytes; k++)
            gb.DebugWriteByte((ushort)(addr + k), (byte)(value >> (8 * k)));
    }

    /// <summary>Build <c>func main(width a, width b) : i8 { entry: cmp = icmp op(a, b); condbr(cmp, t, f);
    /// t: ret 111; f: ret 222; }</c> — an entry function (no caller, so <c>a</c>/<c>b</c> land at fixed WRAM
    /// parameter slots the test can poke before running) with two genuinely dynamic operands, so the compare
    /// can never be constant-folded and always needs the compiled comparison logic. <paramref name="cmp"/>'s
    /// single use is the immediately following condbr, so it is always fusion-eligible by construction.</summary>
    private static (
        IrModule Module,
        IrFunction Fn,
        CompareInstruction Cmp,
        IrBasicBlock TrueBlock,
        IrBasicBlock FalseBlock
    ) BuildSingleUseCompareBranchFn(IrCompareOp op, IrType width)
    {
        var m = new IrModule("t");
        var a = new IrParameter("a", width);
        var bParam = new IrParameter("b", width);
        var fn = new IrFunction("main", IrType.I8, [a, bParam]);
        m.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var t = fn.AppendBlock("t");
        var f = fn.AppendBlock("f");

        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        var cmp = b.Compare(op, a, bParam);
        b.CondBr(cmp, t, f);

        b.PositionAtEnd(t);
        b.Ret(I8(111));

        b.PositionAtEnd(f);
        b.Ret(I8(222));

        return (m, fn, cmp, t, f);
    }

    /// <summary>Same shape, but the compare feeds the condbr AND an <c>add</c> — two uses, so
    /// <see cref="Sm83Backend.EmitContext.FusedCompareBranch"/> must exclude it. The true arm returns
    /// <c>cmp + 10</c> (11 when the predicate holds) so a wrong second-use value (not just a wrong branch)
    /// would be caught; the false arm returns a distinct sentinel (99) so a wrong branch direction would
    /// be caught too.</summary>
    private static (
        IrModule Module,
        IrFunction Fn,
        CompareInstruction Cmp
    ) BuildTwoUseCompareBranchFn(IrType width)
    {
        var m = new IrModule("t");
        var a = new IrParameter("a", width);
        var bParam = new IrParameter("b", width);
        var fn = new IrFunction("main", IrType.I8, [a, bParam]);
        m.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var t = fn.AppendBlock("t");
        var f = fn.AppendBlock("f");

        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        var cmp = b.Compare(IrCompareOp.Eq, a, bParam);
        var extra = b.Add(cmp, I8(10)); // second use of cmp
        b.CondBr(cmp, t, f); // condbr is the first use

        b.PositionAtEnd(t);
        b.Ret(extra);

        b.PositionAtEnd(f);
        b.Ret(I8(99));

        return (m, fn, cmp);
    }

    /// <summary>Build the <c>FunctionAllocation</c>/<c>EmitContext</c> pair for <paramref name="fn"/> exactly
    /// as <see cref="Sm83Backend.Compile"/> would for the entry function of a module with no globals ahead of
    /// it (residency allowed for locals, but never for the entry function's own parameters — see
    /// <c>Sm83Backend.Compile</c>'s <c>allowParamResidency</c> gate) — so <c>ctx.Slot</c> matches whatever
    /// slot the SAME function gets when compiled through the full <see cref="Compile"/> pipeline.</summary>
    private static (
        FunctionAllocation Allocation,
        Sm83Backend.EmitContext Ctx
    ) BuildAllocationAndContext(IrFunction fn)
    {
        var allocation = FunctionAllocation.For(
            fn,
            Sm83Backend.WramBase,
            allowResidency: true,
            allowParamResidency: false
        );
        var allocations = new Dictionary<IrFunction, FunctionAllocation>(
            ReferenceEqualityComparer.Instance
        )
        {
            [fn] = allocation,
        };
        var ctx = new Sm83Backend.EmitContext(
            new Emitter(),
            fn,
            allocations,
            new Dictionary<IrGlobal, int>(),
            new HashSet<IrFunction>(ReferenceEqualityComparer.Instance),
            isEntry: true,
            softStackBase: 0
        );
        return (allocation, ctx);
    }

    /// <summary>Whether <paramref name="needle"/> occurs anywhere in <paramref name="haystack"/> as a
    /// contiguous byte run.</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    /// <summary>SM83 <c>LD (nn), A</c> targeting <paramref name="addr"/> — the exact 3-byte sequence
    /// <see cref="Sm83Backend.EmitContext.StoreAToAddr"/>/<c>Emitter.StoreA</c> emits to materialize a value
    /// to a WRAM slot. Absence of this sequence (for the fused compare's own slot address) is the concrete,
    /// byte-level evidence that fusion actually eliminated the store, not just "the test still passes."</summary>
    private static byte[] StoreASequence(int addr) =>
        [0xEA, (byte)(addr & 0xFF), (byte)(addr >> 8)];

    // ---- (a) Behavioral end-to-end: correct arm taken, across widths/predicates/both directions --------

    [Test]
    // i8, unsigned (Ult): the classic sign-bit gotcha - 0xFF (255 unsigned, -1 signed) vs 1.
    [Arguments(1, IrCompareOp.Ult, 1L, 255L, true)]
    [Arguments(1, IrCompareOp.Ult, 255L, 1L, false)]
    // i8, signed (Slt).
    [Arguments(1, IrCompareOp.Slt, -5L, 3L, true)]
    [Arguments(1, IrCompareOp.Slt, 5L, -5L, false)]
    // i8, Eq.
    [Arguments(1, IrCompareOp.Eq, 7L, 7L, true)]
    [Arguments(1, IrCompareOp.Eq, 7L, 8L, false)]
    // i16, unsigned (Ult): 0xFFFF (65535 unsigned, -1 signed) vs 1.
    [Arguments(2, IrCompareOp.Ult, 1L, 65535L, true)]
    [Arguments(2, IrCompareOp.Ult, 65535L, 1L, false)]
    // i16, signed (Slt).
    [Arguments(2, IrCompareOp.Slt, -1000L, 5L, true)]
    [Arguments(2, IrCompareOp.Slt, 5L, -1000L, false)]
    // i16, Eq.
    [Arguments(2, IrCompareOp.Eq, 300L, 300L, true)]
    [Arguments(2, IrCompareOp.Eq, 300L, 301L, false)]
    // i32, unsigned (Ult): 0xFFFFFFFF (4294967295 unsigned, -1 signed) vs 1.
    [Arguments(4, IrCompareOp.Ult, 1L, 4294967295L, true)]
    [Arguments(4, IrCompareOp.Ult, 4294967295L, 1L, false)]
    // i32, signed (Slt).
    [Arguments(4, IrCompareOp.Slt, -100000L, 5L, true)]
    [Arguments(4, IrCompareOp.Slt, 5L, -100000L, false)]
    // i32, Eq.
    [Arguments(4, IrCompareOp.Eq, 100000L, 100000L, true)]
    [Arguments(4, IrCompareOp.Eq, 100000L, 100001L, false)]
    public async Task FusedCompareBranch_TakesCorrectArm(
        int widthBytes,
        IrCompareOp op,
        long aVal,
        long bVal,
        bool expectTrue
    )
    {
        var (m, _, _, _, _) = BuildSingleUseCompareBranchFn(op, WidthOf(widthBytes));

        var gb = RunToExit(
            m,
            gb =>
            {
                WriteLE(gb, Sm83Backend.WramBase, aVal, widthBytes);
                WriteLE(gb, Sm83Backend.WramBase + widthBytes, bVal, widthBytes);
            }
        );

        await Assert.That(gb.Registers.A).IsEqualTo((byte)(expectTrue ? 111 : 222));
    }

    // ---- (b) Not-fused behavioral: a two-use compare still computes both uses correctly ------------------

    [Test]
    public async Task TwoUseCompare_TrueArm_StillComputesBothUsesCorrectly()
    {
        // a == b: predicate true -> branch to t, which returns cmp + 10 == 11. Exercises the SECOND use
        // (the add), not just the branch: a naive fusion that discarded the flag-setting would still get
        // the branch right by luck but produce a garbage second value.
        var (m, _, _) = BuildTwoUseCompareBranchFn(IrType.I16);

        var gb = RunToExit(
            m,
            gb =>
            {
                WriteLE(gb, Sm83Backend.WramBase, 42, 2);
                WriteLE(gb, Sm83Backend.WramBase + 2, 42, 2);
            }
        );

        await Assert.That(gb.Registers.A).IsEqualTo((byte)11);
    }

    [Test]
    public async Task TwoUseCompare_FalseArm_StillComputesBothUsesCorrectly()
    {
        // a != b: predicate false -> branch to f, which returns the 99 sentinel (never touching "extra").
        var (m, _, _) = BuildTwoUseCompareBranchFn(IrType.I16);

        var gb = RunToExit(
            m,
            gb =>
            {
                WriteLE(gb, Sm83Backend.WramBase, 42, 2);
                WriteLE(gb, Sm83Backend.WramBase + 2, 43, 2);
            }
        );

        await Assert.That(gb.Registers.A).IsEqualTo((byte)99);
    }

    // ---- (c) Direct eligibility-set test -------------------------------------------------------------------

    [Test]
    public async Task FusedCompareBranch_IsInternalContextSet_ForImmediatelyConsumedCompareOnly()
    {
        // One function, two compares: `fusable`'s only use is the immediately-following condbr (eligible);
        // `notFusable` feeds both an `add` and the SAME condbr's condition (two uses - ineligible), mirroring
        // Sm83GepFusionTests.FusedGep_IsInternalContextSet_ForImmediatelyConsumedGepOnly.
        var a = new IrParameter("a", IrType.I16);
        var bParam = new IrParameter("b", IrType.I16);
        var fn = new IrFunction("main", IrType.I8, [a, bParam]);
        var b = new IrBuilder();
        var entry = fn.AppendBlock("entry");
        var blockA = fn.AppendBlock("a");
        var blockB = fn.AppendBlock("b");

        b.PositionAtEnd(entry);
        var notFusable = b.Compare(IrCompareOp.Eq, a, bParam);
        var extraUse = b.Add(notFusable, I8(1)); // notFusable's first use
        var fusable = b.Compare(IrCompareOp.Ult, a, bParam);
        b.CondBr(fusable, blockA, blockB); // fusable's only use; notFusable's second use is below

        b.PositionAtEnd(blockA);
        b.Ret(b.Add(extraUse, notFusable));

        b.PositionAtEnd(blockB);
        b.Ret(I8(0));

        var (_, ctx) = BuildAllocationAndContext(fn);

        await Assert.That(ctx.FusedCompareBranch.Contains(fusable)).IsTrue();
        await Assert.That(ctx.FusedCompareBranch.Contains(notFusable)).IsFalse();
    }

    // ---- (d) Byte-level: fusion actually elides the WRAM store; the unfused path actually has one --------

    [Test]
    public async Task FusedCompare_EmitsNoStoreToItsOwnSlot()
    {
        var (m, fn, cmp, _, _) = BuildSingleUseCompareBranchFn(IrCompareOp.Ult, IrType.I16);
        var (_, ctx) = BuildAllocationAndContext(fn);
        int slotAddr = ctx.Slot[cmp];

        var model = Compile(m);
        byte[] code = model.Sections[0].Data;

        await Assert.That(ContainsSequence(code, StoreASequence(slotAddr))).IsFalse();
    }

    [Test]
    public async Task UnfusedCompare_DoesEmitAStoreToItsOwnSlot_ProvingTheScanDetectsIt()
    {
        // Control for the test above: same scan technique, but on a two-use compare that is NOT fused,
        // so MaterializeBoolean must still store its 0/1 result to its slot. If this assertion failed too,
        // the scan itself would be broken (always "not found"), and the fused test's "not found" would be
        // meaningless.
        var (m, fn, cmp) = BuildTwoUseCompareBranchFn(IrType.I16);
        var (_, ctx) = BuildAllocationAndContext(fn);
        int slotAddr = ctx.Slot[cmp];

        var model = Compile(m);
        byte[] code = model.Sections[0].Data;

        await Assert.That(ContainsSequence(code, StoreASequence(slotAddr))).IsTrue();
    }

    // ---- Byte savings: canonical `if (a < b) return 1; else return 0;` two-block function -----------------

    /// <summary>
    /// Exact emitted byte count for the canonical fused shape, as concrete evidence of the savings.
    /// Under the OLD, unfused path this same IR would have cost 14 more bytes and roughly 14+ more T-cycles:
    /// <c>EmitCompare</c> would call <c>MaterializeBoolean</c>, adding <c>LD A,0</c> (2) + <c>JP cc,done</c>
    /// (3) + <c>LD A,1</c> (2) + <c>LD (slot),A</c> (3) = 10 bytes that no longer exist; and the old generic
    /// <c>EmitCondBr</c> path would then reload and re-test that byte with <c>LD A,(slot)</c> (3) +
    /// <c>AND A</c> (1) = 4 more bytes that also no longer exist — 14 bytes total, replaced by nothing extra:
    /// the conditional jump the fused path emits is the SAME jump <c>EmitCompareCore</c> already had to
    /// produce, just redirected straight to the false-edge label instead of to a local "materialize" label.
    /// </summary>
    [Test]
    public async Task FusedCompareBranch_EmitsExpectedByteCount_WithNoWramRoundTrip()
    {
        var (m, _, _, _, _) = BuildSingleUseCompareBranchFn(IrCompareOp.Ult, IrType.I8);

        var model = Compile(m);
        byte[] code = model.Sections[0].Data;

        // No LD (nn),A anywhere in this tiny function: the compare's flags feed the branch directly, and
        // both return arms load an immediate straight into A without ever going through a WRAM slot.
        await Assert.That(code.Contains((byte)0xEA)).IsFalse();

        // entry: LD A,(bAddr) [3] ; LD B,A [1] ; LD A,(aAddr) [3] ; SUB B [1]      -> compare flags (8)
        //        JP NC, falseEdge [3]                                              -> fused conditional jump
        //        JP trueBlock [3]                                                  -> true edge
        // falseEdge:
        //        JP falseBlock [3]                                                 -> false edge
        // t:     LD A,111 [2] ; RET [1]                                            -> true arm (3)
        // f:     LD A,222 [2] ; RET [1]                                            -> false arm (3)
        const int compareFlags = 8;
        const int fusedJumpCc = 3;
        const int jumpToTrueBlock = 3;
        const int jumpToFalseBlock = 3;
        const int trueArm = 3;
        const int falseArm = 3;
        int expected =
            compareFlags + fusedJumpCc + jumpToTrueBlock + jumpToFalseBlock + trueArm + falseArm;

        await Assert.That(code.Length).IsEqualTo(expected);
    }
}
