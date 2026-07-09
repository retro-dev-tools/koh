using System.Numerics;

namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Replaces multiply / unsigned-divide / unsigned-remainder by a constant power of two with a shift
/// or mask — a large win on SM83, where <c>mul</c>/<c>div</c>/<c>rem</c> are open-coded runtime
/// routines while shifts and <c>and</c> are a few inline instructions:
///
/// <list type="bullet">
/// <item><c>x * 2^k → x &lt;&lt; k</c> (either operand; correct for signed and unsigned, since both
/// are multiplication modulo the type width).</item>
/// <item><c>x u/ 2^k → x u&gt;&gt; k</c> (logical shift right).</item>
/// <item><c>x u% 2^k → x &amp; (2^k - 1)</c>.</item>
/// </list>
///
/// Signed divide/remainder by a power of two are intentionally left alone: arithmetic shift rounds
/// toward negative infinity while C# division truncates toward zero, so they are not equivalent
/// without a correction step. Multiplying by 1 / 0 is handled by the folder's identities, so this
/// pass only fires for 2^k with k ≥ 1.
/// </summary>
public sealed class StrengthReductionPass : IIrFunctionPass
{
    public string Name => "strength-reduction";

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
                    new IrConstInt(b.Type, Wrap((1L << km) - 1, bits))
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
        var value = (ulong)c.Value & Mask(bits);
        if (value < 2 || (value & (value - 1)) != 0)
            return null;
        var k = BitOperations.TrailingZeroCount(value);
        return k < bits ? k : null;
    }

    private static ulong Mask(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    private static long Wrap(long value, int bits)
    {
        if (bits >= 64)
            return value;
        var masked = (long)((ulong)value & Mask(bits));
        var signBit = 1L << (bits - 1);
        return (masked ^ signBit) - signBit;
    }
}
