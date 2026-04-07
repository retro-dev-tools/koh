namespace Koh.Lsp.Config;

/// <summary>
/// Describes how a workspace folder's project configuration was determined.
/// </summary>
internal enum FolderMode
{
    /// <summary>A valid koh.yaml was found and parsed successfully.</summary>
    Configured,

    /// <summary>No koh.yaml was found; projects will be discovered heuristically.</summary>
    Heuristic,

    /// <summary>A koh.yaml was found but contains errors. No fallback to heuristics.</summary>
    InvalidConfiguration,
}

/// <summary>
/// A single project definition from koh.yaml.
/// </summary>
internal sealed record KohProjectDefinition
{
    public required string Name { get; init; }

    /// <summary>
    /// Absolute, normalized path to the project entrypoint file.
    /// </summary>
    public required string Entrypoint { get; init; }
}

/// <summary>
/// Raw YAML model matching the koh.yaml schema. Used only for deserialization.
/// </summary>
internal sealed class KohProjectFileYaml
{
    public int? Version { get; set; }
    public List<KohProjectEntryYaml>? Projects { get; set; }
}

/// <summary>
/// Raw YAML model for a single project entry.
/// </summary>
internal sealed class KohProjectEntryYaml
{
    public string? Name { get; set; }
    public string? Entrypoint { get; set; }
}

/// <summary>
/// A structured validation error produced during config loading.
/// </summary>
internal sealed record ConfigValidationError(string Message);

/// <summary>
/// Result of attempting to load koh.yaml from a workspace folder.
/// </summary>
internal abstract record KohConfigLoadResult
{
    /// <summary>koh.yaml was found and is valid.</summary>
    internal sealed record Configured(IReadOnlyList<KohProjectDefinition> Projects) : KohConfigLoadResult
    {
        public FolderMode Mode => FolderMode.Configured;
    }

    /// <summary>No koh.yaml was found.</summary>
    internal sealed record Missing : KohConfigLoadResult
    {
        public FolderMode Mode => FolderMode.Heuristic;
    }

    /// <summary>koh.yaml was found but is invalid.</summary>
    internal sealed record Invalid(IReadOnlyList<ConfigValidationError> Errors) : KohConfigLoadResult
    {
        public FolderMode Mode => FolderMode.InvalidConfiguration;
    }
}
