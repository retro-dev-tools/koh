# CIL frontend migration audit

The original audit was source inspection only. Later completed replacements are
explicitly marked and linked below; unmarked dispositions remain proposals and
do not establish Rust coverage. The authoritative completion count and evidence
are in [test disposition status](test-disposition.md). No compiler-owned behavior
is deferred to another migration. TileSheetConverterTests belongs to the
integration audit.

## Inventory and disposition rules

35 frontend source files plus SoftFloatDifferentialTests.cs; 230 `[Test]` methods (parameterized rows are not separate methods). All methods are explicitly enumerated below. Implementation inventory: 17 CIL files and 33 GameBoy C# sources. Counts exclude generated bin/obj sources by their current absence from this source inventory.

PORT means keep the observable requirement and fixture inputs in a Rust-driven DLL-to-ELF-to-ROM test; it does not mean translating legacy assertions verbatim. REPLACE means preserve the useful requirement with an implementation-independent oracle or strengthen an incomplete oracle. DELETE applies only to redundant legacy implementation assertions after the replacement passes, never to the underlying feature. Debug/Release equality must accompany an independently expected result and a completion marker with bounded execution; equality alone can accept two wrong runs. The future groups below are proposed test modules, not existing coverage.

## Test-level traceability

Each file forms a coherent group under the stated behavioral requirement. Every enumerated method inherits PORT unless explicitly marked REPLACE below. This includes parameterized inputs attached to each method; retain all existing argument rows.

### CilBgWinTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilBgWinTests.cs`. Future Rust test group: `cil_bg_win`.

Background/window map coordinates, fill/draw byte contents, CGB bank-1 attributes, DMG no-op behavior and safe LCD-on batched writes. Justification comes from authored graphics intent and hardware memory layout. The existing Bg/Win API is evidence of that intent, not a contract or design convention to preserve.

- **PORT** `BgFillAndDrawMap_WriteExpectedMapBytes`.
- **PORT** `WinFillAndDrawMap_WriteExpectedMapBytes`.
- **PORT** `SetAttrFillAttr_OnCgb_WriteVramBank1NotTileIndexMap`.
- **PORT** `SetAttrFillAttr_OnDmg_IsTrueNoOp`.
- **PORT** `BatchedFullBoardRedraw_WithLcdOn_ProducesZeroMode3Violations`.

### CilCanvasTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilCanvasTests.cs`. Future Rust test group: `cil_canvas`.

Tile-planar pixel placement, shade/dither masks, line/triangle rasterization and double-buffer presentation with no mode-3 writes are useful authoring capabilities. Existing API names and shapes are not a baseline; preserve independently justified pixels and hardware behavior, with buffer addresses and DMA choice treated as implementation choices unless hardware requires them.

- **PORT** `Init_Clear_SetPixel_Present_LcdOff_MatchesExpectedBuffer`.
- **PORT** `FillRect_SolidShade_FillsEveryPlaneByteFF`.
- **PORT** `FillSpan_DitherShade_ProducesExpectedPlaneBytesPerRow`.
- **PORT** `DrawLine_LcdOn_NeverWritesDuringMode3_AndDrawsExpectedDiagonal`.
- **PORT** `FillTriangle_LcdOn_NeverWritesDuringMode3_AndFillsExpectedSpans`.
- **PORT** `Present_DoubleBuffered_LcdOn_FlipsLcdcBit4_NeverWritesDuringMode3`.
- **PORT** `Present_Cgb_UsesGdma_NeverWritesDuringMode3_AndMatchesExpectedBuffer`.

### CilClassStringFieldTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilClassStringFieldTests.cs`. Future Rust test group: `cil_class_string_field`.

A constructor string argument must survive field storage and later length/index reads. Ordinary reference-value flow and Text.Draw callers justify this; no particular string header layout is required.

- **PORT, complete** `StringCtorParameter_StoredInClassField_ReadBackViaLengthAndIndexer` — `driver::native_object_identity_fields_and_faults`, `ObjectCore::BannerFields`, exact string/score packets and heap/bank checks in Debug/Release × DMG/CGB; see test-disposition.md.

### CilDelegateTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilDelegateTests.cs`. Native Rust group: `driver::delegates`; see the linked disposition matrix.

Captured/noncaptured callbacks, parameter passage and sealed virtual method
groups must invoke the correct receiver and target in both Roslyn build modes.
The legacy virtual-group diagnostic is replaced by valid virtual execution;
open-instance adaptation remains an explicit unsupported shape. Direct calls
and blob dispatch are optional mechanisms. All 8 methods have completed native
[replacements](test-disposition.md#callback-replacements), included in the
completed-method total after the full 1,193-case regression passed.

- **PORT, complete** `NoCaptureLambda_VerifiesCleanAndInvokesResolvedTarget`.
- **REPLACE, complete** `NoCaptureLambda_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT, complete** `CapturingLambda_VerifiesCleanAndInvokesResolvedTarget`.
- **REPLACE, complete** `CapturingLambda_DebugAndReleaseProduceIdenticalObservableState`.
- **REPLACE, complete** `DelegateThroughParameter_LowersViaBlobDispatch`.
- **REPLACE, complete** `CallvirtOnSealedDeclaringType_DevirtualizesToDirectCall`.
- **REPLACE, complete** `VirtualMethodGroupDelegate_IsDiagnosedNotSilentlyMisresolved`.
- **PORT, complete** `VirtualMethodGroupDelegate_ResolvesToSealedOverrideWhenTokenIsSealed`.

### CilEndToEndTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilEndToEndTests.cs`. Future Rust test group: `cil_end_to_end`.

Real compiled assemblies must reach ROM execution with correct register loops, memory-region pointers and control intrinsics supplied through metadata. This is the source-to-ROM user contract, not a requirement to reproduce old IR.

- **PORT** `RegisterPokeLoop_VerifiesCleanAndProducesExpectedState`.
- **PORT** `RegisterPokeLoop_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `RegionPointerAndControlIntrinsic_LoweredFromAttributeAlone`.

### CilFloatTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilFloatTests.cs`. Future Rust test group: `cil_float`.

Float operations, conversions and byrefs must preserve the declared target numeric contract and diagnose unsupported operations. Keep runtime-attribute extensibility; exact helper names and call instruction shapes are not numeric semantics.

- **PORT COMPLETE** `F32_Arithmetic_MatchesHost` — all9 arithmetic rows in both modes/cartridges; [assertion matrix](softfloat-division.md#additional-source-dispositions).
- **REPLACE COMPLETE** `F32_Add_RoutesThroughRealSoftFloatFunction` — exact native addition and metadata-selected helper renaming replace the private helper name; [matrix](softfloat-division.md#additional-source-dispositions).
- **PORT COMPLETE** `F32_Compare_MatchesHost` — all original pairs and predicates plus special values, `driver/float_compare.rs`; [traceability](float-lowering.md#ordered-and-unordered-comparisons).
- **PORT COMPLETE** `F32_CompareInBranchPosition_MatchesHost` — all original rows plus every short/long relational branch encoding, `driver/float_compare.rs`.
- **PORT COMPLETE** `F32_TernaryMin_MatchesHost` — all three original pairs with subsequent float addition and exact bit results, `driver/float_compare.rs`.
- **PORT COMPLETE** `F32_Neg_MatchesHost` — all finite rows in both modes plus special values, `driver/float32.rs`; [traceability](float-lowering.md#source-test-dispositions).
- **REPLACE COMPLETE** `F32_Neg_RoutesThroughRealSoftFloatFunction` — native full-bit negation results replace the required helper name; [rationale](float-lowering.md#source-test-dispositions).
- **PORT COMPLETE** `F32_ToInt_MatchesHost` — all3 original inputs in both modes plus limits/special values, `driver/float_conversion.rs`; [matrix](float-conversions.md#evidence-and-disposition).
- **PORT COMPLETE** `F32_FromInt_MatchesHost` — all5 original inputs in both modes plus unsigned/native cases, `driver/float_conversion.rs`; [matrix](float-conversions.md#evidence-and-disposition).
- **PORT COMPLETE** `F32_ToF64_MatchesHost` — exact native results/guards in both modes and cartridges; [precision-cast/byref matrix](binary64.md#precision-casts).
- **REPLACE COMPLETE** `F32_ToF64_RoutesThroughRealSoftFloatFunction` — metadata helper rename/missing-key/wrong-signature checks; [matrix](binary64.md#precision-casts).
- **PORT COMPLETE** `F64_ToF32_MatchesHost` — exact native results/guards in both modes and cartridges; [precision-cast/byref matrix](binary64.md#precision-casts).
- **REPLACE COMPLETE** `F64_ToF32_RoutesThroughRealSoftFloatFunction` — metadata helper rename/missing-key/wrong-signature checks; [matrix](binary64.md#precision-casts).
- **PORT COMPLETE** `F64_ToInt_MatchesHost` — all original inputs in both modes, exact native integer/float bits and guards; [integer-cast matrix](binary64.md#integer-casts).
- **PORT COMPLETE** `F64_FromInt_MatchesHost` — all original inputs in both modes, exact native integer/float bits and guards; [integer-cast matrix](binary64.md#integer-casts).
- **PORT COMPLETE** `F64_FromUInt_MatchesHost` — all original inputs in both modes, exact native integer/float bits and guards; [integer-cast matrix](binary64.md#integer-casts).
- **PORT COMPLETE** `F64_Arithmetic_MatchesHost` — all4 original arithmetic rows in both modes with independent full-bit results/guards, `driver/float64.rs`; [multiplication](binary64.md#multiplication).
- **PORT COMPLETE** `F64_Compare_MatchesHost` — all4 original rows in both modes plus NaN, signed-zero, negative and fused-branch coverage, `driver/float64.rs`; [matrix](binary64.md#native-tests-and-source-disposition).
- **PORT COMPLETE** `F32_RefParameter_ReadsAndWritesThroughIndirection` — exact native results/guards in both modes and cartridges; [precision-cast/byref matrix](binary64.md#precision-casts).
- **PORT COMPLETE** `F32_Rem_NoMatchingRuntimeKey_IsADiagnosticNotAMiscompile` — missing `f32.rem` metadata rejects actual CIL5.5%2 with member/IL context; the default library now executes it correctly, [matrix](floating-remainder.md#native-behavior-and-source-disposition).
- **PORT COMPLETE** `F32_Arithmetic_DebugAndReleaseProduceIdenticalObservableState` — independent exact0.1+0.2 bits, completion/guards in each mode; [matrix](softfloat-division.md#additional-source-dispositions).
- **PORT COMPLETE** `F64_Arithmetic_DebugAndReleaseProduceIdenticalObservableState` — independent1/3 full-bit result, completion and guards in each mode, `driver/float64.rs`; [division](binary64.md#division).

### CilFontTextTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilFontTextTests.cs`. Future Rust test group: `cil_font_text`.

Built-in font expansion, BG/window glyph maps, byte-array text and decimal alignment/padding including zero. Public text rendering and hardware bitplanes justify exact content checks.

- **PORT** `Draw_WritesExpectedMapCellsAndExpandsGlyphPixels`.
- **PORT** `DrawToWindow_WritesGlyphIndicesToWindowMap`.
- **PORT** `DrawNumber_RightAligned_SpacePadsToWidth`.
- **PORT** `DrawNumber_LeftAligned_NoPadding_HandlesZero`.
- **PORT** `Draw_ByteArrayOverload_WritesExpectedGlyphIndices`.

### CilFrameworkTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilFrameworkTests.cs`. Future Rust test group: `cil_framework`.

Deterministic RNG, timer restart/one-shot behavior, asset handles, input edges/repeat, boot/frame clock integration. These support ordinary game code and desktop/ROM agreement, independent of lowering architecture.

- **PORT** `Rng_MatchesDesktopSequenceAndDerivedDraws`.
- **PORT** `Timer_CountsDownFiresOnceAndRestarts`.
- **PORT** `Assets_StaticStructHandles_LoadVramAndDrawMap`.
- **PORT** `Input_LatchedEdgesAndRepeatCadence`.
- **PORT** `GameBootAndEndFrame_TickClockAndLatchInput`.

### CilGenericTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilGenericTests.cs`. Native groups: `driver::generics` and `driver::wide_arithmetic::legacy_generic_wide_expressions_match_complete_results`; see [contract and dispositions](generic-methods.md). All six methods have complete behavioral replacements.

Transitive generic-method specialization at multiple concrete types plus signed/unsigned 64-bit arithmetic and extension. CIL type substitution and integer semantics justify results, not mangled instance names.

- **REPLACE, complete** `GenericMethod_TwoTypesTransitively_ProducesExpectedValue`.
- **REPLACE, complete** `GenericMethod_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT, complete** `LongArithmetic_AddSubMulDivRem_MatchesHostComputedValue`.
- **PORT, complete** `ConvI8SignExtends_ConvU8ZeroExtends_MatchesHostComputedValue`.
- **PORT, complete** `UlongDivision_IsUnsigned_NotArithmeticShift`.
- **REPLACE, complete** `WideIntArithmetic_DebugAndReleaseProduceIdenticalObservableState`: independently expected complete bytes in each mode, plus mode equality.

### CilGraphicsSlice2Tests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilGraphicsSlice2Tests.cs`. Future Rust test group: `cil_graphics_slice2`.

Static buffer alignment and safe OAM-DMA execution/return are hardware requirements. The first allocation address and exact cursor movement are legacy allocator assumptions.

- **REPLACE, complete** `KohAligned_RoundsStaticWramGlobalCursorUpToAlignment` — native `driver::aligned_static_arrays_have_aligned_mutable_payloads` checks actual aligned payloads after padding, scalar storage, identity and capacity; legacy fixed-address assertions are intentionally replaced. See [traceability](test-disposition.md#alignment-and-real-video-initialization).
- **PORT** `RunOamDma_TriggersRealDmaAndReturnsWithBusUnlocked`.
- **PORT** `RunOamDma_DebugAndReleaseProduceIdenticalObservableOam`.

### CilInterruptTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilInterruptTests.cs`. Future Rust test group: `cil_interrupt`.

Metadata selects correct vectors, executable handlers fire, unknown vectors and recursive handler call graphs diagnose. Interrupt ABI/state preservation is required; old IrFunction property layout is not.

- **PORT, complete** `Interrupt_SetsVectorOnTheLoweredFunction` — native `driver::interrupts` executes all five vectors, real PPU counts2/3/5 in D/R × DMG/CGB, rejects invalid metadata/recursive bodies and checks register/stack preservation; see [leaf IRQ contract and traceability](interrupts.md).
- **PORT, complete** `Interrupt_ActuallyFires_OnGameBoySystem` — native `driver::interrupts` executes all five vectors, real PPU counts2/3/5 in D/R × DMG/CGB, rejects invalid metadata/recursive bodies and checks register/stack preservation; see [leaf IRQ contract and traceability](interrupts.md).
- **PORT, complete** `Interrupt_DebugAndReleaseProduceIdenticalObservableState` — native `driver::interrupts` executes all five vectors, real PPU counts2/3/5 in D/R × DMG/CGB, rejects invalid metadata/recursive bodies and checks register/stack preservation; see [leaf IRQ contract and traceability](interrupts.md).
- **PORT, complete** `UnknownInterruptKind_ReportedAsDiagnostic` — native `driver::interrupts` executes all five vectors, real PPU counts2/3/5 in D/R × DMG/CGB, rejects invalid metadata/recursive bodies and checks register/stack preservation; see [leaf IRQ contract and traceability](interrupts.md).
- **PORT, complete** `RecursiveInterruptHandler_IsRejected` — native `driver::interrupts` executes all five vectors, real PPU counts2/3/5 in D/R × DMG/CGB, rejects invalid metadata/recursive bodies and checks register/stack preservation; see [leaf IRQ contract and traceability](interrupts.md).

### CilIntrinsicIndexTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilIntrinsicIndexTests.cs`. Future Rust test group: `cil_intrinsic_index`.

Both property accessors and ordinary methods must resolve intrinsic kind/address from referenced metadata. LCDC=$FF40, VRAM=$8000 and HALT are hardware contracts; Mono.Cecil dictionary identity is not.

- **PORT** `LcdcSetter_MapsToRegisterAddress`.
- **PORT** `LcdcGetter_IsAlsoIndexed`.
- **PORT** `GbVramGetter_MapsToRegionAddress`.
- **PORT** `HardwareHalt_MapsToAddresslessControlIntrinsic`.

### CilIteratorTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilIteratorTests.cs`. Future Rust test group: `cil_iterator`.

Generated yield state machines and independent foreach consumers retain their own state and dispose on normal structured exit. C# iteration behavior justifies observable sequences; concrete-type tracking is optional.

- **PORT** `ForeachOverIterator_VerifiesCleanAndComputesExpectedValue`.
- **PORT** `ForeachOverIterator_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `TwoIndependentForeachLoops_VerifiesCleanAndComputesExpectedValue`.

### CilKohRuntimeTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilKohRuntimeTests.cs`. Native group: `driver::softfloat_bits`; all three complete. See [matrix and independent oracles](softfloat-bits-tests.md).

Referenced SoftFloat code must be discoverable, lower transitively and produce correct AddF32 bits. Actual referenced-body execution replaces the old IR display-name assertion. This does not establish float-CIL runtime-key dispatch support.

- **REPLACE, complete** `SoftFloatAddF32_LowersCleanlyAsOrdinaryReferencedCode` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT, complete** `SoftFloatAddF32_ComputesTheCorrectIeeeBits`.
- **PORT, complete** `SoftFloatAddF32_DebugAndReleaseProduceIdenticalObservableState`.

### CilLinqTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLinqTests.cs`. Future Rust test group: `cil_linq`.

Where/Select/Sum and direct-array Max must compute correct reductions and reject unsupported chains clearly. Preserve this useful source API; old fused-loop IR and provenance restrictions are not mandatory.

- **PORT** `WhereSelectSum_VerifiesCleanAndComputesExpectedValue`.
- **PORT** `WhereSelectSum_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `MaxDirectlyOnArray_VerifiesCleanAndComputesExpectedValue`.
- **PORT** `MaxDirectlyOnArray_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `MaxAfterPipeline_IsDiagnosedNotThrown`.

### CilLiteralTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLiteralTests.cs`. Future Rust test group: `cil_literal`.

Array and UTF-8 literals retain exact bytes; Int128 arithmetic retains integer behavior. ROM deduplication/startup elimination is an optimization requiring a mutability proof, not C# array semantics.

- **REPLACE** `LocalArrayLiteral_LandsInRomWithInitialBytesNotRebuiltAtStartup` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `LocalArrayLiteral_DebugAndReleaseProduceIdenticalObservableState`.
- **REPLACE** `U8Literal_LandsInRomWithInitialBytesNotRebuiltAtStartup` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `U8Literal_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `Int128Arithmetic_RoundTripsThroughRealArithmeticOnTheHost`.
- **PORT** `Int128Arithmetic_DebugAndReleaseProduceIdenticalObservableState`.

### CilLoweringTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLoweringTests.cs`. All 12 complete in native `driver::cil_basics`, with `lowering::cil_stack_join_becomes_typed_block_arguments`; see [matrix and assertion accounting](cil-basics.md).

Register IO, ordinary calls, stack joins, arithmetic/comparisons/conversions and array lengths across calls must behave correctly. The CIL evaluation stack and public API justify outputs; spill count/fixed-global IR representation do not.

- **PORT, complete** `RegisterLoop_WritesExpectedValue_DebugAndRelease`.
- **REPLACE, complete** `RegisterLoop_IrShape_HasEntryFunctionAndFixedAddressGlobal` — actual default-entry ROM and MMIO effect.
- **PORT, complete** `RegisterReadThenWrite_RoundTripsThroughFixedAddressGlobal`.
- **PORT, complete** `CallToGameModuleStaticMethod_IsLoweredAsDirectCall`.
- **PORT, complete** `Ternary_NonEmptyStackJoin_IsSpilledCorrectly`.
- **REPLACE, complete** `Ternary_Release_ActuallyExercisesSpillAllocas` — both native branches and explicit hand-CIL typed join invariants.
- **PORT, complete** `ArithmeticAndCompareOpcodes_ProduceExpectedValue`.
- **PORT, complete** `UnsignedAndInequalityLoops_ProduceExpectedValue`.
- **PORT, complete** `NarrowingConversionOpcodes_ProduceExpectedValue`.
- **PORT, complete** `UnsupportedExternalCall_ReportsDiagnostic_DoesNotThrow`.
- **PORT, complete** `ArrayLength_LoopBoundSumsAllElements_DebugAndRelease`.
- **PORT, complete** `ArrayLength_OnUntraceableArray_ReadsRuntimeLengthPrefix`.

### CilMapWriterGdmaTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilMapWriterGdmaTests.cs`. Future Rust test group: `cil_map_writer_gdma`.

LCD-on CGB scattered map updates complete safely and DMG updates eventually drain correctly. Safe content/frame behavior is public runtime behavior; exact drip count or GDMA implementation is not a universal language requirement.

- **PORT** `Cgb_ScatteredRun_LandsCompletelyAfterOneEndFrame_ViaGdma`.
- **PORT** `Dmg_ScatteredRun_StillDripsAcrossSeveralFrames_DripPathUnchanged`.

### CilMdArrayTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilMdArrayTests.cs`. Future Rust test group: `cil_md_array`.

Rank-2 array construction/indexing, ROM-initializer and heap paths, dimension headers and string-reference elements must retain values and lengths across calls. Preserve useful arrays; header representation is internal.

- **PORT** `RectangularArrays_RomFoldHeapCtorAndHeaderPath`.
- **PORT** `StringArrays_ReferenceElementsAndLengths`.

### CilNarrowingEndToEndTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilNarrowingEndToEndTests.cs`. Future Rust test group: `cil_narrowing_end_to_end`.

Unsigned byte ordering and wrapping accumulation must survive optimization in Debug/Release. ROM-size and cycle improvements merit a separately calibrated optimization benchmark, not semantic coupling to an old pass.

- **PORT** `ByteCompare_UnsignedOrderingIsCorrectThroughNarrowing`.
- **PORT** `ByteCompare_DebugAndReleaseAgree`.
- **REPLACE** `ByteCompare_NarrowPassActuallyFiredOnTheCompare` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `ByteAccumulateLoop_ProducesTheCorrectSum`.
- **PORT** `ByteAccumulateLoop_DebugAndReleaseAgree`.
- **REPLACE** `ByteAccumulateLoop_NarrowingReducesRomSizeAndCycles_MeasuredDelta` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.

### CilPalettesRgbTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilPalettesRgbTests.cs`. Future Rust test group: `cil_palettes_rgb`.

DMG shade registers, CGB palette RAM, unsupported DMG slots and RGB555 channel packing follow documented public API/hardware rules. Exact packing is independently defined by hardware.

- **PORT** `SetBgSetObj_OnDmg_WriteDmgShadesToBgpAndObp0`.
- **PORT** `SetObj_OnDmg_Slot1_WritesObp1`.
- **PORT** `SetBgSetObj_OnCgb_WriteRgb555ColorsToPaletteRam`.
- **PORT** `SetBgSetObj_OnDmg_SlotsAboveHardwareRange_AreNoOps`.
- **PORT** `RgbMake_PacksChannelsIntoRgb555`.

### CilRecursionTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilRecursionTests.cs`. Native group: `driver::recursion`; all six methods complete. See the [assertion audit and corrected capacity failure](recursion-tests.md).

Direct/mutual/deep recursive calls must preserve caller locals, arguments, returns and bounded stack behavior. Fibonacci/parity results are independent oracles; exact stack placement is an ABI choice.

Real Roslyn Fib(10)=55 and IsEven(10)/IsEven(7) now execute in Debug and Release,
with full I32 results, actual MMIO, banked helpers and untouched memory checks.
The legacy Sum(500) case expects66 after byte truncation; its exact reproducer
now succeeds through a restricted pure modular accumulator transformation.
A smaller successful depth or an overflow trap alone does not replace that
legacy success assertion. Any intentional capacity change needs a visible
hardware-cost rationale and bounded success/fault evidence, not silent omission.
All six methods are complete. Effectful/general recursion remains bounded by the ABI; arbitrary depth500 is not promised.

- **PORT, complete** `DirectRecursion_Fibonacci_ComputesExpectedValue`.
- **PORT, complete** `DirectRecursion_Fibonacci_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT, complete** `MutualRecursion_IsEvenIsOdd_ComputesExpectedValues`.
- **PORT, complete** `MutualRecursion_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT, complete** `DeepRecursion_ExercisesRelocatedWramCallStack`.
- **PORT, complete** `DeepRecursion_DebugAndReleaseProduceIdenticalObservableState`.

### CilReferenceTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilReferenceTests.cs`. Future Rust test group: `cil_reference`.

Reachable non-BCL library bodies lower transitively; unsupported BCL calls diagnose; Mem arena blocks are distinct and reset reclaims them. On-demand graph traversal and exact heap-top addresses are old mechanisms, while bounded ROM size and valid allocation matter.

- **PORT** `HalAndMemMethods_LowerOnDemandAndProduceExpectedState`.
- **PORT** `HalAndMemMethods_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `SingleSetTile_LowersOnDemandAndWritesTheRightCell`.
- **PORT** `TilemapClear_TransitivelyLowersSetTileAndClearsEveryCell`.
- **PORT** `TilemapClear_DebugAndReleaseProduceIdenticalObservableState`.
- **REPLACE** `UncalledHalMethods_AreNeverLoweredIntoTheModule` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `CallIntoBcl_IsADiagnosticNeverLoweredOrMiscompiled`.
- **REPLACE** `MemAlloc_IsACompilerOwnedIntrinsic_AndAllocatesAtTheExpectedAddress` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `MemAlloc_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `Arena_AllocatesDistinctBlocks`.
- **PORT** `Arena_ResetReclaimsEverything`.

### CilSpritePaletteHardwareTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritePaletteHardwareTests.cs`. Future Rust test group: `cil_sprite_palette_hardware`.

New MMIO registers reach hardware and OAM DMA copies the entire page on target and desktop reference host. Hardware addresses/copy contents are independent; compiler and host evidence must remain distinct.

- **PORT** `RegisterRoundTrip_NewSpritePaletteRegistersReachRealAddresses`.
- **PORT** `OamDmaTrigger_CopiesSourcePageIntoRealOam`.
- **PORT** `OamDmaTrigger_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `DesktopHost_HardwareDmaSetterCopiesWramPageIntoGbOam`.

The two OamDmaTrigger methods now map to the raw-trigger branch of
`driver.rs::oam_dma_runs_from_relocated_hram_and_preserves_banks`. Each target/
configuration checks independent page/readback/bus-idle/OAM expectations rather
than using one compiler result as the other configuration's oracle. They are
discoverable by nextest. The register palette and desktop API methods remain
outstanding, with no compiler-owned deferral. Blocking HRAM DMA has additional
native execution, relocation, capacity, byte-width and guard-mutation coverage;
see [OAM DMA](oam-dma.md).

### CilSpritesCgbTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritesCgbTests.cs`. Future Rust test group: `cil_sprites_cgb`.

Sprite OAM bytes and CGB OBJ palette RAM preserve supplied attributes/colors. Hardware memory and the public graphics API are independent result oracles.

- **PORT** `CgbPaletteSprite_FlushesRealOam_AndWritesRealObjPaletteRam`.

### CilSpritesTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritesTests.cs`. Future Rust test group: `cil_sprites`.

Shadow sprites flush positions/tile/attributes with hardware offsets and clipping, HideAll hides all slots and position changes preserve tile/attribute. Preserve public behavior, not exact shadow-buffer implementation.

- **PORT** `EndFrame_FlushesShadowToOam_MatchingSetMoveHideAndClipping`.
- **PORT** `HideAll_ForcesEveryShadowSlotYToZero`.
- **PORT** `Move_ChangesPositionOnly_TileAndAttrSurvive`.
- **PORT** `EndFrame_WithNoSpriteMutation_LeavesOamAllZero`.

### CilStaticsTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStaticsTests.cs`. Future Rust test group: `cil_statics`.

Mutable fields persist across calls, array initialization retains content, constant fields fold correctly and generated delegate caches do not re-run initialization. ROM placement of readonly references or mutable array dedicated allocation is not required by C# semantics.

- **PORT** `MutableStaticCounter_IncrementedAcrossCallsAndReadBack`.
- **PORT** `MutableStaticCounter_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `StaticReadonlyByteArray_LandsInRomWithInitialBytes`.
- **PORT** `StaticReadonlyByteArray_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `ConstField_FoldedByRoslynWithNoBackingStorage`.
- **PORT** `ConstField_DebugAndReleaseProduceIdenticalObservableState`.
- **REPLACE** `MutableStaticArray_GetsDedicatedWramGlobalNotHeap` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `MutableStaticArray_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `MutableStaticArray_CrossesWramBoundaryAndRetainsEndpointWrites`.
- **PORT** `NoCaptureLambda_DoesNotResurrectCompilerGeneratedCctor`.

### CilStoredDelegateTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStoredDelegateTests.cs`. Native Rust group: `driver::delegates`; see the linked disposition matrix.

Callbacks stored through constructor/instance/static fields and parameters keep target/environment identity, merge return values and respect reassignment. Target IDs and three-byte blobs are internal ABI choices.

All 3 methods have completed native
[replacements](test-disposition.md#callback-replacements), without requiring the
legacy blob or return-slot representation. They are included in the completed
total after the full 1,193-case regression passed.

- **REPLACE, complete** `ActionThroughCtorField_TwoTargetsOneField_DispatchPerBlob`.
- **REPLACE, complete** `FuncThroughParameter_ReturnValuesMergeThroughSlot`.
- **PORT, complete** `StaticDelegateField_ReassignmentSwitchesTarget`.

### CilStringLiteralTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStringLiteralTests.cs`. Native group: `driver.rs::strings_preserve_utf16_calls_identity_and_bounds`, `string_metadata_contracts_and_direct_calls`, and String/optimizer IR cases; see [contract and traceability](strings.md).

Strings preserve unit order, length, foreach and parameter/field flow. The native contract uses UTF-16 rather than the historical ASCII restriction. Internal ROM headers are replaced by exact canonical UTF-16 pool assertions and observable byte/VRAM output.

- **REPLACE** `StringLiteral_IndexLoop_WritesExactAsciiBytesToWram`: ordered array/pointer output and canonical UTF-16 pool; the historical byte-per-char ROM assertion was an internal representation.
- **PORT** `StringLiteral_DebugAndReleaseProduceIdenticalObservableBytes`.
- **PORT** `StringLiteral_ForeachLoop_WritesExactAsciiBytes`.
- **REPLACE** `StringLiteral_NonAsciiCharacter_ReportsDiagnostic_DoesNotThrow`: Latin/NUL/surrogate ROM execution and checked invalid metadata replace the unjustified ASCII restriction.
- **PORT** `StringLength_OnParameterAcrossCallBoundary_Compiles_ReturnsRealLength`.
- **PORT** `TextDrawShapedLibraryCall_StringParameterAcrossCallBoundary_CompilesAndRuns`.
- **REPLACE** `WriteAsciiHelper_StringParameter_LandsAsciiBytesAndRomBlob`: exact helper output and canonical UTF-16 pool replace the historical ASCII ROM representation assertion.

All seven methods' behavioral contracts now have native replacements: four PORT,
three REPLACE, none deferred. Their eight Arguments attributes and22 assertion
sites are the historical inventory, not a promise to preserve private IR shapes.
The old definitions remain temporarily with the still-live C# compiler.

### CilStructReturnTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructReturnTests.cs`. Native group: `driver::struct_returns::struct_results_preserve_ordered_fields_and_frame_independence`. All five methods PORT complete; see [matrix and assertion accounting](struct-returns.md).

Value returns must remain copies through factories, chained calls, loops, recursion, generic specialization and pattern foreach. C# value semantics justify requirements; hidden trailing sret pointers are optional ABI.

- **PORT, complete** `StructReturn_BuildConsumeReverseAndChain`.
- **PORT, complete** `StructReturn_FactoriesInstanceMethodsLoopsAndStatics`.
- **PORT, complete** `StructReturn_ThroughRecursion`.
- **PORT, complete** `StructReturn_FromMonomorphizedGenericMethod`.
- **PORT, complete** `StructReturn_UnlocksPatternBasedForeach`.

### CilStructTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructTests.cs`. Future Rust test group: `cil_struct`.

Struct copies do not alias; nested/array layouts, pointers, stackalloc, ref/byval parameters, switch defaults and enum values remain correct. CIL memory/value semantics justify results; a real Switch IR opcode is optional.

- **PORT** `StructFieldRoundTrip_WholeCopyDoesNotAlias`.
- **PORT** `NestedStructsAndArraysOfStructs_FieldsRoundTrip` — completed in Rust `driver::aggregate_arrays_copy_alias_scale_and_preserve_banks`, fixture `StructArrays::NestedValues`; Debug/Release × DMG/CGB verifies independent BGP28 and full array bytes.
- **PORT** `PointerWriteToVram_ReadBack`.
- **PORT** `PointerArithmetic_OffsetWriteReadBack`.
- **PORT** `StackAllocBuffer_RoundTrips`.
- **PORT** `RefParameter_MutatedByCallee`.
- **PORT** `RefStructParameter_MutatesCallersStorage`.
- **PORT** `ByValStructParameter_CalleeMutationNotVisibleToCaller`.
- **PORT** `SwitchDispatch_JumpTable_SelectsCorrectCase`.
- **REPLACE** `SwitchDispatch_LowersToRealSwitchInstruction` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `SwitchDispatch_OutOfRange_FallsThroughToDefault`.
- **PORT** `Enum_UnderlyingIntegerRoundTrips`.

### CilTileSetTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilTileSetTests.cs`. Future Rust test group: `cil_tile_set`.

Tile copies/subranges, 1bpp-to-2bpp ink/paper masks, row writes and clear preserve bytes with safe LCD-on scheduling. Hardware bitplanes and public API justify behavior.

- **PORT, complete** `Load_LcdOff_CopiesTileBytesVerbatimIntoVram` — native `driver::actual_tileset_load_copies_vram_and_preserves_banks`; whole-bank byte checks and real initialization dependency.
- **PORT** `Load_SubRange_CopiesFromStartTileNotFromDataStart`.
- **PORT, complete** `Load1bpp_ExpandsSetBitsToInkClearBitsToPaper` — native `driver::mono::mono_tiles_preserve_ink_paper_wrap_and_selected_banks`; independent per-pixel masks, all16 color pairs, LCDon/off, banks, wrap and empty input in D/R×DMG/CGB.
- **PORT, complete** `Load1bpp_MixedInkPaper_ProducesCorrectPlaneMasks` — native `driver::mono::mono_tiles_preserve_ink_paper_wrap_and_selected_banks`; independent per-pixel masks, all16 color pairs, LCDon/off, banks, wrap and empty input in D/R×DMG/CGB.
- **PORT, complete** `Load_LcdOn_ChunksAcrossVblanksAndNeverWritesDuringMode3` — native actual_tileset driver retains32 distinct bytes at tile7, adds D/R×DMG/CGB and1920-byte split GDMA; zero mode-3 writes and bounded DMA duration. See [timing evidence](lcd-on-transfers.md).
- **PORT** `SetRowAndClear_PassThroughToTileData`.

### CilVideoJoypadTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilVideoJoypadTests.cs`. Future Rust test group: `cil_video_joypad`.

Layer LCDC bits compose; initialization clears maps/OAM and sets palettes; EndFrame advances after vblank; held inputs emit one rising edge. Hardware and frame-loop API justify these.

- **PORT** `LcdcComposesAcrossShowCallsAndStart`.
- **PORT, complete** `Init_ClearsBothTileMapsAndOamAndSetsDefaultDmgPalette` — native same driver, `VideoInit` entry; every map/OAM byte, palettes and LCD state in D/R × DMG/CGB.
- **PORT** `EndFrame_WaitsForVblankAndAdvancesFrameCount`.
- **PORT** `Pressed_ReportsRisingEdgeOnceForAHeldButton`.

### CilVirtualDispatchTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilVirtualDispatchTests.cs`. Future Rust test group: `cil_virtual_dispatch`.

Inherited fields never overlap, base-typed calls dispatch by runtime receiver, scene changes retain constructor state and interrupt-only roots participate. Interface shapes outside supported closed-world analysis must diagnose. Prefix/tag layouts are optional.

- **PORT, complete** `InheritedFields_UsePrefixLayout_NoOverlap` — same native driver, `ObjectCore::InheritedFields`; exact base/derived/alias packets and final heap descriptors in Debug/Release × DMG/CGB; see test-disposition.md.
- **REPLACE, complete** `UntraceableInterfaceCall_IsDiagnostic` — the old unsupported-interface exception is an implementation limit, not a language contract. Native `driver::interfaces` executes the stored byte-returning receiver and directly checks hardware FF42=07/FF43=EE in D/R × DMG/CGB, alongside interface binding, runtime faults and malformed/unsupported IL diagnostics. See interfaces.md.
- **PORT, complete** `VirtualCalls_TagDispatchThroughBaseTypedStatic` — native `driver::virtual_slots_and_constructor_dispatch_execute`, exact three-type Area/Kind packets through static receiver reloads and loop reassignment; separate ROM banks including257+, four D/R × DMG/CGB cases. Slot representation is redesigned; see virtual-dispatch.md.
- **PORT, complete** `SceneMachine_GameRun_DeferredChangeWithCtorState` — native `driver::game_loop_defers_scene_changes_and_preserves_constructor_state` executes actual Game.Run, nine ordered lifecycle events, constructor state1234 and frame4 in D/R × DMG/CGB; see [scene flow](scene-flow.md).
- **PORT, complete** `TagDispatch_CoexistsWithInterruptHandlerRoots` — native `driver::interrupts` executes all five vectors, real PPU counts2/3/5 in D/R × DMG/CGB, rejects invalid metadata/recursive bodies and checks register/stack preservation; see [leaf IRQ contract and traceability](interrupts.md).

### CilWideArithmeticTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilWideArithmeticTests.cs`. Native Rust group: `driver::wide_arithmetic`; all14 methods have complete native replacements. See the [disposition matrix and independent oracle](wide-arithmetic.md).

32/64-bit wrapping multiply, signed/unsigned division and masked shifts must match declared CIL semantics across operand boundaries and build modes. Optimizer iteration-count changes need no semantic ABI compatibility.

- **PORT, complete** `WideMultiply_MatchesCSharpInt32Wraparound`.
- **REPLACE, complete** `WideMultiply_DebugAndReleaseProduceIdenticalResults`.
- **PORT, complete** `WideDivide_MatchesCSharpInt32Semantics`.
- **REPLACE, complete** `WideDivide_DebugAndReleaseProduceIdenticalResults`.
- **PORT, complete** `ConstPow2Divide_MatchesCSharpInt32Semantics`.
- **REPLACE, complete** `ConstPow2Divide_DebugAndReleaseProduceIdenticalResults`.
- **PORT, complete** `ConstShift_MatchesCSharpInt32Semantics`.
- **REPLACE, complete** `ConstShift_DebugAndReleaseProduceIdenticalResults`.
- **PORT, complete** `WideMultiply64_MatchesCSharpInt64Wraparound`.
- **PORT, complete** `WideDivide64_MatchesCSharpInt64Semantics`.
- **REPLACE, complete** `WideMultiply64_DebugAndReleaseProduceIdenticalResults`.
- **REPLACE, complete** `WideDivide64_DebugAndReleaseProduceIdenticalResults`.
- **PORT, complete** `ConstShift64_MatchesCSharpInt64Semantics`.
- **REPLACE, complete** `ConstShift64_DebugAndReleaseProduceIdenticalResults`.

### SoftFloatDifferentialTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/SoftFloatDifferentialTests.cs`. Native group: `driver::softfloat_host`; all20 complete. All85 rows and145 expanded assertions are retained in eight Rust groups; see [matrix](softfloat-bits-tests.md).

Managed runtime arithmetic/comparisons/conversions must match independent host or explicit target conversion vectors in their declared numeric domain. These are runtime algorithm tests, not evidence of Rust compiler or emitted-ROM coverage.

- **PORT, complete** `AddF32_MatchesHost`.
- **PORT, complete** `SubF32_MatchesHost`.
- **PORT, complete** `MulF32_MatchesHost`.
- **PORT, complete** `DivF32_MatchesHost`.
- **PORT, complete** `NegF32_MatchesHost`.
- **PORT, complete** `F32Comparisons_MatchHost`.
- **PORT, complete** `ToI32_MatchesHost`.
- **PORT, complete** `ToU32_MatchesHost`.
- **PORT, complete** `FromI32_MatchesHost`.
- **PORT, complete** `FromU32_MatchesHost`.
- **PORT, complete** `AddF64_MatchesHost`.
- **PORT, complete** `SubF64_MatchesHost`.
- **PORT, complete** `MulF64_MatchesHost`.
- **PORT, complete** `DivF64_MatchesHost`.
- **PORT, complete** `F64Comparisons_MatchHost`.
- **PORT, complete** `ToI64_MatchesHost`.
- **PORT, complete** `ToU64_MatchesHost`.
- **PORT, complete** `FromI64_MatchesHost`.
- **PORT, complete** `ToF64_MatchesHost`.
- **PORT, complete** `ToF32_MatchesHost`.

## Legacy mechanisms versus minimum replacement features

The source boundaries are `CilFrontend.cs` (assembly IO/resolution/symbols), `CilLoweringContext.cs` (reachable methods, signatures, statics, runtime and heap), `CilMethodLowerer.cs` (evaluation stack/basic blocks/opcode dispatch), and its Arrays/Statics/Structs/Generics/Floats/Strings/Delegates/Iterators/Linq partials. `CilClassLayout`, `CilVirtualDispatch` and `CilDelegateRegistry` hold closed-world object/callback machinery. Replace all Cecil object identity and old IR dependencies; preserve metadata meaning and executable behavior.

Minimum acceptance needs valid managed assembly decoding and deterministic reference resolution; concrete generic substitution; evaluation-stack joins; integer widths/signedness and conversions; calls/recursive frames; value/reference copies and byrefs; static initialization; counted one-/two-dimensional arrays; ASCII string length/indexing across storage boundaries; object inheritance and supported virtual dispatch; stored/captured callbacks; iterator structured finally cleanup; existing LINQ subset; runtime operations; MMIO/control/arena/DMA intrinsics; interrupt roots and source locations when available. Implementing only primitive CIL is useful incremental work but cannot justify deleting these compiler-owned tests or claiming compiler replacement.

Koh.GameBoy's metadata is the useful dependency boundary: `[KohIntrinsic(kind,address)]` on properties applies to both accessors; ordinary methods carry control/heap/DMA operations. `[KohRuntime(key)]` resolves an operation to ordinary referenced code. `[KohAligned(n)]` expresses power-of-two placement, and `[Interrupt(kind)]` expresses hardware vector ownership. Preserve these source APIs without requiring a compiler dependency on Koh.GameBoy. The current simple-name match is permissive policy, not a technical necessity imposed by dependency separation: validating a metadata namespace/assembly identity does not require linking that assembly into the compiler.

`Hardware`, `Gb`, `Mem`, `Hal/*`, `Graphics/*` and `Framework/*` contain usable public gameplay APIs and desktop reference implementations; most should remain ordinary referenced CIL rather than being rewritten as compiler intrinsics. SoftFloat is ordinary C# runtime code with metadata-selected operation keys. Preserve host-only tests independently from compiler-emitted execution tests. Do not remove graphics/framework regressions because they sit under Frontends: they exercise the supported compiler language surface through useful applications.

## Correctness gaps and required replacement checks

1. **Literal mutability/identity:** `DetectArrayLiteralIdioms` replaces `newarr`/InitializeArray with shared ROM data; readonly array fields only make the reference readonly. C# permits element mutation, and separate array creations have distinct identity. REPLACE unconditional ROM-placement expectations with two fixtures: proven immutable data may be ROM-folded; mutable local/static readonly initialized arrays must retain writes and not alias independent arrays. If unsupported, diagnose rather than silently writing ROM. UTF-8 readonly spans can remain ROM data. This is a semantic requirement, not a demand to copy the legacy optimization.
2. **Metadata validation:** `CilRuntimeIndex.IndexModule` uses last-wins duplicate keys; attribute readers cast constructor arguments without shape/signature validation. Add duplicate-key, malformed kind/address/arity, wrong runtime signature, invalid alignment, duplicate-vector and conflicting marker fixtures with deterministic diagnostics. Transitive unresolved references may be ignored when unused; reachable missing bodies must diagnose. Do not treat a parser's successful read as evidence that all tables/tokens are valid.
3. **Limits and memory safety:** array/string lengths use u16 headers; arrays use a payload pointer with count at pointer-2, while strings point at their header. Preserve observed API semantics across fields/calls, not this accidental representational difference. Add empty/maximum/overflow length, negative/dynamic dimension, out-of-range access, null receiver, arena exhaustion/reset-lifetime and stack/heap collision cases. Specify supported target behavior or reject unsupported cases; never silently truncate lengths/addresses or claim CLR bounds checks where none exist.
4. **Numeric domain:** SoftFloatDifferentialTests explicitly limits normal finite inputs and zero; it does not establish general IEEE compliance. Include signed zero, halfway rounding, exponent extremes, subnormal, NaN/infinity and conversion-boundary fixtures with an explicit expected domain policy. Some conversion tests use explicit saturation outputs rather than a host cast oracle; retain that distinction. Floating remainder now executes through the exact shared runtime core; missing-provider diagnostics remain covered by explicitly removing the metadata key. Integer division by zero, signed minimum/-1, checked overflow and shift masking need explicit target decisions and tests.
5. **LINQ:** `LowerLinqTerminal` seeds Max/Min from element zero before checking length, so empty-array behavior needs a diagnostic/trap policy instead of an unconditional invalid read. `Select` changes the current value but accumulator type is selected from the original array element type; add widening and signedness-changing selectors. Cover empty Sum/Count/Any/All, short-circuit predicate side effects and overflow. Existing Max-after-pipeline rejection is a supported-subset boundary, not a permanent requirement to reject a correctly implemented broader chain.
6. **Generated CIL and closed-world state:** Debug/Release is necessary but finite. Add callbacks sharing a type with differing receivers, generic closed types sharing method names, virtual overrides inherited through multiple levels, interrupt-only reachable targets, iterator early break/finally, nested foreach and repeated enumeration. Current try/finally support is structured cleanup without general exceptions; unsupported catches/filters must diagnose rather than vanish.
7. **Oracles and assertions:** comparisons using collection equivalence must become ordered byte/result comparisons where position matters. Differential Debug/Release assertions cannot stand alone. The RGB host helper is a useful reference but include literal RGB555 boundary vectors to avoid duplicating the same bug. Register verdicts need completion markers and a cycle limit; mode-3 guards must observe an active LCD and the operation interval. Hardware behavior here is emulator evidence, not physical-console proof.
8. **Input/debug boundary:** add corrupt/truncated PE/metadata, missing entry point, missing reachable dependency, absent/mismatched/embedded portable PDB, overloaded entry signatures and deterministic diagnostics. CilFrontend currently retries optional symbol failures and contains bad-image/IO failures; replacement must test these user-facing boundaries without depending on Cecil exception types. Source-map acceptance is shared with integration, while decoding and method sequence-point association remain compiler-owned.

## Removal gate and future suite organization

Use three layers: metadata/diagnostic fixtures without an emulator; DLL-to-canonical-ELF structural checks using the authoritative object reader (symbols, relocations, alignment and source associations); bounded linked-ROM runs using the existing emulator and independent expected bytes/state. Runtime math also retains host differential vectors, supplemented with emitted-ROM cases. Share a harness rather than copying every legacy class's Roslyn/build/link loop. Keep C# fixtures as real managed assemblies in both Roslyn modes; Rust test orchestration does not imply reimplementing a C# parser.

DELETE after acceptance: legacy Cecil-index dictionary assertions, exact spill-allocation counts, old IR opcode/name checks, hardcoded private arena/static addresses and duplicate harness boilerplate. Their semantic obligations are retained by the listed PORT/REPLACE cases. Preserve hardware-required fixed addresses and DMA alignment. Keep narrowing size/cycle deltas as a focused benchmark with explicit comparable settings; do not require every correct backend to use the old pass.

No production files, test cases or runtime APIs are changed by this audit. These proposals require actual implementation and completed test evidence before the old compiler or tests can be removed.
