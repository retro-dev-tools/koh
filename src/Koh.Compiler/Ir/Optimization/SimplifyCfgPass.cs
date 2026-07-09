namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Simplifies control flow the constant folder has made trivial:
///
/// <list type="bullet">
/// <item><b>Constant branch folding.</b> A <c>condbr</c> on a constant condition becomes an
/// unconditional <c>br</c> to the taken target. The not-taken edge is removed, and any phi in the
/// not-taken block drops the incoming that arrived from this predecessor.</item>
/// <item><b>Unreachable-block removal.</b> Blocks no longer reachable from entry are deleted, and
/// phis in surviving blocks drop incomings that came from a deleted block — so the emitted code and
/// ROM don't carry dead blocks, and phi incomings stay matched to real predecessors.</item>
/// </list>
///
/// Phi incomings are maintained precisely on every edge change because the backend realizes phis as
/// per-predecessor edge copies; a stale incoming would describe a copy on an edge that no longer exists.
/// </summary>
public sealed class SimplifyCfgPass : IIrFunctionPass
{
    public bool Run(IrFunction function)
    {
        var changed = FoldConstantBranches(function);
        changed |= RemoveUnreachableBlocks(function);
        return changed;
    }

    private static bool FoldConstantBranches(IrFunction function)
    {
        var changed = false;
        foreach (var block in function.Blocks)
        {
            if (
                block.Terminator is not CondBrInstruction condbr
                || condbr.Condition is not IrConstInt constant
            )
                continue;

            var taken = constant.Value != 0 ? condbr.IfTrue : condbr.IfFalse;
            var notTaken = constant.Value != 0 ? condbr.IfFalse : condbr.IfTrue;

            var br = new BrInstruction(taken) { Parent = block, Source = condbr.Source };
            block.Instructions[^1] = br;

            // The edge block -> notTaken no longer exists (unless both arms were the same block).
            if (!ReferenceEquals(notTaken, taken))
                foreach (var instruction in notTaken.Instructions)
                    if (instruction is PhiInstruction phi)
                        phi.RemoveIncomingsFrom(block);

            changed = true;
        }
        return changed;
    }

    private static bool RemoveUnreachableBlocks(IrFunction function)
    {
        var entry = function.EntryBlock;
        if (entry is null)
            return false;

        var reachable = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { entry };
        var stack = new Stack<IrBasicBlock>();
        stack.Push(entry);
        while (stack.Count > 0)
        {
            var block = stack.Pop();
            if (block.Terminator is not { } terminator)
                continue;
            foreach (var successor in terminator.Successors)
                if (reachable.Add(successor))
                    stack.Push(successor);
        }

        if (reachable.Count == function.Blocks.Count)
            return false;

        var unreachable = function.Blocks.Where(b => !reachable.Contains(b)).ToList();

        // A surviving block may hold a phi whose incoming came from a now-deleted predecessor.
        foreach (var block in function.Blocks)
        {
            if (!reachable.Contains(block))
                continue;
            foreach (var instruction in block.Instructions)
                if (instruction is PhiInstruction phi)
                    foreach (var dead in unreachable)
                        phi.RemoveIncomingsFrom(dead);
        }

        function.Blocks.RemoveAll(b => !reachable.Contains(b));
        return true;
    }
}
