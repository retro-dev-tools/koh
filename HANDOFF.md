# Native compiler migration handoff

Full goal: one draft PR55 against master; C# authors/Roslyn, Rust CIL/IR/SM83
compiler and ELF linker, existing C# emulator. Do not merge or narrow acceptance.
Worktree /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Pushed rectangular increment deab1b2cd89a7b45b80925b2d7c33968eb0e53d4; PR55 checklist updated, draft/unmerged.
Preserve main checkout unrelated samples/csharp/gb-jrpg/screenshots/battle.png.

## Active delegate unit

LATEST 2026-11-16T09:52Z: construction/invocation now execute allstatic/captured/
virtual/wide cases. Ordinary8-byte checkedheap payload: header2, codeaddr2,
ROMbank2, retainedreceiver2. Native callbackwrappers use normalRootReceiver+args
ABI and direct/virtual sourcecalls; indirectInvoke sharesnormalargpack/farcall.
Closedworld reachability includeswrappers andsource targets. Verified ECMA
dup/ldvirtftn/newobj sequences replace an incorrect rawSSA-id comparison; branch
into interior rejects. Genericnominaltypes retain originalstorage widths.

Latest integrated selected478/478pass134.829s exit0,
/tmp/koh-delegate-integrated.log,71938terminal. All20actual delegate config/entry
cases forceeachmanaged function/wrapper/target into itsownROMbank starting1or257.
WideTarget haslive200Bstackalloc; bankedinstance/wide8/8pass39.528s in
/tmp/koh-delegate-banked-large.log. DE alias issue fixed: loadcodeBCbeforebankDE
because extendedSPaddressingclobbersDE. Subsequent full478run includes thatfix.
Additional verifier effects9/9pass0.083s /tmp/koh-delegate-verifier-effects.log,
88899terminal: indirectcalls included inbyrefescape andpinnedbank-write graph.
One read-only callback review will inspect currentuncommitteddiff againstHEAD
deab1b2. No delegatededits/builds; parentownsallfiles/serialbuilds. Next actual
JRPG SDKrebuild, remaining null/binding faults and returned/array callbacks,
review fixes, testdisposition/doc update and logicalcommit. Fullgoalincomplete.

Uncommitted tests/driver/delegates.rs, fixtures/DelegateCalls, driver module and
requirements docs/rust-compiler/delegates.md reproduce actual callback gap.
Sixteen nextest-discoverable configuration/entry cases; run eight DMG entries
Debug/Release:0pass8fail45.085s exit100 in
/tmp/koh-delegates-shapes-red.log, session67094 terminal. Desktop CLR first
executes allfour fixture shapes and returns42; native failures intentionally
remain red, never ignored or converted to rejection expectations. Static,
captured and wide-custom delegate paths fail ordinary framework class layout;
virtual Func local type fails GenericInst inDebug and ldvirtftn inRelease.
Initial four-case loop masked later entries and is replaced with per-entry
values. No native delegate execution or legacy closure claimed; metadata reader progress below.

Metadata implementation added in src/resolve/delegates.rs and shared framework
key lookup in resolve.rs. Resolver::delegate_type returns runtime constructor/
Invoke identities, raw signature and source-scoped generic arguments. Real
Action, Func<int,int>, WideCallback positive and malformed/forged-contract tests
pass2/2 in11.766s exit0 /tmp/koh-delegate-metadata-negative.log,2712terminal.
Earlier combined12case references run10pass2fail120.173s (negative assertion
expected wrong diagnostic; corrected). All10 existing reference cases passed.
TypedDelegate references now bind through IR/storage/fields/arrays/byrefcells.
Actual NullDelegateShapes executes42 inD/RDMG/CGB; canonical types distinguish
Func<byte,int> versusFunc<int,int>. IR rejects forged constants/rawpointerloads.
Selected343/343 pass36.383s /tmp/koh-delegate-typed-regression.log. Finalmetadata/
worklimit changes6/6 pass32.787s /tmp/koh-delegate-typed-final.log,44445terminal.
Latest /tmp/koh-delegate-handles-regression.log:353tests345pass8construction
failures83.043s exit100,92431terminal. All eightnonnull D/RDMG paths now reach
explicit callable-allocation diagnostic; no hidden fallback. Last alltargets
check9.15s success (existingcubehelperwarning). Alllocalhandles terminal. Typed method handles now lower ldftn/ldvirtftn, with binding-null check and
retained original receiver mapping. Delegate members resolve instantiated
Invoke signatures. Callable allocation/invocation/reachability still missing; no nonnull callback execution or legacyclosure claimed.
Next implement coherent typed delegate/method-pointer contract, reachability,
construction/invocation and bank-safe ABI before pushing this unit. Preserve
mutable captured storage and correct receiver-selected virtual targets; use
normal heap/calling ABI and exact metadata signatures, not old blob conventions.
Generic Func shapes matter forDebug/Release parity. See delegates.md for ECMA
requirements and remaining negative/banked coverage. Do not embed managed
runtime implementation or treat validSystem.Object nilbase as malformedinput.

Current-head native CI43833072527 was verified inprogress; managed43833072545
fails same JRPG callback in actual Ubuntu job133182850147 logs, buildexit1.
No current-head full/crossplatform green claim. No builds live locally.

## Rectangular-array increment

Rank2 zero-based array identity survives typed IR, cells and fields. Validated
runtime-provided constructor/Get/Set/Address and System.Array queries lower in
Rust. Ordinary checked heap stores count/rows/columns plus row-major payload;
mutable RVA copies, original-index checks, null checks, capacity and SVBK
restoration. Struct/reference/long fields and borrowed element references work.
No production emulator changes. Review rectangle_review found no critical or
important bug; added its requested bound/size/query metadata rejection probes.

Tests and completed logs:
- /tmp/koh-rectangle-final.log:30/30 pass183.953s exit0, session75244 terminal.
- /tmp/koh-rectangle-widths.log:8/8 pass46.495s exit0, session14447 terminal;
  explicit16/64-bit widths and original55 read before alias mutation.
- /tmp/koh-rectangle-shape-query.log:2/2 pass11.361s exit0, session3837 terminal;
  13metadata mutations per Debug/Release case.
- /tmp/koh-rectangle-allocator-regression.log:353tests349pass4fail190.642s
  exit100; only failures obsolete rank2 rejection assertions. Replaced with
  return4/exactheap/bank checks and rank3 rejection;4/4pass49.521s exit0 in
  /tmp/koh-rectangle-vector-corrected.log. Do not call original353run green.
- Formatting Rust and two C# fixtures succeeds; diffcheck succeeds.
- Workspace alltargets/allfeatures check passes9.88s exit0, existing cube-helper
  warning only; /tmp/koh-rectangle-check.log, session5096 terminal.

51/565 legacy source methods have full behavioral replacement,514 outstanding,
zero physically retired. Rectangular source method replaces private ROM-folded
initializer representation with ordinary mutable array execution. Separate
string-reference array source method remains outstanding in this file's audit.
See docs/rust-compiler/{rectangular-arrays,test-disposition,progress}.md.

## Next real sample dependency

Ordinary Release SDK build of unchanged JRPG with fresh native compiler advances
past World rank2 maps, then fails Villager::Interact/callback path at method
0x0600002a IL0005: Reference class has no base type. No ROM/gameplay claim.
/tmp/koh-jrpg-rectangle-build.log exit1,45.16s; session63851 terminal.
Next reproduce callback/delegate metadata in a tiny fixture and fix root cause,
then progress actual JRPG. Keep aspirational sample distinction explicit.

## Whole-goal gates still open

PDB matching/source mappings through ELF/KDBG; actual source breakpoints and
native frame/bank-aware stack inspection; interrupts/reentrancy/bankeddata;
remaining language/runtime/generic/float/delegate/interface coverage; all565
source-test dispositions; C# compiler/test/Mono.Cecil deletion; final packaging,
comparable performance and Linux/Windows/macOS full acceptance. No blanket
feature retirement or compiler-owned DEFER. Current API/emulator behavior is
never an independent oracle. See prior progress.md for hosted/local evidence.

## Commands and discipline

KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet
RUSTUP_TOOLCHAIN=stable /usr/bin/cargo, target-dir /tmp/koh-header-target, -j1.
PATH/DOTNET_ROOT that dotnet for hooks. Serial builds per output directory.
Timeout is not terminal: poll recorded live handles. Read complete logs and
actual exit codes. Final default cargo nextest run --workspace --all-features;
rustdoc separately cargo test --doc. Preserve unrelated dirty files.

Latest hardware-state followup ca835c2 waits nextVBlank before LCD disable.
Pan Docs LCDC91 is postbootPC0100, not cold-reset proof. Existing PPU91 plus raw
CPU/bootmapping needs independent emulator audit; no production reset change.

Historical implementation direction (now implemented; see checkpoint below): ordinary heap8-byte delegate (header2,
codeaddr2, ROMbank2, retainedreceiver2), normal IR-generated receiver+args
wrapper pertarget/delegatekind (static ignoresreceiver, instancecastsroot to
sourceowner thennormalCall). Invoke uses existingfarcall and bankcookie ABI,
loading addr/bank/receiver beforeadjustingSP. No truncatedbanked pointer or
secondaryallocator. Needed NewDelegate/Invoke IR verification/backend, callback
wrapper reachability, exact source signature compatibility and transient SSA
alias handling.

## Current callback checkpoint

Native callback implementation and real Roslyn fixtures form the current increment.
See docs/rust-compiler/delegates.md for the contract and completed focused runs.
JRPG SDK Release build succeeds:131072-byte ROM,0warnings0errors,45.81s,exit0
(/tmp/koh-jrpg-callback-build.log); gameplay remains unverified.
Full workspace/all-features nextest session22137 terminated143 after414passes,
without failure or summary. No nextest/driver processes remained when checked.
Original log preserved:/tmp/koh-delegate-workspace-interrupted.log. Same command
restarted via durable Python supervisor PID1106013, stored in
/tmp/koh-delegate-workspace.pid; current log /tmp/koh-delegate-workspace.log.
Terminal evidence:/tmp/koh-delegate-workspace-result.json (exit/elapsed), plus
completed log; absence of result is not success. Inspect process before restart.
Frozen source hashes:/tmp/koh-delegate-tested-sources.json,44entries match.
Do not edit production/fixtures/tests until terminal.
Commit the focused-verified increment with full regression explicitly pending;
this is progress tracking, not final acceptance. After terminal result:
reconcile11 legacy delegate methods, update ledger/docs, run separate rustdoc/
check as needed, verify source hashes, push and update existing draft PR55
checklist. No merge. Keep production/fixture sources frozen during the run.

Additional runtime evidence: existing Release emulator App DLL executed the
native JRPG ROM without rebuilding.600frames title capture:
/tmp/koh-jrpg-callback-600.png. Start press600/release602,900frames produces
visible overworld/HUD:/tmp/koh-jrpg-callback-world.png. Both processes exit0;
logs /tmp/koh-jrpg-callback-headless.log and -world.log inspected. Visual smoke
only, not independent movement/collision/dialogue/game-state acceptance. Input
script:/tmp/koh-jrpg-callback-input.txt. Tracked sample assets untouched.
Further scripted walk Right900/930/960 thenA990 enters a BAT encounter instead
of NPC dialogue. Capture:/tmp/koh-jrpg-dialogue.png, input
/tmp/koh-jrpg-dialogue-input.txt, log /tmp/koh-jrpg-dialogue.log,exit0. Visible
battle menu is smoke evidence only; do not label this a dialogue test/pass.
Pending11-method callback disposition matrix added without inflating51/565.
Hosted deab1b2 native workflow43833072527 now terminal green, primary logs
inspected: Linux job1331828502761120pass1075.379s; macOS1331828503621120pass
1597.915s; Windows1331828500381118pass2065.902s,0skips in full suites.
Separate rustdoc passes allhosts. Predates callback unit; managed JRPG failure
on pushed source remains historical evidence, not fixed-hosted claim.

Prepared next-unit test outside frozen tree:/tmp/koh-jrpg-sdk-test.rs. Intended
append to tests/sdk/game.rs after callback regression/commit, reusing private
metadata helpers. Actual source/art copied into isolated SDK project; CGB-only
Debug/Release (sample intentionally has no DMG path), ELF/link equality, boot,
title/overworld descriptors, Hero fields and one Left move, independent16-bit
PRNG transition/encounter-type oracle. NOT compiled/run/discoverable yet; do not
count coverage. Review/fix before applying. Further collision/dialogue/battle/
pixel acceptance still required; see jrpg-acceptance.md.

Callback increment committed/pushed7b8a4569ff5d2958d8691a49a0181fc6f49a16d9.
PR55 description updated/read back draft/unmerged; final regression pending.
To avoid mutating frozen tests, committed snapshot exported to
/tmp/koh-jrpg-acceptance-1mc04wdf (not another git worktree). Prepared JRPG test
appended to its tests/sdk/game.rs only. Isolated cargo/.NET outputs under that
export; focused native SDK test is LIVE session39544, log
/tmp/koh-jrpg-acceptance-first.log. Original full run remains supervisor1106013.
No test acceptance claimed until terminal. After test fixes, transfer only the
new test to the real worktree once original regression is terminal, then verify
against actual worktree. Do not lose outside-tree draft at session handoff.
Isolated first run: Debug case PASS229.484s (fresh SDK native/emulator builds),
Release case still running session39544. The Debug assertions include actual
boot, ELF/link equality, CGB-only header, scene descriptors, exact Hero fields,
one Left move, preserved identity and independent PRNG/encounter-type state.
Do not claim full2-case success or full JRPG acceptance from this partial result.
Initial isolated acceptance subsequently completed2/2 PASS360.652s,exit0:
/tmp/koh-jrpg-acceptance-first.log. Next expanded dialogue run is RED2/2:
/tmp/koh-jrpg-dialogue-acceptance.log,exit100,55.825s. Added test-harness-only
script write action to seed existing Rng state2D2D, three Right steps, blocked
NPC step and three dialogue pages/close callback; original sample unchanged.
Zero mode3 violation assertion finds157Debug/239Release attemptedVRAM writes.
Do not weaken zero assertion or declare sample accepted. Suspect existing
MapWriter.WriteAttrVerified: WaitForVramAccess + store/readback intentionally
retries dropped writes; live dialogue FillAttr reaches it. Need trace/confirm,
then fix hardware-safe transaction rather than copying legacy retry semantics.
Trace instrumentation only in export: GameHost prints first20 violations to
stderr; Rust prints native function symbols and hoststderr. Debug-only trace
LIVE session29701, log /tmp/koh-jrpg-dialogue-trace.log. No production fix yet.
Trace now terminal RED1/1,exit100,28.267s. MapWriter.WriteAttrVerified native
symbol starts20455/bank7; all first20 violations PC20583, attribute6 into39329+
(dialogue window row13). Existing source explicitly retries stores dropped
after WaitForVramAccess races. Root cause is unsafe live-attribute transaction,
not a reason to accept the old library's behavior as specification. Next inspect
existing backend timing/interrupt mechanisms and choose minimal safe transaction;
retain exact zero-violation assertion. No fix has been attempted. Export contains
expanded formatted test and changed GameHost; /tmp/koh-jrpg-sdk-test.rs is now
the older initial draft, not the latest test. Preserve actual export files.

## Latest isolated graphics fix checkpoint

Prototype fix is now implemented ONLY in /tmp/koh-jrpg-acceptance-1mc04wdf.
New metadata vramwrite staticvoid(byte*,byte), typed/effectful IR, remapping and
SM83 guarded store. Operands loaded before DI; pointer-range guard8000..9fff;
pollSTATbit1 untilmode0/1 then immediate byte store. Conservative44T from read
through store; accessible following mode2 is80dots (PanDocs raw sources checked).
Successful calls leave interrupts disabled, including LCD-off; no IME-preserve
or reentrancy claim. Public Bg/Win attribute remarks now disclose restriction;
DMG no-ops/empty fills do not execute operation. Existing C# library body is
simple desktop store, no compiler fallback. Old racy retry loop removed.

Completed isolated evidence (actual logs inspected):
- /tmp/koh-jrpg-safe-attr-first.log: Debug red-to-green1/1,91.172s,exit0.
- /tmp/koh-vramwrite-core.log: typed/effect/remap/address/largeframe6/6,6.162s,exit0.
- /tmp/koh-jrpg-safe-dialogue-clean.log: cleaned Debug/Release2/2,178.562s,exit0,
  three exact pages, NPC collision and retained callback return, zero mode3 writes.
- /tmp/koh-vramwrite-phase.log:4/4,110.511s,exit0;9,856 probes across four methods,
  twoLCDstates,154lines*twodots*fourconfigs. Exact wholeVRAM/bank/onewrite,
  SP/SVBK/livevalues/IMEdisabled and duration; real extended SP offsets asserted.
New tests driver/vram.rs, fixtureVramWrite, VBlankHost optionalLCDoff testaction;
GameHost write action for deterministicRngseed. Trace instrumentation removed.
Docs in export docs/rust-compiler/vram-write.md. Latest XML/comment-only edits
after tests need formatting/checks. No build currently active in export.

Main callback regression supervisor1106013 still LIVE, latest1013/1193.
Only HANDOFF/frontend-audit docs modified in realworktree; production/tests
still frozen at pushed7b8a456. Next: wait/verify full result/sourcehashes, then
transfer scoped exportdiff, broaden focused regression (existing VBlankHost
consumer, SDK2048 etc), review hardware timing/contract, update docs and commit
one logical graphics/JRPG acceptance increment. Full battles/pixel acceptance,
PDB/IRQ/testclosure/legacydeletion/perf/release still required. No merge.

Read-only review unit: review guarded VRAM operation in isolated export against
committed7b8a456, especially timing, DI contract, address validation, stack/register
aliasing, metadata/IR side effects, phase-test coverage. Reviewer owns no files,
runs no builds and must not alter either checkout. Parent owns all code/test
changes and build directories. Verification: inspect export vram-write.md and
new contracts/IR/lowering/emission/tests; report concrete correctness defects.
Review /root/vram_write_review finished read-only: no blocking defect found.
It confirms pointer/load_a push accounting, DE alias handling,4byteguard, timing
and metadata/IR consistency. Coverage caveat:2dots/scanline and fixeddot252
unscrolled/noOBJ oracle, not exhaustivePPUphases; documented in export contract.
No review builds/tests performed. Parent completed evidence remains separate.
Broad isolated regression LIVE session42962, /tmp/koh-vramwrite-broad.log:
396selected across IR/SM83, existing bounded-copy4, SDK2048four and JRPGtwo.
Do not run another export build until terminal. Maincallback1193 remainslive.

## Callback verification terminal

Supervisor1106013 completed; resultjson exit0, elapsed2764.626533s. Actual full
nextest log:1193/1193 passed,0skipped,2slow,2764.108s. All44 frozen source hashes
match. Separate rustdoc passes fourcrates/0cases,exit0; allfeature/alltarget
workspacecheck succeeds13.04s,exit0 with existing cube-helper unused warning.
Logs /tmp/koh-delegate-rustdoc.log and /tmp/koh-delegate-check-final.log inspected.
Ledger now62/565 completed methods,503outstanding,0physicallyretired. Callback
sourcegroups8+3 markedcomplete with exact traceability; no feature retirement.
Main source freeze can end. Existing isolated graphics broadsession42962 still
live and owns its exportbuilddirectory; do not mutate export fixtures mid-run.
First record/push this documentation checkpoint and update PR55 (not merge),
then transfer scoped graphics diff and verify actualworktree. Pending source
changes in export are independent of the verified callback1193 snapshot.

Checkpoint docs committed/pushed63cca81f398a2229658a4c9fe441fa0031c24999.
PR55 read back draft/unmerged with1193pass and62/565ledger. Then18scoped graphics
files transferred from export to actualworktree after cleanstatus assertion.
Current worktree verification LIVE session49275,/tmp/koh-vramwrite-worktree.log,
12selected new IR/SM83/phase/JRPG cases. Broad export396 run still LIVE42962.
No additional build in either targetdirectory until its run is terminal.
No graphics commit yet; actualworktree graphics/doc/tests now dirty by design.

## Graphics/JRPG unit verified for commit

All local handles above are now terminal. Actualworktree12/12 passes275.699s,
exit0. Isolated broad396/396 passes472.676s,exit0, including IR/SM83, existing
bounded copies, SDK2048four and JRPGtwo. Added deterministic Slime battle to
exporttest:2/2 pass57.366s; transferred finaltest and renamed to
sdk_jrpg_dialogue_and_battle_preserve_hero_state, actualworktree2/2 pass56.038s.
Review followup independently confirms seed0002 sequence0302,C143,93F3,2C46,
4E41; heroattacks5/5, enemy4,HP16,Exp5 and returnOverworld. No blocking findings;
reviewer did not run tests. Checks/source evidence owned by parent.

Actualunchanged Gbjrpg.csproj ordinaryReleasebuild with outputpath overrides
only succeeds2.92s,0warnings0errors,131072-byteROM,headerC0 and ELF32LE object.
Log:/tmp/koh-jrpg-guarded-sdk-build.log. Allfeature/alltargetcheck succeeds10.66s
with existing cube-helper warning; rustdoc fourcrates/0cases passes,exit0.
Logs:/tmp/koh-vramwrite-check.log,/tmp/koh-vramwrite-rustdoc.log. Rustfmtcheckpass.
190 authored Rust/C#/project source hashes captured for posthookcomparison:
/tmp/koh-vramwrite-tested-sources.json. Unrelated maincheckout battle.png still
fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454.

Ledger64/565 complete,501 outstanding,0retired. Close only JRPGcompile/header;
playthrough/capture methods stillpartial due art/OAM/palette/artifact assertions.
Sample README removes obsolete3byteblob/layout conventions and points to native
acceptance; samplesources/assets unchanged. Explicit IME-disabled contract stays.
Next commit/push scoped graphics/JRPG unit, verify sourcehashes/remotehead, update
existing draftPR55 checklist/evidence. Newhead hosted/full-workspace validation
remains required;1193fullpass belongs precedingcallback snapshot. No merge.

## Next visual acceptance unit

Graphics/JRPG unit is now pusheda19d67ad4d4165a041ac0a6f0b4a53f206f7c406;
PR55 read back draft/unmerged with64/565complete,501outstanding. Worktree wasclean.
Newhead managed CI43860434567 terminalfailure: Linux133275524551 has110 legacy
C#compiler test failures; Windows133275524378 has110 samecompiler failures plus
one LSPtimeout. Primary logs show unsupported legacyintrinsic kinds memfill,
memcopy,vblankcopy,vramwrite. Do not port newalgorithms back to oldcompiler or
weaken tests; nativecoverage closure/deletion stillrequired. Windows separate
UnreadLargeRequestTimesOutAndClientCanRestart elapsed6.1650563s exceeds5s.
Proposal for LSPowner: investigate Windows deadline/cancellation underload;
no threshold increase or unrelated LSPimplementation change in currentunit.
Native workflow43860434611 stilllive: refs133275524120,mac133275524423,
Linux133275524527,Windows133275524540. No hostedgreenclaim.

Current scope: finish two remaining JRPG legacytest requirements with native
OAM/terrain/palette/noresidue assertions, independent PNGpixel checks and
isolated persistent4scene captures. Existing png0.18.1 is installed locally;
use it only as Rustdevdependency instead of customPNGdecoder. Actualsample
code/art unchanged. First inspect/build/runfocusedtests; no new production
graphics/emulator subsystem. All priorlocal test handles terminal.

Visual unit now implemented in tests/sdk/jrpg.rs and expanded GameHost data
capture (OAM/OBJpalette/video/rawCPU), plus test-only png0.18.1 dependency.
First oracle assumed channel-bit truncation; asset pipeline uses normalized
nearestRGB555. Independent32-level minimum-error search plus6fixedcolorvectors
and hardwarebitplane vector replace that unsupported oracle assumption. Exact
pixel comparisons retained. Visual run9/9 passes56.693s,exit0, captures4scenes
perconfig under testbinary/TestResults/gb-jrpg. ActualPNGs reviewed, no curated
images changed. QuickMove snapshot after4frames matches legacy no-ghost budget.

Added original300frame title deadline counted from power-on, not postboot.
Optional GameHost power-on flag preserves existing2048 relative-frame protocol.
Deadline runRED2/9, actualsceneBootScene vsTitleScene at300. Diagnostic artifacts
/tmp/koh-header-target/debug/deps/TestResults/gb-jrpg/Debug-K7SiUe, rawPC473E,
SPCDF5,bank4, __koh_a1_m060000dd.IL_0034.82, TileSet.Load1bppCore.
Budget retained. Streamed source/destination pointers and hoisted four color
masks in C#graphics runtime, reusing Rust vramwrite via internalGraphics.Vram
declaration sharedwithMapWriter. No newcompiler/name pattern or publicAPI.
Deadline visualrun9/9 nowpasses56.334s,exit0 /tmp/koh-jrpg-font-deadline.log.
One semantic-preserving inversebyte change to XORFF afterward needs final rerun.

General mono fixture/driver added for all16ink/paperpairs, LCDon/off, selected
VRAMbank, firsttile255wrap and empty/null input. LIVE session52540,log
/tmp/koh-mono-first.log,4configs. No otherlocalbuildactive. After result run
allSDK/existingtile/vram regression, final checks/format and sourcehashes. Library
Load1bpp XML still needs explicit updated interrupt restriction and removed
stale per-row managed-wait description. Newcompiledfontcase doesnotretire
legacy tests until fulltraceability/coverage checks complete.

Read-only review scope for thisunit: fontloop semantic equivalence, tilewrap/
count/masks, publiccontract changes, independentPNG/palette/OAM/pixel oracles,
power-on/frame deadline accounting and captureisolation. Reviewer owns no files
or builds; parent owns all edits/testoutputs. Compare dirtytree againsta19d67a.

## Current visual/font continuation

Mono first matrix terminal4/4pass28.122s. Review-required quickMove framebuffer
region added to assert oldHero16x16 is grass, plus explicit initial/closed/won
HUD HP/LV glyph assertions discovered in original test cross-check. Public Font
and Load1bpp remarks now disclose interrupts disabled. Rustfmt applied.
LIVE final focused session57280,/tmp/koh-jrpg-visual-final.log,42selected
SDK/mono/tile/pin/VRAM tests. HUD assertions were added after this binary built;
run final sdk selection again afterward before committing. Same targetdir
/tmp/koh-header-target remains owned by this run. No concurrent cargo build.
Do not claim matrix closure or68/565 until final edited tests pass.

Hosted a19 native workflow43860434611 now terminalsuccess, actualprimarylogs
read: Linux1205/1205,1133.700s; mac1205/1205,1280.221s; Win1203/1203,1966.459s,
zero skips. Rustdoc/artifact/native-backedLSPpublish steps success on allhosts.
progress.md records jobIDs and separate managed legacy/LSP failures. This does
not validate currentdirtyvisual/font unit. Next terminalfocusedrun, finalSDK
rerun, workspacecheck/rustdoc/rustfmt, closeexact4legacy requirements ifproven,
reviewfinaldelta, commit/push logicalunit, updatePR55shortchecklist. No merge.

## Visual/font unit ready for commit

All localhandles terminal. Final42/42pass478.485s,exit0, includesallSDK plus
mono/tile/pin/VRAMphase regressions. FinaleditedJRPG/HUD9/9pass56.031s,exit0.
Workspaceallfeature/alltargetcheckpass21.55s(existingcubehelperwarning); separate
rustdocfourcrates/zero casespass8.63s; rustfmtpass. FourlatestDebugPNGs visually
inspected; standarddecoding equalsrawframebuffers. Maincheckoutbattlehashintact.
Docs/testledger68/565complete,497outstanding,0retired; exact2font+2JRPGmethods
closed with independent art/mask/pixel oracles, retained300framepowerondeadline
and4framemovementbudget. Allsourcehashes /tmp/koh-jrpg-visual-sources.json.
Next scopedcommit/push, compareposthookhashes, refreshPR55description/readback.
No fullmigrationcompletionclaim; PDB/debug/IRQ/language/legacydeletion/perfremain.

## Portable PDB reader unit in progress

Visual/fontunit pushedfd547205b4f11588f8d8275c7d9e4a9224588a56; PRreadbackdraft/
unmerged,68/565ledger. Worktree nowdirtyONLYnewPDBreader/libdriverwiring/docs.
Designrequirements docs/rust-compiler/source-debugging.md. Readerusesclrmeta
root/table/unsignedreader, isolatedcorrect6/13/28signedsignextension. Existing
clrmetasigneddecoder provedRED; ninefixedvectors nowgreen. Symbolic13.9evaluated
publiccache discards hiddenpoints, unsuitable for preciseprovenance; nodependency.
Handencodedmetadata/hidden/docdelta/truncationtests17/17pass; emptytablemissing
heap regression reproduced thenfixed. RealRoslynD/R fixtureSourceDebug2/2pass.
Currentfinalselected133cases LIVE96865,/tmp/koh-pdb-reader-final.log; includes
allcompilerlibtests+twoRoslynPDBcases. Same /tmp/koh-header-target nootherbuild.
Newreader is notyetCLI/PEmatching/emission/KDBG/debuggeracceptance; noclosureclaim.

Read-only reviewunit: newuntracked src/pdb.rs, tests/driver/pdb.rs, fixture
SourceDebug and source-debugging.md againstpushedfd54720. Reviewer ownsnofiles,
runsnobuilds; parentownsalledits/targetdir. Audit malformedinputbounds,delta
semantics,hidden/documentchanges,tablewidths/worklimits and testoracles. Parent
will addressconfirmeddefects thencheck/document/commitlogicalreaderincrement.

## PDB reader reviewed terminal checkpoint

Newreader src/pdb.rs implements bounded standalone root/document/methodsequence
decoding and per-method source lookup. NO PEidentity/CLI/ELF/KDBGintegrationyet.
Read-onlyreview found cumulativezero-outputblobscanamplification,UTF8separator
andtrailingdocumentrecord compatibility defects. Each hasindependentREDthen
greenregression. Cumulative64MiBblobworkbudget includesrepeatedrows; perfile/
rows/points/expandednamecaps; UTF8one-scalarvalidation; trailingdocvaliditychecked
thenEOFaccepted. Nodupframework/no newdependency/no unsafe.
Finalselected145/145pass11.896s,exit0:/tmp/koh-pdb-reader-reviewed-final.log,
30PDBunit+2RoslynD/Rcases plus113existinglibcases;243drivercasesunselected.
Workspaceallfeatures/alltargetscheck6.02spass(existingcubehelperwarning),
separaterustdocfourcrates0casespass,rustfmtpass. Alllocalprocessesterminal.
Sourcehashsnapshot /tmp/koh-pdb-reader-sources.json forposthookcomparison.
Next commit/pushlogicalreaderunitandrefreshPR55,keepledger68/565unchanged.
Then PEdebugGUID+timestamp andzeroedIDchecksumvalidation,adjacent/explicitPDB
discovery,emittedranges/readablefunctionsymbols andexistingdebuggerexecution.
Do notclaimdebugging/fullmigrationdone. Maincheckoutbattle.pnguntouched.

## PE/PDB matching unit in progress

Readerunitpushed e36b655bcb6f36b5af3f021ceb1c3c3a58449dea,PRreadbackdraftunmerged.
HostednewheadCI43873387480/Rust43873387467confirmedLIVE; notpolledterminalyet.
Currentdirtyunitadds src/pdb/identity.rs, Assembly.image crate-onlyaccessor,
PortablePdb.for_assembly and SHA2=0.10.9 dependency(defaultfeaturesoff) withlock.
UsesrawPortableCodeViewGUID16+debugtimestamp4,Age1, boundedpaths/records,
SHA256/384/512checksumsoverPDBwith20byteIDzeroed, atmost3hashes cachedperalgorithm.
DoesnotfollowCodeViewpaths. Multiplechecksumrecordsallowamatchingpublic/private
PDB; multiplePortableCodeViewIDsdiagnose. Checksumrecordversion1.0required.
CLI/ELF/sourceintegrationstillpending; thisdoesnotclosethedebuggate.

RealRoslynD/Rmatch initiallyREDnotimplemented; afterimplementationvalidPDB+
all4GUID/stampmutationprobespass. ChecksumtestinitiallyassumednonemptyStrings
heap(wrongfixtureassumption), changedtoGUIDpayloadmutation;2/2pass10.740s,exit0
/tmp/koh-pdb-match-roslyn.log. Rawentry/hashvectors8pass1REDunsupportedversion,
versionvalidationfixed. Finalselected LIVE39681,/tmp/koh-pdb-identity-first-final.log,
alllib+PDBdrivercases. No concurrentbuild in/tmp/koh-header-target.

Read-onlyreviewidentityunit: reviewerownsnoedits/builds; parentownsallsource.
Audit rawPEranges/tablebounds,PortableCodeViewIDrepresentation,checksumzeroing/
multiple-recordsemantics,workamplification,malformedmetadata+hashoracles.
Comparecurrentdirtytreeagainstpushed e36b655. CLI/discovery/ELF/debuggerknownnext.

## Identity matching unit ready for commit

Finalselection154/154pass11.857s,exit0 /tmp/koh-pdb-match-reviewed-final.log.
39PDBunit+2RoslynD/Rcases plus113existinglibcases;243drivercasesunselected.
Bothgame/referencePDBsmatch. GUID/stampmutationsreject,GUIDheapmutationrejects
checksum. RealPEsecond-checksumfixturetestsbadbeforegood/goodbeforebadaccept,
allbadreject. Readonlyreviewnoconfirmeddefect,coveragegapclosed. Separatehash
SHA256/384/512vectors independentlyPythonchecked. ChecksumversionREDthenfixed.
Workspacecheck19.01spass(existingcubehelperwarning); rustdoc4crates0casespass;
rustfmtpass. No localbuildlive. Sourcehash /tmp/koh-pdb-match-sources.json.

LicenseinventorypreviousPNGmissingnoticesdetectedandfixedalongnewSHA2notices.
All60externallockedpackagesnowcoveredwithexactselectedarchiveMIT/Zlibtexts.
NoAPI/sourceDBuserworkflowchangeclaimeduntilnextunit. Nextcommit/pushmatching
unit,PRdescriptionreadback; thenadjacent/explicitPDBdiscoveryandELF/KDBGmapping,
followedactualdebuggerbreakpoint/stacktests. Legacyledgerremains68/565.

## Source mapping / CLI / SDK unit in progress

Pushed98f77a2 matchingunit,cleanstart. CurrentdirtyRustcompile/sm83/cartridge/PDB
adds object_with_debug validatedorderedrawPDBinputs, survivingILlocations to
machinebyte ranges,patchprovenance,readablemetadatafunctionaliases. Prologues/
hiddencode/runtimehelpersnotmapped. Source caps4MiB/function,64MiB/module;
combinedPDB256MiBinput/16MiBexpandedpaths/1m points. Badcounts/orderdiagnose.
CoreD/R ELFroundtrip+link+ROM42 first2/2pass11.049s; strengthened4/4pass11.008s
includinghiddenemittedspans,prologues/softwaremulhelper exclusion,budgetdiagnostic,
optionalroot/referencePDB combinations. NoPDB/sourcePDB ROMbytesidentical.

NativeCLI nowauto-discoversadjacent PDBs, explicitroot --pdb or --no-pdb, preserves
outputonmalformedPDBandrejectsPDB/output/assemblypathaliases. CLItests2/2pass
10.876s. SourceDebugfixtureMainnowcallsrealRgb.Make(9,1,0)thenHelper =>42.
SDKTask PdbPaththinargument+--no-pdbonEmitDebugInfofalse,protectsexistingPDB
inputs. SDKTargetPDBinputs/propertystampincludespresence/pathsforincremental.
FirstSDKrun2/2RED130.255s atMSB4012 itemlistconcatinStamp; correctedtoitem
transformsafterrunterminal. LIVE61376 /tmp/koh-pdb-sdk-stamp-fixed.log,2 SDKcases.
No concurrentbuildin/tmp/koh-header-target; rootnativeMSBuildtargets are distinct
target/compiler-msbuild/Debug,Release. Do noteditowningSDK/Rustsourcesmidrun.

Reviewunitforreadonlyagent: allcurrentdirtydiffagainst98f77a2 acrossRustcompile,
sm83,pdb,cartridge,main +BuildTasks/SDKtargets andtests. Parent ownsalledits/builds.
AuditPDBprovenance,hidden/syntheticranges,resourcescaps,symbolaliasing,CLIpath
safety,SDKoptional/incrementalflags. CheckwhetherreachablePDBoffsets should
validateagainstactualCILinstructionboundaries beforemapping; currentlymatching
validatesPDBmetadataandPEid/checksum,notactualoffset-to-bodyconsistency. No
actualdebuggeracceptanceyet; existingStackTraceHandlerheuristicdoesnotunderstand
compilerframesandbankbytewidthlimitationknown. Do nottreatthosebugsasspec.

## Source mapping SDK now verified

FirststampfixSDK2/2RED30.954s because testtouched bin PDB, which SDKcopiesback
fromobj. SDKprimaryCopytargetconfirmedsourceis_DebugSymbolsIntermediatePath.
Probe nowtouchesobj PDB,assertscopiedbinmtimeequalsthatinput,retainsnativeROM
rebuildassertion. FinalSDK2/2pass125.502s,exit0(two62s cases),log
/tmp/koh-pdb-sdk-intermediate.log. Bothroot/reference maps,incremental/no-debug
flags,outputprotection/pathswithspacespass. SDKCodefrozenafterthischeckpoint.
Readonlyreviewfindsnoconfirmeddefect; ILinstructionboundaryextra-validation
notexplicitPortablePDBformatrequirement,notaddedasspeculativepolicy.

Broadercurrentregression LIVE(log /tmp/koh-pdb-mapping-regression.log),allcompiler
lib+allsm83+PDB/sourceCLI+actualSDKgamescases, same/tmp/koh-header-target.
Do notbuildconcurrentlythere ormutateproduction/fixturesduringrun. Nextinspect
terminalresult,checks/rustdoc/fmt,documentknownremainingactualdebugger/stackgate,
commit/pushthissourceCLI/SDKunit. Ledger68/565unchangedunlessexactlegacy
requirementfullyproved. ExistingnoPDBtestalsoasksPEwithoutPDBmetadata,notjust
missingfile; currentmissingfileCLItestsnotthatfulllegacyclosureyet.
Hosted98f77a2 Rust43875274042 macjob133326574819completed success; Linux
133326574764andWin133326574969stillinprogresswhenpolled. Macprimarylognotread
yet,nocountclaim. Currentdirtytreeincludesreadablealiasesinplainobjectoutput,
so broadregressionneeded. Actualdebugger/sourcebreakpointstillnextunit; no
Debuggerproductionrewrite. No concurrentparentbuildoutsideactivecommand.

## Mapping regression terminal / milestone regrouping

Broader254/254pass416.277s,exit0 /tmp/koh-pdb-mapping-regression.log; allselected
lib/sm83/PDBCLI+6SDKgamecases,253intentionallyunselectedinthesebinaries.
Workspacecheck9.11spass(existingcubehelperwarning); rustfmtpass; rustdocbeing
checkedseparately. Sourcehashsnapshot /tmp/koh-pdb-mapping-sources.json.
AllnativeSDK/runtimehandlesabove nowterminal. No currentdebuggerproofclaim.

Useraskedwhy30-60iterationsandrequestedjustfewmilestones. Thatestimatewas
toospeculativeandcountedmicrosteps. PR55descriptionnow4remainingacceptance
milestones:1source-debug2compiler/runtime/hardware/games3testclosure+legacy
removal4finalproduct/release/perf/hosts. Userwantscontinuedworkwithinmilestones,
notstoppingaftereachinternalstep. Existinggoalunchanged; do notpromise4turns.
PRstillhead98f77a2untilmappingcommitpush, draft/unmerged,68/565ledger.
Nextcommitmappingunitafterrustdoc/sourcehashcheck, refreshPRmilestone1progress
withoutcheckingitcomplete. Continueactualdebugger/KDBGloader/breakpoint/stack
inspection. Existingmethoddisplaynamesomitnestedparents; module/tokenaliases
areunique butqualifiednamesmaybeimprovedduringdebuggerwork. Existingcall-stack
walkerheuristicandbankbytearenotABIorhardwarespecs; donotadaptABItoimitatebugs.


## CI simplification checkpoint

User requested fewer CI steps on every commit. Commit 180eb3356136376b23bca45de30d7691ef73bbe0 is pushed to draft PR55. Ordinary code PR pushes now start only two Linux jobs: full Rust workspace/all-features nextest plus rustdoc, and Koh.Fast .NET tests. Dependency/build caches and superseded-run cancellation enabled. Full validation retains platform matrix, renderer, AOT, full .NET suite, reference/sample/benchmark checks, running on master, non-draft open/reopen/ready or manual dispatch once workflow reaches master. Tag releases unchanged. Seven protected check names/matrices/steps and strict protection verified unchanged by primary API and structural comparison. Manual default-branch limitation documented in docs/ci-validation.md.

Actionlint1.7.11 exits0 with empty completed log; git diff --check passes. Hosted run43886672981 confirms exactly Rust checks and .NET fast tests jobs on current head, both running; no hosted success claim. Prior7c04 Rust run43883525888 remains running; prior managed CI43883526018 failed. PR body updated with CI policy, four migration milestones unchanged. Source debugging remains next migration task; full migration incomplete. No concurrent local builds. Working tree clean after CI commit; main battle.png hash preserved.

## Source debugger acceptance review unit

Owned dirty increment: tests/driver/symbols.rs plus SourceDebug fixture, new
DebuggerHost fixture, serde_json dev dependency/lock, source-debug docs/audit.
Rust drives existing DebugSession/DAP using Roslyn Debug/Release DLL/PDB,
canonical ELF, Rust linker ROM/KDBG, source breakpoint, register/source frame,
function symbols and ROM-matching disassembly. No production debugger rewrite.
Genuinely no-PDB PE build/execute tested separately. Parent test process94389
running selected10driverPDB/symbol cases in /tmp/koh-header-target, no otherbuild.
Review read-only; own no files; do not start another build. Inspect teststrength,
shared project outputs, protocol correctness, unsupported callerunwindclaims.


## Source debugger execution verified

Final selected PDB/CLI/DAP suite10/10pass69.777s, exit0,243intentionally
unselected drivercases /tmp/koh-debug-symbols-reviewed.log. Same-session native
Debug/Release break+resume returns42 A00/SPD000, mapped physicalsource topframe,
functionsymbols and ROM-byte disassembly checked. No-PDB rootPE truly noCodeView/
embeddedPDB/checksum. Reviewfixed sharedRebuild: copiedproject now references
unchanged tempDLL, neverrebuildsruntimewithsymbolsdisabled. Reviewrecheck no
furtherconfirmedfindings. Alltarget/allfeature workspacecheck13.46spass(existing
cubes build_host warning), separateworkspace rustdocpasses0cases;rustfmtpasses.
Ledger69/565complete,496remaining,nonephysicallyretired. Callerstackunwind and
realgame source-debug acceptance stillopen; do notcounttopframe ascallerproof.
Sourcehashsnapshot /tmp/koh-debugger-sources.json beforecommit. Parenthandles
94389,34286,19036,66006 nowterminal. No live localbuild. PR55stilldraft.
Next commit thisverifiedincrement andpush, updatePRmilestoneprogress without
checkingmilestone1complete. Investigate nativeABI-awarecallerunwind without
reproducing StackTraceHandler's arbitraryword/FFFE/bankbyte assumptions.

## Native caller unwind implementation unit

Parent owns sm83 exact-PC debug markers and existing debugger integration plus
Rust DAP caller assertions/docs. No ROM ABI/code instruction changes planned.
Use versioned reserved marker symbols through canonical ELF/KDBG; source IL
boundaries layout.size, exact call resumes layout.size+pending args/results.
Banked E cookie PC atE+2/bankwordE+4, nextSP E+6; validate far_return symbol,
WRAM0 stack bounds, increasingSP, maxframes. Missing/ambiguous/unsupported state
stops native unwind; retain existing assembly path separately. Marker names
central/versioned/strictly parsed; metadata resource budget applies. Reject
in-between prologue/helper/temporary-push states, not infer by nearest label.
Current redtest handle17325 /tmp/koh-unwind-red.log tests Probe→Inspect source
caller independent of arbitrary stackword scans. No other parentlocalbuild.
Review agent can inspect only, no files/build ownership. Next reviewed native
state constants must reflect actual call resume size after bankcookieconsumption.

Unwind implementation dirty now: Rust sm83::Code.mark_unwind uses budgeted
versioned exact-PC local labels atentry, sourceIL points, callresumes; noROMbytes.
Existing debugger Session.NativeStackUnwinder validates recipe conflicts, cookie
stub, u16bank, ROM/SPbounds; StackTraceHandler nativebranch exactonly, legacyasm
branch preserved. Variables hidesreservedmarkers. Rust tests force managedmethods
todistinctbankswithsame4000base, checktwo recursiveframes androotcaller D/R DMG/CGB.
Thin DebuggerHost state injection enables18handbuilt malformed/bounds/cookie/limit
scenarios; assertionsallRust. Parentselected13caseshandle82180 currentlyrunning
/tmp/koh-unwind-cases.log; no otherbuild. Initialbankeddisassemblytestfailureused
wrong rawROMoffset; correctedexpectedbank*4000+address-4000, not emulator changes.
Reviewownership remainsreadonly; source-debugdocs pending contract/evidenceupdate.

Broad regression handle90201 active selected255cases, includes all157compilerlib,
allsm83target, PDB/symbol drivercases, four CLIparitycases and sdk_dotnet_run.
Initialbudgettest defaultempty sectionname invalid; fixed namedsection fixture.
Reviewfound unconditional unwind aggregatebudget gap (fixed unconditional all
function-symbol/provenance accounting,3boundary/overflow tests) and unusednearABI
bankinference. Simplified unshippednear branchaway: only production b marker
emitted/accepted; n explicitlyunknown. Review recheck no furtherconfirmedfinding.
PrimaryCI81af run43888582597 failed947/1267 at driver CLI/library byteparity:
defaultCLI nowautoPDB whereas plainobject API issymbolfree. Fixedexpectations
checkbothdefaultdebug andexplicit--no-pdb forbyteparity+repeatability+inputintegrity;
allfourupdatedcases passed currentbroad log. Keepfix separatelogicalcommit.

Broad255/255 passed438.133s exit0 /tmp/koh-unwind-regression.log incl157lib,
80sm83cases,17drivers andSDKheadlessgraphics;262unselected explicitly.
Committed CIparity fix09d332d (notpushedyet) separatefromunwindunit. Parentnow
handle78236 running19drivercases /tmp/koh-unwind-game-final.log, addsactualSDK
2048 D/R sourcebreakpoint+framework/rootcallers+functionbreakpoint. No parallel
builds. Added DebuggerHost boundedframes/bank snapshot. Rustprocesswriter changed
to concurrent stdinwrite whilewait_with_output drainsresponses, avoiding small
Windowspipe deadlock for18statebatch; mustrerun editedhelperaftercurrentrun.
Native-unwind.md definesexactPCb-onlycontract/unknownstates/budgets/alternatives.


## Source-debugging acceptance terminal checkpoint

Broad255/255pass438.133s /tmp/koh-unwind-regression.log, exit0; includes157lib,
80SM83,17driver,1SDKgraphics,262intentionallyunselected. Subsequent19/19pass
202.899s addsactual2048SDKdebug. Finaleditedconcurrentprocesshelper andordered
callerasserts11/11pass99.619s /tmp/koh-unwind-symbols-final.log,247unselected.
Actual2048D/DMGandR/CGB sourcebreakpointTitleScene.Enter exactphysicalfile/line,
orderedGame.Run/Program.Main callers, resumedTitleScene.Update method/bank pass.
Nativeb-only records exactentry/IL/callresume states withbankedE+6cookie; unsupported
PCs returnprovenprefix. NoROMABI/frame/instructionchanges; canonicalELF/KDBG only.
Reviewfixed budget gap andunneedednearpath; unknown/corrupt18states and3budget
checks pass. ExistingDebuggerTUnit37/37passzero skips20.717s exit0, buildzero
warnings/errors. Workspacecheck7.56spass(existingcubehelperwarning),separate
rustdoc0casespass,rustfmtpass. Allparenthandles90201,78236,14252,8242terminal,
noactivebuilds. Sourcehashsnapshot /tmp/koh-unwind-sources.json.

Commit09d332d CLIparityfix remainsunpushed untilunwindcommit, pushbothonce.
Source-debugmilestone1 cannowcheckcompleteunderexplicitexact-PCcontract; GUI/
arbitraryPCnotclaimed, finalhostedgate4stillopen. Ledger70/565complete,495
outstanding,nonephysicallyretired. KeepPR55draft/unmerged,no secondPR.
Nextwholegoalunit milestone2 language/runtime/interrupt/bank/capacity contract,
thenalltestclosure+legacydeletion3 andfinalproduct/perf/releases/hosts4.
Fullgoalnotcomplete; donotshrinktosourcedebugorclaimwholeworkspacesuccess.
Mainbattle.pngpreservedsha fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454.

## Interface implementation unit active

Parent owns dirty IR ClassType interface tag/layout ClassLayout.interface and
flattened .interfaces; lower coerces statically valid class/interface values with
checked runtime cast; compile passes cached memberships to cartridge. Runtime
ROM0 descriptor prefix parentu16 +countu16 +interface descriptorptr[count]; heap
object header/fields and argument widths unchanged. Interface refs remain typed
2bytes, no interface instantiation/fields allowed. Existing class cast prefix
remains parentwalk; interface cast scans actualtype's flattened memberships.
Dispatch Binder extends closedworld selectors with implicit/explicitMethodImpl,
inherited maps and reimplementation; mappings must use valid owner/signatures,
nonfinalvirtualdeclaration/virtualbody, cycles/work capped. Default/generic/
value-boxing interfaces remain explicitly unsupported pending contract work.

Test fixtureInterfaceCalls simpleIUnit/static Current currently2D/R cases.
Red15031 failed correct unsupportedinterface layout; firstgreen33656 exposed
canonicalnull Extends is Some(TypeDef),row0, nottableNone. Correctedcanonical
nulltag check. Parent9185 rerun /tmp/koh-interface-green.log live now. No other
builds; do not restart it due observationtimeouts. No commits for thisunit yet.
Reviewers read-only, nofiles/buildownership. Reviewerbinding owns dispatch/CIL
methodmapping semantics; reviewerbackend ownsIR/layout/descriptor/runtimecast.
Fullgoal stillactive milestone2,70/565ledger, nonewclosureclaimfrom2casefixture.
Hosted21ed run43896426313 confirmedlive, managedjobpassed,nativeinprogress;
PRmergeabletrue/draftblocked(expectedprotectedchecks),notmergeconflict.

Interfacecore first10cases D/R passed19.132s including actualManagedHost .NET
execution oracles42 (compiler never executesinput). Reviewrepros reproduced:
ReimplementedExplicit returned37 not42 /tmp/koh-interface-review-red.log;
large200byteframe failedtofinishatPC01C9 /tmp/koh-interface-large-red.log.
Fixed inheritedexplicitmapping precedence at eachancestor and preservedDE around
extendedSPbankrestore in sharedobject_class. AddedTryCastClass/isinst across
IR/verifier/optimize/backend, null/nonmatch semantics. InterfaceLibrary reference
fixtures exercisecrossassembly implicit/explicitMemberRef declarations; 208byte
valuearg/return andint64 results included. Current4D/R×DMG/CGB casespassed
48.022s /tmp/koh-interface-banks.log:20success+2faults percase, eachsuccessful
method .NEToracle42 andnativeROMreturn42/SPD000/guard; faultsCLRtypes/trap4/10;
wholebank2snapshotpreserved onCGB; forceddistinctROMbanks. Metadata assembly
structural InterfaceImpl/MethodImpl row validation newlyadded, notyet compiled.
No buildsactive(otherthanhosted21ed43896426313lastlive); parent81403terminal
logbuthandleunpolled. Need metadata mutation oracles+badinputtests, descriptor/
layout/virtual/game regressions anddocs beforecommit/closure. Ledger70 unchanged.

Interface review continuation: user explicitly requires fixing valid CIL shapes,
not copying CLR restrictions. ECMA II.22.27 permits inherited MethodImpl bodies;
the temporary same-type rejection was removed. Final pending fixtures assert
inherited MethodDef/MemberRef ROM42 independently while recording CLR TypeLoad
rejections; a class-slot alias corroborates CLR/native behavior. Added executable
body/strict-access validation, clearer generic diagnostics and class large-frame
cast regression. EmulatorHost gains test-only native-scroll snapshot FF42/FF43.
Parent regression81282 is live, log /tmp/koh-interface-regression.log,677selected;
binary predates final inherited/strict fixture edits. Do not run concurrent builds.
Next format then final focused interface/IR tests and SDK game/debug regression.
Review ownership unchanged: binding_review read-only dispatch/metadata semantics;
backend_audit read-only IR/layout/descriptors/SM83. Parent owns all edits/builds.
CI-only a6d6bc0 pushed; full validation no longer duplicates runs on master pushes.
Other migration work remains active; no interface commit/ledger closure yet.

Final interface state: parent81282 terminal exit100,676/677 pass1005.408s; only
obsolete unsupported-interface layout assertion failed. Replaced with positive
identity/no-instance-storage checks. Final parent36733 terminal exit0:34/34pass
419.564s /tmp/koh-interface-final.log,620unselected. Includes4interface matrices
(29success+2faults each),2metadata cases (3patched nativebindings each),IR/layout,
existingclassvirtualcases,4SDK2048games,2JRPGgames,2actualSDKsource-debugcallers.
Valid inherited MethodDef/MemberRef bodies execute42 despite optional knownCLR
same-type rejection; class-slot alias mustreturn42onboth. No CLR bug is required
bytest. DirectFF42/FF43snapshot07EE avoids matching wrong intrinsic read/write.
Default declaration + concreteclassbody reproducedunsupported13.038s exit100
/tmp/koh-interface-default-red.log; binder now searchesclassbodies before rejecting
default fallback. Default body7 versusclass42 disambiguates selection. Default
fallback/generics/boxing/staticvirtual remain pending fullmigrationimplementation.
Reviews revalidated ECMA RVARule8; no remaining confirmedbinding/backendblocker.
Ledger71/565complete,494partial,outstanding;zero physicallyretired. Source/asset
snapshot764files /tmp/koh-interface-sources.json unchanged through finalrun.
Workspacecheck17084 terminalexit0 in14.25s (existingcubesbuild_hostwarning),
CSharpiercheck87460 terminalexit0 (4files), rustfmtcheckexit0, workspace rustdoc
exit0 (0cases). Then pushsamePR55/updateconcisechecklist; do notmarkgoalcomplete.

Default-interface unit begins at clean pushed a152743. Primary CI run43913045233
stilllive: Rustinprogress,managedfastpassed. Goalturnpriorprogressconfirmed.
Plan: actualRoslyn/default-body fixtures and negative diamond/reabstract probes;
extend existingBinder MethodImpl validation for interface-owned final bodies and
abstract re-abstraction; bounded most-specific fallback after class binding;
skip implementation bodies as separate interface obligations; preserve typedIR/
bankedABI/backend. Test default-body nestedcalls, helpers, properties, classwins,
inherited/reversed diamonds, referenceassembly defaults, cctor and null behavior.
Parent owns all code/builds; no new parallel implementation. Reviewers may only
read assigned files after parent request. No current language capability retired.

Default basicred98163terminal exit100 /tmp/koh-default-red.log:17/18entries
diagnosedunsupported whilemanagedall42. Basicgreen95681terminalexit0 1/1pass
11.008s afterBinderfallback. New boundeddefault_implementation usescanonical
interfaceMethodImpl candidates+strictsubinterface dominance, classbodies first.
Interface-owned bodies requirefinalvirtualnon-newslot; abstractreabstraction
allowedwithRVA0 onlyforinterfaceowners. Implementation methods aren'tnewslots.
Parent34345live /tmp/koh-default-contract.log12tests:bothnegativeD/Rpassed,
first3of4defaultDMGCGBpassed. NegativeCLRprobes corroborateAmbiguous/EntryPoint
exceptions; Rustdiagnoses both plus4malformedbody shapes. Olddefaultunsupported
fixture movedtosuccess; nonewledgerclosure. Bindingreview ownsread-onlyBinder/
defaultfixturesreview onrequest; nobuilds/editsallowed. Backend unchanged.

Contract34345terminal12/12pass133.783s exit0, thenreviewfoundfinalinterfacebody
tokens wronglytreatedasnewslot. Reproducedbothcallvirt/ldvirtftn red85517 exit100
8.144s /tmp/koh-default-final-token-red.log; managedpatchedDLLboth42whileRust
rejected. Binder.slot acceptsconcretefinalnon-newslotregisteredMethodImplbody,
interface_implementation selectsitexactlybeforeclass/defaultsearch. No lower/
delegateduplicatedroute. Green73722terminal10/10pass93.685s,exit0. Addedexplicit
abstract-bodytoken rejectionprobes,22defaultsuccessentriesinclint64/byref/208byte
structcopy,once-onlycctor/deadunsupportedfloatbody; sourcesformatted.
Currentparent13225live /tmp/koh-default-regression.log503selected expected;
alllib/IR/references+default/interface/classvirtual/sealeddelegate regressions.
No other localbuildactive. Reviewbinding remainsread-only; parentownsallmutations.

Finaldefaultunit:13225terminalexit100,497/503pass323.327s. Sixfailureswere
RoslynCS0649frominternalPacketunusedpaddingfields; publictestpacketcorrected
visibilitywithoutdisablingwarnings. Allsixpassinfinal60993terminalexit0
10/10pass95.941s /tmp/koh-default-final.log. Samefinalselectionpasses22success
entries+null ×4DMGCGB,and2exactfinalbodycall/delegateprobes×4;plus8abstract-token
rejections and12malformed/ambiguous/reabstractcompileprobes. These100ROMprobes
plusgraphsmallcase=101new; previousinterfacegroupnow134ROMprobesaftermoving
default-onlyentryfromrejectedtosupported. Ledger71/565unchanged,494open/partial.
Graphbudget67950terminal1pass1fail(wrongtestexpectedBinderwording); source
classes.rs48–56provesearlier1048576ClassLayoutbudget. Correctedexactboundary
assertion+callerIL;93135terminal2/2pass10.508sexit0 /tmp/koh-default-budget-final.log.
8interfacepositiveexecutes42,1024interfaceinputdiagnoseswithoutpanic.
Workspacecheck24177terminalexit0 in8.78s(existingcubeswarning);rustfmt/rustdoc0
casespass;CSharpier58525terminalexit0(2files). Source/assetsnapshot767files
/tmp/koh-default-sources.json capturedafterlastsourceedit; verifyafterhook.
Previoushead a152743 hostedrun43913045233 completedSUCCESSbothjobs; primarylog
/tmp/koh-interface-hosted-success.log showsworkspaceallfeatures1287/1287pass,
zero skips1610.259s+rustdoc+.NETfast. Predatesdefaultunit; donotclaimnewheadCI.
Alllocalparentsnowterminal. Nextcommit/pushsamePR55/updatebodyandvaulthandoff;
fullmigrationstaysactive. No speculativeframework,newbackend,orABIchangeadded.

Wide-arithmetic batch startsfromcleanpushed63c690b. Audit source
Frontends/CilWideArithmeticTests.cs:14methods/33expandedcases,639uniqueoriginal
resultvectors (17mul32+17div32+19pow2+396shift32+11mul64+11div64+168shift64).
Plan Rust-driven Roslyn Debug/Release DMGCGB orderedheap-result packets, preserving
alloldvectors while adding unsigned/rem/mask/faults; runtimeoperandarrays and
constant-shift helperIL replace privateearly-exit/ABI/phase1arrayworkarounds.
Independent Rust modular/truncating arithmetic oracle; managedHEXcorroboration;
completion PC, SP, bank/frame guards and exactorderedbytes. No source-memory
writes in oldC800/C900stackregion; pinnedmanagedbytearrayresult avoids static ABI.
Parent owns all edits/builds. No newparallelimplementation. Goalstillfullmigration;
71complete/494partial remainsuntilactualbatchproof and disposition closure.

Arithmetic initial73542terminalexit10014.992s /tmp/koh-wide-migration-initial.log
exposedoldRustpolicyremainderMIN%-1=0whiledivisiontraps. PrimaryC#spec12.13.4
requiresremainderexceptioniffdivisionwouldthrow; ECMAIII.3.55allowsthrowing.
MovedMIN%-1fromvalidremaindervectorstofaultgroups, preservingalllegacyvectors.
UpdatednativeIRexpectedfaultred63598exit1003.145s /tmp/koh-wide-remainder-red.log
reproducedsuccess0notfault. RootCode.divide overflowguardnowappliestosigned
remainderaswellasdivision;optimizeralreadytreatsdivision/remainderaseffectful.
Green28442terminal2/2pass46.671s /tmp/koh-wide-remainder-green.log, includesDMG
actualD/R37groups andall36nativeIRboundary/faultvectors. Docoldzero policyfixed.
Current98174live /tmp/koh-wide-migration-regression.log:alllib157,IR312,SM8380,
2drivercolorcases551total. Parentownsalledits/builds; backend_auditmayread-only
review arithmetic/oraclemodule onrequest. Nootherlocalbuilds. Sourcegroups37,
25successful,12faultpermode;2275orderedvalidresultspermode,639originalsubset.

98174terminalexit0:551/551pass328.728s,276intentionallyunselecteddrivercases.
BothcolorcasespassactualRoslynD/R:148ROMexecutions,9100orderedvalues,48faults.
Read-onlybackendreviewconfirmedall639legacyvectors/counts/oracle/rootguard;
no correctnessblockers. All14legacy methodsclosed7PORT+7REPLACE:85/565complete,
480partial/outstanding,zero physicallyretired. Workspacecheck18560exit0 9.09s
(existingcubeswarning);rustfmt/rustdoc0casesexit0. Previous63c690bhostedrun
43919305645completedSUCCESS;notcurrentarithmeticCI. Fullmigrationstillactive.

Generic-method unit in progress from7f6308c. Parent owns all mutations/builds;
backend_audit read-only review owns generic binding/substitution/source mapping
inspection; no builds or edits. New generics.rs per-compilation0xfb instances,
originalMethodDef lowering, actual signatures/locals/typedoperands/transitivecalls.
First red30136exit100 IL0002genericcall; firstgreen97835exit0oneDMGDebugcase.
Expanded23242terminal11/11pass24.867s before referenced-library fixture addition.
Current89484live /tmp/koh-generic-regression.log:lib/IR/SM83,4genericdrivercases,
2widearithmetic and4interfacecases expected. AddedGenericLibrary transitivecalls
andPDB/deterministicfresh-vs-reusedResolverchecks. No otherlocalbuildactive.
Knownremaininggenericshapesconstraints/namedargs/declaringtype/virtual/delegates
explicitlydiagnose; notretiredfeatures, fullgoalstillactive. Plan in docs/superpowers/
plans/2026-11-16-generic-methods.md. Recheck current evidence before claiming final.

89484terminalexit100:564/566pass405.122s; onlytwo failures were oldSM83 generic
rejection expectations. Replaced both with actualROMreturn1; precisegenericclass
diagnostic retained in Structs existing negativecase. History70414terminalexit0
1/1pass6.496s; reverse classcache probe found no bug (existingdescriptorfilterworks).
Final15389terminalexit0 20/20pass65.508s /tmp/koh-generic-final.log:10genericunit,
4driver configurations×7ROMs=28,2PDBmachine-source,2constructormetadata,2SM83calls.
Finalincludesprimitivewidths/native/object,typedarray/byref/default,referenced
genericLibraryPDB,missingreference/deadbodychecks,aggregatebudgetcounters and
fresh/reusedresolverELFparity. Read-onlyreviewtwicefoundno confirmedblockers.
Workspacecheck74323terminalexit0 11.96s (existingcubeswarning),rustdoc22633exit0
0cases,rustfmt/CSharpierpass. Ledger87/565complete,478partial/outstanding,zero
physicallyretired. Nootherlocalbuildactive. Source/assets590 snapshot
/tmp/koh-generic-sources.json needs refresh after final brace-only cleanup before
commit. Fullgoalactive; keepdraftPR55. Currenthead7f CI43924773314Ruststillrunning,
.NETfastSUCCESS; genericunitnotpushedyet. Nextcommit/push/updatePR/vault.

Named generic arguments unit starts from2221beb. Parent owns edits/builds.
First test adds local packed Payload, referenced RemotePayload and Marker aliases
through transitive Library.Forward/Wrap/Make. Current12381 red run
/tmp/koh-generic-named-red.log; wait for actual generic named-type rejection.
Design: scoped type arguments retain metadata assembly identity; substitute each
signature/local/typed operand in its own scope, then bind existing IR types.
Do not invent metadata tokens/aliases: malformed raw tokens must not become valid
because compiler-internal aliases happen to share an out-of-table row number.
Canonical named argument keys use resolved TypeDef+assembly and keep enum identity.
No generic class layout/constraints/virtual support claim in this increment.

Named scoped-binding implementation: Argument{assembly,ty} canonical keys preserve
resolved TypeDef+assembly; MethodTypes binds each parameter/return in its scope.
Lowering locals/byrefs/typedoperands/transitivecalls use same scope. No fake tokens.
First86557terminal14/14pass27.511s includes4D/RDMGCGB genericdrivers withNamedTypes.
Added deliberate equal metadata rows for Payload/RemotePayload and Marker/RemoteMarker,
Rustassertfixturepreconditions and distinct3/12-byte payloads+4/2-byte classfields.
Current92956live /tmp/koh-generic-named-regression.log:lib/IR/SM83 plusgeneric,
interface,value-reference andPDBdriverchecks. Parent owns all edits/builds.
backend_audit read-only owns review of scopedsubstitution/canonicalization and
nongeneric regression risk; no builds or edits. Broader constraints/declaringtype/
virtualgeneric remain unfinished. Ledger87/565 unchanged pending fulltest audit.

Named-unit final evidence:92956terminalEXIT0 575/575pass362.555s (268drivercases
intentionallyunselected), /tmp/koh-generic-named-regression.log. Broadpredates
reviewfix replacingforeignGenericParam scan with indexedcheck_arity and final
fixtureadditions. Final50493terminalEXIT0 23/23pass66.511s coversindexedfix,
13unitcases+4genericdriver+PDB/constructors/SM83calls. Newinterface/delegate
same-rowfixturekinds69556terminalEXIT0 4/4pass27.890s includes32actualD/RDMGCGB
ROMs. Pack1 attributes explicitlyset3/12-bytevalues; root/defaultpackingwould
have4/16bytes, so onlyfinalPack1probesclaim3/12. Enumushort/long andclassint/ushort
remain distinct. Rustasserts actual equalTypeDefrows for allfivepairedkinds.
SDK16865terminalEXIT0 2/2pass172.953s:2048DebugDMGgameplay/graphics andJRPG
ReleaseCGBdialogue/battle/herostate. No trackedassetwrites. Check88295EXIT0
13.59s(existingcubesunusedhelperwarning); rustdoc62402EXIT0 zero cases; rustfmt
andCSharpierpass. Reviewresourcegapfixedusingexistingindex; nootherconfirmed
blockers. Ledger87/565unchanged,478partial/outstanding,zero physicallyretired.
Alllocalhandles terminal. Nextcommit/pushsamePR55/updatebodyandvaulthandoff.
Current2221bebCI43928478533 lastverifiedRustin_progress,.NETfastSUCCESS; recheck
exactrun beforeclaiminghostedoutcome. Fullmigrationstillactive; genericconstraints,
constructedargs/declaringtypes/virtualtargets,floats,IRQ,bankdata,etc remainopen.

Constraint unit starts from0913e97. Parent owns allmutations/builds. Added realC#
struct/class/new/base/interface/typeparam constraints includingtransitiveLibrary
andvalue-typeinterfacecase. Current85166 red /tmp/koh-generic-constraints-red.log;
waitactualblanketrejection. Plan metadata validation ofknown closed arguments;
publicparameterless ctor eligibility, nominal ancestry/interface membership,
System.ValueType/Enum/Object constraint rules; explicitunfinished constructed
constraints/primitiveframeworktraits andnewT runtimeconstruction paths. Do not
pretend new() validation implements Activator.CreateInstance<T>. Add mutated
GenericParam/GenericParamConstraint cases for invalid applications and cycles.

Constraint85166terminalEXIT100 reproducedblanketconstraintrejection. First74462
terminalEXIT0 1/1pass7.339s; mutatedflags/nominalnegative40141EXIT0 1/1pass7.272s.
Validator nowchecks specialflags, constructorownpublicparameterlesseligibility,
nominalbase/interfaceincludingvalueInterfaceImpl, typedclosedparameterconstraints,
knownSystemObject/ValueType/Enum/Array rules andstrictframeworkidentityfornominal
coredefinitions. Addedduplicate/basecount/void/flagdiagnostics. No newTallocation
lowering; no boxing/constrained-call runtime claim. ECMAII9.11allowsrecursive
parameterreferences, so doNOTcallclosedselfconstraints malformed. Current89894
live /tmp/koh-generic-constraints-self.log includesmutatedvalidselfTypeSpecconstraint
ROM42,private/protectedCtor/abstractnew() negatives. Parentownsalledits/builds.
backend_auditread-onlyownsconstraintsemantics/resource/spec review, no edits/builds.
Nextbroadregressionaftercurrentterminal, docs/testmatrixledger87unchanged.

Constraintbroad46544terminalEXIT0 572/572pass335.233s. Read-onlyreview value
InterfaceImpl-to-classgap reproduced88688EXIT100; fixed actualinterfaceflagcheck.
GlobalGPCowner0 ignored reproduced86464EXIT100; sharedAssemblymetadataparser now
validates owner/target ranges, duplicateOwner+Constraint andcontiguousownergroups.
Removed redundantper-methodrawduplicateHashSet. Owner71737EXIT0 1/1pass7.376s.
Current45070live /tmp/koh-generic-constraints-final.log lib/assembly/references plus
all4genericdriverconfigurations/PDB/constructorprobes. Includesfinalprivate/protected/
abstract/virtualCtornegatives andforgedSystemValueTypeassemblyidentitynegative.
Namedpredecessor0913e97 hostedrun43933091863 completedSUCCESS:1318/1318allfeatures
workspacezero skips1398.743s+rustdoc+.NETfast. Primary /tmp/koh-named-hosted-success.log
61525terminalEXIT0. Thesehostedresultspredatetheconstraintunit. Ledger87unchanged.

Constraintfinal45070terminalEXIT0 213/213pass294.935s coversall170library,
assembly/reference suites andgeneric/PDB/constructor drivers. Addedlastnoncontig
owner-rowprobe;53403terminalEXIT0 4/4pass29.294s currentfinalcases. Eachconfiguration
executes11regularROMentries+1validpatchedselfconstraint=48ROMtotal and20negative
metadata mutations=80rejections. ExistingExpandingunsupported/missingrefschecks
remain. Check37489EXIT0 9.12s existingcubeswarning; rustdoc22188EXIT0 0cases;
rustfmt/CSharpierpass. Noall-workspaceclaimforthisdirtyunit. Previous0913e97
hostedSUCCESS1318/1318zero skips1398.743s+rustdoc+.NETfast (43933091863).
Alllocalhandles terminal. DocsGenericConstraints contract coversexplicitremaining
boundariesandfullvisibilityaudit. Ledger87/565unchanged,478partial/outstanding,
zero physicallyretired. Nextsnapshot/commit/pushsamePR55/updatebody/vault;
fullgoalactive. No otherbuildsoragentmutations. Reviewreportednoadditionalconfirmedblockers.

Genericconstructionunitstartsfrom5b044a4. Red22835EXIT100 missingSystemRuntime at
RoslynActivator.CreateInstance<T> inCreate<T>. First68451EXIT0 1/1pass7.638s:
newclassdistinctobjects, implicitzerovalue, explicitstructctor42, objectrootallocation.
Sharedconstruct() lowering nowusedbyordinarynewobj andclosedgenericactivation;
existingIR/ABI unchanged. ExacttrustedActivatorMemberRef+MethodSpecsignatureonly,
canonicalclosedarguments, publicparameterlessselectorreusedfromconstraintmodule,
referenceLibraryconstructors andresourcecharges. Privatevaluectorvariantdiagnoses
ratherthanzero fallback. Current68941live /tmp/koh-generic-construction-regression.log
lib/IR/SM83 +generic/valuector/classvirtualdrivers. AddednewFakeOwner/BadSignature/
InvalidActivation fixtures afterwards; mustrerunfinalgenericcases. Parentowns all
mutations/builds. backend_audit read-onlyownsmetadata/helper/newobjregressionreview;
no edits/builds. Goalstillfullmigration; ledger87unchanged, nofeature retirement.

Construction final: /tmp/koh-generic-construction-final.log reports12/12pass89.200s
including final fake-owner, invalid-signature, private-value-ctor and exactly-once
side-effect probes. Previous broad572/572pass315.603s predates those additions.
Workspace alltargets/allfeatures check EXIT0 9.00s (existing cubes unusedhelper
warning), fmt EXIT0, compiler rustdoc EXIT0 zero cases. Construction contract in
 docs/rust-compiler/generic-construction.md. Ledger87/565 unchanged. Constraints
head5b hosted43939901009 still in_progress when checked; no success claimed.
Next: broader compiler/runtime coverage; do not delete legacy before acceptance.

Generic wide-expression closure: original remaining4 CilGenericTests methods now
mapped to driver::wide_arithmetic::legacy_generic_wide_expressions_match_complete_results.
Three exact arithmetic/extension expressions use independent full64bit expected
packets; equality-only method REPLACE requires each mode match oracle. Two rstest
cases run Debug/Release × DMG/CGB,4ROMs12results; final2/2pass18.073s EXIT0
/tmp/koh-generic-wide-closure.log. Formatting finalEXIT0. Ledger91/565complete,
474partial/outstanding, zero physicallyretired. Compiler production code unchanged.
Next full goal still pending broader runtime/features, all tests and legacy removal.

Nonvirtual generic instance increment starts62d2545. Added InstanceMethods fixture
(class widths64/16, receiver sideeffects and mutating struct receiver returning
packed Payload). Red33549 EXIT100 blanket generic instance rejection. Removed
that blanket guard, retaining explicit unfinished virtual dispatch diagnostic.
Existing assembly reader already validates static/hasthis consistency; no duplicate
validator. First4/4pass32.244s. Final staticflag/virtual negative probes running
5139 /tmp/koh-instance-generic-final2.log after earlier assertion text corrected to
existing reader diagnostic. No test weakening: exactstatic mismatch and virtual
unsupported errors+IL provenance required. Parentownsserialbuilds. Ledger91unchanged.
Final5139terminalEXIT0:4/4pass31.877s. Library29196terminalEXIT0:170/170pass1.416s,
zero skipped. Formatting/CSharpierpass; productiondiff only narrows blanket guard
to virtual methods. Readylogicalcommit/pushPR55. Broader generics/fullgoal remains.

Generic delegate binding startsb6f7f3d. Roslyn ldftn MethodSpec red52226EXIT100.
Target now instantiated via existinggenericcache; callback signature checks and
emission unwrap declaration but keep actual scoped arguments and closedcalltarget.
IR methodhandles allow nonzero0xfb identities (new valid/invalid unitcases).
Second82983EXIT100 found fresh/reusedELF mismatch: oldcallbacks retained reset
instance IDs. reachable now clears callbacks with genericinstances; delegate-type
cache retained because cachedlayouts may reference its identities. Third84746
EXIT0 1/1pass8.277s before removing unnecessarydelegate-typeclear. Currentfinal6444
runs544 lib/IR/generic/delegate cases /tmp/koh-generic-delegate-final.log. Added
late signedushort-to-short MethodSpec mismatch probe; rerun genericfinal afterwards.
Fixture covers samegenericmethod int/ushort targets, referenceDLLtarget, closedclass
receiver sideeffect and foreignpackedPayload argument/return. No newABI. Parentowns
serialbuilds. Ledger91unchanged, fullgoalactive. Do not claim completion.
Final6444EXIT0:544/544pass362.459s lib/IR/delegates/generics (224 intentionally
unselecteddrivers). Late signedness probe first build101 missing boolannotation,
then74250EXIT0 4/4pass32.431s /tmp/koh-generic-delegate-signedness-final.log.
Workspace28817EXIT0 alltargets/allfeatures8.68s existingcubesunusedhelperwarning.
Compiler rustdocEXIT0 zero cases; CSharpiercheckEXIT0. backend_audit read-only
review requested currentdiff; no builds/edits. Pendingcommit/review and PR55update.
Read-onlyreview: no confirmed blockers; retained delegate/layout caches containno
callbackIDs, scoped/raw signaturechecks preserve signedness. No reviewerbuildclaim.
Alllocalhandles terminal; readylogicalcommit. Fullgoal/ledger unchanged.

Nonvirtualldvirtftn unit starts159e809. CorrectCIL nonvirtual instance targets were
rejected by delegate sequence check requiringdispatch0xfe. Red36815EXIT100; root
check now accepts tracked dup/ldvirtftn/newobj fornonvirtual/generic targets while
preserving nointeriorbranches, receiver/signaturechecks/nullbeforeallocation.
Earlier docs intentionally limitedto CLI-verifiable forms; newcontract explicitly
supports correct-but-unverifiable form per ECMAIII4.18 correctness vsIII4.21
verifiability. Notmalformed and no assumptionCLRoracle. Docupdatednoterationale.
Final53391 live /tmp/koh-nonvirtual-ldvirtftn-final.log selected496lib/IR/generic/
sealed/nullcases. Fixture patchesSealedGroup targettoCounterPlainAdd and closed
Identity<int> returning21 ratherthanoverride42; nullpatchedtarget fault4 and
unallocatedheap asserted. Parentownsallbuilds/edits. Ledger91unchanged.
Final53391terminalEXIT0:496/496pass86.152s,272 intentionallyunselected drivers.
Includes all170lib/314IR and12generic/pointer/nullcases acrossD/RDMGCGB.
Read-onlyreview no confirmedblockers; null/static/type/sequenceguards preserved.
Rustfmt/diffchecks EXIT0,CSharpierformatted beforefinalrun. Ledger91unchanged.
Readylogicalcommit/pushsamePR55; nofullworkspace/crossplatform claim.

Staticallyboundgenericvirtual unit startse5ad6b2. Red59348EXIT100 rejects C#
base.Forward<T>() despite directcall. Genericinstantiation nowindependentofdispatch;
unsupportedguard movedto actualcallvirt/ldvirtftn withvirtualgeneric targets.
Directcall/ldftn supportbasecalls/basegroups through existingclosedABI. Fixture
GenericBaseCalls requireswide -5000000001, callback42 andValue2 vs override200.
DynamicGenericVirtualCall/Group retain explicitdiagnostics. First92560EXIT0
4/4pass33.106s acrossD/RDMGCGB. FormattedRust/C#; final22522live selected178
lib/generic/classvirtual cases /tmp/koh-generic-base-final.log. Parentownsall
builds/edits. Ledger91unchanged,fullgoalactive. No architecture/ABI expansion.
Final22522terminalEXIT0:178/178pass64.527s,276 intentionallyunselecteddrivers.
Rustfmt/diffpass; read-onlyreview no confirmedblockers, abstractldftnguard preserved.
No fullworkspace/platformclaim. Readylogicalcommit/pushPR55,ledger91unchanged.

Genericruntimedispatch unit starts5d7b44d. Red35864EXIT100 actualcallvirtgeneric
unsupported. Closeddispatchidentity0xfa wraps existing0xfb instance index, distinct
fromordinary0xfe/0x06; roundtrip/domainunitcases andIRhandlepositive/zero probes.
NoCLI tokenaliases ornewobjectformat. Binder selectsrawgeneric slots/MethodImpl/
default bodies usingexisting rawsignaturematching, then specializesselectedbody
withsameassemblyscoped arguments throughfactored Resolver.specialize (allarity/
constraint/storage/workbudgets retained; instantiateearlyaritycheckbeforeexpansion).
Typeddispatcher signatures reuse method_types; callback/dependencycollection/lower
recognize bothdispatchdomains. SourceMethodsremainoriginalmetadata/PDB identities.
First84289EXIT0 1/1pass8.789s; interface25580EXIT0 1/1pass8.893s. Formermetadata
virtualflagrejection nowpositiveROM42; staticflagmismatch remainsnegative.
Fixture positivesclassvirtualwidths/receiverstate, retainedtargetdelegate, implicit/
explicit/default genericinterfaces andforeignbase/override packedarguments.
Current38096live566selected lib/IR/generic/delegate/defaultinterface/classvirtual
cases /tmp/koh-generic-dispatch-regression.log. Needreview/finalreferencecoverage,
docs/commit/pushsamePR55. Parentownsallmutations/builds. Ledger91unchanged/fullgoal.
Read-onlyreview no confirmedblockers; closurearguments/scopes/dependencies/budgets
preserved. Lateadded GenericNullCall/GenericNullGroup ROMfault4/unallocatedheap
assertions requirefinalgenericrerunafter38096; sourceformatted85313EXIT0. Broader
regressionalreadyfirstgenericcasepasses foreignbase/overridepackedargs. Needcurrent
final4 includes100ROMs acrossfourcases (20regular+2null+virtualflag+spoofowner+self
percase); broadbinarypredatesnullassertions. No priorbroadpassclaimyet.
Final38096terminalEXIT0:566/566pass496.601s,208 intentionallyunselecteddrivers.
Final83924terminalEXIT0:22/22pass214.867s,262unselected; includeslate genericnull
assertions,100ROMs in4genericcases, ordinary/defaultinterface metadata regression.
Workspace10134EXIT0 alltargets/allfeatures10.47s existingcubesunusedhelperwarning.
CSharpiercheck2filesEXIT0, fixturehashsnapshotready. Review no confirmedblockers.
Docsupdate currentruntimegenericcontract andremainingconstructed/boxed/constrained
boundaries. Ledger91unchanged; readylogicalcommit/pushsamePR55 afterdoc/fmt.
Prior5d CI43948277863 stillin_progress lastchecked; do notclaimnewheadgreen.

Constrainedcall unit startsa9f48e9. Red91093EXIT100 CILfe16unsupported inAdvance<T>.
Newconstrained module validatesprefix/callatomiccontrolflow andmetadata type tags;
staticcall/ldftn extensions explicitunsupported, nevermalformedbyassumption.
LowerprefixusesclosedscopedthisType. ReferenceCellObjectdereferencedbeforeordinary
callvirt; StructRef passedunchangedtodirectown/interfacebody, no boxing/copy.
Valuehelper reusesgenericassignable+rawmethodsignaturecomparison, checks ownbody,
explicitMethodImpl flags/signature/owner andimplicitpublicvirtual matches, charges
existinggenericwork. Boxing/default-interfacevalue fallback explicitdiagnostic.
No newIRop/ABI; existing LoadMemory nullmanagedpointerchecks retained. Do not add
blanketnullcheck to valuemethodcall: no dereference unlessbodyusesreceiver.
First30826EXIT0 1/1pass9.696s; 98750EXIT0 186/186pass41.615s lib/generic acrossD/RDMGCGB.
Fixturesproveoriginalstructmutation/copyindependence, classaliases/null, explicit
structmethods and genericinterfacemethods onstruct/class withpackedPayloadreturns.
Lateaddedprefixwrongtype andMethodImplwrongstructbody probes requirecurrentregression.
Current89250live524lib/IR/generic/default+ordinaryinterface/classvirtual cases
/tmp/koh-constrained-regression.log. Newmodule8rstest structural cases. Parentowns
allbuilds/edits. Ledger91unchanged, fullgoalactive. Needreview/docs/finalchecks/commit.
Final89250terminalEXIT0:524/524pass252.077s (182lib/316IR/26drivers),258unselected.
Lateassertions/interfacealias59711EXIT0:4/4pass39.612s;116ROMs acrossfourconfigs,
includingvalue-onlyheap5A unchanged,refinterfacealias43 andnull4. Bothhostilemetadata
probespass. Workspace70691EXIT0 alltargets/allfeatures7.99s existingcubeswarning;
rustdocEXIT0 zero cases; rustfmtEXIT0. Read-onlyreviewno confirmedblockers.
Docconstrained-calls.md definescontract/unsupportedboxing/defaultvalue/staticcases.
Ledger91unchanged. PriorheadCI43951785684 stillin_progress Rusttests, .NETfast
completedSUCCESS; no fullCIgreen claimed. Readylogicalcommit/pushafterhookcheck.

Structreturntestclosure starts2faaec1. Auditedall5CilStructReturnTests methods
(10D/Rexecutions,2sharedTUnitAssertsites/20expandedmarker+verdict assertions,
22fixturevaluecomparisons). NewStructReturns fixture preservesallcomparisons via
39orderedfullI32observations, no truncatedshortSumorfieldmasking. Added byval
Reverseinputmutation, retainedFib0/1/10/11 results, receiverY andenumerationorder.
Fourrstestcases driver::struct_returns execute5entryROMseach (20ROMs,624bytes)
D/RDMGCGB, canonicalELF/nativeLink/PDBsource/bankedhelper/SP/frame/bank2guards.
NativeexistingABI tested; legacyArgScratch/sretcomments notported. Test-onlychange.
First47221EXIT0 4/4pass30.692s /tmp/koh-struct-return-migration.log. Formattedfixture
final28705 /tmp/koh-struct-return-final.log shows4/4pass25.641s, pollhandleEXIT.
Matrixdocs struct-returns.md completePORT5 methods; ledger96/565/469remaining,
zero physicallyretired. Rootroadmapstalegeneric/ledgertext updated. Needreview,
finalfmt/hookchecks, logicalcommit/pushPR55/updatevault. Parentownsallbuilds/edits.
Final28705terminalEXIT0:4/4pass25.641s,284unselecteddrivers. Rustfmt/CSharpier
checksEXIT0. Read-onlyreviewconfirmsall5fixtures/22oldcomparisons preserved,
39fullI32observations,20ROMs624bytes; no weakenedrequirement. No reviewerbuilds.
Notclaimed:constructedgenericstructlayouts/staticarrayenumerator onlymentioned
byoldcomments; sourceactualtests use nongenericBoxreturn andRange4. Matrixexplicit.
Ledger96/565469partial/outstanding zero physicallyretired. Readylogicalcommit/push.

BasicCILclosure starts6147437. Auditedall12CilLoweringTests (25Assertsites,22D/R
executions). NewCilBasics actualRoslynfixtures preservehardware5,7/8,3,ternary9,
flags63,loops9,narrow12,arrays150 and3/7/11 withSCXEE. Nativehost test-only
--native-registers outputsactualSCY/SCX/BGP/WY alongsideheap+guarddata; no emulator
corechange. Addedternaryfalse1,fullI32 arithmetic/narrowpackets, unsignedhighbits,
readonlyarray-elementmutation42; explicitldlenfixturepresence andConsoleErrmember/
ILdiagnostic. Replaces2privateIR-shape tests withactualentry/MMIO+handCILtypedjoin
both7/9 cases; no .edges privateAPIexport. Initialcompile41381EXIT101 privatetestedges
before removedC#IRshapeinspection. Finalfirst76671EXIT0 6/6pass33.078s. FormattedC#
2files3885EXIT0. Current59194live /tmp/koh-cil-basics-final.log selectsall45
lowering+8drivercases (expected53).
Needmatrix12 PORT10/REPLACE2, ledger108/565457remaining, review/finalchecks/commit.
Parentownsserialbuilds/edits. Fullgoalactive; no productioncompilercode changed.
Final59194terminalEXIT0:53/53pass62.067s. AfterdefaultMain discoverystrengthening,
35771terminalEXIT0:6/6pass27.642s /tmp/koh-cil-basics-entry-final.log. Read-only
reviewconfirms12methods22executions25sites47expandedassertions, noweakenedcontracts.
Rustfmt/CSharpierchecksEXIT0 (2C#files). Matrixcil-basics.md complete10PORT2REPLACE;
ledger108/565457remaining zero physicallyretired. No productioncompiler/emulator
changes. Readylogicalcommit/push; fullgoalremainsactive.

Nextunit recursion: audit all6CilRecursionTests, retainFib10=55, mutual10/7 and
Sum500=66 inDebug/Release, actualMMIO and fullresults. No reduceddepthsubstitute.
Parentownsallbuilds/edits; currentCI43963076937 live. Testfirstthenfixrootcause.
Initial1623EXIT100, diagnostic88904EXIT100: Fib/MutualpassedthenDeep500 PC01BB
(__koh_abort01BA). Diagnosis14478EXIT0 logkoh-recursion-banked-layout: Sumtoken
06000004 Debugframe0031=49, calldelta0039=57, I32arg/return. 500frames>WRAM0
3840beforecallstorage. No budgetincreasefix, reduceddepthorfaultsubstituteallowed.
RetainProgram::Deepreproducer;2deepmethodsOUTSTANDING. 4Fib/MutualmethodsPORT
complete;10assertsites6expandedexecutions17expandedassertions. New4rstestcases
8ROMsindependentfullreturns/MMIO, bankedhelpersanduntouchedheap/bank/frameguards.
32199EXIT0 4/4pass22.987s. Final7072EXIT0 8/8pass44.783s includes4existing
bank-restorationregressions /tmp/koh-recursion-final.log. Rustfmt/CSharpierEXIT0.
Ledger112/565453outstandingzero physicallyretired. No productionchanges. Next
rootworkmustreduce retainedrecursive state withsoundalgorithm/storagestrategy;
do notmarkSum500doneorconvertitsrequiredsuccesstoexpectedoverflow. No ignoredtest.
Depthfix starts03f85d0: inspect optimizedSum Debug/ReleaseIR, then select bounded
state reduction with semanticproof. Existing failing500 evidence retained. Parent
owns edits/builds; no newagents. NeedIRnegative/alias/fault tests beforeproduction.
Accumulatorpass implementedinternaloptimize/recursion.rs: onepureinteger selfcall,
normalizedmodularAdd continuation ->localaccumulator+loop. Rejectmemory/addresses/
other/multiplecalls/division/faults; validatecandidateIRbeforemutation. Newentry
initializesoldlocals forIRentryrule; oldentryreinitializeseachiteration. Review
caughtmixedwidthconstanttype-erasure; fixedbyoriginalty==returntyrestriction,
redIRtestplusactualSM83F0=0/F1=-1/F3=-3. Existingreviewerbackend_audit read-only
followup nootherconfirmedblockers. No backend/ABI/emulatorchanges.
Red69245EXIT100bothD/R500. RedIR79363EXIT1006positivefail7exclusionspass.
Initialnative30156stillfailed: verifiercandidateentryinitializationrejected;
reviewred91670EXIT100bothentryinitandconstantcases. Corrected18527EXIT0
19/19pass24.298s inclactual500all4configs. Expanded46523EXIT0 22/22pass34.306s
signed8/U16/I32/I64halves and17IRcases +wideningnative. Added6handSM83cases30ROMs
original/optimized0/17 vsindependentmodularoracle +optimized500, allpassinbroadrun.
Broad38065EXIT100:597pass1fail16notrun (614selected), oldnativeCallsDeeppure
Sum10000optimizedloopnolongertraps andbudgettimesout. Replacedguardfixturewith
nonassociativeF(n)=n-F(n-1):depth10=5anddepth10000stillrequiredstacktrap; bothCLI/
backendretainassertions. Notweakeningrequiredsum500success. PDBoptimizedSum
sectionlines+linkedKDBGassertionsadded. First80061compileEXIT101banki32/u32test
comparisonfixedcheckedconversion. Current74048live /tmp/koh-accumulator-debug-stack-final.log
14cases includesCLI/backendstackguard,32nativewidthROMs,existingPDB/debugger/unwind.
Pendingterminalchecks/fullsuite/rustdoc/fmt, docsfinalresults/commit/push/PR/vault.
Ledger114/565451remaining zero retired; do notclaimfullmigrationdone.
Final74048terminalEXIT0 14/14pass122.102s /tmp/koh-accumulator-debug-stack-final.log:
32width/depthROMsoptimizedSumPDB/ELF/link/KDBG, actualsourcebreakpoint/inspection,
corruptstackrejection andcorrectedCLI/backendguard fixtures. Rustfmt/CSharpier
checksEXIT0 (3C#fixtures). Reviewconstantfixconfirmed nootherblockers. Movedpass
maps/maxID constructionaftereligibility toavoidoverheadonnonrecursivefunctions.
WorkspaceallfeaturesrustdocEXIT0 zerodoctestsin4crates. Current10031live running
cargo nextest run --workspace --all-features --locked -j1 --target-dir /tmp/koh-header-target,
/tmp/koh-accumulator-workspace.log. DO NOTstartanotherbuildinthatdirectory; poll
samehandleandreadfinishedlog. No fullsuitepassclaimpendingactualterminalresult.
Readylogicalcommit/pushcurrentunit, updatePR55/vault; preservefullscopeactive.
Pushed15d0cff; fullworkspace10031stilllive at1373cases, do notstartsameoutputbuild.
Nextunitpreparingtest-onlySoftFloatordinaryreferencedcodebaseline: all3
CilKohRuntimeTests methods (6assertsites5expandedexecutions11expandedassertions).
UseactualAddF32body+6finiteIEEEvectors independentlycomputedfromRustf32; no claim
float/doubleCILsupported orKohRuntime dispatchindeximplemented. Parentownsallfiles/
builds. Newtestsnotpartofalreadycompiled15d0cff fullrun; validateafteritfinishes.
Extendedsameunitwith20SoftFloatDifferentialTests managedlibrary methods:85rows,
30sites145expandedassertions. Newtest-onlySoftFloatHostreflectspublicinteger/
boolmethods andprintsrawresults; eightRustgroupsindependentf32/f64oracles. No
C#assertions/compileralgorithms/framework. Preserveallinputs6predicatecomparisons
andsaturationhelperchecks (notuncheckedCILoutofrangecontract). Expectednewtotal
closure23methods ->137/565428remainingONLYAFTERpassed. CurrentlyUNEXECUTED,
ledger114unchanged. Dirtynewfiles docssoftfloat-bits-tests.md, driver/softfloat_bits.rs,
driver/softfloat_host.rs, fixturesSoftFloatBits+SoftFloatHost, driver.rs modulehooks.
Needformat, run12newcases, fixrootfailures, reviewer/auditclosure/commit/pushPR55.
Do notbuildsharedtargetuntil10031terminal. Current15d0cff fullsuite snapshot
excludesnewtests; reporthead/evidenceaccordingly. NativefloatCIL+runtimekeydispatch
stillnotimplemented; no scopeclaimbeyondordinaryintegerlibrary/manageddifferential.
Full10031nowTERMINAL: deliberatelySIGINTrecordedPID1585669 afterprimaryGitHub
CI43968285185same15d0cffconfirmedRunRusttests live (.NETfastsuccess). Avoidduplicate
fullrunblockingfocuseddevelopment; keephostedbaselinealive, holdnextpushuntilits
completion. LocalWORKSPACE_EXIT100:555pass1SIGINT817notrun /tmp/koh-accumulator-workspace.log;
notafullpassandnotareproduciblecodefailure. Safe/sharedoutputnowfreeforfocusednew
runtime12cases. Mustfixallrealtestfailures andrecordhostedheadscope. No restart
becauseoftimeout; intentionaldeduplicationwithverifiedliveCIcoverage.
Softfloat91455terminalEXIT0:12/12pass53.059s /tmp/koh-softfloat-first.log.
24nativeAddF32ROMs6finitevectors plus8managedhostgroupsall85rows145assertions.
Read-onlyreviewbackend_audit no lostinputs/predicates/signedbits; scopeCLR-vs-ROM
correct. All23methodscomplete22PORT1REPLACE; ledger137/565428remainingzero retired.
62090terminal workspacecheckEXIT0 16.78s existingcubesunusedbuild_hostwarning;
workspacerustdocEXIT0 zerocases4crates. Rustfmt/CSharpierEXIT0 (2C#files).
Currentunitdocs/frontendmatrix/roadmapupdated. ReadylogicalLOCALcommit; HOLDPUSH
untilhosted15d0cff43968285185fullbaselinefinishes toavoidcancelinganotherfullrun.
No productionchanges. NativefloatCIL/runtime-keyindex stillunfinished; don'tclaim
allsoftwarefloatoperationsrunontarget (onlyAddF32native;20methodsmanagedasoriginal).
