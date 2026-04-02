using BenchmarkDotNet.Attributes;
using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Benchmarks;

/// <summary>
/// Measures bind + emit on pre-parsed syntax trees.
/// No INCLUDE resolution — inline source only. Pure CPU-bound compiler work:
/// macro expansion, symbol collection, instruction encoding to bytes.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class BindAndEmitBenchmarks
{
    private SyntaxTree _smallTree = null!;
    private SyntaxTree _mediumTree = null!;
    private SyntaxTree _largeTree = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallTree = SyntaxTree.Parse(Sources.Small);
        _mediumTree = SyntaxTree.Parse(Sources.Medium);
        _largeTree = SyntaxTree.Parse(Sources.Large);

        // Validate all inputs produce successful results
        Validate(_smallTree, "Small");
        Validate(_mediumTree, "Medium");
        Validate(_largeTree, "Large");
    }

    [Benchmark]
    public BindingResult BindAndEmitSmall()
    {
        var binder = new Binder();
        var result = binder.Bind(_smallTree);
        if (!result.Success) throw new InvalidOperationException("Bind failed (Small)");
        return result;
    }

    [Benchmark]
    public BindingResult BindAndEmitMedium()
    {
        var binder = new Binder();
        var result = binder.Bind(_mediumTree);
        if (!result.Success) throw new InvalidOperationException("Bind failed (Medium)");
        return result;
    }

    [Benchmark]
    public BindingResult BindAndEmitLarge()
    {
        var binder = new Binder();
        var result = binder.Bind(_largeTree);
        if (!result.Success) throw new InvalidOperationException("Bind failed (Large)");
        return result;
    }

    private static void Validate(SyntaxTree tree, string name)
    {
        var binder = new Binder();
        var result = binder.Bind(tree);
        if (!result.Success)
            throw new InvalidOperationException($"Validation failed ({name}): bind produced errors");
        if (result.Sections is null || result.Sections.Count == 0)
            throw new InvalidOperationException($"Validation failed ({name}): no sections produced");
    }
}
