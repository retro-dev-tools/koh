namespace Koh.Compiler.Ir.Analysis;

/// <summary>
/// SSA liveness: for each basic block, the set of values live on entry (live-in) and on exit
/// (live-out). A value is live at a program point if it holds a result that a later instruction will
/// read before it is redefined. This is the foundational analysis a register allocator runs — two
/// values that are never simultaneously live can share a register (or, today, a WRAM slot), so live
/// ranges drive allocation directly.
///
/// The backend already computes this, but privately and inline in <c>FunctionAllocation</c>, filtered
/// to the values it colours and fused with backend-specific interference rules. This is the same
/// analysis extracted as a reusable, general component over <em>every</em> SSA value (instruction
/// results and parameters), so a future register-residency allocator — and a slimmed WRAM colourer —
/// can share one liveness implementation instead of each re-deriving it. See
/// <c>docs/superpowers/specs/2026-07-09-register-residency.md</c> for how it fits the residency plan.
///
/// The dataflow is the standard backward fixpoint with SSA phi semantics: a phi's incoming value is
/// used on the corresponding <em>predecessor edge</em>, not in the phi's own block, so it is live-out of
/// that predecessor rather than live-in of the phi block.
/// </summary>
public sealed class IrLiveness
{
    private static readonly ReferenceEqualityComparer Eq = ReferenceEqualityComparer.Instance;

    private readonly Dictionary<IrBasicBlock, HashSet<IrValue>> _liveIn;
    private readonly Dictionary<IrBasicBlock, HashSet<IrValue>> _liveOut;

    private IrLiveness(
        Dictionary<IrBasicBlock, HashSet<IrValue>> liveIn,
        Dictionary<IrBasicBlock, HashSet<IrValue>> liveOut
    )
    {
        _liveIn = liveIn;
        _liveOut = liveOut;
    }

    /// <summary>Values live on entry to <paramref name="block"/>.</summary>
    public IReadOnlySet<IrValue> LiveIn(IrBasicBlock block) =>
        _liveIn.TryGetValue(block, out var s) ? s : Empty;

    /// <summary>Values live on exit from <paramref name="block"/>.</summary>
    public IReadOnlySet<IrValue> LiveOut(IrBasicBlock block) =>
        _liveOut.TryGetValue(block, out var s) ? s : Empty;

    /// <summary>Whether <paramref name="value"/> is live on exit from <paramref name="block"/>.</summary>
    public bool IsLiveOut(IrBasicBlock block, IrValue value) => LiveOut(block).Contains(value);

    private static readonly HashSet<IrValue> Empty = new(Eq);

    /// <summary>Whether <paramref name="value"/> is an SSA definition that can be live: a non-void
    /// instruction result or a function parameter. Constants and global references are not tracked.</summary>
    public static bool IsTrackable(IrValue value) =>
        value is IrParameter
        || (value is IrInstruction instruction && instruction.Type.Kind != IrTypeKind.Void);

    public static IrLiveness Compute(IrFunction function)
    {
        var blocks = function.Blocks;

        // Per-block upward-exposed uses and definitions, and the phis of each block (whose operands are
        // edge uses, handled specially in the fixpoint below).
        var use = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
        var def = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
        var phis = new Dictionary<IrBasicBlock, List<PhiInstruction>>(Eq);

        foreach (var block in blocks)
        {
            var blockUse = new HashSet<IrValue>(Eq);
            var blockDef = new HashSet<IrValue>(Eq);
            var blockPhis = new List<PhiInstruction>();

            // Phis define at the top of the block.
            foreach (var instruction in block.Instructions)
                if (instruction is PhiInstruction phi)
                {
                    blockDef.Add(phi);
                    blockPhis.Add(phi);
                }

            var defined = new HashSet<IrValue>(blockDef, Eq);
            foreach (var instruction in block.Instructions)
            {
                if (instruction is PhiInstruction)
                    continue; // phi operands are edge uses, not uses in this block
                foreach (var operand in instruction.Operands)
                    if (IsTrackable(operand) && !defined.Contains(operand))
                        blockUse.Add(operand); // used before any definition in this block
                if (IsTrackable(instruction))
                {
                    blockDef.Add(instruction);
                    defined.Add(instruction);
                }
            }

            use[block] = blockUse;
            def[block] = blockDef;
            phis[block] = blockPhis;
        }

        var liveIn = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
        var liveOut = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
        foreach (var block in blocks)
        {
            liveIn[block] = new HashSet<IrValue>(Eq);
            liveOut[block] = new HashSet<IrValue>(Eq);
        }

        // Backward dataflow to a fixpoint. Iterating blocks in reverse tends to converge fastest on the
        // reducible CFGs the frontend produces, but the result is order-independent.
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var block = blocks[i];

                var newOut = new HashSet<IrValue>(Eq);
                foreach (var successor in Successors(block))
                {
                    foreach (var v in liveIn[successor])
                        newOut.Add(v);
                    // A successor's phi makes its incoming value live-out of the matching predecessor.
                    foreach (var phi in phis[successor])
                    foreach (var (value, pred) in phi.Incomings)
                        if (ReferenceEquals(pred, block) && IsTrackable(value))
                            newOut.Add(value);
                }

                var newIn = new HashSet<IrValue>(use[block], Eq);
                foreach (var v in newOut)
                    if (!def[block].Contains(v))
                        newIn.Add(v);

                if (!newOut.SetEquals(liveOut[block]) || !newIn.SetEquals(liveIn[block]))
                {
                    liveOut[block] = newOut;
                    liveIn[block] = newIn;
                    changed = true;
                }
            }
        }

        return new IrLiveness(liveIn, liveOut);
    }

    private static IEnumerable<IrBasicBlock> Successors(IrBasicBlock block) =>
        block.Terminator?.Successors ?? [];
}
