# Koh

A Game Boy and Game Boy Color development toolchain for RGBDS-style assembly and C#, built with Rust and .NET 10.

Koh combines an assembler, linker, language server, C# compiler, emulator, and debugger. Its goal is to take a game from source to a playable ROM with diagnostics and debugging in the same workflow.

C# compilation targets the Game Boy's SM83 processor; it does not run .NET on the console. Start with the checked-in samples: the compiler supports a constrained C# subset, not arbitrary .NET applications.

## Why Koh?

Traditional Game Boy tooling is fast and well established, but editor support is often limited and larger assembly projects can become harder to work with over time.

Koh is built to stay close to existing RGBDS-style workflows while providing compiler-backed tooling for a better development experience. That includes diagnostics, navigation, references, hover information, completions, and refactoring features driven by real semantic analysis rather than text-only heuristics.

## Tools

| Tool | Purpose |
|------|---------|
| `koh-asm` | Assembler for RGBDS-style Game Boy assembly |
| `koh-link` | Native Rust linker for ELF32/SM83 objects |
| `koh-lsp` | Language server for diagnostics, navigation, and refactoring |
| VS Code extension | VS Code integration for Koh projects |
| `Koh.Sdk` | MSBuild integration that compiles C# games to Game Boy ROMs |
| `Koh.Emulator.App` | Desktop emulator and debug adapter |

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
- ELF32/SM83 object output

### Linker

- linking across multiple object files
- ROM bank assignment
- section placement and alignment
- symbol resolution and patching
- `.sym` and `.kdbg` output
- canonical [ELF32/SM83 ABI](docs/rust-linker/elf32-sm83.md) with checked complex relocations

## Status

Koh is under active development. RGBDS assembly syntax compatibility is a goal, not a guarantee that every RGBDS project builds unchanged. Compiler and emulator behavior is covered by tests and sample games; this does not establish complete language or hardware compatibility.

Current priorities include:

- maintaining passing builds with dependency auditing enabled
- preserving sample assets while testing real ROM behavior
- RGBDS compatibility regressions backed by reference-tool results
- a reproducible source-to-ROM and debugging workflow

See the [roadmap](ROADMAP.md) for current gaps and acceptance criteria, and [CONTRIBUTING.md](CONTRIBUTING.md) for development setup and checks.

## Quick Start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Rust 1.85 or newer](https://www.rust-lang.org/tools/install) with Cargo
- [Node.js](https://nodejs.org) and npm only when developing the VS Code extension

Run commands below from the repository root. SDK selection is controlled by `global.json`.

### Build

```sh
dotnet build
```

### Build a playable sample

For assembly:

```sh
dotnet msbuild build.proj -t:BuildSample2048
```

The ROM is written to `samples/asm/gb-2048/build/2048.gbc`, with source debug information in `2048.kdbg`. See the [build and debug walkthrough](docs/build-and-debug.md) for emulator commands and a VS Code launch configuration.

For C#:

```sh
dotnet build samples/csharp/gb-2048-v2/Gb2048V2.csproj
```

The SDK writes `samples/csharp/gb-2048-v2/2048v2.gb`. See the [sample guide](samples/csharp/gb-2048-v2/README.md) for its game code and compiler requirements. These source builds restore dependencies; the local Koh.Sdk feed does not make the whole build offline.

Headless emulator captures use `--screenshot=frame.png --frames=300` with a ROM path. Screenshot output supports PNG only; other filename extensions are rejected. PNG encoding uses .NET directly and requires no image-library license key.

The native emulator uses SDL3's 2D Renderer API for presentation, windows and input. Native SDL libraries are included in platform publishes; OpenAL still provides audio. Headless DAP sessions use `--headless --dap=<endpoint>` and initialize neither video nor audio. See [SDL3 rendering and validation](docs/sdl3.md) for renderer selection, software fallback and platform verification limits.

If restore fails with `NU1902` or `NU1903`, a dependency audit has found a vulnerability. Report the package, version, and advisory URL; do not disable auditing to obtain a passing result. See the [roadmap](ROADMAP.md) for verification status.

### Test

See [CI validation](docs/ci-validation.md) for PR checks and the full merge gate.

```sh
dotnet msbuild build.proj -t:Test
```

### Common build targets

Invoke as `dotnet msbuild build.proj -t:<Target>` (no external tool install).

| Target                | Description                                                 |
| --------------------- | ----------------------------------------------------------- |
| `Test`                | Run the .NET component tests |
| `TestAll`             | Run all native tests, Rust doctests and .NET component tests |
| `BuildSample2048`     | Build the assembly 2048 sample ROM                          |
| `Benchmark`           | Run benchmarks                                              |
| `PublishDev`          | Publish the LSP server for local VS Code debugging          |
| `PublishEmulatorApp`  | NativeAOT-publish the KohUI emulator for the host (`-p:EmulatorRuntimeIdentifier=<rid>` to override) |
| `RunEmulator`         | Publish the emulator and launch the host executable         |

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

## Samples

| Sample | Description |
|--------|-------------|
| [`samples/ui/KohUI.Demo`](samples/ui/KohUI.Demo/) | Counter app showing off KohUI (the .NET UI framework strand of Koh) |
| [`samples/asm/gb-2048`](samples/asm/gb-2048/) | 2048 game ROM for Game Boy Color demonstrating multi-bank assembly, GBC hardware features, and SRAM saves |
| [`samples/csharp/gb-2048-v2`](samples/csharp/gb-2048-v2/) | C# 2048 using scenes, input, and tile assets |
| [`samples/csharp/gb-jrpg`](samples/csharp/gb-jrpg/) | CGB mini-JRPG using PNG assets, sprites, dialogue, and encounters |

Build the GB sample with `dotnet msbuild build.proj -t:BuildSample2048`.

## Repository Layout

```text
rust/         koh-asm, koh-link, koh-compile (CIL compiler), koh-objects (ELF ABI)
src/
  Common/     Koh.Common (diagnostics, source text), Koh.Opcodes (SM83 opcode table),
              Koh.Objects (compiler output concepts and .kdbg reader)
  Asm/        Koh.Native (native bridge), Koh.Lsp (language server)
  Compiler/   Koh.GameBoy (runtime), Koh.Build.Tasks, Koh.Sdk
  Emulator/   Koh.Emulator, Koh.Debugger, Koh.Boot (boot ROMs), Koh.Verify, Koh.Emulator.App
  UI/         KohUI, KohUI.Backends.Sdl3, KohUI.Backends.Dom
tests/        mirrors src/ by area; tests/fixtures holds external test ROMs
tools/        Koh.Superopt, benchmarks/
samples/      asm/, csharp/, ui/
editors/
  vscode/     VS Code extension
```

## Project Direction

Koh aims to make small Game Boy games practical to build, understand, and debug in assembly or C#. Reliable sample games, clear compatibility limits, and reproducible builds take priority over adding more tools or language features. See the [roadmap](ROADMAP.md).

## Native Rust assembler

The native Rust assembler is built with `cargo build --release -p koh-asm` and
tested with `cargo nextest run --workspace --all-features --locked` and separate
`cargo test --workspace --all-features --doc --locked`. It replaces the C# assembler and CLI; the
C# language server uses the native service. See [Rust assembler status and validation](docs/rust-assembler.md)
and [object-format decisions](docs/rust-assembler-objects.md).

## License

MIT
