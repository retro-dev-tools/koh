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
not inflate that denominator or the45 completed method replacements.

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
