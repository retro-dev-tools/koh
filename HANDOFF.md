# PR #55 handoff

Full migration stays active in the single draft PR55 against master. No merge,
scope reduction or completion claim. Existing public APIs are evidence, not a
baseline or convention; design from authoring needs and hardware semantics.

Worktree: /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Pushed head961e7c3 packages native compiler and retires the Python SDK verifier.
SDK boundary7fe8c13 uses compiler/linker processes, canonical ELF and lazy emulator
run integration; useful C# tile-sheet generation remains. Legacy compiler/tests
still exist and must be deleted after complete native replacement acceptance.

Local full nextest completed774/774, zero skipped,1230.794s, exit0:
/tmp/koh-sdk-workspace.log. Separate doctests exit0, no cases:
/tmp/koh-sdk-doctests.log. Post-commit SDK3/3,152.483s:
/tmp/koh-sdk-postcommit.log. No build/test currently live.

Pending packaging follow-up copies dependency licenses into task output and adds
release-artifact SDK smoke to native CI. Published Linux task payload produced a
65536-byte graphics ROM through default colocated tools; existing managed emulator
ran1200frames,160x144 PNG,0mode3 violations. Both tools report1.0.2; no Compiler or
Mono.Cecil DLL in payload. Logs /tmp/koh-compiler-task-package{,-smoke,-run}.log.
Focused SDK graphics test against those published executables passed1/1 in22.522s
(/tmp/koh-release-artifact-sdk-smoke.log);2 unrelated SDK cases intentionally
filtered. XML/YAML/static smoke configuration checks pass. This is not tagged
archive/AOT emulator/cross-platform runtime proof.

Next major implementation: native CPU-control and raw allocation contracts, then
reference classes. Native solution fails5 samples: reference classes2048v2/JRPG,
di double-buffered3D, alloc full-frame3D/racing-beam. Keep samples/test coverage.
Earlier managed CI43728226133 failed6 legacy timing/gameplay/private-IR tests.
Do not blindly relax cadence or reproduce private C# IR conventions.

Remaining gates: full subset/runtime/IRQ/banked data/capacity, actual game behavior,
Portable PDB→ELF/KDBG/debugger, all565 legacy test dispositions (26 complete,
539 partial/outstanding), C# compiler/test/dependency deletion, final3OS/performance.
Current progress docs and PR checklist record limits; rough45% is not a measured
feature-coverage score. Root owns mutations/builds. Serial shared outputs.
