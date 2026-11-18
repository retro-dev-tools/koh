# Legacy removal candidate validation

This candidate removes the complete `src/Compiler/Koh.Compiler` and
`tests/Compiler/Koh.Compiler.Tests` trees. Their solution/filter entries and the
direct Roslyn/Cecil package versions are removed. The unused Testcontainers
version is removed too; no project consumed it. The remaining runtime, SDK
tile-sheet generation, emulator/debugger, native bridge and standalone tools
are retained. `TestAll` invokes cargo-nextest and separate rustdoc before live
.NET component tests. Ordinary `Test` remains the .NET component loop.

The source ledger and current-head platform acceptance are separate gates. This
candidate is prepared in an isolated checkout while the PR branch's unfiltered
native run completes. Its deletion is not yet published to the PR branch or a
claim that all migration completion criteria pass.

## Linux build

```sh
CARGO_BUILD_JOBS=1 dotnet build Koh.Ci.slnf -c Release -m:1 --nologo
```

The completed build passed, exit0, in5m00.70s with zero warnings and zero errors.
Log: `/tmp/koh-removal-solution-build.log`. It builds the native executables from
this checkout, the C# SDK/build/runtime projects, retained components and these
actual C# sample ROMs:

| Sample | ROM bytes |
| --- | ---: |
| Original2048 |65536|
| Scene2048 |131072|
| Double-buffered cube |131072|
| Full-frame cube |65536|
| Racing-beam cube |65536|
| Graphics showcase |65536|
| JRPG |131072|

These sizes prove produced artifacts, not game behavior or a ROM-size win.
Observable game/source-debug acceptance remains owned by the native integration
tests and final platform validation.

The retained .NET suite is running with no rebuild and one module/test at a time:

```sh
dotnet test --solution Koh.Ci.slnf -c Release --no-build \
  --max-parallel-test-modules 1 -- --maximum-parallel-tests 1
```

The completed suite passed, exit0, in1m04.813s:569 total,567 succeeded, zero
failed, two skipped. Log: `/tmp/koh-removal-dotnet-tests.log`. The two unchanged
SDL native-window/input cases require `KOH_SDL_WINDOW_TEST=1`; this headless
component run does not claim native window/input acceptance. Their dedicated
CI renderer job remains. The original native workspace run and separate
doctests remain pending. No older green run substitutes for current-input results.

## Boundary checks

No project, target, solution or workflow references Koh.Compiler after removal.
The negative standalone-tool dependency test deliberately retains the assembly
name as a forbidden runtime dependency. Historical source names in disposition
matrices are provenance, not dormant implementations. The committed SDK nupkg
contains only repository forwarding props/targets, not a compiler assembly or
fallback. C# EmitModel/section/symbol bridge types remain because Koh.Native's
assembler/linker and LSP consumers still use them; they are not compiler-only
helper types.

A tracked-file container scan found no Docker/Podman/Testcontainers dependency
outside historical/compiler-audit documentation and incidental third-party npm
metadata. The active host-native benchmark project remains. Final release
archive contents, licensing and cross-platform tests are still acceptance gates.

## Minimum Rust version

The workspace now requires Rust1.88. The compiler uses let-chain syntax and
integer APIs newer than the previous advertised1.85 requirement. Rust1.88.0
checks the complete workspace with `--all-targets --all-features --locked`, exit0,
in1m36s using a separate target directory. The selected compiler is explicitly
`rustc1.88.0 (6b00bc388 2025-06-23)`; both RUSTC and RUSTDOC point at that
toolchain instead of the host's Gentoo compiler. Completed log:
`/tmp/koh-msrv-check.log`. One existing unused cube-test `build_host` warning
remains. This is minimum-version compilation evidence, not test execution on
that compiler or cross-platform evidence. README/build-debug requirements match
the Cargo manifest. No extra per-push CI job is added.
