using Koh.Common;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Native;

public sealed class AssemblyOptions
{
    public bool AllowUndefinedSymbols { get; init; }
}

public sealed class Compilation
{
    private readonly ISourceFileResolver resolver;
    private readonly AssemblyOptions options;
    private readonly object gate = new();
    private readonly Dictionary<string, SyntaxTree> documents = new(
        StringComparer.OrdinalIgnoreCase
    );
    private NativeResponse? analysis;
    private EmitModel? emitted;
    private readonly List<NativeResolution> resolutions = [];
    private readonly List<NativeSearchRoots> searchRoots = [];
    private Dictionary<string, byte[]> binarySources = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<SyntaxTree> SyntaxTrees { get; }

    private Compilation(ISourceFileResolver resolver, AssemblyOptions options, SyntaxTree[] trees)
    {
        this.resolver = resolver;
        this.options = options;
        SyntaxTrees = trees;
        foreach (var tree in trees)
            documents[SyntaxTree.SourcePath(tree.Text)] = tree;
    }

    public static Compilation Create(params SyntaxTree[] trees) =>
        new(new FileSystemResolver(), new(), trees);

    public static Compilation Create(ISourceFileResolver resolver, params SyntaxTree[] trees) =>
        new(resolver, new(), trees);

    public static Compilation Create(AssemblyOptions options, params SyntaxTree[] trees) =>
        new(new FileSystemResolver(), options, trees);

    public static Compilation Create(
        ISourceFileResolver resolver,
        AssemblyOptions options,
        params SyntaxTree[] trees
    ) => new(resolver, options, trees);

    private NativeSource[] Sources()
    {
        resolutions.Clear();
        searchRoots.Clear();
        binarySources = new Dictionary<string, byte[]>(
            resolver.GetBinaryOverlays(),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (path, text) in resolver.GetTextOverlays())
            if (!documents.ContainsKey(path))
                documents[path] = SyntaxTree.Parse(SourceText.From(text, path));
        var pending = new Queue<SyntaxTree>(documents.Values);
        var seen = new HashSet<string>(documents.Keys, StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var tree))
        {
            var current = SyntaxTree.SourcePath(tree.Text);
            searchRoots.Add(new NativeSearchRoots(current, resolver.GetResolutionRoots(current)));
            foreach (var include in tree.Document.Includes)
            {
                var path = resolver.ResolvePath(current, include.Path);
                resolutions.Add(new NativeResolution(current, include.Path, path));
                if (include.Binary)
                {
                    if (resolver.FileExists(path))
                        binarySources[path] = resolver.ReadAllBytes(path);
                    continue;
                }
                if (!seen.Add(path) || !resolver.FileExists(path))
                    continue;
                var included = SyntaxTree.Parse(SourceText.From(resolver.ReadAllText(path), path));
                documents[path] = included;
                pending.Enqueue(included);
            }
        }
        return documents
            .Select(pair => new NativeSource(pair.Key, pair.Value.Text.ToString()))
            .ToArray();
    }

    private NativeResponse Request(string op, string format = "elf")
    {
        lock (gate)
            return NativeClient.Shared.Request(
                op,
                Sources(),
                SyntaxTrees.Select(tree => SyntaxTree.SourcePath(tree.Text)),
                options.AllowUndefinedSymbols,
                format,
                binarySources.Select(pair => new NativeBinary(
                    pair.Key,
                    pair.Value.Select(value => (int)value).ToArray()
                )),
                resolutions,
                searchRoots
            );
    }

    internal NativeResponse Analysis
    {
        get
        {
            lock (gate)
            {
                if (analysis != null)
                    return analysis;
                analysis = Request("analyze");
                foreach (var document in analysis.Documents)
                    if (!documents.ContainsKey(document.Path) && resolver.FileExists(document.Path))
                        documents[document.Path] = new SyntaxTree(
                            SourceText.From(resolver.ReadAllText(document.Path), document.Path),
                            document,
                            analysis.Diagnostics
                        );
                return analysis;
            }
        }
    }
    internal IReadOnlyDictionary<string, SyntaxTree> Documents
    {
        get
        {
            _ = Analysis;
            return documents;
        }
    }

    public SemanticModel GetSemanticModel(SyntaxTree tree) => new(this, tree);

    public byte[] EmitBytes(string format)
    {
        var response = Request("assemble", format);
        if (!response.Ok)
            throw new InvalidDataException(
                string.Join(Environment.NewLine, response.Diagnostics.Select(d => d.Message))
            );
        return response.Data.Select(value => checked((byte)value)).ToArray();
    }

    public EmitModel Emit()
    {
        lock (gate)
            return EmitCore();
    }

    private EmitModel EmitCore()
    {
        if (emitted != null)
            return emitted;
        var response = Request("assemble");
        var diagnostics = response.Diagnostics.Select(d => d.ToDiagnostic()).ToArray();
        if (!response.Ok)
        {
            if (!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException(
                    "Native assembler failed without an error diagnostic"
                );
            return emitted = new EmitModel([], [], diagnostics);
        }
        return emitted = NativeObject.Read(
            response.Object,
            response.Data.Select(value => checked((byte)value)).ToArray(),
            diagnostics
        );
    }
}
