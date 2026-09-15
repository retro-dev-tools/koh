# Repository Guidelines

## Project Structure & Module Organization

Koh is a .NET 10 Game Boy development toolchain. C# projects live in `src/`, grouped by area: `Common/` (`Koh.Common` for diagnostics and source text, `Koh.Opcodes` for the SM83 opcode table, `Koh.Objects` for the shared object model and the `.kobj`/RGBDS/`.kdbg` formats), `Asm/` (`Koh.Assembler`, the `Koh.Asm` CLI, `Koh.Lsp`), `Link/` (`Koh.Linker`, the `Koh.Link` CLI), `Compiler/` (`Koh.Compiler`, `Koh.GameBoy`, `Koh.Build.Tasks`, `Koh.Sdk`), `Emulator/` (`Koh.Emulator`, `Koh.Debugger`, `Koh.Boot`, `Koh.Verify`, `Koh.Emulator.App`) and `UI/` (`KohUI*`). Tests under `tests/` mirror the same areas and project names, for example `tests/Asm/Koh.Assembler.Tests`. VS Code extension sources and grammar assets are in `editors/vscode/src` and `editors/vscode/syntaxes`. Tools and benchmarks are in `tools/`, samples in `samples/{asm,csharp,ui}`.

## Build, Test, and Development Commands

- `dotnet build` builds the default solution.
- `dotnet restore Koh.Ci.slnf` and `dotnet build Koh.Ci.slnf --configuration Release` mirror CI's main build.
- `dotnet msbuild build.proj -t:Test` runs the fast suite (`Koh.Fast.slnf`) — everything except `Koh.Compiler.Tests`. This is the default development loop.
- `dotnet msbuild build.proj -t:TestAll` adds `Koh.Compiler.Tests`, whose 522 cases each Roslyn-compile a fixture to a real assembly and run an emulator over it. It is memory-hungry enough to take a desktop down; its parallelism is capped at 4 (`tests/Compiler/Koh.Compiler.Tests/ParallelLimit.cs`). CI runs this one.
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
