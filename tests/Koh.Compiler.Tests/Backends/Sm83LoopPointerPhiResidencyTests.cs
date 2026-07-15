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
