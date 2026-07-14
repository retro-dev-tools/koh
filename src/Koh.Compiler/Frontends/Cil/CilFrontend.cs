using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// The CIL frontend (see <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>): reads a
/// game's compiled assembly and its references with Mono.Cecil — a resolved object model (operands
/// already bound to <see cref="TypeReference"/>/<see cref="MethodReference"/>) rather than
/// hand-decoded metadata blobs, and pure metadata reading, so it stays AOT-safe. Unlike
/// <see cref="Koh.Compiler.Frontends.CSharp.CSharpFrontend"/>, this frontend is assembly-driven: it
/// requires a <see cref="CompilerInput"/> built via <see cref="CompilerInput.FromAssembly"/>, never
/// <see cref="CompilerInput.FromSource"/>.
/// </summary>
public sealed class CilFrontend : IFrontend
{
    public string Name => "cil";

    public IReadOnlyList<string> Extensions => [".dll"];

    /// <summary>
    /// <see cref="IFrontend"/> entry point. Reports a diagnostic — never throws — for a shape this
    /// frontend cannot consume (source-text input) or an assembly path that is missing or does not
    /// exist on disk (the caller controls which frontend runs on which input; a mismatch is
    /// user-reachable, e.g. a misconfigured build). Otherwise loads the module with Mono.Cecil and
    /// hands it to <see cref="CilModuleLowerer"/> (phase 1: see
    /// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>).
    /// </summary>
    public IrModule Lower(CompilerInput input, DiagnosticBag diagnostics)
    {
        var moduleName = input.FilePath.Length > 0 ? input.FilePath : "cil";
        var module = new IrModule(moduleName);

        if (input.Text is not null)
        {
            diagnostics.Report(
                default,
                $"The '{Name}' frontend requires an assembly-only input, but '{input.FilePath}' "
                    + "was given as source text.",
                DiagnosticSeverity.Error,
                input.FilePath
            );
            return module;
        }

        if (input.FilePath.Length == 0 || !File.Exists(input.FilePath))
        {
            diagnostics.Report(
                default,
                $"The '{Name}' frontend requires a compiled assembly, but "
                    + $"'{input.FilePath}' is missing or does not exist.",
                DiagnosticSeverity.Error,
                input.FilePath
            );
            return module;
        }

        using var resolver = BuildResolver(input);
        var readerParameters = new ReaderParameters { AssemblyResolver = resolver };
        try
        {
            using var cecilModule = ModuleDefinition.ReadModule(input.FilePath, readerParameters);
            CilModuleLowerer.Lower(cecilModule, module, diagnostics);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            diagnostics.Report(
                default,
                $"The '{Name}' frontend could not read '{input.FilePath}': {ex.Message}",
                DiagnosticSeverity.Error,
                input.FilePath
            );
        }

        return module;
    }

    /// <summary>
    /// An assembly resolver whose search directories are the assembly's own directory plus every
    /// distinct directory among <see cref="CompilerInput.ReferencePaths"/> — so a reference (e.g.
    /// <c>Koh.GameBoy.dll</c>) resolves regardless of where the game's own output lives.
    /// </summary>
    private static DefaultAssemblyResolver BuildResolver(CompilerInput input)
    {
        var resolver = new DefaultAssemblyResolver();
        var searchDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ownDirectory = Path.GetDirectoryName(Path.GetFullPath(input.FilePath));
        if (!string.IsNullOrEmpty(ownDirectory))
            searchDirectories.Add(ownDirectory);

        foreach (var referencePath in input.ReferencePaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(referencePath));
            if (!string.IsNullOrEmpty(dir))
                searchDirectories.Add(dir);
        }

        foreach (var dir in searchDirectories)
            resolver.AddSearchDirectory(dir);

        return resolver;
    }
}
