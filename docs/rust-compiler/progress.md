# Native CIL compiler migration (PR #55)

Status: source audit and empirical baseline recorded; native raw metadata/body
reader, CIL encoding decoder, bounded signatures, reference binding and initial
typed integer lowering and initial ELF/SM83 emission implemented. No
native SDK integration has landed yet; a standalone native executable now exists.
This document tracks the single replacement PR; it does not establish acceptance.

Latest increment: [unmanaged values](aggregates.md). Bounded canonical layouts,
inline/reference IR types, field access, default initialization, typed copies and
value-type instance signatures now execute actual Sprite library methods and
larger by-value C# fixtures. Compact copies/zeroing above8bytes resolve ROM0
capacity failures without reducing the fixture. Whole-memory/bank guards cover
nested/static/explicit-overlap fields, all integer widths and overlapping copies.
Linux workspace nextest passed675/675,0skips,exit0,832.362s
(`/tmp/koh-aggregate-workspace.log`), including499 compiler cases. The final explicit
cpobj overlap variant passed4/4,exit0,25.109s
(`/tmp/koh-aggregate-cpobj.log`), retaining original Roslyn ldobj/stobj execution.
Post-commit Sprite/aggregate checks passed8/8,exit0,50.459s
(`/tmp/koh-aggregate-postcommit.log`). Combined native coverage is478 ROM
scenarios (408 normal,70 traps), four full frame comparisons. Separate workspace doctests (no cases) and formatting pass.
Ten of565 legacy methods have complete native behavioral replacements;555 still
need completion auditing, none physically retired. Constructors/newobj,
aggregate arrays, reference-bearing/generic values remain outstanding.

Hosted aggregate checkpoint `7863872` now has completed successful
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43693156643) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43693156641) workflows.
Completed native logs report Linux675/675 (417.995s), macOS675/675 (450.576s),
Windows673/673 (400.440s), all zero skips. Windows omits only Unix-specific
assembler `physical_include_keys_preserve_native_path_separators` and
`include_provenance_preserves_the_logical_symlink_path`; no compiler case differs.
Native build/check/doc/SDK-boundary steps also succeeded on all three hosts.
The SDK boundary here still uses the production C# compiler for C# authoring;
these jobs do not prove a native compiler SDK replacement. This is predecessor
proof, not validation of the subsequent uncommitted constructor changes.

Previous increment: [LCD-on transfers](lcd-on-transfers.md). Independent guards
exposed unsafe legacy timing assumptions on the native backend. CPU targets and
four-byte values, or GDMA registers/control, now prepare before waiting. LCD-on
GDMA bursts are limited to1024 bytes; LCD-off limits and public signatures remain
unchanged. Workspace617/617 passed before the final four-byte refinement;
formatted final focused cases passed4/4,exit0,23.357s. Still-live .NET TileSet
integration passed7/7 after a zero-warning build. Doc/fmt checks exited0.
Combined coverage now454 ROM scenarios (385 normal,69 traps), four frame checks,
and eight complete legacy behavioral replacements;557 methods need completion
auditing and none is physically retired. IRQ/double-speed timing and full native
SDK/game/debug acceptance remain open.

Previous increment: [static/array alignment](alignment.md) and actual
`Video.Init` execution. Focused Debug/Release × DMG/CGB emulator tests passed4/4
(`/tmp/koh-video-init-poison-fixed.log`, exit0,19.541s), including tile maps, OAM,
palettes, aligned backing storage and restored banking. Nine metadata boundary
cases passed (`/tmp/koh-alignment-validation.log`, exit0). Workspace nextest
passed617/617,0skips,exit0,451.447s (`/tmp/koh-alignment-workspace.log`). Expanded
alignment assertions subsequently passed4/4,exit0,16.764s
(`/tmp/koh-alignment-edges.log`):20 ROM executions covering empty4096 boundaries,
overflow before heap writes, local/branch aliases, scalar placement and factory
diagnostics. Final suite discovery includes441 compiler cases; combined executed
coverage is444 ROM scenarios (375 normal,69 traps), with four frame comparisons.
Workspace doctests (no cases) and rustfmt checks exited0. This is not a completed
migration or a native SDK integration claim.

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
- The baseline included a container-only pokecrystal benchmark harness alongside
  active `tools/benchmarks/Koh.Benchmarks`. Consumer audit found no CI/root target
  using the container scripts; both Koh phases were permanently unsupported by
  empty capability arrays. PR #55 removes that harness and report generator,
  preserving host-native benchmark discovery/execution and RGBDS verification.
  See [bounded cleanup](container-cleanup.md).

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

## Reference-binding increment

The [reference binder](reference-binding.md) resolves full assembly identities,
types, overloads, fields, generic instantiations and explicit forwarders from
supplied images. Debug/Release Roslyn projects exercise transitive binding;
SDK reference metadata exercises strong-name identity without assembly execution.
Review found malformed zero-row scopes and unchecked nested generic arities;
both real-DLL mutation regressions failed before their fixes.
Inherited generic substitution, contextual generic constraints, body reachability,
typed lowering and executable code generation remain incomplete.

Local Linux `cargo nextest run --workspace --all-features --locked`, with
`KOH_DOTNET` selecting SDK 10.0.115, completed with **328 run, 328 passed, 0 skipped**
and exit 0. Log: `/tmp/koh-reference-nextest.log`. Separate workspace doctests
exited 0 with no cases (`/tmp/koh-reference-doc.log`); stable rustfmt check exited
0 (`/tmp/koh-reference-fmt.log`). No legacy compiler-owned method is retired.
All 14 hosted checks passed for preceding signature head `81d9836`, including
Linux, Windows and macOS native jobs. Binding-head hosted results are recorded below.

## Typed IR and initial integer lowering

The [IR contract and increment plan](ir.md) use typed SSA values, block parameters,
explicit local storage and method/IL identities. The verifier checks graph shape,
reachability, definition uniqueness, dominance, use order, edge signatures,
operation types, return types and entry initialization for local loads.

The initial native lowerer translates primitive integer CIL arithmetic,
comparisons, conversions, local/argument accesses and branches into verified IR.
Small storage gets explicit truncation/extension. Reachable unsupported execution
shapes are diagnostics, not placeholder code. Calls, objects, generic execution,
exception semantics, optimization and machine emission remain pending.

Tests include independently authored IL joins/loops and malformed cases, plus real
Roslyn Debug/Release fixtures checked against eight literal result scenarios per
configuration in a bounded test-only IR interpreter. Review caught host-width
arithmetic in that interpreter; all four new regressions failed before its fix.
This is IR evidence, not SM83 execution or retirement of old compiler coverage.
All 14 hosted checks passed for preceding binding head `215ce9e`; current-head
hosted evidence must be recorded separately.

Completed Linux verification: `cargo nextest run --workspace --all-features --locked`
with `KOH_DOTNET` selecting SDK 10.0.115 reports **364 run, 364 passed, 0 skipped**,
exit 0 (`/tmp/koh-ir-final-nextest.log`). This includes 188 compiler cases, 36 of
which exercise the new IR/lowering increment. The earlier run of 360 cases
preceded the four width regressions and is not final-tree evidence.

## Initial SM83 integer backend

The [initial SM83 ABI and executed slice](sm83-abi.md) use dynamic hardware-stack
frames, caller-owned return/argument storage, deterministic spilled values and
guarded capacity. Every operation in the current integer IR has machine emission,
including software multiplication/division/remainder and parallel block transfers.
This is not the final language/runtime compiler: managed calls, interrupts, objects,
generic execution, banking, startup/SDK integration and PDB/KDBG remain incomplete.

The focused Rust suite passes ten cases. Seven execute 140 independently checked
ROM scenarios through serialized canonical ELF, the Rust linker and the unchanged
C# emulator, including 18 real Roslyn Debug/Release scenarios. Ten intentionally
trap; 130 must return expected memory results with balanced SP. Guard sentinels
must remain intact. Three other cases cover ELF method/IL markers, maximum IR
offset robustness and oversized machine-code diagnostics. Log:
`/tmp/koh-sm83-reviewed.log` (exit 0, default test threading).

Review found diagnostic-offset overflow and shared test paths/build directories;
both are fixed. The offset test failed before its fix. Streaming emission now
stops at ROM0 capacity. No legacy compiler method is retired by this increment.
All 14 hosted checks passed for preceding IR head `579b910`; backend-head hosted
validation remains separate.

Completed Linux workspace command `cargo nextest run --workspace --all-features --locked`,
with SDK 10.0.115 selected through `KOH_DOTNET`: **374 run, 374 passed, 0 skipped**,
exit 0 (`/tmp/koh-sm83-nextest.log`). The suite includes 198 compiler cases; the
ten new backend cases are all discoverable by nextest. These results do not prove
SDK replacement or complete legacy test migration.

## Managed calls and body reachability

The [call increment](calls.md) adds typed call sites and whole-program signature
verification, iterative transitive body discovery, guarded caller storage, direct
and mutual recursion, and linker-visible stack UNION reservations. Only reached
methods are lowered; missing references and unsupported reachable code are errors.
Type/module initializer omission was reproduced with real C# and corrected to
explicit diagnostics pending static-storage/initialization implementation. Initializer
execution remains required, not retired. The caller-guard mutation caused protected
memory corruption and failed its test; the guard was restored for all final runs.

Completed local command `cargo nextest run --workspace --all-features --locked`
with `KOH_DOTNET` selecting SDK 10.0.115 reports **383 run, 383 passed, 0 skipped**,
exit 0 (`/tmp/koh-calls-nextest.log`). There are 207 compiler cases and 154 ROM
scenarios: 141 expected returns, 13 traps. Focused default-threaded tests also
passed (25 IR, 16 lowering, 14 backend cases; `/tmp/koh-calls-focused.log`).
All 14 hosted checks passed for preceding backend head `6e01f88`; call-head hosted
validation must be checked separately. No native CLI, SDK switch, game graphics,
PDB/KDBG or final legacy-test retirement is claimed.

## Native executable and cartridge startup

The [native driver](driver.md) now provides `koh-compile`, explicit references,
entry selection and canonical ELF output with startup and trap symbols. It uses
the existing object writer and linker. Nine driver cases add 18 emulator ROM
scenarios, including Debug/Release executable/library inputs, nested entry names,
transitive calls and stack exhaustion. Output safety and deterministic reference
ordering have direct assertions. SDK replacement remains pending.

Separate native-process DMG and CGB-only demonstrations both returned
`00 0000002A D000 OK`, exit 0, in the existing C# emulator. See driver.md for
artifact sizes and limits. This is initial CLI acceptance, not game acceptance.

The preceding call head `a1c7685` passed Linux locally but its hosted macOS native
job failed during Roslyn fixture compilation with nine CS0103 errors, before
native compilation. The fixture directly used Runtime while declaring only Bridge.
Locally disabling transitive project references reproduced those errors. Adding
the direct Runtime project reference produced a clean build under that same flag;
call tests now set the flag explicitly. New-head hosted macOS validation remains
required. Failed hosted job:
https://github.com/retro-dev-tools/koh/actions/runs/43644986002/job/132600633936

Hardware attributes currently fail closed rather than compiling desktop intrinsic
bodies or omitting interrupt roots. Actual intrinsic/interrupt lowering remains
required. No legacy compiler method is retired by this increment.

Completed Linux workspace run: **392 tests run, 392 passed, 0 skipped** in
283.774 seconds (`/tmp/koh-driver-nextest.log`). This includes 216 compiler cases
and 172 ROM scenarios (158 normal returns, 14 traps). The original terminal exit
record was lost during context transfer; the completed nextest summary was read
directly after confirming the process had terminated. Separate workspace doctests
exited 0 (no cases). No hosted result is claimed for this unpushed increment.

## Indirect memory and CFG simplification

The [memory increment](memory.md) adds explicit-width indirect integer loads and
stores, native/unmanaged pointer signatures, single-predecessor CFG chain merging
and conservative block-local spill reuse. Contiguous byte emission removes repeated
address setup. All transformations preserve memory-operation order; alias-based
optimization is not assumed. A combined Debug fixture exposed ROM0 overflow and
was retained while the underlying block/copy costs were corrected.

Focused tests passed **77/77**, exit 0, including the unchanged Debug/Release
memory fixture, existing arithmetic/call/recursion/cyclic-transfer cases, width
verification and a stack scratch-boundary check. Two additional block-order loop
cases are included in the subsequent workspace run. Original expected integer
results are also checked after simplification; repeated simplification is stable.
Logs: `/tmp/koh-memory-optimized-focused.log` and `/tmp/koh-memory-nextest.log`.
Whole legacy methods are not yet retired. Managed byrefs, aggregate/object/array
memory, globals/initializers, real hardware intrinsic lowering and IRQ remain open.

Preceding CLI head `b66b36e` completed all 14 hosted jobs successfully, including
native Linux/Windows/macOS. The completed macOS job log reports
`392 tests run: 392 passed, 0 skipped`; this supersedes the preceding call-head
fixture failure. [Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43651123570)
and [CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43651123593).
These hosted results do not cover the newer memory increment.

Completed Linux workspace command `cargo nextest run --workspace --all-features --locked`
with SDK 10.0.115 selected by `KOH_DOTNET`: **407 run, 407 passed, 0 skipped**,
exit 0, 291.998 seconds. There are 231 compiler cases and 176 ROM scenarios
(161 normal returns, 15 traps). Completed log: `/tmp/koh-memory-nextest.log`.
Separate workspace doctests exited 0, no cases (`/tmp/koh-memory-doc.log`).

## Hardware register/region intrinsics

The [hardware contract](hardware.md) now lowers canonical Koh.GameBoy register
and region attributes at call sites, including property accessors and direct
method aliases. Addresses come from validated metadata blobs. Byte MMIO accesses
use existing typed memory IR; desktop bodies and their initializers remain outside
reachability. Unknown kinds, malformed payloads, invalid signatures/addresses and
duplicate declarations diagnose. Control/heap/DMA/interrupt support remains open.

Debug/Release real Hardware/Gb fixtures pass via the native executable, canonical
ELF, Rust linker and existing C# emulator. They check MMIO, LCD-off VRAM access,
pointer offsets, custom aliases and an excluded throwing desktop body. Four scalar
cases cover mixed native/int32 addition/subtraction. The initial focused run
exposed a missing subtraction arm in the test-only reference interpreter; that
arm now uses independent wrapping arithmetic, preserving the expected results.

Preceding memory head `5409d45` passed all 14 hosted jobs. Its completed macOS
log reports `407 tests run: 407 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43652752199),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43652752215).
These results do not cover the newer intrinsic increment. Full migration,
sample/debug/SDK acceptance and retirement of legacy compiler tests remain open.

Completed Linux workspace nextest: **420 run, 420 passed, 0 skipped**, exit 0,
321.348 seconds (`/tmp/koh-intrinsic-nextest.log`). There are 244 compiler cases
and 178 ROM scenarios (163 normal returns, 15 traps). Full command:
`cargo nextest run --workspace --all-features --locked`, with `KOH_DOTNET`
selecting SDK 10.0.115. Current-head hosted validation remains separate.

## Zero-initialized static storage and typed primitive references

The [static-storage increment](statics.md) adds symbolic field addresses,
cross-assembly static integer loads/stores, packed zero-fill WRAM0 storage and
startup clearing through a linker relocation. Primitive managed references retain
their scalar target type in IR and across calls/returns; mismatched reference
arguments and dereference widths diagnose. The first code-generation attempt
failed observable execution because HL was clobbered before saving an address's
high byte; preserving BC corrected that failure.

Completed Linux workspace command `cargo nextest run --workspace --all-features --locked`
reports **443 passed, 0 skipped**, exit 0, 224.302 seconds. There are 267 compiler
cases and 180 ROM scenarios (165 normal returns, 15 traps). Log:
`/tmp/koh-statics-nextest.log`. The two static Roslyn cases subsequently passed
again with stronger poison readback and all-field initial-zero assertions, exit 0
(`/tmp/koh-statics-poison-final.log`). This targeted strengthening followed the
workspace run; it did not change production compiler code. Separate workspace
doctests and stable formatting checks exited 0.

Initializers are still required: constructor-dependent fields, readonly writes,
generic storage, RVA/literal/instance shapes and aggregate/reference fields remain
diagnostic. This commit does not silently replace constructors with zeroed storage.
No whole legacy test method is retired. The initial 256-byte static capacity is
documented alongside pending RAM/heap/banking work, not asserted as final product
capacity.

Preceding intrinsic head `0fe6192` passed all 14 hosted jobs; the completed macOS
log reports `420 tests run: 420 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43654007601),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43654007574).
New static-storage hosted validation must be checked separately.

## Executed type and module constructors

[Initializer lowering](initializers.md) now includes ordinary supported constructor
bodies through generated, verified once guards. Module and strict-type method checks
preserve order; `beforefieldinit` types defer to field access and leave unused
initializers unreachable. Recursive/cyclic access observes initialized-so-far/default
state. Readonly writes require the declaring constructor. Unsupported exception
bodies still diagnose at their actual member/IL operation, replacing blanket rejection.

Completed local command `cargo nextest run --workspace --all-features --locked`
with SDK 10.0.115 selected through `KOH_DOTNET`: **450 passed, 0 skipped**, exit 0,
232.391 seconds (`/tmp/koh-init-nextest.log`). There are 274 compiler cases and
186 ROM scenarios (171 normal returns, 15 traps). Focused driver/backend tests
also passed 32/32 (`/tmp/koh-init-focused.log`). Real Debug/Release fixtures cover
module dependencies, readonly initialization, once counters, recursion, cycles,
BFI deferral, dead initializer omission and static-store operand ordering.
Removing generated guards in a test-only object mutation must produce failure 1
instead of 42. The prior initializer-dependent field entry now executes and returns 17.
No legacy compiler method is yet retired; broader runtime, IRQ, exception/generic
semantics, PDB/SDK/game acceptance and compiler deletion remain incomplete.

Preceding static-storage head `0e3cd05` passed all 14 hosted jobs; its completed
macOS log reports `443 tests run: 443 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43655742204),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43655742198).
New initializer-head hosted validation remains separate.

## Primitive local/argument addresses and reference checks

[Local reference lowering](local-references.md) adds short/wide `ldloca`/`ldarga`,
stable addresses into dynamic invocation storage and normalization of narrow local
loads after aliased writes. A bounded origin graph rejects managed references to
the current frame returned through aliases, block arguments or reference-returning
calls. Its conservative reassignment/scoped-return limitations are documented.
Managed null dereferences abort with fault 4; native bus address zero remains legal.

Local workspace command `cargo nextest run --workspace --all-features --locked`,
SDK 10.0.115 through `KOH_DOTNET`: **472 passed, 0 skipped**, exit 0, 232.609s
(`/tmp/koh-local-ref-nextest.log`). There are 296 compiler cases and 196 ROM
scenarios (179 normal returns, 17 traps). Focused predecessor checks passed 117/117.
Afterward, stronger positive-to-negative byte/short transitions passed the two
Roslyn cases again, exit 0 (`/tmp/koh-local-ref-signed-final.log`); this changed only
fixture assertions/setup, not compiler code. Separate doctests/fmt exited 0.

Eight-level recursion verifies ancestor addresses and independent per-frame guards;
additional cases check addressed by-value arguments, long mutation, borrowed returns,
null read/write behavior, address encodings, typed storage and frame escapes.
This advances primitive ref/ABI requirements, not aggregate/float references or
whole legacy-method retirement. Arrays, objects, broader runtime, IRQ/banking,
PDB/SDK/sample acceptance, test migration/deletion and performance work remain open.

Previous constructor head `d73963a` passed all 14 hosted jobs; its completed macOS
log reports `450 tests run: 450 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43657584065),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43657584034).
New local-reference head hosted validation must be checked separately.

## Primitive arrays and checked WRAM heap

[Array lowering and ABI](arrays.md) add mutable primitive SZARRAYs, null joins,
runtime length, checked element references and a linker-reserved bank-1 heap.
Allocation checks full-width inputs and remaining capacity before writes; objects
are initialized explicitly. Managed reference accesses preserve the caller's SVBK.
The allocator's internal CALL is included in frame guards. Object/reference,
aggregate, float and multidimensional arrays and RVA initialization remain pending.

The final focused array/IR/lowering selection passed **29/29**, exit 0
(`/tmp/koh-arrays-focused-final.log`). Four real Debug/Release DMG/CGB driver cases
execute 40 ROM scenarios and 16 rejection probes; IR/lowering cases validate
element identities, null joins and malformed operations. Review caught and fixed
native/int16 opcode confusion. Additional ref-return and loop-join fixtures exposed
ROM0 costs; shared allocation helpers, direct word address arithmetic, contiguous
comparison/extension and proven-boolean branch tests resolve them without reducing
assertions.

The corrected, formatted tree passed the full Linux workspace command
`cargo nextest run --workspace --all-features --locked`: **501 passed, 0 skipped**,
exit 0, 251.829s (`/tmp/koh-arrays-verified.log`). This includes 325 compiler cases
and 236 ROM scenarios overall (187 normal returns, 49 intentional traps). An
earlier complete run passed 500/501: the rejected return-type diagnostic had lost
its operation context. The implementation restores that context; its assertion
was retained. Separate workspace doctests (no cases) and stable fmt checks exited
0. Source review checked allocator SP offsets/capacity and bank preservation,
without claiming runtime evidence beyond the executed cases.

Prior local-reference head `8267af0` now has completed successful hosted
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43660015702) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43660015743) workflows.
That is predecessor evidence; the array head requires separate hosted validation.
No legacy test method is fully retired and the production SDK still uses C#.

## Mutable RVA array literals

[RVA initializers](rva-initializers.md) add bounded field-data reading, symbolic
metadata-only field handles, exact framework-helper identity/signature checks and
runtime payload copying from canonical read-only ELF sections. Arrays remain
distinct mutable allocations, including static readonly references. Partial/empty
copies follow the helper contract; invalid size faults7 before payload writes.
Compile-time handles cannot cross machine calls or acquire fabricated constants.
Opaque RVA storage requires canonical ValueType ancestry and explicit layout.

Focused reader/IR/driver checks passed **34/34**, exit0, 81.735s
(`/tmp/koh-rva-review-fixes.log`). Four D/R x DMG/CGB cases execute24 ROM scenarios
and12 malformed helper probes; reader cases include three PE variants and eleven
malformed metadata mutations. Review found and fixed the standalone IR call-ABI
gap; negative tests preserve its rejection. The earlier526-case workspace run was
intentionally interrupted after498 passes to complete review fixes; it is not a
successful full-suite result. The corrected535-case rerun is recorded separately.

The corrected formatted tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**535 passed, 0 skipped**, exit0, 333.191s (`/tmp/koh-rva-final-nextest.log`). This
includes359 compiler cases and260 ROM scenarios (203 normal returns,57 traps).
Rust formatting and separate workspace doctests (currently no cases) exit0.

Predecessor array head `4a32ba8` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43664185371) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43664185401) workflows.
Native Linux, Windows and macOS jobs include successful Test steps; completed
macOS log confirms501/501, no skips. RVA-head hosted evidence remains separate.
Real sample graphics, broader runtime/aggregate/enum, IRQ/banking, PDB/debug/SDK,
whole-test migration/deletion and performance/container gates remain open.

## Enum and stack-buffer increment

[Enum storage, fixed stack buffers and physical spill widths](stack-enums.md)
support the actual 2048 Board component without changing its source. Four Rust
driver cases cover Debug/Release and DMG/CGB, with independent board merge and
memory expectations. Complete graphical-game and SDK acceptance remain open.
The ABI retains full evaluation widths across calls; upper-bound analysis reduces
only provably zero spill bytes. Partial-alias regression tests caught an incorrect
max-versus-OR combination before this increment was committed.

Focused arithmetic2/2, partial-alias2/2 and stack-IR9/9 runs passed with exit0.
Logs: `/tmp/koh-width-boundaries.log`, `/tmp/koh-alias-regression.log` and
`/tmp/koh-stack-ir-final.log`. The first stack-IR run passed8/9: its cycle fixture
incorrectly branched to the prohibited entry block. Moving that cycle to a valid
nonentry block tests the intended allocation diagnostic without changing it.
Linux `cargo nextest run --workspace --all-features --locked --no-fail-fast`
completed **552/552 passed, 0 skipped**, exit0,365.322s
(`/tmp/koh-board-workspace-nextest.log`). This includes376 compiler cases and352
ROM scenarios overall (291 normal returns,61 intentional traps). C# formatting
completed successfully; the focused post-format run passed8/8, exit0,30.588s
(`/tmp/koh-board-formatted.log`), including all four real Board configurations.
Read-only backend review found no confirmed defect; a tentative enum-array
identity concern was withdrawn after checking CLI reduced-type compatibility.

Predecessor RVA head `293585d` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43666575641) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43666575657) workflows,
queried live by commit SHA. These are predecessor results, not current-tree proof.

## CIL table-switch increment

[Typed switches](ir.md#table-switches) preserve the remaining stack on all default
and case edges, verify int32 selectors and emit full-width SM83 comparisons.
Empty/duplicate tables and backward targets execute. Actual Debug/Release Roslyn
fixtures contain `switch` and produce an independently computed wrapping checksum.
This replaces representation assumptions with observable control-flow semantics;
it does not establish broader runtime or SDK/game acceptance.

The raw-CIL regression first failed with missing opcode0x45 lowering
(`/tmp/koh-switch-red.log`). Strengthened focused tests passed18/18, exit0,9.741s
(`/tmp/koh-switch-final-focused.log`), including13 emulator ROM scenarios. An
earlier17-case run passed16/17 because its expected diagnostic used lowercase
`undefined` while the existing message begins `Undefined`; the fixture was fixed.
Read-only review found cumulative stack-edge cloning could exceed the budget
before verification. The preallocation guard and retained-stack regression now
pass; review confirmed the fix. A redundant mutable test binding was then removed.
The formatted tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**570/570 passed,0skips**, exit0,374.746s (`/tmp/koh-switch-workspace.log`). This
includes394 compiler cases and365 ROM scenarios (304 normal returns,61 traps).
The disposition matrix records complete native behavioral replacements for four
legacy switch methods (three PORT,one REPLACE); their definitions remain until
the old compiler test project is removed. The remaining561 methods still require
completion auditing. The SDK remains on C# and the full migration is incomplete.
Separate workspace documentation tests (currently no cases) and stable rustfmt
checks exited0: `/tmp/koh-switch-doc.log`, `/tmp/koh-switch-fmt.log`.

Predecessor Board head `448f35b` now has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43672970728) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43672970693) workflows.
Native Linux/Windows/macOS Test steps succeeded; completed macOS job132686867798
log confirms552/552 passed,0skips,298.190s. These are predecessor results, not
verification of the uncommitted switch unit.

## Pinned pointer increment

[Pinned byref/array locals](pinned-pointers.md) keep heap bank1 mapped for native
copy routines and restore the caller's SVBK when the final pin ends or the method
returns. Per-frame flags preserve nested/recursive invocations and tolerate
cleanup order. Unbanked pins leave SVBK unchanged. Known explicit bank writes
reachable from a pinned method diagnose; raw bus manipulation remains unsafe.
Pin stores participate in verification, optimization and frame-reference escape.
Native zero cleanup is guarded against nonzero reference fabrication (fault8).

The real D/R × DMG/CGB fixture initially failed4/4 with unsupported pinned-local
types (`/tmp/koh-pinned-red.log`). After implementation, strengthened checks passed
14/14, then15/15 with the review-found invalid-initialization-index regression:
`/tmp/koh-pinned-review-fixes.log`, exit0,19.700s. This selector includes one old
reservation test;14 tests are new. Four driver cases execute28 ROM scenarios and
eight compile rejection probes; the raw cleanup test executes five ROM scenarios.
Nine IR cases cover valid and malformed pin lifetimes/storage. Actual referenced
`Koh.GameBoy.Mem.Copy` writes known LCD-off VRAM bytes and mutates array aliases.
These are not full graphics, DMA, TileSet or SDK acceptance.

Read-only review found local state indexing could precede bounds validation on a
malformed pin initialization. The implementation now validates the index first;
the regression and review confirmation preserve the diagnostic boundary. The
formatted pin tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**584/584 passed,0skips**, exit0,394.810s (`/tmp/koh-pinned-workspace.log`). This
includes408 compiler cases and398 ROM scenarios (333 normal returns,65 traps).
Separate workspace doctests (currently no cases) and stable rustfmt checks exited0
(`/tmp/koh-pinned-doc.log`, `/tmp/koh-pinned-fmt.log`).

Predecessor switch head `2c44000` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43674121192) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43674121261) workflows.
Native Linux/Windows/macOS Test steps succeeded; the completed macOS
job132690390503 log confirms570/570,0skips,190.157s. These do not verify the new
pin implementation.

## Native graphics execution

[Actual tile loading and rendered frames](graphics-execution.md) add four Rust
D/R × DMG/CGB cases,12 ROM executions and four full-frame pixel comparisons.
The real GameBoy library copies tile data through CPU and aligned CGB GDMA paths,
preserves caller banks and renders explicitly authored bit planes/palettes. Full
poisoned bank snapshots and programmed DMA state are checked in Rust. PNG uses
the existing emulator framebuffer writer via a test-friend declaration; emulator
algorithms and game APIs are unchanged. This is library/frame evidence, not full
Video.Init or complete sample gameplay acceptance.

First copies4/4 passed (`/tmp/koh-tiles-first.log`), then rendered4/4 passed
(`/tmp/koh-tiles-render.log`); formatted DMA checks4/4 passed,exit0,17.682s
(`/tmp/koh-tiles-dma.log`). Read-only review found no concrete issues. The
formatted tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**588/588 passed,0skips**, exit0,412.346s (`/tmp/koh-tiles-workspace.log`). This
includes412 compiler cases and410 ROM scenarios (345 normal returns,65 traps),
with four full-frame comparisons. Full SDK/game/debug acceptance remains open.

Standalone Release Roslyn → native CLI ELF → native CLI linker → existing C#
emulator runs also passed for DMG and CGB, including independent complete-pixel
checks and fresh PNG artifacts. The documented direct project builds were run
serially and reproduced the same pixels. Logs `/tmp/koh-tiles-demo.log` and
`/tmp/koh-tiles-root-demo.log`, both exit0; artifacts and `evidence.json` under
`/tmp/koh-native-tiles-demo-pXXzIw`. Both ROMs are32768bytes. DMG ELF80008bytes and
PNG822bytes, result `00 00000044 D000 OK`; CGB ELF82380bytes and PNG805bytes, result
`00 00000043 D000 OK`. Raw RGBA SHA256 values:

- DMG: `5d21f5266cb14dfce3e6fefd01ad48ae41edf280923492ead5dcefd82835824a`.
- CGB: `26e9b35f016a5b1b4e0bed412bb384df061ead6d4f82baf77e66bed6b0f6dfc7`.

PNGs were visually inspected after pixel validation. These are local component
artifacts, not compilation performance or full sample-game/debug evidence.
Separate workspace doctests (currently no cases) and stable rustfmt checks exited0
(`/tmp/koh-tiles-doc.log`, `/tmp/koh-tiles-fmt.log`).

Predecessor pin head `19e4949` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43676239605) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43676239601) workflows.
Native Linux/Windows/macOS Test steps succeeded; completed macOS job132696913473
log confirms584/584,0skips,271.132s. Current graphics-head hosted proof is separate.
Graphics commit `7c8b917` passed post-commit focused checks4/4,exit0,18.300s
(`/tmp/koh-tiles-postcommit.log`). The separate roadmap refresh changes only
documentation; it does not add code verification claims.
