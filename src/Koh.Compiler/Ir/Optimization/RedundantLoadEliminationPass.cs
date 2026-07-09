namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Within a basic block, forwards the value most recently written to (or read from) a non-escaping
/// scalar alloca directly to later loads of the same alloca, then deletes those loads:
///
/// <code>
///   store %v, %p     ; %p a non-escaping alloca
///   %a = load %p     ->  %a replaced by %v
///   %b = load %p     ->  %b replaced by %v
/// </code>
///
/// Two loads of the same unmodified alloca likewise collapse to one (load→load forwarding). This is
/// the workhorse for the C# frontend, which materializes every scalar local as an alloca with
/// explicit load/store; forwarding turns those back into direct SSA data flow that the constant
/// folder and DCE can then act on.
///
/// Sound because a non-escaping alloca cannot alias anything else (see <see cref="AllocaAnalysis"/>),
/// so nothing between the write and the load — not a call, not a store through another pointer — can
/// change its value. Forwarding is intra-block only: the known value is dropped at each block
/// boundary, so no cross-edge reasoning (or phi insertion) is needed.
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
