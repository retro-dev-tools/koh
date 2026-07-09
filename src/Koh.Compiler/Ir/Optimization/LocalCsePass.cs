namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Intra-block common-subexpression elimination: within a basic block, a pure instruction whose
/// operation and operands exactly match an earlier one is replaced by that earlier result and
/// deleted. Non-constant operands are matched by SSA identity and constants by (type, value) — so a
/// literal index recomputed as a fresh constant still matches — coalescing recomputations the
/// frontend and earlier passes leave behind: most usefully repeated <c>gep</c> address arithmetic
/// for array and struct accesses (<c>a[i]</c> read then written), and arithmetic duplicated across
/// expressions.
///
/// Only value-pure, deterministic instructions are considered — <c>binary</c>, <c>icmp</c>,
/// <c>conv</c>, <c>gep</c>. <c>load</c> is excluded (memory may change between two loads;
/// <see cref="RedundantLoadEliminationPass"/> handles safe load reuse), and <c>alloca</c> is excluded
/// because each one names distinct storage. Staying within a block means the earlier instruction
/// always dominates the later one, so the replacement is always valid.
/// </summary>
public sealed class LocalCsePass : IIrFunctionPass
{
    public string Name => "local-cse";

    public bool Run(IrFunction function)
    {
        var changed = false;
        foreach (var block in function.Blocks)
        {
            // Per-block value numbering: a non-constant value gets a stable ordinal; a constant is
            // tokenized by (type, value) so equal literals from distinct objects still match.
            var number = new Dictionary<IrValue, int>(ReferenceEqualityComparer.Instance);
            var seen = new Dictionary<string, IrInstruction>(StringComparer.Ordinal);
            var remove = new List<IrInstruction>();

            string Token(IrValue v) =>
                v is IrConstInt c ? $"#{v.Type}:{c.Value}"
                : number.TryGetValue(v, out var n) ? $"%{n}"
                : $"%{number[v] = number.Count}";

            foreach (var instruction in block.Instructions)
            {
                var key = KeyOf(instruction, Token);
                if (key is not null && seen.TryGetValue(key, out var first))
                {
                    IrOptimizer.ReplaceAllUses(function, instruction, first);
                    remove.Add(instruction);
                    changed = true;
                    continue; // do not number the removed result; its uses now point at `first`
                }

                if (key is not null)
                    seen[key] = instruction;
                Token(instruction); // number this result for later operands
            }

            foreach (var instruction in remove)
                block.Instructions.Remove(instruction);
        }
        return changed;
    }

    /// <summary>A canonical key for a pure instruction, or null if it is not a CSE candidate.</summary>
    private static string? KeyOf(IrInstruction instruction, Func<IrValue, string> token) =>
        instruction switch
        {
            BinaryInstruction b => $"bin:{(int)b.Op}:{b.Type}:{token(b.Left)}:{token(b.Right)}",
            CompareInstruction c => $"cmp:{(int)c.Op}:{token(c.Left)}:{token(c.Right)}",
            ConvInstruction c => $"conv:{(int)c.Op}:{c.Type}:{token(c.Operand)}",
            GetElementPtrInstruction g =>
                $"gep:{g.Type}:{g.ElementType}:{token(g.BasePointer)}:{token(g.Index)}",
            _ => null,
        };
}
