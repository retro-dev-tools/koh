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
/// <see cref="Sm83Backend.EmitContext.FusedGep"/>: a dynamic <c>gep</c> whose result has exactly one use,
/// which is the immediately following <c>load</c>/<c>store</c> in the same block, is computed directly at
/// that use instead of round-tripping through a WRAM slot (skips the store-to-slot and reload-from-slot
/// that every other SSA value goes through in this backend's otherwise fully naive, non-optimizing
/// per-instruction lowering). See <c>Sm83Backend.EmitContext.ComputeFusedGeps</c> for the eligibility
/// rule and why "immediately following" (not just "later in the same block") is load-bearing: an earlier,
/// looser version of this rule let <see cref="FunctionAllocation"/> reuse the gep's operands' WRAM slots
/// for an unrelated value in the gap before the deferred computation read them, silently corrupting the
/// address — caught by <c>CSharpEndToEndTests.Pointer_IndexedStoreWithVariableOffset</c> and 27 similar
/// end-to-end failures during development.
/// </summary>
public class Sm83GepFusionTests
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

    private static IrConstInt I16(int v) => IrBuilder.ConstInt(IrType.I16, v);

    private static IrConstInt I8(int v) => IrBuilder.ConstInt(IrType.I8, v);

    /// <summary>Build <c>func main(byte* p, i16 idx) : i8 { entry: body(builder, p, idx) }</c> — an entry
    /// function (no caller, so <c>p</c>/<c>idx</c> land at fixed WRAM parameter slots the test can poke
    /// before running) with a genuinely dynamic pointer and index, so <c>gep(p, idx)</c> cannot be folded
    /// to a compile-time address and always needs a WRAM slot (fusion is only ever a candidate then).</summary>
    private static IrModule PointerIndexFn(Action<IrBuilder, IrParameter, IrParameter> body)
    {
        var m = new IrModule("t");
        var p = new IrParameter("p", IrType.Pointer(IrType.I8));
        var idx = new IrParameter("idx", IrType.I16);
        var fn = new IrFunction("main", IrType.I8, [p, idx]);
        m.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        body(b, p, idx);
        return m;
    }

    [Test]
    public async Task SingleUseGep_ImmediatelyStored_ComputesCorrectAddress()
    {
        // *(p + idx) = 77, with p and idx both runtime values — the gep is single-use and its store is
        // the very next instruction, so it fuses. Correctness (not just "no crash") is the point: the
        // fused address computation must still land on exactly p + idx.
        var m = PointerIndexFn(
            (b, p, idx) =>
            {
                b.Store(I8(77), b.Gep(p, idx, IrType.I8));
                b.Ret(I8(0));
            }
        );

        var gb = RunToExit(
            m,
            gb =>
            {
                gb.DebugWriteByte(Sm83Backend.WramBase, 0x50); // p low
                gb.DebugWriteByte(Sm83Backend.WramBase + 1, 0xC0); // p high -> p = 0xC050
                gb.DebugWriteByte(Sm83Backend.WramBase + 2, 3); // idx low
                gb.DebugWriteByte(Sm83Backend.WramBase + 3, 0); // idx high -> idx = 3
            }
        );

        await Assert.That(gb.DebugReadByte(0xC053)).IsEqualTo((byte)77); // p + idx
    }

    [Test]
    public async Task SingleUseGep_ImmediatelyLoaded_ComputesCorrectAddress()
    {
        // return *(p + idx) — the gep's single use is the immediately following load, so it fuses too.
        var m = PointerIndexFn(
            (b, p, idx) =>
            {
                b.Ret(b.Load(b.Gep(p, idx, IrType.I8)));
            }
        );

        var gb = RunToExit(
            m,
            gb =>
            {
                gb.DebugWriteByte(Sm83Backend.WramBase, 0x50);
                gb.DebugWriteByte(Sm83Backend.WramBase + 1, 0xC0); // p = 0xC050
                gb.DebugWriteByte(Sm83Backend.WramBase + 2, 4);
                gb.DebugWriteByte(Sm83Backend.WramBase + 3, 0); // idx = 4
                gb.DebugWriteByte(0xC054, 200); // *(p + idx)
            }
        );

        await Assert.That(gb.Registers.A).IsEqualTo((byte)200);
    }

    [Test]
    public async Task TwoUseGep_StillComputesBothUsesCorrectly()
    {
        // *(p + idx) = 9; return *(p + idx) + 1 — the SAME gep feeds a store AND a load, so it has two
        // uses and is NOT fused (it keeps its slot and is read back for the second use). This is the
        // shape a naive "single-use-anywhere-later" fusion would have wrongly touched; it must still just
        // compute the plain, unfused way and give the right answer.
        var m = PointerIndexFn(
            (b, p, idx) =>
            {
                var g = b.Gep(p, idx, IrType.I8);
                b.Store(I8(9), g);
                b.Ret(b.Add(b.Load(g), I8(1)));
            }
        );

        var gb = RunToExit(
            m,
            gb =>
            {
                gb.DebugWriteByte(Sm83Backend.WramBase, 0x60);
                gb.DebugWriteByte(Sm83Backend.WramBase + 1, 0xC0); // p = 0xC060
                gb.DebugWriteByte(Sm83Backend.WramBase + 2, 1);
                gb.DebugWriteByte(Sm83Backend.WramBase + 3, 0); // idx = 1
            }
        );

        await Assert.That(gb.Registers.A).IsEqualTo((byte)10);
        await Assert.That(gb.DebugReadByte(0xC061)).IsEqualTo((byte)9);
    }

    [Test]
    public async Task DeferredSingleUseGep_AcrossInterveningInstructions_StillComputesCorrectly()
    {
        // Mirrors the exact shape that broke an earlier, looser version of this pass: p and idx are each
        // read TWICE through their own alloca'd locals (once to build a gep that's immediately used, once
        // more afterward to build a second, single-use gep whose use is several instructions later). The
        // second gep must NOT fuse (its use isn't immediately next) and must still read the correct,
        // uncorrupted operands — regression guard for the WRAM-slot-reuse corruption described in the
        // class remarks.
        var m = PointerIndexFn(
            (b, p, idx) =>
            {
                var pSlot = b.Alloca(IrType.Pointer(IrType.I8));
                b.Store(p, pSlot);
                var idxSlot = b.Alloca(IrType.I16);
                b.Store(idx, idxSlot);

                // First read-through: immediately-fused gep + load (values discarded, just occupies slots
                // the allocator might otherwise consider free for reuse before the deferred gep below).
                var p1 = b.Load(pSlot);
                var idx1 = b.Load(idxSlot);
                var discard = b.Load(b.Gep(p1, idx1, IrType.I8));

                // Second read-through: gep is single-use, but several instructions (the store below, plus
                // whatever the allocator schedules) separate it from that use — not eligible for fusion.
                var p2 = b.Load(pSlot);
                var idx2 = b.Load(idxSlot);
                var g2 = b.Gep(p2, idx2, IrType.I8);
                b.Store(b.Add(discard, I8(1)), g2);

                b.Ret(I8(0));
            }
        );

        var gb = RunToExit(
            m,
            gb =>
            {
                gb.DebugWriteByte(Sm83Backend.WramBase, 0x70);
                gb.DebugWriteByte(Sm83Backend.WramBase + 1, 0xC0); // p = 0xC070
                gb.DebugWriteByte(Sm83Backend.WramBase + 2, 2);
                gb.DebugWriteByte(Sm83Backend.WramBase + 3, 0); // idx = 2
                gb.DebugWriteByte(0xC072, 41); // *(p + idx) before the run
            }
        );

        await Assert.That(gb.DebugReadByte(0xC072)).IsEqualTo((byte)42); // 41 + 1, same address both times
    }

    [Test]
    public async Task FusedGep_IsInternalContextSet_ForImmediatelyConsumedGepOnly()
    {
        // Direct check of the eligibility set itself: build one function with a fusable gep (store is the
        // very next instruction) and one with a non-fusable gep (two uses), and confirm FusedGep contains
        // exactly the fusable one. This is the mechanism the behavioral tests above exercise indirectly.
        var p = new IrParameter("p", IrType.Pointer(IrType.I8));
        var idx = new IrParameter("idx", IrType.I16);
        var fn = new IrFunction("main", IrType.I8, [p, idx]);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var fusable = b.Gep(p, idx, IrType.I8);
        b.Store(I8(1), fusable);
        var notFusable = b.Gep(p, idx, IrType.I8);
        b.Store(I8(2), notFusable);
        b.Ret(b.Load(notFusable)); // second use of notFusable -> ineligible

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

        await Assert.That(ctx.FusedGep.Contains(fusable)).IsTrue();
        await Assert.That(ctx.FusedGep.Contains(notFusable)).IsFalse();
    }
}
