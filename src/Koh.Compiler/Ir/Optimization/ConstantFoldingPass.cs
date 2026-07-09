namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Folds integer <c>binary</c>/<c>icmp</c>/<c>conv</c> instructions whose operands are constants
/// to a single constant, and applies algebraic identities that collapse an operation to one of its
/// operands or to a constant (<c>x + 0 → x</c>, <c>x * 0 → 0</c>, <c>x &amp; -1 → x</c>, …). All
/// arithmetic is done at the operation's declared width with the operation's signedness, and the
/// result is wrapped to that width, so folding matches exactly what the backend would have emitted.
///
/// Division and remainder by a zero constant are deliberately left unfolded (their behavior is the
/// backend's to define), as are shifts by an out-of-range amount.
/// </summary>
public sealed class ConstantFoldingPass : IIrFunctionPass
{
    public string Name => "constant-folding";

    public bool Run(IrFunction function)
    {
        var folded = new List<(IrBasicBlock Block, IrInstruction Instruction)>();

        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
        {
            var replacement = TryFold(instruction);
            if (replacement is null)
                continue;
            IrOptimizer.ReplaceAllUses(function, instruction, replacement);
            folded.Add((block, instruction));
        }

        foreach (var (block, instruction) in folded)
            block.Instructions.Remove(instruction);

        return folded.Count > 0;
    }

    /// <summary>The constant/value this instruction reduces to, or null if it doesn't fold.</summary>
    private static IrValue? TryFold(IrInstruction instruction) =>
        instruction switch
        {
            BinaryInstruction b => FoldBinary(b),
            CompareInstruction c => FoldCompare(c),
            ConvInstruction c => FoldConv(c),
            _ => null,
        };

    // ---- Binary --------------------------------------------------------------

    private static IrValue? FoldBinary(BinaryInstruction b)
    {
        if (b.Type.Kind != IrTypeKind.Int)
            return null;
        var bits = b.Type.Bits;

        if (b.Left is IrConstInt l && b.Right is IrConstInt r)
            return FoldConstBinary(b.Op, l.Value, r.Value, bits, b.Type);

        return FoldBinaryIdentity(b, bits);
    }

    private static IrValue? FoldConstBinary(IrBinaryOp op, long l, long r, int bits, IrType type)
    {
        long? result = op switch
        {
            IrBinaryOp.Add => unchecked(AsSigned(l, bits) + AsSigned(r, bits)),
            IrBinaryOp.Sub => unchecked(AsSigned(l, bits) - AsSigned(r, bits)),
            IrBinaryOp.Mul => unchecked(AsSigned(l, bits) * AsSigned(r, bits)),
            IrBinaryOp.And => l & r,
            IrBinaryOp.Or => l | r,
            IrBinaryOp.Xor => l ^ r,
            IrBinaryOp.Shl => FoldShift(op, l, r, bits),
            IrBinaryOp.LShr => FoldShift(op, l, r, bits),
            IrBinaryOp.AShr => FoldShift(op, l, r, bits),
            IrBinaryOp.UDiv => AsUnsigned(r, bits) == 0
                ? null
                : (long)(AsUnsigned(l, bits) / AsUnsigned(r, bits)),
            IrBinaryOp.URem => AsUnsigned(r, bits) == 0
                ? null
                : (long)(AsUnsigned(l, bits) % AsUnsigned(r, bits)),
            IrBinaryOp.SDiv => FoldSignedDivRem(op, l, r, bits),
            IrBinaryOp.SRem => FoldSignedDivRem(op, l, r, bits),
            _ => null,
        };

        return result is { } v ? new IrConstInt(type, Wrap(v, bits)) : null;
    }

    private static long? FoldShift(IrBinaryOp op, long l, long r, int bits)
    {
        var amount = AsUnsigned(r, bits);
        if (amount >= (ulong)bits)
            return null; // out-of-range shift: leave for the backend to define
        var shift = (int)amount;
        return op switch
        {
            IrBinaryOp.Shl => AsSigned(l, bits) << shift,
            IrBinaryOp.LShr => (long)(AsUnsigned(l, bits) >> shift),
            IrBinaryOp.AShr => AsSigned(l, bits) >> shift,
            _ => null,
        };
    }

    private static long? FoldSignedDivRem(IrBinaryOp op, long l, long r, int bits)
    {
        var (a, b) = (AsSigned(l, bits), AsSigned(r, bits));
        if (b == 0)
            return null;
        // The single overflow case in two's complement: MIN / -1 has no representable result.
        if (b == -1 && a == MinSigned(bits))
            return null;
        return op == IrBinaryOp.SDiv ? a / b : a % b;
    }

    private static IrValue? FoldBinaryIdentity(BinaryInstruction b, int bits)
    {
        var leftConst = b.Left as IrConstInt;
        var rightConst = b.Right as IrConstInt;
        var allOnes = Mask(bits);
        var zero = new IrConstInt(b.Type, 0);

        switch (b.Op)
        {
            case IrBinaryOp.Add:
                if (IsZero(rightConst, bits))
                    return b.Left;
                if (IsZero(leftConst, bits))
                    return b.Right;
                break;
            case IrBinaryOp.Sub:
                if (IsZero(rightConst, bits))
                    return b.Left;
                if (ReferenceEquals(b.Left, b.Right))
                    return zero;
                break;
            case IrBinaryOp.Mul:
                if (IsValue(rightConst, 1, bits) || IsValue(leftConst, 1, bits))
                    return IsValue(rightConst, 1, bits) ? b.Left : b.Right;
                if (IsZero(rightConst, bits) || IsZero(leftConst, bits))
                    return zero;
                break;
            case IrBinaryOp.And:
                if (IsZero(rightConst, bits) || IsZero(leftConst, bits))
                    return zero;
                if (IsUnsigned(rightConst, allOnes, bits))
                    return b.Left;
                if (IsUnsigned(leftConst, allOnes, bits))
                    return b.Right;
                if (ReferenceEquals(b.Left, b.Right))
                    return b.Left;
                break;
            case IrBinaryOp.Or:
                if (IsUnsigned(rightConst, allOnes, bits) || IsUnsigned(leftConst, allOnes, bits))
                    return new IrConstInt(b.Type, Wrap((long)allOnes, bits));
                if (IsZero(rightConst, bits))
                    return b.Left;
                if (IsZero(leftConst, bits))
                    return b.Right;
                if (ReferenceEquals(b.Left, b.Right))
                    return b.Left;
                break;
            case IrBinaryOp.Xor:
                if (IsZero(rightConst, bits))
                    return b.Left;
                if (IsZero(leftConst, bits))
                    return b.Right;
                if (ReferenceEquals(b.Left, b.Right))
                    return zero;
                break;
            case IrBinaryOp.Shl:
            case IrBinaryOp.LShr:
            case IrBinaryOp.AShr:
                if (IsZero(rightConst, bits))
                    return b.Left;
                break;
            case IrBinaryOp.UDiv:
            case IrBinaryOp.SDiv:
                if (IsValue(rightConst, 1, bits))
                    return b.Left;
                break;
            case IrBinaryOp.URem:
            case IrBinaryOp.SRem:
                if (IsValue(rightConst, 1, bits))
                    return zero;
                break;
        }

        return null;
    }

    // ---- Compare -------------------------------------------------------------

    private static IrValue? FoldCompare(CompareInstruction c)
    {
        if (c.Left is not IrConstInt l || c.Right is not IrConstInt r)
            return null;
        if (c.Left.Type.Kind != IrTypeKind.Int)
            return null;

        var bits = c.Left.Type.Bits;
        var (ua, ub) = (AsUnsigned(l.Value, bits), AsUnsigned(r.Value, bits));
        var (sa, sb) = (AsSigned(l.Value, bits), AsSigned(r.Value, bits));

        var result = c.Op switch
        {
            IrCompareOp.Eq => ua == ub,
            IrCompareOp.Ne => ua != ub,
            IrCompareOp.Ult => ua < ub,
            IrCompareOp.Ule => ua <= ub,
            IrCompareOp.Ugt => ua > ub,
            IrCompareOp.Uge => ua >= ub,
            IrCompareOp.Slt => sa < sb,
            IrCompareOp.Sle => sa <= sb,
            IrCompareOp.Sgt => sa > sb,
            IrCompareOp.Sge => sa >= sb,
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };

        return new IrConstInt(IrType.I8, result ? 1 : 0);
    }

    // ---- Conversions ---------------------------------------------------------

    private static IrValue? FoldConv(ConvInstruction c)
    {
        if (c.Operand is not IrConstInt operand || operand.Type.Kind != IrTypeKind.Int)
            return null;

        var srcBits = operand.Type.Bits;
        return c.Op switch
        {
            IrConvOp.Trunc when c.Type.Kind == IrTypeKind.Int => new IrConstInt(
                c.Type,
                Wrap(operand.Value, c.Type.Bits)
            ),
            IrConvOp.ZExt when c.Type.Kind == IrTypeKind.Int => new IrConstInt(
                c.Type,
                (long)AsUnsigned(operand.Value, srcBits)
            ),
            IrConvOp.SExt when c.Type.Kind == IrTypeKind.Int => new IrConstInt(
                c.Type,
                AsSigned(operand.Value, srcBits)
            ),
            // A same-width int/int bitcast is a pure reinterpretation of identical bits.
            IrConvOp.Bitcast when c.Type.Kind == IrTypeKind.Int && c.Type.Bits == srcBits =>
                new IrConstInt(c.Type, operand.Value),
            _ => null,
        };
    }

    // ---- Width helpers -------------------------------------------------------

    private static ulong Mask(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    private static ulong AsUnsigned(long value, int bits) => (ulong)value & Mask(bits);

    private static long AsSigned(long value, int bits)
    {
        if (bits >= 64)
            return value;
        var masked = (long)AsUnsigned(value, bits);
        var signBit = 1L << (bits - 1);
        return (masked ^ signBit) - signBit;
    }

    /// <summary>Normalize a value to its two's-complement representation at the given width.</summary>
    private static long Wrap(long value, int bits) => AsSigned(value, bits);

    private static long MinSigned(int bits) => bits >= 64 ? long.MinValue : -(1L << (bits - 1));

    private static bool IsZero(IrConstInt? c, int bits) =>
        c is not null && AsUnsigned(c.Value, bits) == 0;

    private static bool IsValue(IrConstInt? c, ulong value, int bits) =>
        c is not null && AsUnsigned(c.Value, bits) == value;

    private static bool IsUnsigned(IrConstInt? c, ulong value, int bits) =>
        c is not null && AsUnsigned(c.Value, bits) == value;
}
