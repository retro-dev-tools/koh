using Koh.Lsp.Config;

namespace Koh.Lsp.Tests.Config;

public class KohProjectFileLoaderTests
{
    private static string CreateTempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "koh-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteConfig(string folder, string yaml)
    {
        File.WriteAllText(Path.Combine(folder, "koh.yaml"), yaml);
    }

    [Test]
    public async Task ValidSingleProject_ReturnsConfigured()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game
                entrypoint: src/game.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Configured>();
        var configured = (KohConfigLoadResult.Configured)result;
        await Assert.That(configured.Projects.Count).IsEqualTo(1);
        await Assert.That(configured.Projects[0].Name).IsEqualTo("game");
        await Assert.That(configured.Projects[0].Entrypoint)
            .IsEqualTo(Path.GetFullPath(Path.Combine(folder, "src", "game.asm")));
        await Assert.That(configured.Mode).IsEqualTo(FolderMode.Configured);
    }

    [Test]
    public async Task ValidMultiProject_ReturnsConfiguredWithAll()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game
                entrypoint: src/game.asm
              - name: engine
                entrypoint: src/engine.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Configured>();
        var configured = (KohConfigLoadResult.Configured)result;
        await Assert.That(configured.Projects.Count).IsEqualTo(2);
        await Assert.That(configured.Projects[0].Name).IsEqualTo("game");
        await Assert.That(configured.Projects[1].Name).IsEqualTo("engine");
    }

    [Test]
    public async Task MissingConfigFile_ReturnsMissing()
    {
        var folder = CreateTempFolder();

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Missing>();
        var missing = (KohConfigLoadResult.Missing)result;
        await Assert.That(missing.Mode).IsEqualTo(FolderMode.Heuristic);
    }

    [Test]
    public async Task InvalidYamlSyntax_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game
                entrypoint: [[[invalid
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Mode).IsEqualTo(FolderMode.InvalidConfiguration);
        await Assert.That(invalid.Errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task MissingVersion_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            projects:
              - name: game
                entrypoint: src/game.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors).Contains(new ConfigValidationError("Missing required field: 'version'."));
    }

    [Test]
    public async Task MissingProjects_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors).Contains(new ConfigValidationError("Missing required field: 'projects'."));
    }

    [Test]
    public async Task EmptyProjects_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects: []
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors).Contains(new ConfigValidationError("'projects' must not be empty."));
    }

    [Test]
    public async Task MissingProjectName_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - entrypoint: src/game.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors).Contains(new ConfigValidationError("projects[0]: Missing required field 'name'."));
    }

    [Test]
    public async Task MissingProjectEntrypoint_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors).Contains(new ConfigValidationError("projects[0]: Missing required field 'entrypoint'."));
    }

    [Test]
    public async Task DuplicateProjectNames_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game
                entrypoint: src/game.asm
              - name: game
                entrypoint: src/other.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors).Contains(new ConfigValidationError("Duplicate project name: 'game'."));
    }

    [Test]
    public async Task DuplicateNormalizedEntrypoints_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game1
                entrypoint: src/game.asm
              - name: game2
                entrypoint: src/../src/game.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors.Count).IsGreaterThan(0);
        await Assert.That(invalid.Errors.Any(e => e.Message.Contains("Duplicate entrypoint"))).IsTrue();
    }

    [Test]
    public async Task RelativePathNormalization_ProducesAbsolutePath()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game
                entrypoint: src/game.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Configured>();
        var configured = (KohConfigLoadResult.Configured)result;
        var entrypoint = configured.Projects[0].Entrypoint;

        await Assert.That(Path.IsPathRooted(entrypoint)).IsTrue();
        await Assert.That(entrypoint).IsEqualTo(Path.GetFullPath(entrypoint));
    }

    [Test]
    public async Task InvalidConfig_BlocksHeuristicFallback()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects: []
            """);

        var result = KohProjectFileLoader.Load(folder);

        // When config is invalid, mode must be InvalidConfiguration, NOT Heuristic.
        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Mode).IsEqualTo(FolderMode.InvalidConfiguration);
        await Assert.That(invalid.Mode).IsNotEqualTo(FolderMode.Heuristic);
    }

    [Test]
    public async Task UnsupportedVersion_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 99
            projects:
              - name: game
                entrypoint: src/game.asm
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors.Any(e => e.Message.Contains("Unsupported config version"))).IsTrue();
    }

    [Test]
    public async Task EmptyFile_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, "");

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
    }

    [Test]
    public async Task AbsoluteEntrypoint_ReturnsInvalid()
    {
        var folder = CreateTempFolder();
        WriteConfig(folder, """
            version: 1
            projects:
              - name: game
                entrypoint: /etc/passwd
            """);

        var result = KohProjectFileLoader.Load(folder);

        await Assert.That(result).IsTypeOf<KohConfigLoadResult.Invalid>();
        var invalid = (KohConfigLoadResult.Invalid)result;
        await Assert.That(invalid.Errors.Any(e => e.Message.Contains("relative path"))).IsTrue();
    }
}
