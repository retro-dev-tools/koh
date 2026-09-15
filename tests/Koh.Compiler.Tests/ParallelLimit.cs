using TUnit.Core.Interfaces;

[assembly: ParallelLimiter<Koh.Compiler.Tests.CompilerTestParallelLimit>]

namespace Koh.Compiler.Tests;

/// <summary>
/// Caps how many of this assembly's tests run at once.
///
/// This project is not ordinary unit testing: a typical case here Roslyn-compiles a C#
/// source fixture to a real assembly on disk, lowers it through the CIL frontend and the
/// SM83 backend, links a ROM, and then runs a <c>GameBoySystem</c> over the result. Each
/// one holds a Roslyn compilation and an emulator's RAM arrays live at the same time.
/// Multiplied by TUnit's default unbounded parallelism across 522 cases, that is enough
/// to exhaust memory on a normal desktop and take the whole machine down — not just the
/// test run.
///
/// Four is a deliberate compromise: enough to keep the suite from crawling on a build
/// server, low enough that a developer machine survives it. Raise it only if you have
/// measured the peak working set, not because the suite feels slow.
/// </summary>
public record CompilerTestParallelLimit : IParallelLimit
{
    public int Limit => 4;
}
