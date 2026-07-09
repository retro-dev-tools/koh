using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;

namespace Koh.Compiler.Tests.Ir;

public class LoopInvariantCodeMotionTests
{
    /// <summary>Build a counted loop <c>for (i = 0; i &lt; bound; i++)</c> where <paramref name="bound"/>
    /// is produced inside the header by the given factory, and return the pieces a test inspects.
    /// The entry is a plain <c>br header</c>, so the pass reuses it as the preheader.</summary>
    private static (
        IrModule Module,
        IrFunction Fn,
        IrBasicBlock Entry,
        IrBasicBlock Header,
        IrBasicBlock Body
    ) CountedLoop(Func<IrBuilder, IrParameter, IrValue> bound)
    {
        var module = new IrModule("test");
        var n = new IrParameter("n", IrType.I16);
        var fn = new IrFunction("f", IrType.I16, [n]);
        module.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var header = fn.AppendBlock("header");
        var body = fn.AppendBlock("body");
        var exit = fn.AppendBlock("exit");

        var b = new IrBuilder();

        b.PositionAtEnd(entry);
        b.Br(header);

        b.PositionAtEnd(header);
        var i = b.Phi(IrType.I16);
        i.AddIncoming(IrBuilder.ConstInt(IrType.I16, 0), entry);
        var boundValue = bound(b, n);
        var cond = b.Compare(IrCompareOp.Slt, i, boundValue);
        b.CondBr(cond, body, exit);

        b.PositionAtEnd(body);
        var iNext = b.Add(i, IrBuilder.ConstInt(IrType.I16, 1));
        i.AddIncoming(iNext, body);
        b.Br(header);

        b.PositionAtEnd(exit);
        b.Ret(boundValue.Type.Kind == IrTypeKind.Int ? boundValue : i);

        return (module, fn, entry, header, body);
    }

    [Test]
    public async Task Licm_HoistsInvariantArithmeticIntoPreheader()
    {
        // bound = n + n — both operands are the parameter, so it is loop-invariant.
        var (module, fn, entry, header, _) = CountedLoop((b, n) => b.Add(n, n));
        var invariant = header.Instructions.OfType<BinaryInstruction>().First();

        var changed = new LoopInvariantCodeMotionPass().Run(fn);

        await Assert.That(changed).IsTrue();
        // The entry block was a plain `br header`, so it is reused as the preheader.
        await Assert.That(entry.Instructions).Contains(invariant);
        await Assert.That(header.Instructions).DoesNotContain(invariant);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Licm_DoesNotHoistLoopVariantComputation()
    {
        // The compare uses the induction phi `i`, which changes every iteration — not invariant.
        var (module, fn, _, header, _) = CountedLoop((b, n) => n);
        var compare = header.Instructions.OfType<CompareInstruction>().First();

        var changed = new LoopInvariantCodeMotionPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(header.Instructions).Contains(compare);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Licm_DoesNotHoistLoads()
    {
        // A load inside the loop reads memory that a store could change; it must stay in the loop even
        // though its pointer operand (a global address) is loop-invariant.
        var module = new IrModule("test");
        var fn = new IrFunction("f", IrType.Void, []);
        module.Functions.Add(fn);
        var g = new IrGlobal("counter", IrType.I8, Targets.AddressSpace.Wram);
        module.Globals.Add(g);

        var entry = fn.AppendBlock("entry");
        var header = fn.AppendBlock("header");
        var exit = fn.AppendBlock("exit");
        var b = new IrBuilder();

        b.PositionAtEnd(entry);
        b.Br(header);
        b.PositionAtEnd(header);
        var loaded = b.Load(IrBuilder.GlobalRef(g));
        var cond = b.Compare(IrCompareOp.Ne, loaded, IrBuilder.ConstInt(IrType.I8, 0));
        b.CondBr(cond, header, exit);
        b.PositionAtEnd(exit);
        b.Ret();

        var changed = new LoopInvariantCodeMotionPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(header.Instructions.Contains(loaded)).IsTrue();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Licm_SplicesPreheaderWhenEntryEdgeIsConditional()
    {
        // The header's only external predecessor branches conditionally, so no reusable preheader
        // exists; the pass must splice one onto the entry edge and hoist into it.
        var module = new IrModule("test");
        var flag = new IrParameter("flag", IrType.I8);
        var n = new IrParameter("n", IrType.I16);
        var fn = new IrFunction("f", IrType.I16, [flag, n]);
        module.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var header = fn.AppendBlock("header");
        var body = fn.AppendBlock("body");
        var exit = fn.AppendBlock("exit");
        var b = new IrBuilder();

        b.PositionAtEnd(entry);
        var take = b.Compare(IrCompareOp.Ne, flag, IrBuilder.ConstInt(IrType.I8, 0));
        b.CondBr(take, header, exit);

        b.PositionAtEnd(header);
        var i = b.Phi(IrType.I16);
        i.AddIncoming(IrBuilder.ConstInt(IrType.I16, 0), entry);
        var invariant = b.Add(n, n);
        var cond = b.Compare(IrCompareOp.Slt, i, invariant);
        b.CondBr(cond, body, exit);

        b.PositionAtEnd(body);
        var iNext = b.Add(i, IrBuilder.ConstInt(IrType.I16, 1));
        i.AddIncoming(iNext, body);
        b.Br(header);

        b.PositionAtEnd(exit);
        b.Ret(IrBuilder.ConstInt(IrType.I16, 0));

        var blockCountBefore = fn.Blocks.Count;
        var changed = new LoopInvariantCodeMotionPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(fn.Blocks.Count).IsEqualTo(blockCountBefore + 1); // a preheader was spliced in
        await Assert.That(header.Instructions).DoesNotContain(invariant);

        // The spliced preheader holds the invariant and branches to the header; the header's entry-side
        // phi incoming now arrives from it.
        var preheader = invariant.Parent!;
        await Assert.That(preheader.Terminator).IsTypeOf<BrInstruction>();
        await Assert.That(((BrInstruction)preheader.Terminator!).Target).IsEqualTo(header);
        await Assert.That(i.Incomings.Any(inc => ReferenceEquals(inc.Block, preheader))).IsTrue();
        await Assert.That(i.Incomings.Any(inc => ReferenceEquals(inc.Block, entry))).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Licm_HoistedResultIsReusedNotRecomputed()
    {
        // Running to a fixed point must converge: after hoisting, a second run finds nothing to move.
        var (_, fn, _, _, _) = CountedLoop((b, n) => b.Add(n, n));
        var pass = new LoopInvariantCodeMotionPass();

        await Assert.That(pass.Run(fn)).IsTrue();
        await Assert.That(pass.Run(fn)).IsFalse();
    }
}
