# Koh

A modern Game Boy development toolchain for RGBDS-style assembly, built on .NET 10.

Koh combines an assembler, a linker, and a language server so Game Boy and Game Boy Color projects can be built, checked, and navigated with modern tooling.

## Why Koh?

Traditional Game Boy tooling is fast and well established, but editor support is often limited and larger assembly projects can become harder to work with over time.

Koh is built to stay close to existing RGBDS-style workflows while providing compiler-backed tooling for a better development experience. That includes diagnostics, navigation, references, hover information, completions, and refactoring features driven by real semantic analysis rather than text-only heuristics.

## Tools

| Tool | Purpose |
|------|---------|
| `koh-asm` | Assembler for RGBDS-style Game Boy assembly |
| `koh-link` | Linker for Koh and RGBDS object files |
| `koh-lsp` | Language server for diagnostics, navigation, and refactoring |
| VS Code extension | VS Code integration for Koh projects |

## Features

### Language server

- diagnostics as you type
- go-to-definition
- find references
- hover information
- completions
- document symbols
- rename
- semantic highlighting
- inlay hints
- signature help
- support for multiple configured projects in one workspace

### Assembler

- RGBDS-style assembly support
- full SM83 instruction set
- sections, banking, and alignment handling
- macros and conditional assembly
- expressions, directives, and assertions
- object or ROM output

### Linker

- linking across multiple object files
- ROM bank assignment
- section placement and alignment
- symbol resolution and patching
- `.sym` output
- support for Koh and RGBDS object formats

## Status

Koh is under active development.

Current priorities include:

- RGBDS compatibility
- solid compiler and linker foundations
- semantic editor tooling
- workspace support through `koh.yaml`

## Quick Start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org) for the VS Code extension

### Build

```sh
dotnet build
````

### Test

```sh
dotnet msbuild build.proj -t:Test
```

### Common build targets

Invoke as `dotnet msbuild build.proj -t:<Target>` (no external tool install).

| Target                | Description                                                 |
| --------------------- | ----------------------------------------------------------- |
| `Test`                | Run the full test suite (excludes compat)                   |
| `CompatTests`         | Run RGBDS compatibility tests                               |
| `Benchmark`           | Run benchmarks                                              |
| `PublishDev`          | Publish the LSP server for local VS Code debugging          |
| `PublishEmulatorApp`  | NativeAOT-publish the KohUI emulator (`-r <rid>`; default `win-x64`) |
| `RunEmulator`         | Publish the emulator and launch the `.exe`                  |

Quick-launch the emulator:

```powershell
./scripts/run-emulator.ps1     # Windows PowerShell
./scripts/run-emulator.sh      # bash / git-bash
```

## VS Code

To run the extension locally:

```sh
dotnet msbuild build.proj -t:PublishDev
```

Then open `editors/vscode/` in VS Code and press `F5`.

For workspace-based projects, place a `koh.yaml` file in the repository root:

```yaml
version: 1
projects:
  - name: game
    entrypoint: src/game.asm
```

If `koh.yaml` is not present, the language server can fall back to project discovery heuristics.

## Example

```asm
SECTION "Main", ROM0[$100]

EntryPoint:
    nop
.loop
    jr .loop
```

## Repository Layout

```text
src/
  Koh.Core/           # Compiler core
  Koh.Emit/           # Binary and object emission
  Koh.Asm/            # Assembler CLI
  Koh.Linker.Core/    # Linker core
  Koh.Link/           # Linker CLI
  Koh.Lsp/            # Language server
editors/
  vscode/             # VS Code extension
tests/                # Unit, integration, and compatibility tests
benchmarks/           # Performance benchmarks
```

## Project Direction

Koh is intended to provide a complete development experience for Game Boy assembly projects: reliable builds, strong compatibility, and language tooling that scales to larger codebases.

## License

MIT