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
    // NarrowPass runs right after phi construction settles (a byte loop counter's compare/increment
    // only exposes its zext/trunc chain once Mem2RegPass has promoted the counter's alloca away) and
    // before StrengthReduction, so a newly-narrowed multiply is still eligible for the shift/mask
    // reduction. Internal (not private) so tests can measure the pass's effect by running a filtered
    // copy of this list — see NarrowPassTests/CilNarrowingEndToEndTests.
    // RedundantConversionEliminationPass runs immediately after NarrowPass so it cleans up the
    // trunc(zext/sext(x)) identity pairs NarrowPass's demotion leaves behind each fixed-point round
    // (CIL's implicit re-widen on every narrow-local read) — DeadCodeEliminationPass then removes the
    // orphaned zext/sext.
    internal static readonly IIrFunctionPass[] Passes =
    [
        new ConstantFoldingPass(),
        new Mem2RegPass(),
        new TrivialPhiEliminationPass(),
        new NarrowPass(),
        new RedundantConversionEliminationPass(),
        new StrengthReductionPass(),
        new SimplifyCfgPass(),
        new RedundantLoadEliminationPass(),
        new LocalCsePass(),
        new LoopInvariantCodeMotionPass(),
        new DeadStoreEliminationPass(),
        new RedundantBankSelectEliminationPass(),
        new DeadCodeEliminationPass(),
    ];

    /// <summary>Optimize every defined function in <paramref name="module"/> in place.</summary>
    public static void Optimize(IrModule module) => Optimize(module, Passes);

    /// <summary>
    /// Same pipeline as <see cref="Optimize(IrModule)"/>, but over a caller-supplied pass list.
    /// Internal — its only caller is test code that needs to measure one pass's effect by running the
    /// pipeline with and without it (e.g. NarrowPass's ROM-size/cycle-count delta); production code
    /// always goes through the public overload's fixed <see cref="Passes"/> list.
    /// </summary>
    internal static void Optimize(IrModule module, IReadOnlyList<IIrFunctionPass> passes)
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
            OptimizeFunction(function, passes);
        }

        // A per-function pass (constant-branch folding, DCE) can delete the last call to a function;
        // prune again so it doesn't cost ROM.
        RemoveUnreachableFunctions(module);
    }

    /// <summary>Remove functions unreachable from the entry or any interrupt handler through the call
    /// graph. External declarations are always kept (they may be resolved by the linker). When
    /// <paramref name="removable"/> is given, only unreachable functions it selects are dropped (e.g. the
    /// frontend prunes just the appended <c>__</c>-runtime, leaving user dead code to later passes).</summary>
    internal static void RemoveUnreachableFunctions(
        IrModule module,
        Func<IrFunction, bool>? removable = null
    )
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

        module.Functions.RemoveAll(f => !keep.Contains(f) && (removable is null || removable(f)));
    }

    private static void OptimizeFunction(IrFunction function, IReadOnlyList<IIrFunctionPass> passes)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            var changed = false;
            foreach (var pass in passes)
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
