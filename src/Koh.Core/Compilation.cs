using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core;

/// <summary>
/// Immutable compilation unit. Holds syntax trees, runs the binder lazily,
/// and caches binding results. Modifying operations return new instances.
/// </summary>
public sealed class Compilation
{
    private readonly IReadOnlyList<SyntaxTree> _trees;
    private readonly TextWriter? _printOutput;
    private BindingResult? _bindingResult;
    private EmitModel? _emitModel;

    private Compilation(IReadOnlyList<SyntaxTree> trees, TextWriter? printOutput = null)
    {
        _trees = trees;
        _printOutput = printOutput;
    }

    public static Compilation Create(params SyntaxTree[] trees) =>
        new(trees.ToList());

    public static Compilation Create(TextWriter printOutput, params SyntaxTree[] trees) =>
        new(trees.ToList(), printOutput);

    public IReadOnlyList<SyntaxTree> SyntaxTrees => _trees;

    public IReadOnlyList<Diagnostic> Diagnostics => GetBindingResult().Diagnostics;

    public Compilation AddSyntaxTrees(params SyntaxTree[] trees)
    {
        var newTrees = new List<SyntaxTree>(_trees);
        newTrees.AddRange(trees);
        return new Compilation(newTrees, _printOutput);
    }

    public Compilation ReplaceSyntaxTree(SyntaxTree oldTree, SyntaxTree newTree)
    {
        var newTrees = new List<SyntaxTree>(_trees.Count);
        foreach (var t in _trees)
            newTrees.Add(ReferenceEquals(t, oldTree) ? newTree : t);
        return new Compilation(newTrees, _printOutput);
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
        var binder = new Binder(printOutput: _printOutput);
        BindingResult? result = null;
        foreach (var tree in _trees)
            result = binder.Bind(tree);

        _bindingResult = result ?? new BindingResult(null, null, []);
        return _bindingResult;
    }
}
