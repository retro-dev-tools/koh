# Compiler measurements

The managed bridge/emulator BenchmarkDotNet suite and the root `Benchmark`
target were decommissioned in PR62. They depended on the retired C# bridge and
emulator and did not measure the native product directly.

Native compiler workloads and the optional Linux process measurement runner
remain under [compiler](compiler/README.md). The C# files there are required
source-language inputs consumed by Roslyn and the native CIL compiler.

Record the commit, host, toolchain versions, workload hashes and competing load
with results. Keep generated measurements outside the repository. Historical
managed benchmark timings are not directly comparable with native process
measurements or full SDK builds.
