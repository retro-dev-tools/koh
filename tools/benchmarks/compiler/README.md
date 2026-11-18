# Compiler measurements

These small common-subset fixtures cover startup/calls, arrays/structs/byrefs,
and tile/map writes. They are compiler workloads, not representative full games
or regression-test timing benchmarks. Each entry writes an independently known
result at C900 and then idles. No Koh.GameBoy API or external runtime reference
is required. Roslyn remains the C# frontend.

Build one input and measure the native CLI on Linux:

```sh
work=$(mktemp -d)
dotnet build tools/benchmarks/compiler/Fixture.csproj -c Release \
  -p:BenchmarkCase=Memory -o "$work/input" -m:1
cargo build -p koh-compile --release --locked
python3 tools/benchmarks/compiler/measure.py --output "$work/native.json" -- \
  target/release/koh-compile "$work/input/Fixture.dll" --no-pdb -o "$work/output.o"
```

`BenchmarkCase` is `Startup`, `Memory` or `Tiles`. The optional measurement runner
requires Python3.9+, Linux and zsh's resource-accounting builtin; it adds no build,
test, CI or release prerequisite. No container is used. Windows/macOS measurement
is not implemented or claimed. Measure on an otherwise idle host, record CPU,
RAM, OS/tool versions, power profile, commit and input hashes, and keep raw JSON
outside the repository. Builds are excluded from the timed command.

One untimed filesystem-cache warmup precedes nine fresh processes by default.
There are no retries. Each sample records elapsed time, the compiler child's
maximum RSS, CPU time, stdout and stderr; any nonzero exit aborts without writing
a success result. Existing result files are never overwritten. Elapsed time
includes the small zsh launcher; it is not an in-process compiler timer. Calibrate
with `/bin/true` and a known allocation before comparing low memory usage.
Python's direct child `wait4` accounting inherited a roughly17MiB launcher peak
on the measured host; zsh's separate child accounting removes that inflation.

[Recorded comparison](../../../docs/rust-compiler/compiler-performance.md)
documents a narrower, like-for-like compiler-core boundary using two temporary
library adapters. The CLI example above additionally includes ELF serialization
and file output; do not compare it directly with that core table. Referenced
assemblies, PDBs, full samples, hot repeated compilation, SDK build time and target
execution costs require separately labeled measurements.
