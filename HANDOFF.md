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
