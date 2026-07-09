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
}
