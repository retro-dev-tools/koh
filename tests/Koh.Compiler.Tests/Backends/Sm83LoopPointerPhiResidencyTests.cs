using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Backends.Sm83.Mir;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Backends;

/// <summary>
/// Loop-residency-generalization spec, execution step 2: <c>SelectLoopInductionResidents</c> admits a
/// width-2 (i16/pointer) loop-carried phi and assigns it a register PAIR (<c>Hl</c>/<c>De</c>), porting
/// Layer 2's dereference/post-increment fusion so it sources from the resident phi's own pair instead of
/// a separate memory-alloca reload. Nothing in the frontend produces a pointer-typed phi yet (Mem2RegPass
/// stays integer-only until step 3), so these tests hand-build the IR shape directly with
/// <see cref="IrBuilder"/> — see the spec's "Execution" step 2 note that this is the expected way to
/// exercise the mechanism ahead of Mem2Reg's own pointer promotion.
/// </summary>
public class Sm83LoopPointerPhiResidencyTests
{
    private static IrConstInt I8(long v) => IrBuilder.ConstInt(IrType.I8, v);

    private static IrConstInt Ptr(long addr) => IrBuilder.ConstInt(IrType.Pointer(IrType.I8), addr);

    /// <summary>Hand-builds a single-pointer-phi walk loop: <c>src</c> starts at <paramref name="start"/>,
    /// dereferences itself once per iteration (a <c>Load</c> if <paramref name="derefIsLoad"/>, else a
    /// <c>Store</c> of a fixed byte) through a fixed-address helper pointer, and steps by <c>+1</c> on the
    /// back edge — <c>TryFindLoopShape</c>'s gep arm plus <c>TryFindPhiDerefSite</c>'s single-dereference
    /// recognition. <paramref name="count"/> bounds the loop via a plain (non-resident-eligible, since D/E
    /// end up claimed by the pointer pair) byte counter.</summary>
    private static (IrModule Module, IrFunction Fn, PhiInstruction Src) BuildSinglePointerWalkLoop(
        int start,
        int count,
        bool derefIsLoad,
        int fixedOtherAddr
    )
    {
        var module = new IrModule("t");
        var fn = new IrFunction("main", IrType.Void, []);
        module.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var header = fn.AppendBlock("header");
        var tail = fn.AppendBlock("tail");
        var exit = fn.AppendBlock("exit");

        var b = new IrBuilder();
        var ptrI8 = IrType.Pointer(IrType.I8);

        b.PositionAtEnd(entry);
        b.Br(header);

        b.PositionAtEnd(header);
        var src = b.Phi(ptrI8);
        var cnt = b.Phi(IrType.I8);
        var cond = b.Compare(IrCompareOp.Ne, cnt, I8(0));
        b.CondBr(cond, tail, exit);

        b.PositionAtEnd(tail);
        if (derefIsLoad)
        {
            var v = b.Load(src);
            b.Store(v, Ptr(fixedOtherAddr)); // literal-const address: whitelisted independent of src's pair
        }
        else
        {
            b.Store(I8(0xAA), src);
        }
        var srcNext = b.Gep(src, I8(1), IrType.I8);
        var cntNext = b.Sub(cnt, I8(1));
        b.Br(header);

        b.PositionAtEnd(exit);
        b.Ret();

        src.AddIncoming(Ptr(start), entry);
        src.AddIncoming(srcNext, tail);
        cnt.AddIncoming(I8(count), entry);
        cnt.AddIncoming(cntNext, tail);

        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );

        return (module, fn, src);
    }

    /// <summary>Hand-builds a MULTI-ENTRY copy loop: an <c>if</c>/<c>else</c> off a runtime-read
    /// selector byte picks between two DIFFERENT starting pointers (mirrors the shape
    /// <c>TryFindLoopShape</c> now admits — Mem.Copy's own block/remainder <c>if</c>/<c>else</c> before
    /// its shared inner <c>do</c>/<c>while</c>, see that region's header comment) before both arms fall
    /// into one shared pointer-walk loop with a single back-edge latch. Each arm's pointer init
    /// DIFFERS (buffer A vs buffer B) — Mem.Copy's own pointer candidates happen to carry the SAME init
    /// on both its arms (both just pass an outer value through unchanged), so a bug that synced every
    /// entry edge from only ONE arm's incoming value would still pass a Mem.Copy-shaped test but would
    /// silently read the wrong buffer here. <paramref name="selectorAddr"/> is read at runtime (a
    /// fixed-address load, not a compile-time constant) so the SAME compiled module can be run twice —
    /// once choosing each arm — to prove both entry syncs are wired to their own edge.</summary>
    private static (
        IrModule Module,
        IrFunction Fn,
        PhiInstruction Src,
        IrBasicBlock ArmA,
        IrBasicBlock ArmB
    ) BuildMultiEntryPointerWalkLoop(
        int selectorAddr,
        int startA,
        int startB,
        int count,
        int sumAddr
    )
    {
        var module = new IrModule("t");
        var fn = new IrFunction("main", IrType.Void, []);
        module.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var armA = fn.AppendBlock("armA");
        var armB = fn.AppendBlock("armB");
        var header = fn.AppendBlock("header");
        var tail = fn.AppendBlock("tail");
        var exit = fn.AppendBlock("exit");

        var b = new IrBuilder();
        var ptrI8 = IrType.Pointer(IrType.I8);

        b.PositionAtEnd(entry);
        var selector = b.Load(Ptr(selectorAddr));
        var isA = b.Compare(IrCompareOp.Ne, selector, I8(0));
        b.CondBr(isA, armA, armB);

        b.PositionAtEnd(armA);
        b.Br(header); // unconditional entry edge #1 - required by TryFindLoopShape

        b.PositionAtEnd(armB);
        b.Br(header); // unconditional entry edge #2

        b.PositionAtEnd(header);
        var src = b.Phi(ptrI8);
        var cnt = b.Phi(IrType.I8);
        var cond = b.Compare(IrCompareOp.Ne, cnt, I8(0));
        b.CondBr(cond, tail, exit);

        b.PositionAtEnd(tail);
        var v = b.Load(src); // the phi's one dereference - fuses to the resident pair's post-increment
        b.Store(v, Ptr(sumAddr));
        var srcNext = b.Gep(src, I8(1), IrType.I8);
        var cntNext = b.Sub(cnt, I8(1));
        b.Br(header); // the single back-edge latch

        b.PositionAtEnd(exit);
        b.Ret();

        src.AddIncoming(Ptr(startA), armA); // arm A's OWN init - must sync only on armA's edge
        src.AddIncoming(Ptr(startB), armB); // arm B's OWN init - must sync only on armB's edge
        src.AddIncoming(srcNext, tail);
        cnt.AddIncoming(I8(count), armA);
        cnt.AddIncoming(I8(count), armB);
        cnt.AddIncoming(cntNext, tail);

        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );

        return (module, fn, src, armA, armB);
    }

    /// <summary>Both arms qualify as entry edges (unconditional <c>br</c> to a shared header, one single
    /// back-edge latch), the phi still gets a register pair, its deref still fuses, and — the property
    /// unique to multi-entry — EACH arm gets its OWN <see cref="FunctionAllocation.LoopInductionSync"/>
    /// carrying THAT arm's own init value, not one shared sync reused for both.</summary>
    [Test]
    public async Task MultiEntry_BothArmsGetTheirOwnInitSync()
    {
        var (_, fn, src, armA, armB) = BuildMultiEntryPointerWalkLoop(
            selectorAddr: 0xC050,
            startA: 0xC100,
            startB: 0xC200,
            count: 4,
            sumAddr: 0xC300
        );

        var allocation = FunctionAllocation.For(
            fn,
            baseAddr: 0xC000,
            allowResidency: true,
            allowParamResidency: true
        );

        await Assert.That(allocation.Register.ContainsKey(src)).IsTrue();
        var reg = allocation.Register[src];
        await Assert.That(reg is Sm83Register.Hl or Sm83Register.De).IsTrue();

        // Each arm is its own entry-edge key, each holding a sync for the SAME register but a DIFFERENT
        // init value - not one sync shared/reused across both blocks.
        await Assert.That(allocation.LoopInductionPreheaderSync.ContainsKey(armA)).IsTrue();
        await Assert.That(allocation.LoopInductionPreheaderSync.ContainsKey(armB)).IsTrue();
        var syncA = allocation.LoopInductionPreheaderSync[armA].Single(s => s.Reg == reg);
        var syncB = allocation.LoopInductionPreheaderSync[armB].Single(s => s.Reg == reg);
        await Assert.That(((IrConstInt)syncA.Init).Value).IsEqualTo(0xC100);
        await Assert.That(((IrConstInt)syncB.Init).Value).IsEqualTo(0xC200);
    }

    /// <summary>Correctness end to end, on the emulator, for BOTH entry edges of the SAME compiled
    /// module: running with the selector picking arm A must walk buffer A (leaving its last byte at
    /// <c>sumAddr</c>); running it again from a fresh boot with the selector picking arm B must walk
    /// buffer B instead. A bug that wired every entry edge's sync to only one arm's init would pass one
    /// of these runs and silently corrupt the other by reading the wrong buffer.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MultiEntry_EachArmWalksItsOwnBuffer_RunsCorrectlyOnHardware(bool pickArmA)
    {
        const int selectorAddr = 0xC050;
        const int startA = 0xC100;
        const int startB = 0xC200;
        const int count = 4;
        const int sumAddr = 0xC300;
        var (module, _, _, _, _) = BuildMultiEntryPointerWalkLoop(
            selectorAddr,
            startA,
            startB,
            count,
            sumAddr
        );

        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("t", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        int pcStart = Sm83Backend.CodeBase;
        int length = model.Sections[0].Data.Length;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)pcStart;
        gb.DebugWriteByte(selectorAddr, (byte)(pickArmA ? 1 : 0));
        for (int i = 0; i < count; i++)
        {
            gb.DebugWriteByte((ushort)(startA + i), (byte)(0x10 + i)); // buffer A: 0x10..0x13
            gb.DebugWriteByte((ushort)(startB + i), (byte)(0x40 + i)); // buffer B: 0x40..0x43 (distinct)
        }

        for (int steps = 0; steps < 100_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < pcStart || pc >= pcStart + length)
                break;
            gb.StepInstruction();
        }

        // The loop walks the chosen arm's buffer for `count` bytes; the last iteration's byte should be
        // the final value left at sumAddr - proving THIS run's arm loaded its OWN init, not the other
        // arm's.
        byte expected = pickArmA ? (byte)(0x10 + count - 1) : (byte)(0x40 + count - 1);
        await Assert.That(gb.DebugReadByte(sumAddr)).IsEqualTo(expected);
    }

    [Test]
    public async Task LoadWalk_PhiGetsRegisterPair_AndDerefIsFused()
    {
        var (module, fn, src) = BuildSinglePointerWalkLoop(
            start: 0xC100,
            count: 8,
            derefIsLoad: true,
            fixedOtherAddr: 0xC300
        );

        var allocation = FunctionAllocation.For(
            fn,
            baseAddr: 0xC000,
            allowResidency: true,
            allowParamResidency: true
        );

        await Assert.That(allocation.Register.ContainsKey(src)).IsTrue();
        var reg = allocation.Register[src];
        await Assert.That(reg is Sm83Register.Hl or Sm83Register.De).IsTrue();
        // Load-fused: Hl is preferred (one opcode, `ld a,(hl+)`) over De's `ld a,(de)` + `inc de`.
        await Assert.That(reg).IsEqualTo(Sm83Register.Hl);

        // The one dereferencing Load is registered as a fused post-increment site on the same pair.
        var deref = fn
            .Blocks.SelectMany(bl => bl.Instructions)
            .OfType<LoadInstruction>()
            .Single(l => ReferenceEquals(l.Pointer, src));
        await Assert.That(allocation.FusedPointerSite.ContainsKey(deref)).IsTrue();
        await Assert.That(allocation.FusedPointerSite[deref]).IsEqualTo(reg);

        // The phi is dual-placed (still has a WRAM slot) but its step gep is register-only.
        await Assert.That(allocation.Slot.ContainsKey(src)).IsTrue();
        var step = fn
            .Blocks.SelectMany(bl => bl.Instructions)
            .OfType<GetElementPtrInstruction>()
            .Single(g => ReferenceEquals(g.BasePointer, src));
        await Assert.That(allocation.Slot.ContainsKey(step)).IsFalse();
        await Assert.That(allocation.Register[step]).IsEqualTo(reg);
    }

    [Test]
    public async Task StoreWalk_PhiGetsRegisterPair_PrefersHl()
    {
        var (_, fn, dst) = BuildSinglePointerWalkLoop(
            start: 0xC100,
            count: 20,
            derefIsLoad: false,
            fixedOtherAddr: 0
        );

        var allocation = FunctionAllocation.For(
            fn,
            baseAddr: 0xC000,
            allowResidency: true,
            allowParamResidency: true
        );

        await Assert.That(allocation.Register.ContainsKey(dst)).IsTrue();
        await Assert.That(allocation.Register[dst]).IsEqualTo(Sm83Register.Hl);
    }

    /// <summary>Correctness end to end: link and run the load-walk shape on the emulator, confirming the
    /// resident pair's fused post-increment dereference produces the same result a plain reload/step
    /// would — the mechanism is a perf overlay, never a semantics change.</summary>
    [Test]
    public async Task LoadWalk_RunsCorrectlyOnHardware()
    {
        const int start = 0xC100;
        const int count = 8;
        const int sumAddr = 0xC300;
        var (module, _, _) = BuildSinglePointerWalkLoop(start, count, derefIsLoad: true, sumAddr);

        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("t", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        int pcStart = Sm83Backend.CodeBase;
        int length = model.Sections[0].Data.Length;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)pcStart;
        for (int i = 0; i < count; i++)
            gb.DebugWriteByte((ushort)(start + i), (byte)(i + 1)); // distinct per-byte content

        for (int steps = 0; steps < 100_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < pcStart || pc >= pcStart + length)
                break;
            gb.StepInstruction();
        }

        // The loop walks `src` from 0xC100 for `count` bytes, storing each dereferenced byte to the fixed
        // `sumAddr` in turn (overwriting it each iteration) — the last iteration's byte (i = count-1)
        // should be the final value left there.
        await Assert.That(gb.DebugReadByte(sumAddr)).IsEqualTo((byte)count);
    }
}
