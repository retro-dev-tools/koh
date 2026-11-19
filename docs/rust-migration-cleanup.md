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

## Required C# and retirement scope

| Area | Disposition |
| --- | --- |
| `Koh.GameBoy`, samples and compiler fixtures | C# source language and compiler/runtime contracts |
| `Koh.Build.Tasks`, `Koh.Sdk` | .NET SDK/MSBuild integration |
| `Koh.Native`, `Koh.Common`, `Koh.Objects` | Retire managed bridge and duplicate tests; retain native build glue |
| Managed bridge/emulator benchmarks and `Koh.Superopt` | Decommission; retain native compiler measurement workloads |
| `Koh.Opcodes` | Retire with managed superoptimizer and duplicate disassembler tests |
| `Koh.Emulator`, `Koh.Boot` | Retire managed libraries after verification callers move; keep boot assembly |
| `Koh.Verify`, 2048 verifier | Replace the live workflow with the native emulator |
| `tools/rust-asm-verify` execution helper | Replaced by `koh-test-host AssemblerVerify execute <rom>` |
| C# oracle capture sources | Retire managed-emulator capture programs; retain independent .NET semantic oracles |

The user's 2026-11-19 clarification expands PR62 to all nonessential C# and
supersedes the earlier [engine design](rust-engine/design.md) exclusions.
The [retirement plan](superpowers/plans/2026-11-19-managed-retirement.md) tracks
the remaining work. This audit does not claim the whole migration is complete.

## Local validation

Cleanup sources built with `dotnet build Koh.Ci.slnf -c Release -m:1`:
zero warnings and errors. The retained managed suite passed 297/297, zero skips,
using `dotnet test --solution Koh.Ci.slnf -c Release --no-build
--max-parallel-test-modules 1`. CSharpier checked the retained bridge/common code.
These checks ran in the original checkout before transfer to the cleanup branch;
hosted validation of the draft PR remains required.

The native assembler verifier passes four focused tests, including bounded
execution failure and malformed input. Independent RGBDS 1.0.1 verification
passes seven complete-ROM comparisons and 18 invalid-input pipelines through
the native helper, with no .NET executable on PATH.
