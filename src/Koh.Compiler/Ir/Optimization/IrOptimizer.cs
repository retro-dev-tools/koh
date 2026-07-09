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
        new SimplifyCfgPass(),
        new RedundantLoadEliminationPass(),
        new DeadStoreEliminationPass(),
        new DeadCodeEliminationPass(),
    ];

    /// <summary>Optimize every defined function in <paramref name="module"/> in place.</summary>
    public static void Optimize(IrModule module)
    {
        foreach (var function in module.Functions)
        {
            if (function.IsExternal || function.Blocks.Count == 0)
                continue;
            OptimizeFunction(function);
        }
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
