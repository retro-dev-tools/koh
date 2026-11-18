# PR55 acceptance audit

Final validated implementation/package baseline:f5a10e8. Documentation-only
closure follows that baseline; compiler implementation is unchanged from3d15f2d.
This record maps the requested migration requirements to concrete sources and
completed verification. It does not treat a green workflow as a language oracle.

| Requirement | Implementation and independent evidence | Status |
| --- | --- | --- |
| One Rust compiler, C# authoring | `rust/koh-compile` owns PE/CLI reading, resolution, CIL lowering, typed IR, optimization and SM83 emission. `CompileKohRom` invokes native processes using argument lists. Roslyn still compiles game source. [Contract](supported-subset.md), [reader/library evaluation](metadata-reader.md). | Implemented and exercised. |
| Canonical ELF, existing linker | Compiler depends on `koh-objects`, not assembler implementation. Native CLI objects are read back and linked by `koh-link`; SDK/package runs produce ROM/KDBG. No second serializer/linker or KOBJ compiler path. [Driver](driver.md), [package](packaged-tools.md). | Verified. |
| Architectural redesign and subset | Direct frontend/backend; dynamic guarded frames replace static activation assumptions. Metadata-driven intrinsics/runtime keys, explicit reachability and closed instantiations. [ABI](sm83-abi.md), [runtime decisions/alternatives](runtime-boundaries.md), [contract](supported-subset.md). | Documented; legacy conventions are not requirements. |
| Metadata, references and diagnostics | Actual Roslyn assemblies, signature/identity/type-forwarding/transitive resolution tests; malformed/truncated PE/metadata, missing references, unsupported generic/IL shapes and input-preservation diagnostics. `tests/assembly.rs`, `tests/references.rs`, `tests/driver.rs` and its feature modules. [Reference contract](reference-binding.md), [retained IL](retained-il.md). | Covered by full native suites. |
| Typed IR and optimizer | Explicit typed values/blocks, checked CFG/dominance/use-def/type/terminator invariants; stack joins, alias barriers and original IL provenance. `tests/ir.rs`, `tests/lowering.rs`, optimizer and machine original/optimized execution cases. [IR](ir.md), [optimization](optimization-semantics.md). | Covered; no byte-identical Debug/Release requirement. |
| SM83, runtime and capacities | Wide/signed arithmetic, software floats, structs/references/arrays/objects, guarded frames/recursion, MBC5 calls, interrupt preservation and checked RAM/ROM limits. `tests/sm83.rs`, `tests/driver/`, `tests/literal_limits.rs`. [ABI](sm83-abi.md), [RAM](ram-layout.md), [IRQ](irq-calls.md). | Supported contract executed; exclusions diagnose. |
| PDB and source debugging | Matching explicit/adjacent/embedded Portable PDBs, missing-symbol builds, hidden ranges, cross-module provenance, ELF/KDBG, actual breakpoints/disassembly/stack inspection. [Source debugging](source-debugging.md), [native unwind](native-unwind.md), SDK source-debug cases. | Executed in native integration tests using existing debugger. |
| SDK/product workflow | Ordinary game builds, references, DMG/CGB flags, native discovery/errors, incremental outputs, debug flags and paths with spaces. `tests/sdk.rs`; graphics and sample cases in driver/cubes. Useful tile-sheet task retained. [SDK](sdk.md). | Seven sample ROM builds and native integration acceptance passed. |
| Legacy removal | Compiler/test trees physically absent; no project/solution references or direct Cecil package remain. Standalone Superopt uses shared opcode descriptions and retains tools-owned tests. [Removal](legacy-removal-validation.md), [Superopt](superopt-boundary.md). | Removed; no production fallback. |
| Complete test migration |565/565 source-method dispositions:564 replacements, one obsolete private-parser deletion, zero compiler DEFER. Original arguments/assertion-site counts remain provenance, not inflated execution counts. [Matrix](test-disposition.md), frontend/backend/integration audits. | Closed.2062 compiler cases discoverable in nextest. |
| Rust test infrastructure | Workspace rstest dependency, parameterized cases/fixtures, committed nextest config with zero retries, bounded Roslyn concurrency, unfiltered full CI and separate rustdoc. Retained .NET tests belong to live noncompiler components. | Configured and exercised. |
| Actual game behavior | Real boot, exact hardware/memory/graphics, numeric progression, input, scenes, callbacks, JRPG dialogue/battle, cube animation and timing checks. [Showcase](showcase-timeline.md), [JRPG](jrpg-acceptance.md), [original2048](original-2048.md), [cube sweep](cube-phase-sweep.md). | Representative acceptance passed; no exhaustive gameplay/HIL claim. |
| Cross-platform quality | Full CI44316310945 passes all14 jobs. Native Linux2239/2239, macOS2239/2239, Windows2237/2237; zero skipped. Separate doctests, native executable smoke and LSP publish passed. Rust1.88 all-target/all-feature compile check passes locally. | Implementation baseline verified on three hosts. |
| Performance |90 fresh processes across five identical-input workloads; measured core/wall time, peak RSS, deployed binary sizes and generated payload. [Refresh](performance-refresh.md) preserves host/protocol/ranges and unfavorable size results. | Measured; no whole-SDK, hot-JIT or target-speed claim. |
| Docker cleanup | Obsolete four-file benchmark/container pipeline deleted; meaningful host-native benchmark/reference workflows retained. Static inventory has no tracked container definition. [Audit](container-cleanup.md). | Complete within migration scope. |
| One reviewable PR | Existing [draft55](https://github.com/retro-dev-tools/koh/pull/55), master base, logical commits and maintained concise checklist. No merge or second PR. | Published; remains draft. |

## Exact platform difference

Windows excludes two assembler cases guarded by `cfg(unix)`:
`analysis::tests::physical_include_keys_preserve_native_path_separators` and
`include_provenance_preserves_the_logical_symlink_path`. They require Unix
filename/symlink semantics. No compiler test is excluded by this difference.
The full run is unfiltered; the separate one-case packaged-executable smoke
intentionally selects one SDK case and does not replace it.

## Final validation

All14 jobs of [run44330863800](https://github.com/retro-dev-tools/koh/actions/runs/44330863800)
completed successfully atf5a10e8. Completed native logs report Linux2239/2239
in1580.550s, macOS2239/2239 in1741.666s and Windows2237/2237 in2091.354s,
zero skipped. Separate doctests, native compiler artifact smoke, runtime notice
staging and language-server publishing passed on each host. The retained Windows
.NET job reports567 passed/zero failed/two opt-in SDL skips; the separate SDL
job executes its native-window cases with five passed/zero skipped per backend.
NativeAOT publishing and300-frame PNG smoke checks passed on all three hosts.

Downloaded all three native artifacts and matched their ZIP SHA256 values to
GitHub metadata. Each contains the native compiler, Cargo notices and14 active
Rust runtime notice/provenance files; provenance identifies Rust1.100.0. Linux
artifact14441083408, macOS14442292052, Windows14441833719. The separate local
updated archive check preserves hashes/modes for all66 files, including runtime
notices, and executes the extracted compiler/linker version commands.

## Evidence limits and release follow-ups

- The full three-host run validatesf5a10e8, including the notice helper. Final
  documentation-only closure does not create new production verification claims.
- Historical-runtime Graphics/Original2048 inputs used for compiler timing have
  different numeric HUD captures under the two compilers. They are not gameplay
  parity acceptance. Production samples use the current library and their own
  independent observable acceptance. Root cause of the historical-runtime
  discrepancy is not established by these measurements.
- The native dependency bundle has60 verified registry archives and61 selected
  license-text references. First-party MIT metadata has no accompanying root
  LICENSE; attribution awaits maintainer input. The inventory does not establish
  toolchain-wide license compliance or the existing managed payload. The release
  now stages the active Rust sysroot's upstream library notices separately;
  local1.88/1.99 byte-identity checks and hosted three-platform staging pass.
  First-party attribution and managed-payload notice review remain release
  follow-ups, not claims of license compliance. No release is published by this PR.
- No physical Game Boy validation or exhaustive game-route test is claimed.
  Documented exclusions include boxing, variance adaptation, exceptional unwind,
  general reflection/BCL, richer IRQ execution and bank-bearing immutable data.

The compiler migration acceptance is complete. This is a draft PR for review,
not authorization to publish a toolchain release or merge it. The existing
first-party/managed notice gaps remain release blockers under repository policy;
recording them does not invent a copyright owner or expand this migration into
a licensing redesign of the retained emulator/LSP/.NET distribution.
