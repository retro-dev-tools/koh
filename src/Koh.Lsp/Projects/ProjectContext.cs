using Koh.Core;

namespace Koh.Lsp.Projects;

/// <summary>
/// Represents a single compilation unit rooted at an entrypoint file.
/// Each entrypoint produces its own isolated <see cref="Compilation"/>
/// with transitive includes resolved through the workspace overlay resolver.
/// </summary>
internal sealed class ProjectContext
{
    /// <summary>
    /// Stable identifier for this project context, generated once on creation.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Human-readable project name. For configured projects this comes from koh.yaml;
    /// for heuristic projects it is derived from the entrypoint filename.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Absolute, normalized path to the entrypoint file.
    /// </summary>
    public string EntrypointPath { get; }

    /// <summary>
    /// All files reachable from the entrypoint via INCLUDE directives (including the entrypoint itself).
    /// </summary>
    public IReadOnlySet<string> ReachableFiles { get; private set; }

    /// <summary>
    /// The compilation instance for this project context. Rebuilt when affected files change.
    /// </summary>
    public Compilation Compilation { get; private set; }

    /// <summary>
    /// Monotonically increasing version number, incremented on each rebuild.
    /// </summary>
    public int GraphVersion { get; private set; }

    public ProjectContext(
        string name,
        string entrypointPath,
        IReadOnlySet<string> reachableFiles,
        Compilation compilation)
    {
        Id = Guid.NewGuid();
        Name = name;
        EntrypointPath = entrypointPath;
        ReachableFiles = reachableFiles;
        Compilation = compilation;
        GraphVersion = 1;
    }

    /// <summary>
    /// Updates the compilation and reachable file set, incrementing the graph version.
    /// </summary>
    public void Update(IReadOnlySet<string> reachableFiles, Compilation compilation)
    {
        ReachableFiles = reachableFiles;
        Compilation = compilation;
        GraphVersion++;
    }
}
