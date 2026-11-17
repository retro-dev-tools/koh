# Compiler test inventory

Baseline: `9cd147265eb0c660d94c7bf4eee6a97d9e1f4e1f`.

This is the inventory input to the mandatory semantic disposition matrix, **not
that completed migration**. Source-level dispositions now live in the
[frontend](frontend-audit.md), [backend](backend-audit.md) and
[integration](integration-audit.md) audits. Completed native coverage is recorded
in [the current disposition status](test-disposition.md). No source methods have
been physically retired, and no compiler-owned coverage is deferred.

Mechanical source counts: 565 `[Test]` attributes, 390 `[Arguments(...)]`
attributes and 1,524 `Assert.That(...)` call sites. The scan matches test/argument
attributes at line starts and permits whitespace/newlines in `Assert.That`.
Counts do not represent independently justified scenarios or executed assertions:
parameterization and loops expand execution, and embedded C# fixtures need review.

Current source-method totals:232/565 complete, comprising231 native behavioral
replacements and one obsolete-parser DELETE decision;333 remain outstanding.
These count source methods, not distinct Rust declarations or parameter-expanded
executions. Scenario/assertion accounting belongs to each completed group's
matrix; a workspace test count does not prove source coverage. For example, the
three [symbol-address methods](symbol-addresses.md) map to four rstest cases and
16 ROM executions, preserving or replacing all seven source assertion sites.
The native compiler and C# emulator process harness now exist. Existing C# tests
remain until full replacement acceptance and final compiler-project removal.
Current workspace/all-feature discovery lists1,823 cases, including1,646 compiler
cases and zero ignored compiler cases. The symbol-address group's four cases
are explicitly present. These are discovery counts, not complete-migration proof.

For each row, the completed disposition must identify individual requirements,
independent justification, public versus implementation contracts, PORT / REPLACE /
DELETE / DEFER decision, Rust test location and nextest discovery. DEFER is only
valid for another active component, never for compiler coverage. Do not assign a
group-wide decision without reading its cases and assertions.

`TileSheetConverterTests` belongs to a retained build-time component; its current
location inside the compiler suite does not make it obsolete. Similarly inspect
`DisassemblerLengthTests` ownership rather than deleting it with compiler internals.

| Source file (relative to compiler test project) | Test attributes | Argument attributes | Assertion call sites |
| --- | ---: | ---: | ---: |
| [Backends/MirDecoderTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/MirDecoderTests.cs) | 19 | 0 | 81 |
| [Backends/Sm83BackendTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83BackendTests.cs) | 20 | 0 | 41 |
| [Backends/Sm83CompareBranchFusionTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83CompareBranchFusionTests.cs) | 7 | 18 | 9 |
| [Backends/Sm83ControlFlowTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83ControlFlowTests.cs) | 54 | 0 | 67 |
| [Backends/Sm83EmitterTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83EmitterTests.cs) | 1 | 0 | 2 |
| [Backends/Sm83EncodingTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83EncodingTests.cs) | 4 | 0 | 33 |
| [Backends/Sm83GepFusionTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83GepFusionTests.cs) | 5 | 0 | 7 |
| [Backends/Sm83LoopPointerPhiResidencyTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83LoopPointerPhiResidencyTests.cs) | 5 | 2 | 19 |
| [Backends/Sm83OpsTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83OpsTests.cs) | 3 | 0 | 16 |
| [Backends/Sm83PeepholeTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Backends/Sm83PeepholeTests.cs) | 38 | 0 | 43 |
| [Frontends/CilBgWinTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilBgWinTests.cs) | 5 | 2 | 50 |
| [Frontends/CilCanvasTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilCanvasTests.cs) | 7 | 2 | 36 |
| [Frontends/CilClassStringFieldTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilClassStringFieldTests.cs) | 1 | 2 | 2 |
| [Frontends/CilDelegateTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilDelegateTests.cs) | 8 | 8 | 22 |
| [Frontends/CilEndToEndTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilEndToEndTests.cs) | 3 | 4 | 10 |
| [Frontends/CilFloatTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilFloatTests.cs) | 22 | 67 | 16 |
| [Frontends/CilFontTextTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilFontTextTests.cs) | 5 | 4 | 31 |
| [Frontends/CilFrameworkTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilFrameworkTests.cs) | 5 | 10 | 14 |
| [Frontends/CilGenericTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilGenericTests.cs) | 6 | 8 | 20 |
| [Frontends/CilGraphicsSlice2Tests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilGraphicsSlice2Tests.cs) | 3 | 2 | 12 |
| [Frontends/CilInterruptTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilInterruptTests.cs) | 5 | 8 | 9 |
| [Frontends/CilIntrinsicIndexTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilIntrinsicIndexTests.cs) | 4 | 0 | 8 |
| [Frontends/CilIteratorTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilIteratorTests.cs) | 3 | 4 | 7 |
| [Frontends/CilKohRuntimeTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilKohRuntimeTests.cs) | 3 | 4 | 6 |
| [Frontends/CilLinqTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilLinqTests.cs) | 5 | 4 | 10 |
| [Frontends/CilLiteralTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilLiteralTests.cs) | 6 | 6 | 25 |
| [Frontends/CilLoweringTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilLoweringTests.cs) | 12 | 20 | 25 |
| [Frontends/CilMapWriterGdmaTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilMapWriterGdmaTests.cs) | 2 | 0 | 13 |
| [Frontends/CilMdArrayTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilMdArrayTests.cs) | 2 | 4 | 5 |
| [Frontends/CilNarrowingEndToEndTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilNarrowingEndToEndTests.cs) | 6 | 6 | 11 |
| [Frontends/CilPalettesRgbTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilPalettesRgbTests.cs) | 5 | 4 | 27 |
| [Frontends/CilRecursionTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilRecursionTests.cs) | 6 | 6 | 14 |
| [Frontends/CilReferenceTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilReferenceTests.cs) | 11 | 14 | 35 |
| [Frontends/CilSpritePaletteHardwareTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritePaletteHardwareTests.cs) | 4 | 4 | 15 |
| [Frontends/CilSpritesCgbTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritesCgbTests.cs) | 1 | 0 | 15 |
| [Frontends/CilSpritesTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritesTests.cs) | 4 | 2 | 33 |
| [Frontends/CilStaticsTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilStaticsTests.cs) | 10 | 12 | 35 |
| [Frontends/CilStoredDelegateTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilStoredDelegateTests.cs) | 3 | 6 | 2 |
| [Frontends/CilStringLiteralTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilStringLiteralTests.cs) | 7 | 8 | 22 |
| [Frontends/CilStructReturnTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructReturnTests.cs) | 5 | 10 | 2 |
| [Frontends/CilStructTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructTests.cs) | 12 | 2 | 6 |
| [Frontends/CilTileSetTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilTileSetTests.cs) | 6 | 2 | 27 |
| [Frontends/CilVideoJoypadTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilVideoJoypadTests.cs) | 4 | 4 | 25 |
| [Frontends/CilVirtualDispatchTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilVirtualDispatchTests.cs) | 5 | 8 | 10 |
| [Frontends/CilWideArithmeticTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/CilWideArithmeticTests.cs) | 14 | 28 | 28 |
| [Frontends/TileSheetConverterTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Frontends/TileSheetConverterTests.cs) | 2 | 0 | 9 |
| [Ir/IrBuilderAndVerifierTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/IrBuilderAndVerifierTests.cs) | 5 | 0 | 5 |
| [Ir/IrLivenessTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/IrLivenessTests.cs) | 4 | 0 | 15 |
| [Ir/IrRoundTripTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/IrRoundTripTests.cs) | 7 | 0 | 20 |
| [Ir/LoopInvariantCodeMotionTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/LoopInvariantCodeMotionTests.cs) | 6 | 0 | 23 |
| [Ir/NarrowPassTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/NarrowPassTests.cs) | 19 | 0 | 73 |
| [Ir/OptimizationTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/OptimizationTests.cs) | 36 | 0 | 132 |
| [Ir/RedundantBankSelectEliminationTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/RedundantBankSelectEliminationTests.cs) | 6 | 0 | 17 |
| [Ir/RedundantCompareEliminationPassTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/RedundantCompareEliminationPassTests.cs) | 5 | 0 | 23 |
| [Ir/RedundantConversionEliminationPassTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Ir/RedundantConversionEliminationPassTests.cs) | 6 | 0 | 20 |
| [Samples/CgbHalTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Samples/CgbHalTests.cs) | 7 | 0 | 15 |
| [Samples/CilGame2048Tests.cs](../../tests/Compiler/Koh.Compiler.Tests/Samples/CilGame2048Tests.cs) | 16 | 0 | 32 |
| [Samples/Cube3dTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Samples/Cube3dTests.cs) | 3 | 6 | 8 |
| [Samples/Gb2048KdbgTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Samples/Gb2048KdbgTests.cs) | 2 | 0 | 8 |
| [Samples/Gb2048V2Tests.cs](../../tests/Compiler/Koh.Compiler.Tests/Samples/Gb2048V2Tests.cs) | 2 | 2 | 6 |
| [Samples/GbGfxDemoTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Samples/GbGfxDemoTests.cs) | 9 | 0 | 36 |
| [Samples/GbJrpgTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Samples/GbJrpgTests.cs) | 4 | 2 | 33 |
| [Sm83/DisassemblerLengthTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Sm83/DisassemblerLengthTests.cs) | 1 | 0 | 1 |
| [Sm83/FlushRunLoopResidencyTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Sm83/FlushRunLoopResidencyTests.cs) | 2 | 0 | 11 |
| [Sm83/MemRuntimeTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Sm83/MemRuntimeTests.cs) | 10 | 0 | 15 |
| [Sm83/RedundantCompareLoopTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Sm83/RedundantCompareLoopTests.cs) | 4 | 0 | 9 |
| [Sm83/Sm83LoopCodegenTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Sm83/Sm83LoopCodegenTests.cs) | 20 | 0 | 30 |
| [Sm83/Sm83SymbolAddressTests.cs](../../tests/Compiler/Koh.Compiler.Tests/Sm83/Sm83SymbolAddressTests.cs) | 3 | 0 | 7 |
| [SoftFloatDifferentialTests.cs](../../tests/Compiler/Koh.Compiler.Tests/SoftFloatDifferentialTests.cs) | 20 | 85 | 30 |
