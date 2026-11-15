# PR #55 handoff

Full native CIL-to-SM83 migration stays ACTIVE in single draft PR55/master.
Never merge, split PRs, shrink acceptance or mark complete for an increment.
Existing public APIs are evidence, not a design baseline or convention.

Worktree /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Pushed HEAD c9d0bc5b09a2bc1e87e3cfabfe6185aa096da723.
5aa69ef ships native dependency licenses and adds release-artifact SDK smoke.
c9d0bc5 lowers DI/EI/HALT/NOP/STOP through typed observable IR and fixes the
startup speed helper: IME-disabled precondition, preserve IE/pending requests,
leave IME disabled. Existing 3D callers are startup-only. No IRQ ABI claim.

Focused CPU tests10/10 passed25.164s, exit0:
/tmp/koh-cpu-control-final.log. Four actual C# Debug/Release×DMG/CGB cases check
raw DI/EI delay/NOP, HALT external wake, repeated speed switch, metadata errors,
stack/result/IE/IF/KEY1 and IME captured before shutdown DI. Six IR cases check
typing/effects/ordered encodings. First missing-di red:
/tmp/koh-cpu-control-red.log. Stronger CGB helper IME red2/4:
/tmp/koh-speed-ime-red.log. Unarmed STOP wake, HALT-bug cases, physical switch
stall, handlers/reentrancy remain open. Existing emulator core unchanged.

LIVE local full allfeatures workspace run11811, /tmp/koh-header-target,
log /tmp/koh-cpu-workspace.log,784 tests. Last observed249/784, no failures.
Separate doctests scheduled after success, /tmp/koh-cpu-doctests.log.
Poll actual handle; do not restart for observation timeout. No concurrent shared
builds. Freeze production Rust/GameBoy/SDK while fixtures can rebuild host tools.

Prior SDK full774/774 passed1230.794s,0skips, exit0:
/tmp/koh-sdk-workspace.log. Separate doctests0cases passed. Published Linux task
payload built/runs graphics:65536-byte ROM,160x144 PNG,0mode3; both tools1.0.2,
no Compiler/Mono.Cecil DLL. /tmp/koh-compiler-task-package{,-smoke,-run}.log.
Focused SDK graphics against published executables1/1 passed22.522s
(/tmp/koh-release-artifact-sdk-smoke.log),2 unrelated cases intentionally filtered.
No tagged archive/AOT emulator/final cross-platform runtime proof.

Actual double-buffered SDK build now gets past di and fails alloc in Canvas.Init
IL0036, exit1,0warnings,45.97s:
/tmp/koh-cpu-double-buffered-build.log. All3D variants need raw allocation;
2048v2/JRPG need reference classes. Keep all sample/test behavior.
Native CI43739101141 failed Linux/reference jobs on stale METHOD rejection using
supported halt. Contracts fixture now uses unsupported-cpu-control; all rejection
assertions preserved and existing native HALT execution stays covered. The
corrected case passed locally at328/784 in24.998s. Fullrun stillLIVE11811.
Managed43739101352 fails5 sample builds on both Linux/Windows; actual logs confirm
alloc inall3D variants and reference classes2048v2/JRPG. No tests run past build;
AOT/benchmark jobs skipped. Other native hosts not yet terminal at last poll.

Prepared UNCOMMITTED RawArena fixture,4-case Rust driver and raw-arena.md proposal
are not yet compiled/executed/countable coverage. No production allocator changes.
The current784 run's driver binary predates these new4 cases; it does include
the updated Contracts fixture loaded at runtime. Do not claim788 coverage.

Next implementation: independently designed safe raw arena allocation/reset,
then classes and remaining runtime. Existing managed arrays use4KiB bank1 arena;
evaluate split upward managed/downward raw cursors with shared collision checks,
and reset raw only. Candidate, not an approved or implemented invariant.

Full test matrix28/565 complete,537 partial/outstanding,0physically retired.
Earlier managed CI43728226133 failed6 gameplay/timing/private-IR tests; closure
open. Remaining gates: complete subset/IRQ/banked-data/capacity/game behavior,
Portable PDB→ELF/KDBG/debugger, test closure, C# compiler/tests/deps deletion,
final3OS/performance. PR live checklist updated; rough45% is not measured coverage.
Root owns mutations/builds. Preserve main checkout's unrelated battle.png edit.
