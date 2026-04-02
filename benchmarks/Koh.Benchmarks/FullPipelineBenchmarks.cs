using BenchmarkDotNet.Attributes;
using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Benchmarks;

/// <summary>
/// Measures the full pipeline: lex + parse + bind + emit + freeze.
/// No INCLUDE resolution — inline source only.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class FullPipelineBenchmarks
{
    private string _small = null!;
    private string _medium = null!;
    private string _large = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = Sources.Small;
        _medium = Sources.Medium;
        _large = Sources.Large;

        // Validate all inputs
        Validate(_small, "Small");
        Validate(_medium, "Medium");
        Validate(_large, "Large");
    }

    [Benchmark]
    public EmitModel FullPipelineSmall()
    {
        var model = Compilation.Create(SyntaxTree.Parse(_small)).Emit();
        if (!model.Success) throw new InvalidOperationException("Pipeline failed (Small)");
        return model;
    }

    [Benchmark]
    public EmitModel FullPipelineMedium()
    {
        var model = Compilation.Create(SyntaxTree.Parse(_medium)).Emit();
        if (!model.Success) throw new InvalidOperationException("Pipeline failed (Medium)");
        return model;
    }

    [Benchmark]
    public EmitModel FullPipelineLarge()
    {
        var model = Compilation.Create(SyntaxTree.Parse(_large)).Emit();
        if (!model.Success) throw new InvalidOperationException("Pipeline failed (Large)");
        return model;
    }

    private static void Validate(string source, string name)
    {
        var model = Compilation.Create(SyntaxTree.Parse(source)).Emit();
        if (!model.Success)
            throw new InvalidOperationException($"Validation failed ({name}): pipeline produced errors");
        if (model.Sections.Count == 0)
            throw new InvalidOperationException($"Validation failed ({name}): no sections produced");
    }
}
