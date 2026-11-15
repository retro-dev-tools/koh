# PR #55 working handoff

Branch feat/rust-cil-compiler, isolated from main checkout modified screenshot.
Baseline integrated linker 9cd1472. Existing PR #55 stays draft; never merge.
C# compiler remains production. Full attachment migration goal stays active.

Latest init unit verified locally: type/module .cctor bodies run through generated
once guards, states 0/1/2 in zeroed BSS, strict method checks and BFI field deferral.
Readonly writes allowed only in the declaring constructor. Real D/R module dependencies, recursive
and cyclic init, readonly values, repeated calls, dead BFInit body exclusion and
store RHS-before-ctor ordering pass. Omission mutation returns 1 rather than 42.
Earlier constructor-dependent field entry now executes 17. Unsupported throwing
ctors still diagnose actual body opcode/member; no blanket ctor omission remains.
Read initializers.md, including generated FF function-ID namespace and remaining
single-thread/IRQ/exception/generic limits. Review read-only complete; no writers.
Workspace 450/450, no skips, exit 0, 232.391s; 274 compiler cases, 186 ROM scenarios
(171 normal, 15 traps), /tmp/koh-init-nextest.log. Focused 32/32 also passed.
Prior 0e3cd05 passed all 14 hosted jobs, macOS 443/443. New head CI must be checked.
Rough progress 35%; full original goal active. Next local/aggregate/array/runtime
support and remaining IRQ/banking/PDB/SDK/sample/test/deletion/performance gates.

Current uncommitted unit: root owns initialize.rs generated once guards/state
addresses, method/field/module constructor triggers and readonly ctor writes.
beforefieldinit types defer until field access; strict types guard static methods.
Initializers D/R fixture verifies module ordering, recursive/cyclic defaults,
readonly values, once counters, BFI deferral and dead initializer exclusion.
Test-only guard-omission mutation must return failure1 instead of42. Earlier
initializer-rejection tests now require real ctor values or body-specific unsupported
exception diagnostics. Focused suite live, no concurrent builds; review read-only.

Latest statics increment verified: symbolic field addresses/ELF relocations,
packed zero-fill WRAM0/startup clear, primitive ByRef(Scalar) types with call/memory
type checks. D/R real referenced fields, all integer widths/bool/char/native,
returned refs and ref byte/int mutation execute against poisoned RAM. Shared storage
31 bytes; dead fields omitted; >256 bytes errors. Constructor-dependent fields,
readonly writes, generic/RVA/literal/instance/aggregate/reference storage still
diagnose. Read statics.md. Constructor execution is next mandatory unit, not retired.
Workspace 443/443, no skips, exit 0, 224.302s; 267 compiler cases, 180 ROM scenarios
(165 normal,15 trap), /tmp/koh-statics-nextest.log. Stronger poison readback/all-field
zero assertions passed focused 2/2 afterward, with no production-code change.
Separate doctests/fmt pass. Source review complete; no delegated edits/builds.
Prior 0fe6192 passed all 14 hosted jobs, macOS log 420/420. New head CI pending.
Rough progress 32%; full original migration remains active with SDK/debug/runtime,
language/test migration/sample/deletion/performance/container gates still open.

Current uncommitted unit: root owns integer static storage, symbolic field-address
IR/ELF relocation, BSS allocation/zero startup, field reference resolution, scalar
managed ByRef IR types and ld[s]flda/ref ABI. Initializer execution remains the next
required increment; reached ctor-dependent fields still diagnose. Static tests use
nonzero WRAM poison, D/R transitive fields and ref byte/int mutation; bounds and
IR type/width checks added. Full nextest running serially in target/. Review only,
no delegated edits/builds. Verify full suite, doctests/fmt, commit and push same PR.

Latest intrinsic increment verified locally: canonical attribute payload/signature
validation, register/region call-site lowering with no desktop bodies/initializers,
mixed native/i32 add/sub. Real Hardware/Gb fixtures execute MMIO, LCD-off VRAM,
custom aliases and positive/negative byte-pointer offsets in Debug/Release.
Read hardware.md, including explicit target16 native-width extension and gaps.
Workspace nextest 420/420, no skips, exit 0, 321.348s; 244 compiler cases and
178 emulator scenarios (163 normal, 15 trap). Log /tmp/koh-intrinsic-nextest.log.
Focused negative variants/payload tests pass in workspace. Source review complete;
no delegated edits/builds. Prior memory head 5409d45 passed all 14 hosted jobs,
macOS completed log 407/407. New head hosted results must be checked after push.
Next static globals/initializers and remaining hardware control/IRQ/DMA/runtime;
then full language/debug/SDK/sample/test migration/deletion/performance gates.
Rough progress 30%; full original objective remains active and incomplete.

Current uncommitted unit: root owns contracts.rs metadata payload/signature checks,
call-site register/region intrinsic lowering and mixed native/i32 add/sub; Hardware
fixture plus negative contract and scalar lowering cases. Uses existing GameBoy
API and attribute addresses, no duplicate hardware table or desktop body execution.
Focused tests are running serially in target/. Review read-only; no edits/builds.
Verify full workspace nextest/doctests/fmt after review, then commit/push same PR.

Latest memory increment complete locally: indirect 8/16/32/64/native integer
memory, unmanaged/native signatures, unique-predecessor CFG chain simplification,
block-local temporary slots and contiguous copy/constant emission. Read memory.md.
Workspace nextest 407/407, no skips, exit 0, 291.998s; 231 compiler cases,
176 emulator scenarios (161 normal, 15 trap). Log /tmp/koh-memory-nextest.log.
Separate doctests exit 0 (no cases). Focused suite 77/77; new reversed-order loop
regressions also pass in workspace. Source review complete; nested-push defect
removed, scratch-boundary test passes. No delegated builds/writers remain.
Preceding b66b36e passed all 14 hosted jobs, macOS log 392/392, superseding earlier
fixture failure. New memory head hosted results must be checked separately.
Next: metadata-driven register/region/control intrinsics, pointer arithmetic,
static storage/initialization, followed by all outstanding full migration gates.
Rough progress 27%; no product acceptance or C# compiler deletion yet.

Current uncommitted unit: root owns integer indirect memory IR/verifier/lowering,
SM83 byte-order/sign extension, unmanaged pointer/native signatures, block-local
temporary slot reuse and shorter copy/constant emission. Tests add Memory fixture,
Debug/Release ROM scenarios and width verifier cases. Focused suite running serially
in target/. Review is read-only; no delegated edits or builds. Verify with workspace
nextest then doctests/fmt. Large combined memory fixture initially hit ROM0 limit;
retain that fixture and fix code size rather than split away the failure.

Latest increment supersedes CLI-pending statements below: native koh-compile,
startup, single canonical ELF composition and entry selection now exist.
Read docs/rust-compiler/driver.md and progress.md. Linux nextest completed
392/392 passed, no skips, 216 compiler cases, 172 emulator scenarios (158 normal,
14 trap). Log /tmp/koh-driver-nextest.log, 283.774s. Original terminal exit record
was lost in context transfer; completed summary was read after process termination.
Separate doctests exit 0. DMG and CGB-only native CLI demonstrations both returned
00 0000002A D000 OK, exit 0. No SDK/PDB/runtime completeness is claimed.
Call-head a1c7685 macOS failed Roslyn fixture compilation before native compilation;
direct Runtime reference fixes a locally reproduced DisableTransitiveProjectReferences
failure. New hosted verification required. Driver review findings (nested entry
identity and reference-only interrupt roots) were reproduced and fixed; 9 driver
cases pass. No delegated writers/builds remain. Next memory/globals/initializers
and real hardware intrinsic lowering, followed by remaining full goal requirements.

## Implemented

All 565 legacy compiler methods have source dispositions, not complete migration.
None is retired/replaced yet. Native readers/reference binding, typed SSA/verifier,
integer lowering/emission and static managed calls/reachability are implemented.
Read calls.md, sm83-abi.md and progress.md for exact scope/limits/evidence.
Calls have explicit signatures validated against actual IR definitions. A finite
work queue lowers each reached definition once; generic/instance/virtual shapes
are still incomplete. No whole BCL or unreachable unsupported bodies are included.
Dynamic SP frames preserve caller values and recursion. Caller capacity checks
precede buffer writes/CALL; callee checks frame before writes. Every ELF object
has a canonical WRAM0 UNION stack reservation, preventing linked global overlap.

Initializer omission was reproduced in real C#: both declaring-type .cctor and
<Module>..cctor must not be ignored. They now produce explicit diagnostics for
reached methods/assemblies until real static storage/initialization lowering lands.
This is pending required implementation, not retirement of those capabilities.
A controlled removal of caller guard produced CORRUPT sentinels despite a callee
guard. The production guard was restored before all final runs.

Root owns all files. Read-only reviews are complete, including module-init fix.
No delegated writers/builds remain. No native CLI/startup/SDK switch, graphics,
PDB/KDBG acceptance or legacy compiler deletion yet. Library entry is
compile::reachable(&Resolver, Definition); output IR is program-verified.

## Evidence

Completed workspace nextest: 383/383 passed, 0 skipped, exit 0; 207 compiler cases.
Log /tmp/koh-calls-nextest.log. Suite executes 154 ROM scenarios through ELF
serialization/readback and Rust linker in unchanged C# emulator: 141 expected
normal returns, 13 traps, guard sentinels below C100 preserved. New cases include
12 real C# scenarios across Debug/Release (transitive calls, mixed widths, void,
recursion/mutual recursion, deep-stack trap), plus caller guard/live value cases.
Exact Main reachability is four methods; dead unsupported code stays absent.
Focused default-threaded tests: 25 IR + 16 lowering + 14 backend passed, exit 0
(/tmp/koh-calls-focused.log). Separate workspace doctests exit 0, no cases
(/tmp/koh-calls-doc.log); stable fmt check exit 0 (/tmp/koh-calls-fmt-check.log).
Legacy baseline 814/814 remains historical. All 14 hosted checks passed for
preceding backend head 6e01f88. Check new call head separately after push.

## Next

Implement native entry/CLI/module emission, metadata hardware contracts, memory,
static globals and real initialization. Then remaining language/runtime, generic,
objects/arrays, optimization, banking/IRQ, PDB/debug, SDK, sample, test audit/
migration, legacy deletion, performance/platform and bounded Docker work in this
single PR. Do not redefine success as integer/call slice. Rough progress 20%;
100% means every original requirement verified. Keep old compiler until replacement
acceptance, then delete it entirely. GameBoy API is preserved by default.

## Verification

Current uncommitted unit: root owns cartridge.rs/compile.rs/main.rs native entry,
single-ELF module composition/startup, contracts.rs guards, driver tests/fixtures,
shared test Host support and C# test-only native-PC stop mode. No emulator core
changes. Review is read-only, no edits/builds. Driver currently passes eight cases
including native CLI, 16 extra ROM executions, output safety/determinism and
canonical Koh.GameBoy contract rejection. Full verification follows review.

Serial builds per output directory. KOH_DOTNET selects .NET SDK 10.0.115; read
completed logs/direct exit codes. Use stable rustfmt. Reuse koh-objects ELF and
Rust linker; execute C# emulator. Preserve sample assets/main screenshot. Commit
and push logical increments to this draft PR; do not open another or merge it.
