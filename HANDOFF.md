# PR #55 working handoff

## Current aggregate unit

Root owns all edits/builds; no delegated writers. Layout metadata committed/pushed as14b9a89; aggregate execution remains
uncommitted. Draft PR #55 targets master. Full migration remains active, about45%.

Actual Sprite acceptance now passes Debug/Release × DMG/CGB: real Video.Init,
out/byref handle, instance Set/Move/SetAttr/SetTile, complete shadow OAM heap
snapshot, unchanged VRAM/OAM and bank restoration. Broader Roslyn fixtures pass
by-value arguments/results, static/nested/explicit-overlap fields, padding zero,
all scalar field widths, addressed explicit instance arguments, overlapping
64-byte copies and raw bank2 pointer fields. No GameBoy API changes in this unit.

Layout reader uses cached canonical identities and bounded unmanaged layouts;
independent .NET Size5 probes caught erroneous tail rounding. Typed Struct and
StructRef, Storage descriptors, wide memory operations, field address/extract
and ZeroStruct preserve byte layouts and derived-reference escape checks.
Native field addresses retain raw bank provenance; managed refs map heap bank1.
Implicit value-type this is a typed reference. Constructors/newobj, aggregate
arrays, reference-bearing/generic/auto/InlineArray shapes remain unsupported.

Initial broad execution exceeded ROM0: fixed backend copies/zeroing above8bytes
with compact counted loops, retaining scalar instruction paths. Did not shrink
the fixture or bypass canonical linker capacity. Focused combined12/12 passed,
exit0,41.664s,/tmp/koh-aggregate-compact-first.log. Additional scalar/overlap fixture
4/4 passed,exit0,19.854s,/tmp/koh-aggregate-scalars-overlap.log. IR156/156 passed.
Full workspace675/675 passed,0skips,exit0,832.362s,
/tmp/koh-aggregate-workspace.log. Final cpobj focused run4/4 passed,exit0,25.109s,
/tmp/koh-aggregate-cpobj.log. No concurrent builds in its target directory.

Read-only aggregate review found no actionable defects; cpobj coverage gap now
has a same-length test IL variant retaining original Roslyn assignment. Final focused execution passed after full run. Latest scalar/overlap run actual exit0 confirmed.

Next: reread doc/fmt logs, commit aggregate execution increment,
push same draft PR, then update PR/vault evidence. Coverage499 compiler cases,
478 ROM scenarios (408 normal,70 traps), ten complete legacy replacements. Do not claim whole migration or complete struct support.
SDK still uses C# compiler; PDB/debug, full runtime/language coverage, samples,
test migration, packaging and legacy deletion remain outstanding.

## Current LCD-on graphics unit

Root owns fixtures/host/driver; no delegated edits or builds. Extend actual
TileSet.Load acceptance to LCD-on aligned/unaligned CPU/GDMA paths with the
existing emulator Mode3WriteGuard and complete poisoned-bank checks.
Old vblank timing was unsafe on the native backend; guards exposed and now cover
the corrected transfer ordering. Normal-speed/interrupts-disabled proof only.
Previous verified pushed checkpoint f19bf50; full migration remains incomplete.

Root cause verified: firstCPUwrite10044dots after LY144 and2680dots/byte;
alignedGDMA14160dots later atLY21dot92. Red4/4failed,exit100,
/tmp/koh-lcd-on-red.log; detailed /tmp/koh-lcd-on-timing-fixed.log and
/tmp/koh-lcd-on-gdma-timing.log. Ordinary C# runtime fix prepares CPU byte/target
before WaitVBlank and writes directly afterward; GDMA setup/control precedes wait
in new internal Cgb.CopyToVramInVBlank helper. Public API/nonwaiting helper unchanged.
1920-byte burst still failed atLY146dot104(duration3840,remaining3544); reduced
LCD-on chunk1024, retaining2048 LCD-off ceiling. Focused4/4passed,exit0,23.790s,
/tmp/koh-lcd-on-bounded.log, including1920-byte two-burst CGB fixture. Initial
onebyte/frame fallback superseded by prepared32-bit store retaining4bytes/frame.
Formatted4C#files +Rust. Workspace617/617passed,0skips,exit0,456.891s beforefinal
fourbyte refinement. Final focused4/4passed,exit0,23.357s,
/tmp/koh-lcd-on-final-focused.log; retainsoriginalbytes1..32 at tile7. .NETbuild
exit0,zero warnings/errors,11.56s; filteredCilTileSet tests7/7,0skips,exit0,7.431s,
/tmp/koh-lcd-on-dotnet-{build,tests}.log. Doc/fmt EXIT=0, no doctests.
No active builds. Need commit/push, posthook focused test, PR/vault refresh.
Combined454ROM scenarios(385normal,69traps). Eight legacy behavioral replacements,
557 still needcompletionauditing, none physically retired. Docs:lcd-on-transfers.md.
Read-only review may inspect runtime/host/driver; root owns edits and all builds.
Read-only arrays_review completed: no concrete correctness defects. Evidence is
normal-speed, interrupts-disabled execution; no double-speed/IRQ timing claim.
Throughput improvement implemented after the serial full run:
DripCpu total is always count*16; prepare a uint value and uint* target before
WaitVBlank, then one direct 32-bit store and copied+=4. SM83 StoreMemory32 emits
one address setup and four contiguous byte stores (about88 transfer dots), unlike
the expensive C# Mem.Copy loop. Focused fourbyte run4/4passed,exit0,23.464s,
/tmp/koh-lcd-on-word.log, including1920-byte two-burst CGB transfers and all guards.
Full workspace prior onebyte version617/617passed,0skips,exit0,456.891s,
/tmp/koh-lcd-on-workspace.log. Final code nowfourbytes/frame; old slow onebyte is
superseded. Read-only follow-up confirms uint count/alignment/non-overlap conditions;
no concrete defects. Public APIs unchanged; .NET oldcompiler graphics proofpassed.

## Latest verified alignment unit

Committed/pushed94f3764b16b7e8b3acf8d919be82a592ae825f30. DraftPR#55
verified master base and matching head; body records this increment. Never merge.

Native KohAligned metadata validates static field targets and power-of-two bounds
1..4096. Aligned primitive array payloads propagate from static stores through
local/SSA aliases to visible allocations; factory/external aliases diagnose.
Scalar static placement includes alignment padding. Native/int32 binary operands
now follow the CIL operand table using the documented 16-bit native target.
Actual Video.Init now executes in the TileSet fixture: aligned MapWriter/Sprites
backing arrays, tile maps, OAM and palette state checked on DMG/CGB, Debug/Release.
Focused test log /tmp/koh-video-init-poison-fixed.log: 4/4, EXIT=0,19.541s.
Metadata boundary tests /tmp/koh-alignment-validation.log:9/9,EXIT=0.
Full workspace run finished:617/617,0skips,EXIT=0,451.447s,
/tmp/koh-alignment-workspace.log. No builds remain live.
Current unit owned by root; no delegated edits/builds.
Added Edges.cs and expanded alignment driver assertions after full run started:
empty4096 header at4094, overflow fault5 with pristine heap, branch/local alias
identity and strongest alignment, factory diagnostic with method IL provenance.
Expanded assertions passed4/4,EXIT=0,16.764s (/tmp/koh-alignment-edges.log),
20ROM executions. Separate doc/fmt checks EXIT=0; no doctests currently exist.
Read-only arrays_review found no concrete defects in allocation/alias/metadata.
New docs/rust-compiler/alignment.md documents current contract and limits.
Combined coverage441 compiler cases,444 ROM scenarios(375normal,69traps),4frames.
Seven legacy methods now have completed native behavioral replacements;558 still
need completion auditing. None physically retired. Post-commit graphics/alignment
checks8/8,EXIT=0,37.085s (/tmp/koh-alignment-postcommit.log). PR/vault refreshed.
No build remains live. Hosted validation of this new head is pending.
Next broader aggregate/runtime/game and PDB/KDBG dependencies, native SDK/package
replacement, full test migration/deletion and measured cross-platform/performance
gates. Benchmark shell now consumes Rust assembler/linker: assess host-native
replacement rather than deleting it based on the obsolete baseline description.

## Objective and checkout

Complete the entire native Rust CIL-to-SM83 migration in this single draft PR:
https://github.com/retro-dev-tools/koh/pull/55 . Never merge it. Do not mark the
goal complete for an increment. User wants logical commits and an ASCII progress
bar where 100% means every original requirement is verified. Rough estimate 45%.

Worktree `/home/minorum/projekty/koh-rust-compiler`, branch `feat/rust-cil-compiler`.
Baseline integrated linker migration: `9cd1472`. The main checkout's modified
battle screenshot is unrelated; preserve hash
`fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`.

The C# compiler remains the production SDK compiler. No old test method is fully
retired yet. All 565 have source-level dispositions, not completed Rust migration.
Eight methods have complete native behavioral replacements; their source
definitions remain with the old project until deletion. Remaining557 need
completion auditing.
Read docs/rust-compiler/progress.md and source audits for the full acceptance ledger.
The original assignment is also in the conversation and attachment
`/home/minorum/.codex/attachments/f20b0d62-eb77-4947-9b7a-4dcb4328fc7f/pasted-text-1.txt`.

## Implemented

Bounded native PE/CLI/CIL/signature/reference reading; typed SSA/block arguments;
dominance/type/program verification; integer lowering and SM83 emission; direct
calls/recursion with guarded dynamic frames; native CLI/startup and canonical ELF;
indirect memory, static storage and primitive managed references; register/region
intrinsics; actual type/module constructors with once guards and BFI deferral.
CFG chain merging and conservative block-local temporary reuse reduce code/frame
costs. Reuse koh-objects/koh-link; no second linker or object serializer.

Earlier unit adds primitive local/argument addresses, metadata-aware narrow load
normalization, a bounded frame-return escape graph and managed null fault 4.
Read local-references.md, statics.md, initializers.md and sm83-abi.md. Reference
return analysis is conservative for reassignment/scoped-return contracts. Native
pointers retain raw bus semantics. Structs/objects, float/generic/runtime,
IRQ/banking and source-debug/SDK acceptance remain incomplete.

Primitive SZARRAYs now allocate explicitly zeroed packed count/payload objects in
linker-reserved WRAMX bank 1. Full-width length/index checks fault before invalid
writes; empty objects have distinct identities. Array/static/local/return/null
semantics and heap element refs are typed. Managed references preserve SVBK;
native bus pointers retain selected-bank semantics. Internal allocator CALL/AF
scratch is guarded. Read arrays.md. No GC/reset or aggregate/
reference/float/multidimensional array support yet; these remain required work.
RVA initialization now copies validated field bytes into distinct mutable arrays;
compile-time field handles have no machine representation and cannot cross calls.
Exact framework helper identity/signature checks replace pattern suppression.
Canonical read-only ELF sections carry literal bytes; runtime null/size checks
and bank preservation guard copies. Read rva-initializers.md for subset limits.

## Evidence

Latest formatted-tree Linux workspace nextest: 535/535 passed, no skips, exit 0,
333.191s (`/tmp/koh-rva-final-nextest.log`); 359 compiler cases,260 ROM scenarios
(203 normal,57 traps). RVA-focused34/34, including24 ROM cases,12 helper rejection
probes,3 PE variants,11 malformed metadata cases and16 IR invariants. Source
review caught machine-call metadata handle fabrication; negative tests now reject
it through both verifier/emitter. An earlier526-case run was deliberately
interrupted after498 passes for those review fixes, not a full success.
Array-focused 29/29 passes; four D/R x DMG/CGB cases check
40 ROM scenarios and 16 rejection probes, full poisoned-bank snapshots, all twelve
primitive element types, returned refs and loop/null joins. Review caught native/
int16 opcode confusion; six malformed-IL regressions preserve the distinction.
Earlier full run500/501 exposed a lost return-diagnostic context, fixed without
weakening its assertion. Separate doc/fmt checks exit0. Existing C# emulator
executes all ROMs. Eight-level ancestor recursion,
by-value argument addresses, narrow signed ref rewrites, long mutation and borrowed
aliases pass in real Debug/Release Roslyn fixtures. Additional CPU cases cover
short/wide addresses and null refs versus valid native bus address zero.

Focused predecessor suite 117/117. Stronger positive-to-negative byte/short setup
passed 2/2 afterward, with no compiler-code change:
`/tmp/koh-local-ref-signed-final.log`. Separate doctests and stable fmt checks exit 0.
Read-only source review complete; no delegated writers/builds remain. Constructor
head d73963a passed all 14 hosted jobs; completed macOS log confirms 450/450.
Array head4a32ba8 has successful completed Rust/CI workflows, native Linux/Windows/
macOS Test steps; completed macOS log501/501. RVA-head hosted results remain
pending. Historical legacy baseline
814/814 is reference evidence, not a correctness oracle.

## Next work and verification

Graphics acceptance committed as7c8b917; post-commit4/4 passed,exit0,18.300s
(/tmp/koh-tiles-postcommit.log). Refreshing ROADMAP in a separate docs increment
before pushing both commits to the same draft PR#55. Graphics unit adds actual TileSet.Load aligned/unaligned
DMG/CGB fixtures, two rendered frames with independent full RGBA expectations,
poisoned full VRAM/WRAM bank snapshots and canonical emulator PNG writer output.
No compiler algorithms or emulator core algorithms changed. Emulator csproj only
adds InternalsVisibleTo EmulatorHost so test shim can use the existing PNG writer.
Parent owns all edits/builds; read-only review must not edit/build. First copies
4/4 passed (/tmp/koh-tiles-first.log), then full rendered checks4/4 passed
(/tmp/koh-tiles-render.log). Current DMA-state strengthening/formatted fixtures
running session37450, /tmp/koh-tiles-dma.log. It checks programmed GDMA source/
destination and completion state on aligned CGB; other paths retain initial DMA
parameters. Current unit has4newRustcases/12ROMscenarios (four frame captures).
Full workspace588/588 passed,0skips,exit0,412.346s
(/tmp/koh-tiles-workspace.log);412compiler cases,410ROMscenarios(345normal,65traps).
Standalone serial direct-project CLI DMG/CGB demonstrations passed with all pixels
validated; /tmp/koh-tiles-root-demo.log,exit0. PNGs visually inspected, artifacts
/tmp/koh-native-tiles-demo-pXXzIw/{dmg,cgb}.{gb,o,png,rgba},evidence.json. Both ROMs
32768bytes, ELF80008/82380bytes, PNG822/805bytes. Doc/fmt checks exit0(no doctests).
No builds remain running. Publish current commits and verify draft/master head.
Predecessor19e4949 hosted Rust43676239605/CI43676239601 successful;
native three hosts Test steps succeeded, macOS584/584 confirmed. This is library graphics output,
not full 2048/other sample acceptance or LCD-on copy timing coverage.
All text below is predecessor history, not current build state.

Latest committed/pushed head is2c44000, switch unit; PR#55 remains draft/master.
Current uncommitted pin unit normalizes scalar byref/array pinned locals and
introduces StorePinned with per-frame pin flags/saved bank. Heap pins map bank1;
last release/method return restores the caller bank. Unbanked frame pins do not
change SVBK. Native zero cleanup is checked (fault8 for nonzero). Normal writes
cannot bypass pinned local initialization; frame-reference escape graph includes
pin stores. A transitive constant-SVBK-write check rejects known explicit bank
remapping in pinned methods; callers may select a bank before entering them.
Parent owns all edits/builds; read-only reviewers must not edit/build. Focused
14/14 passed in /tmp/koh-pinned-strengthened.log; new bank rejection checks and
formatted fixtures passed14/14 in /tmp/koh-pinned-bank-guards.log. Review found
StoreLocal could index initialized state before checking a malformed pin local
index; fixed and regression added. Review confirmed the fix. Strengthened15/15
passed,exit0,19.700s in /tmp/koh-pinned-review-fixes.log (includes one old test).
Workspace584/584 passed,0skips,exit0,394.810s in
/tmp/koh-pinned-workspace.log;408compiler cases and398ROMscenarios(333normal,65traps).
Separate doc/fmt checks exit0(no doctests). No build remains running. Pin unit
ready for commit/push. New fixtures cover real Mem.Copy,28ROMscenarios
across D/R x DMG/CGB plus5raw checked-cleanup scenarios; heap/bank snapshots,
nested/recursive/loop/empty/null/unbanked pin lifetimes. Read-only review complete.
After full tests, run doc/fmt checks, commit/push this unit and verify draft head;
rerun pinned fixtures post-hook. Next investigate actual TileSet/graphics control
and DMA runtime dependencies without weakening timing/output assertions.
Everything below is predecessor history, not current pending build state.

Latest committed/pushed head is448f35b (enum/stack/2048 Board unit), verified draft
PR#55 master base. Current uncommitted unit adds actual CIL switch lowering,
typed Switch CFG edges and full-int32 SM83 comparison selection. Parent owns all
edits/builds. Read-only review may inspect switch changes; do not edit or build.
Focused raw CIL/CPU8/8 and Roslyn/lowering12/12 passed, exit0; strengthened
IR/lowering/driver/CPU18/18 passed exit0,9.741s in
/tmp/koh-switch-final-focused.log, including13ROMscenarios. Review found/fixed
cumulative edge-argument cloning before budget validation; regression retains128
values across16,384targets and review confirmed the preallocation guard. Earlier
17-case run passed16/17 because the test expected lowercase diagnostic text.
Workspace570/570 passed,0skips,exit0,374.746s in
/tmp/koh-switch-workspace.log; compiler394cases,365ROMscenarios(304normal,61traps).
Separate workspace doc/fmt checks exit0 (no doctest cases). No build remains
running. Current unit ready to commit/push after updating this handoff. Four legacy switch
methods now have complete native behavioral replacements in the disposition
matrix; definitions remain until legacy-project deletion. Remaining561 methods
need completion auditing. Predecessor448f35b has successful completed hosted
Rust43672970728 and CI43672970693; native Linux/Windows/macOS Test steps succeeded,
macOS log552/552 passed0skips298.190s. Do not infer current-switch hosted results.
Text below records the completed predecessor unit, not pending work.

Current uncommitted unit adds enum storage normalization, typed object memory
operations, fixed-size acyclic stack allocation, and conservative physical spill
width selection. ABI widths remain unchanged. Board fixture links the actual
2048 Board.cs and tests all moves plus recursive/zeroed stack buffers, all enum
widths and pointer aliases in Debug/Release and DMG/CGB. Parent owns all edits and
builds. Read-only review completed with no confirmed findings; no delegated builds or writers.
Arithmetic width regressions passed 2/2, partial local alias regressions passed
2/2, both exit 0. Workspace552/552 passed,0skips,exit0,365.322s in
/tmp/koh-board-workspace-nextest.log; compiler376cases and352ROMscenarios
(291normal,61traps). Post-format8/8 passed,exit0,30.588s in
/tmp/koh-board-formatted.log. Separate doc/fmt checks exit0 (no doctest cases).
Previous RVA unit was committed and pushed as 293585d, with completed successful
Rust run43666575641 and CI run43666575657 queried live by commit SHA.
No build remains running. Current unit is ready to commit/push; after the hook,
rerun the four Board cases and verify the same draft PR's head.

Finish verification/commit/push this unit, update the same draft PR and verify its
remote head/draft state. Format changed fixture C# and rerun affected cases after
formatting. New stack-IR9/9 passed; the initial cyclic fixture incorrectly targeted
entry block0, corrected to a valid nonentry loop without changing the assertion.
Next pursue switch/pinned-storage and library/runtime support needed by actual
Tiles/gfx-demo sources, then aggregate/array/runtime
support toward real games; keep full IRQ/banking, Portable PDB/KDBG/debugger,
native SDK/incremental/package/CI, sample graphics execution, complete Rust test
audit/migration, C# compiler deletion, performance/platform and bounded Docker gates.
Do not replace the full goal with a polished primitive slice or retire useful
features merely to pass tests. Preserve Koh.GameBoy API and the existing emulator.

Build serially per output directory. Tests use
`KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet`.
Run workspace nextest/all-features/locked and separate workspace doctests. Use
`RUSTUP_TOOLCHAIN=stable cargo fmt --all -- --check`. Capture direct exits in logs
and read completed logs. Commits need the pinned dotnet directory prepended to PATH
for Husky/CSharpier. After its C# formatting, rerun the affected fixture cases.
Stage only this unit's files; do not touch unrelated main-checkout assets or merge.
