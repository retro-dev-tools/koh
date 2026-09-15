namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Removes instructions whose result is never used and whose evaluation has no side effect. A
/// removed instruction can leave its own operands unused, so the pass iterates to a fixed point
/// (deleting <c>%b = add %a, 1</c> may make <c>%a</c> dead in turn).
///
/// Conservative on purpose: <c>load</c> is kept even when unused, because a load can target a
/// memory-mapped hardware register whose read has an observable effect; <c>store</c>/<c>call</c>/
/// intrinsics and terminators are never dead.
/// </summary>
public sealed class DeadCodeEliminationPass : IIrFunctionPass
{
    public bool Run(IrFunction function)
    {
        var changedOverall = false;
        bool changedThisRound;
        do
        {
            changedThisRound = false;
            var live = CollectLive(function);

            foreach (var block in function.Blocks)
            {
                var removed = block.Instructions.RemoveAll(instruction =>
                    IsTriviallyDead(instruction) && !live.Contains(instruction)
                );
                if (removed > 0)
                    changedThisRound = true;
            }

            changedOverall |= changedThisRound;
        } while (changedThisRound);

        return changedOverall;
    }

    /// <summary>
    /// Transitive mark-and-sweep liveness. Seeds the live set from every instruction that is
    /// NOT a removal candidate per <see cref="IsTriviallyDead"/> — i.e. every instruction with a
    /// side effect or observable output (store/call/intrinsic), every terminator (ret/br/condbr/
    /// switch), and load (kept unconditionally: a load can target volatile MMIO). Each root, and
    /// then transitively every value reachable from a root through operand edges (including phi
    /// incomings), is marked live.
    ///
    /// This is deliberately NOT the one-hop "is this an operand somewhere" check it replaces: a
    /// one-hop check can never sweep a closed cycle of otherwise-dead instructions whose only
    /// uses are each other (e.g. two phis in a loop header that each list the other as an
    /// incoming, with no store/call/return ever consuming either) — no root reaches into the
    /// cycle, so a real mark-and-sweep leaves both unmarked and both get swept, whereas the old
    /// one-hop check saw each phi "used" by the other, forever, and never removed either.
    /// </summary>
    private static HashSet<IrValue> CollectLive(IrFunction function)
    {
        var live = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
        var worklist = new Stack<IrValue>();

        void Mark(IrValue value)
        {
            if (live.Add(value))
                worklist.Push(value);
        }

        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
            if (!IsTriviallyDead(instruction))
                Mark(instruction);

        while (worklist.Count > 0)
        {
            var value = worklist.Pop();
            if (value is IrInstruction instruction)
                foreach (var operand in instruction.Operands)
                    Mark(operand);
        }

        return live;
    }

    /// <summary>
    /// A pure, result-producing instruction that is safe to delete when unused. Excludes anything
    /// with a side effect (store/call/intrinsic), any terminator, and load (potentially volatile MMIO).
    /// </summary>
    private static bool IsTriviallyDead(IrInstruction instruction) =>
        instruction
            is BinaryInstruction
                or CompareInstruction
                or ConvInstruction
                or GetElementPtrInstruction
                or AllocaInstruction
                or PhiInstruction;
}
