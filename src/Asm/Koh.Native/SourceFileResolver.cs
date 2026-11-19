namespace Koh.Native;

public interface ISourceFileResolver
{
    bool FileExists(string path);
    string ReadAllText(string path);
    byte[] ReadAllBytes(string path);
    string ResolvePath(string currentFile, string includedPath);
    IReadOnlyList<string> GetResolutionRoots(string currentFile) => [];
    IReadOnlyDictionary<string, string> GetTextOverlays() => new Dictionary<string, string>();
    IReadOnlyDictionary<string, byte[]> GetBinaryOverlays() => new Dictionary<string, byte[]>();
}

public sealed class FileSystemResolver(string? basePath = null) : ISourceFileResolver
{
    public IReadOnlyList<string> GetResolutionRoots(string currentFile) =>
        [Path.GetFullPath(basePath ?? Environment.CurrentDirectory)];

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public string ResolvePath(string currentFile, string includedPath)
    {
        var candidate = Path.GetFullPath(
            Path.Combine(basePath ?? Environment.CurrentDirectory, includedPath)
        );
        return File.Exists(candidate)
            ? candidate
            : Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(currentFile) ?? ".", includedPath)
            );
    }
}
