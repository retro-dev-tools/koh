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
    public bool Run(IrFunction function)
    {
        var changed = false;
        foreach (var block in function.Blocks)
        {
            // Per-block value numbering: a non-constant value gets a stable ordinal by identity; a
            // constant by (type, value) so equal literals from distinct objects share a number.
            var number = new Dictionary<IrValue, int>(ReferenceEqualityComparer.Instance);
            var constNumber = new Dictionary<(IrType, long), int>();
            var seen = new Dictionary<CseKey, IrInstruction>();
            var remove = new List<IrInstruction>();
            var next = 0;

            int Vn(IrValue v)
            {
                if (v is IrConstInt c)
                    return constNumber.TryGetValue((c.Type, c.Value), out var cn)
                        ? cn
                        : constNumber[(c.Type, c.Value)] = next++;
                return number.TryGetValue(v, out var n) ? n : number[v] = next++;
            }

            foreach (var instruction in block.Instructions)
            {
                if (KeyOf(instruction, Vn) is not { } key)
                    continue;
                if (seen.TryGetValue(key, out var first))
                {
                    IrOptimizer.ReplaceAllUses(function, instruction, first);
                    remove.Add(instruction);
                    changed = true;
                    continue; // do not number the removed result; its uses now point at `first`
                }
                seen[key] = instruction;
                Vn(instruction); // number this result for later operands
            }

            foreach (var instruction in remove)
                block.Instructions.Remove(instruction);
        }
        return changed;
    }

    /// <summary>A value-typed canonical key for a pure instruction: (kind, op, distinguishing type,
    /// operand value-numbers). The type must be a reference-stable one — an integer type (a singleton)
    /// or a gep's element type — never a gep's freshly-built pointer result type, which two equal geps
    /// would not share. Structurally-distinct types compare by reference, at worst missing a CSE.</summary>
    private readonly record struct CseKey(byte Kind, int Op, IrType Type, int A, int B);

    private static CseKey? KeyOf(IrInstruction instruction, Func<IrValue, int> vn) =>
        instruction switch
        {
            // A binary/compare's result type equals its operand types, so the operand numbers carry it.
            BinaryInstruction b => new CseKey(0, (int)b.Op, b.Type, vn(b.Left), vn(b.Right)),
            CompareInstruction c => new CseKey(1, (int)c.Op, c.Left.Type, vn(c.Left), vn(c.Right)),
            // A conversion is distinguished by its (singleton) target type.
            ConvInstruction c => new CseKey(2, (int)c.Op, c.Type, vn(c.Operand), 0),
            // A gep is fully determined by base, index, and element type; the pointer result follows.
            GetElementPtrInstruction g => new CseKey(
                3,
                0,
                g.ElementType,
                vn(g.BasePointer),
                vn(g.Index)
            ),
            _ => null,
        };
}
