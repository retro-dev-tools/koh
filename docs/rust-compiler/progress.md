# Native CIL compiler migration (PR #55)

Status: source audit and empirical baseline recorded; native raw metadata/body
reader, CIL encoding decoder and bounded signatures implemented. No
executable native compiler is integrated yet.
This document tracks the single replacement PR; it does not establish acceptance.

## Inspected baseline

- Integrated master: `9cd147265eb0c660d94c7bf4eee6a97d9e1f4e1f`.
- Linker migration [PR #54](https://github.com/retro-dev-tools/koh/pull/54) merged on
  2026-11-14. GitHub state and remote master were checked before branching.
- Cargo workspace: `koh-asm`, `koh-objects`, `koh-link`; no compiler crate.
- Canonical object boundary: `koh_objects::Object`, `validate`, `write_elf`.
  Compiler code must reuse this boundary rather than serialize ELF itself.
- The live compiler remains `src/Compiler/Koh.Compiler`: 68 C# files,
  19,876 source lines at this baseline. These are inventory counts, not complexity
  estimates or a claim that every file has been audited.
- `CompileKohRom` invokes `CompilerRegistry`/`CompilerDriver` in process, then
  `Koh.Native.NativeLinker`. `Koh.Build.Tasks` references the compiler project.
- `CilFrontend` uses Mono.Cecil, resolves directories derived from reference
  paths, and optionally reads matching symbols. This does not prove correct
  assembly identity resolution or establish the native reader contract.
- `CompilerDriver` optimizes unless frontend errors exist, but calls the backend
  even after frontend errors. It does not invoke the IR verifier.
- Compiler tests occupy 72 C# files and 24,834 source lines. Inventory and semantic
  disposition are separate steps; no coverage is migrated yet.
- Docker benchmark scripts remain tracked. The CI also uses
  `tools/benchmarks/Koh.Benchmarks`; deleting the entire benchmark directory would
  remove an active CI consumer. Audit consumers individually.

## Commit-sized work sequence

Each row is an intended coherent increment in this PR. Split further when review
or correctness needs it; no intermediate increment is a completed replacement.

| Order | Increment | Required evidence |
| --- | --- | --- |
| 1 | Record baseline, scope and progress | Integrated linker migration and preserved unrelated work |
| 2 | Inventory original tests | Exact source identities and explicit unaudited counts |
| 3 | Complete semantic audit and select supported contract | Reviewed test dispositions, independent semantics, library coverage evaluation, sample feature requirements |
| 4 | Native metadata/reference/PDB reader | Real Debug/Release Roslyn fixtures, malformed inputs, identity resolution, no assembly execution |
| 5 | Typed IR, lowering and verification | Stack merges, dominance, types, reachability, diagnostics, source identity |
| 6 | SM83 ABI and executable code generation | Independent CPU/memory expectations, arithmetic edge cases, calls, frames, interrupts, capacity failures |
| 7 | Runtime and game feature completion | Transitive Koh.GameBoy code, sample behavior, supported contract coverage |
| 8 | Native SDK/build/debug integration | Ordinary dotnet build/run, paths with spaces, incremental behavior, PDB to KDBG, debugger execution |
| 9 | Finish Rust test migration | Every compiler-owned scenario disposition resolved, rstest, nextest discovery and CI, separate doctests |
| 10 | Remove C# compiler and obsolete integration | No production fallback, obsolete references/packages/solutions removed, useful build tasks retained |
| 11 | Bounded Docker cleanup and performance evidence | Consumer audit, equivalent workloads, host measurements |
| 12 | Final validation and PR evidence | Samples executed, supported hosted platforms, known limitations, final-tree logs |

Implementation and tests should land together within increments. The test migration
row means completion of coverage, not postponement of all testing to the end.
Delete the old compiler only after replacement behavior is demonstrated.

## Scope and decisions still open

Preserve C# authoring, the existing Koh.GameBoy API, emulator, debugger and SDK
workflow. The request permits API changes in several sections but explicitly
excludes rewriting the game-facing API in section 12. Use the narrower scope:
preserve that API unless a specific required correction is documented. Do not
silently retire features to reduce implementation work.

Choose a direct CIL-to-IR-to-SM83 pipeline. Do not copy the generic frontend/backend
registry or create a second linker. Metadata library choice, IR operations,
supported language contract, calling convention, memory allocation, banking and
interrupt rules are pending audit, not inherited guarantees.

The old subset guide claims capabilities including wide integers, software float,
classes, delegates, generics, iterators and LINQ. Those claims require empirical
checks; neither old documentation nor a passing legacy test is an oracle.

## Acceptance ledger

- [x] Branch from integrated linker migration, PR #54.
- [x] Account for every legacy compiler test declaration in source-level audits.
- [ ] Complete architecture and test audit with independent expectations.
- [ ] Implement native compiler and documented supported contract.
- [ ] Prove game-level ROM execution and debugging.
- [ ] Integrate native compiler into SDK, CI and release packaging.
- [ ] Finish Rust coverage migration and delete legacy compiler.
- [ ] Record platform/performance evidence and bounded container cleanup.

No compiler build, test, sample execution, performance result or cross-platform
validation is claimed by the initial documentation commits.

## Audit results

All 565 test methods are enumerated in the [disposition status](test-disposition.md).
The source audits cover CIL/runtime semantics, backend/ABI and integration consumers.
Their dispositions are proposals, not migrated tests. The existing compiler suite
passed 814/814 executions locally with .NET SDK 10.0.115 (see disposition status).
Final supported contract, native semantic scenario coverage and native-compiled
sample execution remain open.

## Native reader increment

`rust/koh-compile` now reads bounded PE/CLI metadata and raw CIL method bodies.
The [reader decision and limits](metadata-reader.md) explain library selection,
malformed-input checks and pending reference/signature/PDB work. It does not yet
emit ELF or replace the SDK compiler.

Local Linux verification: `cargo nextest run --workspace --all-features --locked`
reported **198 tests run: 198 passed, 0 skipped**, including 22 native reader cases.
Real Roslyn fixtures cover Debug/Release and PE32/PE32+, temporary paths with
spaces, method identities and bodies, invalid local signature references, and
truncation/header mutation robustness. The original read tests failed before the
reader existed; a review-added local-token regression failed before its fix.

`cargo test --doc --workspace --all-features --locked` exited 0 (no doctest cases
currently exist). `cargo fmt --all -- --check` exited 0 with the installed stable
formatter. Completed logs: `/tmp/koh-compile-nextest.log`,
`/tmp/koh-compile-doctests.log`, `/tmp/koh-compile-fmt-check.log`. Host Cargo/rustc:
1.99.0; fixtures use .NET SDK 10.0.115. These local results are not hosted
Windows/macOS or minimum-Rust-version evidence. CI now installs the SDK before
nextest and runs documentation tests separately.

## CIL decoder increment

The [encoding decoder](cil-decoder.md) preserves IL byte offsets and operand
bits, computes branch/switch targets and rejects malformed encodings and targets.
Its .NET-catalog oracle runs only in tests. A decoded opcode is not yet a
supported lowering operation; there is still no native ELF compiler or SDK switch.

Local workspace nextest run: **232 run, 232 passed, 0 skipped**, exit 0, including
56 compiler-reader/decoder cases. Full command was
`CARGO_TARGET_DIR=/tmp/koh-compiler-decoder-target cargo nextest run --workspace --all-features --locked`
with `KOH_DOTNET` selecting SDK 10.0.115. Separate target output kept this build
isolated from the legacy .NET baseline's native build. Completed log:
`/tmp/koh-compiler-decoder-workspace.log`.

Hosted reader head `0f313ff` passed all 14 checks, including native nextest on
Linux, Windows and macOS. [Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43630616545)
and [CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43630616487).
These results precede the decoder commit and must not be described as its hosted
validation.

## Signature increment

The [bounded signature layer](signatures.md) reads methods, fields, locals,
TypeSpecs and MethodSpecs into the metadata library's existing result types.
It corrects signed compressed lower-bound handling, bounds recursion/counts,
preserves modifiers/generic shapes, separates MethodDef from call-site conventions,
and accepts modern .NET ref fields without claiming their execution is implemented.
Real Roslyn fixture assertions cover generic identities, byref/pointer parameters,
rectangular-array fields and function pointers in Debug/Release PE32/PE32+ images.

The initial signature run failed 47 cases before implementation. The current
workspace run reports **295 tests run: 295 passed, 0 skipped**, exit 0, including
63 new signature cases (119 compiler infrastructure cases in total). Command:
`cargo nextest run --workspace --all-features --locked`, with `KOH_DOTNET` selecting
SDK 10.0.115. Completed log: `/tmp/koh-signature-nextest.log`.

All 14 hosted checks passed for preceding decoder head `2340e29`, including the
three native host jobs. [Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43631738227)
and [CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43631738198).
Signature-head hosted validation remains separate. No legacy compiler method is
retired and no native ROM generation is claimed by these parsing tests.
