namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Collapses a comparison of a comparison's (Boolean) result against the constant <c>0</c> or <c>1</c>
/// into a single comparison — the algebraic simplification of the redundant <c>== 0</c>/<c>!= 0</c>
/// negation chains the frontend produces for ordinary source.
///
/// <para>A <see cref="CompareInstruction"/> always yields exactly <c>0</c> or <c>1</c>
/// (<see cref="ConstantFoldingPass.FoldCompare"/> materialises it as <c>IrType.I8</c> 0/1, and the
/// backend's <c>MaterializeBoolean</c> does the same), so an outer <c>icmp</c> that tests that result
/// against a constant Boolean is pure redundancy:</para>
/// <list type="bullet">
/// <item><c>icmp ne (cmp), 0</c> and <c>icmp eq (cmp), 1</c> are exactly <c>cmp</c> (identity).</item>
/// <item><c>icmp eq (cmp), 0</c> and <c>icmp ne (cmp), 1</c> are exactly <c>!cmp</c> — the same
/// comparison with its predicate negated (Eq↔Ne, Ult↔Uge, Ule↔Ugt, Slt↔Sge, Sle↔Sgt), which needs no
/// extra runtime work at all: the backend has a direct opcode form for every predicate.</item>
/// </list>
///
/// <para><b>Why this matters (and why it is more than cosmetic).</b> Roslyn lowers a pointer/relational
/// inequality with no dedicated IL opcode — e.g. <c>p != q</c> — as <c>ceq ; ldc.i4.0 ; ceq</c>
/// (<c>(p == q) == 0</c>), and the CIL frontend's <c>brtrue</c>/<c>brfalse</c> lowering then wraps that
/// already-Boolean value in yet another <c>icmp ne …, 0</c> / <c>icmp eq …, 0</c> to feed the
/// <c>condbr</c>. A single <c>while (p != q)</c> loop header therefore lowers to a THREE-deep compare
/// chain (<c>eq ; eq ; ne</c>) where one comparison suffices. Every one of those extra comparisons is a
/// full materialise-Boolean-to-a-slot-then-retest sequence on the accumulator machine — a dozen-plus
/// instructions per loop iteration, and, worse, a dozen-plus instructions of latency between a loop's
/// entry and the first thing its body actually does. A vblank-bounded byte-copy drip
/// (<c>Koh.GameBoy.Graphics.MapWriter.FlushRun</c>) whose per-iteration guard reads <c>LY</c> and bails
/// the instant the PPU leaves the vblank window is acutely sensitive to that latency: bloating the guard
/// path pushes the first <c>LY</c> read past the end of the window, so the drip copies zero bytes and
/// makes no progress — and it does so only once some unrelated upstream code shifts the loop's arrival
/// phase by a few cycles, which is exactly the "layout-perturbed stride-1 loop" failure mode. Folding
/// the chain to one comparison keeps the guard tight enough that the loop reliably makes progress
/// regardless of that phase, closing the whole class of fragility rather than one instance of it.</para>
///
/// <para>Runs to a fixpoint through <see cref="IrOptimizer"/>'s round loop (it emits a fresh
/// <see cref="CompareInstruction"/> for the negated case, which the next round can fold again if it in
/// turn feeds another redundant test), and pairs with <see cref="DeadCodeEliminationPass"/>, which
/// removes the now-unused inner comparisons.</para>
/// </summary>
public sealed class RedundantCompareEliminationPass : IIrFunctionPass
{
    public bool Run(IrFunction function)
    {
        // Applied one at a time with a re-scan after each: folding a chain (e.g. `ne(eq(x, 0), 0)`)
        // rewrites the outer test's operand, so the next scan sees the now-directly-foldable remainder.
        // Batching all rewrites first would capture stale replacements — an inner comparison chosen as
        // one rewrite's replacement can itself be the target of an earlier-applied rewrite and be removed
        // out from under it.
        bool changedAny = false;
        while (TryApplyOne(function))
            changedAny = true;
        return changedAny;
    }

    private static bool TryApplyOne(IrFunction function)
    {
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
        {
            if (instruction is not CompareInstruction outer)
                continue;
            if (outer.Op is not (IrCompareOp.Eq or IrCompareOp.Ne))
                continue;

            // Exactly one operand must be the Boolean constant 0 or 1, the other a comparison (whose
            // result is provably 0/1). Handle the constant on either side.
            var (inner, konst) = Classify(outer.Left, outer.Right);
            if (inner is null || konst is null)
                continue;

            long value = konst.Value;
            if (value is not (0 or 1))
                continue; // a comparison result is 0/1, so only these constants carry information

            // "Selects inner" (identity) vs. "selects !inner" (negation). Eq-to-1 and Ne-to-0 are the
            // value itself; Eq-to-0 and Ne-to-1 are its logical negation.
            bool identity = (outer.Op == IrCompareOp.Ne) == (value == 0);
            IrValue replacement;
            if (identity)
            {
                replacement = inner;
            }
            else
            {
                // The negated comparison must be placed where the outer one was, so it dominates every
                // use the RAUW is about to redirect to it.
                var fresh = new CompareInstruction(Negate(inner.Op), inner.Left, inner.Right)
                {
                    Parent = block,
                };
                block.Instructions.Insert(block.Instructions.IndexOf(outer), fresh);
                replacement = fresh;
            }

            IrOptimizer.ReplaceAllUses(function, outer, replacement);
            block.Instructions.Remove(outer);
            return true;
        }
        return false;
    }

    /// <summary>Split an outer comparison's operands into its inner comparison and its constant, in
    /// either operand order, or <c>(null, null)</c> if it is not a "comparison vs. constant" shape.</summary>
    private static (CompareInstruction? Inner, IrConstInt? Const) Classify(
        IrValue left,
        IrValue right
    ) =>
        (left, right) switch
        {
            (CompareInstruction c, IrConstInt k) => (c, k),
            (IrConstInt k, CompareInstruction c) => (c, k),
            _ => (null, null),
        };

    /// <summary>The predicate whose truth is the exact logical negation of <paramref name="op"/>.</summary>
    private static IrCompareOp Negate(IrCompareOp op) =>
        op switch
        {
            IrCompareOp.Eq => IrCompareOp.Ne,
            IrCompareOp.Ne => IrCompareOp.Eq,
            IrCompareOp.Ult => IrCompareOp.Uge,
            IrCompareOp.Uge => IrCompareOp.Ult,
            IrCompareOp.Ule => IrCompareOp.Ugt,
            IrCompareOp.Ugt => IrCompareOp.Ule,
            IrCompareOp.Slt => IrCompareOp.Sge,
            IrCompareOp.Sge => IrCompareOp.Slt,
            IrCompareOp.Sle => IrCompareOp.Sgt,
            IrCompareOp.Sgt => IrCompareOp.Sle,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "unknown compare predicate"),
        };
}
