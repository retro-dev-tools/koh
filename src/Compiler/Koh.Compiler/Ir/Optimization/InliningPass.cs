namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Inlines calls to small single-block leaf functions (the tiny accessors a game is full of),
/// erasing the SM83 call cost — arg marshalling through <c>ArgScratch</c>, static-frame save/restore,
/// <c>ReturnScratch</c> — and exposing the body to the rest of the optimizer. A callee qualifies only
/// when it is one block ending in <c>ret</c>, calls nothing (a leaf, so no recursion and the process
/// terminates), is not external/interrupt/entry/banked, and is at most <see cref="MaxCalleeSize"/>
/// instructions; its straight-line body is then cloned with an operand remap ahead of the call.
/// </summary>
public sealed class InliningPass
{
    /// <summary>Upper bound on a callee's instruction count, to keep inlining from bloating the ROM.</summary>
    private const int MaxCalleeSize = 16;

    /// <summary>Inline every eligible call in the module, to a fixed point. Returns true if anything changed.</summary>
    public bool Run(IrModule module)
    {
        var inlinable = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        foreach (var function in module.Functions)
            if (IsInlinable(function))
                inlinable.Add(function);
        if (inlinable.Count == 0)
            return false;

        var changed = false;
        foreach (var caller in module.Functions)
        {
            if (caller.IsExternal)
                continue;
            foreach (var block in caller.Blocks)
                changed |= InlineCallsIn(block, inlinable);
        }
        return changed;
    }

    private static bool IsInlinable(IrFunction function)
    {
        if (
            function.IsExternal
            || function.IsEntry
            || function.InterruptVector is not null
            || function.Bank is not null
            || function.Blocks.Count != 1
        )
            return false;

        var block = function.Blocks[0];
        if (block.Terminator is not RetInstruction)
            return false;
        if (block.Instructions.Count - 1 > MaxCalleeSize)
            return false;

        // Every non-terminator instruction must be a leaf, clonable operation (no calls, no phis).
        for (var i = 0; i < block.Instructions.Count - 1; i++)
            if (!IsClonable(block.Instructions[i]))
                return false;
        return true;
    }

    private static bool IsClonable(IrInstruction instruction) =>
        instruction
            is BinaryInstruction
                or CompareInstruction
                or ConvInstruction
                or AllocaInstruction
                or LoadInstruction
                or StoreInstruction
                or GetElementPtrInstruction
                or IntrinsicInstruction;

    private static bool InlineCallsIn(IrBasicBlock block, HashSet<IrFunction> inlinable)
    {
        var changed = false;
        for (var i = 0; i < block.Instructions.Count; i++)
        {
            if (
                block.Instructions[i] is not CallInstruction call
                || !inlinable.Contains(call.Callee)
            )
                continue;

            i += InlineOneCall(block, i, call) - 1; // advance past the spliced-in body
            changed = true;
        }
        return changed;
    }

    /// <summary>Splice <paramref name="call"/>'s callee body into <paramref name="block"/> at index
    /// <paramref name="index"/>, replace uses of the call result with the returned value, and remove
    /// the call. Returns the number of instructions now occupying the call's old slot.</summary>
    private static int InlineOneCall(IrBasicBlock block, int index, CallInstruction call)
    {
        var callee = call.Callee;
        var body = callee.Blocks[0];
        var remap = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);

        IrValue Map(IrValue v)
        {
            for (var p = 0; p < callee.Parameters.Count; p++)
                if (ReferenceEquals(callee.Parameters[p], v))
                    return call.Arguments[p];
            return remap.TryGetValue(v, out var mapped) ? mapped : v;
        }

        var cloned = new List<IrInstruction>();
        for (var i = 0; i < body.Instructions.Count - 1; i++)
        {
            var original = body.Instructions[i];
            var clone = Clone(original, Map);
            clone.Parent = block;
            clone.Source = call.Source ?? original.Source;
            remap[original] = clone;
            cloned.Add(clone);
        }

        block.Instructions.InsertRange(index, cloned);
        var callIndex = index + cloned.Count;

        var ret = (RetInstruction)body.Terminator!;
        if (ret.Value is { } returnValue)
            IrOptimizer.ReplaceAllUses(block.Parent, call, Map(returnValue));
        block.Instructions.RemoveAt(callIndex);

        return cloned.Count;
    }

    private static IrInstruction Clone(IrInstruction instruction, Func<IrValue, IrValue> map) =>
        instruction switch
        {
            BinaryInstruction b => new BinaryInstruction(b.Op, map(b.Left), map(b.Right)),
            CompareInstruction c => new CompareInstruction(c.Op, map(c.Left), map(c.Right)),
            ConvInstruction c => new ConvInstruction(c.Op, map(c.Operand), c.Type),
            AllocaInstruction a => new AllocaInstruction(a.Allocated),
            LoadInstruction l => new LoadInstruction(map(l.Pointer)),
            StoreInstruction s => new StoreInstruction(map(s.Value), map(s.Pointer)),
            GetElementPtrInstruction g => new GetElementPtrInstruction(
                map(g.BasePointer),
                map(g.Index),
                g.ElementType
            ),
            IntrinsicInstruction n => new IntrinsicInstruction(n.Intrinsic),
            _ => throw new InvalidOperationException(
                $"inliner cannot clone {instruction.GetType().Name}"
            ),
        };
}
