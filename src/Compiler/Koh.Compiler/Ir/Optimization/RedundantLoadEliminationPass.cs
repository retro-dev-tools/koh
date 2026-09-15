namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Within a basic block, forwards the value last written to (or read from) a non-escaping scalar
/// alloca to later loads of it and deletes those loads (store→load and load→load forwarding) — the
/// workhorse that turns the frontend's per-local alloca/load/store back into direct SSA data flow.
/// Sound because a non-escaping alloca cannot alias anything else (see <see cref="AllocaAnalysis"/>),
/// so nothing between the write and the load can change it. Intra-block only, so no phi insertion.
/// </summary>
public sealed class RedundantLoadEliminationPass : IIrFunctionPass
{
    public bool Run(IrFunction function)
    {
        var promotable = AllocaAnalysis.NonEscaping(function);
        if (promotable.Count == 0)
            return false;

        var changed = false;
        foreach (var block in function.Blocks)
        {
            // The current known value held in each tracked alloca's slot, as of this point in the block.
            var known = new Dictionary<AllocaInstruction, IrValue>(
                ReferenceEqualityComparer.Instance
            );
            var remove = new List<IrInstruction>();

            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case StoreInstruction { Pointer: AllocaInstruction p } store
                        when promotable.Contains(p):
                        known[p] = store.Value;
                        break;

                    case LoadInstruction { Pointer: AllocaInstruction p } load
                        when promotable.Contains(p):
                        if (known.TryGetValue(p, out var value))
                        {
                            IrOptimizer.ReplaceAllUses(function, load, value);
                            remove.Add(load);
                            changed = true;
                        }
                        else
                        {
                            // First read of the slot in this block: later reads forward to this load.
                            known[p] = load;
                        }
                        break;
                }
            }

            foreach (var instruction in remove)
                block.Instructions.Remove(instruction);
        }

        return changed;
    }
}
