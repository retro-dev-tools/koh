# PR #55 handoff

## Dispatch verification ready for commit

Full1047-case workspace run finished exit100 in1487.093s:1045passed, two stale
string diagnostic assertions failed, zero skipped. Both were the already
identified forged-framework wording check. Production rejects the correct
missing identity. Final selected run283/283 passed43.495s, exit0 (132 filtered):
IR, all virtual/negative/work-bound cases, exact three-type packets with unsigned
byte kinds, and both strengthened string tests. Source hashes match
/tmp/koh-virtual-tested.sha256. Workspace all-feature/all-target check succeeds
7.49s (existing cube support dead-code warning); separate rustdoc succeeds for
four crates/zero cases. All local process handles terminal. Full test log remains
an honest failed original run, not retroactively green. Hosted next-head CI needed.

Exact packet now replaces aggregate-only observations. Legacy virtual static-
receiver method has a complete stronger replacement:48/565 complete,517 partial/
outstanding, zero physically retired. Re-review found no remaining production
blocker after duplicate-override fix. Added docs/ABI traceability and removed
obsolete byte-tag implementation claims from Game/Scene XML (no API change).
Next commit/push this unit, update PR55 checklist and monitor fresh CI.

## Active uncommitted unit — virtual dispatch

Worktree now has native class virtual slot binding, fixed-point reachability,
typed synthetic dispatchers, checked class casts and fault10. Initial banked/IR
run278/278 passed24.479s; review/fix selection281/281 passed35.138s, exit0.
Nine native fixture entries execute across four D/R × DMG/CGB configurations,
separate ROM banks including257+, SVBK2/poison/guards and wide/byref signatures.
Review's duplicate-override bug reproduced both configurations and is fixed,
with final-method/malformed metadata tests. Generated80-class/80-slot work-limit
probe passes with caller/IL diagnostic. Re-review reports no remaining blocker.
The newest fixture also checks explicit covariant MethodImpl rejection; its
final full run is pending. No virtual-unit commit or legacy closure yet.

Full workspace/all-features nextest is LIVE: write_stdin session55690,
/tmp/koh-virtual-workspace.log. Initial full build exposed two exhaustive test
Control matches missing TypeFault in lowering.rs; both were updated, then the
full run restarted only after the first handle finished exit101. Poll55690;
do not start another shared build. All earlier handles terminal.
Pushed419803b hosted native run43787325828 FAILED on all hosts/references job.
Actual logs show only the forged-framework string test's brittle `contains
callvirt` assertion: production now rejects earlier with missing exact assembly
identity. Updated Rust source assertion to require Missing assembly System.Runtime,
public key token and Program::At/IL location. This does not permit compilation.
Current full-run binary predates this TEST-ONLY edit and is expected to show
that stale assertion in both configurations; preserve the run for other checks,
then rerun the two source tests under the new assertion before claiming a fix.
Primary log job133037767310:839passed/1failed before fail-fast cancellation.
Independent isolated Roslyn/CLI probe reproduced the exact rejection without
another shared build: SDK build exit0, native compiler exit1, no object output;
Program::At IL_0002 Missing assembly System.Runtime version10.0.0.0 with altered
public key token. Logs /tmp/koh-forged-framework-{build,cli}.log. PR55 description
now honestly notes419803b CI failure rather than pending. New assertion still
requires its focused rerun after current full binary finishes.
SDK2048v2 now reaches Assets::Load IL_0005 and explicitly fails managed reference
fields inside value structs, exit1 in29.78s. Preserve useful asset capability;
do not force API redesign merely to bypass missing compiler semantics.
Latest pushed commit is still419803b object core; PR55 is draft/unmerged and its
checklist correctly describes committed work.47/565 closed,518 outstanding.
After full results, run separate rustdoc/check as needed, refresh source hashes,
update docs/ABI/test matrix, commit/push logical dispatch increment and PR body.
Before closing the legacy virtual test, strengthen Main with exact per-type
Area/Kind observations (prefer a32-byte raw packet checked by Rust). Current
three-type loop only checks aggregate42, which could miss compensating wrong
selections. Keep current full-run inputs stable; add/check the packet afterward.
Then managed structs and actual scene-game behavior, interfaces/delegates/IRQs/
source debugging, remaining test migration and old compiler deletion remain.

Independent read-only audits during the full run:
- TileAsset/MapAsset are value handles containing array references. Preserve
  inline value-copy semantics plus shared heap references; changing them to
  classes just to evade current layout rejection is not a correctness fix.
  Managed byrefs in ordinary fields must remain distinct/rejected. Explicit
  managed-reference overlap needs its own ECMA rule/diagnostic, not byte-copy
  assumptions. No next-unit production code has been changed.
- clrmeta0.1.0 TableId ends at0x2c; it does not cover Portable PDB tables0x30+.
  Evaluate maintained symbolic-ppdb against needed sequence points, identity
  matching and local scopes before choosing a small isolated compatibility
  layer. Official dotnet/runtime PortablePdb-Metadata.md is the format source.
- Existing DAP StackTraceHandler walks16-bit words toFFFE using current ROM
  bank; native ABI uses dynamic frames endingD000 plus bank cookies. Source
  line lookup/KDBG emission cannot establish stack correctness. Investigate a
  minimal compiler-specific unwind integration, not a debugger rewrite/DWARF.
  Existing Cartridge.CurrentRomBank is byte-valued, so ninth-bank-bit debug
  handling also requires explicit validation. These are outstanding gates.

## Current checkpoint — object core pushed

Pushed419803be92e0f4c579d8c4cd9d917955749cebf0; remote branch and PR55 head,
master base, open/draft/unmerged state verified. Live checklist updated.
Native object core passes376/376 broad regressions and277/277 final focused
checks after formatting/final class-shape validation; workspace check and
separate rustdoc succeed. Eleven tested source hashes still match after the
commit hook (/tmp/koh-object-tested.sha256). All local process handles terminal.
Sixty ObjectCore ROM probes cover15 entries in Debug/Release × DMG/CGB.
Test dispositions47/565 complete,518 outstanding, zero physically retired.

Ordinary2048v2 SDK build now advances to Game::Run IL_000c and fails explicitly
on pending virtual dispatch, exit1,29.55s. It remains a required failing sample.
Review found raw/ref-array fabrication, static-cell aliases and class identity
shape gaps; fixes/negative cases are included, re-review has no remaining blocker.
New native CI43787325828 and managed CI43787325868 confirmed in progress.
Priorb4caf55 native CI completed1012Linux/macOS,1010Windows, zero workspace
skips plus rustdoc/artifact/RGBDS; those results are not new-head acceptance.

Next: virtual dispatch with exact CIL slot semantics, including newslot/override,
base calls, null checks, constructor-time dispatch, inherited implementations
and closed-world reachable instantiated types. Then interfaces/delegates and
actual2048v2/JRPG behavior. Keep public APIs open to critical redesign; preserve
useful product capabilities and update consumers/tests/docs together.
PDB/KDBG source+stack proof, IRQs/reentrancy/banked data, all test migration,
old compiler deletion, performance and final cross-platform/release gates remain.
Do not merge. Preserve unrelated main-checkout battle.png edits.

## Earlier checkpoints (historical)

### Uncommitted virtual dispatch unit

New dispatch.rs Binder selects ECMA-335 nongeneric class virtual slots with
newslot/reuse, inherited implementation and final/strict checks. Reachability
now computes a bounded fixed point of allocated classes, virtual sites and
selected override bodies, then emits typed synthetic FE dispatcher functions
using existing Call/banked ABI. Explicit MethodImpl/interface mappings still
diagnose; generic virtual shapes are not claimed. Raw unsupported dead override
bodies are not included. IR adds exact IsClass, checked CastClass and TypeFault;
descriptors retain parent links, cast walk bounded65 (64 classes plus Object).
Fault10 identifies invalid runtime type; CIL castclass for reference classes
is wired. Synthetic calls retain originating call-site IL diagnostics.

Initial16 ROM probes passed4/4 in17.475s; expanded32 probes passed4/4 in18.967s.
Bank-constrained cases force every managed function/dispatcher to a separate
ROM bank, including257+, preserve SVBK2 and bank2 poison, and add int64/byte/
byref parameters. IR+banked run278/278 in24.479s, exit0,134 filtered
(/tmp/koh-virtual-banked.log). This predates the latest signature prefilter and
three-type loop/dead generic-overload fixture changes; rerun before claiming
latest bytes. All local build/test handles terminal.

Ordinary2048v2 SDK now advances through Game::Run to Assets::Load IL_0005 and
fails explicitly on managed-reference fields inside a value struct, exit1 in
29.78s (/tmp/koh-virtual-2048-sdk.log). This exposes the next compiler gap, not
game acceptance or a reason to remove/redesign useful asset APIs mechanically.
Need finish negative metadata/provenance tests, resource bounds and review.
No virtual unit commit yet;47/565 test closure unchanged. Full goal remains.

PUSHED b4caf551a1428481c547fc0bdde5a626196b7307 class-layoutfoundation.
Remote/PR55head/masterdraftverified; checklistkeepsclassgamesunchecked.
All8testedsource/fixturehashesmatchposthook /tmp/koh-class-tested.sha256.
Alllocalbuilds/tests terminal. NativeCI43781354384 confirmedinprogress.
ManagedCI43781354492 completedfailure; freshLinuxjob133019035977 logsconfirm
2048v2/JRPGstillfailAggregate...ValueType atMainIL0000. Notfixed/retired.
Previousphase d3f2d2a nativeCIgreenallhosts:987Linux/macOS985Windows,0workspace
skips+rustdoc/artifact/RGBDS; actualcompletedjoblogsread. Notnewheadproof.
Next: wirevalidatedClassLayout into typedobjectIR, allocation, constructors,
fieldaccess andcalls, withactualC#DLL→ELF→ROM→existingemulator tests. Distinguish
referenceidentity fromvaluecopy/nativeintegers. ExistingType::Array(Storage),
Storage::{Scalar,Struct} areCopy; intentionallyextendtypedreferencemodel for
class/string/array fields ratherthanerasingallrefs intoNative. Valueconstructor
per-ILtemporaryreuse mustNOTapplytonewobjclassidentity. ClassLayoutfixtureMain
alreadyproduces42onCLR withdefault/freshidentity/shadowedfields/crossassembly
aliases; itisnotyetnativeacceptance. Scenevirtual/interface/delegatesnextafter
coreobjects; sourcePDB/IRQ/testclosure/compilerdeletion remainrequired. Fullgoal
active, no merge. Onlythispost-pushhandoffrefreshuncommitted.

CLASS CHECKS COMPLETE: session79068 terminalexit0 /tmp/koh-class-workspace-check.log
workspace/allfeatures/alltargets46.58s; onepreexistingcubehelperdeadcodewarning.
Separate /tmp/koh-class-doctests.logexit0,4crates0doctests. Alllocalbuildsterminal.
Final8source/fixturehashesrecorded /tmp/koh-class-tested.sha256. Commit/pushnow;
no falseclasssupportclaim. Followwithtypedobjectreferences/allocation/fieldcalls.

CLASS FINAL REGRESSION: session54167 terminal exit0; /tmp/koh-class-metadata-
regression.log58/58passed241.459s,zeroskips. 48layout(25newclass)+10references.
Reviewerfound2importantdefects: cachedparentsbypassdepth; deepTypeSigcopies
bypassbudget. Reproduced /tmp/koh-class-review-red.log1pass/3fail9.302s;
fixedcumulativedepth, sharedArc<Field>, preparseblobbytecharge. Focusedgreen
5/5 14.719s /tmp/koh-class-review-green.log. Re-reviewno furtherblockers.
Priorcrossrun44/45failedonlywrongtestwordreference; actualMissingassemblyClassBase
diagnostic nowassertsexactidentity/versionandmissingbasecacheguardpasses.
Currentworkspacealltargets/allfeatureschecksession79068 /tmp/koh-class-workspace-
check.log stilllive; waitbeforeanyotherbuild. Source/fixturesfinalformatted.
ClassAPI is metadatafoundation ONLY, notwiredtoobjectIR/runtimeyet. Gameclasses
stillfail; no565-methodclosure. Commitasoneincrementthencontinueclassruntime.

ACTIVE class metadata/layout foundation, base pushedd3f2d2a. Rootowns edits and
serialbuilds; reviewerread-only/no builds. Newlayout::class/ClassLayout separate
fromunmanagedvalue layouts, packedAutoLayout/header2/baseprefix/fieldidentities,
2B refs,structpayloads,metadatawidths; ResolverArc cache+globalworkbudget1m.
NotyetwiredtoobjectIR/allocation/dispatch; nativeclasses and2048v2/JRPG STILLFAIL.
Do notclaimclass executionorcloselegacyclassdispositions. docclass-layout.md
recordsrequirements/ABI rationale/currentlimits/requiredfollowingwork.
Tests ClassLayouts D/R+CLR42probe, inheritance/shadowing/referencefields,
selfrefs,abstractbase,generic/layout/capacitydiagnostics;5malformedmetadata cases.
Initialfunctionmissing red /tmp/koh-class-layout-red.log; initial11/12 passed
withvalue-typeclassification diagnosticfailure; fixedbeforefull41/41green
195.938s /tmp/koh-class-layout-final.log. Stress3000siblings×400longfields
hitsbudget safely andcachedRootstillvalid. No globalfieldrangefixneeded:
existingreaderrejectedmalformedranges; didnotaddredundantvalidation.
Expandedcrossassemblybase/enumfieldandmissingref,numeric/arrayfieldcases now
running session38481 /tmp/koh-class-layout-cross-final.log (45layoutcases).
Waituntilterminalbeforeanyotherbuild. Fixturesmustnoteditwhiletestsreadthem.
Review correctness: recursion/cache/resourcebounds, referencevsinline fields,
packedinheritance/headerABI, missingrefs, unsupportedgeneric/layoutdiagnostics,
identityassemblypreservation andno falseclaimofnativeclasssupport.

PUSHED phase d3f2d2a56be99c69facecfae3796f1bb9df0b6e0; remote/PRheadverified,
single draftPR55/master checklist marksphasecomplete. Post-hook9source/fixture
hashes match /tmp/koh-phase-tested.sha256; finaldouble-rendererchangecommentonly.
Alllocalbuilds/tests terminal. NativeCI43777137980 and managedCI43777137954
confirmedinprogress; no greenclaim. Onlythispost-pushHANDOFF refreshuncommitted.
Nextmajorunit: reference-class semantics foractual2048v2/JRPG, rootdiagnostic
Aggregate must be a sealed framework System.ValueType. LatestmanagedrasterCI
logs directlyconfirmbothsamplesfailatMain IL0000; notretiredoroptional.
Inspect actual class layout/calls/Framework usage and design fromECMA/hardware
requirements, nevercurrentAPIconventions. PDB/KDBGsource/stack,IRQ/bankeddata,
testclosure,legacycompilerdeletion,performance/release/finalhosts remainrequired.
Fullgoalstillactive; no merge;45/565complete520outstandingunchanged.

PHASE FINAL: session6273 terminal exit0, /tmp/koh-cube-phases-final.log76/76
passed123.815s zero skips. All12nativeROMs +2CLRallphase/config +24negative
phasecases + existingimage/GDMA/rasterchecks. Reviewerphase_review found no
blockingdefect/lostassertions; no reviewerbuilds/edits. Session20350 terminal
exit0, /tmp/koh-phase-screenshot.log existingheadlessApp ranSDKracingROM500frames,
0mode3writes. PNG /tmp/koh-cube-sweep-capture.png160x144opaquegray4colors,
1269litpixels withinracingviewport; visualinspection shadedcube. No assets
modified. All localbuilds terminal. Solution/filter36projectsconsistent.
Finalhash/format/commit/push andPRchecklist next. No prodcompileralgorithm/API
changes inthisunit. Fullmigrationremainsactive; matrix45/565unchanged.

ACTIVE phase-sweep migration, base pushed4341278. Root owns edits and serial
builds; reviewer read-only, no builds/edits. Rust cubes.rs now owns CLR allphase
geometry assertions:768scenarios/config,24negativeframecases. New CubePhases
fixture emits actualsharedrenderer pixels withoutassertions. Red26cases24pass/
2missingfixture /tmp/koh-phase-red.log; green26/26 4.227s /tmp/koh-phase-green.log.
Deleted redundant samples/csharp/gb-3d/verify project+3sources+ignore; removed
Koh.slnx/Koh.Ci.slnf entries. All36 CI filter projects exist and belong tosolution.
Updated docs/consumercomments; onlyremainingdeleted-path references areaudit
history. ExistingemulatorCLI preservesPNGcapture; documentedcommand pendingrun.
Current serial session6273 /tmp/koh-cube-phases-final.log runsall76cube cases,
including12actualnativeROMs. Do not start another build untilterminal.
Review scope: behavioralcoverage vs removed verifier, CLR/native boundary,
fixture math/naivedither, records/counts, edge/area rules and consumerdeletion.
No production compileralgorithm/API changes. Fullmigrationnotcomplete.

PUSHED raster43412789d0d8030000d1a5e25222dc4cb8c58934, remote and draftPR55
head/base verified. PRbody/checklist updated;45/565 complete remains honest.
Post-hook all17 testedcode/fixture hashes match /tmp/koh-raster-tested.sha256.
All local sessions terminal. NativeCI43776124627 and managedCI43776124629
confirmed inprogress; do not infer results. Previous3ce nativeCI green allhosts
924Linux/macOS922Windows; exactlogs read. ManagedCI3ce fails referenceclasses.
Next: migrate standalone 256-phase/3viewport checks to Rust-owned assertions,
then close/delete redundant compiler-owned C# cube verifier after caller audit.
Do not delete Koh.Verify production component. Whole migration stillactive;
classes2048v2/JRPG,PDB/source,KDBGstack,IRQ/bankeddata,testclosure,legacycompiler
deletion, performance/release/finalcrossplatform remainrequired. No merge.
Only this post-push handoff refresh is uncommitted. Main battle.png hash unchanged.

RACING SDK: session76828 terminal exit0, /tmp/koh-raster-sdk.log 0warnings/errors,
38.41s,131072byteROM. Direct header/global checksum and CGBflag checks pass.
SDK emits obj/Release/net10.0/CubeRacingBeam.o and optional KDBG (still IL-only).
All local builds terminal. Commit/push current complete raster unit next, then
PRchecklist update; do not claim full migration/phase-sweep/source-debug complete.

RASTER FINAL: session97729 terminal exit0, /tmp/koh-raster-regression.log
380/380passed415.625s,126unrelateddriverfiltered. Includes all12realcubes,
allIR/backend and4hardwareboundarycases. Read-only raster_review no blocking
defect; no reviewer builds/edits. CSharpier ran before final regression. Tested
code hashes in /tmp/koh-raster-tested.sha256. Root now owns serial SDK build
session76828 /tmp/koh-raster-sdk.log; no second build until terminal. Commit,
push and updatePR55 after SDK result. Phase-sweep and whole migration remainopen.

ACTIVE raster unit; root owns all edits and serial builds. Reviewer may inspect
only, no edits/builds. Base HEAD3ce00aa; current diff adds hblankstream metadata,
typed IR/lowering/remap, native bounded stream, hardware fault9 and public
Ppu.StreamScrollX. Racing sample precomputes table and uses native stream.
Real four-way raster acceptance green4/4 in63.580s; boundary/IR10/10 in30.162s;
backend/validators35/35 in8.023s. Logs /tmp/koh-racing-raster-green.log,
/tmp/koh-hblank-boundaries.log, /tmp/koh-hblank-backend.log. Final expanded
IR/remap/register tests + all sm83/IR/cubes and driver boundaries running in
session97729 /tmp/koh-raster-regression.log; do not start another build until
terminal. CSharpier formatted six owned C# files before final run. No commit
yet. Review timing, count0/byte narrowing, long-offset setup, range/LCD guards,
IME contract, optimizer effects and actual trace assertions. Full goal persists.

Racing-beam acceptance in progress; three dirty files owned by root.
Added physical scanline/SCX traces in CubeHost and four real racing ROM cases.
Completed /tmp/koh-racing-raster-red.log: exit100, 0/4 passed. Geometry and
animation pass before raster failure; writes occur during drawing or VBlank.
No production timing fix yet; do not weaken raster assertions or claim support.
Added nine adversarial raster-validator cases and expanded image-validator
coverage to the racing viewport. Completed /tmp/koh-raster-checker.log: exit0,
27/27 passed, 23 unrelated filtered, 0.199s. Session3588 terminal; no local build
live. cargo fmt changed only the owned cubes.rs file. Next: choose a bounded
hardware timing contract and implement/test native raster streaming; existing
public API remains evidence, never the design baseline. Preserve full goal.

ACTIVE full migration; byte-copy unit pushed3ce00aa7a944e03a3ea94a694ae0abd454c79dbe.
Remote verified; draftPR55checklistupdated45/565complete520outstanding.
Post-hook hashesmatchall14testedcode/fixturefiles. No localbuilds/tests live.
NativeCI43771777807 andmanagedCI43771777666 confirmedinprogress; no greenclaim.
Next: racing-beam native structural +SCX/HBlank effect validation and256phase
sweep; inspectactualSurface.Present/Ppu.WaitForHBlank beforechoosingAPI/codefix.
Classes2048v2/JRPG,PDB/KDBGsource,IRQ/bankeddata,testclosure,legacydeletion and
hostperformance/release/crossplatform gates remainopen. Do notmerge orshrinkgoal.
Onlythispost-pushhandoffrefreshuncommitted. Rootownsserialbuilds/code.
Initial ordinaryCILcopy /tmp/koh-copy-cost-red.log8/8failedbudget: bulk3840
Debug6235252T Release5735156T; exactmemoryassertions passedbeforeratefailure.
Newaddresslessmemcopy contract void(byte*,byte*,ushort); typedMemoryCopy Op,
effectful3operands/remap, knownSVBKdestination spans, SM83DEdestHLsrcBCcount
forwardbyte56T loop,4Btemporaryguard. OrdinaryCLR body simplebytewalk.
/tmp/koh-copy-cost-green.log8/8passed50.685s. /tmp/koh-copy-backend-cubes.log
17/17passed128.775s: all8realcubeD/RDMG/CGB nowgreen unchangedbudgets,5typedIR,
4backendwidth/largeframe/stackcases. /tmp/koh-copy-contracts.log31/31passed
116.418s: bulk217748T allmodes/alignments; smallmatrix94036T; wrapsource/dest,
zero bus accesses, aliases, invalidmetadata5shapes, pinnedSVBKguards, desktop.
Added original7byteoverlap, combinedalloc/copy/fill and64/256/1920ratecases.
Cube test nowroundtripscanonicalELF beforelinking. Finalformatted code and
fixtures fullworkspace/allfeatures/no-fail-fast924/924passed1638.039s,exit0,
zeroskips/retries /tmp/koh-copy-workspace-nextest.log session12214terminal.
Doctests separateall4crates0cases succeeded /tmp/koh-copy-doctests.log.
FinalrawBank2probe fixturestrengthening +allcontractcases4/4passed21.380s,
/tmp/koh-copy-final-banks.log session81905terminal;56ROMprobes, rates
64/256/1920=9040/19824/113008T, marginal56T/byte. Productioncodeunchanged.
Reviewmemory_copy_review no blockingdefect; no reviewer builds/edits.
No localbuildlive. Matrix45/565complete520outstandingzero physicallyretired.
Commit3ce00aapushed; PRchecklistupdated; no legacycompilerfallbackadded.
Do notclaimhostcompilerperformance fromtargetCPUcosts; legacyC#compilermustdelete.

Previous pin-scope checkpoint: pushed5c1c12e099b417fe69ac14a87c2f0b1c3eeb46db,
remote verified; draftPR55updated withf86dfd7GDMA and5c1c12epinscopeincrements.
NativeCI43764804111 andmanagedCI43764804116 confirmedinprogress; no greenclaim.
Next: full-frameDMGDebug still450/750identical; profileMem.Copy6232152T and
framechanges83,437. Diagnosecopycost andpreservebudgets; racing-beamneedsnative
effectvalidation. Fullclasses/PDB/IRQ/testclosure/compilerdeletion remainopen.
No localbuild/test live. Onlythispost-pushhandoffrefreshuncommitted.
Post-hook hashes of all9 tested code/fixture files unchanged. No hidden fallback.
Root owns edits/serialbuilds. Red /tmp/koh-pin-scope-red.log2pass/8fail19.812s;
4OamDma and4validbefore/after rejected. Expanded12IRconversion/direct/callee
cases red4pass/8fail /tmp/koh-pin-address-red.log. Changedpins.rs to possible
live localsets, joinsunion, zeroStorePinnedkill, transitivecalls checkedatcall.
Focused /tmp/koh-pin-scope-green.log20/20 passed46.505s exit0, all4originalOAM
and4memoryfill bank cases +12IR scopes. Added4CFG join/backedge cases.
Session64791 terminal: /tmp/koh-pin-scopes-regression.log241/241passed82.006s
exit0,98unrelatedfiltered; fullIR +14OAM/fixed/memoryfillbank cases.
Reviewpin_scope_review found no blocker; requested overlappinglocals coverage.
Added2cases first-release rejects/final-release allows. FinalfullIR229/229,
1.610s exit0 /tmp/koh-pin-ir-final.log session13532terminal. No localbuildslive.
No testsweakened or diagnosticchecksremoved. Commit5c1c12epushed; PRupdated.

ACTIVE full-frame unit, after pushed e9a6be0 (remote verified).
Root owns edits/serial builds; reviewer owns read-only inspection, no builds.
Native CGB GDMA start trace revealed full-frame starts at LY0dot410 Release /
LY1dot132 Debug, despite passing CPU-only mode3 guard and cube images.
Current diff prepares registers/control before public Cgb.CopyToVram's LCD-on
VBlank wait, deletes duplicate internal helper, updates TileSet + full-frame calls.
Focused /tmp/koh-fullframe-safe-profile.log2pass/1fail25.294s exit100:
bothCGB safe now; DMGDebug still no animation at450/750. No budgets waived.
/tmp/koh-fullframe-cadence.log records framebuffer changes at83,437 only.
Profile Mem.Copy6232152T tofirst450; remaining render also expensive.
Rust cube target now8real D/R×DMG/CGB×2variants +12viewport validator cases;
new9GDMA boundary cases need run. Review general API timing/maxcount as well.
Single-transfer2048 phase test exposed DebugLY146dot248 and ReleaseLY145dot292,
both red /tmp/koh-gdma-max-red.log2/2failed26.754s. PublicAPI nowLCDon1024
chunks; setup/controlbeforeeachwait, LCDoffhardwaremax unchanged. Focused15/15
passed53.813s /tmp/koh-gdma-chunks-green.log; newboundarycases expand4D/R×DMG/CGB
with8maxphases plusZero/16/1024/1040/1920/Off2048. Reviewgdma_review no blockers.
Serial session79233 terminal: /tmp/koh-gdma-regression.log44passed/1failed,
239.891s exit100; solefullframeDMGDebuganimation. Newdistinct3C/C3sourcehalves
verifyadvancingbothaddresses: final4cases passed29.211s,exit0,108filtered,
/tmp/koh-gdma-patterns-final.log (56actualROMprobes). Allhandles terminal.
Rootownsbuilds. Commit/push andupdatePR55next; thenpinned-bankrootfix.
Nextcompilerrootfix: hosted e9 nativeCI43762030785 fails all3hosts onOamDma
Main explicitbank2beforepin. Currentverify_banks rejects wholemethods rather
than onlybankwrites/calls while a pinisactive. Do notweakenfixture. ManagedCI
43762031131 failsknownreferenceclasses2048v2/JRPG. Exacthostlogsreadbyroot.

ACTIVE VBlank unit: root owns edits/serial builds; review owns read-only
inspection, no builds/edits. Ppu.WaitVBlank now >=144 leave / <144 enter.
Old Debug phase3 took137692T, missing firstwindow (90k budget); Release67k.
Red /tmp/koh-vblank-phase-red.log2/4fail; new32phase+4doublewait cases green
/tmp/koh-vblank-map-green.log5/5 (includesCGBDebugshowcase),44.393s.
Initial rangefixmadeallcube10/10 green50.773s /tmp/koh-cube-vblank-window.log,
but broad28 hadoneCGBDebugshowcase regression5mode3writes. Traced toFlushRun:
checkLY152→store948dotslateratLY0 (/tmp/koh-vblank-map-margin-red.log).
Fix source/destination preparationbeforeguard, byteLYcomparisons, stopbefore152.
AddedShowcaseHost bank/PC/line/clock failure diagnostics. Rejected global
read-to-write metric: it included unrelated writes and zero DMG observations;
removing it does not weaken the actual mode3, pixel or frame assertions.
Read-only vblank_map_review found no blocking issue in the final diff.
Final serial /tmp/koh-vblank-final-green.log completed28/28,161.668s,exit0;
90 unrelated tests filtered. Includes all10 cube cases,4 showcase,4 tile,
2fullmap,4lazy and4VBlank (32phases+4doublewaits). Session40973 terminal.
Post-hook /tmp/koh-vblank-postcommit.log8/8 passed87.227s,exit0,
100 unrelated cases filtered. Includes all4 showcase + all4 VBlank cases.
Session11197 terminal; no local build/test live. Push remains next.
Do not waive clocks, mode3 checks or framebounds.

Latest pushed77c0fc3c3ae80ce76be97445dc3170b161d61b76 (remote verified),
with411f3ed profiling and77c0fc3 nativefill. DraftPR55 checklist updated34/565.
New native CI43757920270 and managed CI43757920724 were verified live after
push; no current-head hosted outcome claimed. All local process handles terminal.
Only this post-push handoff refresh is uncommitted; all code/tests/docs pushed.

Current unit: native MemoryFill, following profiling commit411f3ed. Root owns
edits and serial builds. Base for this unit is pushed2ebfc4c. Existing API shapes
are evidence, not conventions. No local test/build live at this update.

CGB first200 profile: Mem.Fill8733956T, FillSpan6646432T
(/tmp/koh-cube-profile-first.log). Byte-loop fill ROM1434496Debug/1276400Release;
word-loop attempt814812Debug/722476Release stillfailed350000T budget.
New metadata memfill void(byte*,byte,ushort) -> effectful IR -> native52T/byte,
no source loop/compiler fallback. Actual testROM91492T allalignments/modes/configs.
Four temporary stack bytes, low8/low16 formals, ascending16bit wrapping writes.
Desktop body is ordinary byte C#, safe unaligned. Legacy C# compiler rejects
new metadata; explicitly accepted because it must be deleted and SDK is Rust-only.

Focused fill20/20 passed68.415s /tmp/koh-fill-all-contracts.log; additional
wrap4/4 passed34.553s /tmp/koh-fill-wrap.log. Covers exactfullpoisonbanks,
zero/tails/255/256/257/300, four alignments, DFFF guard, fivebadmetadata shapes,
desktop112fills, sixIRcases and fourbackendlargeoffset/truncation/stack cases.
Executable wrap exactlyFFFE:A7,FFFF:A7,0000:A7,0001:A7; no extra0002.
Pin red /tmp/koh-fill-pinned-red.log acceptedSVBKFF6F+2. Fixed using known
constant/conversion graph and modulo16bit spancheck, including transitivecalls.
First literal-only fix failed because pointer was OpConvert; corrected20green.
Dynamic rawpointer capacity/bank safety remains caller-owned, same as raw
StoreMemory. Review memory_fill_review verified graphlinear, masks andstack;
no remainingCritical/Important issues, no reviewer builds/edits. Instance/generic/
calling-convention metadata mutations are not claimed as new coverage.

Broader /tmp/koh-fill-graphics-regression.log completed308passed/1failed,
466.164s,exit100;82 unrelateddrivercasesfiltered. All207IR,70backend,22selected
drivercasespass. Cube has9passes/1fail: sixvalidatorcases, threegamecasespass.
CGB Debug/Release and DMGRelease now pass both structuralcaptures. DMGDebug
first1100 remainsblank; late2000capture / VRAMsafe checksremainrequired.
Cube /tmp/koh-cube-native-fill.log9/10,59.485s,exit100. No assertion/budgetrelaxation.

Final post-hook fill24/24 passed125.887s,exit0:
/tmp/koh-fill-final-posthook.log. Includes new local-memory aliases and4 IR
conversion/sign/zero/pointer-truncation cases;361 unrelated casesfiltered.
ActualSDKgraphics dotnetbuild/run passed87.842s; post-hookproductionselection
30/31 passed273.776s,exit100 /tmp/koh-fill-postcommit-sdk.log, soleDMGDebugcube.
Nativeproductioncodeunchanged since thatSDKcheck; later additions are tests/docs.
Push logicalincrements and update draft PR55 checklist34/565complete,
531partial/outstanding (0deleted).
Then test/fix Ppu.WaitVBlank: currentcodepollsonlyLY==144 (one456-dotline);
profileDMGDebug49518400T inWait, firstcapturePCinWait. Test nextblank latency /
fullVBlankwindow, notlargerframebudgets. Source start/stopandGDMA timingmust
remain safe; existingemulator/core/debuggernotrewritten. Fullframe/racingbeam
need actualnative structuralacceptance afterdoublebuffer is fullygreen.

The full native Rust CIL-to-SM83 migration remains ACTIVE in one draft PR55
against master. Never merge, split PRs, shrink acceptance or declare completion
for an increment. Existing public APIs are evidence, not a baseline/convention.
Full objective: use get_goal if needed; the user request's acceptance is intact.

Worktree: /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Main worktree has unrelated battle.png edits: preserve SHA256
fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454.
Base HEAD2ebfc4c63b4bc1a08a2866a49519d4906dfe071c was remote-verified.
Increments7f7b8a7 small-magnitude multiply and063c695 shared canvas/tile transfers.
Test commit2ebfc4c exposes stronger actual Cube tests
and serial nextest configuration in the same PR. Root owns edits and builds.
Read-only multiply_transfer_review found no math/transfer defect; its concurrent
cargo-test build concern is fixed by a local Roslyn build lock. No reviewer
builds, edits or active subagents. No local build/test live at this update.

## Verification and open cube failure

- Division b77361e: backend50/50, post-hook focused9/9; I32 30000/31
  51268T→4532T, I64 now6212T. docs/rust-compiler/word-division.md.
- Multiply red -30*-127: I32 47080T, I64 185896T, both fail20000T budget.
  /tmp/koh-multiply-cost-red.log. Shared-helper green I32 3828T/I64 6260T;
  /tmp/koh-multiply-shared.log passed9/9 in27.012s.
- Initial inline multiply made actual Cube.Transform exceed16KiB, explicit
  diagnostic /tmp/koh-cube-fast-math.log. Per-function/width helper fixes it.
  Operands scratch0/8, result16, sign24; helper offsets+2 for CALL, entry
  temporary guard4 for return+scratchpush. Full modular widths preserved.
- Complete backend66/66 + real tile transfer4/4 passed70/70 in221.574s,
  exit0 /tmp/koh-multiply-transfer-regression.log;90 unrelated cases filtered.
  Covers dispatch255/256, MIN/high bits, wide fallback, large frames,
  forty chained products fitting a bank, stack exhaustion, prior arithmetic.
- Canvas now shares TileSet.CopyToVram. CPU value/destination and CGB registers
  prepared before WaitVBlank; complete tile sizes; LCD-on GDMA bound1024.
  Previous DMG red806Release/826Debug mode3 writes. Old weaker4 animation/VRAM
  probes now pass49.586s /tmp/koh-cube-shared-transfer.log.
- Stronger cubes.rs checks BOTH early/late captures: palette/alpha/dimensions,
  centered96x80 viewport, multiple face shades, size/center, one8-connected
  silhouette, animation, real boot/frame limits and zero mode3 writes.
  Six synthetic validator cases pass. Real4-case result1passed/3failed,
  51.034s, exit100 /tmp/koh-cube-shared-math.log:
  DMGRelease passes; CGBRelease/Debug and DMGDebug first images are blank.
  Limits unchanged CGB200/500, DMG1100/2000 after boot. DO NOT relax them.
- /tmp/koh-cube-first-owner.log traces CGB first200 PC51EC bank3 to
  Canvas.FillTriangle+85. Boot is complete, allocation492managed/1935raw
  succeeds, mode3zero. Further performance investigation is required.
  CubeHost reports first PC/bank as fields10/11 (12 total) for diagnostics.
  The sample source is linked unmodified by fixture Cube.csproj. Full-frame
  and racing-beam still need native structural acceptance.
- Exact post-hook /tmp/koh-cube-multiply-postcommit.log:21 cases,18passed,
  3failed,78.830s,exit100;55 unrelated backend cases filtered. All11 selected
  math/stack cases pass,6 validator cases pass,DMGRelease cube passes; the3
  remaining cube cases fail their first-capture geometry. No local process live.
- Next: profile remaining startup/render cost, fix early-capture timing.
  Do not claim the weaker inequality test establishes a rendered cube.
  No emulator/core/debugger rewrite; no new C# parser or compiler fallback.

## CI, PR and closure

Current2ebfc4c runs43750920221 (Rust) and43750920286 (managed) verified live
after push. Poll these exact runs; no current-head cross-platform result yet.

PR https://github.com/retro-dev-tools/koh/pull/55 stays draft. Update its concise
live checklist and evidence after pushes. Rough45% is not measured coverage.
Test matrix34/565 complete,531partial/outstanding, zero physically retired.
Cube source group3methods/6cases/8assertion sites remains partial; isolated
Canvas7methods/36sites also need exact raster/plane/page tests. All compiler
tests must finish migration; none may be deferred to the emulator PR.

b77361e native run43748940025: Linux132918364479 actual logs806/806 passed,
769.867s, zero skipped; separate doctests (no cases), artifact graphics1/1
(two other SDK cases intentionally filtered). References job132918364266
success. macOS132918364416 and Windows132918364469 were live at last query.
Managed run43748939949 failed; inspect primary logs before describing causes.
Earlier4e7212d native all3hosts passed794Linux/macOS,792Windows. Two Unix
assembler cases explain difference. Do not transfer that proof to newer heads.
Newly committed cube acceptance will be red until early captures are fixed;
that is intentional exposure of unfinished required work, not waived coverage.

Still required: classes2048v2/JRPG, floats/IRQs/reentrancy/banked data and final
capacity contract, all sample behavior, PortablePDB→ELF→KDBG/source breakpoints/
stacks, complete test dispositions, delete C# compiler and compiler TUnit
project/packages, final artifact/AOT/release checks, comparable compile time/
memory/binary measurements and final Linux/Windows/macOS acceptance.
Koh.GameBoy remains C# authoring library; tile-sheet build task preserved.
ELF32/SM83 only; no KOBJ path, no hidden C# compiler route. Old source remains
pending verified replacement, not final acceptance.

## Uncommitted object-core increment

Native typed object references, checked fresh allocation, constructor/direct
instance calls, inherited/reference fields, parent-descriptor ELF relocations,
class/null SSA joins and object/string array storage are implemented in the
current dirty tree. Virtual/interface dispatch remains explicitly unsupported.
Do not claim real2048v2/JRPG acceptance or retire legacy tests yet.

ObjectCore fixture exercises14 entries in Debug/Release on DMG/CGB through
the existing C# emulator:56 ROM probes. Focused test passes4/4 in22.748s,
exit0,130 unrelated cases filtered (/tmp/koh-object-expanded-green.log).
Expanded red run first exposed resolver.type_signature assigning ValueType
to reference-class array elements. Corrected metadata classification plus
ldelem.ref/stelem.ref now pass. Exact heap records cover constructor strings,
inherited fields, descriptors and untouched bank2. Broader IR/SM83/storage
regression is RUNNING: write_stdin session97085,
/tmp/koh-object-regression.log. Re-poll that handle before another build.
An earlier invocation used nonexistent --test backend and exited101 before
building; corrected target is --test sm83.

Five new IR identity/upcast adversarial cases were added AFTER that regression
compiled, so rerun the IR target afterward. Docs class-layout.md now distinguish
executable object core from pending dispatch. No object-core commit yet.
Next: finish regression/negative checks, review descriptors and reference
storage invariants, then logical commit/push and live PR55 checklist refresh.
Reference-cell indirect IL and nested-array execution remain pending features.

## Commands and discipline

### Object-core acceptance before commit

Final broad IR/SM83/storage regression completed376/376 in338.797s, exit0,
99 unrelated cases filtered (/tmp/koh-object-final-regression.log).
After CSharpier formatted the two fixtures and the final cross-function class
identity check was added, focused IR/object/static/constructor checks passed
277/277 in40.210s, exit0,126 unrelated cases filtered
(/tmp/koh-object-postformat.log). All15 object entries execute in four configs,
60 ROM probes. Workspace all-feature/all-target check succeeds7.14s with the
existing cube support unused-helper warning; separate rustdoc succeeds four
crates/zero cases. Source hashes match /tmp/koh-object-tested.sha256.
Read-only object_core_review re-review reports no remaining blocker, including
the new canonical class/value identity checks. Two legacy class methods now
have complete stronger native replacements:47/565 complete,518 outstanding,
zero physically retired. See test-disposition.md for exact mappings.

Prior pushed metadata commitb4caf55 now has completed native hosted evidence:
run43781354384 actual logs1012Linux in774.586s,1012macOS in1178.527s,
1010Windows in1792.635s, zero workspace skips; separate rustdoc/artifact/RGBDS
jobs succeed. This predates current object-core work, not current-head proof.
Ordinary SDK2048v2 Release build finished exit1 in29.55s:
/tmp/koh-object-2048-sdk.log. It now reaches Game::Run IL_000c, explicit
Virtual reference calls require dispatch lowering. Class construction advanced;
the required game still fails. All local build/test handles now terminal.
Next: commit this reviewed increment, push same branch, update draft PR55 live
checklist. Then virtual/interface dispatch and actual class-game execution.

### Latest object-core verification checkpoint

Earlier broad regression completed357pass/6fail in338.093s (exit100).
All six failures were now-supported ObjectArray and UnsupportedClass rejection
expectations; replaced them with native length/null-cell and constructor/heap
checks, preserving generic/malformed/readonly assertions. Review found raw
Native loads fabricating Object identities. Reproduced5pass/1fail, exit100 in
/tmp/koh-object-identity-red.log, then required matching typed reference cells
and typed static Object/String/Array addresses. Follow-up review found array
indirection and conflicting static cell declarations could bypass this; now
all managed reference loads require typed cells and static identity validation
checks reference storage types across functions. Explicit negative tests added.

Intermediate review run /tmp/koh-object-review-green.log passes270/270 in60.788s,
exit0 (122 filtered), including all15 ObjectCore entries (60 ROM probes) and
replacement positives. That binary predates the final array/string load and
static-cell consistency fixes: DO NOT transfer its proof to the latest bytes.
Final broader regression just started /tmp/koh-object-final-regression.log;
write_stdin session24941 is live; poll it before another build.
Code review agent object_core_review is read-only; original findings recorded.
No object-core commit/push yet. Finish final checks and follow-up review before
commit. The source fixture method UnsupportedClass is now ReferenceConstructor.

Use RUSTUP_TOOLCHAIN=stable /usr/bin/cargo, target-dir /tmp/koh-header-target,
-j1, KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet.
Run builds serially. A write_stdin result with session_id is STILL LIVE.
Record actual terminal exit and re-read completed log. No pipeline status.
Default: cargo nextest run --workspace --all-features; separate cargo test --doc.
Cube target: --test cubes; all4 real cases plus6 checker cases are required.
Commit hook PATH/DOTNET_ROOT=/home/minorum/.local/share/koh-dotnet-10.0.115.
No destructive cleanup or unrelated staging. Preserve sample assets.
Vault handoff is brief project-level knowledge; code details stay in this file.
