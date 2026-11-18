# Current work: DWARF then GPUI

2026-11-18: fetched and fast-forwarded master to `9ebb6e5` (compiler PR55 merged).
Active branch `feat/gpui-ui`. User requires complete KohUI removal and selected
KDBG-to-DWARF migration before continuing Rust + GPUI Kit UI implementation.
The DWARF prerequisite and windowless engine are implemented. Current UI
progress, preview commands and full parity/removal acceptance are in
`docs/gpui-migration.md`; the wire contract is in `docs/engine-session.md`.

Draft PR56: https://github.com/retro-dev-tools/koh/pull/56 (keep draft and updated).
Inspector increment: CPU/Palettes/Tiles/Memory tabs, conditional detailed snapshots,
integer-scale cached tile images with explicit release, validated hex entry,
wrapping PageUp/PageDown/Home. Normal memory pane fits hex+ASCII; small windows
scroll. Focused tests incl NativeAOT client8/8, build/Clippy/format exit0.
Native DMG palette/tile pixels match restored native snapshot; CGB fixture's
distinct bank0/bank1 images match too, including scrolled bank1. Reviewed memory
FFF0 wrap, invalid entry and680x480 layout. Logs /tmp/koh-gpui-inspectors-*.log;
bounded harnesses /tmp/koh-gpui-preview/run_inspectors_oracle.py and
run_cgb_inspectors.py. Curated docs/images/gpui-memory.png is actual capture.
Still7/10 checkpoints: full input/accessibility/picker/theme polish, complete
KohUI deletion/package/notices and final platform/audio/DAP acceptance remain.
Hosted PR checks pass at3642a44, run44413679545. No full-suite rerun required for
inspector-only Rust change; earlier workspace1484/1484 remains prior evidence.
Production now emits and consumes DWARF: -d/--debug, SDK/editor/sample .elf
sidecars, neutral records, managed native-reader binding, updated fixtures.
KDBG writer/reader/format/kind/builder removed, no fallback retained. Range
counts support600bytes; signed constants stay values, not code addresses.
The emulator still rejects banks above255 explicitly; native metadata preserves
wide banks. Loading invalid data preserves maps; symbol-free loading clears them.
Assembly expansion ancestry uses the documented vendor extension.

Final local checks: CI solution build exit0, zero warnings/errors, all7C# ROMs;
fast suite581total/579passed/0failed/2existing opt-in SDL skips, including49
debugger tests, exit0.35Rust linker cases pass. Workspace all-target/all-feature
check passes with existing unused cube-helper warning.44extension tests pass
under owned Xvfb, which was stopped. All7SDK ELF sidecars parse with symbols/
sources. See docs/dwarf-migration.md for exact completed logs and limitations.
All3new Cargo notices checked against archives/source and copied output.
Focused Clippy/format checks pass; pre-existing all-target findings unchanged.

Real C# source-breakpoint/stack/SDK debugger acceptance passed: 37/37 selected
cases, exit0, 244.779seconds; 619 cases outside the filter. Final log:
/tmp/koh-dwarf-source-debugger-tests-final.log. Both SDK2048 variants pass.
DebuggerHost's injected-symbol fixture uses the neutral record constructor.
Hosted22f9ef4 failed1/1480Rust fast cases: assembler macro test retained KDBG
offset assertions. Replaced with full decoded DWARF range/ancestry comparison;
all149assembler tests pass, exit0, /tmp/koh-dwarf-macro-acceptance.log.
Hosted Rust checks passed at a481017, run44393108491.
Windowless engine session implemented; see docs/engine-session.md for protocol.
Commands execute on the emulator thread between frames, including while paused.
DAP uses the same queue. Replies copy LCD/CPU/palette/VRAM/memory data.
Save/restore replies reflect completed work; corrupt restore rolls back the core.
EOF/Quit/oversized input flush SRAM after joining the worker. Audio pacing uses
stderr. Cancellation reads raw stdin asynchronously, not blocking Console.In.
Nine process tests pass against fresh Linux NativeAOT, including DWARF source
breakpoints/stepping, DMG/CGB, actual joypad edges, saves and shutdown.
Native build /tmp/koh-gpui-engine-native/Koh.Emulator.App, SHA256
0f4be5e41e9e67f53943633a94242797e52c2bd1aff37fd222215dc5cc468398.
Native SDK2048v2 smoke:123frames,8638408T-cycles,changed opaque LCD,save/restore,
exit0. Logs /tmp/koh-engine-native-process-tests.log and
/tmp/koh-engine-native-2048.log. Native publish /tmp/koh-engine-native-publish.log.
Tests can use KOH_TEST_EMULATOR=<absolute native binary> to exercise published
engine; otherwise use the managed test-host runtime.
Connected GPUI preview now exists in rust/koh-ui, binary koh-emulator. Uses
gpui-kit0.10.0 with matched gpui-pre0.3.12, image/base64/serde for pixels/IPC.
The async worker owns the real engine; bounds/validates replies and rejects
overlong commands without ending the current session. Shutdown waits for the
engine's completed persistence; no automatic forced kill. The renderer copies
RGBA to BGRA and scales to integer physical pixels. Replaced images are
explicitly released from GPUI's sprite atlas. CPU inspector and transport/
picker code exist; all four inspectors now render real snapshots. Full input,
accessibility and polish are still pending.
Preview: KOH_ENGINE=/tmp/koh-gpui-engine-native/Koh.Emulator.App
CARGO_BUILD_JOBS=2 CARGO_PROFILE_DEV_DEBUG=0 cargo run -p koh-ui -- <ROM>.
Default engine lookup is relative to the UI executable; packaging is not done.
New member requires Linux desktop build headers; PR/full workflows updated.
No previously locked Cargo package versions removed; new Kit graph adds739.
Workspace fast Rust:1484/1484 pass, one explicitly ignored native-client test;
/tmp/koh-gpui-workspace-fast.log. Native-client test separately executed with
KOH_TEST_ENGINE=<engine> cargo test -p koh-ui --lib --locked -- --ignored.
That test checks real debug snapshots, oversized path recovery, save and exit.
Native Xvfb window ran actual SDK2048v2; waits for title pixel oracle, Enter
reaches score screen, exact integer LCD pixels, P freezes rendering, F5 saves,
WM close exits0. /tmp/koh-gpui-window-bounded-final.log. Curated screenshot
docs/images/gpui-desktop.png is from that window, not a mock. Null OpenAL driver
used: no audible-output claim. Rust headless PNG is byte-identical to direct
engine output, /tmp/koh-gpui-headless.log.
Owned native harness remains /tmp/koh-gpui-preview/run_window.py and helper
x11_capture.py; all processes are bounded/owned and cleaned up. ImageMagick
import lacks X11 support, so screenshots use XGetImage and Pillow. Wait for
actual X11 mapping before focus/input; window existence alone is too early.
Next: palette/tile/memory panels, memory navigation/input/focus/accessibility,
right Shift/Select parity via GPUI modifier events, light/dark slate polish and
resize acceptance. Then remove ALL KohUI/backends/demo/tests/dependencies and
update launch/package/CI. Preserve DAP and headless entry points. The UI still
uses the legacy engine project, so framework removal has NOT happened yet.
No agents spawned. Hosted PR checks passed at0822698 (run44396773962);
new UI increment needs fresh hosted checks after push.
User authorized opening a draft PR and keeping it updated; push this branch and
update its description as increments land. No merge or release authorized.
User requests an ASCII progress bar and detailed checkpoints. Whole-goal ledger
is in `docs/gpui-migration.md`: [#######>--]7/10 checkpoints complete, not effort.

Host gotcha: a stopped orphan aspire-managed template-search process holds the
shared NuGet lock. Leave it alone. Use NUGET_SCRATCH=/tmp/koh-nuget-scratch on
all dotnet/cargo-driven dotnet checks. Pinned SDK:
/home/minorum/.local/share/koh-dotnet-10.0.115; set PATH,DOTNET_ROOT,KOH_DOTNET.
DOTNET_PROCESSOR_COUNT=4; serial builds/tests. No NuGet audit suppression.
Final fast log /tmp/koh-engine-fast-tests-final.log; build log
/tmp/koh-engine-solution-build.log. No local build/test process live at commit.

Unrelated modified `samples/csharp/gb-jrpg/screenshots/battle.png` remains intact,
SHA256 `fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`.
Stage only task files. Builds run serially in `target/debug`.
Edit/LSP tools unavailable; use apply_patch and targeted source reads.
Vault AGENTS.md and vault-observer skill not found at inspected paths.

The compiler handoff below is historical:

# Native compiler migration handoff

## Current integrated removal

The native compiler and SDK migration acceptance is complete in draft PR55.
The validated removal deletes the old compiler and compiler test tree, their solution
entries and direct Roslyn/Cecil packages. GameBoy, tile-sheet build tools,
emulator/debugger and standalone Superopt remain. Native compiler coverage lives
under `rust/koh-compile/tests` and runs through cargo-nextest; doctests run
separately. No compiler fallback is retained.

The unfiltered Linux native run passed2239/2239, zero skipped, exit0, with separate
doctests passing. All565 source dispositions are complete and legacy definitions
retired. Removal-candidate solution build produces all seven C# sample ROMs with
zero warnings/errors; retained .NET tests pass567, with two existing opt-in SDL
skips. Final hosted run44330863800 atf5a10e8 passes all14 jobs: Linux/macOS2239
native tests each, Windows2237, zero skipped, separate doctests, runtime notices,
artifact smoke, live .NET/editor/SDL and NativeAOT publish/smoke. All three
downloaded native ZIP digests match GitHub metadata and contain runtime notices.
See [progress](docs/rust-compiler/progress.md),
[removal checklist](docs/rust-compiler/legacy-removal-checklist.md) and
[runtime boundaries](docs/rust-compiler/runtime-boundaries.md). The
[acceptance audit](docs/rust-compiler/acceptance.md) records all requirements,
the90-measurement performance refresh and known limitations. Existing first-party
and managed-payload notice gaps remain release-publication blockers. No release
is published; PR55 remains draft and must not be merged by the agent.

The checkpoints below are historical implementation notes, not current counts,
live process handles or commands for removed projects.

## Active scalar control-flow test migration

Published7109d8e0baf03062937ee5710b39137aaa94d0ac; draft PR55/master,
CI44140255184 confirmed running. Symbol-address unit published and PR ledger
232/565,333pending reflects that head. Local scalar increment is ready to commit.

Reviewed first34 methods of Sm83ControlFlowTests,37 assertion sites; ported all
exact numeric expectations. New tests/support/control_flow_scalars.rs plus three
sm83.rs rstest width cases normalize8-bit values explicitly, use Native16 for
16-bit vectors and I32 for32. Parameter/constant inputs run before/after real
optimization; full return bits, SP and guards checked by retained C# emulator.
Adds9 actual32-bit carry/borrow/signed-boundary vectors because source operands
did not prove the stronger test-name claims. Retains original subtraction
producer chain and a dynamic3-input version to keep intermediates live.

46 vectors*4forms +4chain runs =188 ROM executions, chunks<=32paths for command
limits. Final3/3 passes3.448s,exit0,/tmp/koh-control-flow-scalars-boundaries.log.
Fmt/discovery exit0. Discovery1826 includes1649compiler cases,0ignored,all3new
cases present. No production change. Local ledger266/565 (265native+1prior
parserDELETE),299outstanding. Source54-method file is34complete/20pending;
calls/memory/boot/phi cases are not deferred or claimed complete. Documented
per method in control-flow-scalars.md; source inventory/global totals reconciled.
Commit increment; queue push until current CI terminal. No local builds live.

## Active symbol-address test migration

Published headca8ae6d35a60641ef0311c32b6cd4583daba4876; draft PR55/master,
CI44135436415 confirmed running. Previous CI44128918287 oncd41567 passed
1812/1812 with0skips, separate rustdoc and .NET fast checks.

Audited all3 Sm83SymbolAddressTests methods and7 assertion sites. New Rust
driver/symbol_addresses.rs uses actual Roslyn DLL/PDB, with/without PDB input,
two explicit ROM/WRAM placements and retained C# emulator. Four modes execute
16 ROMs: code bytes/relocations match actual code symbols; header reaches shifted
startup rather than banked Main; globals resolve to actual physical Counter1,
Evidence2,Zero0 bytes, with poisoned initialization and stack/heap guards.
No production compiler change. PORT1/REPLACE2, no DEFER/DELETE or source removal.
Ledger232/565 complete (231native+1prior parserDELETE),333 outstanding.

Fresh execution4/4 passes20.128s,exit0,/tmp/koh-symbol-address-execution.log.
Rustfmt/CSharpier/discovery exit0. Discovery1823 including1646 compiler cases,
zero compiler ignored, all4new cases present. Source mechanical cross-check565
methods, thisgroup3methods/7assertions. Corrected stale zero-migrated inventory
prose and backend entry-case disposition totals104PORT/185REPLACE/1DELETE=290.
Documented in docs/rust-compiler/symbol-addresses.md. Commit increment, queue
push until current CI terminal, then refresh PR55 ledger/checklist. No local
builds live; full migration remains incomplete.

## Active generic-reference method unit

Published headcd41567929926f28a0bf17af65853ef38c01fa8b, draft PR55/master;
CI44128918287 completed success:1812/1812 passed,0skipped,1677.891s, with
separate rustdoc and .NET fast checks passing. Primary completed log
/tmp/koh-ci-cd41567-completed-plain.log,GH_EXIT0. Current generic-reference
unit is ready to commit/publish; no local builds live. Initial entry failed4/4
at the explicit unfinished guard,20.730s,exit100,
/tmp/koh-generic-reference-red.log. Basic virtual overrides then passed4/4
in25.883s. Expanded implicit/explicit generic interfaces, default generic body
and captured virtual callback passed4/4 in26.623s using a local delegate.
The initial System.Func fixture lacked System.Runtime input and correctly
diagnosed the missing assembly; final fixture reuses existing PairHandler<Payload>,
not a new delegate or a claim of full BCL support. New definitions append at end.

Resolver now separates a scoped method declaration from applying method arguments
using the existing method identity interning. A generic declaration retains its
closed owner with an empty method-argument list; method_types still rejects that
open declaration at the typed-IR boundary (new unit test). specialize_on still
checks actual arity/constraints. Shared method_owner_in binds declaring context;
binder uses declarations for slots/mappings, then applies actual arguments to the
selected body's exact owner. Interning remains count/work bounded. Removed the
now-unused specialize wrapper. No new runtime ABI or IR operation.

Session14322 finished133/133 pass409.320s,exit0,
/tmp/koh-generic-reference-regression.log. Fixture-only foreign and inherited
generic MethodImpl additions then8/8 pass39.404s,exit0,
/tmp/koh-generic-reference-scopes.log. Generic owner/nongeneric leaf inherit
virtual generic ancestor bodies, overrides still select; unrelated bodies reject.

Review found selected override signature/GenericParam arity could disagree and
still compile. Two metadata mutations reproduced success where rejection was
required. Declaration binding and actual specialization now check metadata arity.
Final all-library/reference-resolution plus targeted generic consumers248/248
pass192.823s,exit0 (/tmp/koh-generic-reference-arity-regression.log); this follows
the133/8 checkpoints and is not a claim they were rerun after the guard fix.
Final fmt/CSharpier/workspace-all-feature rustdoc pass (4crates/0cases).
Discovery1819,all7newcasesunignored. Logs /tmp/koh-generic-reference-owned-*.log,
fixture hashes /tmp/koh-generic-reference-owned-fixtures.sha256. Commit unit,
push (previous CI terminal), verify remote/new CI, refresh PR55 checklist.
Ledger229/565 unchanged; full goal incomplete. Next prioritize the remaining
source-test disposition audit and explicit subset decisions; no boxing/variance
frontend test cases were found in the initial repository search, not a completed
feature audit. Retain the direct constrained own-method owner review.

## Active instantiated-interface unit

Local commitbe80033 independently improves regression setup: run-scoped reuse of
successful execution-host builds in the serialized roslyn group, MSBuild on each
new run. Same8 delegate cases:46.865s baseline,42.954s incremental,26.850s reused,
all8 passed. Separate generic/debugger consumer8/8 pass53.513s,fmt passed.
Commit249a789 contains the verified instantiated-interface unit. Both commits
are queued with the class signature-byte budget fix for one publication batch.

Published head remains3d44c0b; CI44117991696 completed failure:1807/1808 passed,
one unchanged signature-byte budget test failed, zero skipped; .NET fast passed.
Primary /tmp/koh-ci-3d44-completed-plain.log,GH_EXIT0. Resolver binding shares closed InterfaceImpl
substitution across layout, constraints and dispatch. New ClosedTypes entry tests
implicit, explicit, inherited and multiple instantiations on one receiver.
Initial four cases reject valid TypeSpec rows; corrected four cases pass34.089s,
exit0 (/tmp/koh-instantiated-interfaces-green.log). Final fixture adds byte/sbyte
identity and is included in the active broader regression.

Nextest session29383 ended exit100:125/128 passed in728.347s; three failures in
/tmp/koh-instantiated-interfaces-regression.log. It found an interface graph
budget diagnostic regression: dropping the
original class scan charge caused the generic work guard to fire first. Restored
the class charge without changing the test; that edit is newer than this run's
binary. The two other failures are stale GenericInterface rejection assertions
in interfaces.rs; now replaced with executable acceptance, retaining BoxedInterface
diagnostics. Session11921 ended2budget passes/4expected constrained-value
failures,exit100,19.074s. Constrained selection now binds closed declarations,
compares scoped signatures and retains exact MethodImpl owner identity before
specializing the body on the receiver. Original struct pointer is retained.

Final consumer14/14 passes82.948s,exit0,
/tmp/koh-interface-final-consumers.log. Tests include generic methods on closed
interfaces, implicit/explicit generic structs, packed/ulong returns, byte/sbyte
contracts and foreign struct/class code returning game-local payloads. Heap must
remain untouched for value calls; caller mutation and aggregate copies checked.
A metadata mutation names the correct generic body definition on the wrong
closed instantiation and must reject with member/IL context.

Session31864 completed132/132 passes407.720s,TEST_EXIT0, followed serially by
fmt (exit0) and workspace/all-feature rustdoc (exit0,4crates/0cases). Logs:
/tmp/koh-instantiated-interfaces-final-regression.log,
/tmp/koh-instantiated-interfaces-final-fmt.log,
/tmp/koh-instantiated-interfaces-final-doc.log. Discovery1812 includes all4new
unignored cases; CSharpier checked3files,exit0. Fixture hashes
/tmp/koh-instantiated-interface-fixtures.sha256 verified unchanged.
Interface unit committed249a789. Next gaps include method-generic reference
dispatch, variance and boxing/default-interface value fallback. Ledger unchanged.

The hosted byte-budget failure reproduced locally. Layout now charges all
instance-field signature bytes before parsing/binding any field, preserving the
limit and static-field behavior without double-charging. Unchanged test passes;
all54 layout plus closed-type4/interface-budget2 pass60/60 in266.024s,exit0,
/tmp/koh-layout-byte-budget-regression.log; fmt passes. Commit this separate fix,
push batch (previous CI terminal), verify remote head/new CI and update PR55.

## Latest closed-reference dispatch unit

Published34a7e9b CI44104980515 ended failure after1361 Rust passes: obsolete
generic value-constructor rejection. .NET fast job passed. Primary completed
log /tmp/koh-ci-34a-completed.log,GH_EXIT0. Local ccacab2 (closed constrained
values) already committed but unpushed; current unit and CI fix are ready to
commit. Prior CI is terminal, so publish the batch after commits; never merge.

Binder metadata reads normalize MethodDef/TypeDef identities; slot/cache/call
identities retain closed owners. Scoped signatures reuse inherited binding;
typed signatures preserve method instance context. Supports nongeneric virtual
methods and nongeneric interface contracts on closed reference types, including
explicit/inherited/default bodies, overrides, aggregate returns and captured
virtual callbacks. MethodImpl inherited bodies bind the closed ancestor even for
nongeneric leaves; unrelated bodies still reject. Methods with their own generic
arguments dispatched on constructed reference owners and instantiated interfaces
remain unfinished, with the former explicitly diagnosed in the new cases.

Independent actual Roslyn/emulator checks retain byte/64-bit payloads, different
counter effects, copies, default-method nested calls, constrained reference cells,
foreign aggregate returns, receiver mutation after delegate binding, and valid/
invalid inherited MethodImpl rows. ECMA II22.27 permits base-class bodies; do not
use CLR rejection as an oracle or call that shape malformed. Initial basic4/4 red,
basic4/4 pass32.510s; nongeneric leaf inherited-body red then8/8 pass56.467s.
Final production Rust binder/library/interface/default/delegate/virtual/object
run322/322 pass744.216s,exit0,357 excluded,
/tmp/koh-closed-reference-regression.log. Later fixture-only extended consumer
selection10/10 pass68.525s,exit0,450 excluded,
/tmp/koh-closed-reference-consumers-final.log. No production Rust changes between
those runs. GenericConstructor returns1 with a fully untouched managed heap;
all four malformed constructor/private-local/readonly-store assertions remain.

Rustfmt/CSharpier and separate workspace/all-feature rustdoc pass (4crates,0doc
cases). Discovery1808,all4 new MethodImpl cases unignored. Existing unused cubes
host warning remains. No builds live. Fixture hashes
/tmp/koh-closed-reference-fixture.sha256; recheck after commit hooks. Ledger stays
229/565 complete,336 outstanding. No source dispositions credited.

Commit constructor acceptance (driver.rs,Structs fixture,aggregates.md), then
closed-reference binder/fixtures/docs, then CI collect-all-failures change (same
two jobs, same assertions, --no-fail-fast for workspace runs). Push queued batch
and refresh concise draft PR55 checklist/current CI and vault handoff. Next:
instantiated interfaces, method-generic dispatch on closed reference owners,
variance/boxing, remaining runtime/storage/test/product/removal gates.

## Latest closed-value constrained unit

Published head34a7e9b; CI44104980515 verified running on that head, .NET fast
job passed. Local increment supports closed generic value receivers under
constrained.callvirt for nongeneric interface contracts, including methods with
their own generic arguments. Metadata reads use raw TypeDef; selected call keeps
the exact closed struct owner through existing specialization/ABI. No boxing or
new runtime convention. Class/instantiated-interface dispatch remains unfinished.

Actual fixture checks repeated mutation42/43, byte/64-bit payloads, explicit vs
same-name public selection, packed/64-bit generic returns, foreign value/local
payload, and MethodImpl borrowing a different generic value definition's body.
Retains banked ELF round-trip, resolver determinism, stack/heap/alternate-WRAM
guards. Initial4/4 red at old generic-value guard, basic4/4 pass31.726s, generic
return checkpoint4/4 passes. Final237/237 pass150.387s,exit0,438 excluded,
/tmp/koh-closed-constrained-regression.log. Rustfmt/CSharpier and separate
workspace/all-feature rustdoc pass (4crates,0doc cases). No local builds live.
Fixture hashes /tmp/koh-closed-constrained-fixture.sha256 saved after final run;
check unchanged after commit hook. No new rstest declarations; ledger229/565,
336 outstanding remains. Docs constrained-calls.md/closed-types.md/progress.md.

Commit this coherent unit locally, keep current hosted run alive; publish after
CI44104980515 is terminal. Update PR55 queued work and vault. Next runtime work is
constructed generic reference/interface dispatch and instantiated interfaces;
boxing, broader IRQ/banked-data and full product/test/removal gates remain.

## Current inherited-member unit

Local d530aec plus pending CI/static and inherited-method increments are ready
for publication after commit. Published6c2da32 CI44097447812 ended failure after
1328 Rust passes: stale generic-static rejection assertion. .NET fast job passed.
Primary log /tmp/koh-ci-6c2-completed.log,GH_EXIT0. driver.rs now executes
NeedsGenericStorage=0 under poison; static capacity rejection is preserved.

Typed inherited-method lookup carries caller scopes and exact closed base owner
through specialization. Shared base_type replaces duplicate layout binding.
Field references name their declaring type; the old ancestry search incorrectly
repaired unresolved fields. Initial edited-field acceptance assumption was wrong
under ECMA II9.8/22.25, corroborated by CoreCLR FindField. Corrected tests retain
native method execution42, overloads/MethodSpec/generic caller/foreign packed
payload and IL-context field rejection. No API/ABI changes or disposition credit.

Final249/249 pass262.796s,exit0,436 excluded,
/tmp/koh-inherited-members-owned-final.log. Prior245/249 exposed4 deliberate
row-collision fixture assertions shifted by RemoteBase; moved new type to end,
retained all assertions. Rustfmt/CSharpier and separate workspace/all-feature
rustdoc pass (4crates,0doc cases). Discovery1804,all8 new cases unignored. Fixture
hashes /tmp/koh-members-fixture.sha256 unchanged. No local builds remain live.

Commit static CI assertion fix separately, then metadata/fixture/docs unit. Push
queued batch now prior CI is terminal; refresh PR55 checklist/new CI and vault.
Never merge. Ledger229/565,336 outstanding. Constructed virtual/interface dispatch
and nested open generic method signatures remain open. Full migration incomplete.

## Current closed-base unit

Published head6c2da323817694b7d5f8d387ba26cf958938f7cc, draft PR55/master.
CI44097447812: .NET fast job success, Rust checks still running on that head.
Local class-layout change binds base TypeSpecs with declaring
arguments and resolves sealed metadata through raw TypeDefs. ClosedTypes adds
Inheritance with byte/wide/nested/multi-level/foreign bases and base writes;
four Debug/Release × DMG/CGB cases pass29.910s in
/tmp/koh-closed-inheritance-scopes.log. Initial valid fixture rejected at generic
base layout (/tmp/koh-closed-inheritance-red-valid.log). Earlier fixture typo
derived from an existing sealed helper; fixed to a dedicated unsealed base
before the meaningful red run.

After adding sealed-base mutation rejection, final selected438/438 pass460.872s,
exit0,375 excluded, /tmp/koh-inheritance-regression.log. Run is terminal.
Final Rustfmt and separate workspace/all-feature rustdoc pass, four crates/zero
doc cases. CSharpier ran before final test run; both fixture hashes unchanged.
Docs closed-types.md describe the bounded capability; generic
virtual/interface dispatch and inherited MemberRef substitution remain open.
Ledger stays229/565,336 outstanding; no disposition credit for new runtime work.
Commit this coherent unit and push after current hosted run ends. Update concise
PR checklist and vault handoff. No local builds remain live.

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
650c522LOCALcommitcomplete(clean), holdpushCI43968285185same15d0cffstilllive.
NewunitIEEEsingleAddruntimebugfix: originalAddF32flushessubnormalsandmishandles
infinity/NaN. Added14boundaryROMentries and2managedgroups(14+256deterministic
exponent-sweep pairs). All3selectedregressions55858EXIT10017.504s:1+1rawsubnormal
returned0insteadof2inCLRandactualROM; NaNclassfailureinthesweep. FixonlyAddF32
algorithm(bitpatternAPIunchanged): effectiveexponent1forzero/subnormal, hiddenbit
onlynormals, normalizewhileexp>1, encodecorrectsubnormalexponent0, explicitNaN/
infinity/signedzero; keepRNEguardroundsticky. RemovedstaleclassXMLclaimingflush/
oldCSharpFrontend architecture. NoFloatCILloweringclaim, othermathstillneedsaudit.
Current98547live /tmp/koh-softfloat-ieee-first.log selects14casesallold+new,
80nativeROMs20vectors ×D/RDMGCGB plus415managednumericassertions. Parentownsall
edits/builds; reviewread-only. Needfinaltests/docs/checks/logicalcommit; holdpush.
98547terminalEXIT0 first14/14pass64.421s; formattedfinal61412terminalEXIT0
14/14pass45.734s /tmp/koh-softfloat-ieee-final.log. All80ROMs/415managedchecks,
alloriginalvectorspreserved. Numericalread-onlyreviewnoconfirmedblockers;
notexhaustiveproofnorfloatCILclaim. Fmt/CSharpierchecksEXIT0. Newdesign/evidence
docsoftfloat-addition.mdlinksprimaryRustf32contractforRNE/zero/NaNclassification.
BaselineCI43968285185isnowTERMINALSUCCESShead15d0cff:download95895GH_EXIT0
/tmp/koh-ci-15d0cff-completed.log confirms1373/1373pass1953.920szero skips,
rustdoc4zerotestsand.NETfastPassedsummary. Holdpushgatecleared: commitcurrent
numericfixthenpushbothlocal650c522+fixinONEbatch; updatePR55/vault/currentCI.
Noactivelocalbuilds. Ledger137/565428remainingzero retired, fullscopeactive.
NextunitFloatingCIL actualcompilerfeature starts4741e89: distinctbinary32IRtype,
explicitbitreinterpretation, metadataKohRuntimekeyindex, lowerfloatAddtoordinary
helpercalls. TestFloat32actualRoslynAdd/call/phi/ref plusunsupporteddivision,
4D/RDMGCGBcases16ROMs prepared. Mustrejectmixedfloat/int IR anddiagnoseunsupported
floatops. No floatgeneralcompatibilityclaim. Parentownsallfiles/builds. Plan
docs/rust-compiler/float-lowering.md. No newagentsunlessreviewskillasks.
Floatunitprogress:91668redEXIT100opcode0022. AddedTypeF32/ScalarF32 andexplicit
OpReinterpret(I32↔F32only), backendbitscopy, IRrejectintarithmetic/compare/convert
onfloat. ldc.r4/ldind.r4/stind.r4, fields/arraysscalar4, ClassSingleframeworkmap.
MetadataKohRuntime keyindex viaexistingqualifiedattributeKind; staticnongeneric,
boundedcanonicalpayload/duplicatesdiagnose, f32.addrawuint(uint,uint) checked.
Indexclearedperreachability thenpopulatedbeforelowering; helperbodyrequiredonly
whenchosen(referenceassemblydeclarationsallowed). FloatAdd→bitcast/call/bitcast,
otherbinary/unaryfloatopsreject. No nativefloatALUorC#compilerfallback.
Initial55783EXIT0 1/1pass4.289s. 75053EXIT0 11/11pass17.238s. AddedField/Array,
51934redEXIT100MissingSystem.RuntimeforSystem.Single; resolvedprimitiveframework
map+ldelem.r4/stelem.r4. 56570EXIT0 11/11pass18.335s (24ROMs+7IRcases), missingkey/
duplicatekey/wrongrawsignaturechecks. NewtestrenamesactualhelperAddF32→SumF32 in
inputmetadata toproveattribute-basedselection, rootPortablePDB/ELF/KDBGchecks.
Current52232live /tmp/koh-float32-regression.log: selectedalllib/IR/lowering plus
Float32/newbasic/runtimebankcases. Parentownsalledits/builds. Needreview, payload
unitnegatives/finalchecks/docs/subsetABIupdate/logicalcommit; holdpushuntilcurrent
4741e89CI43974339605finishes ifstillrunning, batchfuturecommits. Ledger137unchanged.

2026-11-17 Float32 continuation: current pushed4741e89 CI43974339605 now
TERMINALSUCCESS. SDK first run45310 terminal/missinghandle; completedprimarylog
/tmp/koh-float32-sdk-first.log explicitly2/2pass99.416s. Added11runtimepayload
cases (valid1/7/128bytekeys, alltruncatedprefixes, invalidcanonical/UTF8/namedargs/
trailingdata). Updated float-lowering.md contract/evidence and sm83-abi.md.
Current final539selectedtests handle43599 /tmp/koh-float32-final.log ACTIVE;
537passreported, SDKcasespending. No fullcurrentworkspaceclaim. Dirtyfeature
uncommitted; finishlog/checks/review thenlogicalcommit. Fullgoalstillactive.
Final43599 TERMINALEXIT0:539/539 selectedtests pass117.417s,334unselected
(/tmp/koh-float32-final.log). Includesalllib/allIR,4Float32drivercases24ROMs,
2SDKD/Rcases. No assertionthatunselectedtestsran. Current47861 workspace
alltargets/allfeaturescheck+separaterustdoc /tmp/koh-float32-workspace-check.log
and/tmp/koh-float32-doc.log; readterminalstatusbeforecommit.
47861 TERMINAL: workspacealltargets/allfeaturescheckEXIT0(10.92s, existing
unusedcubeshelperwarning), separaterustdocEXIT0zerodoctests. Format/diffcheckpass.

2026-11-17 nextincrement after pushedcceba4c: metadataf32.sub withsamechecked
rawsignature, directF32neg signbitXOR (nohelperframe). Red9699 EXIT100 realCILsub
unsupported. First61536 EXIT0 4/4pass21.847s. Expandedfixture15entries60ROMs,
alloldneginputs1.5/-2.5/+0 D/R DMG/CGB plus tiny/inf/NaN andsubfinite/tiny/invalid.
Missingf32.subkeydiagnosticadded. ParameterizedNegateensuresrealCILneg;
ECMA335I.12.1.3/III.3.50/3.64primarysource consulted. Documentation explains
helpernameassertionreplacement, twooldmethods1PORT1REPLACE; pendingfinaltests
ledger139/565complete426outstanding, nolegacydefinitionsremoved. No floatfeature
retirement. Current32611active /tmp/koh-float32-sub-neg-final.log selected551cases
(alllib/allIR/Float32/softfloats), fournewcasespassed. Finishreadterminalchecks
thencommitlocally; holdpushwhilecceba4c CI43981727648in_progress. Parentownsfiles.
32611TERMINAL EXIT0 551/551pass71.684s296unselected; all60Float32ROMs andexisting
softwaremathcases pass. 12378TERMINALworkspacealltargets/allfeaturescheckEXIT0
5.48s(existingunusedcubeshelperwarning), separaterustdocEXIT0zerocases;
rustfmt/diffchecksEXIT0. Readylogicalcommit, no fullworkspace-test/hostclaim.

2026-11-17 Floatcomparisonsunitdirty: staticf32.cmp metadata contract byte(uint,uint),
classification0equal/1less/2greater/3unordered, typedintegerclasscomparison only.
CILclt/cgt.unincludesNaN; invertedF32bge/ble swapsordered/unordered predicate;
integerbranchloweringunchanged. Red65442 EXIT100 actualRoslynfloatcomparison
unsupported. First52929 EXIT0 4/4pass32.903s. Expanded82998 EXIT0 6/6pass59.315s:
84RoslynROMs(value/branch/minjoin) +200validCILshort/longall10branchencodings,
NaNleft/right/negativeordering/signedzero, DebugDMG/ReleaseCGB. Missingcmpkey/
wrongbyte-signaturediagnostics included. NewILcoverageassert firstwronglyassumed
Debugfusedbranch;9755 EXIT100199pass1fail. ActualDebughasbooleanbranches,
Releasefused: nowchecksconditionalboth/fusedRelease +all5valueopcodes. No numeric
assertionsweakened; directvalidCILprobescoverallencodingsindependently.
CI cceba4c43981727648 TERMINALFAIL:1073pass1fail337unrun717.941s, .NETfastgreen;
primary/tmp/koh-ci-cceba4c-completed.log GH_EXIT0. Root: primitivearraysteststill
expectedFloatArrayrejectafterF32support. Changedtoactualnativeexecution length2,
zeroed8bytepayload, fullheap/bank/frameguards; nested/rank3diagnosticsretained.
No productionbugprovenbythisfailure. Nextlocal30520active selected606tests
/tmp/koh-float-compare-final.log: all lib/IR/lowering +Float32/comparison/runtime/
primitivearrays. Finishterminalcheck,workspacecheck/rustdoc/format,commitroot
arrayacceptancefixseparatelythencomparisonfeature;push4286856+thesecommitsONEbatch
(nowbaselineCIfinished). Ledger142/565after3newcomparisonmethodsclose36original
expandednumericchecks; nolegacydeletion/featureretirement. Parentownsfiles/builds.
30520TERMINALEXIT0 606/606pass156.717s292unselected. Includesalllibrary/IR/
lowering +Float32/compare/runtime +all4CLIarraycases. 51825TERMINALworkspace
alltargets/allfeaturescheckEXIT0(6.24s, existingunusedcubeshelperwarning),
separaterustdocEXIT0zerocases; rustfmt/diffpass. Rootarrayacceptancefixcommitted
separatelyeed0a91(3files), notyetpushed. Currentcomparisonfeature readycommit;
thenpush4286856+eed0a91+featureONEbatch. NewfullCIrequired, cceba4cCIisFAILED,
notgreen. No localprocessesremain. Nextfloatruntimealgorithms/conversionsneed
independentedgevalidationbeforelowering; nofullIEEE/overallmigrationclaim.

2026-11-17 dirtyFloat32multiplicationincrementafterf8ca6c6: primaryraw1*1.0 old
helperreturns0 BOTH actualROM(56627EXIT100) andCLR(host77470EXIT1002/2fail).
FixedonlyMulF32ordinaryruntimebody: classifyNaN/inf/zero; normalize nonzero
subnormals; exact48bitproduct; oneRNEroundafterunderflowshift; normal/subnormal/
infcarry andshift>=64guard. NoAPIchange. Hostfirst22boundaries+256sweep pass,
85758EXIT1002pass1fail: rawROMnowcorrect, actualtypedCILmul stillunsupported.
EnabledBinaryMultiply→metadataf32.mulcheckeduint(uint,uint) ordinarybody.
65454EXIT0 initial20/20pass76.365s; latesttests26boundaries+256sweep and13typed
mulentries+1rawentry D/R DMG/CGB (56mulROMs;116fullFloat32ROMs). Newdocs
softfloat-multiplication.md mathrationale/oracle/limits. Ledger142unchanged,
fullmixedarithmeticmethodstillhasdivisionrowsoutstanding. Nofeature retirement.
Current66614live /tmp/koh-float-mul-final.log selects28driver/SDKcases: allFloat32,
comparison,rawAddF32,softwarehostmath andSDKfloatpathswithspaces. Atlastpoll25pass,
2SDKpending. DoNOTbuildconcurrently. Finishlog,workspacecheck/separaterustdoc/
format,logicalcommitlocally. f8ca6c6CI43984479501stillconfirmedin_progress;
holdpushuntilterminal, batchfuturelogicalcommits. No newagents; parentownsfiles.
66614TERMINAL EXIT0 28/28pass235.656s320unselected. All116Float32ROMs(56mul),
284comparisonROMs,80rawAddROMs,697managednumericobservations and2SDKadd-entry
D/R builds/execution pass. SDKcasesdoNOTclaimmulentryexecution; native driver
provesmul. Checks43537finishing logs /tmp/koh-float-mul-check.log and
/tmp/koh-float-mul-doc.log; readterminalbeforecommit. CI43984479501stilllive.
43537TERMINALworkspacealltargets/allfeaturescheckEXIT0(5.24s, existingunused
cubeshelperwarning), separaterustdocEXIT0zerocases. Rustfmt/diffcheckpass.

2026-11-17 dirtydivisionunitafterlocal73ed932(notpushed): raw1/1.0 oldDivF32
returns0BOTHROM+CLR,36262redEXIT1003/3fail8.305s. CorrectedruntimewithoutAPIchange:
NaN/inf/zerofirst,normalize operands, exactquotient/remainder atfinalunderflow
scale, oneRNEround; normalizedratio[1,2) permits signedzeroearlyifscale<-1,
scale-1 doublesdenominator. Numerator<=48bits/denominator<=25bits, no masked
negativeshift. 49039EXIT1002pass1fail12.280s: rawROMfixedand26+256hostoraclepairs
pass; typedCILdivstillunsupported. ThenDivideSignedF32→metadataf32.div checkedraw
signature. RemainderSignedexplicitf32.rem diagnostic; unsupportedfixtureisnow
actualparameterized5.5%2. Integerdivisionunchanged. No CILremfeatureclaim.
69441EXIT0 first22/22pass96.704s. Float32now48entries192ROMs incl15typeddiv+1raw
(64divROMs) and3exactoldroundedAdd/Sub inputs; allold9arithrowscoveredD/RDMGCGB.
Newdocsoftfloat-division.mdindependentproof+sourceassertionmatrix:4moremethods
3PORT1REPLACEclose42expandedassertions;9Floatmethodstotal34originalparameter
cases211expandedAssert.Thatchecks. Localledger146/565complete419outstanding;
nolegacydefinitionsremovedandnofeatureretirement. Conversion/binary64remainopen.
Current89673live /tmp/koh-float-div-final.log selected610 allLib/IR/lowering +
float32/comparison/runtime/primitivearraycases. Atlast209pass, nofail. Finish
terminal/checkworkspacealltargets/rustdoc/fmt, logicalcommitlocally; holdpushwhile
f8ca6c6CI43984479501stillconfirmedin_progress. DoNOTrestartCIorconcurrentbuild.
89673TERMINALEXIT0 610/610pass184.595s292unselected. All192Float32ROMs(64div),
284comparisonROMs,80rawAddROMs,979managednumericobservations andall4CLIarray
configspass. 32937TERMINALworkspacealltargets/allfeaturescheckEXIT0(5.25s,
existingunusedcubeshelperwarning), separaterustdocEXIT0zerocases. Fmt/diffpass.
Readylogicaldivisioncommitlocally; holdpushuntil f8ca6c6CI43984479501terminal.
Noactivelocalbuilds. Neednextconversionalgorithmaudit, remainderimplementation
andbinary64; notfinishedIEEE/fullmigration. Parentownsfiles, no newagents.

2026-11-17 dirty32-bitF32conversionunitafterlocal2cc8e98: conv.r4/conv.r.un from
I32/Native16 via checkedmetadatai32.toF32/u32.toF32, F32conv.r4identity; F32→I32/U32
usesf32.toI32/f32.toU32 thenexistingnarrow/nativeintegerconversion. Shared
runtime_methodchecks selectedCILbody, conversionrawsignaturescheckedexactly.
Kohuncheckedpolicy: truncateinrange, clamp32overflow,NaN0; narrower8/16/native
thenlowbittruncate/signextend. ECMA335sixthIII.3.27leavesfloatingoverflow/NaN
unspecified; no cross-runtimeclaim. NaNToU32helperguardchangeschosenpolicy,
wronghostcastcommentremoved.64-bit andcheckedshapesexplicitlyunsupportedpending.
PrimaryECMA6conv.r.unreturnsF, notoldMSdocsmandatory64intermediate; current
expressiondomainbinary32, R8stillrejects. Newfloat-conversions.mddoc/matrix.
32971redEXIT1003fail3pass17.025s(actualconv.r4unsupported,NaNpolicyfails);
70472firstEXIT0 9/9pass34.416s (96ROMs +43boundary+1024sweepobservations).
AddedhelperrenameFromI32→CastI32, missingkeys/wrongsignature diagnostics. First
60273 broadEXIT100203pass1fail: newWideOutputUnsupportedwasinvalidlongentry,
earlyentryvalidatorfailedbeforeoperation. Fixturecorrectedintentrycallinglong
conversion, same64-bit+ILdiagnosticassertionretained. 4408EXIT0 4/4pass24.329s.
CURRENT broadfinalhandle24133; /tmp/koh-float-conversion-final.log
alllib/IR/lowering +float32/comparison/conversions/runtime/primitivearrays619cases.
No concurrentbuild. Needfinish/checkworkspace/rustdoc/fmt,logicalcommitthenpush
73ed932+2cc8e98+conversionONEbatch: baselinef8ca6c6CI43984479501TERMINALSUCCESS!
Completedprimary/tmp/koh-ci-f8ca6c6-completed.log GH_EXIT0:1417/1417pass1840.941s
zeroskips,separaterustdoc4zerocases,.NETfast562success2UIskips0fail. Doesnotprove
localcommits. Localledger148/565complete417outstanding;2additionalPORTmethods
all8oldinputrows64expandedassertions, floatgroup11/22methods42cases275assertions.
No featureretirement oroldcompilerremoval; fullgoalstillactive. Parentownsfiles.
Read-onlyreview assignment: existing /root/backend_audit reviews currentdirty32bit
conversiondiff pluslocal73ed932/2cc8e98 numericruntime changes. ParentownsALL
edits/builds; reviewer MUSTNOT edit,commit,build,ortest. Verify fromsource/ECMA6
contract andreportonlyconfirmedsemantic/metadata defects. Current24133testlive.
24133TERMINAL EXIT0 619/619pass218.223s292unselected. New96conversionROMs and
2046managednumericobservations total pluspriorFloat32/comparison/arraygroups.
Read-onlybackend_auditreviewnoconfirmeddefect; notedmissingexplicitF32conv.r4
identitytest. Added4handCILcasesnegativezero/NaNs/subnormal withpre/postoptimizer
bitsassertions. 15474TERMINALidentity4/4pass0.022s,workspacealltargets/allfeatures
checkEXIT0(6.46s existingunusedcubeshelperwarning),separaterustdocEXIT0zerocases.
No productioneditafter619run. Rustfmt/diff/CSharpierchecksEXIT0. Readycommitthen
batchpushlocal73ed932+2cc8e98+conversion. Baselinef8CIgateclearedasabove. No live
localbuilds, parentownsfiles. Nextwideintegerconversion/binary64/checked work.

2026-11-17 dirtywideinteger/F32unitafterpushedbc1598a: newmetadatakeys i64.toF32,
u64.toF32,f32.toI64,f32.toU64 checkedrawsignedness/width; BuildertransportsI64
arguments/results with existingABI. GameBoynewordinaryILhelpersFromU64F32/
FromI64F32normalize64bitmagnitude,retain24+40bits,RNEonce; noF64intermediate.
ToU64F32/ToI64F32 truncate/clamp at64limits,NaN0, boundedshifts0..40. No newIRtype.
94984redEXIT1006/6fail16.505s missinghelpers/unsupported64. 40210firstEXIT0
13/13pass59.771s. Addedwideunsignedargument/signedreturnmetadata negativeprobes;
69446EXIT0 4/4pass24.789s. Former2temporary64diagnosticcases nowactualpositive
WideInput/WideOutput; checkedcastdiagnosticsretained. No legacycase retired.
Newconversiongroups50entries×4=200ROMs,16fullI64storeprobes assertall8bytes plus
entireheap/bank2/stackguards. IncludesULongHalfAbove2^63+2^39+1→5F000001 (F64
intermediatewoulddouble-round). Hostadds48boundary+1024sweepobservations,
softwarehosttotal3118. Ledger148unchanged;binary64/checked/remexecutionstillopen.
CURRENT18562active /tmp/koh-float-wide-final.log selected632 allLib/IR/lowering+
float32/comparison/conversion/runtime/arraycases. Last206pass. Finishterminal,
checkworkspace/rustdoc/fmt,logicalcommitlocally. bc1598aCI43991609562stilllive;
holdpushuntilitsfullrunfinishes. Parentownsallsource/builds; no concurrentcargo.
Read-onlyreview assignmentexistingbackend_audit: inspectwideconversiondiff only,
noedits/builds/tests/commits ornewagents. Returnconfirmeddefects/coveragegaps.
18562TERMINAL EXIT0 632/632pass253.196s292unselected. All200conversionROMs,
3118softwarehostnumericobservations +priorarithmetic/comparison/arraygroups.
36317TERMINALworkspacealltargets/allfeaturescheckEXIT0(5.50s existingunused
cubeshelperwarning), separaterustdocEXIT0zerocases. Fmt/diff/CSharpierpass.
Read-onlybackend_auditnoconfirmeddefects: RNE/bounds/signedminimum/metadata/
full8byteABI anddouble-roundcounterexamplecovered. Noproductioneditsafter632run.
Readylogicalcommitlocally, holdpushwhilebc1598aCI43991609562in_progress.
Nextbinary64 mustpreservecurrentdirectu64→F32counterexample: addingF64internal
supportmustnotunconditionallyroundconv.r.unthroughF64beforeconv.r4. Inspect
actualRoslynconv.r.un+conv.r4/r8shapes andnominalprecision/merges; documentvalid
unsupportedCILshapes ratherthanlabelmalformed. CurrentR8stillrejects. Checked
casts andremexecutionalsoopen;nooverallmigrationcompletionclaim.

2026-11-17 dirtybinary64core afterlocal3122c6c: Type/ScalarF64, onlyI64↔F64
bitreinterpret,strictfloatintverifier, eight-byte calls/fields/array/refstorage,
ldc.r8/ldind/stind/ldelem/stelemR8,System.Doubleprimitiveauth. F64add/sub route
checkedmetadataulong(ulong,ulong), cmpbyte(ulong,ulong), negsignbitXOR. No I128,
no nativefloatALU, no compilerfallback. Binary64Mul/Div/conversionsstillreject;
conv.r8F64identityonly so currentu64→F32one-roundproofnotchanged. No mixed
precisioncoercionorunverifiedUInt128helperenablement. AddF64oldraw1+raw1=0 both
ROM/CLR observed77210redEXIT1003/3fail11.580s. Numerichelperfixedeffectiveexp1/
nohiddenbitforsubnormals,stopnormalizationatexp1,quietNaN/inf/zeros/encoding.
94823EXIT1009pass1fail21.583s: all14+2048hostboundary/sweep checksfixed; native
RawTinypasses thenldc.r8unsupported. AddedtypedF64core;56742EXIT100missingouter
ldind.r8opcoderange. Fixedouterranges4f/57/99/a1;69397EXIT0 4/4pass27.148s.
Addedgeneric32/64IRnegative/constant/identitycases andF64cmpNaN/negative/zero:
82208EXIT0 28/28pass30.668s. FourmoreoriginalF64cmpinputswillclose1PORTmethod
(4rows×2modes×4assertions=32); currentlocalproposedledger149/565complete416
remaining, no legacydefinitionsretired. New19+4entries23×4=92F64ROMs withPDB/
ELF/KDBG/full8byteheapoutputatDFF8, arraysatD000, allbank/frameguards.
Completed19564 /tmp/koh-float64-final.log passed658/658 in306.759s,292unselected;
allLib/IR/lowering +
float32/float64/comparison/conversion/runtime/arraycases. SourceONLYdiagnostic
stringswerepolishedafterbuild (floatmetadatamessage,F64mul/divhuman names), so
followupfocusedF64testrequiredafterterminal; no semanticchanges. Neednegative
metadatahelpershape tests/review/checks/docs thenlogicalcommit andbatchpush
3122c6c+binary64core. bc1598aCI43991609562 TERMINALSUCCESS:1434/1434pass2408.087s
zeroskips,separaterustdoc4zerocases,.NETfast562pass2UIskips0fail. Completedprimary
/tmp/koh-ci-bc1598a-completed.log GH_EXIT0. Previousheadsuccessnotcurrentproof.
Read-onlyreviewassignmentexistingbackend_audit: currentbinary64core/numericdiff;
NOedits/builds/tests/commits/spawns; parentownsallfiles/currentbuild.

Finalbinary64 follow-up7207 TERMINALEXIT0:8/8pass141.183s375unselected. Four
drivercases include missingf64metadata/wrongsignedness diagnostics; four SDK
cases retainF32andaddF64 ordinarydotnetbuild pathswithspaces andwholeheap/
bank2/frameguards. Broad658run isterminal; no activebuilds. Workspacealltargets/
allfeaturescheckEXIT0(existingunusedcubeshelperwarning), rustdocEXIT0fourcrates
zerocases, rustfmt/CSharpier2files/diffchecks pass. Read-onlybackendreviewno
confirmeddefect. Ledger149/565complete416outstanding, zero physicallyretired.
Readylogicalbinary64commit thenbatchpushwith3122c6c; previousbcCIgreen.

2026-11-17 nextdivisionunit DIRTYafterpublished3a45685. RawDivF64tinyregression
91059EXIT100: originalUInt128helpercannotresolveSystem.Runtime. Replacedonly
DivF64bodywith64bitbinarylongdivision; normalization53bit, remainder<53bits,
shiftedremainder<54bits, atmost52fractioniterations. Finalsubnormalprecision
beforeoneRNEstep; exp-52halfminimumsubnormalbranch; NaN/inf/zeroexplicit.
RustlowerF64DivideSigned→metadataf64.div, exactexistingrawcontract. NoI128.
20733/tmp/koh-f64-div-first.log TERMINALEXIT0 46/46pass126.290s318unselected:
4nativecases140ROMs +all42softwarehostgroups7252numericobservations(total
adds24boundary+2048sweepdivisions). Added6more nativeexp-52/below/negativezero/
infNaNentries afterfirstrun, now41×4=164ROMs (72division).
72368TERMINALEXIT0/tmp/koh-f64-div-final.log 663/663pass317.607s296unselected;
allLib/IR/lowering +float/softwaremath. The cli_array filter didnotselect
primitive-arrayCLIcases; focusedfollowupnowrunning /tmp/koh-f64-div-arrays.log. Nootherlocalbuild. Docspropose150/565complete415remaining:
F64ArithmeticDebugRelease1/3methodPORT, preserves3originalassertions and
independentbits/CPU/memoryguards. Mixedarithmeticmethodstillopenuntilmul.
Published3a45685CI43999660970stillinprogress; do notpush/canceluntilterminal.
Nextfinishbroadtest/checks/docs/logicalcommit; nofullmigrationclaim.
Read-onlybackend_auditreviewcompleted: noconfirmeddefect; quotient/remainder
bounds, singleRNE, exp-52halfquantum, subnormal→normalpromotion/specialvalues
checked. No importantcoveragegapidentified. Parentownsregressionresults. Workspacealltargets/allfeaturescheckEXIT0(5.46s
existingunusedcubeshelperwarning), separaterustdocEXIT0fourzerocasecrates.
Formatting/diffchecks passed. No semanticproductionchangesafter663run.

36126arrayCLIfollowupTERMINALEXIT0 4/4pass20.102s360unselected. Alllocal
buildsfinished. Readydivisionlogicalcommit; holdpushuntilpublished3a45685CI
43999660970terminal. Newdivisionmethodledger150/565complete415remaining.

2026-11-17 DIRTYbinary64multiplication afterLOCALd3384e8. Publishedhead3a45685
CI43999660970stilllive. Red30347EXIT100 originalUInt128referencecannotresolve.
Exactproductfour32×32partialscombinedlow/high64withcarry; normalizes53bits,
rightshiftwithjamretains3GRSbitsandroundsonceafterunderflowscale. NaN/inf/zero
classificationfirst. Initial57160andsharedpartial13566bothEXIT10016KiB
functionlimit; validcapacitydiagnosticpreserved. SplitordinaryCILprivate
MulPartF64(one32×32→64multiply) andRoundProductF64(productrounding), noABIor
publicAPIchange. Current49566LIVE/tmp/koh-f64-mul-round-helper.log selects55
F64native/softwarehostcases; firstDebugDMGpasses18.641s. Native56entries×4
=224ROMs(52mul), includestiny/half/transition/carry/specials andoriginalfour
legacyarithmeticrows. Hostadds22boundary+2048exponentstratifiedmulchecks;
proposedsourceclosureF64ArithmeticMatchesHost→ledger151/565complete414remain.
Parentownsallfiles/builds; reviewermayREADONLYcurrentdiffagainstHEADd3384e8.
Do notedit/build/test/commit/spawn. Needterminalchecks,review,docsandcommit.

49566TERMINALEXIT0 55/55pass170.946s318unselected. Reviewnoconfirmeddefect;
addedreviewrequestedbothcrosscarryvector(3fffffffffffffff squared) tohostand
nativefixturesAFTERinitialnativepass; now57entries×4=228ROMs(56mul),23+2048
newmanagedmulobservations,total9323. Addedexplicitf64.remrejectionandactual
CILnegativefixture. Nootherproductionchangesafterroundhelpersplit.
FinalbroaderbuildnowLIVE/tmp/koh-f64-mul-final.log includesallLib/IR/lowering,
float/softwaremath andproperlyselected4primitivearrayCLIcases. Needterminal
checks/format/docsandlogicalcommit. Parentownsallfiles/builds.

Nextconversionaudit(readonly): existingToF64flushesF32subnormals; ToF32flushes
normalF64valuesthatshouldbeF32subnormalsandneedsonefinalscaleRNE. ToU64NaN
currentlymax, inconsistentwithdeclaredNaN0policy; ToI64oldcommentassumesCLR
castuniversal, mustuseexplicitKohpolicy. FromU64RNE11discardbitslooksplausible
butneedsboundaries. FromI64minimumnegationmustbeexplicitunchecked. No edits
totheseconversionsinmulunit. conv.r.uncurrentlyalwaysF32; beforeenablingR8
conversionsinspectactualRoslynconv.r.un→conv.r4/r8shape. Preserveu64directF32
2^63+2^39+1→5F000001; neverwidenthealreadyroundedF32forunsignedtodouble,
andneverroundF64firstforunsignedtoF32. ValidunsupportedCILisnotmalformed.

14887TERMINALEXIT0/tmp/koh-f64-mul-final.log676/676pass381.176s292unselected;
all57×4=228F64ROMs(56mul),9323managednumericobservationsincludingreview
doublecarryvector. f64.remactualCILdiagnosticpasses. 93434workspacecheck
EXIT0(5.53s existingunusedcubeshelperwarning), separaterustdocEXIT0fourzero
casecrates. Rustfmt/diff/CSharpierchecks pass. Noactivebuilds. Readylogical
mulcommit, holdpushuntilpublished3a45685CI43999660970terminal. Ledger151/565
complete414remaining; nolegacydefinitionsphysicallyretired.

2026-11-17 DIRTYfloatwidthcasts afterpublishedbc06529. CI44003120165live.
55710redEXIT100RawWidenTinyrealROMheap0vsnonzerosubnormal4.535s. ToF64now
normalizesF32subnormals, preserveszero/inf, quietsNaNs; ToF32roundsonceat
finalnormal/subnormalscale, shift29..53, smaller→signed0, overflow/NaNexplicit.
Runtimekeysf32.toF64ulong(uint),f64.toF32uint(ulong)validated. Lowerfloat_conversion
rawI32/F32orI64/F64asappropriate; F32conv.r8andF64conv.r4nowhelpercalls. Integer
R8castsandremainderstillreject; conv.r.unpolicyuntouched, u64F32proofretained.
11030LIVE/tmp/koh-float-width-first.log4nativecases,firstDebugDMGpass22.755s.
83entries×4=332ROMs,104newwidthcases; missingkeys/wrongsignedreturnandrenamed
helpers. Added2590managedwidthobservations(sourcependingnextrun)total11913.
Proposed4sourceclosures(2PORT2REPLACE):ToF64/ToF32numericandhelpernames;
ledger155/565complete410remaining, float18/22methods59originalcases392asserts.
Parentownsfiles/builds. ReviewREADONLYagainstHEADbc06529; noedits/build/test/
commit/spawn. Needterminalfirstthenfullregression/checks/docs/logicalcommit.

11030TERMINALEXIT0 4/4pass90.133s369unselected. Current48908LIVE
/tmp/koh-float-width-final.log688selectedallLib/IR/lowering,float/softwaremath,
primitivearrays. Afterbuildonlypolishedinvalidconv.r.unfloatoperanddiagnostic
(oldmessagefalselyclaimedF64→F32unsupported) andadded2handCILnegativecases;
needfocusedloweringfollowupafterterminal. No semanticvalid-inputchanges.
ReviewedF32RefParameteroriginal1.5+2.5→4existingFloat32Referencealreadycovers
independentbyrefrequirementinall4modes; closesadditionalPORTwithoutnewcode.
Finalproposedledger156/565complete409remaining; float19/22methods61cases400
assertions(5closures,10originalcases58assertions). Reviewnoconfirmeddefect.

48908TERMINALEXIT0 688/688pass417.433s292unselected; fullfloatwidthcases332
ROMs11913managedobs. 70275handCILinvalidfloatoperand2/2pass0.011s,workspace
checkEXIT0(5.73s existingunusedcubeshelperwarning),rustdocEXIT0fourzerocases.
PRECOMMITAUDITCAUGHTnewlyreachableunsignedint→F32→F64loss: integerR8path
previouslyrejected,butnewF32conv.r8wouldacceptRoslynconv.r.un→conv.r8.
68052redEXIT10022.254s confirmedUnsupportedUnsigned32unexpectedlycompiles;
no claimnativeexactunsignedresultwasobserved. Guardnowallowsconv.r.uninteger
onlywhenfollowedbyexplicitconv.r4(skippingnops); otherwiseactionableprecision
diagnostic. ThiskeepsC#uint→floatRNEdirectandpreventslossyuint→doubleuntil
nextintegerunit. No permanentfeatureretirement; bareconv.r.unFexpressionsare
explicittemporaryunsupportedshape. Addedactualuint32/uint64negativefixtures
and3handCILprecisioncasesplus2floatoperandcases. 66054LIVE
/tmp/koh-float-width-unsigned-final.log selects12nativeconversion/F64cases
plus5handCILcases. Needterminal/checks/docs/reviewbeforecommit.

66054TERMINALEXIT0 17/17pass146.528s426unselected afterprecisionguard. Covers
4F64cases332ROMs+8integer/F32conversioncases+5handCILdiagnostics. Directu64F32
roundingcounterexampleunchanged. Followupreviewnoconfirmedguardhole. 89418
workspacecheck/rustdoccompleted(readterminaloutput); format/Rustfmt/diffpass.
No semanticchangesafter17run. Readylogicalwidthcastcommit; currentCIpublished
bc06529run44003120165stillinprogress, holdpushandbatchnextintegerunit.
Ledger156/565complete409remaining; zero physicallyretired.

2026-11-17 DIRTYinteger↔binary64 afterLOCALf8e99a1(notpushed). Publishedbc06529
CI44003120165wasinprogress. Red37598EXIT1004.540sunsupportedIntegerInput.
Initial86124failedC#buildbecauseeditmatchedToI64F32insteadToI64andremoved
unrelatedhelperblock; restoredfilefromHEAD(readgitshow, NOreset/checkout)
andappliedexactsigedit, diffnowonly45lines. Repaired87610TERMINALEXIT0
9/9pass95.524s434unselected original87entries×4=348ROMs+5handCIL.
Newmetadatai64/u64.toF64, f64.toI32/U32/I64/U64. Source32/Native16sign/zero
extendtoI64; unsignedconv.r.unlooksnextnonNOPconv.r4orconv.r8, selectsF32
directorF64direct; otherbareinternalFshapesdiagnoseexplicitprecision.
Signedconv.r8integer→i64.toF64. F64tointegerclampsat32/64thenexistingnarrow
8/16/Nativecasts;NaN0. FromI64minimumuncheckedmag. ToU64NaNguard. ToI64
saturationwithnobogusuniversalCLRclaim. NewToI32F64/ToU32F64ordinaryCIL
wrappersclamp32aftersafe64helper, noC#compileralgorithms.
Expandednative140entries×4=560ROMs(228integer):26typedinputs+27typedoutputs
+4previousunsupportednowcorrectpositives. Original6signed+4unsigned+3ToInt
rowsincluded. Sixnewkeysmissing/renamed/wrongreturnsignednessprobes. Hostnew
2176numericobservations(16×2bounds+4×128×2input,24×4bounds+4×64×4output),
total14089. Current66931LIVE/tmp/koh-f64-integer-final.log broadLib/IR/
lowering+float/softwaremath+primitivearrayCLI. Parentownsallfiles/builds.
ReviewREADONLYagainstHEADf8e99a1,noedits/build/test/commit/spawn. Proposed
3PORTclosuresledger159/565complete406remain;float22/22methods74cases504asserts.
Needterminal/review/docs/checks/logicalcommit; batchpushwithf8e99afterCIterminal.

66931TERMINALEXIT100197pass1fail505unrun49.906s: generatednegative(nint)-123
CS0119/CS0075syntaxerror; fixed(nint)(-123), value/expectationunchanged.
54647expandedTERMINALEXIT0 22/22pass195.205s373unselected:4F64native560ROMs,
8integer/F32conversionnativecasesand10newintegerhostgroups2176observations.
Read-onlyreviewnoconfirmeddefect. Added2signedshort/native65535→-1boundary
rows AFTERfocusedrun; current142entries×4=568ROMs(236integer), samehost14089.
FinalbroaderretryLIVE/tmp/koh-f64-integer-final-fixed.log703selection, all
Lib/IR/lowering+float/softwaremath+primitivearrays. No otherlocalbuilds.
Publishedbc06529CI44003120165TERMINALSUCCESS1489/1489Rust1535.136szeroskips,
separaterustdoc4zero,.NETfast562pass2UIskips0fail. Primarycompletedlog
/tmp/koh-ci-bc06529-completed.logGH_EXIT0. Pushgatecleared; finishunitchecks/
committhenbatchpushf8e99a1+integerunit. Fullgoalactiveincomplete.

66813TERMINALEXIT0 /tmp/koh-f64-integer-final-fixed.log703/703pass486.131s292
unselected. All142×4=568F64ROMs(236integer),14089managednumericobservations,
oldintegerF32single-roundcounterexampleandbothsignedwraprowscovered.
/tmp/koh-f64-float-disposition-check.json sourcecrosscheck22methods74cases
22COMPLETEmappings, last3rows3+6+4independentinputs. No productionchanges
after703run; formatting/diffchecks pass. Finish59074workspacecheck/doclog
thencommitintegerunit, batchpushwithf8e99a1(previousCIbc06529green).

2026-11-17 DIRTYfloatingremainder afterpublishedccf8d9c. CI44009082004live.
76260redEXIT1004.766s f64.remexplicitunsupported. AddedRemF64ordinaryinteger
CIL:classifynan/inf/zero,magnitudeequality/order,53bitnormalize, binarymodulo
alignmentusingcompare/subtract/shift,max54bitintermediate. Exactresidualencode
noquotientorfloatdivision. RemF32widensbothexactly,RemF64,exactnarrow; remainder
ofbinary32inputsisrepresentableinbinary32(proofrequiredindocs). C#truncating
quotientremainder,notIEEEnearestremainder; primaryspecExpressions12.13.4.
89971firstEXIT0 1/1pass32.022s. 57528native8caseslog
/tmp/koh-float-rem-native.log terminalwhenpolled; allF32andF64specials/normal/
subnormal/extremeexp-gaptested. F32driverupgradedwholeheap/bank2guards while
retainingfirst4CPUfields;Arrayexpectedpayloadasserted. Existingmissingrem
contractnowremoveskeyexplicitlyinsteadassumingdefaultlibraryabsence. New
remhelpersmissing/renamed/wrongreturnsignednessprobes. Sourcegroupremains22/22
ledger159/565complete406remain, no newsourceclosureorphysicalretirement.
Addedhost20+256F32 and20+2048F64observationspendingnextrun,total16433.
No productioneditsafter57528build; sourceonlymetadatarenamepositivesandhost
casesaddedafterbuild, needfinalbroad. Parentownsfiles/builds. Reviewread-only
againstHEADccf8d9c, NOedit/build/test/commit/spawn.

37084TERMINALEXIT0/tmp/koh-float-rem-final.log714/714pass518.748s292unselected.
65F32entries+159F64entries×4=896ROMs(136newrem),16433managednumericobservations.
Wholeheap/bank2F32upgradearraypayloadpassedall4configs. Missing/renamed/wrong
returnsignednessprobespassed. Reviewnoconfirmeddefectorcoveragegap.12829
workspacecheckEXIT0(5.74s existingunusedcubeshelperwarning),separaterustdoc
EXIT0fourzerocasecrates; Rustfmt/CSharpier3files/diffchecks pass. Noactive
localbuilds. Readylogicalremcommit; holdpushwhilepublishedccf8d9cCI
44009082004inprogress. Ledger159unchanged; fullmigrationincomplete.

2026-11-17 DIRTYcheckedcasts afterLOCAL592aea8(rem,notpushed). Published
ccf8d9cCI44009082004nowTERMINALSUCCESS; needcompletedprimarylogbeforeclaims.
18819redEXIT1006.834s conv.ovf.i4unsupported. NewIRRequireRange(I32predicate)
is sideeffecting,typed/verifier/operand/optimizeralias/backendchecked; false
usesexistingarithmeticfault2. Newlower/checked.rs handles20checkedconversion
opcodes, signed/unsigned source interpretation, all8/16/32/64/native16targets.
Sourceextensionusesinputsignforwiden, nottargetsign. Floatguardsmetadata
f32/f64.checkIntbyte(raw,bytewidth,bytesigned), exacttruncatedmagnitudecheck,
rejectNaN/inf; convertwitholduncheckedhelperonlyAFTERguard. Width/flagvalidated.
FirstpositiveF32checks68391passed4/4 26.786s. NewCheckedCastsfixture209rows
(132success77fault)×4=836ROMs. Initial52053/77854/15124faultfail: newbackend
guardassumed4bytes butlegalizedI32predicatescanuse1; adjacentnonzerobitsmade
falsepredicateappeartrue. Fixedroottoactualplace(condition).bytes; noabort
orharnesschange. Removedtemporary/tmpROMdumpcode, retainedperentryfailurelog.
Current95199LIVE/tmp/koh-checked-casts-fixed.log31cases(4native+7IR+20handCIL),
first2nativecasespass209each34.681s/32.930s. Host214rangeprobespendingnextbuild;
addedByteargumentadaptationonlytoSoftFloatHost(noassertlogic). NewIRtestsverify
I32/noresult/definedpredicate, optimizerlocalalias, emitfaultABI. HandCIL20
opcodesvalidatebefore/afteroptandexecution. Parentownsfiles/builds. Reviewer
READONLYagainstHEAD592aea8; noedits/build/test/commit/spawn. Needterminal,
metadata-key/argsdiagnostics,fullregression/checks/docs/logicalcommit. Ledger159
unchangedunlessindependentnewsourcecontractclosureproved. Fullgoalactive.

95199terminalEXIT100: all4nativecasespassed836ROMs +7IRtests, thenhandCIL
firstcasefailedtestinterpretermissingunsignedCompare(notcompilerfailure).
AddedunsignedLess/Greateroracleoperations;10476focused27IR/handCILrunning
/tmp/koh-checked-casts-ir.log. Newmetadataguardsignaturetests(4argument/return
signednessmutations,keymissing,rename)addedafter95199build;mustrunfinal.
Native132success+77faultscenarios/configincludingdiscardedcheckedvalueand
loops: failuresterminatebeforeanyheapwrite,successall8bytes/SP/bankguards.
Float214oracleprobesincludeNaN/inf,truncationfractions,invalidwidth/flag;
sourceByteadapteronlyC#testhostnotcompileralgorithm. Parentownsallfiles/builds.

10476IRrun18/27failedmissingtestinterpreterOr afterunsignedcomparisonsadded;
addedbitwiseOroracle.24944TERMINALEXIT0 27/27pass0.155s407unselected. No
productioneditsafter95199native4passed. FinalbroadnowLIVE
/tmp/koh-checked-casts-final.log explicitchecked_castsfilterincludes4newnative
cases(earlierfloatfilterwouldnot). Newmetadataarity3rename/missing/four
signednessmutationprobes+host214 includedinfinal. Sourcefixture209scenarios
×4=836, success132/fault77each. Read-onlyreviewassignmentagainstHEAD592aea8
nowapplicable; parentownsallfiles/builds. Publishedccf8d9cCI44009082004
TERMINALSUCCESS1516/1516Rust1823.836szeroskips,separaterustdocfourzero,
.NETfast562pass2UI-skips0fail. Primary/tmp/koh-ci-ccf8d9c-completed.logGH_EXIT0.
Pushgatecleared; finishunitthenbatch592aea8+checkedcasts.

Continuation verification:97800 confirmed live via tool handle and process1996120.
All4 checked-cast cases pass (836ROMs), binary32/binary64/comparison/conversion
and primitive-array groups pass so far; managed numeric-oracle cases running.
Rustfmt EXIT0; CSharpier4files EXIT0; diff check clean. New checked-casts.md
and ABI/float/progress/ROADMAP links document fault2 and remaining unsupported
checked arithmetic/EH. Serial Roslyn group covers binary(driver), including
new fixture. Main unrelated battle.png hash unchanged fb818583...c1454.
Do not run another cargo build until97800 terminal. Then all-target/all-feature
check and separate rustdoc; record final log, commit checked casts, batch push
with592aea8, update PR55 checklist and exact-head CI. Publishedccf CI passed
1516Rust +rustdoc +562.NET/2UI skips; current PR body still says pending and
needs refresh. No legacy closure/count change; full migration incomplete.

97800TERMINALEXIT0:747/747selectedpass658.402s292unselected. All836checkedROMs,
214newrangeoracleobservations,total16647numericobservations included.
Workspaceall-target/all-featurecheck9970EXIT0(7.11s,existingunusedcubeshelper
warning);separaterustdocEXIT0fourcrateszerocases. FormattingRustfmt/CSharpier4
filesEXIT0,diffclean. No productionchangesaftertestedbuild. Checkedcasts.md
recordscontract/ABI/fault2/testcommand; floatdocsupdatedtoavoidstalerejections.
Nextcommitcheckedcastsandbatchpushwith592aea8;updatePR55fromlocal/pending
claimsusingexactremoteheadandnewrun. Ledger159/565unchanged;goalactive.

Nextlocalunitafter54ab305:IRconstruction/verifiertestmigration. Newshared
support/sum_loop.rsandtests/ir/construction.rs;sm83.rsadds16/32/64-bitloop
executionbefore/afteropt,6inputs0/1/2/10/257/400includingnative16wrap:36ROMs.
SourceIrBuilderAndVerifierTests5methods/5Assert.Thatcallscrosschecked;matrix
REPLACEall5withtypedconstruction/diagnostics/deterministiccanonicalELF/native
execution. NoIRtextparseradded;requiredRustTerminatorprecludesmissingterm.
Separate7IrRoundTripTestsmethodsremainopen. Ledger164/565401outstanding
provisionallydocumentedpendingfinalrun. Firstfocused11/11pass6.879s; added
wrapcaseandbroaderfullIR/SM83run37651currentlyLIVE/tmp/koh-ir-construction-final.log
454cases,last431passing. RustfmtcheckEXIT0/diffclean. Nootherlocalbuilds.
Published54ab305CI44016382427stillinprogress;donotcancel/restart. Committhis
unitafterterminalandholdfornextlogicalbatch;oldgreenisnotnewheadproof.

37651TERMINALEXIT0:fullIR/SM83targets454/454pass190.582szeroskips. New36
loopROMsincluded. Ledger164/565401outstandingnowverifiedagainstsource5methods/
5assertions. Tests-onlyincrement,no productionchanges. Rustfmt/diffchecksPASS.
Commitunitlocally;published54ab305CI44016382427stillrequiresprimaryterminal
evidencebeforebatchpush. Broad7-methodIRroundtripgroupleftopenexplicitly.

Nextlocalunitafter49cf1e2:remaining7IrRoundTripTests/20assertionsaudited.
6nativeREPLACE/PORT +1DELETEprivateEnum.TryParsetextparsercase(no production
IrParser.Parse callers);game-facingbehaviorretained. NewIrContractsfixture
executesinitialized1/2/3table,mutablecounter1,call/branchresults10/260,switch
11/22/33andushort-pointer-ushortbits0/1/7fff/8000/c000/ffff. Twoentries×4
configs=8ROMs,all60packetwordsbytechecked/SP/frame/CGBbank2guard. Eachroot
assertsmultiplemanagedcodebanks. CLIfixtureBeforeEntrybeforeMain; existing
native_drivercasesassertdefaultrootrowisnotfirstandexecuteexplicitroots
inDebug/ReleaseDLL/EXE. New4width-mismatchIRcasesrejectbeforeemission.
First37756EXIT101testfieldnameerrorcorrectedtype_name;72903EXIT100unsigned
pointer-to-intexpectationunjustified(C#24.5implementation-defined). Fixture
nowexplicitushort/nuintbeforeI32widen,expected0000DFE0unchanged;packetasserts
notweakened,no compiler/emulatorchange.40/40selectedpass91.011s744unselected
/tmp/koh-ir-contracts-final.log22090EXIT0. Bankfollowup80004EXIT0,4/4pass
15.755s412unselected. CSharpier2filespass. Rustfmtfinalassertlinewrapfixed,
thencheckEXIT0. Ledger171/565dispositionscomplete=170nativereplacements+1
obsoleteparserDELETEdecision;394outstanding;zerophysicaldefinitionsretired.
CurrentCI44016382427stillinprogress(.NETfastsuccess,Rustlive). Commitlocal
andbatchwith49cf1e2aftersameCIterminal;nevercancel/restart. No buildsremain.

Nextlocalunitafter2c9c676:livenesstestmigration4methods/15Assert.Thatcalls
primarysourcecrosschecked. Sharedsupport/liveness.rs+9rstestsm83casesexecute
186ROMs:72entry/constants1and7,72diamond(false/highbittrue),42signedloop
bounds0/1/2/10/64/min/-1,16/32/64bitsbefore/afteropt. All8returnbytes/SP/
reservedguardsobserved. Olddiamonddeadarmarithmeticnowreturnedthroughphi;
privateLiveIn/LiveOut/IsTrackablesetsreplacedwithactualbitpreservation,not
newanalysisframework. No compilerproductionchanges. Focused78027EXIT0,9/9
18.892s. Full15782TERMINALEXIT0,467/467208.909szeroskips primarylog
/tmp/koh-liveness-final.log. Rustfmt/diffPASS. Newliveness.md source-by-source
matrix;ledger175/565=174native+1obsoleteparserDELETEdecision,390outstanding,
zerophysicalretirements. Commitlocalandbatch49cf1e2+2c9c676+thisunitafter
CI44016382427terminal;lastRusttestsconfirmedlive/.NETfastsuccess. No local
builds remain. Next high-value candidate:remaining5 CgbHalTests contracts
(realreferencedHALcompile,VRAMbankDMG/CGB,palette0/0and2/3)—inspect complete
source/actualhardwaresemantics/hostsnapshot before implementation. Fullgoal
active/incomplete; no permanentAPIbaseline/bugspec, no C#compilerfallback.

Nextlocalunitafterb405992:CgbHalremaining5methods/8assertionsnativeported;
all7sourceHALmethods/15assertionsnowmappedincludingpriorspeed2. NewCgbHal
fixture6entryroots×4configs=24ROMs,fullBG/OBJ64bytechecks1536CGBbyteobservations,
VRAMbank1+roundtrip,all32validpalettecolors,CPUFF4F/68/69writeorderandDMGzero
writes. AddedEmulatorHostadapteronlyexplicitLCDoff/poison+snapshots/trace;no
compiler/runtime/emulatorproductionchanges. ReviewremovednonphysicalDMGpalette
backing/secondVRAMobservations;DMGsnapshotsomitabsentdevices.MainexecutesPpu
pollswithexplicitLCDonandwaitsVBlankbeforeLCDoff. PrimaryPanDocsrawGitHub
CGB_Registers.md/Palettes.mdjustifyhardwarebits/order/mode3preconditions,not
constructorLCDC91defaults. Focused69862EXIT0,4/4pass18.397s. Pre-refinement
5667EXIT0,26/26pass116.444s. FINAL45870EXIT0,26/26pass117.293s394unselected
/tmp/koh-cgb-hal-mode-final.log,includesothermodegraphics/speed/CLIregressions.
Rustfmt/CSharpier2files/diffPASS. Workspaceallfeaturesnextestdiscovery29166
EXIT0,1592cases,all32newbatchcasesnonignored;existingunusedcubeshelperwarning.
Initiallist-junsupportedcommandcorrectedCARGO_BUILD_JOBS=1(no-runtestclaim).
Ledger180/565=179native+1obsoleteparserDELETEdecision,385outstanding,zero
physicaldefinitionsretired. CI44016382427TERMINALSUCCESSexact54ab305:1560/1560
Rust1715.064szeroskips;rustdoc4crates0cases; .NETfast564total/0fail/2UI-skips.
Downloadedcompletedlog/tmp/koh-ci-54ab305-completed.logGH_EXIT0. Pushgatecleared.
CommitHALthenONEbatchpush49cf1e2+2c9c676+b405992+HAL;updatePRcurrent180/newCI
andverifyexactremotehead/draft/master. No localbuildsremain. Fullgoalactive.

Checkedadd/subincrementREADONLYREVIEWscope:dirtytreeagainstpublishedHEAD
401622ad42c7d867f25967c589e12120ae2c0dd0. Parentownsalledits/builds. New
Rustchecked::arithmeticD6/D7/DA/DBforI32/I64/native16,mixedNative/I32widen
thenchecknativebounds,signedXOR/ANDsignoverflow,unsignedcarry/borrow,existing
RequireRangeeffectguardfault2. No newABI/helper/backendops. Sourcefiles
src/lower/checked.rs+src/lower.rs; testsdriver/checked_arithmetic.rs/new
CheckedArithmeticfixture,sm83.rs/lowering.rs. C#cases153(106success47fault)
×4=612ROMspass64.772s;directCIL20op/typecases×13inputs×beforeafter=520ROMs
plus12invalidshapechecks32/32pass41.967s. Red26010EXIT100D6unsupportedbefore
implementation. Broad79343LIVE/tmp/koh-checked-arithmetic-final.log780cases
last678pass. Do notbuildtesteditcommitspawn. Reviewmath/typing/native16
policy/guardplacement/testoracles/coverageandreportconcretedefectsonly.
Afterbroadbuildbegan,added3-linebyref-specificunsupporteddiagnosticand3
validECMAbyrefshapeunsupportedtests;mustfollowupfocusedafter79343terminal.
ECMA3356thTableIII7rejectsFoperands(notMSAPIcopiedordinary-addtable),permits
selectedmanaged-reference.unarithmetic;weexplicitlydocumentunsupportednot
malformed. Floats/I32I64invalidtests andmulD8D9unsupporteddiagnostics. Docs
checked-arithmetic.md+ABI/progress/ROADMAP/floatlinks updated;ledger180unchanged.
Published401622aCI44020313613live;nevercancel/restart. Parentownsallbuilds.

Reviewbackend_auditFINAL:noconfirmeddefects;signedbitidentities,unsigned
carry/borrow,mixedNative/I32wide/finalrangeguards,effectfulguardand i128oracles
sound. No builds/tests/editsbyreviewer. Scopedoutcheckedmul/managedreferences/EH
explicitunsupported. Parentstillowns79343live780suite;last752passing. Late
3byrefshapechecks+unsupporteddiagnosticneedfollowupafterterminal,thenalltarget
check/rustdoc. Rustfmt/CSharpier1filePASS. Counts153roots=106success47fault
perconfig,612ROMs;directCIL520ROMs.Ledger180unchanged;newfeaturedoesnotclose
unrelatedlegacycases.No oldsourceAdd_Ovf/Sub_Ovf testsfoundinbaseline.

79343TERMINALEXIT0:780/780462.124s416unselected. Afterlatebyrefdiagnostic,
40355TERMINALEXIT0:119/119112.746s519unselected,complete lowering+all4new
C#configurations+20directCILnativecases.40610workspacealltarget/allfeature
checkEXIT0(7.59s,existingunusedcubeshelperwarn);separaterustdocEXIT0fourzero.
Added8byte/sbyte/short/ushortfixturecombinationroots (promotedI32ariththen
checkednarrow),testsonly.63876TERMINALEXIT0:4/4pass66.741s420unselected,
161roots×4=644ROMs(110success51fault/config). No productionchangesafter119.
DirectCIL520ROMsbefore/after=1164arithmeticexecutionsacrosstwoindependent
paths. Wholeheappayload/guards/sourcePDB/KDBGchecksretained. Rustfmt/CSharpier
1filePASS.7887nextestworkspaceallfeaturesdiscoveryEXIT0(13.12s),1633cases,
all41newcasesnonignored(4driver20native12invalid2muldiag3byrefunsupported).
Primaryfinalmathlog/tmp/koh-checked-arithmetic-diagnostics-final.log;latest
smallwidthlog/tmp/koh-checked-arithmetic-small-final.log;fullbroadlog
/tmp/koh-checked-arithmetic-final.log. Read-onlyreviewnoconfirmeddefects.
Commitunitlocally;published401622aCI44020313613stilllive,don'tcancel/restart.
Ledger180/565unchangednewfeaturedoesnotcloseunrelatedsourcecases. Current
unsupportedcheckedmul/managedrefarithmetic/EHexplicit;docs/ABI/ROADMAPupdated.
Nextusefulruntimeunitcheckedmul:smallwidthproductviaI64+checkednarrow,64-bit
fullproducthigh/lowlimbs+signedextensioncheckcouldreuseexistingIRopswithout
newruntimeABI;independentlyprovehighproductcarrybounds/measurebankcapacity.
Do nottreatthiscandidateasimplemented. No localbuildsremain.Parentownsstate.

CheckedmultiplicationREADONLYREVIEWscope:dirtytreeagainstLOCALHEAD4b229d3
(published401622a). Parentownsalledits/builds. NewD8/D9::checked::multiply
usesexactI64product+checkednarrowfornative16/I32(andmixedNative/I32),full
128-bitproductasI64high/lowlimbsforI64using4unsigned32×32partialproducts.
B=2^32:p=a0*b0;t=a1*b0+(p>>32);w1=t&mask,w2=t>>32;u=a0*b1+w1;
lo=(u<<32)|(p&mask);hi=a1*b1+w2+(u>>32). p,t,u fitu64;hifinal≤2^64−2.
Signedhighadjusthi−(b&signMaskA)−(a&signMaskB)mod64;validhigh==ASR(lo,63).
Unsignedvalidhigh==0. ExistingIROps/RequireRange/fault2;no newABI/runtime.
Fileslower/checked.rs+lower.rs, testschecked_arithmeticdriver/fixture,sm83/
lowering.rs. Initial90720EXIT100D8unsupported;50543first4/4pass126.641s
276roots/config=1104ROMs. 91956directCIL/diagnostics50/50pass66.748s:all6
ops×5operandshapes,870ROMsbefore/afteropt,rootnear-sqrt/32bitlimb/min−1/high
wordnonzero-lowwordzero cases;u128unsigned/i128signedindependentoracles.
FinalbroadnowLIVE/tmp/koh-checked-mul-final.log includesfunctionsizeobservation
addedafterfirst4pass;needterminal. Do notbuild/test/edit/commit/spawn. Reviewer
checkpartialcarrybounds,signextensionvalidity,narrow/mixedtypes,oracles,code
capacity,untrustedinputdiagnostics. Parentownsallstate. CI401622a44020313613
nowterminalsuccess;downloadprimarylogthenbatch4b229d3+thisunitaftervalidation.
Fullgoalactiveledger180unchanged. No usefulfeaturesretired.Codecommentsnone.

28442TERMINALEXIT0:799/799pass548.424s416excluded. All4arithmeticconfigcases
pass1104ROMs;observedlargestmanagedfunction3616bytesinall4,within16KiB.
No productioneditsafterbroadbuild. Addedtest-onlywideproductproperties to
existingloweringtestinterpreter (Multiply/And/unsignedshift, explicitErr2for
RequireRange;unexpectedops/panicsremainfailures). 10rstestcases×512vectors
×beforeafter=10240evaluations signedi128/unsignedu128 independent oracle.
25256TERMINALEXIT0:full lowering111/111pass6.722szeroskips. Random/full,zero/
one,halfwidthbounded,sign-bit×small and−1/max factorscovervalid/fault paths.
Existingvalidinterpretercallersuse.expect; onlyexplicitguardfalseyieldsErr2.
Latechangeslimitedtests/lowering.rs;parentrequestreviewofthatdelta only,no
newmathcode. Reviewerread-only no build/test/edit/commit/spawn. Needworkspace
check/rustdoc/discovery/formatthencommitandbatchpushwith4b229d3. CI401622a
44020313613confirmedterminalSUCCESS1592/1592Rust1848.444szeroskips,4rustdoc
zerocases,.NETfast562pass2UIskips0fail;primarylog/tmp/koh-ci-401622a-completed.log
GH_EXIT0(pushgatecleared). Ledger180unchanged, fullgoalactive.No localbuilds.

CheckedMulfinalverification:64158workspacealltarget/allfeaturecheckEXIT0
6.16s(existingunusedcubeshelperwarn).51513rustdocEXIT0fourzerocasesand
nextestworkspaceallfeaturesdiscoveryEXIT0(8.26s),1659cases,all67arith/diag
casesunignored(4driver30native18invalid2positive10properties3byref).
Rustfmt/CSharpier1file/diffPASS. ReviewerlateharnessFINALnoconcreteissues;
onlyfalseRequireRangebecomesErr2,unknownops/typefaults/nonterminationfail,
oraclewidth/sign/productsound. No productioneditsafter799broadbuild.
799/799548.424s416excluded+111/111lowering6.722szeroskips,10240pureIR
evaluations,1104realC#ROMs+870directCILROMs=1974arithmeticexecutions;
newMulspecific460Roslyn+350direct=810. SourceRows276=184success92fault/config.
Largestmanagedfunctionobserved3616bytesall4within16KiB;notcomparativeperf.
No runtimehelpers/newABI/IRtypeadded. Docschecked-multiplication.mdproof/
limits/evidence;checked-arithmeticandABI/progress/ROADMAPupdated. Ledger180
unchanged,385outstanding,zero physicallyretired. Committhenbatch4b229d3+Mul
aftergreenpublished401622aCI44020313613:1592Rust+doc+.NET562/2UIskips.
Primary/tmp/koh-ci-401622a-completed.logGH_EXIT0. Verifyremotehead/newCI/PR
draft/master,refreshconcisechecklistandvault.No localbuilds/reviewsremain.

## Scalar optimizer contract increment

Comparison/conversion groups:11 source methods,43 assertion sites replaced
by70 rstest cases,920 ROM executions and44,440 independent IR evaluations.
Full lowering+sm83 targets310/310 pass510.277s zero skips, EXIT0 verified
from terminal session64981 and /tmp/koh-opt-contracts-final.log. Tests only;
no production optimizer changes or old graph-rewrite claims. Ledger191/565
(190native+1DELETE),374 outstanding,zero physically removed. Source matrix
totals corrected105PORT/184REPLACE/1DELETE. CI44027838391 remains in_progress
on published49ac462; do not cancel. Commit locally, batch push after CI.

## Scene-flow acceptance increment

Actual Game.Run fixture now checks nine ordered lifecycle events, constructor
value1234 across three scenes and frame4, D/R×DMG/CGB. Test-only host packet
mode; no emulator production/API/compiler changes. Full neighboring selection
16/16 pass112.176s,412excluded,EXIT0; primary /tmp/koh-scene-flow-regression.log.
CSharpier two files/Rustfmt/diff checks pass. Ledger192/565 complete (191native
+1DELETE),373outstanding. Prior55ff6f2 remains local; batch both after published
49ac462 CI44027838391 terminal. Never cancel current run; .NET job succeeded.

## Leaf interrupt runtime increment verified locally

Base4065231, separate f2836f6 fixes emulator RETI immediateIME (EI staysdelayed).
Whole emulator175/175 pass,zero skips afterformat; primary
/tmp/koh-reti-emulator-final-{build,tests}.log. Rust trace exposed matchingall
regs/SP/PC butfalseIME; do notadaptcompileroutputtobugs. No emulatorrewrite.

NativeIRQ unit validates5vectors/staticvoid metadata, roots allsuppliedassemblies,
allowsinteger/static leafIR, rejects calls/heap/indirectMMIO/EI/reentrant/captured
handlers. ROM0 nonbankedbody and wrappers saveAFBCDEHL,CALL,restore,RETI.
Every main guard reserves max handler Layout.size+16; hardwarePC2+saved8+CALL2
+scratch4. TypedIRunchanged. Existingruntimehelpers stack-local; no bank shadow.

Native tests6cases:20ROMs (D/R×DMG/CGB,2/3/5 realVBlankcounts/periods,5software
vectors,mainbanks257+,deepstackCPUrestore atfailingguard beforetrap) and28
metadata/body/entry diagnostics withpaniccapture. Oldpure recursivefunction
optimizedtoloop; postcallNOPeffectforcesactualstackdepth. No weakenedIRQIME
assertion. PendingContracts oldblanketrejectionreplacedbyinvalidreturntest.
Broad372/372 pass616.386s419excluded beforelate metadata/adapter refinements;
final200/200 pass63.640s427excluded,EXIT0. Primary/tmp/koh-interrupt-final.log,
/tmp/koh-interrupt-late-final.log. Workspaceallfeatures/alltargetscheck,
separaterustdocfourzero,fmt/CSharpier/diffpass. Discovery1739,all6IRQunignored.
Existingunusedcubeshelperwarning. Independentread-onlyreview no confirmeddefect;
reviewdidnotexecute tests. Docsinterrupts.md and reti-reference-correction.md.

Ledger198/565 (197native+1obsoleteparserDELETE),367outstanding,zerophysically
removed. Fullmigrationstillincomplete;broaderIRQ/runtime/banks/games/debug/SDK/
release/licensing/performance/hostacceptance andfinalC#compilerremoval remain.
No localbuilds/reviews live. Published4065231 CI44032064732 stillin_progress
(Rusttests; .NETjobpassed), do notcancel/restart. Commitnativeunitthenbatch with
f2836f6 afterCIterminal. Preserveunrelatedmaincheckoutdirtybattle.png.

## Safe IRQ MMIO follow-up under verification

Published e0b05d1 after4065231 CI44032064732 terminalsuccess1733/1733 Rust,
zero skips,docs/.NET562pass2skip; primary/tmp/koh-ci-4065231-completed.log GH0.
PushEXIT0, newexactheadCI44037748828 running. PRpublishedledger198updated.
Dirty newunit allows constant native byte IRQ IO accesses FF00..FF7F/FFFF;
forbids writes FF46/4D/4F/50/55/70 (DMA/speed/bank/boot). Reads do notswitchbanks.
Staticmemorypolicyunchanged,wide/dynamic/RAM/ROM/HRAM address accessrejected.
Main ownsimplementation/tests/docs. Reviewer read-only owns policy/alias/width
analysis, no builds/edits. Current35395 runs compilerlib+IRQ+scene+tileset
selection /tmp/koh-irq-mmio-final.log; don'tbuildconcurrently. Initial6/6passes
beforeextra targetednegative andIE-mask cases;red /tmp/koh-irq-mmio-red.log.

## IRQ MMIO follow-up ready to commit

Current-input207/207 pass94.112s420excluded; strongerlargest-handlerboundary
6/6pass38.159s428excluded,EXIT0. Primary/tmp/koh-irq-mmio-final.log,
/tmp/koh-irq-mmio-boundary-final.log. 24ROMs and52negative variants; ledger198
unchanged,367outstanding. Nativeconstant byteIOonly; sixcontrolwritesblocked,
wide/dynamic/out-of-range/wrapped-bank diagnostics. IE0masking executes all5
vectors; VBlankSCY3*count/SCXEE/BGPE0|count packets verified. Tightguard now
injects largerVBlankbody, fullCPU/IMErestore thenstacktrap1. Independentread-only
reviewnoconfirmeddefects, didnotrun tests. Workspaceallfeatures/alltargetscheck
andrustdoc4zero,format/diffpass;preexistingunusedcubeshelperwarning. No builds
or reviewers live. Committhisunit;holdlocalwhilepublished e0b05d1 CI44037748828
runs;nevercancel/restartfornewcommits. Previous4065231 CI44032064732success
1733/1733,zero skips;log/tmp/koh-ci-4065231-completed.log GH0. e0b05d1 batch
pushedEXIT0 andPRdraft/master/currentheadverified. Fullmigrationstillincomplete.

## IRQ helper-call unit under implementation/review

Base local1652f39(MMIOverifiedcommitted),published e0b05d1; itsCI44037748828 now
terminalsuccess(freshstatus),lognotdownloaded yet. Dirtyhelperunit addsnative
interrupts::Plan: closed scalar callgraph validation, explicit topological
stackbudget/cyclerejection, shared-main/IRQ member detection. AllIRQreachable
functions useordinarybankedABI butplacementROM0. Farcall/return skipMBCwrites
by targetCPUaddress<4000,notbanknumber. Wrappersuseordinaryfarcallcookie4;
IRQactiveWRAM0byte0→1→0 lets shared code's guards omit globalreserve withinIRQ
but reserve it frommain. IRQ-onlyfunctions baseguards0; allothercodeglobalreserve.
Planbudget rootprefix16(hardware2+saved8+cookie4+CALL2),functionframe+max(temp4,
callargs+ret+cookie4+CALL2+childneed). __koh_irq_stack_reserve absoluteELFsymbol
exposesbudget. Main ownsallchanges/tests/docs; reviewer read-onlyABI/graph/bank
scope, no builds/edits. Current22217 runs11cases /tmp/koh-irq-calls-bank-zero.log;
previous11/11passed39.133s beforeNative16fullproof andBankZeroaddition. Five
rstestbudgetcases include16/32/64fit and4×1024callarg-onlyoverflow. Nativefixture
shared16/32/64helpers,bytewrap/full64checksum,52+mutantdiagnostics plushelpercycle.
Mainhighbank257+; newBankZero forcesmainIRROM0 andselectsphysicalMBC5bank0.
Testoutput C008..C027 reservedwithELFsection toavoidstaticstoragealias. Adapter
comparesallCPUregs+IME+bothMBC5bankregisters atRETI, includingrealVBlank2/3/5;
tightguardprobe boundrelativecompiledreserve. No localbuildconcurrency allowed.

## IRQ helper calls verified locally; ready for commit/batch

Broad384/384 pass892.703s(2slowSDK,440excluded) includesall4 actual2048 SDK
Debug/Release×DMG/CGB cases; primary/tmp/koh-irq-calls-final.log EXIT0. Latest
current11/11pass45.960s621excluded withmatching app/referencePDBs,source maps,
KDBG≤255success/>255expliciterror, strongsharedmainreserveinvariant,high-bit
64-bitwrap/full16native/bytewrap/referencedNegF32bithelper. Primary
/tmp/koh-irq-calls-scoped-final.log EXIT0. New5rstest budgetcases+6integration
cases discoverunignored;workspacecount1744. Workspaceallfeatures/alltargets
check+separaterustdocfourzero pass. Existingunusedcubeshelperwarning. No local
builds/reviewslive. 28ROMs/54negativevariants; ledger198/565unchanged367outstanding.
Read-onlyindependentreviewno confirmedABI/graphdefect;didnotrun tests.

Debugaliasopeningbrace24vsactualreturn25/26 inSoftFloat.NegF32 made anexactline
assertionwrong;changedtoscopedcode-range/sourcepathmapping,notretainedelidedNOP.
Failure/tmp/koh-irq-calls-pdb-diagnostic.log. KDBGv1writer+nativeunwinder reject
callerbanks>255 confirmedprimarysources;physicalexecutionhighbankproofisnot
large-bankdebugproof. No debuggerrewrite or KDBGformatmigration inthisunit.
Metadata diagnosticnames nowformattedonlyonfailure,no unboundednamecache.

Commitnativehelperunitthenbatchwith1652f39. Published e0b05d1 CI44037748828
terminalsuccessconfirmedprimarylog/tmp/koh-ci-e0b05d1-completed.log GH_EXIT0:
1739/1739Rust1846.886s,zero skips,4rustdoczero,.NET564pass/2UIskips/0fail.
Nevercancel/restartCIforobservationtimeout. Fullmigrationactive/incomplete;
constructedgenericlayouts/statics/boxing/bankeddata/debug/SDK/releases/perf/
hostfinalacceptance/test367closures/legacydeletionstillrequired. Maincheckout
battle.pngpreexistingdirtyhashunchangedfb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454.

## Generic rectangular arguments, locally verified

Published head `5f59aa3` includes `1652f39`; PR55 remains draft against master.
The description reflects bounded IRQ helpers. Exact-head CI44056288366 is still
running, with .NET fast tests successful. Do not cancel/restart for a new commit.
The full migration remains incomplete.

Rectangular-array generic arguments now retain the original element assembly,
shape and instantiation budget. Typed memory operations use existing FieldStorage
for array reference cells; the verifier still requires matching typed cells.
The actual fixture checks primitive/local packed3-byte/foreign packed12-byte
arrays, colliding metadata rows, aliases, signed32/64-bit fields, cross-DLL calls,
recursion, null defaults, byref swaps/clears and rank3 diagnostics in D/R × DMG/CGB.
The first red log is `/tmp/koh-generic-rectangle-red.log` (binding); the second
is `/tmp/koh-generic-rectangle-green.log` (initobj storage).

Final `/tmp/koh-generic-rectangle-regression.log`:544/544 passed in741.555s,
400 excluded, EXIT0. This selects all compiler-library/lowering/SM83 cases plus
generic/rectangle drivers, not the full workspace. Workspace all-feature/all-target
check and separate rustdoc pass (four crates, zero doctests). The existing unused
cube build_host warning remains. See `docs/rust-compiler/generic-rectangles.md`.

Ledger198/565 is unchanged,367 outstanding. Constructed generic declaring
layouts/statics, boxing, banked data, final product/debug/release/performance/host
acceptance and legacy deletion remain required. Commit this increment, then batch
push after published CI is terminal. No delegates are active.

## Constructed generic types in progress

Main owns all uncommitted changes; base `509d6c7` is now pushed. Previous-head
CI44056288366 completed1744/1744 Rust tests in2545.497s, zero skips; primary
`/tmp/koh-ci-5f59aa3-completed.log`, GH_EXIT0. New CI44063160385 is running for
509d6c7. Do not cancel/restart it. The full migration remains incomplete.

New `types.rs` interns closed type identities(0xf9), fields(0xf8); existing
method instances(0xfb) retain a declaring-type identity. Signature tag3 is an
internal bound-type reference only; the untrusted signature parser rejects it.
Generic layouts substitute field types with their original assembly scopes;
static fields and initializer states are separate per closed owner. Generic
initializer guards(0xf7) use the existing calling convention and WRAM limits.
Method/local/IL binding carries method and declaring arguments separately.
Caches containing closed identities reset between compilations for determinism.
Type constraints now use the existing validation path, with bounded recursion;
pointer/byref/void type arguments reject even if the callee never uses T.

New fixture ClosedTypes and driver/generic_types.rs execute generic packed
structs, objects, nested reference-bearing values and isolated static cctors
across D/R × DMG/CGB. Initial positive12 ROMs pass4/4; expanded selection
208/208 passes70.918s, EXIT0 (200 library +4 new +4 existing generic cases).
The pointer-argument mutation first compiled an object incorrectly, then rejects;
primary `/tmp/koh-closed-types-constraints-red.log` and regression log.
Current session18161 runs added struct constructors and new-T construction in
declaring generic contexts; expect evidence in
`/tmp/koh-closed-types-construction-red.log`. Only main may build in shared
`/tmp/koh-header-target`; reviewers are read-only and must not run builds/tests.

Review scope: correctness of identity/scope binding, per-instantiation storage
and initialization, cache lifetime, verifier trust boundary and malformed-input
diagnostics. Report concrete reproducible issues; no edits. Constructed generic
inheritance/virtual/interface dispatch and variance remain unfinished; do not
mistake explicit unsupported diagnostics for implemented support. Generic new-T
construction context is the next known fix pending the red fixture. No legacy
test dispositions or source definitions are claimed removed by this increment.

## Closed-type final checkpoint

Supersedes the pending construction run above: final selected221/221 pass83.682s,
430 excluded, /tmp/koh-closed-types-owned-final.log. Bound callback owner contexts,
composed substitution bounds, zero-bound array canonical identity and generic
explicit-layout rejection are included. Workspace check and separate rustdoc
pass. Published predecessor509d6c7 CI44063160385 finished successfully1746/1746
Rust, zero skipped; current uncommitted increment has not run hosted CI.
Next: commit/push this unit, update PR55, then native SDK/game acceptance.
No additional source dispositions;198/565 complete,367 outstanding.

Post-commit product regression: all four real2048 SDK game cases passed in
228.987s, exit0 (`/tmp/koh-closed-sdk-game.log`),24 other SDK cases excluded.
Debug/Release × DMG/CGB builds execute real boot, title/Start, eight moves, exact
board/score/spawn transitions, glyph/tile/pixel checks and mode3 safety. This is
scene-based2048 acceptance, not every sample or arbitrary-PC debugger acceptance.

## Original 2048 closure

All16 legacy sample methods/32 assertion sites now map to eight new native
rstest cases; final12/12 including four old board/stack regressions pass125.675s
exit0, /tmp/koh-original-2048-final.log. Actual SDK boot/input/score/spawn/HUD/OAM/
pixels, full header/logo/checksums and explicit board edge cases are asserted.
Ledger214/565 complete (213 native+1 DELETE),351 outstanding; no definitions
physically removed. See docs/rust-compiler/original-2048.md. No production
compiler/emulator changes. Pending discovery/commit; hosted predecessor CI
44077140986 is still running, do not cancel by pushing pending docs early.

## Showcase timeline closure

Five additional source methods/24 assertion sites (36 expanded) have complete
PORT replacements; all nine graphics-demo methods are closed. Four existing
rstest cases now check every coherent update through79, exact OAM and both
DMG/CGB palette themes. Final4/4 pass47.303s,exit0,440 excluded; primary
/tmp/koh-showcase-timeline-final.log. Ledger219/565 complete (218 native+1 DELETE),
346 outstanding,zero physically removed. No new production game/emulator code.
Docs/showcase-timeline.md records coherence limits. Next: remaining cube sample
source dispositions, then broader compiler groups/product gaps. Published
3dbd750 CI44077140986 remains active; pending local commits must not cancel it.

## Final cube budget checkpoint

Restored Release full-frame/DMG first capture250; old native450 was insufficient.
The initial250 capture was blank. Final changes are in-place multi-byte memory
shifts, conservative argument-free literal-return call folding with typed no-op
conversion proofs, and Tilemap.Clear using existing Mem.Fill for exactly1024 bytes.
The attempted arithmetic rewrite is absent: it exceeded the existing scratch
budget and new SP sweeps reproduced reserved-memory corruption. Eight optimizer
proof cases include signed widening; three SP sweeps cover672 ROM entries. Six
shift probes measure complete values and cycles (64-bit1<<63:55624 old,15304 new).

Final350/350 selection passes546.837s,exit0,594 excluded, primary
/tmp/koh-cube-budget-width-final.log. Includes219 library,76 cube cases, relevant
arithmetic/memory, both live source-debugger cases and four original2048 SDK
configurations. Check/all-targets, formatting, separate rustdoc pass. Discovery
1788,all17 new cases unignored. Read-only cube_budget_review found no blocking
issue; no edits/builds by reviewer. No local builds live. Source matrix closes
three legacy cube methods/eight sites/16 expanded assertions. All45 integration
methods now have native replacements. Ledger222/565 complete (221 native+1
obsolete parser DELETE),343 outstanding,zero physically removed. Full goal remains
incomplete. Docs cube-phase-sweep.md,ir.md,sm83-abi.md hold rationale/evidence.

Hosted3dbd750 CI44077140986 ended cancelled at the60-minute job limit; check-run
annotation explicitly confirms maximum1h0m0s. .NET fast job succeeded. Rust logs
were not uploaded (gh log reports log not found), so no Rust result is claimed.
Native PR-check job limit changes to90 in a separate commit; two Linux jobs and
all required tests remain. Safe to push queued batch after commits; never merge.

## Compare/branch closure

Seven legacy methods/nine sites/26 expanded assertions now have native
replacements (3 PORT/4 REPLACE). Four rstest cases execute standard CIL with
single/shared/mixed predicates and intervening MMIO. Retains18 source vectors
and42/42→11,42/43→99; extends64-bit high-only cases.246 semantic ROMs plus12
timing calls. Exact old slots/registers/byte counts retired explicitly; initial
8192-cycle estimate was not a justified contract. Full checked-call ceiling is
one DMG hardware frame70224; measured4360..14128. No production source changed.
Final4/4 pass14.056s,exit0,/tmp/koh-compare-branch-consumers-final.log; prior adjacent
45/45 pass78.238s. Ledger229/565 complete (228 native+1 DELETE),336 outstanding.
CI44088642289 for publishedc08a609 remains active; queue this commit until it
finishes rather than cancelling it. Next substantive runtime gap is constructed
generic inheritance/dispatch or banked data; compiler audit groups also remain.

## CI bank-stop regression fix

CI44088642289 for c08a609 ended failure:1070 Rust passes then Debug SceneFlow
packet mismatch, .NET fast job success. Completed primary log
/tmp/koh-ci-c08-completed.log,GH_EXIT0. Reproduced local Debug in both modes;
wrong observation point, not incorrect scene state. Bare16-bit PC matched a
function's window address in the wrong bank. New independent ELF/ROM probes
first failed in source banks2/257 for both modes before publishing42.

EmulatorHost test protocol now requires bank:address. Shared execute_mode and
all direct SDK/DMA callers updated. Host handles ROM0/MBC1 mode and9-bit MBC5
selected banks; production emulator/debugger unchanged. Original scene packet
assertions preserved. Final22/22 (scene4, bank probes4, banked calls2, DMA4, SDK
float4, SDK incremental2) pass239.993s,exit0;458 cases excluded.
Primary /tmp/koh-stop-bank-final.log. No legacy
disposition credit; current229/565 complete,336 outstanding. Safe to push queued
ed20be1 plus this fix after commit; previous CI is terminal.
