using BenchmarkDotNet.Attributes;
using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Benchmarks;

[Config(typeof(BenchmarkConfig))]
public class NativeAssemblyBenchmarks
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
    public EmitModel AssembleSmall() => Assemble(_smallTree);

    [Benchmark]
    public EmitModel AssembleMedium() => Assemble(_mediumTree);

    [Benchmark]
    public EmitModel AssembleLarge() => Assemble(_largeTree);

    private static EmitModel Assemble(SyntaxTree tree)
    {
        var result = Compilation.Create(tree).Emit();
        if (!result.Success)
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        return result;
    }

    private static void Validate(SyntaxTree tree, string name)
    {
        var result = Assemble(tree);
        if (!result.Success)
            throw new InvalidOperationException(
                $"Validation failed ({name}): native assembly produced errors"
            );
        if (result.Sections is null || result.Sections.Count == 0)
            throw new InvalidOperationException(
                $"Validation failed ({name}): no sections produced"
            );
    }
}
