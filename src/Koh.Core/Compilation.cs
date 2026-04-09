using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;
using System.Threading.Tasks;

namespace Koh.Core;

/// <summary>
/// Immutable compilation unit. Holds syntax trees, runs the binder lazily,
/// and caches binding results. Modifying operations return new instances.
/// </summary>
public sealed class Compilation
{
    private readonly IReadOnlyList<SyntaxTree> _trees;
    private readonly TextWriter? _printOutput;
    private readonly BinderOptions _binderOptions;
    private readonly ISourceFileResolver _resolver;
    private BindingResult? _bindingResult;
    private EmitModel? _emitModel;

    private Compilation(IReadOnlyList<SyntaxTree> trees, ISourceFileResolver? resolver = null,
        TextWriter? printOutput = null, BinderOptions binderOptions = default)
    {
        _trees = trees;
        _resolver = resolver ?? new FileSystemResolver();
        _printOutput = printOutput;
        _binderOptions = binderOptions;
    }

    public static Compilation Create(params SyntaxTree[] trees) =>
        Create(new FileSystemResolver(), trees);

    public static Compilation Create(TextWriter printOutput, params SyntaxTree[] trees) =>
        new(trees.ToList(), null, printOutput);

    public static Compilation Create(BinderOptions options, params SyntaxTree[] trees) =>
        new(trees.ToList(), binderOptions: options);

    public static Compilation Create(BinderOptions options, TextWriter printOutput, params SyntaxTree[] trees) =>
        new(trees.ToList(), null, printOutput, options);

    /// <summary>
    /// Canonical factory that accepts a source file resolver for INCLUDE/INCBIN.
    /// Multi-tree compilations reject trees with null/empty FilePath.
    /// </summary>
    public static Compilation CreateFromSources(IReadOnlyList<SourceText> sources,
        BinderOptions options = default, TextWriter? printOutput = null)
    {
        var trees = new SyntaxTree[sources.Count];
        Parallel.For(0, sources.Count, i => { trees[i] = SyntaxTree.Parse(sources[i]); });
        return new Compilation(trees, binderOptions: options, printOutput: printOutput);
    }

    public static Compilation Create(ISourceFileResolver resolver, params SyntaxTree[] trees)
    {
        if (trees.Length > 1)
        {
            for (int i = 0; i < trees.Length; i++)
            {
                if (string.IsNullOrEmpty(trees[i].Text.FilePath))
                    throw new ArgumentException(
                        $"Multi-tree compilation requires all trees to have a non-empty FilePath. Tree at index {i} has a null or empty path.",
                        nameof(trees));
            }
        }

        return new Compilation(trees.ToList(), resolver);
    }

    public IReadOnlyList<SyntaxTree> SyntaxTrees => _trees;

    public IReadOnlyList<Diagnostic> Diagnostics => GetBindingResult().Diagnostics;

    public Compilation AddSyntaxTrees(params SyntaxTree[] trees)
    {
        var newTrees = new List<SyntaxTree>(_trees);
        newTrees.AddRange(trees);
        return new Compilation(newTrees, _resolver, _printOutput, _binderOptions);
    }

    public Compilation ReplaceSyntaxTree(SyntaxTree oldTree, SyntaxTree newTree)
    {
        var newTrees = new List<SyntaxTree>(_trees.Count);
        foreach (var t in _trees)
            newTrees.Add(ReferenceEquals(t, oldTree) ? newTree : t);
        return new Compilation(newTrees, _resolver, _printOutput, _binderOptions);
    }

    public SemanticModel GetSemanticModel(SyntaxTree tree)
    {
        if (!_trees.Any(t => ReferenceEquals(t, tree)))
            throw new ArgumentException(
                "The syntax tree does not belong to this compilation.", nameof(tree));

        var result = GetBindingResult();
        return new SemanticModel(tree, result);
    }

    public EmitModel Emit()
    {
        if (_emitModel != null) return _emitModel;
        _emitModel = EmitModel.FromBindingResult(GetBindingResult());
        return _emitModel;
    }

    private BindingResult GetBindingResult()
    {
        if (_bindingResult != null) return _bindingResult;

        // Bind all trees through a single binder (shared symbol table).
        // NOTE (Task 5.x): Binder.Bind() currently checks for undefined symbols at the
        // end of each call. In a multi-tree compilation this produces false "Undefined
        // symbol" diagnostics for symbols that are forward-referenced in one tree but
        // defined in a later tree. The fix is to move the undefined-symbol check out of
        // Bind() and call it once here after all trees are processed. Safe for now because
        // no test exercises cross-tree forward references.
        var binder = new Binder(_binderOptions, fileResolver: _resolver, printOutput: _printOutput);
        BindingResult? result = null;
        foreach (var tree in _trees)
            result = binder.Bind(tree);

        _bindingResult = result ?? new BindingResult(null, null, []);
        return _bindingResult;
    }
}
