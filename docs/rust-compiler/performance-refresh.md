# Compiler performance refresh at3d15f2d

Measured the current Rust compiler against the retained pre-removal C# compiler
library fromc36e41a. That C# implementation is unchanged throughf4b6cc6, the last
revision before deletion. Its cached DLL is306176 bytes, SHA256
`5d5c818c03ec03708e9bf15ff434c6c4f0e6447ea65a9a47a85b1742f1bc4e5a`.
It and all measurement adapters remain outside the repository, SDK and release
discovery. There is no production fallback.

## Scope and protocol

Both adapters read the identical Roslyn DLL/reference paths, compile to their
in-memory machine-code object and print core time, section count and ROM-section
bytes. Rust includes its normal verification/legalization. No guarding or normal
optimization is disabled. ELF serialization, linking, Roslyn/MSBuild, PDB loading
and execution are outside the timer. Native input reads are included; Cecil may
read referenced metadata lazily. These implementation differences are retained.

The existing host-native `tools/benchmarks/compiler/measure.py` runs one excluded
filesystem-cache warmup and nine fresh measured processes per compiler/workload:
90 successful measured processes total, without retries. Every stdout is parsed
as JSON and all measured stderr is empty. Managed CLR startup/JIT first-use work
remains present. The old SDK ran its compiler in MSBuild's process, so these are
not warmed compiler, whole-SDK build or general speedup measurements.

Host: Intel Core i7-6820HQ2.70GHz, eight available logical CPUs, Gentoo Linux7.3.3
x86_64/glibc2.43,16GiB RAM; rustc1.99.0 Gentoo, .NET SDK10.0.115/runtime10.0.15.
Governor powersave, platform profile balanced, no pinning/frequency lock or cache
eviction. Preparatory load averages2.44/2.54/2.86 include preceding builds;
sampling starts after builds finish. Desktop/background activity is not isolated.
Ranges below show observed variation, not confidence intervals.

## Fresh-process compiler-core measurements

Core milliseconds are medians with min/max in parentheses. Wall milliseconds
include process startup and the measurement launcher. RSS is the largest child
peak among nine samples, not a managed allocation counter or parent/child sum.

| Input | C# core ms | Rust core ms | C# wall ms | Rust wall ms | C# peak KiB | Rust peak KiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Startup |599.052 (557.460–682.746)|0.535 (0.447–0.836)|732.979|4.421|90020|3660|
| Memory |892.416 (640.476–927.905)|1.653 (1.483–2.350)|1026.823|5.930|90284|3944|
| Tiles |785.717 (629.771–884.059)|1.564 (1.398–2.803)|920.446|6.013|90168|3944|
| Graphics game DLL |550.979 (516.036–647.852)|47.716 (44.259–54.340)|696.795|55.082|67128|11888|
| Original2048 game DLL |554.832 (502.705–580.191)|58.227 (52.911–84.301)|681.320|64.266|57104|13000|

The first three inputs are the unchanged common kernels from the
[earlier checkpoint](compiler-performance.md), with matching DLL hashes. The
additional game inputs compile the actual unchanged game C# files against the
historical GameBoy library atbf0a5fc, giving both compilers the same runtime IL.
They do not use the new library as a legacy API convention or claim compatibility
with its old hardware-timing assumptions. Current production samples use the
current GameBoy library and have separate SDK/game acceptance.

Calibration: `/bin/true` peak1836KiB; a page-touched24MiB child peaks33512KiB.
All native reported peaks exceed the minimal child, and the allocation check
exceeds24000KiB. The existing zsh child accounting avoids Python wait4's inherited
launcher-peak problem described in the earlier report.

## Generated and deployed sizes

| Input | C# ROM-section bytes | Rust ROM-section bytes | C# cartridge bytes | Rust cartridge bytes |
| --- | ---: | ---: | ---: | ---: |
| Startup |113|739|32768|32768|
| Memory |1691|3330|32768|32768|
| Tiles |929|1829|32768|32768|
| Graphics game DLL |16236|45990|32768|65536|
| Original2048 game DLL |18233|49059|32768|65536|

Rust emits more ROM payload in every workload; the two game cartridges double
in size. No generated-code size or target-speed win is claimed. Section sums
exclude gaps and ELF metadata. Calling/guarding/layout choices remain different.

The actual native CLI is2077584 bytes versus946214 owned files for the managed
adapter/compiler/dependencies, excluding PDBs. The compiler DLL alone is306176.
The host's shared .NETCore runtime is82292832 bytes, a separate prerequisite
rather than per-compiler owned deployment. C# authoring and the retained emulator
still require .NET; this does not establish an SDK installation-size saving.

## Untimed execution and limits

All six common-kernel cartridges execute in the retained C# emulator and check
independent results42,98244583 and90, stable idle PC/SP, no HALT fault and eight
poisoned output guard bytes. Tiles additionally checks all1024 map bytes, the
literal alternating16 tile bytes, BGP E4 and the program's explicit LCDC91 write.
These use a stated PC0100/SPFFFE entry precondition, not real-boot acceptance or
an emulator constructor's LCD value as an oracle.

All four additional game ROMs link and run through real boot for1200 frames with
valid screenshots and zero mode3 writes. Their captures differ: historical-runtime
Rust outputs lack numeric HUD updates visible in the legacy outputs at that
checkpoint. They are compiler workloads, **not game-parity acceptance**. No
framebuffer/real-time equivalence or target-performance claim is made from them.
Older polling/copy implementations are not the current hardware contract;
the [current sample/runtime acceptance](test-disposition.md) separately retains
exact graphics, numeric progression and bounded deadlines with the current library.
The obsolete validation adapter initially lacked Cecil after retirement; its
dependencies were restored only in an isolated temporary copy before the six
successful kernel probes. No compiler/runtime regression test is bypassed.

## Provenance and reproduction

Raw90 samples, host/input metadata, adapter sources and untimed observations live
under the directory recorded in `/tmp/koh-current-perf-root.txt`; generated
measurements are not committed. The runner and three kernel sources are tracked
under `tools/benchmarks/compiler`. Extend the documented temporary core adapters
to accept trailing reference paths: C# passes them to CompilerInput.FromAssembly;
Rust reads those images and passes slices to Resolver::new. Each adapter uses
Release optimization; native LTO/stripping is enabled. Its registry versions
were checked against the workspace Cargo.lock.

For the two added workloads use plain net10.0 Release executable projects with
unsafe/implicit usings enabled and DebugType none, compiling each historical
sample's `*.cs`, adding the Koh.GameBoy global using and its historical project
reference. Pass that GameBoy DLL and the SDK's System.Runtime reference DLL to
both adapters. All builds complete with zero warnings/errors.

| Input | Bytes | SHA256 |
| --- | ---: | --- |
| Graphics game DLL |7168|`25b812dee690bb5c2dd66c51c42ce760b648984ee2c6447a06c18e7efdd037f3`|
| Original2048 game DLL |8192|`178197ad454d85d7a187f7f633df67933b962bef0deedc8632f18b3c1427fa25`|
| Shared historical GameBoy DLL |35840|`bb23d0861e9942cdf0255c5e86faf34483384757ef255dc60ab61ff508bdb838`|
| Shared System.Runtime reference |862504|`a94896915fb846bd6d393280559bbe9e0c81dfffc93a49ab4a3155dfe2d2939b`|

Windows/macOS performance is not measured. The old table and this refresh use
different sampling conditions, so their timing differences are not a regression
or improvement claim across native compiler revisions.
