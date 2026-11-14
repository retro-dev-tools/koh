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

public sealed class VirtualFileResolver : ISourceFileResolver
{
    private readonly Dictionary<string, string> text = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> binary = new(StringComparer.OrdinalIgnoreCase);

    public void AddTextFile(string path, string content) => text[path] = content;

    public void AddBinaryFile(string path, byte[] content) => binary[path] = content;

    public bool FileExists(string path) => text.ContainsKey(path) || binary.ContainsKey(path);

    public string ReadAllText(string path) =>
        text.TryGetValue(path, out var value) ? value : throw new FileNotFoundException(path);

    public byte[] ReadAllBytes(string path) =>
        binary.TryGetValue(path, out var value)
            ? value
            : System.Text.Encoding.UTF8.GetBytes(ReadAllText(path));

    public string ResolvePath(string currentFile, string includedPath) => includedPath;

    public IReadOnlyDictionary<string, string> GetTextOverlays() =>
        new Dictionary<string, string>(text, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, byte[]> GetBinaryOverlays()
    {
        var result = text.ToDictionary(
            pair => pair.Key,
            pair => System.Text.Encoding.UTF8.GetBytes(pair.Value),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (path, bytes) in binary)
            result[path] = bytes.ToArray();
        return result;
    }
}
