# Benchmarks

`NativeParseBenchmarks` measures a native parse request and managed syntax DTO
creation. `NativeAssemblyBenchmarks` measures native assembly and ELF output inspection
from existing source DTOs. `NativePipelineBenchmarks` includes both requests.
All three use the persistent Rust worker through `Koh.Native`; they include
transport overhead. BenchmarkDotNet's allocation figures cover the managed
process, not Rust worker allocations.

Run the maintained benchmarks on the host:

```sh
dotnet build tools/benchmarks/Koh.Benchmarks -c Release
dotnet run --project tools/benchmarks/Koh.Benchmarks -c Release --no-build -- --list flat --filter '*Native*'
dotnet run --project tools/benchmarks/Koh.Benchmarks -c Release --no-build -- --filter '*Native*'
```

Record the commit, host OS/architecture, CPU, RAM, Rust/.NET versions, power mode
and competing load with exported results. Compare equivalent workloads on the
same host; managed allocations do not measure native worker peak memory. These
assembler/bridge benchmarks do not establish compiler performance. Historical
C# binder timings describe the deleted assembler implementation and are not
directly comparable with native protocol timings.

The former container-based pokecrystal harness was removed in PR #55. Both Koh
phases were permanently classified unsupported by its capability configuration,
so it could not provide the advertised equivalent toolchain comparison. CI's
independent RGBDS verification remains under `tools/verification`; the benchmark
project, root benchmark target and CI discovery checks remain available. No
container runtime is needed for these workflows.

Small compiler workloads and an optional Linux process measurement runner are
documented under [compiler](compiler/README.md). Their separately recorded
core-compilation measurements exclude Roslyn, ELF serialization and linking;
they do not measure regression-suite speed or whole SDK builds.
