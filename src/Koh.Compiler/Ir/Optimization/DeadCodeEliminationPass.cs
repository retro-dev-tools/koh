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
    public string Name => "dce";

    public bool Run(IrFunction function)
    {
        var changedOverall = false;
        bool changedThisRound;
        do
        {
            changedThisRound = false;
            var used = CollectUsed(function);

            foreach (var block in function.Blocks)
            {
                var removed = block.Instructions.RemoveAll(instruction =>
                    IsTriviallyDead(instruction) && !used.Contains(instruction)
                );
                if (removed > 0)
                    changedThisRound = true;
            }

            changedOverall |= changedThisRound;
        } while (changedThisRound);

        return changedOverall;
    }

    private static HashSet<IrValue> CollectUsed(IrFunction function)
    {
        var used = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
        foreach (var operand in instruction.Operands)
            used.Add(operand);
        return used;
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
