# Compiler-core performance checkpoint

Measured the still-present C# compiler and Rust compiler at
`c36e41ac7eee5426a5d984d688c2d45c13cd9e8b`, before legacy deletion, using identical
Roslyn DLLs for each pair. The new source fixtures and small host-native runner
live in [tools/benchmarks/compiler](../../tools/benchmarks/compiler/README.md).
This is a common-kernel baseline, not a claim about whole-game, SDK or warmed
managed compiler performance. Broader sample/performance acceptance remains open.

## Scope and host

Both temporary adapters read a DLL, resolve metadata, lower/optimize and emit
their compiler's in-memory machine-code object, then print statistics. The Rust
path includes its normal verification/legalization. Neither timed path serializes
ELF, links, runs Roslyn/MSBuild, loads a PDB, executes the game or invokes a
compiler subprocess. Reference inputs are empty for these self-contained kernels.
Both implementations are optimized Release builds. Their independent ABI,
guarding and reachability rules are retained; none is disabled for measurement.

The core timer encloses the library call (and input read in Rust), while wall
time includes process/runtime startup, JSON statistics and the zsh launcher.
Each measured sample is a **fresh process and first compiler call**, including
managed JIT work. One filesystem-cache warmup is excluded before nine samples.
The original C# SDK task ran in MSBuild's existing process, so process-startup
differences here are not SDK-build speedups. These are small inputs with a large
first-use cost; no general speedup factor is inferred.

Host: Intel Core i7-6820HQ2.70GHz,8 logical CPUs/8 available,16244540KiB RAM;
Gentoo Linux7.3.3 x86_64, glibc2.43; rustc1.99.0
`b940084d7`, .NET SDK10.0.115, runtime10.0.15, Python3.14. Governor `powersave`,
platform profile `balanced`; no CPU pinning, frequency lock, cache eviction or
dedicated benchmark host. Preparatory load average1.26/1.05/0.95; builds finished
before sampling. Background desktop activity was not isolated. Results are local
observations, not cross-platform statistics or confidence intervals.

## Results

Milliseconds are medians of nine final samples. RSS is the largest child peak
across those samples, in KiB. All54 final samples succeeded with nonempty emitted
ROM data and no diagnostic stderr.

| Input | C# core ms (range) | Rust core ms (range) | C# wall ms | Rust wall ms | C# peak KiB | Rust peak KiB |
|---|---:|---:|---:|---:|---:|---:|
| Startup: call/add/store | 417.085 (413.043–430.112) | 0.392 (0.336–0.502) | 503.552 | 3.466 | 89424 | 3576 |
| Memory:64-byte array, struct/ref accumulation | 452.611 (451.047–468.482) | 1.241 (1.189–1.406) | 546.958 | 4.387 | 89776 | 3788 |
| Tiles: alternating tile planes,1024 map writes | 433.500 (429.550–439.840) | 1.077 (1.011–1.365) | 529.932 | 4.309 | 89712 | 3844 |

The first Python `wait4` collector inflated native peaks to roughly17MiB through
inherited launcher accounting; those samples were rejected. Final `measure.py`
uses zsh's resource-accounted compiler child. A minimal-child calibration measured
1840KiB, below all reported compiler peaks. A24MiB allocation self-check also
exceeded24000KiB as expected. No parent/child sum, installed-runtime size or
managed-allocation counter is presented as compiler RSS.

Emission payloads are **larger** in Rust for these kernels:

| Input | C# ROM-section bytes | Rust ROM-section bytes |
|---|---:|---:|
| Startup | 113 | 733 |
| Memory | 1691 | 3455 |
| Tiles | 929 | 2023 |

These sums exclude gaps/ELF metadata and include each implementation's own startup
and helpers. All six linked cartridges were32768 bytes. This is not a claim of
equal generated-code cost; the native calling/guarding model has costs requiring
separate game-level budgets. No target-cycle improvement is claimed.

## Deployment size

Files were freshly built and measured by logical byte length, not compressed
archive size or filesystem block allocation. Native release settings are
`lto=true`, `strip=true`; managed Release output is framework-dependent, not AOT
or trimmed. External PDBs are excluded.

| Artifact | Bytes | Scope |
|---|---:|---|
| Native `koh-compile` executable | 1946512 | Actual production CLI, not the measurement adapter |
| Native core measurement executable | 1823624 | Temporary library adapter used in the table |
| Managed compiler library | 306176 | `Koh.Compiler.dll` alone |
| Managed adapter deployment | 946262 | Apphost, adapter, compiler/Common/Objects, Mono.Cecil assets and runtime/dependency JSON |
| Installed .NETCore runtime directory | 82292832 | Host's shared `Microsoft.NETCore.App/10.0.15`; excludes SDK/muxer and is not copied per compiler |

The Rust CLI exceeds the managed owned-file deployment in this comparison.
The shared runtime is a separate prerequisite, not per-build allocation or an
automatic SDK saving: C# authoring and the existing emulator still need .NET.
Native CLI SHA256:
`b917ba162b4d1eb8673804911f84b1a353247fca1423cd8002f3e75190714c02`.

## Inputs and independent execution

Inputs are ordinary Release Roslyn assemblies, `DebugType=none`, no adjacent PDB:

| Case | DLL bytes | SHA256 |
|---|---:|---|
| Startup | 4608 | `e1a60cba916ea194b93e64dd8851d25e9cbaea6a57bea4a0643e1073cf422a11` |
| Memory | 5120 | `8cb4f1b5da9e28a10791a617a2a631e00b31958d50a44af53664eebae1c786a4` |
| Tiles | 4608 | `c477676207acb1078a36bc8a818020ee5beb8f134447d8f02b2cea59d8d7d97c` |

Outside timing, the native CLI emitted canonical ELF for all three inputs and
the Rust linker finalized their ROMs. The C# models used the existing
`NativeLinker` bridge and same Rust linker; no extra serializer/linker was written.
The existing C# emulator executed all six cartridges from an explicit cartridge
entry precondition (PC0100, SPFFFE), with poisoned output/VRAM. These are kernel
execution probes, not boot-ROM or debugger/source acceptance.

Independent checks passed6/6: Startup writes32-bit42; Memory writes
`X=6119 | (Y=1499 << 16)` =98244583; Tiles writes90, exact alternating16 tile
bytes, all1024 map bytes zero, BGP E4 and LCDC91. Each result was followed by256
instruction steps checking stable idle PC/SP and no HALT trap. No constructor
LCD value, private allocator layout or legacy output was an oracle. Framebuffer
and target-cycle measurements were not made in these probes.

An earlier returned-entry Startup probe exposed a legacy difference: it wrote42
but continued execution after two million steps; native output halted after255.
The shared benchmark inputs explicitly idle after their result, reflecting a
game entry's steady loop. This does not make the old return behavior a contract
or weaken the Rust compiler's returning-entry behavior. Only the final looping
input hashes/samples above are reported.

## Reproduction and provenance

Build all inputs serially with `BenchmarkCase=Startup`, `Memory`, `Tiles` and
distinct output directories. Use the same DLL file for both compilers. Preserve
the baseline checkout before removing its source; after deletion, historical
reproduction requires commit `c36e41a`. The old adapter is measurement-only and
is not part of SDK, release discovery or a fallback compiler route.

The temporary managed net10.0 executable enabled implicit usings/nullable and referenced only
`src/Compiler/Koh.Compiler/Koh.Compiler.csproj`; its complete entry body was:

```csharp
using System.Text.Json;
using Koh.Common;
using Koh.Compiler;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Backends.Sm83;
using Koh.Objects;
var timer = System.Diagnostics.Stopwatch.StartNew();
var diagnostics = new DiagnosticBag();
var model = CompilerDriver.Compile(new CilFrontend(), new Sm83Backend(),
    CompilerInput.FromAssembly(args[0], []), diagnostics);
if (!model.Success || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
{
    foreach (var diagnostic in diagnostics) Console.Error.WriteLine(diagnostic);
    return 1;
}
timer.Stop();
Console.WriteLine(JsonSerializer.Serialize(new {
    compile_ms = timer.Elapsed.TotalMilliseconds,
    sections = model.Sections.Count,
    rom_bytes = model.Sections.Where(s => s.Type is SectionType.Rom0 or SectionType.RomX)
        .Sum(s => s.Data.Length)
}));
return 0;
```

The temporary Rust2024 executable depended only on the checkout's `koh-compile`
path, with the root lockfile copied for dependency selection and Release
`lto=true`, `strip=true`. It was built offline against the same compiler source:

```rust
fn main() {
    let path = std::env::args_os().nth(1).expect("assembly");
    let timer = std::time::Instant::now();
    let bytes = std::fs::read(path).unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[&bytes]).unwrap();
    let object = koh_compile::compile::object(&resolver, None).unwrap();
    let compile_ms = timer.elapsed().as_secs_f64() * 1000.0;
    let bytes: usize = object.sections.iter().filter(|section| section.kind <= 1)
        .map(|section| section.data.len()).sum();
    println!("{{\"compile_ms\":{compile_ms},\"sections\":{},\"rom_bytes\":{bytes}}}",
        object.sections.len());
}
```

For each case, invoke `measure.py --output <new.json> -- <adapter command> <DLL>`
once per compiler. Compute the median `compile_ms` from each sample's JSON stdout;
the runner separately records wall medians and maximum RSS. Raw54 final samples
are under `/tmp/koh-compiler-perf-c36/{legacy,rust}-{Startup,Memory,Tiles}-final.json`;
the rejected collectors/earlier fixture variants have different filenames and
are excluded. Host metadata is `host.json`; build/run logs are
`/tmp/koh-compiler-perf-*.log`; final execution states are
`/tmp/koh-compiler-perf-*-entry-execution.json`. Generated binaries/measurements
are not committed. Table values, input hashes, source and adapter recipes are
retained here so the evidence remains reviewable after temporary files expire.

Runner self-checks passed for allocation/RSS, exact stdout/stderr capture,
sample count, overwrite refusal, exit7 propagation, no failed success artifact
and minimal-child calibration. Fixture builds, both adapter builds and all native
CLI/ELF/link commands completed with exit0; completed logs were read. No new CI
step, compiler/test dependency, retry or regression-test exclusion was introduced.
