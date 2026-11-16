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
