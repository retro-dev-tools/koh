using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// The hand-written SM83 backend. Per the design, it legalizes the IR for an 8-bit
/// accumulator machine (expanding multiply/wide-int ops), selects instructions via the
/// existing <c>Koh.Core.Encoding</c> table, statically allocates locals (NESFab-style, no
/// stack frames), emits far-call trampolines for cross-bank calls, and produces an
/// <see cref="EmitModel"/> with per-instruction line maps so the existing <c>.kdbg</c>
/// pipeline yields C#-source debugging. Codegen is Phase 2; this stub establishes the
/// registration, target facts, and contract. See
/// <c>docs/superpowers/specs/2026-07-05-csharp-frontend-compiler-platform-design.md</c>.
/// </summary>
public sealed class Sm83Backend : IBackend
{
    public string Name => "sm83";

    public TargetInfo Target => TargetInfo.Sm83;

    public EmitModel Compile(IrModule module, DiagnosticBag diagnostics) =>
        throw new NotImplementedException(
            "SM83 code generation is Phase 2 (see the compiler-platform design spec).");
}
