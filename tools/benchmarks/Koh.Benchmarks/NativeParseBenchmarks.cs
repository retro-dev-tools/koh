using BenchmarkDotNet.Attributes;
using Koh.Native.Syntax;

namespace Koh.Benchmarks;

[Config(typeof(BenchmarkConfig))]
public class NativeParseBenchmarks
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
