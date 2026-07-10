using Koh.Compiler.Backends;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.CSharp;

namespace Koh.Compiler;

/// <summary>
/// The single place frontends and backends are wired in. This hand-maintained list is the
/// AOT-safe registration mechanism (reflection-based scanning would break the NativeAOT LSP,
/// see <c>docs/decisions/lsp-aot-decision.md</c>). A later source generator can populate
/// these lists by scanning <c>Frontends/*/</c> and <c>Backends/*/</c> at build time, so that
/// adding a directory is the entire gesture; until then, add one line here.
/// </summary>
public static class CompilerRegistry
{
    public static IReadOnlyList<IFrontend> Frontends { get; } = [new CSharpFrontend()];

    public static IReadOnlyList<IBackend> Backends { get; } = [new Sm83Backend()];

    public static IFrontend? FrontendForExtension(string extension) =>
        Frontends.FirstOrDefault(f =>
            f.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
        );

    public static IBackend? BackendByName(string name) =>
        Backends.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)
        );
}
