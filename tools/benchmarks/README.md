# Benchmarks

`NativeParseBenchmarks` measures a native parse request and managed syntax DTO
creation. `NativeAssemblyBenchmarks` measures native assembly and ELF output inspection
from existing source DTOs. `NativePipelineBenchmarks` includes both requests.
All three use the persistent Rust worker through `Koh.Native`; they include
transport overhead. BenchmarkDotNet's allocation figures cover the managed
process, not Rust worker allocations.

`benchmark.sh` builds the Rust assembler and linker for the Docker
command-line comparison. Historical C# binder measurements describe the deleted
implementation and are not directly comparable with native protocol timings.
