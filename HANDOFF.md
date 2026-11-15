# PR #55 handoff

Packaging preparation follows7fe8c13 and is independent of live17148:
Rust CI uploads/builds compiler; release staging adds target-specific compiler
and packaged C# build/emulator smoke step; old Python SDK verifier retired after
its9 phases move to Rust; notices/docs updated. Both YAMLs parse and8 release Bash
steps pass bash-n. No packaged runtime/hosted proof yet. Add license copying to
Build.Tasks output only after full-run production freeze ends, then finish
packaging validation/review/commit. Do not count prepared workflow as executed.
Read-only packaging review: banked_calls_review owns inspection of workflow,
license-notice and verifier-retirement diff only; no edits/builds/tests/commits.
Root owns all mutation and the live774 run.
Packaging review completed with no concrete defects; no runtime tests executed.

## Current SDK unit

SDK native-boundary unit is PUSHED7fe8c13b2aa4b41b5b9be9bf4439f4afacd0782f.
Root owns all writes/builds. Post-commit SDK3/3 passed152.483s,0skips,exit0,
`/tmp/koh-sdk-postcommit.log`, including final timestamp-isolation strengthening.
Full774 workspace/allfeatures is LIVE17148 in `/tmp/koh-header-target`,
`/tmp/koh-sdk-workspace.log`; doctests scheduled sequentially after it succeeds.
Do not duplicate builds or mutate production Rust/GameBoy/SDK source while this
run's later SDK fixtures may rebuild host tools from the live source. Pure CI/docs
changes are independent. Poll handle17148 and actual completed logs before reuse.
Initial nativeSDK2/2 passed259.720s cold;
expanded flag/ref/failure2/2 passed90.914s; assets2/2 passed98.566s. Subsequent
output-alias test exposed actual DLL replacement via symlinked parent
(`/tmp/koh-sdk-alias-red.log`,exit100). New canonical path-component resolution
passed final verification. Debug-toggle red exposed staleKDBG; cleanup after
successful no-debug link fixes it. Relative-output SDK run initially failed
(`ROM not found`) and now passes after absolute-path forwarding,17.955s,
`/tmp/koh-sdk-relative-run-green.log`. The three-case target includes default
SDK dotnet-run/headless real graphics and all asset/ref/flag/failure cases.

SDK production changes: Task now uses native processes/ArgumentList, no compiler
or object algorithms; Build.Tasks references neither Compiler nor Native bridge.
Its private host-tool target builds Rust compiler/linker into separate
target/compiler-msbuild/<config>. SDK persists canonical .o, tracks tool paths/
timestamps, removes frontend/backend registry options, retains tile-sheet tools,
and builds emulator only on dotnet run. PDB/source-debug acceptance remains open.
Synthetic PNG fixtures have real CRC/Adler checksums; Rust SDK tests drive actual
source generation plus ROM data consumption and five-color errors. These map
both original tile-sheet methods. Matrix now26/565 complete,539 outstanding,
none physically retired. Exact16-byte WRAM payload replaces brittle generated
source formatting checks; Some.Ns.Art consumption proves configurable namespace.

Actual native SDK Release build of graphics sample passed, ROM65536bytes.
Actual dotnet run1200frames exited0, screenshot/tmp/koh-native-sdk-gfx.png viewed
(checker/orbs/SCORE833); mode3report0. These are workflow evidence, not performance
comparisons or full game/source-debug acceptance. No sample assets modified.

Read-only review completed: banked_calls_review found no concrete defects in
Task/targets, native build, test assertions or path identity. No reviewer builds
or edits. Root owns landing and final logs. Release packaging, older SDK script migration,
remaining games and full workspace rerun still required; never weaken CI filters.

Important hosted evidence:3a9f3a1 native43728226099 PASSED Linux771/771679.078s,
macOS771/771614.780s, Windows769/769946.295s,0skips. Managed43728226133 FAILED6
legacy compiler cases on both hosts: DMG scattered run61-frame budget;2048v2
title/play/slide; FlushRun fused addressing and register-resident phi structural
expectations; JRPG walking/callback and dialogue screenshot startup. Do NOT claim
managed CI green, drop gameplay, weaken assertions, or restore C# fallback. Source
SDK changed, class/other-game support still pending. Inspect/fix native contracts
and port coverage before retirement. Native SDK full solution build is not proven.

Local native SDK `dotnet build Koh.Ci.slnf -c Release -m:1` FINISHED exit1,
0warnings,5errors,33.94s (`/tmp/koh-sdk-managed-solution.log`). Actual five failed
projects:2048v2 and JRPG reject reference classes (`Aggregate must be a sealed
framework System.ValueType`); 3D double-buffered rejects `di` in
Cgb.TryEnableDoubleSpeed; full-frame/racing-beam reject `alloc` in
Surface.Initialize. Original2048 built131072-byte ROM; gfx built65536-byte ROM.
Do not remove these projects from CI or count build-only proof as gameplay.
These are3 root missing capabilities, not5 unrelated blockers. Current SDK
diagnostics should improve alongside implementing the capabilities. No fallback.

## Goal and ownership

Complete the full Rust CIL-to-SM83 compiler migration in this one draft PR against
master. Never merge, open another migration PR, shrink acceptance, or declare
completion for an increment. Read the active goal attachment for all gates.
Existing public APIs are evidence, not a baseline or convention. Preserve useful
game-authoring behavior; redesign contracts when justified and migrate consumers.

Root owns all edits and builds. No parallel editor. The read-only reviewer
`banked_calls_review` finished; no concrete production defect was found in the
optimization changes. Review did identify lost chronology in the HUD assertion,
which is now fixed locally and passed its final focused run.

Review follow-up ownership: `banked_calls_review` inspects only the new Clear
correction, map fixture/budget and final showcase assertions. No builds, edits,
tests or commits by that reviewer; root owns verification and landing.
Follow-up review completed: no concrete defects found; no builds/tests run.
Discovery confirmed771total,594compiler,0ignored, including both map cases.

## Checkout and commits

Worktree `/home/minorum/projekty/koh-rust-compiler`, branch
`feat/rust-cil-compiler`. Pushed HEAD `b9be7eb071332d20844e38d7f0725ec4de2bba34`.

- `bb629b6`: initializer fast check and register-pointer arithmetic selection.
- `b9be7eb`: cross-block forwarding of unaliased locals.
- `44c7ff7`: raw linker golden fixtures, all original byte assertions retained.
- `5a4745a`: linker-owned canonical boot logo.
- `9687199`: banked managed calls, including banks 257/258.

Normal push succeeded this turn. If DNS fails later, use a fresh DNS answer for
per-command `http.curloptResolve`; never change host settings or disable TLS.
The main checkout's unrelated battle.png stays untouched (SHA256
`fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`).

## Verification state

Root session69931 is TERMINAL exit0: full workspace/all-features nextest,
769/769 passed,0skips,1997.889s, `/tmp/koh-showcase-workspace.log`.
No local test/build handle remains live. Use `/tmp/koh-header-target` serially;
avoid concurrent Roslyn processes rebuilding shared GameBoy outputs.

That full binary predates chronological HUD handling, the two full-map cases and
the production LCD-on Clear correction. Final focused showcase/sprite8/8 passed
56.058s (`/tmp/koh-presentation-final-focused.log`); full-map/text6/6 passed48.744s
(`/tmp/koh-map-text-final.log`); separate workspace doctests passed(no cases).
Both targeted runs and doctests exited0. Do not claim a full771-case rerun.

Discovery JSON `/tmp/koh-presentation-discovery.json` was produced successfully
with `CARGO_BUILD_JOBS=1 cargo nextest list --workspace --all-features
--target-dir /tmp/koh-header-target --message-format json` (list has no-j flag).
Final follow-up after commit: check formatting/staged-tree identity and push the
presentation unit, then update PR current evidence/checks in place.

Environment: `KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet`,
`RUSTUP_TOOLCHAIN=stable`, `/usr/bin/cargo`, serial `-j1`. Completed focused logs:
arithmetic 9/9 `/tmp/koh-pointer-arithmetic-semantics.log`; initializers 2/2
`/tmp/koh-init-fastpath-semantics.log`; IR 190/190 `/tmp/koh-crossblock-ir.log`;
byref execution 2/2 `/tmp/koh-crossblock-alias-execution.log`.

## Uncommitted presentation unit

`MapWriter.cs` reacquires fresh vblank when copy/commit starts late; zero-length
copies do not wait. Actual unchanged graphics sample source is compiled by a
plain Roslyn fixture, native compiler, canonical ELF and Rust linker, then run
through actual Koh DMG/CGB boot ROMs in the existing emulator. Stronger checks
cover score increments, quarter-turn sprite geometry, exact checker pixels,
map/OAM bytes and mode-3 safety; corrupted CGB logo must fail boot.
Initial strong 4/4 pass: `/tmp/koh-flush-resync-first.log`, exit0,40.883s.
Diagnostic tracing was removed before the current full run.

Changed files: nextest timeout override, driver/support tests, Showcase and
ShowcaseHost fixtures, private MapWriter timing fix, presentation/progress docs.
New `MapTransfers` fixture tests CGB full BG/window maps via Fill and Clear in
Debug/Release, with exact 8KiB VRAM and untouched bank1/OAM checks. Source inspection
found `Bg.Clear` writes VRAM with LCD on. The first red run hit2m instructions;
marker03 proved it was in second-map setup after completing the first. A special
bounded8m map mode (all existing bounds unchanged) exposed355 mode3 writes.
`/tmp/koh-full-map-hardware-red.log`exit100 is the real hardware red evidence.
Clear now fills shadow/marks full dirty with LCD on, immediate copy/reset state
with LCD off. All-map2/2 passed18.720s before final6/6. Existing stale class-level
timing assertions were removed; presentation.md holds measured decisions/limits.
Full-size transfers are checked in these scenarios, but arbitrary start phases,
IRQs/reentrancy and exact palette/score phase relations remain unproven.

## CI and PR

PR55 remains draft/master; short live checklist updated in place, ~45% is rough
scope estimate,100% means every acceptance criterion. Current-head hosted runs:
Rust 43726054037 and managed 43726053987 were live at last poll.

Predecessor44c7ff7: managed43723302385 success. Rust43723302350 success; actual
job logs Linux756/756346.993s (132839735269), macOS756/756448.466s (132839735501),
Windows754/754694.396s (132839735503), zero skipped; separate doctests no cases.
Two Unix assembler cases explain the platform count difference. Native SDK
verification in that workflow still means linker integration, not Rust compiler.

Prior local banked regression755/7550skips2581.751s
`/tmp/koh-rombank-workspace.log`; header23/23 `/tmp/koh-header-green.log`;
managed linker52/52 `/tmp/koh-raw-fixtures-postcommit.log`. Keep scopes separate.

## SDK audit and next major work

SDK still invokes the C# compiler; no native SDK source change is made yet.
`CompileKohRom` contains registry/driver/model algorithms and native linker bridge.
Replace with a small native process boundary using argument lists; remove obsolete
frontend/backend selection. Track compiler/tool/property/reference timestamps and
preserve clear diagnostics, paths with spaces and prior outputs on failures.
TileSheetConverter, PngReader and GenerateKohTileSheets already live in Build.Tasks,
so retain them while removing Compiler/Native project dependencies as appropriate.
NativeBuild.targets currently builds/copies only assembler/linker. SDK eagerly
builds emulator GUI; audit run semantics before changing that graph. Verify actual
ReferencePath items rather than assuming Roslyn reference assemblies are executable
implementations. Native compiler CLI currently has no PDB option.

Remaining major gates: PDB→ELF/KDBG/debugger, native SDK/build/run/release integration,
runtime/subset and IRQ/banked-data/capacity, whole game behavior, test disposition
closure (24/565 complete;541 partial/outstanding), delete C# compiler/tests/deps,
performance comparison and final3OS acceptance. No feature retirement approved.
