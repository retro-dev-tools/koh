using Koh.Native;

namespace Koh.Lsp.Source;

/// <summary>
/// Decorator over an <see cref="ISourceFileResolver"/> that overlays in-memory
/// document text for files currently open in the editor. This ensures unsaved
/// changes are reflected in compilation immediately.
/// <para>
/// Binary reads (<see cref="ReadAllBytes"/>) always delegate to the underlying
/// resolver because INCBIN should use the on-disk content.
/// </para>
/// </summary>
internal sealed class WorkspaceOverlayResolver : ISourceFileResolver
{
    private readonly ISourceFileResolver _inner;
    private readonly Dictionary<string, string> _overlays = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceOverlayResolver(ISourceFileResolver inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Sets the overlay text for a document, keyed by normalized path.
    /// </summary>
    public void SetOverlayText(string path, string text)
    {
        _overlays[NormalizePath(path)] = text;
    }

    /// <summary>
    /// Removes the overlay text for a document, causing future reads to
    /// fall back to the underlying resolver (disk).
    /// </summary>
    public void RemoveOverlay(string path)
    {
        _overlays.Remove(NormalizePath(path));
    }

    public bool FileExists(string path)
    {
        return _overlays.ContainsKey(NormalizePath(path)) || _inner.FileExists(path);
    }

    public string ReadAllText(string path)
    {
        if (_overlays.TryGetValue(NormalizePath(path), out var text))
            return text;

        return _inner.ReadAllText(path);
    }

    /// <summary>
    /// Always delegates to the underlying resolver. Binary reads (INCBIN)
    /// should use the on-disk content, not unsaved editor text.
    /// </summary>
    public byte[] ReadAllBytes(string path)
    {
        return _inner.ReadAllBytes(path);
    }

    public string ResolvePath(string currentFile, string includedPath)
    {
        return _inner.ResolvePath(currentFile, includedPath);
    }

    public IReadOnlyList<string> GetResolutionRoots(string currentFile) =>
        _inner.GetResolutionRoots(currentFile);

    public IReadOnlyDictionary<string, string> GetTextOverlays()
    {
        var result = new Dictionary<string, string>(
            _inner.GetTextOverlays(),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (path, text) in _overlays)
            result[path] = text;
        return result;
    }

    public IReadOnlyDictionary<string, byte[]> GetBinaryOverlays() => _inner.GetBinaryOverlays();

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }
}
