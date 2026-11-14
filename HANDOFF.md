# PR #55 working handoff

Branch feat/rust-cil-compiler, isolated from main checkout modified screenshot.
Baseline integrated linker 9cd1472. Existing PR #55 stays draft; never merge.
C# compiler remains production. Full attachment migration goal stays active.

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
