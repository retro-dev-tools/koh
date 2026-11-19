# Repository Guidelines

## Project Structure & Module Organization

Koh is a Rust toolchain with .NET 10 game authoring. The Cargo workspace owns the assembler, linker, CIL compiler, language server, emulator, debugger, execution/test hosts and GPUI desktop. `rust/koh-objects` owns the canonical ELF32/SM83 model. C# under `src/Compiler` provides Koh.GameBoy and SDK/MSBuild integration; samples and native compiler fixtures remain C# input programs. `src/Asm/Koh.Native` contains native build/publish targets and NuGet asset integration, with no C# source. Tests live in the owning Rust crates. VS Code sources are under `editors/vscode`, tools under `tools/`, and samples under `samples/{asm,csharp}`.

## Build, Test, and Development Commands

- `cargo build --release -p koh-asm -p koh-link --locked` builds the native assembler and linker.
- `cargo nextest run --workspace --all-features --locked` runs native tests; `cargo test --workspace --all-features --doc --locked` runs doctests separately. `KOH_RGBDS_TEST=1` enables RGBDS 1.0.1 reference checks.
- `dotnet build` builds the .NET game APIs, SDK integration, samples and native build glue.
- `dotnet restore Koh.Ci.slnf` and `dotnet build Koh.Ci.slnf --configuration Release` mirror CI's main build.
- `dotnet msbuild build.proj -t:Test` runs native tests and separate Rust doctests.
- `just test` builds .NET inputs and runs native tests and doctests. Native compiler tests invoke Roslyn and the native emulator; nextest limits their concurrency with zero retries.
- `dotnet msbuild build.proj -t:PublishDev` publishes binaries for VS Code debugging.
- `dotnet msbuild build.proj -t:PublishLanguageServer` publishes native `koh-lsp` and its assembler worker; `LanguageServerRuntimeIdentifier` and `LanguageServerPublishDir` override the target and destination.
- `cd editors/vscode; npm ci; npm test` installs and runs the extension test harness.
- `just run [ROM/options...]` publishes and launches the emulator locally.

## Coding Style & Naming Conventions

C# uses `net10.0`, C# 14, nullable references, implicit usings, and `TreatWarningsAsErrors`. Formatting is done with [CSharpier](https://csharpier.com) (pinned in `.config/dotnet-tools.json`) and applied on commit by a [Husky.NET](https://alirezanet.github.io/Husky.Net/) pre-commit hook (installed on your first `dotnet restore`/build) that formats staged C# and re-stages it into the same commit. Run `dotnet csharpier format .` to format the whole tree by hand, or `dotnet csharpier check .` to verify. Keep namespaces and project names aligned with `Koh.*`. Use PascalCase for public types and members, camelCase for locals and parameters, and `Async` suffixes for asynchronous methods. TypeScript extension code is compiled with `tsc -p editors/vscode/tsconfig.json`; keep generated `out/`, `.vscode-test/`, `node_modules/`, `bin`, and `obj` out of reviews.

## Testing Guidelines

Add coverage in the owning Rust crate, using rstest for meaningful parameterized cases. SDK and game-library contracts belong in native compiler integration tests. Check observable behavior and explicit diagnostics. Run focused checks first; Roslyn/emulator integration tests remain discoverable by cargo-nextest.

## Commit & Pull Request Guidelines

Recent history follows Conventional Commits such as `feat(debug): ...`, `fix(ci): ...`, and `test(vscode): ...`; keep subjects imperative and scoped. Pull requests should describe behavior changes, list test commands run, link related issues, and include screenshots or recordings for VS Code UI/debugger changes. Note fixture, artifact, or benchmark changes explicitly.

## Security & Configuration Tips

Do not commit downloaded ROMs, local toolchains, VSIX packages, benchmark outputs, or test-host caches. Keep machine-specific paths in local VS Code settings or `koh.yaml` examples, not in shared project files.

## Maintainer Context

- Keep the active priorities and completed milestones in root `ROADMAP.md`.
- Preserve native compiler/linker boundaries through `koh-objects`; neither depends on assembler implementation details. C# SDK tasks invoke native executables and contain no compiler algorithms.
- Prioritize a reproducible assembly/C# source-to-ROM and debugging workflow over new subsystems.
- Keep NuGet auditing and warnings-as-errors enabled. Update the owning dependency or supported SDK instead of suppressing an advisory.
- `global.json` requires SDK 10.0.115 or a compatible newer .NET 10 SDK. Keep auditing enabled across SDK feature bands: a higher SDK feature band does not guarantee a newer NativeAOT runtime patch.
- Preserve pre-existing edits. Stage only files belonging to the current increment; do not merge into the default branch or push without authorization.
- Licensing and third-party attribution are release blockers; unrelated engineering can continue.
- Run builds serially in each output directory. On constrained machines, use `dotnet build Koh.Ci.slnf -c Release -m:1`, then `cargo nextest run --workspace --all-features --locked`.
- Read completed logs and record actual exit codes. Never infer test success from an empty log or pipe status.
- Keep code comments short, without prose or implementation detail.
- Headless screenshots use the emulator framebuffer's PNG writer. Keep compression in the platform libraries and test pixel order, alpha, PNG structure, and checksums.
- Tests must not write tracked sample assets. JRPG captures belong under the test binary's `TestResults/gb-jrpg/<run-id>/`; curated sample screenshots are updated manually after review.
- For a constrained full-suite run, build first, then use `cargo nextest run --workspace --all-features --locked` and run doctests separately.
- Root emulator publish/run targets default to the host runtime identifier. Override publishing with `-p:EmulatorRuntimeIdentifier=<rid>`; native publishing requires a compatible host/toolchain. CI exercises the root publish target on Linux, Windows, and macOS.
