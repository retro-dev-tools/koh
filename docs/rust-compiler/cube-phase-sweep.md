# Cube verifier migration

The standalone `samples/csharp/gb-3d/verify` project owned compiler/sample
acceptance assertions. Its use of the C# emulator or CLR did not require keeping
a separate .NET test implementation. `rust/koh-compile/tests/cubes.rs` now owns
the checks, driven by cargo-nextest. Its small C# fixtures provide renderer
pixels and emulator observations, with no pass/fail geometry assertions.
The old project, three implementation files and ignore file are deleted; solution
and CI-filter entries are removed. `Koh.Verify` and the emulator remain active.

## Disposition matrix

These groups were outside the565-method TUnit inventory. Their migration does
not inflate that denominator. The separate legacy sample group is audited below.

| Original source / purpose | Requirement and independent justification | Decision / discoverable Rust coverage |
| --- | --- | --- |
| `verify/Program.cs`, six ROM/mode combinations | Actual sample builds, bounded boot/render progress, two distinct captures, no mode3 VRAM writes; sample geometry and PPU restrictions are the oracle | PORT/strengthen: `cube_boots_and_animates`, twelve Debug/Release × DMG/CGB builds. Existing frame limits retained or strengthened; actual GDMA/raster timing checks added. |
| `verify/CubeFrameChecks.cs::Check` | Shaded cube, screen-edge clearance, nontrivial size and later animation; these are public sample behavior, not C# helper APIs | REPLACE: Rust palette/alpha, viewport, minimum area, centered projection, connectivity and both-capture checks. Eighteen negative/positive validator cases cover all viewports. |
| `verify/CubeFrameChecks.cs::CountNonBackgroundRegions` | One eight-connected silhouette, independently defined adjacency | REPLACE implementation with Rust flood fill; disconnected pixels are explicitly rejected. |
| `verify/PhaseSweepCheck.cs::Run/Sweep` | All256 byte phases at64x64,96x80,128x120; every frame nonempty, two-pixel edge clearance and bbox area10..75% of viewport. These are intentional sample design limits, not universal CIL rules | PORT: `shared_renderer_sweeps_every_phase`, two Roslyn configurations,768 scenarios each. Rust owns all assertions and adds palette validation. |
| `PhaseSweepCheck.cs` Surface stand-in | Observe the actual shared renderer without hardware presentation; naive per-pixel dither is independent of optimized span packing | REPLACE with `tests/fixtures/CubePhases/Program.cs`, a bounded row-major pixel producer including the actual shared source files. No C# assertions. |
| Standalone verifier screenshots/filter arguments | Useful capture and focused diagnosis, independent of the old executable layout | REPLACE: existing emulator `--headless --screenshot --mode3-report`; named nextest `double_`, `full_`, `racing_` filters. Sample README documents commands. |
| Project, solution entries and `verify/out` convention | Historical packaging/test organization | DELETE after caller audit; no coverage deferred, no sample assets removed. Generated screenshots stay outside tracked assets. |

All768 geometry scenarios execute in each of Debug and Release,1536 total.
There are24 adversarial phase-validator cases: valid shape, empty image,
truncated/trailing bytes, invalid palette, edge contact, too-small/too-large area,
each at all three geometries. Stream dimensions, phase order, truncation and
absence of extra output records are also checked at the Rust process boundary.
These augment the twelve ROM cases, eighteen image-validator, eleven GDMA-window
and nine raster-trace cases: the cube target discovers76 nextest cases.

## Evidence limits

The all-phase sweep runs shared `FixedMath.cs`/`CubeRenderer.cs` under the CLR,
matching the removed verifier's purpose. It is not all-phase native execution.
It also does not execute the separate Canvas-based double-buffered renderer:
96x80 is a shared-renderer geometry check. Actual native tests compile each
sample's real local overrides and referenced library and execute two captures
on each hardware model/configuration. These distinct evidence scopes are explicit
instead of treating a CLR reference sweep as a compiler correctness oracle.

The initial Rust selection is24 passed/2 failed because the fixture did not yet
exist (`/tmp/koh-phase-red.log`, exit100). The complete fixture/check selection
passes26/26 in4.227s (`/tmp/koh-phase-green.log`, exit0). The complete cube target
passes76/76 in123.815s, exit0, zero skips (`/tmp/koh-cube-phases-final.log`).
Read-only coverage review found no blocking issue or lost assertion.

The documented existing-emulator CLI was run on the SDK-generated racing-beam
ROM, using `/tmp/koh-cube-sweep-capture.png` as the output path. It exits0 after
500 frames with zero mode3 write violations (`/tmp/koh-phase-screenshot.log`).
The PNG is160x144, opaque grayscale, four colors and1269 nonbackground pixels
inside the racing viewport; visual inspection shows the shaded cube. This
preserves capture capability without another verifier executable or screenshot
API. The retained output is outside tracked sample assets. Whole compiler
acceptance remains incomplete.

## Legacy cube sample requirements

`Samples/Cube3dTests.cs` contains three methods, six parameter-expanded cases
and eight assertion sites (16 expanded assertions). All are compiler-owned;
none is deferred to an emulator migration.

| Source method | Sites / expanded assertions | Independent requirement | Native replacement |
| --- | ---: | --- | --- |
| Demo_CompilesWithoutDiagnostics | 1 / 2 | Actual shared sources and local overrides compile with referenced HAL. Managed compiler classes are private details. | REPLACE: twelve real Roslyn builds, verified native IR and ELF write/read round-trip. |
| Demo_LinksToBootableRom | 4 / 8 | Valid cartridge entry/header reaches the actual game. Two opcode bytes and two logo bytes are weak proxies. | REPLACE: canonical Rust linker and actual boot-ROM handoff in both modes, with independent linker header coverage. |
| Demo_RunOnHardwareRendersARealCube | 3 / 6 | At least two shaded face colors and between50 pixels and85% of the screen filled at the original200-frame double-buffered/CGB and250-frame full-frame/DMG Release deadlines. Despite its name this is emulator evidence. | PORT/strengthen: retain both original deadlines, require200..5999 shaded pixels, bounded centered viewport, palette/alpha, connected silhouette and a distinct later capture. |

The source lower bound is50 **pixels**, not50 percent of the screen. The new
area bounds strengthen it. Debug and the additional mode/variant combinations
remain separate extended coverage; their budgets do not replace the two original
Release deadlines. Frames count from boot handoff, equivalent to the old harness
starting at0100, while native tests additionally execute the real boot ROM.

### Corrected performance gap

The earlier native full-frame/DMG first capture at450 frames did not establish
the legacy250-frame contract. Restoring250 reproduced a blank framebuffer while
the first cube was still drawing (`/tmp/koh-cube-legacy-budget.log`, exit100).
Profiling identified startup tilemap calls and raster arithmetic costs. No
assertion, source geometry, asset or deadline was weakened to resolve it.

The backend now shifts multi-byte slots directly with SM83 memory shifts/rotates.
Calls to argument-free,
effect-free integer literal returns may fold, including conversions proven to
leave the literal unchanged. Width/Height calls previously retained such byte
return conversions. Nonconstant operations, side effects and narrowing or signed
conversions that change bits prevent folding. Caller IL offsets are retained.
Eight parameterized unit cases verify those boundaries; six executed32/64-bit
shift cases check complete results, stack/guards and a24000-T-cycle ceiling.
The proof retains each constant's source type: signed widening from int32/native16
must not mistake a negative input's raw bits for an unchanged positive result.
Both missing-source-width regressions first fail
(`/tmp/koh-constant-source-width-red.log`,6 passed/2 failed), then the typed proof
rejects those folds.

`Tilemap.Clear` uses the existing ascending Mem.Fill runtime for exactly1024
bytes. Its API, values, write order and selected VRAM bank are unchanged.
Four existing memory-fill cases additionally test both boundaries, every byte
and distinct contents in both CGB banks with legal LCD shutdown. This is a
runtime implementation optimization, not a game-facing API redesign.

The restored250-frame cube passes (`/tmp/koh-cube-noop-constant-calls.log`,
1/1,9.716s,exit0). Final350/350 selected tests pass546.837s,exit0
(`/tmp/koh-cube-budget-width-final.log`),594 other cases excluded. The selection
includes all219 compiler library cases, all76 cube cases, targeted arithmetic/
memory checks, both real source-debugger cases and four original2048 SDK cases.
Workspace all-feature/all-target checking, formatting and separate rustdoc pass.
Discovery lists1788 cases; all17 new cases are unignored. Read-only review found
no blocking issue. The existing cubes `build_host` unused warning remains.
This measures ROM execution acceptance, not compiler throughput or peak memory
against the removed C# implementation; those comparisons remain separate work.

An additional pointer-arithmetic rewrite was discarded during review: its pushes
exceeded the existing helper scratch budget. A near-boundary division sweep
reproduced reserved-memory corruption in both original probe configurations
(`/tmp/koh-arithmetic-scratch-red.log`). The final code retains the old arithmetic
emitter and stack budget. Three retained sweep cases exercise224 initial SP
positions each, requiring either the exact quotient/restored SP or an explicit
stack fault, always with reserved memory intact. The larger481/481 intermediate
run passed before this review but does not verify the final code.

For the same64-bit `1 << 63` ROM probe, restoring the preceding shift emitter
measured55624 T-cycles and failed the new24000-cycle budget
(`/tmp/koh-shift-baseline-cost.log`). The memory-shift emitter measured15304
T-cycles with the same complete value, SP and guards. This is a single comparable
backend microbenchmark, not a general compilation-performance claim.
