using System.Numerics;

namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Replaces multiply / unsigned-divide / unsigned-remainder by a constant power of two with a shift
/// or mask (<c>x*2^k → x&lt;&lt;k</c>, <c>x u/2^k → x&gt;&gt;k</c>, <c>x u%2^k → x &amp; (2^k-1)</c>) —
/// a large win on SM83, where mul/div/rem are open-coded runtime routines. <c>*1</c>/<c>*0</c> are the
/// folder's job, so this only fires for 2^k with k ≥ 1.
///
/// Signed divide by a constant positive power of two ALSO reduces, via the standard bias-before-shift
/// identity: a naive arithmetic shift rounds toward −∞, but C# division truncates toward zero, and
/// <c>(x + ((x &gt;&gt;&gt;s (bits-1)) &amp; (2^k-1))) &gt;&gt;&gt;s k</c> (arithmetic shifts, `&gt;&gt;&gt;s`) computes
/// exactly the truncating quotient: broadcasting the sign bit across the low k bits via an
/// arithmetic shift by (bits-1) gives an all-zero bias when x ≥ 0 and an all-one-bits (i.e. 2^k-1)
/// bias when x &lt; 0, and adding that bias before the arithmetic shift cancels the −∞ rounding's
/// error term in exactly the negative case that needs correcting. This is the textbook identity for
/// signed power-of-two division (see e.g. Hacker's Delight §10-1) — sound for every dividend,
/// including <c>int.MinValue</c>, at any width. Signed REMAINDER is left alone (no analogous
/// single-mask identity fires as cheaply), and only a POSITIVE divisor qualifies: 2^(bits-1) is
/// negative when reinterpreted as this width's signed divisor and this identity assumes positive.
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
                var chain = TryReduce(binary);
                if (chain is null)
                    continue;

                foreach (var inst in chain)
                {
                    inst.Parent = block;
                    inst.Source = binary.Source;
                }
                var replacement = chain[^1];
                if (chain.Length > 1)
                    block.Instructions.InsertRange(i, chain[..^1]);
                block.Instructions[i + chain.Length - 1] = replacement;
                IrOptimizer.ReplaceAllUses(function, binary, replacement);
                i += chain.Length - 1;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Returns the new instruction(s) to splice in place of <paramref name="b"/>, in order,
    /// with the last entry being the value that replaces <paramref name="b"/>'s uses — or null if no
    /// reduction applies.</summary>
    private static IrInstruction[]? TryReduce(BinaryInstruction b)
    {
        if (b.Type.Kind != IrTypeKind.Int)
            return null;
        var bits = b.Type.Bits;

        switch (b.Op)
        {
            case IrBinaryOp.Mul when Pow2Log(b.Right, bits) is { } kr:
                return [Shift(IrBinaryOp.Shl, b.Left, kr, b.Type)];
            case IrBinaryOp.Mul when Pow2Log(b.Left, bits) is { } kl:
                return [Shift(IrBinaryOp.Shl, b.Right, kl, b.Type)];
            case IrBinaryOp.UDiv when Pow2Log(b.Right, bits) is { } kd:
                return [Shift(IrBinaryOp.LShr, b.Left, kd, b.Type)];
            case IrBinaryOp.URem when Pow2Log(b.Right, bits) is { } km:
                return
                [
                    new BinaryInstruction(
                        IrBinaryOp.And,
                        b.Left,
                        new IrConstInt(b.Type, IntWidth.Wrap((1L << km) - 1, bits))
                    ),
                ];
            case IrBinaryOp.SDiv when PositivePow2Log(b.Right, bits) is { } ks:
                return SignedDivPow2(b.Left, ks, b.Type);
            default:
                return null;
        }
    }

    private static BinaryInstruction Shift(IrBinaryOp op, IrValue value, int amount, IrType type) =>
        new(op, value, new IrConstInt(type, amount));

    /// <summary>Builds the bias-before-shift chain for <c>x / 2^k</c> (signed, truncating toward
    /// zero) described in the class remarks: sign-broadcast, mask to the bias, add, arithmetic-shift.
    /// Returns the four instructions in dependency order; the last is the quotient.</summary>
    private static IrInstruction[] SignedDivPow2(IrValue x, int k, IrType type)
    {
        var bits = type.Bits;
        var signBcast = new BinaryInstruction(IrBinaryOp.AShr, x, new IrConstInt(type, bits - 1));
        var bias = new BinaryInstruction(
            IrBinaryOp.And,
            signBcast,
            new IrConstInt(type, (1L << k) - 1)
        );
        var biased = new BinaryInstruction(IrBinaryOp.Add, x, bias);
        var quotient = new BinaryInstruction(IrBinaryOp.AShr, biased, new IrConstInt(type, k));
        return [signBcast, bias, biased, quotient];
    }

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

    /// <summary>Like <see cref="Pow2Log"/>, but additionally excludes k == bits-1 (2^(bits-1)): that
    /// bit pattern is a NEGATIVE value when reinterpreted as this width's signed divisor, which the
    /// signed bias-before-shift identity does not handle (it assumes a positive divisor).</summary>
    private static int? PositivePow2Log(IrValue operand, int bits) =>
        Pow2Log(operand, bits) is { } k && k < bits - 1 ? k : null;
}
