# Compiler integration acceptance audit

Status: source inspection only, 2026-11-14. No builds, emulator runs, or migrated-coverage claims. Dispositions below are proposed work before retiring the managed compiler, not evidence the Rust compiler implements it. Paths are relative to this repository.

## Inventory and decision rule

Inspected all seven files in `tests/Compiler/Koh.Compiler.Tests/Samples/`: **43 test methods, 48 argument-expanded cases**; `Frontends/TileSheetConverterTests.cs`: **2 methods/cases**; both `TestSupport` files. Counts describe declarations, not successful execution. Every one of these 45 methods has a disposition below. Compiler-owned sample acceptance stays in this migration; no compiler-owned test is DEFERred.

PORT means preserve an independently meaningful observable contract through a native compiler → canonical ELF → existing native linker → ROM → existing emulator harness. REPLACE means retain the requirement but remove dependence on managed IR, backend classes, function ordering, or a weak proxy. DELETE means remove obsolete scaffolding only after replacement acceptance exists. Owners: **compiler migration** owns compilation, ELF and compiler-origin debug evidence; **SDK** owns MSBuild/art conversion; **debugger** owns DAP consumers; **emulator** owns hardware models; **release** owns distribution; **tools** owns superoptimization/benchmarks. Noncompiler DEFERs preserve existing checks with named owners and reasons; they are not migration completion claims.

## Test traceability

All method names below are exact names in the named class. A coherent group enumerates every included method. Unless stated otherwise the owner is compiler migration.

### `Samples/CilGame2048Tests.cs` — 16 methods, 16 cases

- **REPLACE** `Sample_CompilesWithoutDiagnostics`: real Board/Tiles/Game sources plus referenced Koh.GameBoy compile without errors; its managed `IrVerifier` result is an implementation assertion. Require successful native compilation and ELF validation instead.
- **REPLACE** `Sample_LinksToBootableRom`: currently checks NOP/JP at 0x100/0x101 and only two logo bytes. Preserve valid cartridge entry/header, but validate the complete header and execute entry rather than canonizing two opcodes or partial logo checks.
- **PORT** `Sample_RomBootsIntoMainAndInitializes`, `Optimized_SampleRomBootsIntoMainAndInitializes`: emulator reads VRAM 0x8010 == 0xFF after bounded real-ROM execution. Keep boot/initialization observation; avoid forcing an optimizer toggle if Rust has no equivalent switch.
- **PORT** `Sample_RendersScoreLabelAndNumberToTheBackgroundMap`, `Sample_CursorSpriteIsVisibleAndFlushedToOam`: SCORE glyph tile addresses, initial decimal number, cursor tile 12 and nonzero OAM Y are game/UI requirements, not proof of whole-screen visual correctness.
- **PORT** `SlideLine_MergesEqualPair`, `SlideLine_CompactsAcrossGap`, `MoveRight_MergesRowIntoRightEdge`, `MoveDown_MergesColumnIntoBottomEdge`, `Spawn_FillsExactlyOneEmptyCell`, `Spawn_ReturnsFalseWhenBoardFull`, `CanMove_FalseOnGridlock_TrueWithAnOpening`, `HasWon_DetectsThe2048Tile`, `Render_PaintsBottomRowCellsToTheCorrectTilemapAddress`: injected entry points invoke unmodified Board/Tiles and return observable results; preserve board rules and tilemap addressing. Their code-range stopping and managed symbol offsets are harness details to replace with explicit completion markers/native symbols.
- **REPLACE** `Optimized_SlideLogicMatchesUnoptimized`: optimized/unoptimized equivalence alone can preserve a shared bug; retain the independent expected result 2 and semantic equivalence only if both native modes exist.

### `Samples/CgbHalTests.cs` — 7 methods, 7 cases

- **REPLACE** `CgbHal_CompilesThroughTheRealBackend`: native compilation of referenced Cgb/Ppu methods, not instantiation of `CilFrontend`/`Sm83Backend` or managed diagnostics types.
- **PORT** `TryEnableDoubleSpeed_FlipsKey1Bit7_InCgbMode`: returned success, KEY1 bit 7, emulator double-speed state, and switch disarming.
- **PORT** `TryEnableDoubleSpeed_DoesNothingHarmful_InDmgMode`: false result, no speed state, CPU not parked in STOP.
- **PORT** `SelectVramBank_SelectsBank1_InCgbMode`, `SelectVramBank_IsInertOnDmg`: bank-1/VBK readback on CGB and no switch on DMG.
- **PORT** `SetBackgroundColor_WritesPaletteRam_InCgbMode`, `SetBackgroundColor_WritesTheRequestedPaletteAndColorSlot`: little-endian RGB555 at exact palette/color slots (0/0 and 2/3).

Independent justification: public HAL register behavior and Game Boy mode differences. The harness starts at `Sm83Backend.CodeBase`, assumes Main first, reads HL and stops outside one section; replace those implementation assumptions. The source explicitly excludes real pending-interrupt speed-switch hazards and PLL relock timing: the emulator does not model them. These tests cannot prove physical hardware timing safety.

### `Samples/Gb2048KdbgTests.cs` — 2 methods, 2 cases

- **REPLACE** `Kdbg_ResolvesSymbolAndSourceLocation_ForCilFrontendBuild`: real sample + portable PDB → compiler → ELF/linker → `.kdbg` → `DebugInfoLoader`. Currently only requires some symbol with some source location at the same PC. Preserve symbol/source coexistence, strengthen to a known method and expected original file/line; do not copy old addresses or require the old optimization pipeline.
- **PORT** `Frontend_DoesNotThrow_WhenAssemblyHasNoPdb`: a genuinely PDB-free assembly still compiles without error. Replace the weak managed `module.Functions.Any()` check with executable native output; missing optional debug data must not reject otherwise valid input.

Compiler-origin C# PDB mapping is independent acceptance. Existing assembly-origin DAP tests below do not cover it.

### `Samples/Gb2048V2Tests.cs` — 2 methods, 3 cases

- **REPLACE** `Sample_CompilesUnmodified_NoDiagnostics_ValidIr` (Debug, Release): preserve both Roslyn IL configurations of all unmodified sample files; replace managed IR verification with native ELF validation.
- **PORT** `Sample_BootsToTitle_StartsGame_SlidesSpawnTiles`: title observation, Start transition, exactly two initial cells, bounded cell-count change after input and final count >2 and <=16. This is interactive scene/input/spawn acceptance, not exact board-state validation or win/lose/restart coverage.

### `Samples/GbJrpgTests.cs` — 4 methods, 5 cases

- **REPLACE** `Sample_CompilesUnmodified_NoDiagnostics_ValidIr` (Debug, Release): all real sample sources plus actual PNG-generated Art; preserve both IL configurations, replace managed IR checks.
- **PORT** `Sample_CgbPlaythrough_WalksTalksAndCallsBack`: title → overworld; exact terrain/NPC tile observations; four hero sprites and one-cell movement; no hero residue in vacated BG cells; PNG-authored water RGB555; walk to NPC; three specific dialogue pages; stored close-callback restores overworld and hero position. Helpers handle encounters while walking, but do not assert every combat outcome.
- **REPLACE** `Sample_CaptureAcceptanceScreenshots`: keep bounded arrival at title, overworld, dialogue and encounter with monster tiles; four PNG artifacts under test-output `TestResults/gb-jrpg/<run-id>` and unchanged hashes/file set for tracked screenshots. Existing assertions check file existence and scene memory markers, **not image equality, scene composition or visual quality**. Preserve artifact isolation and add reviewed visual/frame invariants when claiming visual parity.
- **PORT** `Sample_RomHeader_IsCgbExclusive`: linked ROM byte 0x143 == 0xC0. Header flag is a linker contract exercised with compiler output; retain explicit SDK forwarding acceptance too.

### `Samples/Cube3dTests.cs` — 3 methods, 6 cases

- **REPLACE** `Demo_CompilesWithoutDiagnostics` (double-buffered, full-frame): real shared files plus local overrides and referenced HAL, replace managed frontend/backend calls.
- **REPLACE** `Demo_LinksToBootableRom` (same variants): partial NOP/JP/logo check becomes valid header/entry plus boot acceptance.
- **PORT** `Demo_RunOnHardwareRendersARealCube` (double-buffered/CGB/200 frames; full-frame/DMG/250 frames): >=2 nonbackground shades and 50..85%-screen nonbackground pixels. Despite its name it uses `GameBoySystem`, not physical hardware. The thresholds reject blank output but do not prove a cube, orientation, animation or race-free presentation.

The separate `samples/csharp/gb-3d/verify/Program.cs`, `CubeFrameChecks.cs`, `PhaseSweepCheck.cs` are **PORT**, compiler migration owner: preserve all three variants (including racing-beam), both hardware modes, first/second frame comparison, structural checks and Mode3WriteGuard/phase checks. These are separate acceptance tooling, not cases counted above and not proof that CI executes them. Existing comments about measured timing are historical rationale, not fresh measurements. Keep bounded gameplay/render progress, reconsider exact old frame budgets from native output evidence.

### `Samples/GbGfxDemoTests.cs` — 9 methods, 9 cases

- **REPLACE** `Demo_CompilesWithoutDiagnostics`, `Demo_LinksToBootableRom`: real sample acceptance; managed IR verifier and partial header bytes become ELF/header/boot checks.
- **PORT** `WindowMap_SpellsScoreViaFontBasePlusOffset`: stable SCORE glyphs after >=2 completed iterations.
- **PORT** `Lcdc_HasBgObjAndWindowEnabled`: LCD/BG/OBJ/window enabled after initialization.
- **PORT** `Oam_AfterFirstPaintedIteration_MatchesExpectedYXTileAttr`, `Oam_AfterMoreIterations_PositionsChangedAsExpected`: exact four-sprite Y/X/tile/attribute formula at a completed iteration, then progress and updated state.
- **PORT** `Palette_TogglesAcrossASixtyIterationBoundary`: observes actual progress across a theme boundary and BGP change. It explicitly stays below frame-counter byte wrap; wrap behavior is not covered.
- **PORT** `Dmg_Run_HasQuantizedShadesInBgpAndObp`, `Cgb_Run_HasRgb555InPaletteRam`: DMG quantized BGP/OBP0 and CGB BG/OBJ RGB555 colors for the observed theme.

Stable-score synchronization is an observation aid; generated instruction counts, symbol addresses and managed compiler calls are not public requirements. Expected sprite formulas repeat sample behavior and can share mistakes; retain independent position/progress observations.

### `Frontends/TileSheetConverterTests.cs` — 2 methods, 2 cases

- **PORT**, SDK owner within this migration: `SyntheticPng_RoundTripsToPlanesPaletteAndSource` checks 8x8 → 16-byte 2bpp row planes, luminance-ordered palette bookends and generated namespace/member names. The required output is real tile/palette/source compatibility, not this class's location under compiler tests. Retain/move this small managed art tool if useful; replacing the compiler does not require rewriting PNG conversion.
- **PORT**, SDK owner within this migration: `FiveColorSheet_IsARejectedAuthoringError` rejects >4 colors. Keep a clear authoring error at the build boundary even if exception type changes across a process boundary.

The synthetic PNG is RGBA8/filter-0/noninterlaced and writes an Adler32 placeholder ignored by the reader. It does not establish broad PNG support, checksum rejection, transparency semantics, or filesystem/build-task error handling. Do not elevate that permissive fixture to a native PNG contract.

## Helpers and harness replacement

`TestSupport/TestRepo.cs` searches ancestors of test binaries for Koh.slnx. **REPLACE**, compiler migration: explicit fixture/repository paths usable by Rust tests or retain a managed integration runner; preserve failure when sample sources cannot be located. `TestSupport/TestPng.cs` writes scaled RGB24 filter-0 PNG with Adler32 and chunk CRC. **REPLACE**, compiler migration: use the existing emulator framebuffer PNG writer for screenshots where possible; DELETE only the now-unused duplicate helper after equivalent artifacts exist. Neither helper declares tests; neither is evidence of PNG correctness on its own. Keep tracked sample art immutable.

Most old tests compile genuine files with Roslyn but bypass MSBuild, invoke managed compiler APIs, link in-process bridge output, initialize PC/SP manually and often omit a boot ROM. PORT the requirements, REPLACE orchestration with native process/ELF consumption. Successful compilation alone must not replace ROM execution. Synthetic test entries for board rules complement, but do not replace, unmodified real-game boot.

## Minimum features needed to preserve shipped game behavior

These are derived from real sources and acceptance observations, not an assertion every feature is already fully tested or Rust-supported:

- All samples: .NET PE/CIL and referenced Koh.GameBoy resolution; entry discovery, static initialization, integer/byte wrap/narrowing, loops/branches/switches, calls, static and instance state, arrays and initialized constant data; MMIO/pointers/intrinsics, correct calling conventions, ROM/data relocation and startup. Real Release and Debug IL are inputs, not separate source languages.
- 2048 original: board byte arrays, indexed movement/merging, RNG/spawning, score arithmetic, glyph drawing, joypad polling, VRAM and shadow-OAM flush. Independent expected board outcomes must survive optimization.
- 2048 v2: constructors/new objects, reference/readonly fields, virtual scene lifecycle, returned-by-value Line structs, indexers/properties, strings/text, retained scene state and input transitions. Existing sample play test does not assert victory/game-over/restart.
- JRPG: rectangular `byte[,]` world, `string[]` dialogue, objects/inheritance/virtual enemy and scene dispatch, stored `System.Action` callback and closure environment, constructors and persistent hero state, random combat, PNG-backed TileSet/Palette data, CGB attributes and OBJ updates. Existing walk/dialogue test and encounter screenshot do not prove all enemy damage rules, every encounter, leveling/victory/game-over/restart, save persistence or physical CGB operation. These game behaviors must not be silently discarded merely because the old sample tests were narrower.
- 3D: signed fixed-point math, multiply/divide/shifts, loops/arrays and fill/copy intrinsics; CGB/DMG presentation paths, DMA/LCDC bank switching and beam-timed writes. Existing two-case cube test omits racing-beam and cross-mode combinations; separate verifier is the broader source of requirements.
- Gfx demo/HAL: window glyphs, independent sprite motion, palette/theme progress, CGB RGB555 vs DMG quantization, VRAM bank selection, double speed and VBlank/HBlank coordination. Emulator evidence remains distinct from physical timing proof.

## Consumers outside the compiler test project

- **REPLACE**, SDK + compiler migration, required before compiler removal: `src/Compiler/Koh.Build.Tasks/CompileKohRom.cs` currently references CompilerRegistry/CompilerDriver/CompilerInput/EmitModel and forwards reference paths, program name, CGB flags, debug selection and linker path. Replace this orchestration with native compiler output without losing errors/warnings, no-output-on-failed-compilation/link, ROM/.kdbg writing or path handling. `Koh.Build.Tasks.csproj` directly references Koh.Compiler; remove only after the task no longer uses it. `GenerateKohTileSheets.Execute` converts sorted PNG items, generates Art before CoreCompile, reports InvalidDataException/IOException as build errors; keep it working independent of compiler implementation.
- **PORT**, SDK owner within migration: `src/Compiler/Koh.Sdk/Sdk/{Sdk.props,Sdk.targets}` defaults, Koh.GameBoy global using/reference, generated-source include on incremental builds, managed reference binary, dotnet-run emulator launch, game assembly/reference/tool/property-stamp inputs and conditional .kdbg outputs. A native compiler executable path/version must become an incremental input; merely switching the task call can leave stale ROMs. Packaged `pkg/Sdk` files are forwarding shims and intentionally reject projects outside the repo; do not claim a standalone released SDK.
- **REPLACE**, SDK owner within migration: `scripts/verify-rust-sdk.py::main` currently validates native **linker**, not native compiler integration. Its build cases `prepare`, `baseline`, `unchanged`, `cgb-property`, `tool-timestamp`, `tool-selection`, `debug-disabled`, `failed-link`, `restore-defaults` assert paths with spaces, no-op rebuild, CGB/header and debug selection, tool invalidation, failed-link diagnostics and preserved ROM hash. Extend/adapt equivalent compiler-path/timestamp/failure cases; existing script name is not compiler coverage. No dedicated Build.Tasks/SDK test project was found by direct compiler/SDK-reference scan.
- **DEFER**, debugger owner: `DapGeneratedRomIntegrationTests.GeneratedRomKdbg_DapLaunchBreakpointStackTraceScopesAndVariables`, `StackTrace_UsesSourceForPcInsideMultiByteMappedRange` are assembly-generated ROM tests of launch/breakpoint/stack/scopes/register/source/symbol consumers. Keep them unchanged; assembly .kdbg is not C# PDB mapping acceptance. Compiler-origin known-line DAP acceptance is **PORT**, compiler migration, in addition to the compiler kdbg test above.
- **DEFER**, debugger owner: `KdbgFixtureTests.ReaderPreservesExplicitSymbolSizeAndSource` uses a hand-built .kdbg fixture; `HeadlessDapProcessTests.HeadlessSession_DebugsWithoutDisplayAndExits` covers disconnect/terminate/EOF with headless plus disconnect without headless (4 cases). Preserve their reader/process contracts, which do not depend on compiling CIL. Other debugger tests and emulator DebugReadWriteTests consume synthetic ROM/debug fixtures and are not migrated compiler tests.
- **DEFER**, emulator owner: existing CPU/PPU/MMIO/input models remain the execution oracle; no compiler reference was found in emulator test C# projects by the compiler/SDK reference scan. Keep their tests; adding a new compiler cannot turn an unmodeled hardware hazard into validated behavior.
- **REPLACE**, tools + compiler migration: `tests/Tools/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj` and `tools/Koh.Superopt/Koh.Superopt.csproj` reference Koh.Compiler; `tools/Koh.Superopt/Sm83Alphabet.cs` consumes `Koh.Compiler.Backends.Sm83.Mir`. Remove dependency by retaining/moving the needed instruction definition or using the native equivalent, then rerun the tool suite. Test traceability (16 methods): `EnumeratorTests` — `Every_alphabet_op_is_straight_line_and_register_only`, `Rejects_a_memory_op`, `Rejects_a_control_op`, `Rejects_an_sp_writing_op`, `Rejects_an_sp_reading_op`, `Sequences_include_empty_and_grow_to_bound` preserve instruction filtering and enumeration; `MinerTests` — `Rediscovers_LdA0_to_XorA_when_flags_are_dead`, `Declines_LdA0_to_XorA_when_flags_are_live`, `Shrinks_double_move_to_single`, `Every_rewrite_is_a_strict_improvement` preserve liveness-sensitive rewrites and size/cycle improvement; `Sm83OracleTests` — `XorA_equals_LdA0_when_flags_are_dead`, `XorA_differs_from_LdA0_when_flags_are_live`, `Run_reports_tcycles_and_final_state`, `Run_throws_when_the_sequence_never_leaves_its_range` preserve the emulator oracle and runaway guard; `RewriteFormattingTests` — `Describes_a_rewrite_with_hex_and_savings`, `Describes_a_deletion_as_removed` preserve CLI formatting. These are tools requirements independent of compiler internals, not native compiler coverage. Its compiler dependency blocks assembly deletion even though it is not game acceptance.
- **REPLACE**, release + compiler migration: `.github/workflows/ci.yml` builds Koh.Ci.slnf (including SDK sample projects) and full compiler tests; `build.proj` fast Test excludes compiler tests while TestAll includes them. Keep source-to-ROM acceptance in CI after moving tests; cargo workspace success alone cannot replace it. `.github/workflows/rust-assembler.yml` workspace checks/test apply to workspace crates but explicit release packaging currently names only koh-asm/koh-link. Include compiler artifacts/licenses when integrating it; do not infer executable shipping from workspace tests.
- **REPLACE**, release + compiler migration: `.github/workflows/release-toolchain.yml` stages LSP and emulator publish outputs for Linux/Windows/macOS and checks assembler dependency licenses. Audit native compiler payload and dependency attribution for every RID and installer/archive; current recipe does not explicitly publish a compiler. `scripts/publish-release.{sh,ps1}` only promotes a draft and does not test payloads. Release acceptance must execute packaged compiler/SDK workflow, not just repository-local cargo binaries.
- **DEFER**, tools owner: `tools/benchmarks/Dockerfile` installs RGBDS and benchmark scripts; `run-benchmark.sh` invokes /opt/koh/koh-asm and /opt/koh/koh-link for assembly/link benchmarks. It does not currently consume CIL compiler output. Keep assembler/linker benchmark reproducibility; adding compiler benchmarks is optional separate work, not deletion acceptance.

## Retirement gates

Do not delete Koh.Compiler or declare parity until all compiler-owned PORT/REPLACE groups above have native artifact/run evidence, SDK default and incremental/error paths use that compiler, all direct consumers compile without the managed compiler, and shipped payloads contain what those paths invoke. DELETE only obsolete managed harness/IR assertions and unused helpers after meaningful replacements land. No old compiler-owned behavior is deferred because it is difficult; noncompiler DEFERs retain independent owners and do not reduce these gates.
