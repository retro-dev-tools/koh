using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;

namespace Koh.Compiler.Tests.Ir;

/// <summary>
/// Unit tests on hand-built IR for <see cref="NarrowPass"/> (spec §4 / task 3 of the CIL frontend
/// work: docs/superpowers/specs/2026-07-14-cil-frontend-design.md). Each test proves either that a
/// legal demotion fires (structurally: op width, and — for the compare-predicate remap — via a
/// value computed by chaining <see cref="ConstantFoldingPass"/> after demotion, so a wrong remap
/// would fold to the wrong boolean) or that an illegal one is correctly refused. See
/// <c>CilNarrowingEndToEndTests</c> for the assembly-driven end-to-end counterpart (real computed
/// values through the SM83 backend/emulator, plus the ROM-size/cycle-count measurement).
/// </summary>
public class NarrowPassTests
{
    private static (IrModule Module, IrFunction Fn, IrBuilder B, IrBasicBlock Entry) NewFn(
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
        return (module, fn, b, entry);
    }

    private static long ConstValueOf(IrValue? v) =>
        v is IrConstInt c
            ? c.Value
            : throw new Exception($"expected a constant, got {v?.GetType().Name}");

    private static BinaryInstruction OnlyBinary(IrFunction fn) =>
        fn.EntryBlock!.Instructions.OfType<BinaryInstruction>().Single();

    // ---- Arithmetic/bitwise: single op ----------------------------------------------------------

    [Test]
    public async Task Add_DemotesWhenOperandsAreExtensionsAndResultIsTruncated()
    {
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, y, IrType.I32);
        var sum = b.Add(zx, zy);
        var trunc = b.Conv(IrConvOp.Trunc, sum, IrType.I8);
        var ret = b.Ret(trunc);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var narrow = OnlyBinary(fn);
        await Assert.That(narrow.Type.Bits).IsEqualTo(8);
        await Assert.That(narrow.Left).IsSameReferenceAs((IrValue)x);
        await Assert.That(narrow.Right).IsSameReferenceAs((IrValue)y);
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)narrow); // trunc removed, ret rewired directly
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Add_DemotesToI16WhenOperandsAreExtensionsFromI16()
    {
        // The width-8 path above is exhaustively covered; this proves the pass is genuinely
        // width-generic (not width-8-specific) for `short c = (short)(a + b)`-style i16 arithmetic,
        // which is production-reachable through both frontends.
        var x = new IrParameter("x", IrType.I16);
        var y = new IrParameter("y", IrType.I16);
        var (module, fn, b, _) = NewFn(IrType.I16, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, y, IrType.I32);
        var sum = b.Add(zx, zy);
        var trunc = b.Conv(IrConvOp.Trunc, sum, IrType.I16);
        var ret = b.Ret(trunc);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var narrow = OnlyBinary(fn);
        await Assert.That(narrow.Type.Bits).IsEqualTo(16);
        await Assert.That(narrow.Left).IsSameReferenceAs((IrValue)x);
        await Assert.That(narrow.Right).IsSameReferenceAs((IrValue)y);
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)narrow);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Add_DemotesWithMixedZextAndSextOperands()
    {
        // Arithmetic bit-locality (see NarrowPass class remarks) doesn't require both operands to
        // share an extension kind — only compares do.
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var sy = b.Conv(IrConvOp.SExt, y, IrType.I32);
        var sum = b.Add(zx, sy);
        var trunc = b.Conv(IrConvOp.Trunc, sum, IrType.I8);
        b.Ret(trunc);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var narrow = OnlyBinary(fn);
        await Assert.That(narrow.Type.Bits).IsEqualTo(8);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Add_DemotesWithConstantOperand()
    {
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var sum = b.Add(zx, IrBuilder.ConstInt(IrType.I32, 300)); // 300 truncates to 44 at i8
        var trunc = b.Conv(IrConvOp.Trunc, sum, IrType.I8);
        b.Ret(trunc);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var narrow = OnlyBinary(fn);
        await Assert.That(narrow.Type.Bits).IsEqualTo(8);
        await Assert.That(ConstValueOf(narrow.Right)).IsEqualTo(44L);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Arithmetic/bitwise: chains --------------------------------------------------------------

    [Test]
    public async Task Chain_DemotesEveryLinkWhenOnlyTheOuterResultEscapesViaTrunc()
    {
        // (a+b)+c, only ever observed through a cast back to byte — mirrors real C# `(byte)(a+b+c)`.
        var a = new IrParameter("a", IrType.I8);
        var pB = new IrParameter("b", IrType.I8);
        var c = new IrParameter("c", IrType.I8);
        var (module, fn, bld, _) = NewFn(IrType.I8, a, pB, c);
        var za = bld.Conv(IrConvOp.ZExt, a, IrType.I32);
        var zb = bld.Conv(IrConvOp.ZExt, pB, IrType.I32);
        var zc = bld.Conv(IrConvOp.ZExt, c, IrType.I32);
        var inner = bld.Add(za, zb);
        var outer = bld.Add(inner, zc);
        var trunc = bld.Conv(IrConvOp.Trunc, outer, IrType.I8);
        var ret = bld.Ret(trunc);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var adds = fn.EntryBlock!.Instructions.OfType<BinaryInstruction>().ToList();
        await Assert.That(adds.Count).IsEqualTo(2); // both inner and outer survive, both narrowed
        await Assert.That(adds.All(add => add.Type.Bits == 8)).IsTrue();
        var narrowOuter = adds.Single(add => ReferenceEquals(add.Right, c));
        var narrowInner = (BinaryInstruction)narrowOuter.Left;
        await Assert.That(narrowInner.Left).IsSameReferenceAs((IrValue)a);
        await Assert.That(narrowInner.Right).IsSameReferenceAs((IrValue)pB);
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)narrowOuter);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Chain_DoesNotDemoteWhenAnInnerLinkAlsoEscapesAtWideWidth()
    {
        // Same shape as above, but the inner `a+b` is ALSO stored to an i32 slot directly — it must
        // survive at full width, so absorbing it into the narrow chain would either drop that use or
        // silently duplicate the computation. The whole chain is left untouched.
        var a = new IrParameter("a", IrType.I8);
        var pB = new IrParameter("b", IrType.I8);
        var c = new IrParameter("c", IrType.I8);
        var (module, fn, bld, _) = NewFn(IrType.Void, a, pB, c);
        var za = bld.Conv(IrConvOp.ZExt, a, IrType.I32);
        var zb = bld.Conv(IrConvOp.ZExt, pB, IrType.I32);
        var zc = bld.Conv(IrConvOp.ZExt, c, IrType.I32);
        var inner = bld.Add(za, zb);
        var outer = bld.Add(inner, zc);
        var trunc = bld.Conv(IrConvOp.Trunc, outer, IrType.I8);
        var wideSlot = bld.Alloca(IrType.I32);
        bld.Store(inner, wideSlot); // escaping full-width use of the inner add
        var narrowSlot = bld.Alloca(IrType.I8);
        bld.Store(trunc, narrowSlot);
        bld.Ret();

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsFalse();
        var adds = fn.EntryBlock!.Instructions.OfType<BinaryInstruction>().ToList();
        await Assert.That(adds.Count).IsEqualTo(2);
        await Assert.That(adds.All(add => add.Type.Bits == 32)).IsTrue();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Arithmetic/bitwise: refusals ------------------------------------------------------------

    [Test]
    public async Task Add_DoesNotDemoteWhenResultAlsoObservedAtWideWidth()
    {
        // The task's own example: `int wide = a + b; byte narrow = (byte)(a + b);` where a genuine
        // sum (200 + 100 = 300) does not fit a byte and IS observed as int elsewhere — a shared Add
        // feeding both a wide sink and a trunc-to-i8 must not be demoted.
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.Void, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, y, IrType.I32);
        var sum = b.Add(zx, zy); // the "genuinely overflows a byte" value (e.g. 200 + 100 = 300)
        var trunc = b.Conv(IrConvOp.Trunc, sum, IrType.I8);
        var wideSlot = b.Alloca(IrType.I32);
        b.Store(sum, wideSlot); // escapes at full width
        var narrowSlot = b.Alloca(IrType.I8);
        b.Store(trunc, narrowSlot);
        b.Ret();

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(OnlyBinary(fn).Type.Bits).IsEqualTo(32);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Add_DoesNotDemoteWhenTruncatedToTwoDifferentWidths()
    {
        // The same wide sum truncated to i8 in one place and i16 in another: neither width can claim
        // exclusive ownership of the node, so it stays at i32.
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.Void, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, y, IrType.I32);
        var sum = b.Add(zx, zy);
        var t8 = b.Conv(IrConvOp.Trunc, sum, IrType.I8);
        var t16 = b.Conv(IrConvOp.Trunc, sum, IrType.I16);
        var slot8 = b.Alloca(IrType.I8);
        var slot16 = b.Alloca(IrType.I16);
        b.Store(t8, slot8);
        b.Store(t16, slot16);
        b.Ret();

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(OnlyBinary(fn).Type.Bits).IsEqualTo(32);
    }

    [Test]
    public async Task Shl_IsNeverDemotedEvenWhenOperandsAndConsumerLookEligible()
    {
        // Shl is excluded on purpose (see class remarks): an out-of-range narrow shift is undefined
        // where the wide shift simply zeroes out, so demoting would be a miscompile, not a perf win.
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var shifted = b.Binary(IrBinaryOp.Shl, zx, IrBuilder.ConstInt(IrType.I32, 10));
        var trunc = b.Conv(IrConvOp.Trunc, shifted, IrType.I8);
        b.Ret(trunc);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(OnlyBinary(fn).Type.Bits).IsEqualTo(32);
    }

    [Test]
    public async Task UDiv_IsNeverDemotedEvenWhenOperandsAndConsumerLookEligible()
    {
        // Division isn't bit-local: the low bits of a quotient depend on the operands' high bits too.
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, y, IrType.I32);
        var div = b.Binary(IrBinaryOp.UDiv, zx, zy);
        var trunc = b.Conv(IrConvOp.Trunc, div, IrType.I8);
        b.Ret(trunc);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(OnlyBinary(fn).Type.Bits).IsEqualTo(32);
    }

    // ---- Compare: the predicate remap is the crux --------------------------------------------

    [Test]
    public async Task Compare_SltWithZextOperandsRemapsToUltAndFoldsToTheCorrectValue()
    {
        // The exact miscompile this pass must avoid: byte 200 vs byte 50. CIL's evaluation stack
        // always widens sub-int32 values to i32, so a source-level unsigned `a < b` surfaces as a
        // *signed* Slt over *zero-extended* operands. The wide comparison correctly says false (200
        // is not less than 50); naively re-running Slt at i8 would read 200 as -56 and say true.
        var (module, fn, b, _) = NewFn(IrType.I8);
        var zx = b.Conv(IrConvOp.ZExt, IrBuilder.ConstInt(IrType.I8, 200), IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, IrBuilder.ConstInt(IrType.I8, 50), IrType.I32);
        var cmp = b.Compare(IrCompareOp.Slt, zx, zy);
        var ret = b.Ret(cmp);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var demoted = fn.EntryBlock!.Instructions.OfType<CompareInstruction>().Single();
        await Assert.That(demoted.Op).IsEqualTo(IrCompareOp.Ult); // the remap
        await Assert.That(demoted.Left.Type.Bits).IsEqualTo(8);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // Fold the now-constant i8 operands to prove the remapped predicate computes the right value
        // (a naive "keep Slt" bug would fold this to 1, not 0).
        new ConstantFoldingPass().Run(fn);
        await Assert.That(ConstValueOf(ret.Value)).IsEqualTo(0L);
    }

    [Test]
    public async Task Compare_SltWithSextOperandsStaysSltAndFoldsToTheCorrectValue()
    {
        // Same bit patterns, sign-extended instead: -56 (0xC8) vs 50 signed IS -56 < 50 = true, and
        // sign extension preserves signed ordering directly, so the predicate must NOT remap here.
        var (module, fn, b, _) = NewFn(IrType.I8);
        var sx = b.Conv(IrConvOp.SExt, IrBuilder.ConstInt(IrType.I8, -56), IrType.I32);
        var sy = b.Conv(IrConvOp.SExt, IrBuilder.ConstInt(IrType.I8, 50), IrType.I32);
        var cmp = b.Compare(IrCompareOp.Slt, sx, sy);
        var ret = b.Ret(cmp);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var demoted = fn.EntryBlock!.Instructions.OfType<CompareInstruction>().Single();
        await Assert.That(demoted.Op).IsEqualTo(IrCompareOp.Slt); // unchanged
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        new ConstantFoldingPass().Run(fn);
        await Assert.That(ConstValueOf(ret.Value)).IsEqualTo(1L);
    }

    [Test]
    public async Task Compare_SgeWithZextI16OperandsRemapsToUgeAndFoldsToTheCorrectValue()
    {
        // The i8 remap test above is exhaustively covered; this proves the remap is genuinely
        // width-generic at i16 too (`ushort c = ...; if (c >= other) ...`), not an i8-only special
        // case. 40000 has bit 15 set — as a signed i16 it's negative (-25536), so a naive
        // "keep the signed predicate" bug reads it as smaller than 1000 and gets the wrong answer.
        var (module, fn, b, _) = NewFn(IrType.I8);
        var zx = b.Conv(IrConvOp.ZExt, IrBuilder.ConstInt(IrType.I16, 40000), IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, IrBuilder.ConstInt(IrType.I16, 1000), IrType.I32);
        var cmp = b.Compare(IrCompareOp.Sge, zx, zy); // wide: 40000 >= 1000 (both non-negative) = true
        var ret = b.Ret(cmp);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var demoted = fn.EntryBlock!.Instructions.OfType<CompareInstruction>().Single();
        await Assert.That(demoted.Op).IsEqualTo(IrCompareOp.Uge); // the remap, at width 16
        await Assert.That(demoted.Left.Type.Bits).IsEqualTo(16);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        new ConstantFoldingPass().Run(fn);
        await Assert.That(ConstValueOf(ret.Value)).IsEqualTo(1L); // a naive "keep Sge" bug folds this to 0
    }

    [Test]
    public async Task Compare_UnsignedPredicateStaysUnchangedForEitherExtensionKind()
    {
        var (module, fn, b, _) = NewFn(IrType.I8);
        var zx = b.Conv(IrConvOp.ZExt, IrBuilder.ConstInt(IrType.I8, 200), IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, IrBuilder.ConstInt(IrType.I8, 50), IrType.I32);
        var cmp = b.Compare(IrCompareOp.Ult, zx, zy);
        var ret = b.Ret(cmp);

        new NarrowPass().Run(fn);
        var demoted = fn.EntryBlock!.Instructions.OfType<CompareInstruction>().Single();
        await Assert.That(demoted.Op).IsEqualTo(IrCompareOp.Ult);

        new ConstantFoldingPass().Run(fn);
        await Assert.That(ConstValueOf(ret.Value)).IsEqualTo(0L); // 200 is not < 50, unsigned
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Compare_EqDemotesForMatchingExtensionKind()
    {
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, y, IrType.I32);
        var cmp = b.Compare(IrCompareOp.Eq, zx, zy);
        b.Ret(cmp);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var demoted = fn.EntryBlock!.Instructions.OfType<CompareInstruction>().Single();
        await Assert.That(demoted.Op).IsEqualTo(IrCompareOp.Eq);
        await Assert.That(demoted.Left).IsSameReferenceAs((IrValue)x);
        await Assert.That(demoted.Right).IsSameReferenceAs((IrValue)y);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Compare_DoesNotDemoteMixedExtensionKinds()
    {
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var sy = b.Conv(IrConvOp.SExt, y, IrType.I32);
        var cmp = b.Compare(IrCompareOp.Eq, zx, sy);
        b.Ret(cmp);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert
            .That(fn.EntryBlock!.Instructions.OfType<CompareInstruction>().Single().Left.Type.Bits)
            .IsEqualTo(32);
    }

    [Test]
    public async Task Compare_DoesNotDemoteWhenConstantDoesNotFitTheNarrowRange()
    {
        // zext(byte) compared against 300: 300 can never be a byte's value, so truncating it would
        // change what's being asked.
        var x = new IrParameter("x", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var cmp = b.Compare(IrCompareOp.Ult, zx, IrBuilder.ConstInt(IrType.I32, 300));
        b.Ret(cmp);

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert
            .That(fn.EntryBlock!.Instructions.OfType<CompareInstruction>().Single().Left.Type.Bits)
            .IsEqualTo(32);
    }

    // ---- Full pipeline sanity ----------------------------------------------------------------

    [Test]
    public async Task NarrowPass_NarrowsAByteLoopCounterCompareAndIncrement()
    {
        // Mirrors the frontend's lowering of `for (byte i = 0; i < 10; i++)`: after Mem2RegPass
        // promotes the counter to a phi, its compare and increment both go through the classic
        // zext/trunc CIL stack-widening pattern. Runs NarrowPass alone (not the full IrOptimizer
        // pipeline) so the assertions below are about this pass specifically, not an interaction
        // with every other pass in the fixed-point loop.
        var module = new IrModule("test");
        var fn = new IrFunction("f", IrType.Void, []);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var loop = fn.AppendBlock("loop");
        var exit = fn.AppendBlock("exit");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        b.Br(loop);

        b.PositionAtEnd(loop);
        var i = b.Phi(IrType.I8);
        i.AddIncoming(IrBuilder.ConstInt(IrType.I8, 0), entry);
        var zi = b.Conv(IrConvOp.ZExt, i, IrType.I32);
        var cond = b.Compare(IrCompareOp.Slt, zi, IrBuilder.ConstInt(IrType.I32, 10));
        var thenB = fn.AppendBlock("inc");
        b.CondBr(cond, thenB, exit);

        b.PositionAtEnd(thenB);
        var zi2 = b.Conv(IrConvOp.ZExt, i, IrType.I32);
        var next32 = b.Add(zi2, IrBuilder.ConstInt(IrType.I32, 1));
        var next8 = b.Conv(IrConvOp.Trunc, next32, IrType.I8);
        i.AddIncoming(next8, thenB);
        b.Br(loop);

        b.PositionAtEnd(exit);
        b.Ret();

        var changed = new NarrowPass().Run(fn);

        await Assert.That(changed).IsTrue();
        var allInstrs = fn.Blocks.SelectMany(bl => bl.Instructions).ToList();
        await Assert
            .That(allInstrs.OfType<BinaryInstruction>().Any(inst => inst.Type.Bits == 32))
            .IsFalse();
        await Assert
            .That(allInstrs.OfType<CompareInstruction>().Any(inst => inst.Left.Type.Bits == 32))
            .IsFalse();
        var demotedCompare = allInstrs.OfType<CompareInstruction>().Single();
        await Assert.That(demotedCompare.Op).IsEqualTo(IrCompareOp.Ult); // Slt-over-zext remap fired here too
        var demotedAdd = allInstrs.OfType<BinaryInstruction>().Single();
        await Assert.That(demotedAdd.Type.Bits).IsEqualTo(8);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Optimize_WiresNarrowPassIntoTheDefaultPipeline()
    {
        // Confirms NarrowPass actually runs as part of IrOptimizer.Optimize (not just standalone).
        var x = new IrParameter("x", IrType.I8);
        var y = new IrParameter("y", IrType.I8);
        var (module, fn, b, _) = NewFn(IrType.I8, x, y);
        var zx = b.Conv(IrConvOp.ZExt, x, IrType.I32);
        var zy = b.Conv(IrConvOp.ZExt, y, IrType.I32);
        var sum = b.Add(zx, zy);
        var trunc = b.Conv(IrConvOp.Trunc, sum, IrType.I8);
        b.Ret(trunc);

        IrOptimizer.Optimize(module);

        var narrow = OnlyBinary(fn);
        await Assert.That(narrow.Type.Bits).IsEqualTo(8);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }
}
