using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;

namespace Koh.Compiler.Tests.Ir;

/// <summary>
/// Unit tests on hand-built IR for <see cref="RedundantConversionEliminationPass"/>: folds a
/// trunc-of-extend round-trip (<c>trunc(zext(x)) -&gt; x</c> / <c>trunc(sext(x)) -&gt; x</c>) back to
/// the original value whenever the trunc lands back on x's own width, and refuses to fold when the
/// widths don't actually round-trip or when the shape is the (unsound) reverse, zext(trunc(...)).
/// </summary>
public class RedundantConversionEliminationPassTests
{
    private static (IrModule Module, IrFunction Fn, IrBuilder B) NewFn(
        IrType returnType,
        params IrParameter[] parameters
    )
    {
        var module = new IrModule("test");
        var fn = new IrFunction("f", returnType, parameters);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        return (module, fn, b);
    }

    [Test]
    public async Task TruncOfZext_FoldsToTheOriginalValue()
    {
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I8, x);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var trunc = b.Conv(IrConvOp.Trunc, zx, IrType.I8);
        var ret = b.Ret(trunc);

        var changed = new RedundantConversionEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)x);
        // The pass only removes the trunc it folds away; the now-orphaned zext is left for DCE (as
        // part of the fixed-point pipeline) to remove, so it's still in the block on its own.
        await Assert.That(fn.EntryBlock!.Instructions.Contains(trunc)).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task TruncOfSext_FoldsToTheOriginalValue()
    {
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I8, x);
        var sx = b.Conv(IrConvOp.SExt, x, IrType.I32);
        var trunc = b.Conv(IrConvOp.Trunc, sx, IrType.I8);
        var ret = b.Ret(trunc);

        var changed = new RedundantConversionEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)x);
        await Assert.That(fn.EntryBlock!.Instructions.Contains(trunc)).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task TruncOfZext_FoldsAtI16TooNotJustI8()
    {
        var x = new IrParameter("x", IrType.I16);
        var (module, fn, b) = NewFn(IrType.I16, x);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var trunc = b.Conv(IrConvOp.Trunc, zx, IrType.I16);
        var ret = b.Ret(trunc);

        var changed = new RedundantConversionEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)x);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task DoesNotFold_WhenTruncWidthDiffersFromTheOriginalValueWidth()
    {
        // zext(i8 -> i32) then trunc to i16: the trunc does NOT land back on x's own width (8), so
        // this is not the identity round-trip — folding it to x would silently change the type/width
        // observed by consumers.
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I16, x);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var trunc = b.Conv(IrConvOp.Trunc, zx, IrType.I16);
        var ret = b.Ret(trunc);

        var changed = new RedundantConversionEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)trunc);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task DoesNotFold_ZextOfTrunc()
    {
        // The reverse shape is NOT an identity: trunc can discard live high bits, so widening it back
        // up does not recover the original value. Must be left alone.
        var x = new IrParameter("x", IrType.I32);
        var (module, fn, b) = NewFn(IrType.I32, x);
        var trunc = b.Conv(IrConvOp.Trunc, x, IrType.I8);
        var zext = b.Conv(IrConvOp.ZExt, trunc, IrType.I32);
        var ret = b.Ret(zext);

        var changed = new RedundantConversionEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)zext);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Optimize_WiresThePassIntoTheDefaultPipelineAfterNarrowPass()
    {
        // The production shape this pass exists for: NarrowPass narrows the add but leaves a
        // trunc(zext(x:i8):i32):i8 identity pair around the loop-phi-feeding back-edge value — this
        // pass must remove it as part of the fixed-point pipeline, leaving only the plain i8 add.
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I8, x);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var sum = b.Add(zx, IrBuilder.ConstInt(IrType.I32, 1));
        var trunc = b.Conv(IrConvOp.Trunc, sum, IrType.I8);
        var zx2 = b.Conv(IrConvOp.ZExt, trunc, IrType.I32);
        var trunc2 = b.Conv(IrConvOp.Trunc, zx2, IrType.I8);
        var ret = b.Ret(trunc2);

        IrOptimizer.Optimize(module);

        await Assert.That(ret.Value is BinaryInstruction { Type.Bits: 8 }).IsTrue();
        await Assert.That(fn.EntryBlock!.Instructions.OfType<ConvInstruction>()).IsEmpty();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }
}
