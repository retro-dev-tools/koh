# Test disposition before Rust linker migration

Historical PR54 source provenance follows. Compiler project paths and commands
are baseline evidence; current compiler coverage is recorded in the native
[disposition ledger](../rust-compiler/test-disposition.md).

Audit baseline: master 9a2cfa8; the inventory below was saved before any test mutation. The original disposition tables preserve that review, including proposed target groups. The final implementation mapping, explicit deletions, and measured focused results below supersede those proposed target names. The audit itself changed no tests or production files; the subsequent migration did.

PORT = preserve invariant and move its owner. REPLACE = retain user behavior using the new interchange/pipeline, remove obsolete assertions. DELETE = obsolete wire-contract assertion only. DEFER = uncovered or unresolved requirement; blocks claiming equivalent coverage.

## Independent evidence and limits

- **H**: cartridge checksums and flags: [Pan Docs cartridge header](https://gbdev.io/pandocs/The_Cartridge_Header.html); observable independent arithmetic: header checksum subtracts bytes $134–$14c plus one; global checksum sums all bytes except $14e/$14f, stored big endian. The URL returned HTTP 403 during this audit, so external text was not reverified.
- **M**: memory windows and bank/file-offset distinctions: [Pan Docs memory map](https://gbdev.io/pandocs/Memory_Map.html). Independent observable invariant: different ROMX banks must occupy different file bytes despite sharing a CPU window. External text not fetched.
- **R**: [RGBDS 1.0.1 assembly language](https://rgbds.gbdev.io/docs/v1.0.1/rgbasm.5) and [object format](https://rgbds.gbdev.io/docs/v1.0.1/rgblink.5). Fetches returned 403; current executable oracle tests pin version 1.0.1 but were not run here.
- **G**: repository [object representation contract](../rust-assembler-objects.md): complete expressions, explicit imports, separate storage/execution PC, provenance, validation. This is a project contract, not independent hardware or ELF authority.
- **D**: observable debugger invariant: setting a breakpoint on an emitted source instruction must stop at that instruction; returned stack source and register state must agree. KDBG parsing alone is weaker.
- **C**: independently calculated fixture result or host-language result compared with emulator registers/memory. This establishes intended observable behavior, not hardware execution.
- **S**: [SDK targets](../../src/Compiler/Koh.Sdk/Sdk/Sdk.targets), [verification harness contract](../../tools/rust-asm-verify/README.md), and build.proj. These establish current integration paths, not proof they work.

At the audit baseline, no test provided ELF validation. The final ELF tests now include the third-party object crate's ELF parser and independently inspected header/table fields. A Koh writer/reader round trip alone remains insufficient independent conformance evidence. The object crate's reader and writer also share a dependency, so its acceptance is narrower evidence than interoperability with another toolchain.

## Linker unit and assembly integration inventory

Paths in this table are under tests/Link/Koh.Linker.Tests/.

| Original cases / coherent group | Invariant and legacy dependency | Evidence | Decision and target | Rationale |
|---|---|---|---|---|
| LinkerTests: Link_SingleFile_ProducesRom, Link_SectionPlacedAtCorrectAddress, Link_MinimumRomSize_32KB, Link_NoDiagnostics_CleanProgram | Successful simple source yields bytes; min 32 KiB and ROM0-first placement are current policy. In-process EmitModel/LinkResult. | Byte fixture C; default sizing S, not a universal hardware requirement. | REPLACE → Rust linker cartridge/basic and real CLI source-to-ROM tests | Keep bytes/success/error checks; explicitly specify default output sizing. |
| Link_HeaderChecksum_Correct, Link_GlobalChecksum_Correct, Link_CgbCompatible_SetsHeaderFlagAndKeepsChecksumsValid | Correct checksum arithmetic after flag mutation; C# LinkOptions. | H and independent arithmetic in test bodies | PORT → cartridge checksum/flag cases | Keep formulas independent of implementation. |
| Link_ExportedSymbol_Resolved, Link_SymbolAfterInstruction_CorrectAddress, Link_ConstantSymbol_ValuePreserved | Labels = placed base + offset; absolute constants stay absolute. C# symbol model. | C, G | REPLACE → ELF symbol/link resolution cases | Avoid retaining section-relative/absolute ambiguity. |
| Link_DuplicateExport_Diagnostic | Two object exports with same identity fail; only asserts message contains Duplicate. | Observable ambiguity; G | REPLACE → multi-object duplicate export failure | Require unsuccessful link and no output, retain useful symbol/source diagnostic. |
| Link_TwoSections_BothPlaced, Link_MultipleFiles_SectionsPlaced | Multiple sections/objects survive; tests assume input-order adjacent packing at 0,2. | C for bytes, placement policy only S | REPLACE → placement and separate-file CLI cases | Pin addresses or assert returned placement rather than accidentally mandating allocator order. |
| SymFile_ContainsSymbols | Public .sym bank:address names; in-process SymFileWriter. | D, explicit expected strings | PORT → Rust .sym writer/CLI output | Consumers still need accurate symbols. |
| Link_RomxSection_PlacedInPhysicalBankOffset, Link_RomxSection_RomSizedCorrectly, Link_RomxSection_SymbolHasCorrectBank, Link_TwoRomxBanks_DataAtCorrectPhysicalOffsets | ROM0/ROMX bytes, bank identity, minimum length, .sym bank prefix. | M, C | PORT → bank placement/image/.sym cases | Keep bank 1 and 2 distinct; floating bank-1 choice is policy. |
| Link_FixedAddress_PlacedCorrectly | Fixed ROM0 $100 placement. | C | PORT → fixed placement | Direct byte offsets are independent observable facts. |
| Link_SectionTooLarge_Diagnostic | Invalid 200-byte HRAM section rejected, no ROM; manually builds RAM as initialized bytes. | M, G | REPLACE → valid uninitialized oversized HRAM ELF rejected | Do not accidentally test only malformed RAM data instead of capacity. |
| ConstraintDiagnosticTests: SingleSectionTooLarge, TwoSectionsExceedBank, FixedBankOverflow, OverflowAmount, NoRoomAnywhere | Reject impossible placements, identify section/bank and capacity/free/overflow; C# SectionPlacer internals. | M; arithmetic 1536 needed,1024 free,512 overflow | REPLACE → Rust placement diagnostics | Assert semantic values, not exact old prose. |
| ConstraintDiagnosticTests: FixedAddressOverlap_ReportsBothSections, FixedAddressOverlap_EmittedOnceNotTwice, FloatingFitsElsewhere_NoError | Reject overlap once with both names; move floating sections to another bank if possible. | Interval intersection and distinct bank invariant M | PORT → placement conflict/fallback cases | Preserve recovery and useful diagnostics. |
| RawImageTests: Raw_Cgb_Sized_Image_Is_Not_Rounded_To_A_Power_Of_Two, Raw_Dmg_Sized_Image_Is_Exactly_256_Bytes, Raw_Size_Is_Exact_Not_A_Floor | Exact $900/$100 output, current PadToPowerOfTwo=false plus MinSize API. | Requested-size observable invariant; S | REPLACE → Rust raw CLI exact-size cases | Raw mode must have an explicit exact-size contract. Duplicate 256-byte success case may merge, not lose overflow coverage. |
| Raw_Cgb_Sized_Image_Keeps_Its_Own_Bytes_At_014D, Raw_Image_Is_Zero_Filled_Past_Its_Sections, Raw_Content_Overflowing_The_Size_Is_An_Error_Not_A_Bigger_Image | No cartridge mutation in raw output; zero fill; fixed size ceiling. | Exact fixture bytes and requested bound C | PORT → raw image regressions | Essential boot image behavior. |
| Cartridge_Output_Still_Pads_To_A_Power_Of_Two_By_Default, Cartridge_Output_Still_Gets_Its_Checksums_Fixed | Raw options must not leak into default cartridge mode. Last test only checks byte changed. | H; sizing S | REPLACE → raw/cartridge paired tests | Assert actual checksum, not merely inequality with AA. |
| AssemblerLinkerGoldenIntegrationTests: AssembleLink_GoldenBytes_CoverCharmapInterpolationAndExplicitLocalJump | Macro/charmap expansion plus JR operand 01 and string lengths 3,4 survive assembly/link. | Independently count text/PC bytes C | REPLACE → real assembler ELF → Rust linker golden | Preserve frontend behavior through serialized boundary. |
| CrossSectionAbsoluteRef_EmitsCorrectJpTarget, FloatingSection_SameSectionRefs_ResolveToPlacedAddress | Absolute data/call references use placed base, JR relative, local scopes distinct. Assumes floating Body/Boot starts $104 after a fixed $100 section. | Address arithmetic C; $104 packing is legacy policy, not independent validity evidence | REPLACE → explicit placement or resolved-symbol-relative checks | Do not bless a legacy allocation quirk as a language invariant. |
| FixedSectionLabel_SymbolAtFixedAddress, WramFixedSectionLabel_SymbolAtFixedAddress, IntraSectionRelativeJump_StillWorks | Fixed ROM/RAM labels not double-added; JR displacement -3 ($FD). | C, M | PORT → assembly ELF link regressions | Exercise final symbol and machine-byte results. |
| CrossSection_KobjRoundtrip_ResolvesDataRef | Serialized deferred dw Target and call Target reach $200; KOBJ SymbolName preservation. Despite helper accepting multiple files, case passes one file with two sections. | C | REPLACE → ELF serialized one-object and true separate-object consumer/provider cases | KOBJ field disappears; relocation survival must remain. |
| LinkerLineMapIntegrationTests: Nops_EachLineResolvesToItsOwnAddress, MultiByteInstruction_ResolvesToInstructionStart, LinesWithoutCode_ReturnNoAddresses, SectionWithFixedRomxAddress_MapsToPlacedAddress | Real source file/line follows placed instruction and bank; empty source lines unmapped. | D; explicit instruction sizes C | REPLACE → assembler ELF debug provenance → Rust KDBG → existing source-map/DAP consumers | Current tests bypass serialized objects; new tests must cross ELF. |
| LargeDbBlock_SplitsIntoMultipleAddressMapEntries | 600 bytes retain source coverage in byte-sized KDBG runs. | KDBG consumer constraint; count sum 600 | PORT → Rust KDBG splitting + C# reader integration | Splitting is KDBG-specific, unrelated to obsolete KOBJ. |
| KdbgFileWriterTests: Header_Magic_And_Version, Symbol_Round_Trip, AddressMap_Round_Trip_Without_Expansion, AddressMap_Round_Trip_With_Expansion_Stack | KDBG header/symbol/file/line/range/expansion chain. C# writer in linker project. | D; existing consumer format constants | REPLACE → Rust writer read by existing C# KdbgReader and independently decoded records | Retain KDBG interoperability; expansion test currently checks count only, strengthen frame values/order. |

## Object-format tests

Paths are under tests/Common/Koh.Objects.Tests/.

| Original cases / group | Invariant / legacy detail | Evidence | Decision → target | Rationale |
|---|---|---|---|---|
| KobjRoundtripTests: SimpleProgram, Symbols, MultipleSections, SectionType, EmptyProgram, SymbolCount, LargeData, ConstantSection_IsNull, DeserializedSuccess_IsTrue | Sections/bytes/local-exported symbols/constants survive KOBJ; .NET serialization and Success convention. | C, G; round trip alone not independent | REPLACE → ELF semantic readback + linking cases | Keep semantic data, remove KOBJ record conventions. |
| InvalidMagic_Throws, InvalidVersion_Throws, UnknownTag_Throws, V2KobjWithPatches_Reads_SymbolNameNull | KOBJ magic/version/tags and v2 backward compatibility. | Legacy wire contract only | DELETE → no compatibility successor | Retirement explicitly removes KOBJ, not validation obligations. New ELF malformed-header/section/relocation tests are separate required coverage. |
| Write_FailedModel_Throws | Failed assembly must not become successful artifact because diagnostics disappear on disk. | Trust-boundary invariant G | REPLACE → failed assembly produces no/replaces no ELF output | Preserve existing destination. |
| Roundtrip_Patch, Roundtrip_PatchSymbolName, Roundtrip_LineMap_PreservesEntries, Roundtrip_LineMap_EmptySection_IsZeroEntries | Patch kind/PC/source metadata and line records survive. | G, D | REPLACE → ELF relocation/provenance persistence | Roundtrip_Patch puts a 16-bit patch at offset 3 in 4 bytes: malformed graph, unsuitable positive ELF fixture. EmptySection name actually uses one byte; test asserts empty map, not empty section. |
| RgbdsFormatTests: Header_ContainsMagicAndRevision, Header_SymbolAndSectionCounts, SectionType_Rom0EncodedCorrectly | RGB9/revision13/node/count/type encodings. | R, unverified live here | DELETE → independent ELF header/table cases | Do not translate numeric RGBDS codes into an ELF API. |
| WriteAndRead_DoesNotThrow, Section_ContainsCorrectData, RamSection_NoDataEmitted | Intended valid serialization/data/RAM shape; bodies merely length>20/30 and finding three zero bytes anywhere. | Insufficient: header can satisfy these assertions | REPLACE → parsed ELF PROGBITS/NOBITS size/data assertions | Names overstate actual coverage. WriteAndRead never reads. |
| Symbol_ExportedLabelPresent, MultipleSymbols_AllPresent | Names retained; body checks UTF-8 substring only. | Weak observable name presence | REPLACE → parsed ELF symbol binding/section/value and linked consumer | Substrings do not prove a valid symbol table. |
| FailedModel_ThrowsException | No object from failed source. | G | REPLACE → ELF/CLI failure-output preservation | Retain boundary invariant. |
| ImportSymbol_WrittenCorrectly, ImportSymbol_PatchContainsRpnSymbolOpcode, ForwardRef_SameFile_NotImport | Undefined external explicit import; actual local forward ref stays defined. | G and source binding semantics | REPLACE → ELF undefined/global versus defined/local and real two-object link | RGBDS import type and RPN bytes retire. |
| RgbdsExpressionTests: Operators, CurrentAddressAndLiterals, FunctionCalls, InstructionOperands | Complete operator trees, current instruction PC, HIGH/LOW/BANK and width/relative semantics; exact RGBDS RPN decoder. | Arithmetic/PC C, G; wire R | REPLACE → evaluate same expressions after placement through ELF | Do not delete expression coverage with its RGBDS byte expectations. |

Additional exact KOBJ cases: Roundtrip_LineMap_FromRealSource is REPLACE with real source → ELF → KDBG line coverage; V1KobjWithoutLineMap_Reads_LineMapEmpty is DELETE (retired v1 compatibility); Roundtrip_Patch_SymbolName_NonNull and Roundtrip_Patch_SymbolName_Null are REPLACE with defined/undefined symbol relocation identity checks. The Roundtrip_PatchSymbolName shorthand above refers to these two cases. No KOBJ compatibility case is evidence for ELF compatibility.

## Rust object and assembler tests

| Original fixture / cases | Invariant / legacy detail | Evidence | Decision → target | Rationale |
|---|---|---|---|---|
| rust/koh-asm/src/object.rs: kobj_v4_keeps_patch_metadata_line_map_and_int64_symbol_values | Exact KOBJ v4 fields; int64 values and line metadata | G; existing decoder is same repository | REPLACE → ELF value-width/provenance tests; DELETE exact KOH bytes | Preserve values without old wire format. |
| kobj_current_address_uses_collision_safe_adapter_only_anchor, kobj_derives_relocations_without_changing_high_low_arithmetic | KOBJ affine adapter synthesizes anchor; HIGH(x)+1 differs from HIGH(x+1), LOW word masking | Mathematical counterexamples C | REPLACE arithmetic/PC through ELF; DELETE synthetic-anchor assertions | Anchor exists solely for restricted KOBJ adapter. |
| kobj_rejects_imported_symbols_even_without_a_patch, kobj_case_insensitive_backend_names_are_explicitly_restricted, kobj_alignment_limits_and_utf8_lengths_are_explicit | KOBJ rejects valid imports/case-sensitive names/non-ASCII/floating alignment; .NET string lengths | Restriction itself only legacy; G needs full representation | DELETE refusal/string-byte cases; REPLACE → ELF accepts/preserves distinct identities, UTF-8 and alignment | New format must not inherit accidental KOBJ limitations. |
| rgbds_keeps_expansion_context_and_patch_location, rgbds_pc_context_is_independent_of_patch_section | Source stack/use location and execution PC retained | D, G | REPLACE → ELF debug metadata and LOAD execution-PC tests | Remove RGBDS source-node layout only. |
| rgbds_signed_patches_have_link_time_range_assertions, high_memory_semantics_are_validated_or_encoded | Signed/high-memory range validation; RGBDS synthesized assertion/RPN | C, G | REPLACE → Rust linker accepted/rejected boundary values | Require actual link failure for invalid values. |
| section_placement_checks_memory_and_bank_boundaries | All eight memory regions reject invalid bank/address/size | M; current loop derives limits from implementation, not independent | PORT → independently tabulated region boundary tests | Avoid testing function against itself. |
| malformed_symbol_section_data_and_provenance_graphs_are_rejected | Dangling symbol/section IDs, invalid label/patch bounds, missing provenance, invalid BANK constant/section kind | G | PORT → shared graph validation + ELF invalid input tests | Keep trust-boundary checks; wire corruption needs additional tests. |
| every_typed_operator_and_section_query_has_exact_rpn | Binary/unary operators; BANK, SIZEOF, STARTOF section/type queries | C, G; numeric bytes R | REPLACE → roundtrip expression graph and independently evaluated final bytes | Exact RPN bytes retire; all operators/queries remain obligations. |
| section_combinations_are_preserved_or_explicitly_refused, cross_object_union_and_fragment_link_like_rgbds_when_enabled | UNION overlays max size; FRAGMENT concatenates; cross-object labels and SIZEOF rebased | Existing independent rgbasm/rgblink fixtures, gated/unrun | REPLACE → Rust ELF multi-object combination cases with same expected bytes | Do not drop non-KOBJ features during migration. |
| tests/object_architecture.rs: complete_expressions_survive_assembly_independent_of_output_format, missing_references_are_explicit_imports_or_diagnostics, pc_tracks_execution_section_inside_load, invalid_graph_is_rejected_before_serialization, raw_identifier_escape_is_not_part_of_symbol_identity, current_address_divisor_is_not_assumed_to_be_zero, signed_offset_semantics_survive_deferred_resolution | Rich graph, imports, LOAD, validation, identifier identity, deferred division/signed semantics; write_kobj/write_rgbds success/refusal arms | G, C | REPLACE writer arms with ELF roundtrip; PORT graph assertions | Keep assembly independent of format limitations. |
| tests/object_architecture.rs: lowering_errors_keep_the_actual_use_location, floating_pc_expressions_and_numeric_jr_targets_remain_relocations, bank_current_uses_its_own_section, unsupported_relocation_functions_and_constant_faults_are_diagnostics | Correct location, delayed PC/bank evaluation, unsupported/invalid expressions rejected | G, C | PORT in place, add final linked-byte counterparts | Graph shape alone cannot prove evaluation correctness. |
| tests/relocations_rgbds.rs: complete_relocation_expressions_link_independently, load_uses_floating_execution_section_for_pc_bank_and_jr, signed_external_offset_is_asserted_at_link_time, external_high_memory_address_is_validated_at_link_time, unresolved_import_is_a_link_error, floating_alignment_constrains_base_without_unnecessary_padding | Exact bytes and invalid links; externally pinned tools; gate returns early unless KOH_RGBDS_TEST=1 | Independent oracle exists, no run evidence in this audit | REPLACE native branch with ELF + Rust linker; retain reference RGBDS source→ROM branch if useful | Eliminating RGBDS object output need not eliminate independent language/link oracle. |
| tests/cli.rs: formats_defaults_and_output_aliases, koh_empty_warn_extension_remains_nonfatal | Defaults/aliases and output suffix; KOBJ default and RGBDS formats | CLI current contract S | REPLACE → ELF defaults/options and documented rejection of retired formats | A byte-nonempty artifact is insufficient format verification. |
| tests/cli.rs: failed_assembly_preserves_existing_output_and_reports_source_location, successful_assembly_replaces_output_and_refuses_source_overwrite, failed_output_rename_preserves_destination_and_removes_temporary_file, structured_warnings_are_printed_once_without_error_labels, help_version_and_argument_diagnostics | Atomic artifact replacement/source safety/diagnostics; .kobj suffix/RGBDS flags incidental | Observable filesystem and exit status | PORT with new format arguments/suffix | Safety invariants survive retirement. |
| tests/encoding_rgbds.rs, tests/instruction_context.rs context_dependent_instruction_bytes_match_rgbds_1_0_1 | Independent instruction-byte comparison uses RGBDS object writer/linking | C, gated R | REPLACE native serialization/link step; keep reference assembler | Encoding compatibility remains a requirement. |
| tests/assembly.rs floating_symbol_relocation_survives; remaining graph/section-data assembly/directives tests | Deferred symbol/current address and data assembly behavior | G, C | PORT graph tests unchanged unless imports move | No need to rewrite unrelated source expansion tests. |

## Compiler and emulator integration

All 51 C# files in the appendix import the linker directly. Runtime cases construct a compiler EmitModel, call Linker.Link, then load its ROM into GameBoySystem. REPLACE that boundary with compiler ELF emission → Rust linker; retain original source, parameter rows, expected registers/memory and execution budgets. Compile-only optimizer assertions in those files remain in place and must not be deleted just because the harness changes.

| Coherent group | Actual invariant / legacy dependency | Evidence | Decision → target |
|---|---|---|---|
| Backends/Sm83BackendTests; Sm83ControlFlowTests; Sm83CompareBranchFusionTests; Sm83GepFusionTests; Sm83LoopPointerPhiResidencyTests | Arithmetic, branch/call/phi/pointer/global memory, optimizer output and emulator results; fixed CodeBase and EmitModel data lengths in load/exit helper | C; structural optimizer assertions are narrower | REPLACE linker harness; PORT runtime/optimizer assertions |
| Sm83/Sm83SymbolAddressTests (all 3) | Helper symbol matches decoded CALL operand, Main symbol matches header JP, WRAM global matches WramBase | Independent machine-byte address relation C; WRAM-base policy S | REPLACE → compiler ELF symbol regression; preserve all three |
| Sm83/Sm83LoopCodegenTests, FlushRunLoopResidencyTests, RedundantCompareLoopTests, MemRuntimeTests | Loops/runtime memcpy/clear and register residency run to expected memory/state | C; fixed addresses/budgets in fixtures | REPLACE boundary; preserve runtime checks and sources |
| Frontends/Cil* files in appendix | Real Roslyn assembly → CIL → SM83 → emulator: integers/floats/strings/classes/arrays/generics/delegates/iterators/recursion/statics/MMIO/graphics | Host results or explicit state C; Debug/Release equality alone is weaker than fixed expected result | REPLACE boundary; preserve both expected results and optimization parity |
| CilGraphicsSlice2Tests aligned shadow OAM; CilInterruptTests handlers | 256-byte WRAM alignment and DMA correctness; vector placement/runtime IRQ effects | Alignment arithmetic and hardware-register emulator effects C | REPLACE ELF section/symbol boundary; retain DMA/IRQ assertions |
| Samples/CilGame2048Tests, Gb2048V2Tests, CgbHalTests, GbGfxDemoTests, Cube3dTests, GbJrpgTests | Real samples compile/run; WRAM game logic, graphics/register/frame state and captures | C; emulator captures are not physical-HIL proof | REPLACE boundary; retain sample input/output ownership and avoid tracked screenshot writes |
| Samples/Gb2048KdbgTests Kdbg_ResolvesSymbolAndSourceLocation_ForCilFrontendBuild | Portable PDB source paths survive to KDBG symbol/source lookup; mirrors SDK manually | D, C; does not invoke SDK/MSBuild task | REPLACE → compiler ELF + Rust KDBG read by existing loader; retain no-PDB frontend-only test |
| Emulator/Koh.Debugger.Tests/DapGeneratedRomIntegrationTests (both cases) | Generated assembly ROM/KDBG launches, source breakpoint stops at PC $102 with A=$42; stack/source/scopes/symbols; interior instruction address resolves | D and independently derived opcode size C | REPLACE assembly/link helper with real ELF+Rust pipeline; preserve complete DAP flow |
| Emulator debugger DapDispatcherTests SetBreakpoints verified/unverified and StepHandlersTests StackTrace_Includes_Source_When_DebugInfo_Maps_Current_Pc | Synthetic KDBG writer dependency; source maps/breakpoint verification | D | PORT to retained KDBG utility or Rust-produced fixture; remaining stepping/conditional cases stay unchanged |

Debugger ConditionalBreakpointTests imports Koh.Linker but does not construct a linker or KDBG writer. It is not a source-to-ROM test. Other debugger tests build ROM bytes directly; do not count them as replacement-linker coverage.

## SDK, scripts, editor and integration gaps

| Existing fixture / path | Invariant / legacy dependency | Evidence | Decision → target |
|---|---|---|---|
| scripts/verify-rust-assembler.py verify_case / compare_kobj / KobjObject / kobj_snapshot; tools/rust-asm-verify and reference helper | C#/Rust KOBJ structural parity, masking relocation bytes; same C# linker for both outputs | Parity alone cannot validate linker; S | REPLACE with ELF structural validation plus independent final-byte expectations; DELETE old KOBJ parser/comparisons |
| verify_cross_file, verify_negative_cases, verify_independent_semantics, generated execution fixture | True multi-object imports/addends/HIGH/BANK, failure source locations, independent assembly semantics, CALL/return producing 2A 2B at $C000 within 10,000 instructions | Independent RGBDS oracle and explicit C result, not run here | REPLACE native pipeline, retain independent source/reference ROM and runtime signature |
| verify_corpus / expected-differences.json | Extracted positive raw Emit literals only; excluded/changed sources and baseline graph classification | Hash/graph regression not independent semantic validation | REPLACE KOBJ snapshots with ELF semantics; preserve honest excluded counts |
| gb-2048 assembly sample and build.proj sample targets | Actual CLI file pipeline .kobj → koh-link .gb/.sym/.kdbg | S | REPLACE with ELF → Rust linker, verify artifacts and emulator behavior |
| .github/workflows/ci.yml Koh.Ci.slnf SDK samples | SDK builds compile ROM and optional KDBG through CompileKohRom; currently C# linker in-process | S; build success is not run proof | REPLACE task/packaging boundary and build samples serially; inspect actual ROM/KDBG |
| src/Compiler/Koh.Sdk/Sdk/Sdk.targets CompileKohRom and props stamp | ROM/KDBG paths, CGB flags, optional debug, property/reference/tool invalidation and clean tracking | Read targets; no dedicated SDK integration test project found | DEFER → required clean/incremental SDK integration: toggle flags/debug, change source/reference/linker, rebuild without source change, inspect artifacts |
| editors/vscode extension tests and publish/install scripts | Tool executable discovery, assembler/linker arguments/output paths and platform packaging | Not body-audited in this unit | DEFER → editor owner inventories launcher tests and validates new binaries/arguments per platform |

SDK tests that only imitate CompileKohRom in C# cannot establish MSBuild target ordering, fresh tool deployment, incremental invalidation or packaging. New Rust executable/library changes must participate in target Inputs; otherwise a successful incremental build may retain an old ROM.

## Required migration gates

1. Every PORT/REPLACE row has real replacement coverage before deleting its old owner.
2. ELF reader rejects malformed/truncated headers/tables/strings, invalid indexes, out-of-bounds relocation widths/expressions and unsupported required metadata without artifact damage.
3. Cross-object local namespaces, exported imports, absolute constants, every retained operator/query, LOAD PC, signed/high-memory/JR boundaries, alignment, UNION/FRAGMENT and debug provenance have independently expected outcomes.
4. Fresh assembler and compiler/SDK ELF files link with the Rust implementation; no hidden C# linker or KOBJ conversion path supplies success.
5. Read completed test logs and actual artifact bytes. RGBDS-gated early-return tests do not count as executed oracle cases.
6. No execution was performed during the initial audit. Subsequent focused execution is recorded below; it does not establish hardware behavior or a complete SDK/platform qualification.

## Final implementation mapping and feature coverage

This maps contracts rather than equating old and new test counts. C# paths retain the Koh.Linker.Tests project name for solution compatibility; their production linker is Koh.Native.NativeLinker invoking the Rust executable. All Rust target names in this section were checked against the actual test bodies. Their presence is not an execution claim.

Abbreviations: **EI** = tests/Common/Koh.Objects.Tests/ElfIntegrationTests.cs; **LT**, **CT**, **AT**, **LM**, **KT**, **RI** = LinkerTests, ConstraintDiagnosticTests, AssemblerLinkerGoldenIntegrationTests, LinkerLineMapIntegrationTests, KdbgFileWriterTests, RawImageTests under tests/Link/Koh.Linker.Tests. **OE** = rust/koh-objects/tests/elf.rs; **RL** = rust/koh-link/src/tests.rs; **LC** = rust/koh-link/tests/cli.rs.

| Feature contract before migration | Actual final tests | Coverage after migration / remaining limit |
|---|---|---|
| KOBJ section data, empty/multiple sections, RAM kind/size, constants and exported labels | EI.Elf32RelocatableHeaderAndSectionData; EI.EmptyMultipleAndLargeSectionsRetainDataAndSymbols; OE.standard_sections_symbols_and_provenance_survive | Checks ELF32 ET_REL fields, PROGBITS bytes, NOBITS size, 256-byte payload, linked RAM address and constant value. OE reads through object::read::elf before Koh readback. KOBJ wire bytes intentionally absent. |
| Imports, forward references, symbol visibility and patch identity | EI.ImportsAndLocalForwardReferencesResolveAcrossSeparateElfFiles; EI.InvalidElfAndMissingImportsFailWithoutRom; RL.import_resolves_export_not_same_named_local; RL.undefined_and_duplicate_exports_are_errors; retained LT.Link_DuplicateExport_Diagnostic | Real separate consumer/provider ELF files link to independently expected CALL/data bytes. An unrelated same-named local cannot satisfy an import. Rust duplicate test asserts error; the old C# test still only checks its diagnostic substring. |
| All unary/binary expression operators survive serialization and evaluate correctly | EI.EveryDeferredOperatorEvaluatesAfterImportResolution; EI.CurrentAddressHighLowBankAndInstructionOperandsKeepTheirContext; OE.all_expression_operators_survive; RL.expression_arithmetic_matches_rgbds_floor_and_wrapping; RL.remaining_binary_operators_have_explicit_results; RL.all_expression_queries_and_unary_operators_resolve | C# independently expects values for imported ext=5; Rust separately checks arithmetic edge values and section/bank/unary queries. OE roundtrip checks preservation, not evaluation. RGBDS RPN byte codes are retired. |
| Deferred patch width, signed/relative/high-memory meaning, independent execution PC | OE.relocation_semantics_widths_pc_and_expansion_survive; RL.patches_reject_out_of_range_and_accept_boundaries; rust/koh-asm/tests/object_architecture.rs.pc_tracks_execution_section_inside_load; rust/koh-asm/tests/relocations_rgbds.rs.load_uses_floating_execution_section_for_pc_bank_and_jr, signed_external_offset_is_asserted_at_link_time, external_high_memory_address_is_validated_at_link_time | Width/semantics/PC serialize; byte boundary cases evaluate; LOAD and external signed/high-memory cases compare Rust ELF linking against independently assembled RGBDS input when the oracle gate is enabled. The oracle is not exercised by the focused .NET runs below. |
| Fixed/floating placement, ROMX physical offsets and symbol banks | Retained LT.Link_SectionPlacedAtCorrectAddress, Link_FixedAddress_PlacedCorrectly, Link_RomxSection_PlacedInPhysicalBankOffset, Link_TwoRomxBanks_DataAtCorrectPhysicalOffsets, Link_RomxSection_SymbolHasCorrectBank; RL.fixed_reservations_precede_floating_and_overlaps_fail; RL.all_memory_regions_obey_bank_and_alignment | Actual image and symbol checks remain. AT.CrossSectionAbsoluteRef_EmitsCorrectJpTarget and FloatingSection_SameSectionRefs_ResolveToPlacedAddress now derive expected references from placed symbols instead of assuming free ROM0 space below $100 must be skipped. |
| Capacity/overlap failure and actionable numerical diagnostics | All eight original CT.Placement_* methods retained; OE.section_placement_checks_memory_and_bank_boundaries; OE.malformed_symbol_section_data_and_provenance_graphs_are_rejected | CT uses Rust through NativeLinker; accepts decimal or equivalent hexadecimal numerical values; preserves distinct required/free/overflow amounts and both conflicting names. OE's memory-boundary loop still derives region limits from memory_region, so it remains self-consistency evidence rather than an independently tabulated hardware map. |
| UNION/FRAGMENT combination and rebasing | OE.section_combinations_names_and_optional_provenance_survive; RL.fragments_append_and_adjust_constraints_symbols_and_relocations; RL.union_uses_maximum_size_and_shared_start | Checks shared max-size start, appended bytes, rebased fragment symbol/PC and preserved combination metadata. These replace RGBDS writer/KOBJ-refusal assertions. |
| Exact raw image size, zero fill, no checksum mutation; separate cartridge sizing/checksums/CGB flags | All original RI tests retained; LT.Link_HeaderChecksum_Correct, Link_GlobalChecksum_Correct, Link_CgbCompatible_SetsHeaderFlagAndKeepsChecksumsValid; RL.raw_is_exact_and_preserves_header_bytes; RL.cartridge_rounds_sets_cgb_and_both_checksums | Rust and C# pipeline cases check exact $100/$900 raw length, overflow refusal, preserved $14d bytes, cartridge bank rounding and checksum formulas. RI.Cartridge_Output_Still_Gets_Its_Checksums_Fixed remains a weaker inequality check; independent formulas are covered by LT/RL. |
| Macro/charmap/local-label bytes and serialized data relocations | All seven original AT cases retained; CrossSection_KobjRoundtrip_ResolvesDataRef renamed CrossSection_ElfObject_ResolvesDataRef | Same golden bytes and fixed ROM/RAM symbol invariants cross the ELF boundary. The renamed case remains one object containing two sections; EI.ImportsAndLocalForwardReferencesResolveAcrossSeparateElfFiles provides true multi-object coverage. |
| Real source line ranges and banked instruction addresses | All five LM cases retained; EI.SourceLineRangesSurviveSerializedElf; OE.standard_sections_symbols_and_provenance_survive | NativeLinker writes the assembler's ELF image and Rust produces KDBG. Tests cover no-code source lines, multibyte instruction start, bank 3 addresses and 600-byte line splitting. |
| KDBG magic/version, symbols/source, explicit size and expansion ancestry | All four KT methods retained with real Rust-produced KDBG; RL.kdbg_chunks_lines_and_preserves_expansion_offsets; RL.kdbg_rejects_unrepresentable_symbols; tests/Emulator/Koh.Debugger.Tests/KdbgFixtureTests.ReaderPreservesExplicitSymbolSizeAndSource | Real nested macro expansion checks frame file/line values, not just count. Rust labels with unknown size assert zero. Synthetic explicit size=3 and line=42 reader coverage remains in the debugger fixture test, using test-only writer/builder. |
| End-to-end debugger breakpoint/source/register agreement | DapGeneratedRomIntegrationTests.GeneratedRomKdbg_DapLaunchBreakpointStackTraceScopesAndVariables and StackTrace_UsesSourceForPcInsideMultiByteMappedRange; existing DapDispatcherTests and StepHandlersTests source-map cases | Real assembly ELF/Rust ROM/KDBG feeds the existing debugger: breakpoint at $102, A=$42, stack line/source and symbol/context scopes remain asserted. Synthetic debugger fixtures use tests/Support; they are not counted as Rust linking proof. |
| Compiler calls/header entry/WRAM symbol metadata and portable-PDB source path | Sm83SymbolAddressTests.FunctionLabel_SymbolAtActualCodeAddress_NotDoubleCountingCodeBase, EntryFunctionLabel_SymbolMatchesHeaderEntryPoint, WramGlobalLabel_SymbolAtWramBase; Gb2048KdbgTests.Kdbg_ResolvesSymbolAndSourceLocation_ForCilFrontendBuild | Uses NativeLinker; compares linked symbols against independent CALL/header JP bytes and WRAM-base expectation. Real sample PDB source resolves through Rust KDBG to existing SymbolMap/SourceMap. No-PDB frontend test is preserved. |
| Remaining compiler/emulator observable behavior | All 51 direct-linker files and cases enumerated in the appendix remain; aliases now point at Koh.Native.NativeLinker | Existing runtime/optimizer expected-state assertions remain. Only the symbol/KDBG classes were executed in this unit; full compiler execution belongs to the broader validation run. |
| Invalid/truncated object rejection and preservation of existing artifacts | EI.InvalidElfAndMissingImportsFailWithoutRom, FailedAssemblyCannotEmitElf; OE.every_truncation_and_corruption_is_rejected, invalid_graph_never_serializes, malformed_rpn_is_rejected_before_evaluation, malformed_symbol_section_data_and_provenance_graphs_are_rejected, overlapping_section_payloads_cannot_amplify_input_allocations; rust/koh-objects/src/elf.rs.rpn_pending_operands_obey_expression_depth_limit; LC.corrupt_input_preserves_existing_output, alias_output_cannot_overwrite_input | Replaces KOBJ magic/version/tag and RGBDS failed-model cases with actual ELF rejection and trust-boundary tests. Truncation covers each prefix of one fixture; corruption mutates selected fields, not every possible ELF corruption. |
| Assembler/linker CLI formats, output aliases, help/version, whitespace paths and source/output safety | Retained rust/koh-asm/tests/cli.rs cases; LC.json_to_elf_to_rom_sym_debug_and_report, long_aliases_version_and_default_outputs_with_spaces, corrupt_input_preserves_existing_output, alias_output_cannot_overwrite_input | Existing assembler safety cases now target ELF. Linker exercises real executable JSON→ELF→ROM/.sym/KDBG/report and default/explicit paths. |
| Independent instruction/expression/relocation comparison | rust/koh-asm/tests/encoding_rgbds.rs.exhaustive_instruction_output_matches_rgbds_1_0_1; retained instruction_context.rs.context_dependent_instruction_bytes_match_rgbds_1_0_1; all six relocations_rgbds.rs cases; RL.rgbds_101_expression_oracle | Native object side is ELF/Rust; RGBDS object format remains only external reference-tool output. KOH_RGBDS_TEST=1 gate must be enabled explicitly. Source presence alone is not a passing oracle result. |
| Historical assembler corpus and full sample parity | scripts/verify-rust-assembler.py compare_case/negative_cases/main replaces KOBJ/reference-helper flow; execution, relocations, macros, opcodes, semantics, gb2048, cross-file workflows | New harness retains named independent fixtures and emulator signature. It no longer extracts all historical C# raw Emit literals or checks old expected-differences graph snapshots. That historical corpus coverage is not claimed equivalent. The 2048 byte comparison pins a temporary native source copy to independently obtained RGBDS placements; a separate floating image is built, but matching pinned bytes does not prove identical unconstrained allocation. |
| SDK/MSBuild incremental behavior and editor/platform launch | SDK targets/sample build graph migrated by implementation; no dedicated new test identified in this unit | Remains DEFER for this audit: source/reference/tool/property invalidation, debug-info toggling, clean tracking, executable packaging/discovery and cross-platform editor launch need direct integration evidence. Compiler tests that imitate the SDK do not discharge this gate. |

## Explicit deletion and replacement ledger

The three removed C# test files contained 37 methods, regrouped by contract above. Eight EI methods do not imply equal coverage by count. Only obsolete wire assertions were intentionally retired; semantic obligations have named successors or an explicit limit.

- **KobjRoundtripTests.cs (21 methods)**: DELETE InvalidMagic_Throws, InvalidVersion_Throws, UnknownTag_Throws, V1KobjWithoutLineMap_Reads_LineMapEmpty, V2KobjWithPatches_Reads_SymbolNameNull as KOBJ magic/tag/version/backward-compatibility contracts. ELF malformed-input tests replace the validation category, not KOBJ compatibility. REPLACE Roundtrip_SimpleProgram, Symbols, MultipleSections, SectionType, EmptyProgram, SymbolCount, LargeData, ConstantSection_IsNull and DeserializedSuccess_IsTrue with EI section/symbol/data cases and OE.standard_sections_symbols_and_provenance_survive; there is no deserialized Success flag in ELF. REPLACE Write_FailedModel_Throws with EI.FailedAssemblyCannotEmitElf. REPLACE Roundtrip_Patch, Roundtrip_Patch_SymbolName_NonNull/Null with EI import/cross-file linking and OE relocation preservation; the malformed old positive patch-at-offset-3-in-four-bytes is not retained as a valid fixture. REPLACE all three Roundtrip_LineMap_* methods with EI.SourceLineRangesSurviveSerializedElf, LM cases and OE provenance preservation; real sources, empty maps and serialized ranges remain represented.
- **RgbdsFormatTests.cs (12 methods)**: DELETE Header_ContainsMagicAndRevision, Header_SymbolAndSectionCounts and SectionType_Rom0EncodedCorrectly numerical RGB9/revision13/node/type encodings. REPLACE WriteAndRead_DoesNotThrow, Section_ContainsCorrectData and RamSection_NoDataEmitted weak length/zero-search checks with EI.Elf32RelocatableHeaderAndSectionData structural PROGBITS/NOBITS checks. REPLACE Symbol_ExportedLabelPresent and MultipleSymbols_AllPresent substring checks with parsed symbol and actual link assertions in EI/OE. REPLACE FailedModel_ThrowsException, ImportSymbol_WrittenCorrectly, ImportSymbol_PatchContainsRpnSymbolOpcode and ForwardRef_SameFile_NotImport with EI failure/import/forward-reference cases; RGBDS import/RPN opcode identity is retired.
- **RgbdsExpressionTests.cs (4 methods)**: REPLACE Operators, CurrentAddressAndLiterals, FunctionCalls and InstructionOperands with EI deferred-expression/current-PC/operand cases, OE.all_expression_operators_survive and RL evaluation/query tests. DELETE the test-only RGBDS decoder and its exact RPN token strings; arithmetic, placement-dependent PC and patch semantics survive.
- **rust/koh-asm/src/object.rs writer unit tests**: the module's KOBJ/RGBDS writers and exact wire tests are deleted. OE/RL and retained assembler object_architecture tests own the graph/evaluation/provenance/combination/bounds invariants. KOBJ affine-anchor generation, .NET string encodings, case-insensitive-name prohibitions and refusal of valid imports/floating alignment have no successor requirement because they were adapter limitations. OE.section_combinations_names_and_optional_provenance_survive instead accepts case-distinct names and UTF-8 section/source text. Exact old int64 KOBJ symbol storage is not an ELF32 promise: the new expression/value domain is 32 bits, as documented in elf32-sm83.md; KDBG v1 additionally rejects values it cannot represent.
- **tools/rust-asm-verify/reference and old KOBJ/RGBDS parsers/snapshots in the verifier**: DELETE the retired C# object-format/linker reference path and wire snapshots. The execute-only helper and new independent RGBDS source-to-ROM workflows replace executable behavior checks. Historical corpus extraction/baseline classification is not silently claimed preserved; see the coverage limit above.
- **No compiler runtime, assembler golden, placement, raw-image or generated-ROM debugger test class was deleted.** Linker dependency aliases and artifact consumers changed; synthetic KDBG writer/builder code moved to tests/Support, outside production. Linker KDBG symbol-size coverage was split into unknown real size=0 and explicit synthetic size=3.

## Focused execution evidence and reproduction

These results are from completed log files reread after their corresponding process returned. Every listed command returned exit 0 and every run below reported zero skipped cases. Focused results do not stand in for full CI, full compiler, SDK incremental, editor UI, external-oracle, or hardware runs.

| Target | Completed build log | Completed test log and actual summary |
|---|---|---|
| Koh.Linker.Tests | /tmp/koh-link-tests-build-final.log: Build succeeded, 0 warnings, 0 errors | /tmp/koh-link-tests-final.log: total 52, succeeded 52, failed 0, skipped 0 |
| Koh.Debugger.Tests | /tmp/koh-debugger-tests-build.log: Build succeeded, 0 warnings, 0 errors | /tmp/koh-debugger-tests.log: total 37, succeeded 37, failed 0, skipped 0 |
| Koh.Objects.Tests | /tmp/koh-objects-tests-build.log: Build succeeded, 0 warnings, 0 errors | /tmp/koh-objects-tests.log: total 8, succeeded 8, failed 0, skipped 0 |
| Koh.Compiler.Tests / Sm83SymbolAddressTests | /tmp/koh-compiler-tests-build.log: Build succeeded, 0 warnings, 0 errors | /tmp/koh-compiler-symbol-tests.log: total 3, succeeded 3, failed 0, skipped 0 |
| Koh.Compiler.Tests / Gb2048KdbgTests | same compiler build | /tmp/koh-compiler-kdbg-tests.log: total 2, succeeded 2, failed 0, skipped 0 |

The first Link run failed 21/52 cases and exposed facade JSON type/null handling, missing emitted-byte macro provenance, and diagnostics omissions; those were fixed in production and the fresh final 52/52 run above covers them. One combined TUnit filter run exited 0 but reported zero tests: /tmp/koh-compiler-focused-tests.log. It is excluded from passing evidence; the two explicit single-class filters above are the measured compiler cases.

From the repository root, use a .NET SDK satisfying global.json (the local run used a locally installed .NET 10.0.115 SDK first in PATH). Build serially, then run each focused project serially:

```sh
dotnet build tests/Common/Koh.Objects.Tests/Koh.Objects.Tests.csproj -c Release -m:1 > /tmp/koh-objects-tests-build.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-objects-tests-build.log
dotnet test --project tests/Common/Koh.Objects.Tests/Koh.Objects.Tests.csproj -c Release --no-build -- --maximum-parallel-tests 1 > /tmp/koh-objects-tests.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-objects-tests.log
```

Repeat that build/test command pair with tests/Link/Koh.Linker.Tests/Koh.Linker.Tests.csproj and tests/Emulator/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj, using separate logs. For the measured compiler subset:

```sh
dotnet build tests/Compiler/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj -c Release -m:1 > /tmp/koh-compiler-tests-build.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-compiler-tests-build.log
dotnet test --project tests/Compiler/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj -c Release --no-build -- --maximum-parallel-tests 1 --treenode-filter '/*/*/Sm83SymbolAddressTests/*' > /tmp/koh-compiler-symbol-tests.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-compiler-symbol-tests.log
dotnet test --project tests/Compiler/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj -c Release --no-build -- --maximum-parallel-tests 1 --treenode-filter '/*/*/Gb2048KdbgTests/*' > /tmp/koh-compiler-kdbg-tests.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-compiler-kdbg-tests.log
```

The following reproduce Rust/oracle categories; this document does not claim their execution from the focused .NET logs:

```sh
cargo test --workspace --locked > /tmp/koh-workspace-tests.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-workspace-tests.log
KOH_RGBDS_TEST=1 cargo test --workspace --locked > /tmp/koh-rgbds-oracle-tests.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-rgbds-oracle-tests.log
python3 scripts/verify-rust-assembler.py > /tmp/koh-independent-workflows.log 2>&1
echo "EXIT=$?"
cat /tmp/koh-independent-workflows.log
```

RGBDS commands require installed rgbasm/rgblink 1.0.1. Artifact/log paths above are session-local evidence, not committed test fixtures; rerun commands on another checkout. For tests using the Native facade, its build target builds/deploys the native tools; --no-build is meaningful only after the appropriate current build.

## Independent emulator observation

Both representative ROMs were also run in **mGBA Qt 0.11.0**, independently of Koh.Emulator, using scripted framebuffer capture. Each run used an isolated ROM copy and configuration under ~/.cache/mgba-instances, QT_QPA_PLATFORM=offscreen, DISPLAY and WAYLAND_DISPLAY unset, and -C mute=1. No tracked sample screenshot was written. Each process finished at 600 frames with exit 0; the bounded controller retained its PID and would kill only that PID after 35 seconds. Both processes are stopped.

The input script was identical: START at frame 180; LEFT, UP, RIGHT, DOWN, LEFT, UP at frames 300,350,400,450,500,550, held eight frames each. Captures at frames 150,300,450,600 came from mGBA itself and were inspected with view_image. The Lua sidecar read Game Boy addresses directly, not the GBA cartridge address base. This proves the particular boot/title/gameplay sequence below; it is not physical hardware, endurance, save persistence, or general emulator compatibility qualification.

| ROM / SHA-256 of source and loaded copy | Actual observed behavior | Primary artifacts |
|---|---|---|
| samples/csharp/gb-2048-v2/2048v2.gb / fd83e0d12508708197214637def32d5d207bfb6ec943658455dad3d1aa589219 | Frame 150 shows 2048 / PRESS START; frame 300 shows SCORE 0 and two occupied cells; frame 600 shows SCORE 20 and a changed board. Lua $9800 background-map reads at the fixture's 4x4 board grid count 0,2,4,3 occupied cells at frames 150,300,450,600. $C000–$C0ff bytes were also captured but are not interpreted as named C# state. | $HOME/.cache/mgba-instances/koh-rust-link-csharp-pi8v71ot/ |
| /tmp/koh-native-verification/gb2048-floating.gb / 8c15e8a8c853f1b2f061021b2b7912995382ba25f79d5fd7db6c2427525f97af | Frame 150 shows KOH 2048 / PRESS START; frame 300 shows two 2 tiles; frame 600 shows score 000020, two 8 tiles and three 2 tiles. Lua wBoard at $C000 has 0,2,4,5 occupied cells; wGameState at $C06e changes 0→1; wScore at $C060 ends 14000000 (little-endian 20). The independently replayed ELF link matches the loaded floating image hash exactly. | $HOME/.cache/mgba-instances/koh-rust-link-asm-w_ub31gj/ |

Each artifact directory contains frame-00150.png, frame-00300.png, frame-00450.png, frame-00600.png, memory.txt, run.json, capture.lua, mgba.log and pid. The assembly directory additionally contains report.json, relink.log and relinked.gb: wBoard/wScore/wGameState addresses agree with the fixed $C000 section and declaration sizes in samples/asm/gb-2048/src/memory.inc. Its relink.log reports EXIT=0. run.json records source/copy hashes, process ID, exit status, frame count and keys. The logs contain an XDG_RUNTIME_DIR warning; complete rendered PNGs and flushed frame600 memory records were present despite that warning.

To replay either retained instance after choosing its artifact directory as RUN, copy its game.gb into a fresh isolated directory, set XDG_CONFIG_HOME to that directory's config, preserve the Lua MEMORY/BOARD/SCORE/STATE variables where applicable, and invoke:

```sh
env -u DISPLAY -u WAYLAND_DISPLAY QT_QPA_PLATFORM=offscreen \
  FRAMES=600 EVERY=150 SHOT="$RUN/frame-%d.png" MEMORY="$RUN/memory.txt" \
  KEYS='180:START,300:LEFT,350:UP,400:RIGHT,450:DOWN,500:LEFT,550:UP' \
  mgba-qt -C mute=1 "$RUN/game.gb" --script "$RUN/capture.lua"
```

The assembly Lua requires BOARD=49152, SCORE=49248, STATE=49262. Fresh copies avoid an existing .sav affecting the sequence. The saved Lua files are the actual run scripts; no custom rendering code was used.

## Compiler file and case inventory

The following enumerates all direct linker-import files and their current cases so a harness migration cannot silently omit a file. Decisions and target boundaries are the coherent groups above; negative/IR-only cases remain unchanged. Methods are inventory identifiers, not evidence of execution.

### tests/Compiler/Koh.Compiler.Tests/Backends/Sm83BackendTests.cs

`Emits_ExpectedBytes_ForConstantFolding`, `Residency_AssignsShortLivedByteValueToRegister`, `Residency_IsDisabledByDefault`, `Residency_ComputesCorrectResultThroughRegister`, `Residency_SkipsValueUsedByNonGentleOp`, `Residency_AssignsSixteenBitValueToHL`, `Residency_ComputesCorrectSixteenBitResultThroughHL`, `Residency_GivesSimultaneouslyLiveValuesDistinctRegisters`, `Residency_CoalescesChainIntoOneRegister`, `Residency_CoalescesSixteenBitChainInHL`, `Residency_UsesHalfOfHLUnderBytePressure`, `Residency_ReceivesLeafParametersInRegisters`, `Residency_ParameterPassingIsOffWithoutOptIn`, `Residency_RejectsParameterWhenEntryIsLoopHeader`, `Residency_LeafCallPassesArgsInRegisters`, `Allocation_ReusesSlotsForNonOverlappingValues`, `LineMap_MapsInstructionsToSourceLines`, `Runs_ConstantChain_InEmulator`, `Runs_RegisterPathSubtraction_InEmulator`, `Runs_BitwiseOps_InEmulator`.

### tests/Compiler/Koh.Compiler.Tests/Backends/Sm83CompareBranchFusionTests.cs

`FusedCompareBranch_TakesCorrectArm`, `TwoUseCompare_TrueArm_StillComputesBothUsesCorrectly`, `TwoUseCompare_FalseArm_StillComputesBothUsesCorrectly`, `FusedCompareBranch_IsInternalContextSet_ForImmediatelyConsumedCompareOnly`, `FusedCompare_EmitsNoStoreToItsOwnSlot`, `UnfusedCompare_DoesEmitAStoreToItsOwnSlot_ProvingTheScanDetectsIt`, `FusedCompareBranch_EmitsExpectedByteCount_WithNoWramRoundTrip`.

### tests/Compiler/Koh.Compiler.Tests/Backends/Sm83ControlFlowTests.cs

`I16_Add_CarriesAcrossBytes`, `I16_Sub_BorrowsAcrossBytes`, `I16_Ult_True`, `I16_Ugt_True_ViaSwap`, `I16_Eq_False`, `Mul_I8`, `Mul_I16`, `Mul_Signed_I8_LowByteMatches`, `UDiv_I16`, `URem_I16`, `UDiv_I8`, `SDiv_I16_NegativeDividend`, `SRem_I16_SignOfDividend`, `SDiv_I16_BothNegative`, `Shl_Const_I8`, `LShr_Const_I8`, `AShr_Const_I8_SignFills`, `Shl_Const_I16`, `LShr_Const_I16`, `AShr_Const_I16_SignFills`, `Shl_Variable`, `Slt_Signed_NegativeLessThanPositive`, `Sgt_Signed_NegativeOrdering`, `SignedVsUnsigned_DifferForNegatives`, `Slt_Signed_I16`, `Slt_Signed_CloseValuesWithLowByteBorrow`, `I32_Add_CarriesAcrossFourBytes`, `I32_Sub_BorrowsAcrossFourBytes`, `I32_Bitwise_And`, `I32_SignedCompare_AcrossBoundary`, `I32_ZeroExtendFromByte`, `ZExt_I8_To_I16`, `SExt_I8_To_I16_SignExtendsHighByte`, `Trunc_I16_To_I8_KeepsLowByte`, `Alloca_Store_Load_RoundTrips`, `Gep_IndexesArrayElements`, `Call_PassesArgs_ReturnsI8`, `Call_ReturnsI16_InHL`, `Call_Nested_ChainOfFrames`, `Recursion_IsSupported`, `Switch_SelectsCaseOrDefault`, `Header_IsBootableCartridge`, `Boots_FromEntryVector`, `Mmio_StoreReachesHardwareRegister`, `Global_RomTable_IndexedRead`, `Global_RomI16Constant`, `Global_WramState_MutatedAcrossCalls`, `Gep_DynamicIndex_I8Array`, `Gep_DynamicIndex_I16Array_ScalesBySize`, `Pointer_Parameter_Dereferences`, `Store_ThroughDynamicPointer_RoundTrips`, `PhiSwap_RealizedInParallel`, `SumLoop_RunsToCompletion`, `PhiCopyCycle_MixedWidth_DoesNotCorruptWiderValue`.

### tests/Compiler/Koh.Compiler.Tests/Backends/Sm83GepFusionTests.cs

`SingleUseGep_ImmediatelyStored_ComputesCorrectAddress`, `SingleUseGep_ImmediatelyLoaded_ComputesCorrectAddress`, `TwoUseGep_StillComputesBothUsesCorrectly`, `DeferredSingleUseGep_AcrossInterveningInstructions_StillComputesCorrectly`, `FusedGep_IsInternalContextSet_ForImmediatelyConsumedGepOnly`.

### tests/Compiler/Koh.Compiler.Tests/Backends/Sm83LoopPointerPhiResidencyTests.cs

`MultiEntry_BothArmsGetTheirOwnInitSync`, `MultiEntry_EachArmWalksItsOwnBuffer_RunsCorrectlyOnHardware`, `LoadWalk_PhiGetsRegisterPair_AndDerefIsFused`, `StoreWalk_PhiGetsRegisterPair_PrefersHl`, `LoadWalk_RunsCorrectlyOnHardware`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilBgWinTests.cs

`BgFillAndDrawMap_WriteExpectedMapBytes`, `WinFillAndDrawMap_WriteExpectedMapBytes`, `SetAttrFillAttr_OnCgb_WriteVramBank1NotTileIndexMap`, `SetAttrFillAttr_OnDmg_IsTrueNoOp`, `BatchedFullBoardRedraw_WithLcdOn_ProducesZeroMode3Violations`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilCanvasTests.cs

`Init_Clear_SetPixel_Present_LcdOff_MatchesExpectedBuffer`, `FillRect_SolidShade_FillsEveryPlaneByteFF`, `FillSpan_DitherShade_ProducesExpectedPlaneBytesPerRow`, `DrawLine_LcdOn_NeverWritesDuringMode3_AndDrawsExpectedDiagonal`, `FillTriangle_LcdOn_NeverWritesDuringMode3_AndFillsExpectedSpans`, `Present_DoubleBuffered_LcdOn_FlipsLcdcBit4_NeverWritesDuringMode3`, `Present_Cgb_UsesGdma_NeverWritesDuringMode3_AndMatchesExpectedBuffer`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilClassStringFieldTests.cs

`StringCtorParameter_StoredInClassField_ReadBackViaLengthAndIndexer`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilDelegateTests.cs

`NoCaptureLambda_VerifiesCleanAndInvokesResolvedTarget`, `NoCaptureLambda_DebugAndReleaseProduceIdenticalObservableState`, `CapturingLambda_VerifiesCleanAndInvokesResolvedTarget`, `CapturingLambda_DebugAndReleaseProduceIdenticalObservableState`, `DelegateThroughParameter_LowersViaBlobDispatch`, `CallvirtOnSealedDeclaringType_DevirtualizesToDirectCall`, `VirtualMethodGroupDelegate_IsDiagnosedNotSilentlyMisresolved`, `VirtualMethodGroupDelegate_ResolvesToSealedOverrideWhenTokenIsSealed`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilEndToEndTests.cs

`RegisterPokeLoop_VerifiesCleanAndProducesExpectedState`, `RegisterPokeLoop_DebugAndReleaseProduceIdenticalObservableState`, `RegionPointerAndControlIntrinsic_LoweredFromAttributeAlone`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilFloatTests.cs

`F32_Arithmetic_MatchesHost`, `F32_Add_RoutesThroughRealSoftFloatFunction`, `F32_Compare_MatchesHost`, `F32_CompareInBranchPosition_MatchesHost`, `F32_TernaryMin_MatchesHost`, `F32_Neg_MatchesHost`, `F32_Neg_RoutesThroughRealSoftFloatFunction`, `F32_ToInt_MatchesHost`, `F32_FromInt_MatchesHost`, `F32_ToF64_MatchesHost`, `F32_ToF64_RoutesThroughRealSoftFloatFunction`, `F64_ToF32_MatchesHost`, `F64_ToF32_RoutesThroughRealSoftFloatFunction`, `F64_ToInt_MatchesHost`, `F64_FromInt_MatchesHost`, `F64_FromUInt_MatchesHost`, `F64_Arithmetic_MatchesHost`, `F64_Compare_MatchesHost`, `F32_RefParameter_ReadsAndWritesThroughIndirection`, `F32_Rem_NoMatchingRuntimeKey_IsADiagnosticNotAMiscompile`, `F32_Arithmetic_DebugAndReleaseProduceIdenticalObservableState`, `F64_Arithmetic_DebugAndReleaseProduceIdenticalObservableState`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilFontTextTests.cs

`Draw_WritesExpectedMapCellsAndExpandsGlyphPixels`, `DrawToWindow_WritesGlyphIndicesToWindowMap`, `DrawNumber_RightAligned_SpacePadsToWidth`, `DrawNumber_LeftAligned_NoPadding_HandlesZero`, `Draw_ByteArrayOverload_WritesExpectedGlyphIndices`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilFrameworkTests.cs

`Rng_MatchesDesktopSequenceAndDerivedDraws`, `Timer_CountsDownFiresOnceAndRestarts`, `Assets_StaticStructHandles_LoadVramAndDrawMap`, `Input_LatchedEdgesAndRepeatCadence`, `GameBootAndEndFrame_TickClockAndLatchInput`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilGenericTests.cs

`GenericMethod_TwoTypesTransitively_ProducesExpectedValue`, `GenericMethod_DebugAndReleaseProduceIdenticalObservableState`, `LongArithmetic_AddSubMulDivRem_MatchesHostComputedValue`, `ConvI8SignExtends_ConvU8ZeroExtends_MatchesHostComputedValue`, `UlongDivision_IsUnsigned_NotArithmeticShift`, `WideIntArithmetic_DebugAndReleaseProduceIdenticalObservableState`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilGraphicsSlice2Tests.cs

`KohAligned_RoundsStaticWramGlobalCursorUpToAlignment`, `RunOamDma_TriggersRealDmaAndReturnsWithBusUnlocked`, `RunOamDma_DebugAndReleaseProduceIdenticalObservableOam`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilInterruptTests.cs

`Interrupt_SetsVectorOnTheLoweredFunction`, `Interrupt_ActuallyFires_OnGameBoySystem`, `Interrupt_DebugAndReleaseProduceIdenticalObservableState`, `UnknownInterruptKind_ReportedAsDiagnostic`, `RecursiveInterruptHandler_IsRejected`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilIteratorTests.cs

`ForeachOverIterator_VerifiesCleanAndComputesExpectedValue`, `ForeachOverIterator_DebugAndReleaseProduceIdenticalObservableState`, `TwoIndependentForeachLoops_VerifiesCleanAndComputesExpectedValue`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilKohRuntimeTests.cs

`SoftFloatAddF32_LowersCleanlyAsOrdinaryReferencedCode`, `SoftFloatAddF32_ComputesTheCorrectIeeeBits`, `SoftFloatAddF32_DebugAndReleaseProduceIdenticalObservableState`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilLinqTests.cs

`WhereSelectSum_VerifiesCleanAndComputesExpectedValue`, `WhereSelectSum_DebugAndReleaseProduceIdenticalObservableState`, `MaxDirectlyOnArray_VerifiesCleanAndComputesExpectedValue`, `MaxDirectlyOnArray_DebugAndReleaseProduceIdenticalObservableState`, `MaxAfterPipeline_IsDiagnosedNotThrown`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilLiteralTests.cs

`LocalArrayLiteral_LandsInRomWithInitialBytesNotRebuiltAtStartup`, `LocalArrayLiteral_DebugAndReleaseProduceIdenticalObservableState`, `U8Literal_LandsInRomWithInitialBytesNotRebuiltAtStartup`, `U8Literal_DebugAndReleaseProduceIdenticalObservableState`, `Int128Arithmetic_RoundTripsThroughRealArithmeticOnTheHost`, `Int128Arithmetic_DebugAndReleaseProduceIdenticalObservableState`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilLoweringTests.cs

`RegisterLoop_WritesExpectedValue_DebugAndRelease`, `RegisterLoop_IrShape_HasEntryFunctionAndFixedAddressGlobal`, `RegisterReadThenWrite_RoundTripsThroughFixedAddressGlobal`, `CallToGameModuleStaticMethod_IsLoweredAsDirectCall`, `Ternary_NonEmptyStackJoin_IsSpilledCorrectly`, `Ternary_Release_ActuallyExercisesSpillAllocas`, `ArithmeticAndCompareOpcodes_ProduceExpectedValue`, `UnsignedAndInequalityLoops_ProduceExpectedValue`, `NarrowingConversionOpcodes_ProduceExpectedValue`, `UnsupportedExternalCall_ReportsDiagnostic_DoesNotThrow`, `ArrayLength_LoopBoundSumsAllElements_DebugAndRelease`, `ArrayLength_OnUntraceableArray_ReadsRuntimeLengthPrefix`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilMapWriterGdmaTests.cs

`Cgb_ScatteredRun_LandsCompletelyAfterOneEndFrame_ViaGdma`, `Dmg_ScatteredRun_StillDripsAcrossSeveralFrames_DripPathUnchanged`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilMdArrayTests.cs

`RectangularArrays_RomFoldHeapCtorAndHeaderPath`, `StringArrays_ReferenceElementsAndLengths`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilNarrowingEndToEndTests.cs

`ByteCompare_UnsignedOrderingIsCorrectThroughNarrowing`, `ByteCompare_DebugAndReleaseAgree`, `ByteCompare_NarrowPassActuallyFiredOnTheCompare`, `ByteAccumulateLoop_ProducesTheCorrectSum`, `ByteAccumulateLoop_DebugAndReleaseAgree`, `ByteAccumulateLoop_NarrowingReducesRomSizeAndCycles_MeasuredDelta`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilPalettesRgbTests.cs

`SetBgSetObj_OnDmg_WriteDmgShadesToBgpAndObp0`, `SetObj_OnDmg_Slot1_WritesObp1`, `SetBgSetObj_OnCgb_WriteRgb555ColorsToPaletteRam`, `SetBgSetObj_OnDmg_SlotsAboveHardwareRange_AreNoOps`, `RgbMake_PacksChannelsIntoRgb555`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilRecursionTests.cs

`DirectRecursion_Fibonacci_ComputesExpectedValue`, `DirectRecursion_Fibonacci_DebugAndReleaseProduceIdenticalObservableState`, `MutualRecursion_IsEvenIsOdd_ComputesExpectedValues`, `MutualRecursion_DebugAndReleaseProduceIdenticalObservableState`, `DeepRecursion_ExercisesRelocatedWramCallStack`, `DeepRecursion_DebugAndReleaseProduceIdenticalObservableState`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilReferenceTests.cs

`HalAndMemMethods_LowerOnDemandAndProduceExpectedState`, `HalAndMemMethods_DebugAndReleaseProduceIdenticalObservableState`, `SingleSetTile_LowersOnDemandAndWritesTheRightCell`, `TilemapClear_TransitivelyLowersSetTileAndClearsEveryCell`, `TilemapClear_DebugAndReleaseProduceIdenticalObservableState`, `UncalledHalMethods_AreNeverLoweredIntoTheModule`, `CallIntoBcl_IsADiagnosticNeverLoweredOrMiscompiled`, `MemAlloc_IsACompilerOwnedIntrinsic_AndAllocatesAtTheExpectedAddress`, `MemAlloc_DebugAndReleaseProduceIdenticalObservableState`, `Arena_AllocatesDistinctBlocks`, `Arena_ResetReclaimsEverything`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritePaletteHardwareTests.cs

`RegisterRoundTrip_NewSpritePaletteRegistersReachRealAddresses`, `OamDmaTrigger_CopiesSourcePageIntoRealOam`, `OamDmaTrigger_DebugAndReleaseProduceIdenticalObservableState`, `DesktopHost_HardwareDmaSetterCopiesWramPageIntoGbOam`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritesCgbTests.cs

`CgbPaletteSprite_FlushesRealOam_AndWritesRealObjPaletteRam`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilSpritesTests.cs

`EndFrame_FlushesShadowToOam_MatchingSetMoveHideAndClipping`, `HideAll_ForcesEveryShadowSlotYToZero`, `Move_ChangesPositionOnly_TileAndAttrSurvive`, `EndFrame_WithNoSpriteMutation_LeavesOamAllZero`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilStaticsTests.cs

`MutableStaticCounter_IncrementedAcrossCallsAndReadBack`, `MutableStaticCounter_DebugAndReleaseProduceIdenticalObservableState`, `StaticReadonlyByteArray_LandsInRomWithInitialBytes`, `StaticReadonlyByteArray_DebugAndReleaseProduceIdenticalObservableState`, `ConstField_FoldedByRoslynWithNoBackingStorage`, `ConstField_DebugAndReleaseProduceIdenticalObservableState`, `MutableStaticArray_GetsDedicatedWramGlobalNotHeap`, `MutableStaticArray_DebugAndReleaseProduceIdenticalObservableState`, `NoCaptureLambda_DoesNotResurrectCompilerGeneratedCctor`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilStoredDelegateTests.cs

`Press`, `ActionThroughCtorField_TwoTargetsOneField_DispatchPerBlob`, `FuncThroughParameter_ReturnValuesMergeThroughSlot`, `StaticDelegateField_ReassignmentSwitchesTarget`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilStringLiteralTests.cs

`StringLiteral_IndexLoop_WritesExactAsciiBytesToWram`, `StringLiteral_DebugAndReleaseProduceIdenticalObservableBytes`, `StringLiteral_ForeachLoop_WritesExactAsciiBytes`, `StringLiteral_NonAsciiCharacter_ReportsDiagnostic_DoesNotThrow`, `StringLength_OnParameterAcrossCallBoundary_Compiles_ReturnsRealLength`, `TextDrawShapedLibraryCall_StringParameterAcrossCallBoundary_CompilesAndRuns`, `WriteAsciiHelper_StringParameter_LandsAsciiBytesAndRomBlob`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructReturnTests.cs

`Set`, `StructReturn_BuildConsumeReverseAndChain`, `StructReturn_FactoriesInstanceMethodsLoopsAndStatics`, `StructReturn_ThroughRecursion`, `StructReturn_FromMonomorphizedGenericMethod`, `StructReturn_UnlocksPatternBasedForeach`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructTests.cs

`StructFieldRoundTrip_WholeCopyDoesNotAlias`, `NestedStructsAndArraysOfStructs_FieldsRoundTrip`, `PointerWriteToVram_ReadBack`, `PointerArithmetic_OffsetWriteReadBack`, `StackAllocBuffer_RoundTrips`, `RefParameter_MutatedByCallee`, `RefStructParameter_MutatesCallersStorage`, `ByValStructParameter_CalleeMutationNotVisibleToCaller`, `SwitchDispatch_JumpTable_SelectsCorrectCase`, `SwitchDispatch_LowersToRealSwitchInstruction`, `SwitchDispatch_OutOfRange_FallsThroughToDefault`, `Enum_UnderlyingIntegerRoundTrips`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilTileSetTests.cs

`Load_LcdOff_CopiesTileBytesVerbatimIntoVram`, `Load_SubRange_CopiesFromStartTileNotFromDataStart`, `Load1bpp_ExpandsSetBitsToInkClearBitsToPaper`, `Load1bpp_MixedInkPaper_ProducesCorrectPlaneMasks`, `Load_LcdOn_ChunksAcrossVblanksAndNeverWritesDuringMode3`, `SetRowAndClear_PassThroughToTileData`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilVideoJoypadTests.cs

`LcdcComposesAcrossShowCallsAndStart`, `Init_ClearsBothTileMapsAndOamAndSetsDefaultDmgPalette`, `EndFrame_WaitsForVblankAndAdvancesFrameCount`, `Pressed_ReportsRisingEdgeOnceForAHeldButton`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilVirtualDispatchTests.cs

`InheritedFields_UsePrefixLayout_NoOverlap`, `UntraceableInterfaceCall_IsDiagnostic`, `VirtualCalls_TagDispatchThroughBaseTypedStatic`, `SceneMachine_GameRun_DeferredChangeWithCtorState`, `TagDispatch_CoexistsWithInterruptHandlerRoots`.

### tests/Compiler/Koh.Compiler.Tests/Frontends/CilWideArithmeticTests.cs

`WideMultiply_MatchesCSharpInt32Wraparound`, `WideMultiply_DebugAndReleaseProduceIdenticalResults`, `WideDivide_MatchesCSharpInt32Semantics`, `WideDivide_DebugAndReleaseProduceIdenticalResults`, `ConstPow2Divide_MatchesCSharpInt32Semantics`, `ConstPow2Divide_DebugAndReleaseProduceIdenticalResults`, `ConstShift_MatchesCSharpInt32Semantics`, `ConstShift_DebugAndReleaseProduceIdenticalResults`, `WideMultiply64_MatchesCSharpInt64Wraparound`, `WideDivide64_MatchesCSharpInt64Semantics`, `WideMultiply64_DebugAndReleaseProduceIdenticalResults`, `WideDivide64_DebugAndReleaseProduceIdenticalResults`, `ConstShift64_MatchesCSharpInt64Semantics`, `ConstShift64_DebugAndReleaseProduceIdenticalResults`.

### tests/Compiler/Koh.Compiler.Tests/Samples/CgbHalTests.cs

`CgbHal_CompilesThroughTheRealBackend`, `TryEnableDoubleSpeed_FlipsKey1Bit7_InCgbMode`, `TryEnableDoubleSpeed_DoesNothingHarmful_InDmgMode`, `SelectVramBank_SelectsBank1_InCgbMode`, `SelectVramBank_IsInertOnDmg`, `SetBackgroundColor_WritesPaletteRam_InCgbMode`, `SetBackgroundColor_WritesTheRequestedPaletteAndColorSlot`.

### tests/Compiler/Koh.Compiler.Tests/Samples/CilGame2048Tests.cs

`Sample_CompilesWithoutDiagnostics`, `Sample_LinksToBootableRom`, `Sample_RomBootsIntoMainAndInitializes`, `Sample_RendersScoreLabelAndNumberToTheBackgroundMap`, `Sample_CursorSpriteIsVisibleAndFlushedToOam`, `SlideLine_MergesEqualPair`, `SlideLine_CompactsAcrossGap`, `MoveRight_MergesRowIntoRightEdge`, `MoveDown_MergesColumnIntoBottomEdge`, `Spawn_FillsExactlyOneEmptyCell`, `Spawn_ReturnsFalseWhenBoardFull`, `CanMove_FalseOnGridlock_TrueWithAnOpening`, `HasWon_DetectsThe2048Tile`, `Render_PaintsBottomRowCellsToTheCorrectTilemapAddress`, `Optimized_SampleRomBootsIntoMainAndInitializes`, `Optimized_SlideLogicMatchesUnoptimized`.

### tests/Compiler/Koh.Compiler.Tests/Samples/Cube3dTests.cs

`Demo_CompilesWithoutDiagnostics`, `Demo_LinksToBootableRom`, `Demo_RunOnHardwareRendersARealCube`.

### tests/Compiler/Koh.Compiler.Tests/Samples/Gb2048KdbgTests.cs

`Kdbg_ResolvesSymbolAndSourceLocation_ForCilFrontendBuild`, `Frontend_DoesNotThrow_WhenAssemblyHasNoPdb`.

### tests/Compiler/Koh.Compiler.Tests/Samples/Gb2048V2Tests.cs

`Sample_CompilesUnmodified_NoDiagnostics_ValidIr`, `Sample_BootsToTitle_StartsGame_SlidesSpawnTiles`.

### tests/Compiler/Koh.Compiler.Tests/Samples/GbGfxDemoTests.cs

`Demo_CompilesWithoutDiagnostics`, `Demo_LinksToBootableRom`, `WindowMap_SpellsScoreViaFontBasePlusOffset`, `Lcdc_HasBgObjAndWindowEnabled`, `Oam_AfterFirstPaintedIteration_MatchesExpectedYXTileAttr`, `Oam_AfterMoreIterations_PositionsChangedAsExpected`, `Palette_TogglesAcrossASixtyIterationBoundary`, `Dmg_Run_HasQuantizedShadesInBgpAndObp`, `Cgb_Run_HasRgb555InPaletteRam`.

### tests/Compiler/Koh.Compiler.Tests/Samples/GbJrpgTests.cs

`Sample_CompilesUnmodified_NoDiagnostics_ValidIr`, `Sample_CgbPlaythrough_WalksTalksAndCallsBack`, `Sample_CaptureAcceptanceScreenshots`, `Sample_RomHeader_IsCgbExclusive`.

### tests/Compiler/Koh.Compiler.Tests/Sm83/FlushRunLoopResidencyTests.cs

`FlushRun_DstAndSrcPointerPhisAreRegisterResident`, `FlushRun_CompiledLoopBodyUsesFusedAddressingNotPerIterationReload`.

### tests/Compiler/Koh.Compiler.Tests/Sm83/MemRuntimeTests.cs

`Frontend_ProducesVerifiableIr`, `Copy_CountZero_IsNoOp`, `Copy_SingleByte`, `Copy_OddSize`, `Copy_CrossesA256ByteBoundary`, `Fill_VariousCounts`, `Fill_CrossesA256ByteBoundary`, `Copy_OverlapDestinationBeforeSource_IsDefined`, `Copy_CostPerByte_IsWithinLooseCeiling`, `Copy_MarginalCostPerByte_IsWithinLooseCeiling`.

### tests/Compiler/Koh.Compiler.Tests/Sm83/RedundantCompareLoopTests.cs

`RealFlushRun_LoopGuards_HaveNoRedundantCompareChain`, `GuardedStride1CopyLoop_CopiesEveryByte_WhenGuardOpen`, `GuardedStride1CopyLoop_CopiesNothing_WhenGuardClosed`, `TwoSequentialStride1CopyLoops_BothCopyCorrectly`.

### tests/Compiler/Koh.Compiler.Tests/Sm83/Sm83LoopCodegenTests.cs

`Baseline_CountedConstAddrStoreLoop`, `CountedConstAddrStoreLoop_MeetsCeiling`, `InductionValueUsedAfterLoopExit`, `InductionValueUsedAfterLoopExit_WithInterveningClobber_StillCorrect`, `NestedLoops`, `EarlyBreakExitEdge`, `InductionValueSavedBeforeIncrement_UsedAfterIt`, `InductionValueSavedBeforeIncrement_ReadAfterLoopExit`, `PointerCopyLoop_RegressionSafe`, `TwoPointerCopyLoop_MeetsCeiling`, `SinglePointerFillLoop`, `PointerUsedAfterLoopExit`, `NonQualifyingMiddleInstruction_NegativeAdmission_StillCorrect`, `OverlappingRegionCopy_ForwardSemanticsPreserved`, `TwoSequentialI8CountedLoops_DistinctCounters`, `TwoI8CountedLoopsInIfElseBranches`, `TwoSequentialFillLoops_DistinctBuffers`, `TwoLoopsInIfElseBranches`, `TwoSequentialCopyLoops_DistinctBuffers`, `CopyLoopFollowedByFillLoop`.

### tests/Compiler/Koh.Compiler.Tests/Sm83/Sm83SymbolAddressTests.cs

`FunctionLabel_SymbolAtActualCodeAddress_NotDoubleCountingCodeBase`, `EntryFunctionLabel_SymbolMatchesHeaderEntryPoint`, `WramGlobalLabel_SymbolAtWramBase`.

