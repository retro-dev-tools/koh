# Koh roadmap

Assessment date: 2026-11-14. Source baseline: `c2fddcf`, with pre-existing uncommitted RGBDS expression changes and a modified JRPG screenshot. This is an initial assessment of the architecture, build and release configuration, representative implementation and tests, documentation, and recent history. It is not a complete code, security, licensing, or hardware conformance audit.

## Product direction

Active user-requested migration (2026-11-18): replace the entire desktop UI
with Rust + GPUI Kit and remove KohUI completely, including its backends,
demo and build references. Preserve emulator/debugger behavior. The current
inventory and acceptance gates are in [GPUI migration](docs/gpui-migration.md);
implementation and native acceptance are still pending.
The user selected KDBG-to-DWARF migration first, then UI implementation.
The initial DWARF writer and bounded native reader/CLI are implemented and
pass 32 focused linker tests. Production still emits KDBG until
the debugger, SDK, CLI, editor and fixture consumers migrate together.

Serve Game Boy hobbyists who want to write assembly or constrained C#, build a ROM, and diagnose problems without stitching together unrelated editor and debugger integrations. The differentiator is the connected development workflow: semantic assembly tooling plus C# compilation, sample games, and an emulator/debugger in one repository.

The next milestone should be one documented, reproducible source-to-ROM-to-debugger workflow. Prefer making that journey reliable over expanding the language, emulator UI framework, or number of command-line tools. Preserve existing file formats and game behavior unless a demonstrated correctness issue requires a change.

## Current condition

- Legacy compiler removal is integrated on the PR branch: both
  C# compiler/test trees and direct compiler packages are removed, while the SDK,
  runtime, emulator and useful tools remain. The serial Linux solution build
  produces all seven C# sample ROMs with zero warnings/errors; retained .NET tests
  report567 succeeded, zero failed and two unchanged opt-in SDL window/input
  skips. Unfiltered native2239/2239 passes, zero skipped, with separate doctests.
  All565 source dispositions are complete and legacy definitions retired. Final
  run44330863800 atf5a10e8 passes all14 jobs, including native Linux/macOS2239
  and Windows2237 cases, zero skipped, separate doctests and artifact/notice
  checks. Compiler migration acceptance is complete in draft PR55; existing
  first-party/managed notice gaps still block release publication. See the
  [acceptance audit](docs/rust-compiler/acceptance.md) and
  [removal evidence](docs/rust-compiler/legacy-removal-validation.md).
- Rust assembler [PR #53](https://github.com/retro-dev-tools/koh/pull/53) and Rust linker/ELF [PR #54](https://github.com/retro-dev-tools/koh/pull/54) are merged.
  `koh-objects` owns the canonical ELF32/SM83 model; the native assembler and linker share it.
  The linker migration removes the C# linker and retired object formats and retains C# game development.
  See its [test audit](docs/rust-linker/test-disposition.md), [ABI](docs/rust-linker/elf32-sm83.md)
  and [validation record](docs/rust-linker/validation.md) for evidence and limitations.
- Rust CIL compiler [PR #55](https://github.com/retro-dev-tools/koh/pull/55) includes the565-method source audit, verified SSA/ELF/SM83 pipeline, integer calls/recursion, arrays/statics/initializers, hardware intrinsics, unmanaged values, UTF-16 text, HRAM-safe OAM DMA and banked managed calls.
  Closed static/instance [generic methods](docs/rust-compiler/generic-methods.md) now specialize
  supported primitive/named arguments, including referenced transitive bodies,
  byref storage and original PDB provenance. Assembly scopes distinguish same-row
  types across DLLs. [Rectangular array arguments](docs/rust-compiler/generic-rectangles.md)
  retain those scopes through generic calls, defaults and byref copies.
  Closed special/base/interface/parameter constraints now
  validate, including constructor eligibility and malformed-metadata probes.
  Runtime class/interface dispatch, generic delegates, base calls and unboxed
  [constrained calls](docs/rust-compiler/constrained-calls.md) now execute through
  the same native ABI. [Closed declaring layouts and static storage](docs/rust-compiler/closed-types.md)
  now specialize with separate method/type argument scopes and per-instance initialization.
  [Generic reference methods](docs/rust-compiler/closed-types.md#generic-methods-on-closed-reference-receivers)
  retain separate declaring/method scopes through overrides, default/explicit
  interface bodies, captured callbacks and inherited MethodImpl bodies.
  [Runtime boundaries](docs/rust-compiler/runtime-boundaries.md) explicitly retain
  exact variance identities and unboxed values, with explicit diagnostic acceptance.
  [Bounded interrupt handlers](docs/rust-compiler/irq-calls.md) now execute all five
  vectors with saved registers, a reserved stack budget and bank-preserving ROM0
  bodies and shared scalar helper calls; allocation/nested IRQ enabling remain
  explicit diagnostics.
  The current ledger has565/565 complete dispositions, none outstanding
  (564 native replacements, one obsolete private-parser DELETE decision), and
  all565 legacy method definitions retired;
  23 [backend allocation contracts](docs/rust-compiler/allocation-contracts.md)
  replace private residency choices with actual arithmetic, ABI, liveness and
  source mapping. [128-bit arithmetic](docs/rust-compiler/int128.md) also has native acceptance;
  all54 control-flow methods now have native acceptance:34
  [scalar contracts](docs/rust-compiler/control-flow-scalars.md) retain37 assertions
  with188 executions;20 [memory/call/boot/phi contracts](docs/rust-compiler/control-flow-memory.md)
  account for30 assertion sites through real C#, direct ROM reads and parallel cycles;
  three [symbol-address methods](docs/rust-compiler/symbol-addresses.md) now verify
  banked code, relocated startup/header entry and actual WRAM bytes in16 ROMs.
  five [IR construction/verifier methods](docs/rust-compiler/ir-construction.md)
  now have native validation, object round-trip and executed-loop replacements;
  the seven-method [IR round-trip group](docs/rust-compiler/ir-roundtrip.md)
  preserves useful data/entry/bitcast behavior with native tests;
  four [live-value preservation methods](docs/rust-compiler/liveness.md) now
  execute entry/diamond/signed-loop scenarios at16/32/64 bits before/after optimization;
  all seven [CGB HAL methods](docs/rust-compiler/cgb-hal.md) now have native
  replacements, including actual VRAM/palette contents and DMG no-write checks;
  binary32/binary64 [remainder](docs/rust-compiler/floating-remainder.md) now executes
  through an exact shared core with signed-zero/subnormal/special-value coverage.
  [Checked numeric casts](docs/rust-compiler/checked-casts.md) now validate all20
  conversion opcodes; [checked addition/subtraction](docs/rust-compiler/checked-arithmetic.md)
  now uses explicit overflow guards, including [multiplication](docs/rust-compiler/checked-multiplication.md).
  Managed-reference arithmetic and catchable exceptions remain unsupported.
  all five [struct-return methods](docs/rust-compiler/struct-returns.md) now have
  native behavioral replacements. No legacy compiler definitions are removed yet.
  All 12 [basic CIL cases](docs/rust-compiler/cil-basics.md) also have native
  replacements with independent MMIO/packet checks and typed stack-join invariants.
  The SDK invokes Rust tools, retains tile-sheet generation and runs the existing emulator.
  Native copy/fill and corrected VBlank polling let all eight double-buffered/
  full-frame Debug/Release × DMG/CGB cases pass structural captures. CGB uses
  bounded1024-byte DMA bursts; raw CPU copy has explicit ordered semantics.
  Callback checkpoint Linux workspace/all-features passes1193/1193, plus separate rustdoc;
  actual SDK build/run checks pass. Final selected-bank probes pass4/4.
  That earlier callback/raster checkpoint closed68 methods with497 partial/
  outstanding, none deleted. Racing-beam now passes all four native configurations
  with actual SCX/scanline traces; the shared all256-phase CLR sweep now has
  Rust-owned assertions in Debug/Release and its redundant C# verifier is removed.
  Earlier raster regression passed380/380 and the phase/cube target passes76/76. The
  existing headless emulator captures the SDK-built racing ROM with zero mode3
  writes. This CLR sweep complements native ROM tests; it is not all-phase
  native or Canvas-renderer execution.
  The phase commit's hosted Rust suites pass987 Linux/macOS and985 Windows
  cases, plus rustdoc/artifact checks. New reference-class metadata layouts pass
  the58-case layout/reference regression, including cache-order and signature
  amplification guards. Typed native objects now cover fresh allocation,
  constructors/direct calls, inherited/reference fields, reference arrays and
  checked null/exhaustion faults:60 ROM probes in Debug/Release × DMG/CGB.
  Object regression passes376/376, with277/277 final focused checks after
  verifier review fixes and fixture formatting. Two class test methods now have
  complete replacements. Class virtual dispatch now adds36 bank-constrained ROM
  probes, exact per-type results and wide/byref signatures. Final selected283/283
  checks pass. Workspace1047-case run had1045 passes and two stale forged-reference
  diagnostic assertions; both strengthened replacements pass in that selection.
  Do not call that original full run green. Reference-bearing structs and typed
  reference-cell indirection now pass347 selected regressions, including16 ROM
  probes; ordinary2048v2 SDK compilation succeeds. Native2048v2 gameplay now
  passes Debug/Release × DMG/CGB with eight-move rules, exact board pixels and
  title/HUD checks. Bounded VBlank copy fixes DMG flushing at unchanged capture
  budgets, with6160 phase/count/frame probes. The1086-case workspace run has
  1084 passes and two LCD-off setup failures; both corrected tests pass2/2 with
  unchanged compiler sources. Do not call that original command green.
  Rank-two arrays and stored/virtual callbacks now compile the unchanged JRPG.
  Guarded VRAM stores replace its racy attribute retry loop; normal SDK builds
  and Debug/Release CGB dialogue/Slime-battle assertions pass. New primitive
  checks include9856 sampled phase probes and396 broader regressions. The
  [contract](docs/rust-compiler/vram-write.md) explicitly leaves interrupts
  disabled for nonempty CGB attribute writes and nonempty1bpp/font loads.
  JRPG art/palette/OAM/pixel checks and four isolated captures now pass, retaining
  the300-frame power-on title deadline; font-loop throughput was corrected.
  Final selected42/42 and edited JRPG9/9 pass. Nongeneric interface dispatch,
  explicit/inherited MethodImpl bindings and class-body selection through default
  declarations now have native execution coverage. Default bodies, re-abstraction
  and most-specific selection are implemented. Generic/static-virtual dispatch
  and the other whole-migration gates remain required.
  Metadata commitb4caf55 hosted Rust suites pass1012 Linux/macOS
  and1010 Windows cases; those results predate the object-core increment.
  Native source-breakpoint and exact-PC caller inspection now pass for real2048
  SDK Debug/Release ROMs through the existing debugger. See the
  [unwind contract](docs/rust-compiler/native-unwind.md); arbitrary-PC caller
  unwinding is not claimed. Full current-head workspace/hosted acceptance, broader
  runtime/gameplay, test closure, release integration and legacy deletion remain
  incomplete. Container cleanup is done. Existing API shapes are evidence,
  not a design baseline.
  Track the [commit sequence and acceptance ledger](docs/rust-compiler/progress.md);
  the standalone native compiler is verified only for its documented subset, not final migration acceptance.

- Shared diagnostics, opcodes, and object formats are separated from assembler, linker, compiler, emulator, and UI projects. Recent architecture commits deliberately removed compiler dependencies on assembler implementation details. Retain those boundaries; there is no demonstrated need for another restructure.
- Tests cover individual components and compile and emulate sample games. CI builds on Linux and Windows, runs extension tests, publishes the emulator with NativeAOT on three host platforms, and builds the assembly 2048 ROM. These workflow definitions describe intended checks, not evidence that current CI passes.
- Release automation creates platform archives, checksums, and draft releases. A draft-and-smoke-test gate already exists; improve and verify it rather than replacing it.
- Public documentation underrepresented the C# and emulator workflow. The README and contribution guide now expose those entry points, without claiming complete RGBDS or .NET compatibility.
- The README and extension metadata declare MIT, but no tracked LICENSE or NOTICE file was found in the checkout or matching filename history. This needs an ownership and attribution review, not an invented copyright holder.
- `GbJrpgTests` writes acceptance captures under the test binary's `TestResults/gb-jrpg/<run-id>/`. Its regression guard checks that curated sample screenshots are not rewritten. Updating documentation images is a deliberate manual step.
- The local Koh.Sdk package imports repository-relative source projects. Its presence in a local feed is useful for this checkout but does not establish a standalone SDK distribution story.

## PR #55 completion path

Overall completion is unmeasured; 100% requires every acceptance gate, rather
than a test count or a working standalone compiler. All work stays in PR #55.

1. Finish the language/runtime needs of existing games: wider string operations,
   software floating point, reference objects and required generic,
   interface/delegate/dispatch shapes. Document deliberate subset decisions and
   reject unsupported IL explicitly.
2. Complete hardware ABI support: interrupts, banked code/data, arithmetic and
   memory operations, reentrancy rules and checked ROM/RAM/stack capacities.
3. Prove complete representative games in the existing C# emulator, including
   graphics and nontrivial language behavior. Preserve assets and distinguish
   documented aspirational examples from passing acceptance samples.
4. Source-debug acceptance is implemented: matching PDBs, IR/ELF/KDBG provenance,
   real source/function breakpoints, disassembly and exact-PC caller inspection.
   Retain these checks during final hosted validation; unsupported PCs stop unwinding.
5. Switch SDK/MSBuild, run/debug tooling, CI and release payloads to Rust. Verify
   ordinary dotnet builds, references, DMG/CGB flags, incremental behavior and
   paths with spaces; preserve useful tile-sheet/build functionality.
6. Migrate tests alongside each feature, then close every disposition in the
   565-method audit. Run all feasible compiler-owned scenarios from Rust with
   nextest; keep only justified noncompiler .NET tests and fixture executables.
7. Delete the C# compiler, obsolete dependencies/projects and all fallback routes
   after replacement acceptance; verify there is one production compiler.
8. Record comparable compile-time/size/peak-memory measurements and final
   Linux/Windows/macOS results. Recheck samples, diagnostics, docs and packaging.
   Container cleanup is complete. Keep the single PR draft until all gates pass;
   do not merge it automatically.

Latest increments: [expanded RAM capacity](docs/rust-compiler/ram-layout.md)
closes the5000-byte array requirement; [video/input acceptance](docs/rust-compiler/video-joypad-contracts.md)
retains real VBlank pacing and rising input edges while skipping clean-map
commit overhead. Full acceptance and legacy compiler removal remain required.

Earlier SDK increment: the unchanged graphics showcase executes through Roslyn,
native compiler, canonical ELF, Rust linker and real DMG/CGB boot in the existing
emulator. Stronger HUD checks exposed late flushes; full-map tests exposed unsafe
LCD-on clear. Both are corrected with independent graphics/data/timing checks.
The native SDK now builds/runs that same source with default native tool discovery;
Debug/Release SDK fixtures also execute actual generated art and referenced code.
Existing public APIs are not a design baseline. Next: finish SDK/CI/release
integration, then the remaining game/runtime, source-debug and test-disposition
gates. Detailed evidence and limits are in [SDK boundary](docs/rust-compiler/sdk.md)
and [presentation decisions](docs/rust-compiler/presentation.md).

## Baseline verification

On .NET SDK 10.0.113, `dotnet msbuild build.proj -t:Test` exited 1 before tests ran. NuGet audit warnings treated as errors reported:

| Package | Reported version | Audit result |
| --- | --- | --- |
| MessagePack | 2.5.302 | NU1902, including [GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h) |
| Microsoft.DotNet.ILCompiler | 10.0.13 | NU1903, [GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9) |
| SixLabors.ImageSharp | 3.1.12 | NU1902 and NU1903, multiple advisories |

These were restore diagnostics, not independently validated exploitability findings. They describe the initial baseline before the dependency repair below.

Both documented sample builds were also attempted and exited 1: `BuildSample2048` was blocked by the ILCompiler advisory, and `dotnet build samples/csharp/gb-2048-v2/Gb2048V2.csproj` was blocked by ILCompiler and ImageSharp advisories. Neither run established a newly built ROM. Documentation checks passed for nine local links and balanced code fences across the README, contribution guide, and this roadmap.

## Priorities

Order reflects impact, effort, risk, and dependencies; it is not a release-date commitment.

Dependency repair: SDK 10.0.115 and StreamJsonRpc 2.26.150 resolve to ILCompiler 10.0.15 and MessagePack 2.5.307. ImageSharp 3.2.0 cleared the reported advisories but required a license key for release builds. Its sole use was replaced with framebuffer PNG output using .NET compression. Screenshot formats other than PNG are now rejected explicitly.

## Completed milestones

- SDL3 native GUI migration (2026-11-14): native windows, input, texture copies, fonts and clipping now use SDL3 Renderer; OpenGL/GLFW code and dependencies are removed. Images retain their integer scale inside stretched layouts. Headless DAP requires neither graphics nor audio, and closes cooperatively. Release solution and Linux x64 NativeAOT builds pass without added warning suppression. Software/native renderer and input regressions pass on dummy video, X11 and Wayland, including a 2x compositor scale. NativeAOT ROM execution, debugger panels and transport were exercised together. Detailed evidence and platform limits are in [SDL3 rendering](docs/sdl3.md).
- [PR #51](https://github.com/retro-dev-tools/koh/pull/51) review follow-up: use `/tmp` for Unix DAP sockets so long or multibyte `TMPDIR` values cannot exceed the macOS socket path limit. The regression supplies a temporary-directory path longer than 103 UTF-8 bytes and checks the generated endpoint's byte length plus a real socket connection. It fails before the repair (43 passed, 1 failed) and passes afterwards (44/44 extension tests, exit 0). Native macOS execution remains unverified locally.
- Unix DAP transport repair (2026-11-14): the extension passes an explicit absolute socket path in the system temporary directory to both the .NET host and VS Code. Windows retains its logical pipe name and full client path. A real socket connection regression fails on the previous implementation (42 passed, 1 failed) and passes with the fix (43/43 extension tests). The Release CI build passes with zero warnings/errors, the assembly sample rebuilds with its source map, and the serialized fast suite passes 1,673/1,673; all exit 0. A Linux VS Code 1.146.0 smoke test reaches the real emulator's `client connected` log, then fails because the Xvfb environment cannot create the OpenGL window (`glfwCreateWindow failed`). This establishes editor-to-host transport, not a completed editor breakpoint session. Windows/macOS execution remains unverified.
- Newcomer build/debug increment (2026-11-14): all three assembly sample build entry points now emit `2048.kdbg`. A fresh source copy with Git initialized builds both documented games using SDK 10.0.115; the C# build reports zero warnings/errors. Both ROMs produce valid 160x144 PNG captures after 300 frames, visually showing their title screens. A DAP-core probe loads the fresh assembly ROM and source map, verifies the breakpoint at `main.asm:78`, executes to it at `$0154`, and reads `SP=$E000`. The debugger regression now executes to its breakpoint and checks `A=$42`, rather than only checking registration. Focused debugger tests pass 32/32 and the default fast suite passes 1,673/1,673 (exit 0). The shell sample build also passes; PowerShell execution remains unverified. The [walkthrough](docs/build-and-debug.md) records commands, prerequisites, and proof limits. Editor/GUI acceptance remains open: a live Linux pipe probe confirms .NET creates `/tmp/CoreFxPipe_<name>`, but the extension attempts `/tmp/<name>.sock`.
- RGBDS HIGH/LOW/BANK regression closure (2026-11-14): reviewed and completed the inherited expression changes. Unresolved HIGH/LOW now retain their operators; BANK(symbol) emits a bank lookup. Existing enum values remain stable. The expanded regression fails on baseline `71490f0` (36 passed, 1 failed) and passes with the fix (37/37). RGBDS 1.0.1 links Koh and rgbasm objects into byte-identical ROMs; the fixture yields `60 ff 02 61 3e ff 61 03` at `$100`. Reproduction commands and scope are in `tests/fixtures/rgbds-functions.md`. The default fast suite passes 1,673/1,673; the Release assembler build has zero warnings/errors. The pre-existing battle screenshot remains untouched. Full compiler suite and other BANK argument forms were not qualified in this increment.
- `c965bf9`: documented both game-development entry points and contributor checks.
- `c755769`, dependency repair: restore and CI release build pass with auditing enabled (0 warnings/errors); the fast suite passes 1,673/1,673. The transitive vulnerability report covers 41 projects with no reported vulnerable packages or audit problems. The assembly 2048 sample builds; its headless capture shows the title screen, and independent PNG checks validate dimensions, compression, and every chunk CRC. Rejected JPEG output preserves an existing file. The CI build also emits all seven C# sample ROMs.
- `2451c6c`, screenshot isolation: the new guard fails against the old writer in an isolated worktree and passes after redirecting captures. All four source screenshot SHA-256 hashes are preserved, including the pre-existing modified battle image. All four generated 480x432 captures have valid PNG chunk CRCs. The full CI suite passes 2,485/2,485 with one test/module at a time (5m44s). The compiler test process still consumes several GB; serialization reduces concurrency but does not establish a low-memory workflow.
- Portable emulator targets: publish defaults to the host RID; launch selects the correct executable name. Dispatch checks pass for Linux, macOS, Windows, and the host default, including paths containing spaces. The root target performs a real Linux x64 NativeAOT publish; its executable runs the assembly sample and produces a 160x144 PNG with valid CRCs and decompressed pixel data. The exact new CI smoke step passes locally on Linux. The normal `build.proj -t:Test` loop also passes 1,673/1,673. Windows/macOS execution and GUI launch remain unverified locally; CI is configured to exercise native headless captures on all three hosts.

- [PR #48](https://github.com/retro-dev-tools/koh/pull/48) review follow-up: the capture guard compares the source filename set and SHA-256 hashes. Temporary mutation probes reject timestamp-preserving rewrites, added files, and removed files; the old timestamp guard accepted the rewrite. All five JRPG tests and all 1,673 fast-suite tests pass on the isolated PR checkout.

## Planned work

| Order | Outcome | Effort / risk | Acceptance criteria |
| --- | --- | --- | --- |
| 1 | Establish release readiness | Medium / ownership review required | Confirm license text and asset provenance with the rights holder; inventory shipped dependencies; smoke-test draft archives and the extension's install/update path on supported hosts. |
| 2 | Prove the newcomer journey | Medium / low | From a fresh checkout, build both documented games, launch a ROM, set a breakpoint, and inspect state; record platform prerequisites and compatibility limits. |
| 3 | Specify the supported surface | Medium / medium | Publish tested C# subset and RGBDS compatibility limits, backed by fixtures; document whether Koh.Sdk is checkout-only or independently consumable. |
| 4 | Bound compiler-test memory | Medium / medium | Measure retained fixture/compiler state and establish a bounded-memory full-suite invocation before changing parallelism defaults. |

Next autonomous increment: qualify Windows/macOS desktop rendering and physical monitor DPI transitions. The SDL3 migration demonstrates native Linux rendering, input and ROM/DAP execution; it does not establish physical GPU or editor acceptance on every target platform. Release ownership and attribution questions still await the rights holder. The extension's earlier `npm ci` reported 22 audit vulnerabilities (1 low, 9 moderate, 11 high, 1 critical); dependency triage remains a separate release-readiness increment. Treat the RGBDS fixture's BANK(symbol) coverage as bounded evidence, not full RGBDS compatibility.

## Boundaries

No adoption, performance, hardware-accuracy, or complete compatibility claims are established by this assessment. Defer new language features, UI expansion, dependency replacements without evidence, and architectural rewrites until the core journey has a reproducible passing baseline. Reassess priorities when that evidence changes.
