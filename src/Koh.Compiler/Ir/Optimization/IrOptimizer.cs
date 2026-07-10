namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Runs the IR optimization pipeline over a module. Correctness-first: every pass is a
/// semantics- and validity-preserving rewrite, so the optimized module still passes
/// <see cref="IrVerifier"/> and produces identical program behavior — only smaller/faster code.
///
/// The pipeline is a small, fixed list of function-local passes iterated to a fixed point:
/// folding a constant can expose a dead instruction, and deleting a dead instruction can expose
/// another constant, so the two reinforce each other until neither changes anything.
/// </summary>
public static class IrOptimizer
{
    /// <summary>Bound on fixed-point iterations, as a guard against a mis-behaving pass that never
    /// converges. Real programs settle in one or two rounds; the cap only prevents an infinite loop.</summary>
    private const int MaxRounds = 16;

    // Ordered so each pass tends to expose work for the next, then the whole list re-runs to a fixed
    // point: folding turns branch conditions constant (→ SimplifyCfg), simplified control flow and
    // store→load forwarding expose more constants (→ ConstantFolding again) and unused code (→ DCE).
    private static readonly IIrFunctionPass[] Passes =
    [
        new ConstantFoldingPass(),
        new Mem2RegPass(),
        new TrivialPhiEliminationPass(),
        new StrengthReductionPass(),
        new SimplifyCfgPass(),
        new RedundantLoadEliminationPass(),
        new LocalCsePass(),
        new LoopInvariantCodeMotionPass(),
        new DeadStoreEliminationPass(),
        new DeadCodeEliminationPass(),
    ];

    /// <summary>Optimize every defined function in <paramref name="module"/> in place.</summary>
    public static void Optimize(IrModule module)
    {
        // Interprocedural first: splice tiny leaf callees into their call sites (to a fixed point,
        // which terminates because each inline removes a call and adds none), then drop functions no
        // longer reachable from the entry or an interrupt handler so they don't cost ROM.
        var inliner = new InliningPass();
        for (var round = 0; round < MaxRounds && inliner.Run(module); round++) { }
        RemoveUnreachableFunctions(module);

        foreach (var function in module.Functions)
        {
            if (function.IsExternal || function.Blocks.Count == 0)
                continue;
            OptimizeFunction(function);
        }

        // A per-function pass (constant-branch folding, DCE) can delete the last call to a function;
        // prune again so it doesn't cost ROM.
        RemoveUnreachableFunctions(module);
    }

    /// <summary>Remove functions unreachable from the entry or any interrupt handler through the call
    /// graph. External declarations are always kept (they may be resolved by the linker).</summary>
    private static void RemoveUnreachableFunctions(IrModule module)
    {
        // Only prune when the module designates an entry (a real compiled program always marks Main).
        // Without one — a library fragment or a single function under test — every function is a
        // potential root, so removing "callerless" functions would wrongly delete live code.
        if (!module.Functions.Any(f => f.IsEntry))
            return;

        var keep = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        var work = new Stack<IrFunction>();
        foreach (var function in module.Functions)
            if (function.IsEntry || function.InterruptVector is not null || function.IsExternal)
                if (keep.Add(function))
                    work.Push(function);

        while (work.Count > 0)
            foreach (var block in work.Pop().Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction is CallInstruction call && keep.Add(call.Callee))
                    work.Push(call.Callee);

        module.Functions.RemoveAll(f => !keep.Contains(f));
    }

    private static void OptimizeFunction(IrFunction function)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            var changed = false;
            foreach (var pass in Passes)
                changed |= pass.Run(function);
            if (!changed)
                return;
        }
    }

    /// <summary>
    /// Replace every use of <paramref name="from"/> in <paramref name="function"/> with
    /// <paramref name="to"/> (RAUW). <paramref name="to"/> must be type-compatible with what it
    /// replaces. Definitions are referenced by identity, so this rewrites operands of every
    /// instruction, including phi incomings and terminators.
    /// </summary>
    internal static void ReplaceAllUses(IrFunction function, IrValue from, IrValue to)
    {
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
            instruction.ReplaceOperand(from, to);
    }
}
