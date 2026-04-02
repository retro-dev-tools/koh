using BenchmarkDotNet.Attributes;
using Koh.Core.Syntax;

namespace Koh.Benchmarks;

/// <summary>
/// Measures lexing + parsing of inline assembly source strings.
/// No INCLUDE resolution — pure parse of the provided text.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class ParseBenchmarks
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
    }

    [Benchmark]
    public SyntaxTree ParseSmall() => SyntaxTree.Parse(_small);

    [Benchmark]
    public SyntaxTree ParseMedium() => SyntaxTree.Parse(_medium);

    [Benchmark]
    public SyntaxTree ParseLarge() => SyntaxTree.Parse(_large);
}
