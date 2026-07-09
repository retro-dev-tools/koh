namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Removes trivial phis: a phi with a single unique incoming value (ignoring incomings that are the
/// phi itself) is replaced by that value. Such phis arise constantly after <see cref="Mem2RegPass"/>
/// (a local written on only one path, or a loop-header phi whose only non-self input is its initial
/// value) and after <see cref="SimplifyCfgPass"/> prunes a predecessor edge. Iterated to a fixed
/// point because collapsing one phi can make another trivial.
/// </summary>
public sealed class TrivialPhiEliminationPass : IIrFunctionPass
{
    public string Name => "trivial-phi-elimination";

    public bool Run(IrFunction function)
    {
        var changedOverall = false;
        bool changed;
        do
        {
            changed = false;
            foreach (var block in function.Blocks)
            {
                var remove = new List<PhiInstruction>();
                foreach (var phi in block.Instructions.OfType<PhiInstruction>())
                {
                    if (TrivialValue(phi) is not { } value)
                        continue;
                    IrOptimizer.ReplaceAllUses(function, phi, value);
                    remove.Add(phi);
                    changed = true;
                }
                foreach (var phi in remove)
                    block.Instructions.Remove(phi);
            }
            changedOverall |= changed;
        } while (changed);

        return changedOverall;
    }

    /// <summary>The one value this phi always takes (ignoring self-references), or null if it merges
    /// two or more distinct values (or none — an incoming-less phi is left for the verifier to flag).</summary>
    private static IrValue? TrivialValue(PhiInstruction phi)
    {
        IrValue? unique = null;
        foreach (var (value, _) in phi.Incomings)
        {
            if (ReferenceEquals(value, phi))
                continue; // self-reference: a back-edge feeding the phi its own value
            if (unique is not null && !ReferenceEquals(unique, value))
                return null; // two distinct real inputs — a genuine merge
            unique = value;
        }
        return unique;
    }
}
