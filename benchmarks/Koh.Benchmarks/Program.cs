using BenchmarkDotNet.Running;
using Koh.Benchmarks;

var bdnVersion = typeof(BenchmarkRunner).Assembly.GetName().Version;
Console.WriteLine($"BenchmarkDotNet {bdnVersion}");
Console.WriteLine($".NET {Environment.Version}");
Console.WriteLine();

BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConfig).Assembly).Run(args, new BenchmarkConfig());
return 0;
