namespace Koh.Core;

/// <summary>
/// Abstraction over the file system for INCLUDE/INCBIN directives.
/// Tests inject a virtual implementation; production uses the real file system.
/// </summary>
public interface ISourceFileResolver
{
    bool FileExists(string path);
    string ReadAllText(string path);
    byte[] ReadAllBytes(string path);
    string ResolvePath(string currentFile, string includedPath);
}

/// <summary>
/// Real file system implementation for production use.
/// Optionally accepts a base path used as the CWD for INCLUDE resolution.
/// </summary>
public sealed class FileSystemResolver : ISourceFileResolver
{
    private readonly string? _basePath;

    public FileSystemResolver() { }

    /// <summary>
    /// Creates a resolver that uses <paramref name="basePath"/> as the working
    /// directory for resolving INCLUDE paths, instead of the process CWD.
    /// </summary>
    public FileSystemResolver(string basePath)
    {
        _basePath = basePath;
    }

    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public string ResolvePath(string currentFile, string includedPath)
    {
        // RGBDS behavior: includes are relative to CWD first, then the including file's directory.
        var cwdPath = _basePath != null
            ? Path.GetFullPath(Path.Combine(_basePath, includedPath))
            : Path.GetFullPath(includedPath);
        if (File.Exists(cwdPath)) return cwdPath;

        var dir = Path.GetDirectoryName(currentFile) ?? ".";
        return Path.GetFullPath(Path.Combine(dir, includedPath));
    }
}

/// <summary>
/// In-memory file system for testing INCLUDE/INCBIN without disk access.
/// </summary>
public sealed class VirtualFileResolver : ISourceFileResolver
{
    private readonly Dictionary<string, string> _textFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _binaryFiles = new(StringComparer.OrdinalIgnoreCase);

    public void AddTextFile(string path, string content) => _textFiles[path] = content;
    public void AddBinaryFile(string path, byte[] content) => _binaryFiles[path] = content;

    public bool FileExists(string path) =>
        _textFiles.ContainsKey(path) || _binaryFiles.ContainsKey(path);

    public string ReadAllText(string path)
    {
        if (_textFiles.TryGetValue(path, out var text)) return text;
        throw new FileNotFoundException($"Virtual file not found: {path}");
    }

    public byte[] ReadAllBytes(string path)
    {
        if (_binaryFiles.TryGetValue(path, out var bytes)) return bytes;
        if (_textFiles.TryGetValue(path, out var text))
            return System.Text.Encoding.UTF8.GetBytes(text);
        throw new FileNotFoundException($"Virtual file not found: {path}");
    }

    public string ResolvePath(string currentFile, string includedPath) => includedPath;
}
