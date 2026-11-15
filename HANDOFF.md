# PR #55 handoff

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

## Commands and discipline

Use RUSTUP_TOOLCHAIN=stable /usr/bin/cargo, target-dir /tmp/koh-header-target,
-j1, KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet.
Run builds serially. A write_stdin result with session_id is STILL LIVE.
Record actual terminal exit and re-read completed log. No pipeline status.
Default: cargo nextest run --workspace --all-features; separate cargo test --doc.
Cube target: --test cubes; all4 real cases plus6 checker cases are required.
Commit hook PATH/DOTNET_ROOT=/home/minorum/.local/share/koh-dotnet-10.0.115.
No destructive cleanup or unrelated staging. Preserve sample assets.
Vault handoff is brief project-level knowledge; code details stay in this file.
