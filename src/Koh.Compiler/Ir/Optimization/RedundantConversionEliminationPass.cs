namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Folds a trunc-of-extend round-trip back to the original value: <c>trunc(zext(x)) -&gt; x</c> and
/// <c>trunc(sext(x)) -&gt; x</c>, whenever the trunc's result width equals <c>x</c>'s own width.
/// Widening <c>x</c> and then immediately truncating back to its original width recovers <c>x</c>
/// exactly, regardless of which extension kind put the high bits there or what they were: the extra
/// bits zext/sext add above width <c>w</c> are precisely the bits trunc discards to get back to
/// <c>w</c> bits. The reverse shape, <c>zext(trunc(x))</c>, is NOT an identity (trunc can discard live
/// high bits) and is deliberately left alone.
///
/// This exists to clean up an identity pair the CIL frontend routinely introduces: CIL's evaluation
/// stack always widens sub-int32 locals to i32 (ECMA-335 III.1.1), so storing into a narrow local
/// (<c>conv.u1</c>/<c>conv.i1</c>) and then reading it back for the next use round-trips through a
/// zext/sext back up to i32. That leaves a value's own natural width (e.g. an i8 loop counter)
/// wrapped as <c>trunc(zext(x : i8) : i32) : i8</c>. <see cref="NarrowPass"/> narrows the arithmetic
/// that flows through this shape but does not remove the trunc/zext pair itself. Left in place, this
/// defeats <c>Sm83FunctionAllocation</c>'s loop-induction register residency
/// (<c>SelectLoopInductionResidents</c>) — which requires a loop phi's back-edge value be a gentle ALU
/// op (add/sub/and/...) — because the phi's back-edge value is this trunc, not the add/sub it wraps,
/// forcing the induction variable through a WRAM round-trip every iteration instead of staying
/// register-resident.
/// </summary>
public sealed class RedundantConversionEliminationPass : IIrFunctionPass
{
    public bool Run(IrFunction function)
    {
        var folded = new List<(IrBasicBlock Block, IrInstruction Instruction)>();

        // Mirrors ConstantFoldingPass's shape: rewrite operands (RAUW) immediately as each fold is
        // found, in block/instruction order, and only defer the dead instruction's removal. This
        // matters for a chain of two round-trips sharing a value (e.g. `byte old = i;` followed by
        // reading `old` back both fold, and the second fold's inner value IS the first fold's trunc) —
        // folding eagerly means the second fold's TryFold sees the already-rewired operand (the real
        // upstream value), not a stale reference to an instruction this same pass is about to orphan.
        // Batching all replacements to the end instead would let the second fold's replacement point at
        // an instruction already removed from its block by the first, leaving a dangling operand.
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

    /// <summary>The value this trunc(zext/sext(x)) round-trip reduces to, or null if the instruction
    /// isn't that shape or the widths don't actually round-trip.</summary>
    private static IrValue? TryFold(IrInstruction instruction)
    {
        if (instruction is not ConvInstruction { Op: IrConvOp.Trunc } trunc)
            return null;
        if (trunc.Type.Kind != IrTypeKind.Int)
            return null;
        if (trunc.Operand is not ConvInstruction { Op: IrConvOp.ZExt or IrConvOp.SExt } ext)
            return null;

        var inner = ext.Operand;
        if (inner.Type.Kind != IrTypeKind.Int)
            return null;

        // Guard strictly: the trunc must land back on x's OWN width (not merely a narrower one than
        // the extension's target), and the extension must have actually widened x (w < W) — otherwise
        // this isn't the identity round-trip at all.
        var w = inner.Type.SizeInBits;
        var wide = ext.Type.SizeInBits;
        var truncWidth = trunc.Type.SizeInBits;
        if (truncWidth != w || w >= wide)
            return null;

        return inner;
    }
}
