using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;

namespace Koh.Compiler.Tests.Ir;

/// <summary>
/// Unit tests on hand-built IR for <see cref="RedundantCompareEliminationPass"/>: a
/// <see cref="CompareInstruction"/> yields exactly 0 or 1, so an outer <c>icmp</c> that tests that
/// Boolean against the constant 0 or 1 is redundant — it is either the value itself (<c>ne …, 0</c> /
/// <c>eq …, 1</c>) or its logical negation (<c>eq …, 0</c> / <c>ne …, 1</c>, the same comparison with
/// its predicate flipped). This is the algebraic core of the fold that collapses the three-deep
/// <c>eq ; eq ; ne</c> chain a <c>while (p != end)</c> loop guard lowers to (Roslyn's <c>ceq;ldc.0;ceq</c>
/// for a pointer <c>!=</c> plus the frontend's <c>brtrue</c>/<c>brfalse</c> wrapper) down to one
/// comparison — the latency fix that keeps <c>MapWriter.FlushRun</c>'s vblank drip tight enough to
/// survive a layout shift (see <see cref="RedundantCompareEliminationPass"/>'s own remarks).
/// </summary>
public class RedundantCompareEliminationPassTests
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

    // ---- The double-negation chain a `!=` loop guard produces collapses to one comparison ----------

    [Test]
    public async Task DoubleNegationChain_CollapsesToSingleNegatedCompare()
    {
        // eq(x, y) ; eq(that, 0) ; ne(that, 0) — exactly `while (x != y)`'s lowered guard.
        var x = new IrParameter("x", IrType.I16);
        var y = new IrParameter("y", IrType.I16);
        var (module, fn, b) = NewFn(IrType.I8, x, y);
        var eq = b.Compare(IrCompareOp.Eq, x, y);
        var neg = b.Compare(IrCompareOp.Eq, eq, IrBuilder.ConstInt(IrType.I8, 0)); // !(x == y)
        var back = b.Compare(IrCompareOp.Ne, neg, IrBuilder.ConstInt(IrType.I8, 0)); // (x != y) != 0
        var ret = b.Ret(back);

        var changed = new RedundantCompareEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        // The return now feeds a single `ne(x, y)` — no comparison tests another comparison's result.
        await Assert.That(ret.Value).IsAssignableTo<CompareInstruction>();
        var final = (CompareInstruction)ret.Value!;
        await Assert.That(final.Op).IsEqualTo(IrCompareOp.Ne);
        await Assert.That(final.Left).IsSameReferenceAs((IrValue)x);
        await Assert.That(final.Right).IsSameReferenceAs((IrValue)y);
        await Assert
            .That(
                fn.Blocks.SelectMany(bl => bl.Instructions)
                    .OfType<CompareInstruction>()
                    .Any(c => c.Left is CompareInstruction || c.Right is CompareInstruction)
            )
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task NeAgainstZero_IsIdentity_ReplacedByInnerCompare()
    {
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I8, x, y);
        var inner = b.Compare(IrCompareOp.Ult, x, y);
        var outer = b.Compare(IrCompareOp.Ne, inner, IrBuilder.ConstInt(IrType.I8, 0));
        var ret = b.Ret(outer);

        var changed = new RedundantCompareEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)inner); // bool != 0  ==  bool
        await Assert.That(fn.EntryBlock!.Instructions.Contains(outer)).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task EqAgainstZero_IsNegation_ReplacedByFlippedPredicate()
    {
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I8, x, y);
        var inner = b.Compare(IrCompareOp.Ult, x, y); // x < y
        var outer = b.Compare(IrCompareOp.Eq, inner, IrBuilder.ConstInt(IrType.I8, 0)); // !(x < y)
        var ret = b.Ret(outer);

        var changed = new RedundantCompareEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(ret.Value).IsAssignableTo<CompareInstruction>();
        var final = (CompareInstruction)ret.Value!;
        await Assert.That(final.Op).IsEqualTo(IrCompareOp.Uge); // negation of Ult
        await Assert.That(final.Left).IsSameReferenceAs((IrValue)x);
        await Assert.That(final.Right).IsSameReferenceAs((IrValue)y);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task EqAgainstOne_IsIdentity_ReplacedByInnerCompare()
    {
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I8, x, y);
        var inner = b.Compare(IrCompareOp.Sgt, x, y);
        var outer = b.Compare(IrCompareOp.Eq, inner, IrBuilder.ConstInt(IrType.I8, 1)); // bool == 1
        var ret = b.Ret(outer);

        var changed = new RedundantCompareEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)inner);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task NonBooleanOperand_IsLeftAlone()
    {
        // The left operand is a plain value, not a comparison — its result is not provably 0/1, so this
        // is a real comparison the pass must not touch.
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b) = NewFn(IrType.I8, x);
        var cmp = b.Compare(IrCompareOp.Eq, x, IrBuilder.ConstInt(IrType.I8, 0));
        var ret = b.Ret(cmp);

        var changed = new RedundantCompareEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)cmp);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }
}
