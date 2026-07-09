namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Promotes non-escaping integer scalar <c>alloca</c>s to SSA values, inserting phis where a value
/// merges — the enabler that lifts a local whose live range spans control flow (read after an
/// <c>if</c>, a loop counter) out of memory, past what intra-block
/// <see cref="RedundantLoadEliminationPass"/> can reach. Standard construction: place phis at the
/// iterated dominance frontier of each alloca's store blocks, then rename by a dominator-tree walk
/// carrying each alloca's reaching definition. Arrays, structs, and pointers are left in memory.
/// </summary>
public sealed class Mem2RegPass : IIrFunctionPass
{
    public bool Run(IrFunction function)
    {
        if (function.EntryBlock is null)
            return false;

        // Integer scalars only. A pointer local read before any write (the frontend has no
        // definite-assignment check, so that is reachable) would need a synthesized undef, and a
        // pointer-typed IrConstInt is ill-formed — leave pointers in memory for RLE to forward.
        var promotable = new HashSet<AllocaInstruction>(ReferenceEqualityComparer.Instance);
        foreach (var alloca in AllocaAnalysis.NonEscaping(function))
            if (alloca.Allocated.Kind is IrTypeKind.Int)
                promotable.Add(alloca);
        if (promotable.Count == 0)
            return false;

        var order = new Dominators(function);
        var frontier = order.DominanceFrontiers();

        // Phi placement: for each alloca, put a phi at the iterated dominance frontier of the blocks
        // that store to it. Track which phi belongs to which alloca and in which block.
        var phis = new Dictionary<
            IrBasicBlock,
            List<(AllocaInstruction Alloca, PhiInstruction Phi)>
        >(ReferenceEqualityComparer.Instance);
        foreach (var alloca in promotable)
            PlacePhis(alloca, function, frontier, phis);

        // Rename: a dominator-tree walk carrying the reaching definition of every alloca.
        var stacks = new Dictionary<AllocaInstruction, Stack<IrValue>>(
            ReferenceEqualityComparer.Instance
        );
        foreach (var alloca in promotable)
            stacks[alloca] = new Stack<IrValue>();
        var toRemove = new List<(IrBasicBlock Block, IrInstruction Instruction)>();
        Rename(function.EntryBlock, order, promotable, phis, stacks, toRemove);

        foreach (var (block, instruction) in toRemove)
            block.Instructions.Remove(instruction);
        foreach (var block in function.Blocks)
            block.Instructions.RemoveAll(i => i is AllocaInstruction a && promotable.Contains(a));

        return true;
    }

    private static void PlacePhis(
        AllocaInstruction alloca,
        IrFunction function,
        Dictionary<IrBasicBlock, HashSet<IrBasicBlock>> frontier,
        Dictionary<IrBasicBlock, List<(AllocaInstruction, PhiInstruction)>> phis
    )
    {
        var worklist = new Queue<IrBasicBlock>();
        var everOnWorklist = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks)
            if (StoresTo(block, alloca))
            {
                worklist.Enqueue(block);
                everOnWorklist.Add(block);
            }

        var hasPhi = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
        while (worklist.Count > 0)
        {
            var block = worklist.Dequeue();
            foreach (var df in frontier.GetValueOrDefault(block, []))
            {
                if (!hasPhi.Add(df))
                    continue;
                var phi = new PhiInstruction(alloca.Allocated) { Parent = df };
                df.Instructions.Insert(PhiCount(phis, df), phi);
                phis.TryAdd(df, []);
                phis[df].Add((alloca, phi));
                if (everOnWorklist.Add(df))
                    worklist.Enqueue(df);
            }
        }
    }

    private static int PhiCount(
        Dictionary<IrBasicBlock, List<(AllocaInstruction, PhiInstruction)>> phis,
        IrBasicBlock block
    ) => phis.TryGetValue(block, out var list) ? list.Count : 0;

    private static bool StoresTo(IrBasicBlock block, AllocaInstruction alloca)
    {
        foreach (var instruction in block.Instructions)
            if (
                instruction is StoreInstruction { Pointer: AllocaInstruction p }
                && ReferenceEquals(p, alloca)
            )
                return true;
        return false;
    }

    private static void Rename(
        IrBasicBlock block,
        Dominators order,
        HashSet<AllocaInstruction> promotable,
        Dictionary<IrBasicBlock, List<(AllocaInstruction, PhiInstruction)>> phis,
        Dictionary<AllocaInstruction, Stack<IrValue>> stacks,
        List<(IrBasicBlock, IrInstruction)> toRemove
    )
    {
        var pushed = new List<AllocaInstruction>();

        // Phis we inserted here are the new reaching definition of their alloca.
        if (phis.TryGetValue(block, out var blockPhis))
            foreach (var (alloca, phi) in blockPhis)
            {
                stacks[alloca].Push(phi);
                pushed.Add(alloca);
            }

        foreach (var instruction in block.Instructions)
        {
            switch (instruction)
            {
                case LoadInstruction { Pointer: AllocaInstruction a } load
                    when promotable.Contains(a):
                    IrOptimizer.ReplaceAllUses(BlockFunction(block), load, Reaching(stacks[a], a));
                    toRemove.Add((block, load));
                    break;
                case StoreInstruction { Pointer: AllocaInstruction a } store
                    when promotable.Contains(a):
                    stacks[a].Push(store.Value);
                    pushed.Add(a);
                    toRemove.Add((block, store));
                    break;
            }
        }

        // Feed this block's reaching definitions into successor phis.
        if (block.Terminator is { } terminator)
            foreach (var successor in terminator.Successors)
                if (phis.TryGetValue(successor, out var succPhis))
                    foreach (var (alloca, phi) in succPhis)
                        phi.AddIncoming(Reaching(stacks[alloca], alloca), block);

        foreach (var child in order.ChildrenOf(block))
            Rename(child, order, promotable, phis, stacks, toRemove);

        foreach (var alloca in pushed)
            stacks[alloca].Pop();
    }

    /// <summary>The reaching definition on top of the stack, or zero when the (integer) slot is read
    /// before any write — the C# default, and well-typed since only integer allocas are promoted.</summary>
    private static IrValue Reaching(Stack<IrValue> stack, AllocaInstruction alloca) =>
        stack.Count > 0 ? stack.Peek() : new IrConstInt(alloca.Allocated, 0);

    private static IrFunction BlockFunction(IrBasicBlock block) => block.Parent;
}
