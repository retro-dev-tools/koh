using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Analysis;

namespace Koh.Compiler.Tests.Ir;

public class IrLivenessTests
{
    [Test]
    public async Task Liveness_ParameterIsLiveInAtEntry()
    {
        // f(n) { a = n + 1; b = a + n; ret b } — n is used, so it is live-in; a and b are block-local.
        var module = new IrModule("test");
        var n = new IrParameter("n", IrType.I16);
        var fn = new IrFunction("f", IrType.I16, [n]);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        var a = b.Add(n, IrBuilder.ConstInt(IrType.I16, 1));
        var sum = b.Add(a, n);
        b.Ret(sum);

        var live = IrLiveness.Compute(fn);

        await Assert.That(live.LiveIn(entry).Contains(n)).IsTrue();
        await Assert.That(live.LiveOut(entry)).IsEmpty(); // no successors, nothing escapes
    }

    [Test]
    public async Task Liveness_ValueLiveAcrossBranches()
    {
        // x is defined in the entry and used in both arms of a diamond, so it is live-out of the entry
        // and live-in to each arm.
        var module = new IrModule("test");
        var cond = new IrParameter("cond", IrType.I8);
        var n = new IrParameter("n", IrType.I16);
        var fn = new IrFunction("f", IrType.I16, [cond, n]);
        module.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var t = fn.AppendBlock("t");
        var f = fn.AppendBlock("f");
        var m = fn.AppendBlock("m");
        var b = new IrBuilder();

        b.PositionAtEnd(entry);
        var x = b.Add(n, IrBuilder.ConstInt(IrType.I16, 1));
        b.CondBr(cond, t, f);

        b.PositionAtEnd(t);
        var ta = b.Add(x, IrBuilder.ConstInt(IrType.I16, 1));
        b.Br(m);

        b.PositionAtEnd(f);
        var fa = b.Add(x, IrBuilder.ConstInt(IrType.I16, 2));
        b.Br(m);

        b.PositionAtEnd(m);
        b.Ret(IrBuilder.ConstInt(IrType.I16, 0));

        var live = IrLiveness.Compute(fn);

        await Assert.That(live.IsLiveOut(entry, x)).IsTrue();
        await Assert.That(live.LiveIn(t).Contains(x)).IsTrue();
        await Assert.That(live.LiveIn(f).Contains(x)).IsTrue();
        // x dies in each arm (nothing after uses it), so it is not live-out of the arms.
        await Assert.That(live.IsLiveOut(t, x)).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Liveness_LoopCarriedValuesLiveAroundBackEdge()
    {
        // for (i = 0; i < n; i++). The bound n is used every iteration (live around the loop); i2 = i+1
        // flows back through the header phi, so it is live-out of the body.
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
        var c = b.Compare(IrCompareOp.Slt, i, n);
        b.CondBr(c, body, exit);

        b.PositionAtEnd(body);
        var iNext = b.Add(i, IrBuilder.ConstInt(IrType.I16, 1));
        i.AddIncoming(iNext, body);
        b.Br(header);

        b.PositionAtEnd(exit);
        b.Ret(i);

        var live = IrLiveness.Compute(fn);

        // n is live around the entire loop.
        await Assert.That(live.IsLiveOut(entry, n)).IsTrue();
        await Assert.That(live.IsLiveOut(body, n)).IsTrue();
        await Assert.That(live.LiveIn(header).Contains(n)).IsTrue();
        // The induction variable and its next value are loop-carried.
        await Assert.That(live.IsLiveOut(header, i)).IsTrue(); // i used in body and exit
        await Assert.That(live.IsLiveOut(body, iNext)).IsTrue(); // i2 is the phi incoming on the back edge
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Liveness_ConstantsAreNotTracked()
    {
        await Assert.That(IrLiveness.IsTrackable(IrBuilder.ConstInt(IrType.I8, 7))).IsFalse();
        await Assert.That(IrLiveness.IsTrackable(new IrParameter("p", IrType.I8))).IsTrue();
    }
}
