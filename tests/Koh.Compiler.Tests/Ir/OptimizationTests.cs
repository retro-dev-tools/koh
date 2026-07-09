using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;

namespace Koh.Compiler.Tests.Ir;

public class OptimizationTests
{
    /// <summary>A one-block function with a builder positioned at its entry, for concise test setup.</summary>
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

    // ---- Constant folding ----------------------------------------------------

    [Test]
    public async Task ConstantFolding_FoldsIntegerArithmetic()
    {
        var (module, fn, b, _) = NewFn(IrType.I16);
        var sum = b.Add(IrBuilder.ConstInt(IrType.I16, 20), IrBuilder.ConstInt(IrType.I16, 22));
        var ret = b.Ret(sum);

        new ConstantFoldingPass().Run(fn);

        await Assert.That(ConstValueOf(ret.Value)).IsEqualTo(42L);
        await Assert.That(fn.EntryBlock!.Instructions.Count).IsEqualTo(1); // just the ret
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task ConstantFolding_WrapsToOperandWidth()
    {
        var (_, fn, b, _) = NewFn(IrType.I8);
        var sum = b.Add(IrBuilder.ConstInt(IrType.I8, 200), IrBuilder.ConstInt(IrType.I8, 100));
        var ret = b.Ret(sum);

        new ConstantFoldingPass().Run(fn);

        // 300 mod 256 = 44 (fits an i8 without further interpretation).
        await Assert.That(ConstValueOf(ret.Value)).IsEqualTo(44L);
    }

    [Test]
    public async Task ConstantFolding_SignedAndUnsignedDivideDiffer()
    {
        // Bit pattern 0xF8 is -8 signed, 248 unsigned, over i8.
        var (_, sfn, sb, _) = NewFn(IrType.I8);
        var sret = sb.Ret(
            sb.Binary(
                IrBinaryOp.SDiv,
                IrBuilder.ConstInt(IrType.I8, -8),
                IrBuilder.ConstInt(IrType.I8, 2)
            )
        );

        var (_, ufn, ub, _) = NewFn(IrType.I8);
        var uret = ub.Ret(
            ub.Binary(
                IrBinaryOp.UDiv,
                IrBuilder.ConstInt(IrType.I8, -8),
                IrBuilder.ConstInt(IrType.I8, 2)
            )
        );

        new ConstantFoldingPass().Run(sfn);
        new ConstantFoldingPass().Run(ufn);

        await Assert.That(ConstValueOf(sret.Value)).IsEqualTo(-4L); // -8 / 2
        await Assert.That(ConstValueOf(uret.Value)).IsEqualTo(124L); // 248 / 2
    }

    [Test]
    public async Task ConstantFolding_DoesNotFoldDivideByZero()
    {
        var (_, fn, b, _) = NewFn(IrType.I8);
        var div = b.Binary(
            IrBinaryOp.UDiv,
            IrBuilder.ConstInt(IrType.I8, 10),
            IrBuilder.ConstInt(IrType.I8, 0)
        );
        b.Ret(div);

        var changed = new ConstantFoldingPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(fn.EntryBlock!.Instructions[0]).IsSameReferenceAs(div);
    }

    [Test]
    public async Task ConstantFolding_FoldsComparisons()
    {
        // slt: -1 < 0 is true; ult: 255 < 0 is false — same bit patterns, opposite results.
        var (_, sfn, sb, _) = NewFn(IrType.I8);
        var sret = sb.Ret(
            sb.Conv(
                IrConvOp.Trunc,
                sb.Compare(
                    IrCompareOp.Slt,
                    IrBuilder.ConstInt(IrType.I8, -1),
                    IrBuilder.ConstInt(IrType.I8, 0)
                ),
                IrType.I8
            )
        );

        var (_, ufn, ub, _) = NewFn(IrType.I8);
        var uret = ub.Ret(
            ub.Conv(
                IrConvOp.Trunc,
                ub.Compare(
                    IrCompareOp.Ult,
                    IrBuilder.ConstInt(IrType.I8, -1),
                    IrBuilder.ConstInt(IrType.I8, 0)
                ),
                IrType.I8
            )
        );

        new ConstantFoldingPass().Run(sfn);
        new ConstantFoldingPass().Run(ufn);

        await Assert.That(ConstValueOf(sret.Value)).IsEqualTo(1L);
        await Assert.That(ConstValueOf(uret.Value)).IsEqualTo(0L);
    }

    [Test]
    public async Task ConstantFolding_FoldsConversions()
    {
        var (_, fn, b, _) = NewFn(IrType.Void);
        var trunc = b.Conv(IrConvOp.Trunc, IrBuilder.ConstInt(IrType.I16, 0x1234), IrType.I8);
        var zext = b.Conv(IrConvOp.ZExt, IrBuilder.ConstInt(IrType.I8, -56), IrType.I16); // 200 unsigned
        var sext = b.Conv(IrConvOp.SExt, IrBuilder.ConstInt(IrType.I8, -56), IrType.I16);
        // Keep the results live so DCE-independence is clear; store into allocas.
        var pa = b.Alloca(IrType.I8);
        var pb = b.Alloca(IrType.I16);
        var pc = b.Alloca(IrType.I16);
        b.Store(trunc, pa);
        b.Store(zext, pb);
        b.Store(sext, pc);
        b.Ret();

        new ConstantFoldingPass().Run(fn);

        var stores = fn.EntryBlock!.Instructions.OfType<StoreInstruction>().ToList();
        await Assert.That(ConstValueOf(stores[0].Value)).IsEqualTo(0x34L); // low byte
        await Assert.That(ConstValueOf(stores[1].Value)).IsEqualTo(200L); // zero-extended
        await Assert.That(ConstValueOf(stores[2].Value)).IsEqualTo(-56L); // sign-extended
    }

    [Test]
    public async Task ConstantFolding_AppliesIdentities()
    {
        var (module, fn, b, _) = NewFn(IrType.Void, new IrParameter("x", IrType.I8));
        var x = fn.Parameters[0];
        var allOnes = IrBuilder.ConstInt(IrType.I8, -1); // 0xFF
        var zero = IrBuilder.ConstInt(IrType.I8, 0);
        var one = IrBuilder.ConstInt(IrType.I8, 1);

        var addId = b.Add(x, zero); // -> x
        var mulZero = b.Mul(x, zero); // -> 0
        var andOnes = b.Binary(IrBinaryOp.And, x, allOnes); // -> x
        var xorSelf = b.Binary(IrBinaryOp.Xor, x, x); // -> 0
        var shlZero = b.Binary(IrBinaryOp.Shl, x, zero); // -> x
        var divOne = b.Binary(IrBinaryOp.UDiv, x, one); // -> x

        var p = b.Alloca(IrType.I8);
        b.Store(addId, p);
        b.Store(mulZero, p);
        b.Store(andOnes, p);
        b.Store(xorSelf, p);
        b.Store(shlZero, p);
        b.Store(divOne, p);
        b.Ret();

        new ConstantFoldingPass().Run(fn);

        var stores = fn.EntryBlock!.Instructions.OfType<StoreInstruction>().ToList();
        await Assert.That(stores[0].Value).IsSameReferenceAs((IrValue)x); // add x,0
        await Assert.That(ConstValueOf(stores[1].Value)).IsEqualTo(0L); // mul x,0
        await Assert.That(stores[2].Value).IsSameReferenceAs((IrValue)x); // and x,-1
        await Assert.That(ConstValueOf(stores[3].Value)).IsEqualTo(0L); // xor x,x
        await Assert.That(stores[4].Value).IsSameReferenceAs((IrValue)x); // shl x,0
        await Assert.That(stores[5].Value).IsSameReferenceAs((IrValue)x); // udiv x,1
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Dead code elimination ----------------------------------------------

    [Test]
    public async Task DeadCodeElimination_RemovesUnusedChain()
    {
        var (_, fn, b, _) = NewFn(IrType.I8, new IrParameter("x", IrType.I8));
        var x = fn.Parameters[0];
        var a = b.Add(x, IrBuilder.ConstInt(IrType.I8, 1)); // used only by b (which is dead)
        var _dead = b.Add(a, IrBuilder.ConstInt(IrType.I8, 1)); // unused
        b.Ret(x);

        var changed = new DeadCodeEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        // Both adds are gone; only the ret survives.
        await Assert.That(fn.EntryBlock!.Instructions.Count).IsEqualTo(1);
        await Assert.That(fn.EntryBlock!.Instructions[0]).IsTypeOf<RetInstruction>();
    }

    [Test]
    public async Task DeadCodeElimination_KeepsUnusedLoad()
    {
        var (_, fn, b, _) = NewFn(IrType.Void);
        var p = b.Alloca(IrType.I8);
        b.Load(p); // unused, but a load may be volatile MMIO
        b.Ret();

        new DeadCodeEliminationPass().Run(fn);

        await Assert.That(fn.EntryBlock!.Instructions.OfType<LoadInstruction>().Any()).IsTrue();
    }

    [Test]
    public async Task DeadCodeElimination_KeepsStoresAndCalls()
    {
        var module = new IrModule("test");
        var callee = new IrFunction("g", IrType.Void, []);
        var fn = new IrFunction("f", IrType.Void, []);
        module.Functions.Add(callee);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        var p = b.Alloca(IrType.I8);
        b.Store(IrBuilder.ConstInt(IrType.I8, 0), p);
        b.Call(callee, []);
        b.Ret();

        new DeadCodeEliminationPass().Run(fn);

        await Assert.That(entry.Instructions.OfType<StoreInstruction>().Any()).IsTrue();
        await Assert.That(entry.Instructions.OfType<CallInstruction>().Any()).IsTrue();
    }

    // ---- Full pipeline -------------------------------------------------------

    [Test]
    public async Task Optimize_FoldsAndEliminatesCascade()
    {
        var (module, fn, b, _) = NewFn(IrType.I16);
        var a = b.Add(IrBuilder.ConstInt(IrType.I16, 1), IrBuilder.ConstInt(IrType.I16, 2)); // 3
        var c = b.Mul(a, IrBuilder.ConstInt(IrType.I16, 10)); // 30
        var ret = b.Ret(c);

        IrOptimizer.Optimize(module);

        await Assert.That(ConstValueOf(ret.Value)).IsEqualTo(30L);
        await Assert.That(fn.EntryBlock!.Instructions.Count).IsEqualTo(1); // add + mul folded and removed
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Optimize_RewritesPhiIncomingThroughRauw()
    {
        // entry -> loop; loop has a phi whose "entry" incoming is a foldable constant expression.
        var module = new IrModule("test");
        var fn = new IrFunction("f", IrType.I8, []);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var loop = fn.AppendBlock("loop");

        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        var seed = b.Add(IrBuilder.ConstInt(IrType.I8, 2), IrBuilder.ConstInt(IrType.I8, 3)); // -> 5
        b.Br(loop);

        b.PositionAtEnd(loop);
        var phi = b.Phi(IrType.I8);
        phi.AddIncoming(seed, entry);
        phi.AddIncoming(phi, loop); // trivial self-cycle keeps the block valid
        b.CondBr(phi, loop, loop);

        IrOptimizer.Optimize(module);

        // The folded constant 5 replaced the phi's "seed" incoming.
        var incoming = phi.Incomings.First(i => i.Block == entry).Value;
        await Assert.That(ConstValueOf(incoming)).IsEqualTo(5L);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Optimize_LeavesNonConstantCodeIntact()
    {
        var (module, fn, b, _) = NewFn(
            IrType.I8,
            new IrParameter("x", IrType.I8),
            new IrParameter("y", IrType.I8)
        );
        var sum = b.Add(fn.Parameters[0], fn.Parameters[1]);
        b.Ret(sum);

        IrOptimizer.Optimize(module);

        await Assert.That(fn.EntryBlock!.Instructions.OfType<BinaryInstruction>().Any()).IsTrue();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Redundant load elimination -----------------------------------------

    [Test]
    public async Task RedundantLoadElimination_ForwardsStoredValueToLoad()
    {
        var (module, fn, b, _) = NewFn(IrType.I8, new IrParameter("n", IrType.I8));
        var n = fn.Parameters[0];
        var p = b.Alloca(IrType.I8);
        b.Store(n, p);
        var loaded = b.Load(p);
        var ret = b.Ret(loaded);

        var changed = new RedundantLoadEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)n); // load forwarded to the stored param
        await Assert.That(fn.EntryBlock!.Instructions.OfType<LoadInstruction>().Any()).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task RedundantLoadElimination_CollapsesRepeatedLoads()
    {
        // Two loads of the same unmodified alloca in one block: the second forwards to the first.
        var (module, fn, b, _) = NewFn(IrType.I8);
        var p = b.Alloca(IrType.I8);
        var first = b.Load(p);
        var second = b.Load(p);
        var sum = b.Add(first, second);
        b.Ret(sum);

        new RedundantLoadEliminationPass().Run(fn);

        // Only one load remains, and the add uses it for both operands.
        await Assert
            .That(fn.EntryBlock!.Instructions.OfType<LoadInstruction>().Count())
            .IsEqualTo(1);
        await Assert.That(sum.Left).IsSameReferenceAs((IrValue)first);
        await Assert.That(sum.Right).IsSameReferenceAs((IrValue)first);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task RedundantLoadElimination_DoesNotForwardThroughEscapingAlloca()
    {
        // An array alloca is addressed via gep; the load/store go through the gep pointer, not the
        // alloca, so nothing is forwarded (and the alloca escapes anyway).
        var (module, fn, b, _) = NewFn(IrType.I8);
        var arr = b.Alloca(IrType.Array(IrType.I8, 2));
        var elem = b.Gep(arr, IrBuilder.ConstInt(IrType.I8, 0), IrType.I8);
        b.Store(IrBuilder.ConstInt(IrType.I8, 9), elem);
        var loaded = b.Load(elem);
        b.Ret(loaded);

        var changed = new RedundantLoadEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(fn.EntryBlock!.Instructions.OfType<LoadInstruction>().Any()).IsTrue();
    }

    // ---- Dead store elimination ---------------------------------------------

    [Test]
    public async Task DeadStoreElimination_RemovesStoresToWriteOnlyAlloca()
    {
        var (_, fn, b, _) = NewFn(IrType.Void);
        var p = b.Alloca(IrType.I8);
        b.Store(IrBuilder.ConstInt(IrType.I8, 5), p); // never loaded anywhere
        b.Ret();

        var changed = new DeadStoreEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(fn.EntryBlock!.Instructions.OfType<StoreInstruction>().Any()).IsFalse();
    }

    [Test]
    public async Task DeadStoreElimination_KeepsStoresToLoadedAlloca()
    {
        var (_, fn, b, _) = NewFn(IrType.I8);
        var p = b.Alloca(IrType.I8);
        b.Store(IrBuilder.ConstInt(IrType.I8, 5), p);
        b.Ret(b.Load(p)); // the alloca is read, so the store is live

        var changed = new DeadStoreEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(fn.EntryBlock!.Instructions.OfType<StoreInstruction>().Any()).IsTrue();
    }

    // ---- CFG simplification --------------------------------------------------

    [Test]
    public async Task SimplifyCfg_FoldsConstantBranchAndDropsUnreachableBlock()
    {
        var module = new IrModule("test");
        var fn = new IrFunction("f", IrType.Void, []);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var taken = fn.AppendBlock("taken");
        var gone = fn.AppendBlock("gone");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        b.CondBr(IrBuilder.ConstInt(IrType.I8, 1), taken, gone);
        b.PositionAtEnd(taken);
        b.Ret();
        b.PositionAtEnd(gone);
        b.Ret();

        var changed = new SimplifyCfgPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(entry.Terminator).IsTypeOf<BrInstruction>();
        await Assert.That(((BrInstruction)entry.Terminator!).Target).IsSameReferenceAs(taken);
        await Assert.That(fn.Blocks.Contains(gone)).IsFalse(); // unreachable, removed
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task SimplifyCfg_PrunesPhiIncomingFromDeadEdge()
    {
        // A diamond whose condition is constant: the not-taken side becomes unreachable and the merge
        // block's phi drops that incoming, leaving exactly the taken value.
        var module = new IrModule("test");
        var fn = new IrFunction("f", IrType.I8, []);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var t = fn.AppendBlock("t");
        var f = fn.AppendBlock("f");
        var m = fn.AppendBlock("m");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        b.CondBr(IrBuilder.ConstInt(IrType.I8, 1), t, f);
        b.PositionAtEnd(t);
        b.Br(m);
        b.PositionAtEnd(f);
        b.Br(m);
        b.PositionAtEnd(m);
        var phi = b.Phi(IrType.I8);
        phi.AddIncoming(IrBuilder.ConstInt(IrType.I8, 7), t);
        phi.AddIncoming(IrBuilder.ConstInt(IrType.I8, 9), f);
        b.Ret(phi);

        new SimplifyCfgPass().Run(fn);

        await Assert.That(phi.Incomings.Count).IsEqualTo(1);
        await Assert.That(phi.Incomings[0].Block).IsSameReferenceAs(t);
        await Assert.That(ConstValueOf(phi.Incomings[0].Value)).IsEqualTo(7L);
        await Assert.That(fn.Blocks.Contains(f)).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Full pipeline: scalar-local promotion -------------------------------

    [Test]
    public async Task Optimize_PromotesScalarLocalToDirectDataflow()
    {
        // Mirrors the frontend's lowering of `byte f(byte n) { byte x = n; return x + x; }`:
        // an alloca with a store and two loads. The optimizer should forward the stores, delete the
        // loads, drop the now-dead store and alloca, and leave just `add n, n; ret`.
        var (module, fn, b, _) = NewFn(IrType.I8, new IrParameter("n", IrType.I8));
        var n = fn.Parameters[0];
        var x = b.Alloca(IrType.I8);
        b.Store(n, x);
        var add = b.Add(b.Load(x), b.Load(x));
        b.Ret(add);

        IrOptimizer.Optimize(module);

        var instrs = fn.EntryBlock!.Instructions;
        await Assert.That(instrs.OfType<AllocaInstruction>().Any()).IsFalse();
        await Assert.That(instrs.OfType<LoadInstruction>().Any()).IsFalse();
        await Assert.That(instrs.OfType<StoreInstruction>().Any()).IsFalse();
        await Assert.That(instrs.Count).IsEqualTo(2); // add + ret
        var survivingAdd = instrs.OfType<BinaryInstruction>().Single();
        await Assert.That(survivingAdd.Left).IsSameReferenceAs((IrValue)n);
        await Assert.That(survivingAdd.Right).IsSameReferenceAs((IrValue)n);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Strength reduction --------------------------------------------------

    private static BinaryInstruction OnlyBinary(IrFunction fn) =>
        fn.EntryBlock!.Instructions.OfType<BinaryInstruction>().Single();

    [Test]
    public async Task StrengthReduction_MultiplyByPowerOfTwoBecomesShift()
    {
        var (module, fn, b, _) = NewFn(IrType.I16, new IrParameter("x", IrType.I16));
        var x = fn.Parameters[0];
        var ret = b.Ret(b.Mul(x, IrBuilder.ConstInt(IrType.I16, 8)));

        var changed = new StrengthReductionPass().Run(fn);

        var shift = OnlyBinary(fn);
        await Assert.That(changed).IsTrue();
        await Assert.That(shift.Op).IsEqualTo(IrBinaryOp.Shl);
        await Assert.That(shift.Left).IsSameReferenceAs((IrValue)x);
        await Assert.That(ConstValueOf(shift.Right)).IsEqualTo(3L); // log2(8)
        await Assert.That(ret.Value).IsSameReferenceAs((IrValue)shift);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task StrengthReduction_UnsignedDivideByPowerOfTwoBecomesShift()
    {
        var (module, fn, b, _) = NewFn(IrType.I16, new IrParameter("x", IrType.I16));
        var x = fn.Parameters[0];
        b.Ret(b.Binary(IrBinaryOp.UDiv, x, IrBuilder.ConstInt(IrType.I16, 4)));

        new StrengthReductionPass().Run(fn);

        var shift = OnlyBinary(fn);
        await Assert.That(shift.Op).IsEqualTo(IrBinaryOp.LShr);
        await Assert.That(ConstValueOf(shift.Right)).IsEqualTo(2L);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task StrengthReduction_UnsignedRemainderByPowerOfTwoBecomesMask()
    {
        var (module, fn, b, _) = NewFn(IrType.I8, new IrParameter("x", IrType.I8));
        var x = fn.Parameters[0];
        b.Ret(b.Binary(IrBinaryOp.URem, x, IrBuilder.ConstInt(IrType.I8, 8)));

        new StrengthReductionPass().Run(fn);

        var mask = OnlyBinary(fn);
        await Assert.That(mask.Op).IsEqualTo(IrBinaryOp.And);
        await Assert.That(ConstValueOf(mask.Right)).IsEqualTo(7L); // 8 - 1
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task StrengthReduction_LeavesSignedDivideAndNonPowersAlone()
    {
        var (_, sfn, sb, _) = NewFn(IrType.I8, new IrParameter("x", IrType.I8));
        sb.Ret(sb.Binary(IrBinaryOp.SDiv, sfn.Parameters[0], IrBuilder.ConstInt(IrType.I8, 4)));

        var (_, mfn, mb, _) = NewFn(IrType.I8, new IrParameter("x", IrType.I8));
        mb.Ret(mb.Mul(mfn.Parameters[0], IrBuilder.ConstInt(IrType.I8, 3)));

        await Assert.That(new StrengthReductionPass().Run(sfn)).IsFalse();
        await Assert.That(new StrengthReductionPass().Run(mfn)).IsFalse();
        await Assert.That(OnlyBinary(sfn).Op).IsEqualTo(IrBinaryOp.SDiv);
        await Assert.That(OnlyBinary(mfn).Op).IsEqualTo(IrBinaryOp.Mul);
    }

    // ---- Local CSE -----------------------------------------------------------

    [Test]
    public async Task LocalCse_DeduplicatesIdenticalExpressions()
    {
        var (module, fn, b, _) = NewFn(
            IrType.I8,
            new IrParameter("x", IrType.I8),
            new IrParameter("y", IrType.I8)
        );
        var (x, y) = (fn.Parameters[0], fn.Parameters[1]);
        var a = b.Add(x, y);
        var dup = b.Add(x, y);
        var outer = b.Add(a, dup);
        b.Ret(outer);

        var changed = new LocalCsePass().Run(fn);

        await Assert.That(changed).IsTrue();
        // The duplicate `add x, y` is gone; the outer add now uses `a` for both operands.
        await Assert
            .That(fn.EntryBlock!.Instructions.OfType<BinaryInstruction>().Count())
            .IsEqualTo(2);
        await Assert.That(outer.Left).IsSameReferenceAs((IrValue)a);
        await Assert.That(outer.Right).IsSameReferenceAs((IrValue)a);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task LocalCse_DeduplicatesGepWithEqualConstantIndex()
    {
        var (module, fn, b, _) = NewFn(
            IrType.Void,
            new IrParameter("p", IrType.Pointer(IrType.I8))
        );
        var p = fn.Parameters[0];
        var g1 = b.Gep(p, IrBuilder.ConstInt(IrType.I8, 2), IrType.I8);
        var g2 = b.Gep(p, IrBuilder.ConstInt(IrType.I8, 2), IrType.I8); // distinct const object, same value
        b.Store(IrBuilder.ConstInt(IrType.I8, 7), g1);
        b.Store(IrBuilder.ConstInt(IrType.I8, 9), g2);
        b.Ret();

        var changed = new LocalCsePass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert
            .That(fn.EntryBlock!.Instructions.OfType<GetElementPtrInstruction>().Count())
            .IsEqualTo(1);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task LocalCse_KeepsDistinctAllocasAndLoads()
    {
        var (_, fn, b, _) = NewFn(IrType.Void);
        b.Alloca(IrType.I8);
        b.Alloca(IrType.I8); // two allocas name distinct storage — never coalesced
        var p = b.Alloca(IrType.I8);
        b.Load(p);
        b.Load(p); // loads are RLE's job, not CSE's
        b.Ret();

        var changed = new LocalCsePass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert
            .That(fn.EntryBlock!.Instructions.OfType<AllocaInstruction>().Count())
            .IsEqualTo(3);
        await Assert
            .That(fn.EntryBlock!.Instructions.OfType<LoadInstruction>().Count())
            .IsEqualTo(2);
    }
}
