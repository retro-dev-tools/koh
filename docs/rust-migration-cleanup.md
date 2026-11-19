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

Repository callers of the removed APIs were checked before deletion. The native
bridge's C# source and all managed common/object libraries are now retired;
native tests exercise parse, includes, ELF assembly and ROM linking directly.

## Required C# and retirement scope

| Area | Disposition |
| --- | --- |
| `Koh.GameBoy`, samples and compiler fixtures | C# source language and compiler/runtime contracts |
| `Koh.Build.Tasks`, `Koh.Sdk` | .NET SDK/MSBuild process wrappers; PNG/tile algorithms run in Rust |
| `Koh.Native`, `Koh.Common`, `Koh.Objects` | C# source and reference tests retired; native build/NuGet glue remains |
| Managed bridge/emulator benchmarks and `Koh.Superopt` | Decommissioned; native compiler measurement workloads remain |
| `Koh.Opcodes` | Retired with managed superoptimizer; native debugger retains the 512-encoding oracle |
| `Koh.Emulator`, `Koh.Boot` | Managed libraries/tests retired; boot assembly remains input to the native build |
| `Koh.Verify`, 2048 verifier | Replaced by `koh-test-host Verify2048`, including native PNG/GIF/contact output |
| `tools/rust-asm-verify` execution helper | Replaced by `koh-test-host AssemblerVerify execute <rom>` |
| C# oracle capture sources | Managed-emulator capture programs retired; independent .NET semantic oracles remain |

The user's 2026-11-19 clarification expands PR62 to all nonessential C# and
supersedes the earlier [engine design](rust-engine/design.md) exclusions.
The [retirement plan](superpowers/plans/2026-11-19-managed-retirement.md) tracks
the remaining work. This audit does not claim the whole migration is complete.

The tracked source census contains 159 C# files: 35 game API files, two MSBuild
process wrappers, 22 game sample files, 97 compiler inputs/CLR semantic oracles,
and three native-compiler measurement inputs. No C# source remains outside those
owners. The build-only `Koh.Native.csproj` retains native NuGet/build integration;
it contains no C# source and no managed bridge API.

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

The native 2048 verifier passes the same 11 game-state checks as the managed
reference. All 63 captured state/animation images match the managed framebuffer
pixels exactly, including each frame of all four slides. The presentation driver
has tests for pixel positions, PNG dimensions, GIF timing and malformed images.
All 11 complete PNG artifacts, including labels, and the 13-frame/780ms GIF
playback match the managed reference exactly. PNG/GIF codecs run in Rust;
the optional Python launcher uses only the standard library. Pillow is used
solely for independent artifact tests.

SDK PNG reading and tile conversion now run through `koh-compile --tile-sheets`.
The MSBuild task only passes metadata/paths and logs process results. Native
tests cover formats, all five filters, alpha, RGB555/2bpp data and failure
preservation. An isolated actual SDK fixture builds a 32768-byte ROM with zero
warnings/errors; invalid assets preserve the prior source and ROM. A tiny CLR
semantic oracle checks all 63,488 BMP scalars, retaining original .NET filename
casing/digit behavior, including Unicode cases found during review.

Managed benchmark discovery and the standalone experimental superoptimizer are
decommissioned rather than advertised as native replacements. Neither was a
production compiler dependency. The managed opcode decoder/tests and its stale
capture program are retired; `koh-debugger` still checks all 512 encodings against
the captured independent JSON oracle. Historical capture source remains in Git.

## Managed test retirement

The 257 tests still present after development-tool retirement exercised the
managed reference emulator/boot and the managed object/assembler/linker bridge.
Those test projects are decommissioned with their implementations; they are not
counted as passing native tests. Native component tests, captured compatibility
oracles, ELF/protocol/CLI checks and Roslyn/ROM integration remain the gates.

Real boot contracts were explicitly moved to `rust/koh-emulator/tests/boot.rs`:
image shape/family, instruction-level canonical register handoff, actual logo
execution, VRAM/palettes/APU state, animation duration, wrong-family rejection and
corrupted-logo/checksum lockup. The four tests cover both hardware families and
CGB handling of a DMG cartridge, without fabricated post-boot state.

The engine disposition CSV records retired reference cases and their native
component owners. Those component pointers do not assert a one-to-one test port;
the original reference source remains available at the recorded baseline commit.
