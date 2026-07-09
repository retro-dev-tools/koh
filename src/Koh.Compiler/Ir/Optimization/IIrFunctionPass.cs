namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// A transform over a single function. Passes are target-independent IR-to-IR rewrites run by
/// <see cref="IrOptimizer"/> between the frontend and the backend. A pass must preserve IR
/// validity (an optimized function still verifies) and program semantics.
/// </summary>
public interface IIrFunctionPass
{
    /// <summary>Rewrite <paramref name="function"/> in place; return true if anything changed.</summary>
    bool Run(IrFunction function);
}
