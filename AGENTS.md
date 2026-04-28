# Repository Guidelines

## Project Structure & Module Organization

Koh is a .NET 10 Game Boy development toolchain. Main C# projects live in `src/`: compiler logic in `Koh.Core`, emission in `Koh.Emit`, CLIs in `Koh.Asm` and `Koh.Link`, linker logic in `Koh.Linker.Core`, LSP support in `Koh.Lsp`, and debugger/emulator/UI code in `Koh.Debugger`, `Koh.Emulator.*`, and `KohUI*`. Tests are under `tests/` and usually mirror source names, for example `Koh.Core.Tests`. VS Code extension sources and grammar assets are in `editors/vscode/src` and `editors/vscode/syntaxes`. Benchmarks are in `benchmarks/Koh.Benchmarks`.

## Build, Test, and Development Commands

- `dotnet build` builds the default solution.
- `dotnet restore Koh.Ci.slnf` and `dotnet build Koh.Ci.slnf --configuration Release` mirror CI's main build.
- `dotnet msbuild build.proj -t:Test` runs the standard test suite excluding compatibility tests.
- `dotnet msbuild build.proj -t:CompatTests` runs RGBDS compatibility tests after ROM fixtures are available.
- `dotnet msbuild build.proj -t:PublishDev` publishes binaries for VS Code debugging.
- `cd editors/vscode; npm ci; npm test` installs and runs the extension test harness.
- `./scripts/run-emulator.ps1` or `./scripts/run-emulator.sh` publishes and launches the emulator locally.

## Coding Style & Naming Conventions

C# uses `net10.0`, C# 14, nullable references, implicit usings, and `TreatWarningsAsErrors`. Keep namespaces and project names aligned with `Koh.*`. Use PascalCase for public types and members, camelCase for locals and parameters, and `Async` suffixes for asynchronous methods. TypeScript extension code is compiled with `tsc -p editors/vscode/tsconfig.json`; keep generated `out/`, `.vscode-test/`, `node_modules/`, `bin`, and `obj` out of reviews.

## Testing Guidelines

Add unit or integration coverage in the matching `tests/Koh.*.Tests` project. Name test classes after the component under test and use method names that state the behavior. For parser, assembler, linker, LSP, debugger, and emulator changes, include fixture-based regressions when behavior changes. Run the focused project test first, then `dotnet msbuild build.proj -t:Test`. Use compat tests when RGBDS compatibility or ROM behavior is affected.

## Commit & Pull Request Guidelines

Recent history follows Conventional Commits such as `feat(debug): ...`, `fix(ci): ...`, and `test(vscode): ...`; keep subjects imperative and scoped. Pull requests should describe behavior changes, list test commands run, link related issues, and include screenshots or recordings for VS Code UI/debugger changes. Note fixture, artifact, or benchmark changes explicitly.

## Security & Configuration Tips

Do not commit downloaded ROMs, local toolchains, VSIX packages, benchmark outputs, or test-host caches. Keep machine-specific paths in local VS Code settings or `koh.yaml` examples, not in shared project files.
