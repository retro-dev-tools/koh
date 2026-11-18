# CLAUDE.md

Read [AGENTS.md](AGENTS.md) for repository conventions and working-tree safety.

## Toolchain

Koh combines native Rust assembler/linker/compiler tools with C# game authoring,
the .NET SDK, the existing C# emulator/debugger and KohUI. Roslyn compiles ordinary
C# to DLL/PDB; `koh-compile` lowers the supported CIL subset to canonical
ELF32/SM83; `koh-link` owns placement, relocation, headers and ROM/KDBG output.
There is one production compiler. No C# compiler implementation or fallback
remains. Existing public APIs and historical behavior are evidence, not specs.

## Build and tests

```sh
cargo nextest run --workspace --all-features --locked
cargo test --workspace --all-features --doc --locked
dotnet build Koh.Ci.slnf -c Release -m:1
dotnet msbuild build.proj -t:Test
```

Use the .NET10 SDK selected by `global.json`. Native compiler tests use rstest
and drive real Roslyn fixtures and external C# emulator/debugger processes.
Remaining .NET tests use TUnit and belong to live .NET components. The committed
nextest configuration caps expensive fixture concurrency and uses zero retries.
Do not run two builds in the same output directory. Read completed logs and
actual exit codes before claiming success. [CI commands](docs/ci-validation.md).

## Compiler invariants and evidence

- [Typed IR](docs/rust-compiler/ir.md): explicit types, edge arguments, dominance,
  local identity, memory/alias rules and verification before emission.
- [SM83 ABI](docs/rust-compiler/sm83-abi.md): guarded dynamic activation frames,
  scalar/aggregate widths, register aliasing, calls and return buffers.
- [ROM banking](docs/rust-compiler/rom-banking.md) and
  [interrupts](docs/rust-compiler/irq-calls.md): mapper ownership, saved state and
  independently bounded interrupt stack requirements.
- [Runtime boundaries](docs/rust-compiler/runtime-boundaries.md): distinguish
  malformed metadata from valid unsupported semantics; never omit reachable code
  or fabricate placeholder values. No general managed-runtime compatibility.
- [Test dispositions](docs/rust-compiler/test-disposition.md) and
  [progress](docs/rust-compiler/progress.md): historical source traceability,
  independent observations and pending acceptance. Discovery is not execution.

`rust/koh-objects` is the canonical object model. C# `Koh.Objects`/`Koh.Common`
serve live bridge/debugger consumers; they are not the compiler's IR or object
serializer. `Koh.Opcodes` supplies the C# debugger disassembler and standalone
tool instruction table. Do not copy deleted compiler models into these projects.

`Koh.GameBoy` remains the game-facing reference/runtime library. The compiler
reads its metadata contract and includes reachable CIL transitively. SDK build
tasks invoke native processes; useful tile-sheet generation stays in the SDK.
Actual game acceptance lives under `rust/koh-compile/tests`, including SDK builds,
real boot, memory/framebuffer behavior and compiler-origin source debugging.

## Other boundaries and gotchas

- Keep the C# emulator, Koh.Verify, debugger/DAP, LSP and UI implementations.
- Sample builds use `dotnet build samples/csharp/gb-2048-cs`; `dotnet run --project`
  launches the ROM through the SDK. Source is compiled once by Roslyn.
- Real boot needs valid linker-finalized cartridge headers. Skipping boot and
  setting PC/SP is a different test boundary; initial emulator state is not a
  hardware specification.
- Preserve sample assets. Test captures belong under test-output directories;
  curated JRPG screenshots change only after deliberate review.
- Boot tools use Koh.Native's host assembler/linker targets. Emulator publish
  defaults to the host RID; NativeAOT needs a compatible native host/toolchain.
- Close streams before renaming files on Windows.
- Keep NuGet auditing and warnings-as-errors; fix dependencies instead of
  suppressing advisories.
- Preserve protected CI check names when changing workflows. Final acceptance
  requires current-head results, not older green runs.
