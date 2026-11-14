# Native CIL compiler migration (PR #03)

Status: audit in progress. No native compiler is implemented or integrated yet.
This document tracks the single replacement PR; it does not establish acceptance.

## Inspected baseline

- Integrated master: `9cd147265eb0c660d94c7bf4eee6a97d9e1f4e1f`.
- PR #02 ([#54](https://github.com/retro-dev-tools/koh/pull/54)) merged on
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
| 1 | Record baseline, scope and progress | Integrated PR #02 and preserved unrelated work |
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

- [x] Branch from integrated PR #02.
- [ ] Complete architecture and test audit with independent expectations.
- [ ] Implement native compiler and documented supported contract.
- [ ] Prove game-level ROM execution and debugging.
- [ ] Integrate native compiler into SDK, CI and release packaging.
- [ ] Finish Rust coverage migration and delete legacy compiler.
- [ ] Record platform/performance evidence and bounded container cleanup.

No compiler build, test, sample execution, performance result or cross-platform
validation is claimed by the initial documentation commits.
