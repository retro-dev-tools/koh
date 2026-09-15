namespace Koh.Compiler.Tests.TestSupport;

/// <summary>Repository paths for tests that read real sample sources.</summary>
internal static class TestRepo
{
    /// <summary>The nearest ancestor of the test binaries that holds <c>Koh.slnx</c>.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "could not locate the repository root (Koh.slnx)."
            );
    }
}
