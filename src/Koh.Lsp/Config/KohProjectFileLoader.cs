using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Koh.Lsp.Config;

/// <summary>
/// Loads and validates koh.yaml from a workspace folder root.
/// </summary>
internal static class KohProjectFileLoader
{
    private const string ConfigFileName = "koh.yaml";

    /// <summary>
    /// Attempts to load koh.yaml from the given workspace folder.
    /// </summary>
    public static KohConfigLoadResult Load(string workspaceFolderPath)
    {
        var configPath = Path.Combine(workspaceFolderPath, ConfigFileName);

        if (!File.Exists(configPath))
        {
            return new KohConfigLoadResult.Missing();
        }

        string yamlContent;
        try
        {
            yamlContent = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            return new KohConfigLoadResult.Invalid([
                new ConfigValidationError($"Failed to read {ConfigFileName}: {ex.Message}"),
            ]);
        }

        return Parse(yamlContent, workspaceFolderPath);
    }

    /// <summary>
    /// Parses and validates YAML content. Exposed for testing without filesystem.
    /// </summary>
    internal static KohConfigLoadResult Parse(string yamlContent, string workspaceFolderPath)
    {
        KohProjectFileYaml? yaml;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            yaml = deserializer.Deserialize<KohProjectFileYaml>(yamlContent);
        }
        catch (Exception ex)
        {
            return new KohConfigLoadResult.Invalid([
                new ConfigValidationError($"Invalid YAML syntax: {ex.Message}"),
            ]);
        }

        if (yaml is null)
        {
            return new KohConfigLoadResult.Invalid([
                new ConfigValidationError("Config file is empty."),
            ]);
        }

        return Validate(yaml, workspaceFolderPath);
    }

    private static KohConfigLoadResult Validate(KohProjectFileYaml yaml, string workspaceFolderPath)
    {
        var errors = new List<ConfigValidationError>();

        if (yaml.Version is null)
        {
            errors.Add(new ConfigValidationError("Missing required field: 'version'."));
        }
        else if (yaml.Version != 1)
        {
            errors.Add(new ConfigValidationError($"Unsupported config version: {yaml.Version}. Only version 1 is supported."));
        }

        if (yaml.Projects is null)
        {
            errors.Add(new ConfigValidationError("Missing required field: 'projects'."));
        }
        else if (yaml.Projects.Count == 0)
        {
            errors.Add(new ConfigValidationError("'projects' must not be empty."));
        }
        else
        {
            ValidateProjects(yaml.Projects, errors);
        }

        if (errors.Count > 0)
        {
            return new KohConfigLoadResult.Invalid(errors);
        }

        // Build validated project definitions with normalized paths.
        var definitions = new List<KohProjectDefinition>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEntrypoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in yaml.Projects!)
        {
            var name = entry.Name!;
            var rawEntrypoint = entry.Entrypoint!;

            // Normalize: resolve relative to workspace folder, then get absolute path.
            var absoluteEntrypoint = Path.GetFullPath(
                Path.Combine(workspaceFolderPath, rawEntrypoint.Replace('/', Path.DirectorySeparatorChar)));

            if (!seenNames.Add(name))
            {
                errors.Add(new ConfigValidationError($"Duplicate project name: '{name}'."));
            }

            if (!seenEntrypoints.Add(absoluteEntrypoint))
            {
                errors.Add(new ConfigValidationError($"Duplicate entrypoint (after normalization): '{rawEntrypoint}'."));
            }

            definitions.Add(new KohProjectDefinition
            {
                Name = name,
                Entrypoint = absoluteEntrypoint,
            });
        }

        if (errors.Count > 0)
        {
            return new KohConfigLoadResult.Invalid(errors);
        }

        return new KohConfigLoadResult.Configured(definitions);
    }

    private static void ValidateProjects(List<KohProjectEntryYaml> projects, List<ConfigValidationError> errors)
    {
        for (var i = 0; i < projects.Count; i++)
        {
            var entry = projects[i];
            var prefix = $"projects[{i}]";

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                errors.Add(new ConfigValidationError($"{prefix}: Missing required field 'name'."));
            }

            if (string.IsNullOrWhiteSpace(entry.Entrypoint))
            {
                errors.Add(new ConfigValidationError($"{prefix}: Missing required field 'entrypoint'."));
            }
        }
    }
}
