# Scalar contracts from the control-flow suite

The first34 methods in `Backends/Sm83ControlFlowTests.cs` contain37 numeric
assertion sites. All34 are PORT complete in
`sm83::scalar_width_contracts_execute_before_and_after_optimization`, using
`tests/support/control_flow_scalars.rs`. These are public numeric requirements;
the old IR constructors, static argument slots and A/HL/DE return conventions
are private implementation details. The remaining20 methods in that source
file retain pending dispositions for calls, memory, boot and phi behavior.

| Source method | Preserved independent expectation |
| --- | --- |
| `I16_Add_CarriesAcrossBytes` | 300+250=550 |
| `I16_Sub_BorrowsAcrossBytes` | (200+100)-(100+50)=150, retaining the producer chain |
| `I16_Ult_True` | unsigned5<10 gives1 |
| `I16_Ugt_True_ViaSwap` | unsigned10>5 gives1 |
| `I16_Eq_False` | 7==8 gives0 |
| `Mul_I8` | 6*7=42 |
| `Mul_I16` | 300*4=1200 |
| `Mul_Signed_I8_LowByteMatches` | -3*4 modulo256 is F4 |
| `UDiv_I16` | 1000/7=142 |
| `URem_I16` | 1000%7=6 |
| `UDiv_I8` | 100/9=11 |
| `SDiv_I16_NegativeDividend` | -1000/7 truncates toward0: -142, FF72 |
| `SRem_I16_SignOfDividend` | -1000%7=-6, FFFA |
| `SDiv_I16_BothNegative` | -1000/-7=142 |
| `Shl_Const_I8` | 1<<4=16 |
| `LShr_Const_I8` | unsigned80>>3=10 hex |
| `AShr_Const_I8_SignFills` | signedF8>>1=FC |
| `Shl_Const_I16` | 1<<12=4096 |
| `LShr_Const_I16` | unsigned8000>>15=1 |
| `AShr_Const_I16_SignFills` | signedFFF0>>2=FFFC |
| `Shl_Variable` | runtime3<<4=48; fixed WRAM argument slots retire |
| `Slt_Signed_NegativeLessThanPositive` | signed-5<3 gives1 |
| `Sgt_Signed_NegativeOrdering` | signed-1>-2 gives1 |
| `SignedVsUnsigned_DifferForNegatives` | FF<1 gives1 signed,0 unsigned |
| `Slt_Signed_I16` | signed-1000<5 gives1 |
| `Slt_Signed_CloseValuesWithLowByteBorrow` | 299<300 gives1, reverse0, -300<-299 gives1 |
| `I32_Add_CarriesAcrossFourBytes` | preserve70000+100=70100; add actual cross-byte carry vectors |
| `I32_Sub_BorrowsAcrossFourBytes` | preserve100000-1=99999; add actual cross-byte borrow vectors |
| `I32_Bitwise_And` | 00FF00FF & 0F0F0F0F = 000F000F |
| `I32_SignedCompare_AcrossBoundary` | preserve100000>99999; add actual signed-boundary vectors |
| `I32_ZeroExtendFromByte` | unsignedFF widens to000000FF |
| `ZExt_I8_To_I16` | 200 widens to00C8 |
| `SExt_I8_To_I16_SignExtendsHighByte` | signed8-bit200 widens toFFC8 |
| `Trunc_I16_To_I8_KeepsLowByte` | 1234 narrows to34 hex |

Expected values follow modular fixed-width arithmetic, signed ordering,
truncation-toward-zero division, dividend-signed remainder and explicit bit
extension/truncation. They are literal independent expectations, not results
copied from the C# compiler or the Rust optimizer. Hex values above describe
bit patterns; returned values are checked across their full logical width.

The new IR uses I32 evaluation values for small integers; explicit conversions
enforce8-bit normalization/sign extension and narrowing. Native16 represents
16-bit bitvectors; shift counts use I32. Comparisons return typed0/1 values.
This adapts the contract without introducing the old I8/I16 SSA model or ABI.
Every input runs both as actual call parameters and as constant-producing IR,
before and after the real optimizer. All functions pass the Rust verifier and
execute through the existing SM83 object/linker/emulator harness with full result,
balanced stack and reserved-memory guard assertions.

Nine additional32-bit expectations exercise carries at FFFF/FFFFFF/FFFFFFFF,
borrows from10000/1000000/0, and signed ordering across80000000/7FFFFFFF and
FFFFFFFF/0. The original positive operands did not establish those stronger
claims despite their test names. The original subtraction producer chain also
runs before/after optimization, plus a runtime-input version that keeps its
intermediate values live rather than allowing constant folding to remove them.

Three rstest width cases cover46 expected vectors in four forms (parameter versus
constant inputs, unoptimized versus optimized), plus four chain executions:
188 ROM executions. Commands batch at most32 ROM paths to bound command-line
length on supported hosts. No production compiler change was needed.

Fresh final execution passes3/3 in3.448s, exit0
(`/tmp/koh-control-flow-scalars-boundaries.log`). Earlier37-vector coverage and
the two chain variants also passed; those are investigative checkpoints. This
closes34 source dispositions without deleting any source definition or deferring
compiler-owned coverage. Ledger266/565 complete (265 native replacements and
one earlier obsolete-parser DELETE),299 outstanding. Source-file completion is
34/54, not54/54.

Final Rustfmt and workspace/all-feature discovery pass. Discovery lists1,826
workspace cases, including1,649 compiler cases with zero ignored; all three new
width cases are present and unignored. A source cross-check confirms exactly
34 methods/37 assertion sites before the memory section in the54-method file.
