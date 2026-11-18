# Backend, IR and SM83 migration audit

This is a source-review migration ledger, not runtime acceptance. Reviewed the 25 test
files under `tests/Compiler/Koh.Compiler.Tests/{Backends,Ir,Sm83}` and the corresponding
C# IR, optimization, allocation, emission, MIR decoding and peephole implementation.
There are **290 [Test] methods**: Backends 156, Ir 94, Sm83 40. Parameterized
`[Arguments]` expand methods into more executions; 290 is not an execution count.
No builds or tests were run for this audit. Existing test names containing
“InEmulator” or “OnHardware” describe their source harness, not fresh proof:
the pointer-residency “OnHardware” tests instantiate `Koh.Emulator`.

PORT means retain the externally meaningful assertion and input in native acceptance.
REPLACE means retain the justified behavior/boundary while replacing private C# IR,
allocation, byte-shape, helper or harness assertions. DELETE would mean removing a
test with no independent requirement left; the private numeric text-parser case
below now has that disposition. No compiler-owned
case is deferred to another migration. Unless explicitly marked complete, paths
below are **proposed future Rust test locations**, not implemented coverage. A PORT disposition does not require
the C# ABI to be copied.

## Independent requirements and ABI decisions

### Storage and calling convention

SM83 machine CALL/RET requires a balanced hardware stack; it does not require one
static WRAM frame per function. C# `CompileCore` puts globals first, then disjoint
function allocations; `FunctionAllocation` reuses slots by liveness and optionally
places selected values/leaf parameters in registers. The independent requirement is
that simultaneously live values and distinct live objects remain distinguishable.
Pointers to address-taken objects must continue to identify those objects through
calls and for their valid lifetime. A Rust design may use activation stack frames,
static slots with a proven non-reentrant call graph, or a combination; it must not
inherit static storage simply because the old tests name a slot.

The C# direct ABI uses packed callee parameter slots with optional resident leaf
parameters; returns are A (byte), HL (word), DE:HL (32-bit), and fixed ReturnScratch
for 64/128-bit values. Recursive and banked functions use memory returns at every
width. These are implementation conventions, not CIL requirements. Native callers,
runtime helpers, return captures, far-call thunks and any assembly interface must
agree on widths, byte order, clobbers and parameter placement. Test nested calls
with caller live values, mixed-width parameters/results, pointer parameters and
register pressure. An ABI change must update every participating caller/helper,
not just a return-value assertion. External calls currently throw rather than
silently accepting an unspecified ABI (`ControlFlowEmitter.EmitCall`).

### Recursion and activation identity

C# identifies recursive SCCs, stages arguments at ArgScratch, saves each recursive
function's static frame to an upward software stack, restores on return and uses
ReturnScratch. Recursive frames larger than 255 bytes are diagnosed. Correctness
requires terminating direct/mutual recursion, preservation of caller locals and
arguments, zero-sized frames, maximum-depth behavior, and a defined resource-limit
failure. It also requires activation-specific identity for escaped locals: saving
the bytes of a reused static frame alone does not give a pointer into an ancestor
a different address from the current activation. Add a caller-local-address passed
through recursive calls test; the ordinary pointer-parameter test does not establish
this case.

Concrete source risk: `EmitPushFrame` copies the complete frame and updates SoftSp
**before** checking the live heap/ceiling/hardware-SP collision. Its trap can therefore
follow corruption of heap data or return addresses. The Rust acceptance must require
capacity checks before writes, not merely eventual trapping. The existing
`Recursion_IsSupported` checks valid IR and nonempty emitted sections, with no
recursive execution. Replace it with terminating emulator fixtures and overflow
sentinels. This finding is source-derived, not a reproduced runtime failure.

### Aliasing and optimization legality

Language pointer reads/writes, fixed-address IO and overlapping forward copies are
observable even if a pass can make IR shorter. C# `AllocaAnalysis.NonEscaping`
permits only direct load/store-pointer uses; storing the address, GEP, passing or
returning it marks escape. RLE, DSE and Mem2Reg rely on that proof. Keep conservative
unknown-pointer/call barriers, preserve separate allocations, and do not infer
“no alias” from two SSA names. The old forward-overlap loop fixture deliberately
expects bytes produced by earlier iterations to be read by later iterations;
substituting memmove changes its semantics. The Mem.Copy destination-before-source
overlap test does not by itself promise all memmove overlap directions.

MMIO reads can change without a store; writes can trigger hardware. C# retains
unused loads in DCE and does not LICM-hoist loads. Backend dead-store/reload
elimination is constrained to WRAM and disabled with interrupt handlers. These
are conservative strategies. The replacement must explicitly preserve IO and
asynchronous observations, not transplant only the positive optimization cases.

### Interrupts and shared scratch

An interrupt may arrive between ordinary emitted instructions. Handlers must save
interrupted registers/flags they clobber, balance their stack and return via RETI;
their reachable helpers and scratch must be reentrant or rejected before emission.
C# pushes AF/BC/DE/HL, disables residency for handlers, rejects shared
handler/mainline functions, rejects recursive handlers and detects shared wide
arithmetic/memory-return scratch. Static frames in two functions do not make a
shared runtime scratch area safe.

Review gap: `UsesWideScratch` checks widths greater than two bytes and memory
returns, but `EmitSDivMod16` also uses fixed RtCount/RtSignRem/RtSignQuot.
Therefore the wide-only check is not proof that concurrent independent 16-bit
divide helpers are safe. Need an interrupt-preemption test across narrow runtime
division as well as wide arithmetic, return-copy, recursive argument staging and
bank transitions. Also define or reject EI inside a handler: the safety argument
“handlers never nest” is conditional on user code not enabling nested interrupts.
These are migration acceptance obligations, not implemented fixes.

### Banking and placement

A 16-bit window address does not uniquely identify a physical bank. Calls and data
accesses need the intended bank, with the caller mapping restored where required.
C# single-overflow-bank mode keeps bank 1 mapped; multibank code uses ROM0 thunks
and CurBank; simultaneous banked code and banked read-only data is rejected in
the ordinary overflow path. Data selection is author-written; the IR
bank-select elimination tracks constant MBC1 selects within one block and clears
knowledge for calls/intrinsics, variable addresses and other MBC-control writes.
Its documented interrupt assumption is that a handler restores the mapping.
Do not generalize an MBC1 constant-store rule to an arbitrary mapper.

Native acceptance needs cross-bank nested/recursive calls, all return widths,
data-bank selection with aliases/calls, handler bank preservation, and clear
unsupported-layout diagnostics. ROM0 vectors/trampolines and executing code must
remain fetchable while mapping changes. Symbol values must remain section-relative
in objects and resolve to actual linked addresses; fixed CodeBase/DataBase/WramBase
are not mandatory when placement is otherwise valid. Those obligations belong to
the compiler migration even though these 25 files do not comprehensively cover them.

## Test-by-test disposition

For each file, the independent justification applies to all enumerated methods;
each row refines whether its existing assertion is a requirement, private detail,
or a mixture. Future locations are under `rust/koh-compiler/tests/`.
The method name identifies the exact source fixture, including every parameter row.

### Backends/MirDecoderTests.cs (19)

Classification: ISA metadata, not the old MIR representation. Hardware instructions define register/flag/memory/control effects; optimizer legality depends on preserving them. Byte round trips alone cannot certify effect metadata.

All19 methods are complete: four [boundary/input replacements](instruction-boundaries.md) cover15 source assertion sites; fifteen [executable ISA-effect replacements](machine-effects.md) cover66 more. Nine native rstest cases run13952 fresh instruction probes with independent Rust expectations; byte decoding alone is not treated as proof of effects.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Decode_RoundTripsToOriginalBytes` | Mixed | REPLACE complete | [Independent instruction/input boundaries and retired private MIR predicates](instruction-boundaries.md#source-dispositions). |
| `Decode_XorAWritesAAndAllFlags` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_LoadRegRegHasNoFlagEffect` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_LoadImmediateWritesRegisterOnly` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_AdcFromMemoryReadsCarryAAndHl` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_IncByteWritesZnhButNotCarry` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_CbBitTestsWithoutWritingRegister` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_ControlFlowIsClassified` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_PushPopAfTouchAllFlags` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_MmioLoadStoreLengthsAndEffects` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_IllegalOpcodeIsOpaqueBarrier` | Mixed | REPLACE complete | [Independent instruction/input boundaries and retired private MIR predicates](instruction-boundaries.md#source-dispositions). |
| `Decode_IsTotalOverEveryOpcode` | Mixed | REPLACE complete | [Independent instruction/input boundaries and retired private MIR predicates](instruction-boundaries.md#source-dispositions). |
| `Decode_HlPostIncrementLoadWritesHl` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_EveryLoadRegRegLeavesFlagsUntouched` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_EveryAluOpWritesAllFlagsAndReadsA` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_EveryCbFamilyHasTheExpectedFlagShape` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_EveryIncDecByteWritesZnhNotCarry` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |
| `Decode_TruncatedTrailingCbDoesNotThrow` | Mixed | REPLACE complete | [Independent instruction/input boundaries and retired private MIR predicates](instruction-boundaries.md#source-dispositions). |
| `Decode_DiEiAreSideEffectingButNopIsNot` | Requirement | REPLACE complete | [Observed register/flags/control/memory effects against independent Rust ISA expectations](machine-effects.md#source-disposition). |

### Backends/Sm83BackendTests.cs (20)

Classification: Mixed language behavior, debug contract, and allocator details. Return values and source mappings are observable; E/HL choices, default toggles and exact slots are private allocation decisions.

All20 methods are complete in [allocation-independent acceptance](allocation-contracts.md):
41 assertion sites reviewed; private register/slot defaults replaced by native values,
liveness, caller/callee ABI and real-PDB source mapping.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Emits_ExpectedBytes_ForConstantFolding` | Detail | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_AssignsShortLivedByteValueToRegister` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_IsDisabledByDefault` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_ComputesCorrectResultThroughRegister` | Requirement | PORT complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_SkipsValueUsedByNonGentleOp` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_AssignsSixteenBitValueToHL` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_ComputesCorrectSixteenBitResultThroughHL` | Requirement | PORT complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_GivesSimultaneouslyLiveValuesDistinctRegisters` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_CoalescesChainIntoOneRegister` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_CoalescesSixteenBitChainInHL` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_UsesHalfOfHLUnderBytePressure` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_ReceivesLeafParametersInRegisters` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_ParameterPassingIsOffWithoutOptIn` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_RejectsParameterWhenEntryIsLoopHeader` | Mixed | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Residency_LeafCallPassesArgsInRegisters` | Requirement | PORT complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Allocation_ReusesSlotsForNonOverlappingValues` | Detail | REPLACE complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `LineMap_MapsInstructionsToSourceLines` | Requirement | PORT complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Runs_ConstantChain_InEmulator` | Requirement | PORT complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Runs_RegisterPathSubtraction_InEmulator` | Requirement | PORT complete | [Native requirement and removed private assertions](allocation-contracts.md). |
| `Runs_BitwiseOps_InEmulator` | Requirement | PORT complete | [Native requirement and removed private assertions](allocation-contracts.md). |

### Backends/Sm83CompareBranchFusionTests.cs (7)

All seven methods are complete:3 PORT/4 REPLACE, nine assertion sites and26
expanded assertions across24 original executions. The18 original predicate rows
and both word-sized42/42 and42/43 multi-use cases are retained. Public contracts
are predicate truth, normalized0/1 values, branch selection and preservation of
all consumers. Internal eligibility sets, fixed WRAM slots and exact byte layouts
belong to the retired backend.

Implemented native coverage: `tests/support/compare_branch.rs` builds standard CIL;
`tests/sm83.rs::comparison_branches_preserve_predicates_and_all_consumers` has four
rstest width cases (8/16/32/64), all discoverable by cargo-nextest. Small-integer
conversions use ordinary CIL I32 stack values, not legacy I8/I16 SSA types.

| Existing method | Decision | Native acceptance / retired assertion |
| --- | --- | --- |
| FusedCompareBranch_TakesCorrectArm | PORT, complete | All18 original unsigned/signed/equality vectors return111/222 before and after optimization. Adds64-bit high-only differences and both directions. |
| TwoUseCompare_TrueArm_StillComputesBothUsesCorrectly | PORT, complete | Exact42==42 case returns cmp+10=11; every predicate/width also exercises this shared consumer. |
| TwoUseCompare_FalseArm_StillComputesBothUsesCorrectly | PORT, complete | Exact42!=43 case returns99; false-path `(cmp+10)+89` additionally observes the computed zero rather than returning an unconditional sentinel. |
| FusedCompareBranch_IsInternalContextSet_ForImmediatelyConsumedCompareOnly | REPLACE, complete | Mixed Eq/add and unsigned-less/branch consumers return1 for1<2 and0 for reverse/equal inputs. Retires two private set-membership checks. |
| FusedCompare_EmitsNoStoreToItsOwnSlot | REPLACE, complete | Single-use truth/arm checks plus bounded emitted footprint and whole-call cost replace a fixed-slot/opcode scan. |
| UnfusedCompare_DoesEmitAStoreToItsOwnSlot_ProvingTheScanDetectsIt | REPLACE, complete | Shared result and branch are both observed, including false-path control. The old scanner and its positive-control byte sequence have no production counterpart. |
| FusedCompareBranch_EmitsExpectedByteCount_WithNoWramRoundTrip | REPLACE, complete | Retires the static-frame ABI's exact opcode absence/count. Verifies actual results/stack/guards, generated fixture code/data<=2KiB and representative full checked calls within one DMG frame (70224 T-cycles). |

There is no requirement to implement the old fusion-context abstraction. Porting
it would copy a private architecture; the native backend may materialize typed
booleans. The six private assertion sites are explicitly replaced by executable
contracts. No public numeric expectation is removed and no compiler test is
deferred. A future optimization can use these same independent expectations.

The fixture checks single/shared predicates, two independent compares and a
non-eliminable MMIO store between compare and branch. Rust verifies that the
store survives optimization.37 predicate/input triples include all original
vectors and high-only64-bit distinctions.246 before/after semantic ROM executions
plus12 separate timing calls run through ELF and the existing emulator. Timing
includes native checked-frame/call setup, so it is not comparable to an isolated
legacy opcode count. Actual costs range4360..14128 T-cycles. An initial8192-cycle
cap was an unsupported estimate and was rejected after semantic cases passed;
the documented replacement ceiling is a complete hardware frame, not an alleged
legacy performance guarantee. No production optimization was changed to satisfy
this group.

Initial regression selection passes45/45 in78.238s,exit0
(`/tmp/koh-compare-branch-final.log`). Final strengthened high-bit/aggregate-size
cases pass4/4 in14.056s,exit0 (`/tmp/koh-compare-branch-consumers-final.log`),173 other
SM83 cases excluded. Workspace discovery lists1792 tests; all four new cases are unignored. Both runs are selected checks, not workspace completion.

### Backends/Sm83ControlFlowTests.cs (54)

Classification: Language, memory, call, and cartridge contracts. Arithmetic, pointer scaling, parallel phi assignments and calls have observable values independently of the C# emitter.

All54 methods now have native replacements: the first34 retain37 numeric
assertion sites in [scalar coverage](control-flow-scalars.md); the remaining20
account for30 assertion sites in [memory/call/boot/phi coverage](control-flow-memory.md).
See those per-method matrices for independent requirements, ABI replacements
and actual native execution evidence. Source definitions remain until final
legacy compiler/test deletion.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `I16_Add_CarriesAcrossBytes` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I16_Sub_BorrowsAcrossBytes` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I16_Ult_True` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I16_Ugt_True_ViaSwap` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I16_Eq_False` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Mul_I8` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Mul_I16` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Mul_Signed_I8_LowByteMatches` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `UDiv_I16` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `URem_I16` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `UDiv_I8` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `SDiv_I16_NegativeDividend` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `SRem_I16_SignOfDividend` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `SDiv_I16_BothNegative` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Shl_Const_I8` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `LShr_Const_I8` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `AShr_Const_I8_SignFills` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Shl_Const_I16` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `LShr_Const_I16` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `AShr_Const_I16_SignFills` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Shl_Variable` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Slt_Signed_NegativeLessThanPositive` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Sgt_Signed_NegativeOrdering` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `SignedVsUnsigned_DifferForNegatives` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Slt_Signed_I16` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Slt_Signed_CloseValuesWithLowByteBorrow` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I32_Add_CarriesAcrossFourBytes` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I32_Sub_BorrowsAcrossFourBytes` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I32_Bitwise_And` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I32_SignedCompare_AcrossBoundary` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `I32_ZeroExtendFromByte` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `ZExt_I8_To_I16` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `SExt_I8_To_I16_SignExtendsHighByte` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Trunc_I16_To_I8_KeepsLowByte` | Requirement | PORT complete | [Preserved vectors and native execution](control-flow-scalars.md); parameter/constant inputs before/after optimization. |
| `Alloca_Store_Load_RoundTrips` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): aliased byte43. |
| `Gep_IndexesArrayElements` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): distinct stack bytes10+20=30. |
| `Call_PassesArgs_ReturnsI8` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): three arguments return42. |
| `Call_ReturnsI16_InHL` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): full return550 through the new ABI. |
| `Call_Nested_ChainOfFrames` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): nested increments return42. |
| `Recursion_IsSupported` | Requirement | REPLACE complete | [Native memory/call matrix](control-flow-memory.md): actual terminating direct/mutual recursion, live addends and seed. |
| `Switch_SelectsCaseOrDefault` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): 11/22/99, plus actual raw-CIL switch execution. |
| `Header_IsBootableCartridge` | Requirement | REPLACE complete | [Native memory/call matrix](control-flow-memory.md): relocated startup, full logo, MBC5 and independent checksums. |
| `Boots_FromEntryVector` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): PC0100 reaches return42 with balanced SP. |
| `Mmio_StoreReachesHardwareRegister` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): actual SCY store/read0x37. |
| `Global_RomTable_IndexedRead` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): direct read15 from linked ROM bytes. |
| `Global_RomI16Constant` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): direct little-endian ROM read1000. |
| `Global_WramState_MutatedAcrossCalls` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): two calls update shared counter to2. |
| `Gep_DynamicIndex_I8Array` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): runtime byte index2 returns30. |
| `Gep_DynamicIndex_I16Array_ScalesBySize` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): runtime word index1 returns200. |
| `Pointer_Parameter_Dereferences` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): callee reads caller's byte77. |
| `Store_ThroughDynamicPointer_RoundTrips` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): store/read99 plus adjacent-element preservation. |
| `PhiSwap_RealizedInParallel` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): explicit block-parameter cycle, two swaps return1. |
| `SumLoop_RunsToCompletion` | Requirement | PORT complete | [Native memory/call matrix](control-flow-memory.md): existing sum graph explicitly executes10=45 at three widths. |
| `PhiCopyCycle_MixedWidth_DoesNotCorruptWiderValue` | Requirement | REPLACE complete | [Native memory/call matrix](control-flow-memory.md): full64-bit parallel cycles beside I32 parameters, not private allocator slot overlap. |

### Backends/Sm83EmitterTests.cs (1)

Classification: Private emitter byte count. An eleven-byte I8 add is not a language or ABI requirement; runtime addition with wrapping is.

Complete in [allocation-independent acceptance](allocation-contracts.md): actual
compiler-emitted caller/callee addition and wrapping replace the private emitter
constructor and byte-count/prefix assertions.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `ArithmeticEmitter_LowersI8Add` | Detail | REPLACE complete | [Native addition/ABI acceptance and removed assertions](allocation-contracts.md). |

### Backends/Sm83EncodingTests.cs (4)

Classification: ISA encodings. ROM bytes must implement the selected SM83 instructions; agreement with another Koh table is corroboration, not an external ISA oracle.

Complete native replacement: [independent encoding/length/execution contracts](encoding-contracts.md), `rust/koh-compile/tests/driver/{symbols,machine_effects}.rs`.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Loads_MatchTable` | Requirement | REPLACE complete | [Independent ISA bytes, names, boundaries and executable effects](encoding-contracts.md). |
| `Alu_MatchTable` | Requirement | REPLACE complete | [Independent ISA bytes, names, boundaries and executable effects](encoding-contracts.md). |
| `ControlFlow_MatchTable` | Requirement | REPLACE complete | [Independent ISA bytes, names, boundaries and executable effects](encoding-contracts.md). |
| `CbShifts_MatchTable` | Requirement | REPLACE complete | [Independent ISA bytes, names, boundaries and executable effects](encoding-contracts.md). |

### Backends/Sm83GepFusionTests.cs (5)

Classification: Address computation and private fusion decisions. Pointer indexing and both uses must remain correct even if another instruction is scheduled between address definition and use.

Complete native replacement: [indexed address/liveness contracts](indexed-addresses.md),
`rust/koh-compile/tests/{sm83.rs,support/rewrites.rs}`.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `SingleUseGep_ImmediatelyStored_ComputesCorrectAddress` | Requirement | PORT complete | [Real Debug/Release indexed address and physical memory observation](indexed-addresses.md#source-disposition). |
| `SingleUseGep_ImmediatelyLoaded_ComputesCorrectAddress` | Requirement | PORT complete | [Real Debug/Release indexed address and physical memory observation](indexed-addresses.md#source-disposition). |
| `TwoUseGep_StillComputesBothUsesCorrectly` | Requirement | PORT complete | [Real Debug/Release indexed address and physical memory observation](indexed-addresses.md#source-disposition). |
| `DeferredSingleUseGep_AcrossInterveningInstructions_StillComputesCorrectly` | Requirement | PORT complete | [Real Debug/Release indexed address and physical memory observation](indexed-addresses.md#source-disposition). |
| `FusedGep_IsInternalContextSet_ForImmediatelyConsumedGepOnly` | Detail | REPLACE complete | [Visible address reuse replaces the private context; no arbitrary cycle/slot contract](indexed-addresses.md#source-disposition). |

### Backends/Sm83LoopPointerPhiResidencyTests.cs (5)

Classification: Loop edge behavior and private pair assignment. Each predecessor supplies its own pointer and each loop step accesses the intended byte. Pair names and synchronization structures are implementation details.

Complete native replacement: [loop-carried pointer behavior](pointer-walks.md), `rust/koh-compile/tests/{sm83.rs,support/rewrites.rs}`.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `MultiEntry_BothArmsGetTheirOwnInitSync` | Mixed | REPLACE complete | [Predecessor-specific pointer stepping and exact bus observations](pointer-walks.md#source-dispositions). |
| `MultiEntry_EachArmWalksItsOwnBuffer_RunsCorrectlyOnHardware` | Requirement | PORT complete | [Original distinct-buffer/linear results plus all accesses and final pointers](pointer-walks.md#source-dispositions). |
| `LoadWalk_PhiGetsRegisterPair_AndDerefIsFused` | Mixed | REPLACE complete | [Predecessor-specific pointer stepping and exact bus observations](pointer-walks.md#source-dispositions). |
| `StoreWalk_PhiGetsRegisterPair_PrefersHl` | Mixed | REPLACE complete | [Predecessor-specific pointer stepping and exact bus observations](pointer-walks.md#source-dispositions). |
| `LoadWalk_RunsCorrectlyOnHardware` | Requirement | PORT complete | [Original distinct-buffer/linear results plus all accesses and final pointers](pointer-walks.md#source-dispositions). |

### Backends/Sm83OpsTests.cs (3)

Classification: ISA arithmetic helper facts and private normalization. ADC/SBC carry propagation, sign extension and predicate reversal follow integer/ISA semantics; helper names do not.

All three methods complete: two in [allocation-independent acceptance](allocation-contracts.md)
and the128-bit helper in [full-width numeric acceptance](int128.md#source-test-disposition).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `AluOpcodes_UseCarryFormForHighBytes` | Requirement | REPLACE complete | [Native carry/borrow and bitwise values](allocation-contracts.md). |
| `ByteOf_SignExtendsBeyond64Bits` | Requirement | REPLACE complete | [Positive/negative values with all16 bytes checked](int128.md#source-test-disposition); all six original numeric assertions retained. |
| `Normalize_SwapsAndSignsPredicates` | Requirement | REPLACE complete | [Unsigned greater, signed less-or-equal and equality](allocation-contracts.md). |

### Backends/Sm83PeepholeTests.cs (38)

Classification: Rewrite legality plus private edit lists. Carry consumers, branch targets, aliases, calls, IO and asynchronous observers constrain transformations regardless of peephole architecture.

Complete native locations: `tests/sm83.rs`, `tests/support/rewrites.rs`,
`tests/driver/symbols.rs`, real Roslyn `RewriteContracts` and the existing
emulator test adapter. See [38-row rewrite contracts](rewrite-legality.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `InstructionLength_MatchesTheOpcodeTable` | Requirement | PORT, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `ConvertsZeroLoad_WhenFollowedByAFlagRedefiningOp` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `ConvertsZeroLoad_WhenFollowedByAFlagRewritingCbRotate` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsZeroLoad_WhenFollowedByCarryConsumer` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsZeroLoad_WhenFollowedByACbCarryRotate` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsZeroLoad_WhenFollowedByAControlFlowBoundary` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsZeroLoad_WhenNextInstructionIsABranchTarget` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `FoldsHlLoadWithFollowingIncrement` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `FoldsHlStoreWithFollowingDecrement` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DoesNotFoldWhenIncrementIsABranchTarget` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DoesNotFoldAnUnrelatedInstruction` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `FoldsTailCallToJump` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DoesNotFoldTailCall_WhenCallTargetIsNotWhitelisted` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DoesNotFoldTailCall_WhenCallIsNotFollowedByRet` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DoesNotFoldTailCall_WhenRetIsABranchTarget` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DeletesDeadStore_WhenSameSlotIsOverwrittenAdjacently` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DeletesDeadStore_AcrossANonReadingInstruction` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenTheSecondTargetsADifferentSlot` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenAReadIntervenes` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenAPointerWriteIntervenes` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenAddressIsAboveWram` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenAddressIsBelowWram` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenTheDeadStoreIsABranchTarget` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenAJoinIntervenes` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsStore_WhenDeadStoreEliminationIsDisabled` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DeletesRedundantReload_AfterStoreToSameSlot` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DeletesRedundantReload_AfterLoadOfSameSlot` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DeletesRedundantReload_AcrossAStoreToAnotherSlot` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsReload_WhenAIsWrittenBetween` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsReload_WhenAPointerWriteIntervenes` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsReload_WhenACallIntervenes` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsReload_WhenAddressIsNotWram` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsReload_WhenTheReloadIsABranchTarget` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsReload_WhenAJoinClearsTheMirror` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsReload_WhenEliminationIsDisabled` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DeletesJumpToFallThrough` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `KeepsJump_WhenNotMarkedRedundant` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |
| `DeletesJumpToFallThrough_EvenWhenTheJumpIsABranchTarget` | Mixed | REPLACE, complete | [Independent result, control, ABI or observable memory contract](rewrite-legality.md#source-test-disposition). |

### Ir/IrBuilderAndVerifierTests.cs (5)

Classification: IR invariants and textual compatibility. Typed returns/calls and terminated blocks are prerequisites for sound lowering; the C# builder API is disposable.

Complete native locations: `tests/ir/construction.rs`, `tests/sm83.rs` and shared
`tests/support/sum_loop.rs` under `rust/koh-compile`; discoverable by nextest.
See [quantified construction dispositions](ir-construction.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Builder_ProducesVerifiableModule` | Mixed | REPLACE, complete | Verify and execute a typed two-phi sum loop at16/32/64 bits before/after optimization. |
| `Builder_RoundTripsThroughText` | Mixed | REPLACE, complete | Deterministic canonical ELF emission/round-trip plus native execution replaces private IR text stability. |
| `Verifier_FlagsMissingTerminator` | Mixed | REPLACE, complete | Nonoptional Rust terminators prevent missing terminators; reject empty functions, invalid successors and missing return values before emission. |
| `Verifier_FlagsReturnTypeMismatch` | Mixed | REPLACE, complete | Reject mismatched native16/I32 return signatures in verification and emission. |
| `Verifier_FlagsCallArgTypeMismatch` | Mixed | REPLACE, complete | Reject native16 arguments to I32 parameters before emission. |

### Ir/IrLivenessTests.cs (4)

Classification: Dataflow safety with private set representation. Entry parameters, branch uses and backedges must not be overwritten while live. Constants do not need writable storage.

Complete native location: `tests/sm83.rs::live_values_survive_entry_branches_and_backedges`
with `tests/support/liveness.rs`, under `rust/koh-compile`; discoverable by nextest.
See the [source matrix and independent oracles](liveness.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Liveness_ParameterIsLiveInAtEntry` | Mixed | REPLACE, complete | Execute `(n+1)+n` before/after optimization with full-width results and guards. |
| `Liveness_ValueLiveAcrossBranches` | Mixed | REPLACE, complete | Execute both diamond arms with an entry-defined live value and observed block-parameter result. |
| `Liveness_LoopCarriedValuesLiveAroundBackEdge` | Mixed | REPLACE, complete | Execute signed-bound induction loops, preserving bound/backedge values and checking negative/empty bounds. |
| `Liveness_ConstantsAreNotTracked` | Mixed | REPLACE, complete | Execute live-parameter expressions with constants1/7; preserve results without requiring private trackability sets. |

### Ir/IrRoundTripTests.cs (7)

Classification: Private textual IR and verifier rules. Text syntax is not a published source-to-ROM interface; representation stability matters only if the replacement exposes it. Equal-sized bit reinterpretation is independently necessary.

Complete dispositions and quantified evidence: [IR round-trip migration](ir-roundtrip.md).
Native cases are discoverable in `driver::ir_contracts`, the native CLI entry
test and `ir::construction`; one parser-only case is intentionally obsolete.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Canonical_ParsesAndVerifiesClean` | Detail | REPLACE, complete | Compile actual Debug/Release CIL and execute banked control flow, calls and initialized data. |
| `Canonical_HasExpectedStructure` | Detail | REPLACE, complete | Assert actual table1/2/3, counter1, branch/call results and switch outcomes; drop private module/member counts. |
| `Print_Parse_Print_IsStable` | Detail | REPLACE, complete | Deterministic canonical ELF compilation and read/write round-trip, followed by ROM execution. |
| `EntryFlag_RoundTrips` | Detail | REPLACE, complete | Main after another method and explicit alternate roots execute from Debug/Release DLL/EXE inputs. |
| `Bitcast_RoundTripsAndVerifies` | Detail | REPLACE, complete | Execute all16 pointer bits across six unsigned patterns; verify same-width float/integer transport independently. |
| `Parser_RejectsNumericOpToken` | Detail | DELETE, complete decision | Private text parser has no production caller; native input is PE/CIL, so no numeric text-enum boundary survives. |
| `Bitcast_RejectsMismatchedSize` | Requirement | PORT, complete | Reject native16↔I32 and mismatched integer/float bit reinterpretation before emission. |

### Ir/LoopInvariantCodeMotionTests.cs (6)

Classification: Optimization shape with control/memory safety constraints. Invariant arithmetic may move only when values dominate their uses and no newly executed operation changes observable effects; loads need alias/IO proof.

Complete native locations: `tests/sm83.rs`, `tests/support/loop_contracts.rs`,
the real Roslyn fixture `tests/fixtures/LoopContracts`, and the existing native
dominance rejection in `tests/ir.rs`. See [quantified loop contracts](loop-contracts.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Licm_HoistsInvariantArithmeticIntoPreheader` | Mixed | REPLACE, complete | Execute invariant bounds/sums with full-width trip counts; no required preheader shape. |
| `Licm_DoesNotHoistLoopVariantComputation` | Mixed | REPLACE, complete | Execute changing induction-dependent sums/comparisons before/after optimization. |
| `Licm_DoesNotHoistLoads` | Mixed | REPLACE, complete | Actual stores through an invariant pointer change subsequent loads and the final observation. |
| `Licm_SplicesPreheaderWhenEntryEdgeIsConditional` | Mixed | REPLACE, complete | Both conditional-entry paths preserve results and executed/skipped division faults. |
| `Licm_DoesNotOverHoistValueDefinedInSplicedInnerPreheader` | Mixed | REPLACE, complete | Execute valid nested control flow on both inner paths; reject non-dominating definitions independently. |
| `Licm_HoistedResultIsReusedNotRecomputed` | Mixed | REPLACE, complete | Verify and execute original/once/twice optimized IR; no private Changed flag or unmeasured timing contract. |

### Ir/NarrowPassTests.cs (19)

Classification: Integer-width preservation and private rewrite structure. Low-bit add/sub/mul/bitwise observations can permit narrowing; signed comparisons, division and shifts require separate proofs. Wide escaping uses forbid truncating their value.

All19 methods are REPLACE complete in `sm83::narrowing_preserves_comparisons_and_every_escaping_width`
and `sm83::byte_counter_backedges_preserve_wrapping_without_narrowing_the_trip_count`.
See the [73-site source matrix and independent execution oracles](narrowing.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Add_DemotesWhenOperandsAreExtensionsAndResultIsTruncated` | Mixed | REPLACE, complete | [sum](narrowing.md#source-test-disposition). |
| `Add_DemotesToI16WhenOperandsAreExtensionsFromI16` | Mixed | REPLACE, complete | [16-bit sum](narrowing.md#source-test-disposition). |
| `Add_DemotesWithMixedZextAndSextOperands` | Mixed | REPLACE, complete | [mixed](narrowing.md#source-test-disposition). |
| `Add_DemotesWithConstantOperand` | Mixed | REPLACE, complete | [constant300](narrowing.md#source-test-disposition). |
| `Chain_DemotesEveryLinkWhenOnlyTheOuterResultEscapesViaTrunc` | Mixed | REPLACE, complete | [chain](narrowing.md#source-test-disposition). |
| `Chain_DoesNotDemoteWhenAnInnerLinkAlsoEscapesAtWideWidth` | Mixed | REPLACE, complete | [wide-chain](narrowing.md#source-test-disposition). |
| `Add_DoesNotDemoteWhenResultAlsoObservedAtWideWidth` | Mixed | REPLACE, complete | [wide-sum](narrowing.md#source-test-disposition). |
| `Add_DoesNotDemoteWhenTruncatedToTwoDifferentWidths` | Mixed | REPLACE, complete | [two-widths without a wide sink](narrowing.md#source-test-disposition). |
| `Shl_IsNeverDemotedEvenWhenOperandsAndConsumerLookEligible` | Mixed | REPLACE, complete | [int32 shift masking](narrowing.md#source-test-disposition). |
| `UDiv_IsNeverDemotedEvenWhenOperandsAndConsumerLookEligible` | Mixed | REPLACE, complete | [unsigned division and zero faults](narrowing.md#source-test-disposition). |
| `Compare_SltWithZextOperandsRemapsToUltAndFoldsToTheCorrectValue` | Mixed | REPLACE, complete | [zlt200/50=0](narrowing.md#source-test-disposition). |
| `Compare_SltWithSextOperandsStaysSltAndFoldsToTheCorrectValue` | Mixed | REPLACE, complete | [slt200/50=1](narrowing.md#source-test-disposition). |
| `Compare_SgeWithZextI16OperandsRemapsToUgeAndFoldsToTheCorrectValue` | Mixed | REPLACE, complete | [zge40000/1000=1](narrowing.md#source-test-disposition). |
| `Compare_UnsignedPredicateStaysUnchangedForEitherExtensionKind` | Mixed | REPLACE, complete | [zult and sult](narrowing.md#source-test-disposition). |
| `Compare_EqDemotesForMatchingExtensionKind` | Mixed | REPLACE, complete | [zeq and seq](narrowing.md#source-test-disposition). |
| `Compare_DoesNotDemoteMixedExtensionKinds` | Mixed | REPLACE, complete | [mixed-eq](narrowing.md#source-test-disposition). |
| `Compare_DoesNotDemoteWhenConstantDoesNotFitTheNarrowRange` | Mixed | REPLACE, complete | [constant-lt300](narrowing.md#source-test-disposition). |
| `NarrowPass_NarrowsAByteLoopCounterCompareAndIncrement` | Mixed | REPLACE, complete | [direct byte counter and full trip count](narrowing.md#source-test-disposition). |
| `Optimize_WiresNarrowPassIntoTheDefaultPipeline` | Mixed | REPLACE, complete | [production optimizer with executed output](narrowing.md#source-test-disposition). |

### Ir/OptimizationTests.cs (36)

Classification: Semantic preservation and private pipeline structure. Optimization must preserve fixed-width results, effects, aliases and CFG/phi meaning; node identity and specific pass ordering are not user contracts.

Complete native locations: `tests/sm83.rs`, `tests/support/optimization.rs`,
`tests/fixtures/OptimizationContracts`, and reused native verifier/phi contracts.
See the [36-row quantified matrix](optimization-semantics.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `ConstantFolding_FoldsIntegerArithmetic` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `ConstantFolding_WrapsToOperandWidth` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `ConstantFolding_SignedAndUnsignedDivideDiffer` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `ConstantFolding_DoesNotFoldDivideByZero` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `ConstantFolding_FoldsComparisons` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `ConstantFolding_FoldsConversions` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `ConstantFolding_AppliesIdentities` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `DeadCodeElimination_RemovesUnusedChain` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `DeadCodeElimination_KeepsUnusedLoad` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `DeadCodeElimination_KeepsStoresAndCalls` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `DeadCodeElimination_SweepsClosedDeadPhiCycle` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `DeadCodeElimination_KeepsPhiCycleReachableFromStore` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `Optimize_FoldsAndEliminatesCascade` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `Optimize_RewritesPhiIncomingThroughRauw` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `Optimize_LeavesNonConstantCodeIntact` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `RedundantLoadElimination_ForwardsStoredValueToLoad` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `RedundantLoadElimination_CollapsesRepeatedLoads` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `RedundantLoadElimination_DoesNotForwardThroughEscapingAlloca` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `DeadStoreElimination_RemovesStoresToWriteOnlyAlloca` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `DeadStoreElimination_KeepsStoresToLoadedAlloca` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `SimplifyCfg_FoldsConstantBranchAndDropsUnreachableBlock` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `SimplifyCfg_PrunesPhiIncomingFromDeadEdge` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `Optimize_PromotesScalarLocalToDirectDataflow` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `StrengthReduction_MultiplyByPowerOfTwoBecomesShift` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `StrengthReduction_UnsignedDivideByPowerOfTwoBecomesShift` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `StrengthReduction_UnsignedRemainderByPowerOfTwoBecomesMask` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `StrengthReduction_LeavesNonPowersAlone` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `StrengthReduction_SignedDivideByPositivePowerOfTwoBecomesBiasedShift` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `StrengthReduction_LeavesSignedDivideByNegativePowerBoundaryAlone` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `LocalCse_DeduplicatesIdenticalExpressions` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `LocalCse_DeduplicatesGepWithEqualConstantIndex` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `LocalCse_KeepsDistinctAllocasAndLoads` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `Mem2Reg_PromotesLocalAcrossIfElseIntoPhi` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `TrivialPhiElimination_ReplacesSingleIncomingPhi` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `Inlining_SplicesLeafCalleeAndDropsTheCall` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |
| `Optimize_RemovesUnreachableFunctions` | Mixed | REPLACE, complete | [Independent behavioral contract and native location](optimization-semantics.md#source-test-disposition). |

### Ir/RedundantBankSelectEliminationTests.cs (6)

Classification: Mapper state and private elimination counts. Selecting a ROM bank changes which physical byte a window address denotes. Calls and unknown MBC writes can invalidate that state.

Complete native locations: `tests/sm83.rs::mapper_writes_and_calls_preserve_physical_rom_reads`,
`tests/fixtures/BankingContracts`, and the production banked-call cases in
`tests/driver.rs`. See [physical mapper-state contracts](mapper-state.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `RemovesConsecutiveSelectsOfTheSameBank` | Mixed | REPLACE, complete | [Physical-bank observation and independent hardware contract](mapper-state.md). |
| `KeepsSelectsOfDifferentBanks` | Mixed | REPLACE, complete | [Physical-bank observation and independent hardware contract](mapper-state.md). |
| `CallInvalidatesKnownBank` | Mixed | REPLACE, complete | [Physical-bank observation and independent hardware contract](mapper-state.md). |
| `OrdinaryMemoryWriteIsTransparent` | Mixed | REPLACE, complete | [Physical-bank observation and independent hardware contract](mapper-state.md). |
| `WriteToAnotherMbcRegisterInvalidates` | Mixed | REPLACE, complete | [Physical-bank observation and independent hardware contract](mapper-state.md). |
| `VariableBankSelectClearsKnownBank` | Mixed | REPLACE, complete | [Physical-bank observation and independent hardware contract](mapper-state.md). |

### Ir/RedundantCompareEliminationPassTests.cs (5)

Classification: Boolean algebra with private normalized nodes. Comparisons produce 0/1; comparisons of arbitrary integers must not be treated as booleans.

Complete native replacement: [comparison/conversion contracts](optimizer-scalar-contracts.md),
`rust/koh-compile/tests/{lowering,sm83}.rs`.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `DoubleNegationChain_CollapsesToSingleNegatedCompare` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `NeAgainstZero_IsIdentity_ReplacedByInnerCompare` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `EqAgainstZero_IsNegation_ReplacedByFlippedPredicate` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `EqAgainstOne_IsIdentity_ReplacedByInnerCompare` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `NonBooleanOperand_IsLeftAlone` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |

### Ir/RedundantConversionEliminationPassTests.cs (6)

Classification: Conversion identities and private pipeline order. Truncate-to-original-width after extension preserves low bits; extension after truncation loses high bits and cannot generally be removed.

Complete native replacement: [comparison/conversion contracts](optimizer-scalar-contracts.md),
`rust/koh-compile/tests/{lowering,sm83}.rs`.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `TruncOfZext_FoldsToTheOriginalValue` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `TruncOfSext_FoldsToTheOriginalValue` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `TruncOfZext_FoldsAtI16TooNotJustI8` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `DoesNotFold_WhenTruncWidthDiffersFromTheOriginalValueWidth` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `DoesNotFold_ZextOfTrunc` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |
| `Optimize_WiresThePassIntoTheDefaultPipelineAfterNarrowPass` | Mixed | REPLACE | Preserve the named algebra, escape, CFG or mapper boundary using before/after semantic checks; do not require node identity or pass order. |

### Sm83/DisassemblerLengthTests.cs (1)

Classification: Two internal decoders agreeing. Instruction boundaries are ISA facts; matching two local implementations is insufficient if they share the same defect.

Complete native replacement: [independent encoding/length/execution contracts](encoding-contracts.md), `rust/koh-compile/tests/driver/{symbols,machine_effects}.rs`.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `EveryOpcode_HasTheSameLength_InBothDecoders` | Requirement | REPLACE complete | [All512 independently specified base/CB lengths and emitted boundaries](encoding-contracts.md). |

### Sm83/FlushRunLoopResidencyTests.cs (2)

Classification: Private allocation/code shape on real runtime helper. A hot copy helper needs correct addressing and acceptable cost; two phis assigned HL/DE and a recognized opcode pattern are not the requirement.

Proposed future location: `rust/koh-compiler/tests/runtime_memory.rs` (not implemented).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `FlushRun_DstAndSrcPointerPhisAreRegisterResident` | Detail | REPLACE | Check the same fixture's runtime result and an independently chosen size/cycle budget; omit old allocation/fusion structure. |
| `FlushRun_CompiledLoopBodyUsesFusedAddressingNotPerIterationReload` | Detail | REPLACE | Check the same fixture's runtime result and an independently chosen size/cycle budget; omit old allocation/fusion structure. |

### Sm83/MemRuntimeTests.cs (10)

Classification: Runtime memory behavior and performance budgets. Zero count, exact extents, page boundaries, defined forward overlap and bounded target cycles are independently useful to games.

All ten methods already have native dispositions in the [central fill/copy matrix](test-disposition.md#runtime-fill-replacements). Fresh copy extent/overlap/cost checks passed12/12; see [reconciliation evidence](pointer-walks.md#verification). This audit correction does not increase the completed-method count.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Frontend_ProducesVerifiableIr` | Mixed | REPLACE complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |
| `Copy_CountZero_IsNoOp` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |
| `Copy_SingleByte` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |
| `Copy_OddSize` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |
| `Copy_CrossesA256ByteBoundary` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |
| `Fill_VariousCounts` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-fill-replacements). |
| `Fill_CrossesA256ByteBoundary` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-fill-replacements). |
| `Copy_OverlapDestinationBeforeSource_IsDefined` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |
| `Copy_CostPerByte_IsWithinLooseCeiling` | Mixed | REPLACE complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |
| `Copy_MarginalCostPerByte_IsWithinLooseCeiling` | Requirement | PORT complete | [Existing whole-bank memory, exact extents, zero/overlap and unchanged target budgets](test-disposition.md#runtime-copy-replacements). |

### Sm83/RedundantCompareLoopTests.cs (4)

Classification: Real copy correctness and private compare graph. Guarded and sequential copies must obey guards, extents and source order; absence of compare-of-compare nodes is optional.

Three behavioral methods now run through [guarded/sequential native copies](guarded-copies.md). The private real-FlushRun comparison/budget replacement remains open.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `RealFlushRun_LoopGuards_HaveNoRedundantCompareChain` | Detail | REPLACE | Check the same fixture's runtime result and an independently chosen size/cycle budget; omit old allocation/fusion structure. |
| `GuardedStride1CopyLoop_CopiesEveryByte_WhenGuardOpen` | Requirement | PORT complete | [Exact real-CIL copy results, guards, extents and physical access traces](guarded-copies.md). |
| `GuardedStride1CopyLoop_CopiesNothing_WhenGuardClosed` | Requirement | PORT complete | [Exact real-CIL copy results, guards, extents and physical access traces](guarded-copies.md). |
| `TwoSequentialStride1CopyLoops_BothCopyCorrectly` | Requirement | PORT complete | [Exact real-CIL copy results, guards, extents and physical access traces](guarded-copies.md). |

### Sm83/Sm83LoopCodegenTests.cs (20)

Classification: Observable loop values, aliasing and target cycle budgets. Exited induction values, old SSA values, nested/sequential loops and overlapping forward copies must retain their language meaning.

Six exit/saved-value methods have [native real-CIL replacements](loop-exit-values.md). The [counted baseline](loop-costs.md) is also complete. The other13 methods, including timing ceilings, remain open.

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `Baseline_CountedConstAddrStoreLoop` | Requirement | PORT complete | [Real-CIL100-iteration baseline with observed99 and explicit timing limitations](loop-costs.md). |
| `CountedConstAddrStoreLoop_MeetsCeiling` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `InductionValueUsedAfterLoopExit` | Requirement | PORT complete | [Exact exit values, live counters and ordered stores in real-CIL ROMs](loop-exit-values.md). |
| `InductionValueUsedAfterLoopExit_WithInterveningClobber_StillCorrect` | Requirement | PORT complete | [Exact exit values, live counters and ordered stores in real-CIL ROMs](loop-exit-values.md). |
| `NestedLoops` | Requirement | PORT complete | [Exact exit values, live counters and ordered stores in real-CIL ROMs](loop-exit-values.md). |
| `EarlyBreakExitEdge` | Requirement | PORT complete | [Exact exit values, live counters and ordered stores in real-CIL ROMs](loop-exit-values.md). |
| `InductionValueSavedBeforeIncrement_UsedAfterIt` | Requirement | PORT complete | [Exact exit values, live counters and ordered stores in real-CIL ROMs](loop-exit-values.md). |
| `InductionValueSavedBeforeIncrement_ReadAfterLoopExit` | Requirement | PORT complete | [Exact exit values, live counters and ordered stores in real-CIL ROMs](loop-exit-values.md). |
| `PointerCopyLoop_RegressionSafe` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `TwoPointerCopyLoop_MeetsCeiling` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `SinglePointerFillLoop` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `PointerUsedAfterLoopExit` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `NonQualifyingMiddleInstruction_NegativeAdmission_StillCorrect` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `OverlappingRegionCopy_ForwardSemanticsPreserved` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `TwoSequentialI8CountedLoops_DistinctCounters` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `TwoI8CountedLoopsInIfElseBranches` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `TwoSequentialFillLoops_DistinctBuffers` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `TwoLoopsInIfElseBranches` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `TwoSequentialCopyLoops_DistinctBuffers` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |
| `CopyLoopFollowedByFillLoop` | Requirement | PORT | Retain this fixture's observable result/boundary; adapt harness and ABI observation to Rust output. |

### Sm83/Sm83SymbolAddressTests.cs (3)

Classification: Object/debug symbol and entry contracts with private placement. Symbols must resolve to emitted code/data and header entry; exact C000 placement is an implementation choice unless explicitly fixed.

Implemented location: `rust/koh-compile/tests/driver/symbol_addresses.rs`.
All three dispositions are complete; [source-by-source rationale and native evidence](symbol-addresses.md).

| Existing [Test] method | Assertion class | Disposition | Native acceptance |
|---|---|---|---|
| `FunctionLabel_SymbolAtActualCodeAddress_NotDoubleCountingCodeBase` | Requirement | PORT complete | Actual code bytes and call relocation targets at explicitly placed banked symbols; observable Helper writes. |
| `EntryFunctionLabel_SymbolMatchesHeaderEntryPoint` | Mixed | REPLACE complete | Header reaches relocated startup; banked managed entry executes after stack/static initialization. Main/header equality retires with the old ABI. |
| `WramGlobalLabel_SymbolAtWramBase` | Mixed | REPLACE complete | Explicitly relocate WRAM storage; compare symbols, offsets and actual physical byte values without requiring default C000 placement. |

## Coverage accounting and acceptance boundary

Disposition totals: **PORT 102, REPLACE 187, DELETE 1**;
total **290**, matching the source [Test] attribute count. No DEFER entries.
Rows explicitly marked complete cite executed native acceptance; remaining native
paths are proposed destinations. The old suite mixes direct IR construction,
Roslyn-compiled runtime helpers, byte/graph assertions, linking and emulator runs;
passing one layer cannot substitute for another. Native completion requires actual
Rust metadata-to-object output, canonical ELF inspection, linking and execution with
observed values, bounded termination and fresh completed logs. Cycle ceilings
(190 dots/counted-loop iteration, 740 dots/two-pointer-copy iteration, Mem.Copy
1000 dots/byte small-case and 360 marginal dots/byte) are existing regression
budgets, not claimed physical timing measurements or necessarily optimal limits.
Retain them initially with the same measurement boundary; revise only against a
game's independently justified timing budget.

