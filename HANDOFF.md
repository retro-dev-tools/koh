# PR #55 handoff

Full native CIL→SM83 migration stays ACTIVE in single draft PR55/master.
Never merge, split PRs, shrink acceptance or mark complete for an increment.
Existing public APIs are evidence, not a design baseline or convention.

Worktree /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Last pushed HEAD68989409dc3d4217b5e5ec94c0571a8ce4d8fb14 fixes unknown CPU
contract rejection after native HALT support. Raw arena unit is ready to commit.
No local build/test live at this handoff update.

Raw allocation/reset now lower through typed IR into shared4KiB bank1 storage.
Managed grows upward, raw downward; both check combined capacity before writes.
Two independent startup-zeroed16-bit WRAM0 words (__koh_heap_cursor,__koh_raw_used).
Reset releases raw only, preserving live managed arrays. Raw ops preserve SVBK;
callers select bank1 to dereference native pointers. No reentrancy/IRQ guarantee.
Desktop Mem body now bounds sizes/capacity and matches raw address/reset contract.
Docs raw-arena.md explain breaking semantics, ABI, alternatives and evidence.

Missing native allocation red /tmp/koh-raw-arena-red.log exit100.
Desktop actual-body wraparound/address red /tmp/koh-raw-desktop-red.log exit100.
Native four D/R×DMG/CGB cases each execute13 ROM scenarios and reject2 malformed
signatures; separate desktop case and5IR cases total10nextest cases.
Raw/primitive/aggregate/aligned/RVA regression26/26 passed136.631s, exit0:
/tmp/koh-raw-arena-array-regression.log;265 unrelated cases intentionally filtered.
Final test-only reset/bank full-poison assertions postdate that26 run; verify them
after commit along with full IR and targeted raw cases. Formatting check completed.
Matrix32/565 complete,533 partial/outstanding,0physically retired.

All3D Release SDK builds pass0warnings/errors,128KiB ROM each:
logs /tmp/koh-raw-{full-frame,racing-beam,double-buffered}-build.log.
Existing managed emulator CLI ran each through real boot for1200frames, exit0,
160x144 PNG,0mode3 writes. Full-frame/racing captures visibly contain cubes.
Double-buffered remains on Nintendo boot logo: FAILED sample, not game acceptance.
Raw capacity likely exposed Video.Init's eager2×1024 map shadows; confirm CPU trap/
heap state before changing library. Preserve intended Canvas behavior. Other2
reference-class games2048v2/JRPG still block compilation. Never hide samples.

Next major unit: add native Rust-owned actual-game probes, diagnose/fix double
canvas RAM use with independent tests. Consider lazy per-map shadows if evidence
supports it; no such production change made yet. Actual phase/pixel/game-state
assertions remain needed for all3D variants. Do not change emulator core.

Previous CPU-control regression completed784/784,0skips,1210.838s,exit0:
/tmp/koh-cpu-workspace.log. Separate /tmp/koh-cpu-doctests.log passed,no cases.
It used CPU production c9d0bc5 and corrected Contracts fixture; raw tests absent.
Hosted6898940 run43740012386 allsuccess. Actual logs Linux784/784580.774s,
macOS784/784484.138s, Windows782/782934.629s,0skips;2Unix assembler cases differ.
All3hosts passed separate release-artifact SDK graphics1/1 (2cases filtered) and
doctests. Artifact smoke is not tagged release/AOT archive proof. Managed CI still
failed5sample builds at this predecessor; actual logs inspected for both hosts.

Avoid local production freeze for another20-minute full run while debugging
sample RAM: push raw increment and let CI test its pinned snapshot. Local full
IR plus targeted raw post-commit checks are next; shared outputs always serial.
No tests may silently skip or narrow compiler requirements.

Remaining gates: complete subset/classes/floats/IRQ/banked-data/capacity/gameplay,
Portable PDB→ELF/KDBG/debugger, all test dispositions, C# compiler/test/dependency
deletion, final3OS/performance. Legacy compiler/tests remain. PR checklist roughly
45%;100% means every original acceptance criterion. Root owns all writes/builds.
Preserve main checkout's unrelated battle.png edit. Update PR after verified units.
