using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;

namespace Koh.Compiler.Backends;

/// <summary>
/// A code-generation backend for one target. Adding a target means adding a directory under
/// <c>Backends/</c> with one implementation of this interface and registering it in
/// <see cref="CompilerRegistry"/>. A backend legalizes the IR for its hardware, selects
/// instructions, allocates storage, and produces an <see cref="EmitModel"/> — the same
/// frozen object-code contract the assembler emits, so everything downstream (linker,
/// <c>.sym</c>, <c>.kdbg</c>, emulator, DAP debugger) is reused unchanged.
/// </summary>
public interface IBackend
{
    /// <summary>Stable identifier, e.g. "sm83".</summary>
    string Name { get; }

    /// <summary>Target facts (data layout, and — as the backend grows — registers/ABI).</summary>
    TargetInfo Target { get; }

    /// <summary>
    /// Lower an IR module to object code. Errors are reported into
    /// <paramref name="diagnostics"/>; <see cref="EmitModel.Success"/> reflects whether the
    /// result is usable by the linker.
    /// </summary>
    EmitModel Compile(IrModule module, DiagnosticBag diagnostics);
}
