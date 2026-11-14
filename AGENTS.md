# Repository Guidelines

## Project Structure & Module Organization

Koh is a .NET 10 Game Boy development toolchain. C# projects live in `src/`, grouped by area: `Common/` (`Koh.Common` for diagnostics and source text, `Koh.Opcodes` for the SM83 opcode table, `Koh.Objects` for the shared object model and the `.kobj`/RGBDS/`.kdbg` formats), `Asm/` (`Koh.Native` native-service bridge, `Koh.Lsp`), `Link/` (`Koh.Linker`, the `Koh.Link` CLI), `Compiler/` (`Koh.Compiler`, `Koh.GameBoy`, `Koh.Build.Tasks`, `Koh.Sdk`), `Emulator/` (`Koh.Emulator`, `Koh.Debugger`, `Koh.Boot`, `Koh.Verify`, `Koh.Emulator.App`) and `UI/` (`KohUI*`). Tests under `tests/` mirror the same areas and project names, for example `tests/Asm/Koh.Lsp.Tests`. VS Code extension sources and grammar assets are in `editors/vscode/src` and `editors/vscode/syntaxes`. The Rust assembler and CLI live in `rust/koh-asm` in the root Cargo workspace. Tools and benchmarks are in `tools/`, samples in `samples/{asm,csharp,ui}`.

## Build, Test, and Development Commands

- `cargo build --release -p koh-asm --locked` builds the native assembler.
- `cargo test --workspace --locked` runs Rust tests; `KOH_RGBDS_TEST=1` enables checks against RGBDS 1.0.1.
- `dotnet build` builds the default solution, including the native assembler bridge.
- `dotnet restore Koh.Ci.slnf` and `dotnet build Koh.Ci.slnf --configuration Release` mirror CI's main build.
- `dotnet msbuild build.proj -t:Test` runs the fast suite (`Koh.Fast.slnf`) — everything except `Koh.Compiler.Tests`. This is the default development loop.
- `dotnet msbuild build.proj -t:TestAll` adds `Koh.Compiler.Tests`, whose cases Roslyn-compile fixtures to real assemblies and run an emulator over them. It is memory-hungry enough to take a desktop down; its parallelism is capped at 4 (`tests/Compiler/Koh.Compiler.Tests/ParallelLimit.cs`). CI runs this one.
- `dotnet msbuild build.proj -t:PublishDev` publishes binaries for VS Code debugging.
- `cd editors/vscode; npm ci; npm test` installs and runs the extension test harness.
- `./scripts/run-emulator.ps1` or `./scripts/run-emulator.sh` publishes and launches the emulator locally.

## Coding Style & Naming Conventions

C# uses `net10.0`, C# 14, nullable references, implicit usings, and `TreatWarningsAsErrors`. Formatting is done with [CSharpier](https://csharpier.com) (pinned in `.config/dotnet-tools.json`) and applied on commit by a [Husky.NET](https://alirezanet.github.io/Husky.Net/) pre-commit hook (installed on your first `dotnet restore`/build) that formats staged C# and re-stages it into the same commit. Run `dotnet csharpier format .` to format the whole tree by hand, or `dotnet csharpier check .` to verify. Keep namespaces and project names aligned with `Koh.*`. Use PascalCase for public types and members, camelCase for locals and parameters, and `Async` suffixes for asynchronous methods. TypeScript extension code is compiled with `tsc -p editors/vscode/tsconfig.json`; keep generated `out/`, `.vscode-test/`, `node_modules/`, `bin`, and `obj` out of reviews.

## Testing Guidelines

Add unit or integration coverage in the matching `tests/Koh.*.Tests` project. Name test classes after the component under test and use method names that state the behavior. For parser, assembler, linker, LSP, debugger, and emulator changes, include fixture-based regressions when behavior changes. Run the focused project test first, then `dotnet msbuild build.proj -t:Test`.

## Commit & Pull Request Guidelines

Recent history follows Conventional Commits such as `feat(debug): ...`, `fix(ci): ...`, and `test(vscode): ...`; keep subjects imperative and scoped. Pull requests should describe behavior changes, list test commands run, link related issues, and include screenshots or recordings for VS Code UI/debugger changes. Note fixture, artifact, or benchmark changes explicitly.

## Security & Configuration Tips

Do not commit downloaded ROMs, local toolchains, VSIX packages, benchmark outputs, or test-host caches. Keep machine-specific paths in local VS Code settings or `koh.yaml` examples, not in shared project files.

## Maintainer Context

- Keep the active priorities and completed milestones in root `ROADMAP.md`.
- Preserve the dependency boundaries: compiler and linker share `Koh.Objects` and `Koh.Common`; neither depends on assembler implementation details.
- Prioritize a reproducible assembly/C# source-to-ROM and debugging workflow over new subsystems.
- Keep NuGet auditing and warnings-as-errors enabled. Update the owning dependency or supported SDK instead of suppressing an advisory.
- `global.json` requires SDK 10.0.115 or a compatible newer .NET 10 SDK. Keep auditing enabled across SDK feature bands: a higher SDK feature band does not guarantee a newer NativeAOT runtime patch.
- Preserve pre-existing edits. Stage only files belonging to the current increment; do not merge into the default branch or push without authorization.
- Licensing and third-party attribution are release blockers; unrelated engineering can continue.
- Run builds serially in each output directory. On constrained machines, use `dotnet build Koh.Ci.slnf -c Release -m:1`, then `dotnet test --solution Koh.Fast.slnf -c Release --no-build --max-parallel-test-modules 1`.
- Read completed logs and record actual exit codes. Never infer test success from an empty log or pipe status.
- Keep code comments short, without prose or implementation detail.
- Headless screenshots use the emulator framebuffer's PNG writer. Keep compression in the platform libraries and test pixel order, alpha, PNG structure, and checksums.
- Tests must not write tracked sample assets. JRPG captures belong under the test binary's `TestResults/gb-jrpg/<run-id>/`; curated sample screenshots are updated manually after review.
- For a constrained full-suite run, build first, then use `dotnet test --solution Koh.Ci.slnf -c Release --no-build --max-parallel-test-modules 1 -- --maximum-parallel-tests 1`.
- Root emulator publish/run targets default to the host runtime identifier. Override publishing with `-p:EmulatorRuntimeIdentifier=<rid>`; NativeAOT still requires a compatible native host/toolchain. CI exercises the root publish target on Linux, Windows, and macOS.
