using Koh.Common;
using Koh.Native.Syntax;

namespace Koh.Lsp.Discovery;

internal sealed record FileDiscoveryInfo(string FilePath, IReadOnlyList<string> IncludedFiles);

internal sealed class IncludeDiscoveryService
{
    public FileDiscoveryInfo Discover(string filePath, string text, string workspaceFolderPath)
    {
        var path = Path.GetFullPath(filePath);
        var tree = SyntaxTree.Parse(SourceText.From(text, path));
        var resolver = new Koh.Native.FileSystemResolver(workspaceFolderPath);
        return new FileDiscoveryInfo(
            path,
            tree.Includes.Select(include => resolver.ResolvePath(path, include)).ToArray()
        );
    }
}
