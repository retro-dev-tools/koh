# CIL frontend migration audit

Source inspection only. No builds or tests were run for this audit. No listed case has been migrated, and none establishes Rust coverage. Dispositions below are proposals for replacement acceptance, not completed work. No compiler-owned behavior is deferred to another migration. TileSheetConverterTests belongs to the integration audit.

## Inventory and disposition rules

35 frontend source files plus SoftFloatDifferentialTests.cs; 230 `[Test]` methods (parameterized rows are not separate methods). All methods are explicitly enumerated below. Implementation inventory: 17 CIL files and 33 GameBoy C# sources. Counts exclude generated bin/obj sources by their current absence from this source inventory.

PORT means keep the observable requirement and fixture inputs in a Rust-driven DLL-to-ELF-to-ROM test; it does not mean translating legacy assertions verbatim. REPLACE means preserve the useful requirement with an implementation-independent oracle or strengthen an incomplete oracle. DELETE applies only to redundant legacy implementation assertions after the replacement passes, never to the underlying feature. Debug/Release equality must accompany an independently expected result and a completion marker with bounded execution; equality alone can accept two wrong runs. The future groups below are proposed test modules, not existing coverage.

## Test-level traceability

Each file forms a coherent group under the stated behavioral requirement. Every enumerated method inherits PORT unless explicitly marked REPLACE below. This includes parameterized inputs attached to each method; retain all existing argument rows.

### CilBgWinTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilBgWinTests.cs`. Future Rust test group: `cil_bg_win`.

Background/window map coordinates, fill/draw byte contents, CGB bank-1 attributes, DMG no-op behavior and safe LCD-on batched writes. Hardware memory layout and the public Bg/Win API justify these independently of the compiler.

- **PORT** `BgFillAndDrawMap_WriteExpectedMapBytes`.
- **PORT** `WinFillAndDrawMap_WriteExpectedMapBytes`.
- **PORT** `SetAttrFillAttr_OnCgb_WriteVramBank1NotTileIndexMap`.
- **PORT** `SetAttrFillAttr_OnDmg_IsTrueNoOp`.
- **PORT** `BatchedFullBoardRedraw_WithLcdOn_ProducesZeroMode3Violations`.

### CilCanvasTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilCanvasTests.cs`. Future Rust test group: `cil_canvas`.

Tile-planar pixel placement, shade/dither masks, line/triangle rasterization, double-buffer presentation and CGB GDMA with no mode-3 writes. Preserve the Canvas API and observed pixels; buffer addresses and DMA choice are implementation choices unless hardware requires them.

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

- **PORT** `StringCtorParameter_StoredInClassField_ReadBackViaLengthAndIndexer`.

### CilDelegateTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilDelegateTests.cs`. Future Rust test group: `cil_delegate`.

Captured/noncaptured callbacks, parameter passage and sealed virtual method groups must invoke the correct receiver and target in both Roslyn build modes. Unsupported open virtual method groups must fail clearly; direct calls and blob dispatch are optional mechanisms.

- **PORT** `NoCaptureLambda_VerifiesCleanAndInvokesResolvedTarget`.
- **PORT** `NoCaptureLambda_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `CapturingLambda_VerifiesCleanAndInvokesResolvedTarget`.
- **PORT** `CapturingLambda_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `DelegateThroughParameter_LowersViaBlobDispatch`.
- **PORT** `CallvirtOnSealedDeclaringType_DevirtualizesToDirectCall`.
- **PORT** `VirtualMethodGroupDelegate_IsDiagnosedNotSilentlyMisresolved`.
- **PORT** `VirtualMethodGroupDelegate_ResolvesToSealedOverrideWhenTokenIsSealed`.

### CilEndToEndTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilEndToEndTests.cs`. Future Rust test group: `cil_end_to_end`.

Real compiled assemblies must reach ROM execution with correct register loops, memory-region pointers and control intrinsics supplied through metadata. This is the source-to-ROM user contract, not a requirement to reproduce old IR.

- **PORT** `RegisterPokeLoop_VerifiesCleanAndProducesExpectedState`.
- **PORT** `RegisterPokeLoop_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `RegionPointerAndControlIntrinsic_LoweredFromAttributeAlone`.

### CilFloatTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilFloatTests.cs`. Future Rust test group: `cil_float`.

Float operations, conversions and byrefs must preserve the declared target numeric contract and diagnose unsupported operations. Keep runtime-attribute extensibility; exact helper names and call instruction shapes are not numeric semantics.

- **PORT** `F32_Arithmetic_MatchesHost`.
- **REPLACE** `F32_Add_RoutesThroughRealSoftFloatFunction` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `F32_Compare_MatchesHost`.
- **PORT** `F32_CompareInBranchPosition_MatchesHost`.
- **PORT** `F32_TernaryMin_MatchesHost`.
- **PORT** `F32_Neg_MatchesHost`.
- **REPLACE** `F32_Neg_RoutesThroughRealSoftFloatFunction` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `F32_ToInt_MatchesHost`.
- **PORT** `F32_FromInt_MatchesHost`.
- **PORT** `F32_ToF64_MatchesHost`.
- **REPLACE** `F32_ToF64_RoutesThroughRealSoftFloatFunction` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `F64_ToF32_MatchesHost`.
- **REPLACE** `F64_ToF32_RoutesThroughRealSoftFloatFunction` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `F64_ToInt_MatchesHost`.
- **PORT** `F64_FromInt_MatchesHost`.
- **PORT** `F64_FromUInt_MatchesHost`.
- **PORT** `F64_Arithmetic_MatchesHost`.
- **PORT** `F64_Compare_MatchesHost`.
- **PORT** `F32_RefParameter_ReadsAndWritesThroughIndirection`.
- **PORT** `F32_Rem_NoMatchingRuntimeKey_IsADiagnosticNotAMiscompile`.
- **PORT** `F32_Arithmetic_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `F64_Arithmetic_DebugAndReleaseProduceIdenticalObservableState`.

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

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilGenericTests.cs`. Future Rust test group: `cil_generic`.

Transitive generic-method specialization at multiple concrete types plus signed/unsigned 64-bit arithmetic and extension. CIL type substitution and integer semantics justify results, not mangled instance names.

- **PORT** `GenericMethod_TwoTypesTransitively_ProducesExpectedValue`.
- **PORT** `GenericMethod_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `LongArithmetic_AddSubMulDivRem_MatchesHostComputedValue`.
- **PORT** `ConvI8SignExtends_ConvU8ZeroExtends_MatchesHostComputedValue`.
- **PORT** `UlongDivision_IsUnsigned_NotArithmeticShift`.
- **PORT** `WideIntArithmetic_DebugAndReleaseProduceIdenticalObservableState`.

### CilGraphicsSlice2Tests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilGraphicsSlice2Tests.cs`. Future Rust test group: `cil_graphics_slice2`.

Static buffer alignment and safe OAM-DMA execution/return are hardware requirements. The first allocation address and exact cursor movement are legacy allocator assumptions.

- **REPLACE, complete** `KohAligned_RoundsStaticWramGlobalCursorUpToAlignment` — native `driver::aligned_static_arrays_have_aligned_mutable_payloads` checks actual aligned payloads after padding, scalar storage, identity and capacity; legacy fixed-address assertions are intentionally replaced. See [traceability](test-disposition.md#alignment-and-real-video-initialization).
- **PORT** `RunOamDma_TriggersRealDmaAndReturnsWithBusUnlocked`.
- **PORT** `RunOamDma_DebugAndReleaseProduceIdenticalObservableOam`.

### CilInterruptTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilInterruptTests.cs`. Future Rust test group: `cil_interrupt`.

Metadata selects correct vectors, executable handlers fire, unknown vectors and recursive handler call graphs diagnose. Interrupt ABI/state preservation is required; old IrFunction property layout is not.

- **PORT** `Interrupt_SetsVectorOnTheLoweredFunction`.
- **PORT** `Interrupt_ActuallyFires_OnGameBoySystem`.
- **PORT** `Interrupt_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `UnknownInterruptKind_ReportedAsDiagnostic`.
- **PORT** `RecursiveInterruptHandler_IsRejected`.

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

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilKohRuntimeTests.cs`. Future Rust test group: `cil_koh_runtime`.

Referenced SoftFloat code must be discoverable, lower transitively and produce correct AddF32 bits. Runtime keys are an extension contract; an old module function-name assertion is not.

- **REPLACE** `SoftFloatAddF32_LowersCleanlyAsOrdinaryReferencedCode` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `SoftFloatAddF32_ComputesTheCorrectIeeeBits`.
- **PORT** `SoftFloatAddF32_DebugAndReleaseProduceIdenticalObservableState`.

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

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLoweringTests.cs`. Future Rust test group: `cil_lowering`.

Register IO, ordinary calls, stack joins, arithmetic/comparisons/conversions and array lengths across calls must behave correctly. The CIL evaluation stack and public API justify outputs; spill count/fixed-global IR representation do not.

- **PORT** `RegisterLoop_WritesExpectedValue_DebugAndRelease`.
- **REPLACE** `RegisterLoop_IrShape_HasEntryFunctionAndFixedAddressGlobal` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `RegisterReadThenWrite_RoundTripsThroughFixedAddressGlobal`.
- **PORT** `CallToGameModuleStaticMethod_IsLoweredAsDirectCall`.
- **PORT** `Ternary_NonEmptyStackJoin_IsSpilledCorrectly`.
- **REPLACE** `Ternary_Release_ActuallyExercisesSpillAllocas` — retain its behavior; replace representation/pass-name/allocation/helper-name assertions with result, relocation/alignment or measured performance checks.
- **PORT** `ArithmeticAndCompareOpcodes_ProduceExpectedValue`.
- **PORT** `UnsignedAndInequalityLoops_ProduceExpectedValue`.
- **PORT** `NarrowingConversionOpcodes_ProduceExpectedValue`.
- **PORT** `UnsupportedExternalCall_ReportsDiagnostic_DoesNotThrow`.
- **PORT** `ArrayLength_LoopBoundSumsAllElements_DebugAndRelease`.
- **PORT** `ArrayLength_OnUntraceableArray_ReadsRuntimeLengthPrefix`.

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

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilRecursionTests.cs`. Future Rust test group: `cil_recursion`.

Direct/mutual/deep recursive calls must preserve caller locals, arguments, returns and bounded stack behavior. Fibonacci/parity results are independent oracles; exact stack placement is an ABI choice.

- **PORT** `DirectRecursion_Fibonacci_ComputesExpectedValue`.
- **PORT** `DirectRecursion_Fibonacci_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `MutualRecursion_IsEvenIsOdd_ComputesExpectedValues`.
- **PORT** `MutualRecursion_DebugAndReleaseProduceIdenticalObservableState`.
- **PORT** `DeepRecursion_ExercisesRelocatedWramCallStack`.
- **PORT** `DeepRecursion_DebugAndReleaseProduceIdenticalObservableState`.

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

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStoredDelegateTests.cs`. Future Rust test group: `cil_stored_delegate`.

Callbacks stored through constructor/instance/static fields and parameters keep target/environment identity, merge return values and respect reassignment. Target IDs and three-byte blobs are internal ABI choices.

- **PORT** `ActionThroughCtorField_TwoTargetsOneField_DispatchPerBlob`.
- **PORT** `FuncThroughParameter_ReturnValuesMergeThroughSlot`.
- **PORT** `StaticDelegateField_ReassignmentSwitchesTarget`.

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

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructReturnTests.cs`. Future Rust test group: `cil_struct_return`.

Value returns must remain copies through factories, chained calls, loops, recursion, generic specialization and pattern foreach. C# value semantics justify requirements; hidden trailing sret pointers are optional ABI.

- **PORT** `StructReturn_BuildConsumeReverseAndChain`.
- **PORT** `StructReturn_FactoriesInstanceMethodsLoopsAndStatics`.
- **PORT** `StructReturn_ThroughRecursion`.
- **PORT** `StructReturn_FromMonomorphizedGenericMethod`.
- **PORT** `StructReturn_UnlocksPatternBasedForeach`.

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
- **PORT** `Load1bpp_ExpandsSetBitsToInkClearBitsToPaper`.
- **PORT** `Load1bpp_MixedInkPaper_ProducesCorrectPlaneMasks`.
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

- **PORT** `InheritedFields_UsePrefixLayout_NoOverlap`.
- **PORT** `UntraceableInterfaceCall_IsDiagnostic`.
- **PORT** `VirtualCalls_TagDispatchThroughBaseTypedStatic`.
- **PORT** `SceneMachine_GameRun_DeferredChangeWithCtorState`.
- **PORT** `TagDispatch_CoexistsWithInterruptHandlerRoots`.

### CilWideArithmeticTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilWideArithmeticTests.cs`. Future Rust test group: `cil_wide_arithmetic`.

32/64-bit wrapping multiply, signed/unsigned division and masked shifts must match declared CIL semantics across operand boundaries and build modes. Optimizer iteration-count changes need no semantic ABI compatibility.

- **PORT** `WideMultiply_MatchesCSharpInt32Wraparound`.
- **PORT** `WideMultiply_DebugAndReleaseProduceIdenticalResults`.
- **PORT** `WideDivide_MatchesCSharpInt32Semantics`.
- **PORT** `WideDivide_DebugAndReleaseProduceIdenticalResults`.
- **PORT** `ConstPow2Divide_MatchesCSharpInt32Semantics`.
- **PORT** `ConstPow2Divide_DebugAndReleaseProduceIdenticalResults`.
- **PORT** `ConstShift_MatchesCSharpInt32Semantics`.
- **PORT** `ConstShift_DebugAndReleaseProduceIdenticalResults`.
- **PORT** `WideMultiply64_MatchesCSharpInt64Wraparound`.
- **PORT** `WideDivide64_MatchesCSharpInt64Semantics`.
- **PORT** `WideMultiply64_DebugAndReleaseProduceIdenticalResults`.
- **PORT** `WideDivide64_DebugAndReleaseProduceIdenticalResults`.
- **PORT** `ConstShift64_MatchesCSharpInt64Semantics`.
- **PORT** `ConstShift64_DebugAndReleaseProduceIdenticalResults`.

### SoftFloatDifferentialTests.cs

Source: `tests/Compiler/Koh.Compiler.Tests/SoftFloatDifferentialTests.cs`. Future Rust test group: `cil_soft_float_differential`.

Managed runtime arithmetic/comparisons/conversions must match independent host or explicit target conversion vectors in their declared numeric domain. These are runtime algorithm tests, not evidence of Rust compiler or emitted-ROM coverage.

- **PORT** `AddF32_MatchesHost`.
- **PORT** `SubF32_MatchesHost`.
- **PORT** `MulF32_MatchesHost`.
- **PORT** `DivF32_MatchesHost`.
- **PORT** `NegF32_MatchesHost`.
- **PORT** `F32Comparisons_MatchHost`.
- **PORT** `ToI32_MatchesHost`.
- **PORT** `ToU32_MatchesHost`.
- **PORT** `FromI32_MatchesHost`.
- **PORT** `FromU32_MatchesHost`.
- **PORT** `AddF64_MatchesHost`.
- **PORT** `SubF64_MatchesHost`.
- **PORT** `MulF64_MatchesHost`.
- **PORT** `DivF64_MatchesHost`.
- **PORT** `F64Comparisons_MatchHost`.
- **PORT** `ToI64_MatchesHost`.
- **PORT** `ToU64_MatchesHost`.
- **PORT** `FromI64_MatchesHost`.
- **PORT** `ToF64_MatchesHost`.
- **PORT** `ToF32_MatchesHost`.

## Legacy mechanisms versus minimum replacement features

The source boundaries are `CilFrontend.cs` (assembly IO/resolution/symbols), `CilLoweringContext.cs` (reachable methods, signatures, statics, runtime and heap), `CilMethodLowerer.cs` (evaluation stack/basic blocks/opcode dispatch), and its Arrays/Statics/Structs/Generics/Floats/Strings/Delegates/Iterators/Linq partials. `CilClassLayout`, `CilVirtualDispatch` and `CilDelegateRegistry` hold closed-world object/callback machinery. Replace all Cecil object identity and old IR dependencies; preserve metadata meaning and executable behavior.

Minimum acceptance needs valid managed assembly decoding and deterministic reference resolution; concrete generic substitution; evaluation-stack joins; integer widths/signedness and conversions; calls/recursive frames; value/reference copies and byrefs; static initialization; counted one-/two-dimensional arrays; ASCII string length/indexing across storage boundaries; object inheritance and supported virtual dispatch; stored/captured callbacks; iterator structured finally cleanup; existing LINQ subset; runtime operations; MMIO/control/arena/DMA intrinsics; interrupt roots and source locations when available. Implementing only primitive CIL is useful incremental work but cannot justify deleting these compiler-owned tests or claiming compiler replacement.

Koh.GameBoy's metadata is the useful dependency boundary: `[KohIntrinsic(kind,address)]` on properties applies to both accessors; ordinary methods carry control/heap/DMA operations. `[KohRuntime(key)]` resolves an operation to ordinary referenced code. `[KohAligned(n)]` expresses power-of-two placement, and `[Interrupt(kind)]` expresses hardware vector ownership. Preserve these source APIs without requiring a compiler dependency on Koh.GameBoy. The current simple-name match is permissive policy, not a technical necessity imposed by dependency separation: validating a metadata namespace/assembly identity does not require linking that assembly into the compiler.

`Hardware`, `Gb`, `Mem`, `Hal/*`, `Graphics/*` and `Framework/*` contain usable public gameplay APIs and desktop reference implementations; most should remain ordinary referenced CIL rather than being rewritten as compiler intrinsics. SoftFloat is ordinary C# runtime code with metadata-selected operation keys. Preserve host-only tests independently from compiler-emitted execution tests. Do not remove graphics/framework regressions because they sit under Frontends: they exercise the supported compiler language surface through useful applications.

## Correctness gaps and required replacement checks

1. **Literal mutability/identity:** `DetectArrayLiteralIdioms` replaces `newarr`/InitializeArray with shared ROM data; readonly array fields only make the reference readonly. C# permits element mutation, and separate array creations have distinct identity. REPLACE unconditional ROM-placement expectations with two fixtures: proven immutable data may be ROM-folded; mutable local/static readonly initialized arrays must retain writes and not alias independent arrays. If unsupported, diagnose rather than silently writing ROM. UTF-8 readonly spans can remain ROM data. This is a semantic requirement, not a demand to copy the legacy optimization.
2. **Metadata validation:** `CilRuntimeIndex.IndexModule` uses last-wins duplicate keys; attribute readers cast constructor arguments without shape/signature validation. Add duplicate-key, malformed kind/address/arity, wrong runtime signature, invalid alignment, duplicate-vector and conflicting marker fixtures with deterministic diagnostics. Transitive unresolved references may be ignored when unused; reachable missing bodies must diagnose. Do not treat a parser's successful read as evidence that all tables/tokens are valid.
3. **Limits and memory safety:** array/string lengths use u16 headers; arrays use a payload pointer with count at pointer-2, while strings point at their header. Preserve observed API semantics across fields/calls, not this accidental representational difference. Add empty/maximum/overflow length, negative/dynamic dimension, out-of-range access, null receiver, arena exhaustion/reset-lifetime and stack/heap collision cases. Specify supported target behavior or reject unsupported cases; never silently truncate lengths/addresses or claim CLR bounds checks where none exist.
4. **Numeric domain:** SoftFloatDifferentialTests explicitly limits normal finite inputs and zero; it does not establish general IEEE compliance. Include signed zero, halfway rounding, exponent extremes, subnormal, NaN/infinity and conversion-boundary fixtures with an explicit expected domain policy. Some conversion tests use explicit saturation outputs rather than a host cast oracle; retain that distinction. F32 remainder currently diagnoses a missing key: preserve clear rejection until a correct runtime implementation exists, then REPLACE the rejection with numerical remainder tests. Integer division by zero, signed minimum/-1, checked overflow and shift masking need explicit target decisions and tests.
5. **LINQ:** `LowerLinqTerminal` seeds Max/Min from element zero before checking length, so empty-array behavior needs a diagnostic/trap policy instead of an unconditional invalid read. `Select` changes the current value but accumulator type is selected from the original array element type; add widening and signedness-changing selectors. Cover empty Sum/Count/Any/All, short-circuit predicate side effects and overflow. Existing Max-after-pipeline rejection is a supported-subset boundary, not a permanent requirement to reject a correctly implemented broader chain.
6. **Generated CIL and closed-world state:** Debug/Release is necessary but finite. Add callbacks sharing a type with differing receivers, generic closed types sharing method names, virtual overrides inherited through multiple levels, interrupt-only reachable targets, iterator early break/finally, nested foreach and repeated enumeration. Current try/finally support is structured cleanup without general exceptions; unsupported catches/filters must diagnose rather than vanish.
7. **Oracles and assertions:** comparisons using collection equivalence must become ordered byte/result comparisons where position matters. Differential Debug/Release assertions cannot stand alone. The RGB host helper is a useful reference but include literal RGB555 boundary vectors to avoid duplicating the same bug. Register verdicts need completion markers and a cycle limit; mode-3 guards must observe an active LCD and the operation interval. Hardware behavior here is emulator evidence, not physical-console proof.
8. **Input/debug boundary:** add corrupt/truncated PE/metadata, missing entry point, missing reachable dependency, absent/mismatched/embedded portable PDB, overloaded entry signatures and deterministic diagnostics. CilFrontend currently retries optional symbol failures and contains bad-image/IO failures; replacement must test these user-facing boundaries without depending on Cecil exception types. Source-map acceptance is shared with integration, while decoding and method sequence-point association remain compiler-owned.

## Removal gate and future suite organization

Use three layers: metadata/diagnostic fixtures without an emulator; DLL-to-canonical-ELF structural checks using the authoritative object reader (symbols, relocations, alignment and source associations); bounded linked-ROM runs using the existing emulator and independent expected bytes/state. Runtime math also retains host differential vectors, supplemented with emitted-ROM cases. Share a harness rather than copying every legacy class's Roslyn/build/link loop. Keep C# fixtures as real managed assemblies in both Roslyn modes; Rust test orchestration does not imply reimplementing a C# parser.

DELETE after acceptance: legacy Cecil-index dictionary assertions, exact spill-allocation counts, old IR opcode/name checks, hardcoded private arena/static addresses and duplicate harness boilerplate. Their semantic obligations are retained by the listed PORT/REPLACE cases. Preserve hardware-required fixed addresses and DMA alignment. Keep narrowing size/cycle deltas as a focused benchmark with explicit comparable settings; do not require every correct backend to use the old pass.

No production files, test cases or runtime APIs are changed by this audit. These proposals require actual implementation and completed test evidence before the old compiler or tests can be removed.
