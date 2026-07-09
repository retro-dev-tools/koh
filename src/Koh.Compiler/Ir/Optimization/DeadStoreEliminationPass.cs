namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Removes stores to a non-escaping alloca that is never loaded anywhere in the function. Such a
/// slot is write-only — its address never escapes, so no load (direct or aliased) can ever observe
/// what was written — making every store to it unobservable and therefore dead. Once the stores are
/// gone the alloca itself is unused and <see cref="DeadCodeEliminationPass"/> removes it.
///
/// This most often fires after <see cref="RedundantLoadEliminationPass"/> has forwarded and deleted
/// the last load of a local, leaving only its stores behind (e.g. a local assigned and then only
/// used through values the folder already propagated).
/// </summary>
public sealed class DeadStoreEliminationPass : IIrFunctionPass
{
    public string Name => "dead-store-elimination";

    public bool Run(IrFunction function)
    {
        var promotable = AllocaAnalysis.NonEscaping(function);
        if (promotable.Count == 0)
            return false;

        var loaded = AllocaAnalysis.Loaded(function);
        promotable.ExceptWith(loaded); // keep only the write-only allocas
        if (promotable.Count == 0)
            return false;

        var changed = false;
        foreach (var block in function.Blocks)
        {
            var removed = block.Instructions.RemoveAll(instruction =>
                instruction is StoreInstruction { Pointer: AllocaInstruction p }
                && promotable.Contains(p)
            );
            if (removed > 0)
                changed = true;
        }

        return changed;
    }
}
