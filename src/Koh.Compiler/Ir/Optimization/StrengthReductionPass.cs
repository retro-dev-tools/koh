using System.Numerics;

namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Replaces multiply / unsigned-divide / unsigned-remainder by a constant power of two with a shift
/// or mask (<c>x*2^k → x&lt;&lt;k</c>, <c>x u/2^k → x&gt;&gt;k</c>, <c>x u%2^k → x &amp; (2^k-1)</c>) —
/// a large win on SM83, where mul/div/rem are open-coded runtime routines. Signed divide/remainder
/// are left alone (arithmetic shift rounds toward −∞, C# division toward zero); <c>*1</c>/<c>*0</c>
/// are the folder's job, so this only fires for 2^k with k ≥ 1.
/// </summary>
public sealed class StrengthReductionPass : IIrFunctionPass
{
    public bool Run(IrFunction function)
    {
        var changed = false;
        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                if (block.Instructions[i] is not BinaryInstruction binary)
                    continue;
                var replacement = TryReduce(binary);
                if (replacement is null)
                    continue;

                replacement.Parent = block;
                replacement.Source = binary.Source;
                block.Instructions[i] = replacement;
                IrOptimizer.ReplaceAllUses(function, binary, replacement);
                changed = true;
            }
        }
        return changed;
    }

    private static BinaryInstruction? TryReduce(BinaryInstruction b)
    {
        if (b.Type.Kind != IrTypeKind.Int)
            return null;
        var bits = b.Type.Bits;

        switch (b.Op)
        {
            case IrBinaryOp.Mul when Pow2Log(b.Right, bits) is { } kr:
                return Shift(IrBinaryOp.Shl, b.Left, kr, b.Type);
            case IrBinaryOp.Mul when Pow2Log(b.Left, bits) is { } kl:
                return Shift(IrBinaryOp.Shl, b.Right, kl, b.Type);
            case IrBinaryOp.UDiv when Pow2Log(b.Right, bits) is { } kd:
                return Shift(IrBinaryOp.LShr, b.Left, kd, b.Type);
            case IrBinaryOp.URem when Pow2Log(b.Right, bits) is { } km:
                return new BinaryInstruction(
                    IrBinaryOp.And,
                    b.Left,
                    new IrConstInt(b.Type, IntWidth.Wrap((1L << km) - 1, bits))
                );
            default:
                return null;
        }
    }

    private static BinaryInstruction Shift(IrBinaryOp op, IrValue value, int amount, IrType type) =>
        new(op, value, new IrConstInt(type, amount));

    /// <summary>log2 of <paramref name="operand"/> if it is a constant power of two ≥ 2 at this
    /// width (so the shift amount is in range), else null.</summary>
    private static int? Pow2Log(IrValue operand, int bits)
    {
        if (operand is not IrConstInt c)
            return null;
        var value = IntWidth.ToUnsigned(c.Value, bits);
        if (value < 2 || (value & (value - 1)) != 0)
            return null;
        var k = BitOperations.TrailingZeroCount(value);
        return k < bits ? k : null;
    }
}
