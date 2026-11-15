# PR #55 handoff

Active division increment: root owns all edits and serial builds. Read-only
division review completed with no findings; no edits/builds. Backend50/50
passed160.779s (/tmp/koh-division-sm83.log). Expanded boundary/cycle9/9 passed
34.443s (/tmp/koh-division-boundaries.log): I32 4532T, I64 6212T. Original red:
I32 51268T for30000/31 (/tmp/koh-division-cost-red.log). New word path checks
normalized magnitudes, retains full-width fault/sign handling and wide fallback.
Cube CGB Release probe now passes unchanged500-frame budget,10.424s
(/tmp/koh-cube-word-division.log); structural assertions still need strengthening.
Full4-case Cube run48692 is terminal exit100:2 CGB passed,2 DMG failed,
40.931s (/tmp/koh-cube-word-all.log). DMG mode3 writes806Release/826Debug.
No local build/test live. Next fix Canvas bounded CPU transfers and strengthen
Cube geometry assertions; unchanged frame budgets. Commit only division files,
its docs and this handoff; leave Cube/config probes for their completed unit.

Full native CIL→SM83 migration stays ACTIVE in single draft PR55/master.
Never merge, split PRs, shrink acceptance or mark complete for an increment.
Existing public APIs are evidence, not a design baseline or convention.

Worktree /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Pushed HEAD34dc1cfb5c768a37fb6715a90262c1895db8c5a4 fixes lazy map storage;
parent4e7212d implements checked raw arena. Root owns all writes
Current-head hosted runs43747499032 (Rust) and43747499084 (managed) were LIVE
at last primary query. Expected tracked native suite798 tests, Windows796;
local untracked Cube4 adds another4 failing probes, not yet CI coverage.
and serial builds; no local process live at this update. lazy_map_review completed
read-only inspection with no Critical/Important/Minor findings. No tests/builds
run by reviewer. Declined boundaries accepted: flattened bounds retain existing
meaning, allocated shadows retain lifetime, LCD-on clears retain backing storage;
cube rendering/timing are next units inside this SAME full goal, not deferred.

Lazy writer keeps each absent map's uniform clear tile, allocates only when a
real cell write/LCD-on clear needs backing, preserves16-byte alignment and queue
invariants. Same-value valid writes consume no storage; invalid flattened indices
still fault6. Existing Video.Init hardware behavior unchanged; no eager2KiB map
reservation. Sprite/VideoInit heap expectations now require only OAM allocation
and preserve poison in unused space. All map/palette/OAM/sprite assertions remain.
Removed obsolete C010 debug writes that could alias compiler statics.
See docs/rust-compiler/lazy-maps.md for decisions and test evidence.

Actual native Cube red /tmp/koh-canvas-boot-red.log: after155bootframes+68 startup,
A05,managed2464,raw0:1935-byte canvas would exceed4096 shared capacity. With lazy
maps, it runs past allocation:managed492,raw1935, no terminal fault.
Current unfinished Cube probes still FAIL:
CGB first200/last500 postboot snapshots identical white, rawNZ421,VRAMtileNZ0,
palette/map correct, bankF9. Active PC41F7 in bank4 is Canvas.EdgeX+503.
Log /tmp/koh-canvas-cgb-trace.log. Likely expensive generic32-bit math; investigate
without relaxing frame bounds. DMG through2000frames reports mode3 writes812
Release/691Debug (/tmp/koh-lazy-canvas-first.log): Canvas.PresentCpu copies3-byte
chunks after setup; timing contract still unsafe. CGB GDMA path also needs probe
once it reaches presentation. No emulator core/debugger rewrite.

UNTRACKED cubes.rs, fixtures/Cube and CubeHost are unfinished acceptance work.
Keep them for next unit; do not discard/ignore or include as passing coverage.
Run with --test cubes. Uncommitted nextest config now includes cubes in the
roslyn serial group; commit that config with the Cube target when ready.
Do not allow shared .NET outputs to build concurrently.
The memory increment intentionally stages only tracked lazy tests, not failing
cube probes. This is not compiler-test retirement or whole migration acceptance.

Final exact serial memory regression /tmp/koh-lazy-map-verified.log passed22/22,
155.775s,exit0,72 unrelated driver cases filtered. Includes invalid no-op bounds,
same-value zero writes to both maps,2500rawbytes, nonzero uniform seeding, both
maps, D/R×DMG/CGB, full map/tiles/sprites/strings/showcase, exact full bank snapshots.
Separate serial reds: /tmp/koh-lazy-noop-serial-red.log and
/tmp/koh-lazy-bounds-red.log. Earlier overlapping42195/50017 runs were explicitly
not used as final verification; both are terminal. NEVER start next build after
write_stdin returns a session_id; only actual terminal output releases ownership.
Post-commit /tmp/koh-lazy-map-postcommit.log passed22/22,169.673s,exit0,
72unrelated cases filtered, after CSharpier hook. No local builds/tests live.

Raw predecessor4e7212d native CI43742721789 completed allsuccess. Actual job logs:
Linux794/794613.381s (132899205310), macOS794/794939.618s (132899205349),
Windows792/7921273.505s (132899205389),0skips. Two Unix assembler cases differ.
Separate doctests (no cases) and release artifact graphics1/1 passed on allhosts;
two unrelated SDK cases intentionally filtered. Reference/RGBDS job passed too.
Managed run43742721700 failed only2class sample builds on Linux/Windows;
actual logs132899205051/132899204781 show all3D128KiB ROMs built. Tests after
solution build and dependent AOT/benchmark jobs skipped. Do not count them green.

Next implementation candidates (NOT implemented): optimize generic math with
independently tested runtime small-value paths instead of narrowing C# semantics
or increasing capture budgets. Word unsigned division can keep quotientHL,
divisorBC,remainderDE with16 iterations and PUSH/POP AF counter; carry overflow
must force subtraction. Use only after wide signed overflow/zero guards and
normalization, extend result and apply sign at semantic width. Check high offsets
and guard2-byte temporary stack. Multiplication can exit when remaining multiplier
becomes0 while retaining full-width modular accumulator. Test actual results and
CPU budgets before use. Then fix Canvas CPU presentation via a shared bounded
vblank writer (existing MapWriter.FlushRun works) and CGB via existing precomputed
CopyToVramInVBlank; avoid guessed fixed chunk counts. These are candidates, not
approved/verified code or an architecture commitment.

Matrix32/565 complete,533 partial/outstanding,0physically retired. Legacy compiler
and tests remain. Still required: full subset/classes/floats/IRQ/banked-data,
actual game behavior, PortablePDB→ELF/KDBG/debugger, test closure and deletion,
final performance/3OS validation. All3D Release builds already produce128KiB ROMs;
full-frame/racing captures visibly show cubes, double still fails acceptance.
Do not hide samples. Preserve main checkout's unrelated battle.png edit.
PR55 stays draft,rough45%,100% means all original criteria verified.
