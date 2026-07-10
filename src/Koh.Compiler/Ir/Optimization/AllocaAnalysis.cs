namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Classifies a function's <c>alloca</c>s for the memory passes. An alloca is <em>non-escaping</em>
/// when every use is the pointer operand of a <c>load</c>/<c>store</c> — its address is never taken
/// (stored as a value, <c>gep</c>'d, call-passed, returned). Such an alloca can't alias any other
/// pointer, so only its own direct load/stores touch it, which is what makes store→load forwarding
/// and dead-store elimination sound. Scalar locals are non-escaping; arrays/structs (via <c>gep</c>)
/// and address-taken locals escape.
/// </summary>
internal static class AllocaAnalysis
{
    /// <summary>The allocas in <paramref name="function"/> whose address never escapes.</summary>
    public static HashSet<AllocaInstruction> NonEscaping(IrFunction function)
    {
        var allocas = new HashSet<AllocaInstruction>(ReferenceEqualityComparer.Instance);
        var escaped = new HashSet<AllocaInstruction>(ReferenceEqualityComparer.Instance);

        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
        {
            if (instruction is AllocaInstruction alloca)
                allocas.Add(alloca);

            switch (instruction)
            {
                // The pointer operand of a load never escapes the alloca.
                case LoadInstruction:
                    break;
                // A store's pointer operand is fine; its value operand takes the address.
                case StoreInstruction store:
                    if (store.Value is AllocaInstruction stored)
                        escaped.Add(stored);
                    break;
                // Any other appearance (gep base/index, call arg, ret, condbr, ...) takes the address.
                default:
                    foreach (var operand in instruction.Operands)
                        if (operand is AllocaInstruction used)
                            escaped.Add(used);
                    break;
            }
        }

        allocas.ExceptWith(escaped);
        return allocas;
    }

    /// <summary>The allocas that appear as the pointer of at least one <c>load</c>.</summary>
    public static HashSet<AllocaInstruction> Loaded(IrFunction function)
    {
        var loaded = new HashSet<AllocaInstruction>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
            if (instruction is LoadInstruction { Pointer: AllocaInstruction alloca })
                loaded.Add(alloca);
        return loaded;
    }
}
