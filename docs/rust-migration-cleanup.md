# Managed-code retirement audit

The production assembler, linker, CIL compiler, language server, emulator,
debugger and desktop use the Cargo workspace. C# game authoring and MSBuild
integration remain supported.

## Unused managed code removed

- The former LSP semantic model, symbol wrappers and analysis cache.
- Managed syntax node/token/trivia wrappers and their syntax-kind enum.
- Unused protocol models for those wrappers; the native worker protocol is unchanged.
- The virtual source resolver, text-change API and banked-address record.
- The unused `Microsoft.Extensions.Logging.Debug` central package version.

Repository callers of the removed APIs were checked before deletion. The retained
native bridge still parses inputs, resolves includes, assembles ELF and links ROMs
for managed integration tests and benchmarks.

## Remaining managed dependencies

| Area | Live reason |
| --- | --- |
| `Koh.GameBoy`, samples and compiler fixtures | C# source language and compiler/runtime contracts |
| `Koh.Build.Tasks`, `Koh.Sdk` | .NET SDK/MSBuild integration |
| `Koh.Native`, `Koh.Common`, `Koh.Objects` | Managed assembler/linker integration tests and benchmarks |
| `Koh.Opcodes` | Superoptimizer instruction alphabet and disassembler tests |
| `Koh.Emulator`, `Koh.Boot` | Verification harness, superoptimizer, benchmarks and compatibility tests |
| `Koh.Verify`, 2048 verifier, `tools/rust-asm-verify` | Existing verification workflows |
| C# oracle capture sources | Reproducible compatibility fixtures for native tests |

The [engine design](rust-engine/design.md) explicitly excludes verification-tool
ports and retains their managed dependencies. Retiring those live tools requires
resolving that scope before removing their dependencies. This audit does not
claim the whole migration is complete.

## Local validation

Cleanup sources built with `dotnet build Koh.Ci.slnf -c Release -m:1`:
zero warnings and errors. The retained managed suite passed 297/297, zero skips,
using `dotnet test --solution Koh.Ci.slnf -c Release --no-build
--max-parallel-test-modules 1`. CSharpier checked the retained bridge/common code.
These checks ran in the original checkout before transfer to the cleanup branch;
hosted validation of the draft PR remains required.
