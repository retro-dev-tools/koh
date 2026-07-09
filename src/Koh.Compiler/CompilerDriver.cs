using Koh.Compiler.Backends;
using Koh.Compiler.Frontends;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;

namespace Koh.Compiler;

/// <summary>
/// Orchestrates one compilation: source through a frontend to IR, then through a backend to
/// an <see cref="EmitModel"/>. The backend owns legalization and instruction selection, so
/// the driver stays a thin, target-agnostic pipeline. The resulting <see cref="EmitModel"/>
/// is written to <c>.kobj</c> and handed to <c>Koh.Linker.Core</c> exactly like assembler
/// output.
/// </summary>
public static class CompilerDriver
{
    /// <summary>
    /// Compile <paramref name="source"/> with the given frontend and backend. Diagnostics from
    /// both phases accumulate into <paramref name="diagnostics"/>. The IR is run through the
    /// target-independent optimizer between the two phases unless <paramref name="optimize"/> is
    /// false (useful for debugging codegen against un-optimized IR). Optimization is skipped when
    /// the frontend already reported errors, since the IR may be incomplete.
    /// </summary>
    public static EmitModel Compile(
        IFrontend frontend,
        IBackend backend,
        SourceText source,
        DiagnosticBag diagnostics,
        bool optimize = true
    )
    {
        var module = frontend.Lower(source, diagnostics);
        if (optimize && diagnostics.ToList().Count == 0)
            IrOptimizer.Optimize(module);
        return backend.Compile(module, diagnostics);
    }
}
