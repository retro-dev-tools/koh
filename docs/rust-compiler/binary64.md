# Binary64 core

Status: constants, bit transport, addition/subtraction, negation, comparisons and
eight-byte storage/calls and multiplication/division are implemented. Integer
conversions and remainder remain explicitly unsupported pending independently correct runtime
lowering. This is not complete `double` or general .NET floating support.

## IR and ABI

`F64`/`Scalar::F64` retain the nominal floating identity and64-bit width. Only
`I64`↔`F64` raw reinterpretation is allowed; integer arithmetic, integer numeric
conversion, mismatched widths and pointer fabrication do not become valid float
operations. Typed indirect loads/stores, fields, local/byref values and vector
arrays carry all eight bytes. `System.Double` primitive resolution uses the same
authenticated framework identity rules as integer and binary32 storage.

Existing integer64 argument/return storage moves the raw bits. Metadata keys
`f64.add`/`f64.sub`/`f64.mul`/`f64.div` require `ulong(ulong,ulong)`, while `f64.cmp` requires
`byte(ulong,ulong)` and the equal/less/greater/unordered0/1/2/3 contract. Calls
compile the ordinary reachable library CIL; no host floating execution occurs in
production. Negation flips bit63. Value/fused-branch comparisons use the same
ordered/unordered rule as binary32, including the NaN-sensitive inversion of
`bge`/`ble`. The ABI and canonical ELF32/SM83 object format are unchanged.

`conv.r8` on an existing `F64` is bit-preserving; `F32`→`F64` and
`F64`→`F32` numeric casts use checked runtime metadata. Integer↔binary64 casts
and mixed nominal precision arithmetic currently diagnose. Subsequent integer
conversion work must preserve direct unsigned64→binary32 rounding: a binary64
intermediate can change the halfway-above result. Unsupported shapes can be
valid CIL; they are not diagnosed as malformed metadata.

## Runtime correction

The old `AddF64` returned0 for raw1+raw1 in both the CLR and an independently
executed native ROM, rather than the expected raw2. The corrected helper uses
effective exponent1 without an implicit bit for subnormals, stops normalization
at that exponent and emits exponent0 when the rounded significand remains
subnormal. Separate NaN/infinity/zero handling preserves signed-zero semantics
and quiets arithmetic NaNs. The53-bit significand plus three rounding/sticky bits
fits in64 bits, including the addition carry. This requires no `UInt128` or new
general compiler integer type. Multiplication and division use the independently bounded implementations below.

Independent [Rust binary64 arithmetic](https://doc.rust-lang.org/core/primitive.f64.html)
provides numeric observations consistent with ECMA-335 I.12.1.3. Fourteen boundary
pairs and2048 deterministic exponent-stratified additions cover gradual underflow,
halfway rounding, cancellation, signed zeros, overflow, infinities and NaNs. NaNs
are classified as quiet without freezing a host sign/payload. Sampled checks and
the significand/bounds argument are not exhaustive verification of every pair.

## Native tests and source disposition

`driver/float64.rs` runs83 entries in Debug/Release × DMG/CGB (332 ROMs): actual
Roslyn R8 constants, arithmetic, both comparison positions, calls, joins,
references, fields and arrays. The emulator checks all eight result bytes at
WRAM DFF8, array payload bytes and every surrounding byte/bank/frame guard.
Portable PDB lines pass through ELF and linked KDBG. Missing keys, wrong raw
return signedness and unimplemented operations must diagnose with member/IL
context. Generic32/64 IR tests reject integer/float confusion and mismatched
reinterpretation; hand-CIL identity conversions preserve zero/NaN/subnormal bits
before and after optimization.

| Source `Frontends/CilFloatTests.cs` method | Original requirement | Native disposition |
| --- | --- | --- |
| `F64_Compare_MatchesHost` | `<`1.5/2, `>=`2/2, `==`0.1/0.1, `!=`0.1/0.2, Debug and Release | PORT: `CompareForward`, `CompareEqual`, `CompareFractionEqual`, `CompareFractions` check every value and branch predicate independently in both cartridge modes |

All four original parameter rows and32 expanded assertions are retained through
compile/IR verification, complete CPU return, all result bits and guards. One
method closes; mixed binary64 arithmetic and conversion methods remain open.
The float disposition group now covers19 of22 methods,61 original
parameter-expanded cases and400 explicit expanded assertions. No legacy source
definition is physically retired by this increment.

## Verification

The focused compiler regression run passed658/658 cases in306.759s;292 cases
were outside that selection. After the diagnostic and metadata-negative tests
were finalized, the follow-up passed8/8 cases in141.183s. This includes four
Debug/Release × DMG/CGB driver cases and four SDK cases covering binary32 and
binary64 builds in paths with spaces. SDK ROM execution checks the complete
binary64 result, surrounding WRAM, the CGB bank guard and stack restoration.

Workspace all-target/all-feature checking passed with the existing unused
`cubes` test helper warning. Separate workspace rustdoc checking passed (zero
doctests in each of four crates); Rustfmt, CSharpier and diff checks passed.
These local selections do not establish full migration or cross-platform
acceptance. Binary64 numeric conversions remain explicitly unsupported.

## Division

The former `DivF64` used `UInt128` division and flushed subnormal inputs/results.
The new ordinary-CIL helper normalizes each nonzero operand to53 significant
bits and scales the numerator once so its ratio is in[1,2). Binary long division
emits at most52 fractional bits. Each remainder is strictly smaller than the
53-bit denominator; doubling it fits54 bits. No128-bit value, division helper
or host floating operation is required.

For subnormal output, fewer quotient bits are generated at the final storage
scale. Comparing twice the exact remainder with the denominator performs one
round-to-nearest-even step, including the transition to the smallest normal.
At exponent-52 the result lies between half and one minimum-subnormal unit;
exact equality rounds to zero, while a larger ratio rounds to one. Smaller
exponents underflow to signed zero. NaNs, infinities and zeros are classified
before normalization; arithmetic NaNs are quiet without requiring host payload
or sign identity.

Twenty-four independent boundary observations and2048 exponent-stratified
divisions compare raw finite results with Rust binary64 division. Native cases
cover repeating fractions, minimum subnormals, even/odd halfway rounding, the
normal transition, overflow, signed zeros, divide-by-zero and invalid/NaN
results. Missing `f64.div` metadata also diagnoses with IL context.

`F64_Arithmetic_DebugAndReleaseProduceIdenticalObservableState` is PORT COMPLETE:
`DivideThird` executes the original1/3 in both Roslyn modes and both cartridge
modes. Independent expected bits, CPU completion and memory/frame guards replace
the old cross-mode state equality while preserving its3 assertions. This closes
one additional source method; the mixed four-operation method remains open
until multiplication is implemented.

Division verification passed46/46 focused cases in126.290s, followed by663/663
selected compiler/IR/lowering/float/software-runtime cases in317.607s. The latter
includes all164 binary64 ROMs and7252 managed numeric observations;296 tests
were outside this selection. The separate primitive-array CLI follow-up passed
4/4 cases in20.102s. Workspace all-target/all-feature checking, separate rustdoc
and formatting passed. The existing unused `cubes` helper
warning remains. Independent code review found no confirmed defect. These are
local Linux results, not current-head hosted or cross-platform acceptance.

Focused reproduction (with the .NET SDK on PATH or `KOH_DOTNET` set):

```sh
cargo nextest run -p koh-compile --test driver \
  -E 'test(float64::) | test(softfloat_host::)' --locked
```

## Multiplication

The former `MulF64` required `UInt128` and flushed subnormal inputs/results. The
replacement normalizes each finite nonzero operand to53 bits, then constructs
the exact106-bit product from four32×32 partial products. Low-limb wrap carries
into the high limb explicitly. Every partial product fits64 bits; the complete
product fits two64-bit limbs. All wrap additions are explicitly unchecked.

The rounding helper shifts the two limbs with an accumulated sticky bit and
retains three guard/round/sticky bits. It first accounts for the final
subnormal scale and then rounds once to nearest, ties to even. Shifts above106
return signed zero; ordinary CIL shift operands remain bounded1/32/63. A
rounding carry can promote a subnormal to normal or overflow the largest normal.
Special-value classification precedes normalization and zero/infinity is invalid.

Generating the whole routine as one function exceeded the existing16 KiB bank
capacity check. A shared private partial-product function and a private rounding
function keep each emitted function within that limit. They use ordinary CIL
calls, reachability, existing64-bit argument/result storage and linker banking;
there is no new integer type, compiler intrinsic or alternative object format.

Twenty-three boundary pairs plus2048 exponent-stratified pairs compare against
independent Rust binary64 multiplication. Native ROM cases check all eight
result bytes and surrounding heap/bank/frame guards for subnormals, halfway
rounding, normal transition, product carry, overflow, zeros and NaNs. Metadata
absence still diagnoses at the source member/IL location. Binary64 remainder
is explicitly rejected with `f64.rem` rather than lowered as integer remainder.

`F64_Arithmetic_MatchesHost` is PORT COMPLETE: original1.25+3.5,5-1.5,2×3.25
and7/2 execute with independent expected bits in Debug/Release and both
cartridge modes. All four original rows and32 expanded assertions are retained
by `OriginalAdd`, `OriginalSubtract`, `Multiplication` and `Division`. One source
method closes; conversions and the remaining floating group stay open.

Verification:55/55 focused cases passed in170.946s before the review-requested
double-carry vector was added. The final selected regression passed676/676 in
381.176s, including228 binary64 ROMs (56 multiplication executions),9323 managed
numeric observations and all primitive-array CLI cases;292 tests were outside
the selection. Workspace all-target/all-feature checking, separate rustdoc and
formatting passed. Independent review found no confirmed defect and its extra
carry vector is covered by the final run. These local results do not establish
complete migration, exhaustive numeric proof or cross-platform acceptance.

## Precision casts

`conv.r8` from binary32 selects `f32.toF64`, requiring `ulong(uint)` raw bits.
`conv.r4` from binary64 selects `f64.toF32`, requiring `uint(ulong)`. The same
metadata validator rejects missing helpers and wrong raw width/signedness;
renamed helpers compile successfully. No helper name is part of the compiler
contract. Numeric casts are calls with independently verified raw integer
signatures, followed by width-matched IR reinterpretation. Integer arithmetic
and integer IR conversions on float values remain forbidden.

Widening is exact for finite binary32 values, including all subnormals. The
old helper returned zero for the smallest subnormal, reproduced by a native ROM.
Normalization recovers the binary32 significand/exponent before rebiasing to
binary64. Narrowing first chooses its final normal/subnormal output scale,
then compares the exact discarded remainder with half and rounds once to
nearest, ties to even. Its shifts are bounded29..53; smaller magnitudes round
to signed zero. Rounding may promote a subnormal or overflow to infinity.
Zero signs and infinities survive, and NaNs are quiet without freezing a host
sign/payload. Binary64 subnormals are all far below half the minimum binary32
subnormal, so they round to signed zero.

Native fixtures add104 cast executions across Debug/Release × DMG/CGB, checking
all eight result bytes (including zero upper bytes for narrowed results), heap,
WRAM-bank and stack guards, plus source/PDB/ELF/KDBG provenance. Managed checks
add12+256 widening observations and18+2048+256 narrowing observations, including
all exponent fields and extra samples near binary32's representable range.

| Source `CilFloatTests` method | Requirement and independent basis | Disposition / Rust coverage |
| --- | --- | --- |
| `F32_ToF64_MatchesHost` | Exact widening of1.5f,-2.5f,0.1f | PORT: `Widen0..2`, raw Rust binary64 expectations, both modes/cartridges |
| `F32_ToF64_RoutesThroughRealSoftFloatFunction` | A real runtime implementation is selected; old private name is not a contract | REPLACE: renamed helper, missing key and wrong signature checks in `driver/float64.rs` |
| `F64_ToF32_MatchesHost` | Correct narrowing of1.5,-2.5,0.1 | PORT: `Narrow0..2`, raw Rust binary32 expectations, both modes/cartridges |
| `F64_ToF32_RoutesThroughRealSoftFloatFunction` | Runtime dispatch rather than a required method name | REPLACE: renamed helper, missing key and wrong signature checks |
| `F32_RefParameter_ReadsAndWritesThroughIndirection` | A byref write changes the caller's float; CIL managed-pointer semantics | PORT: existing `Float32::Reference` / `driver/float32.rs`,1.5+2.5→4.0 in both modes/cartridges |

These five source methods preserve all10 original parameter-expanded cases and
58 expanded assertions. The byref case was already executed by the Rust suite;
this audit closes its source traceability without inventing a new test. The
float group is now19/22 methods,61 original cases and400 expanded assertions.
Only the three binary64 integer-conversion methods remain open in this group.
No legacy definitions are physically removed yet. Direct unsigned64→binary32
rounding is unchanged; internal-F precision requests are restricted below.

Until integer↔binary64 conversion is implemented, `conv.r.un` requires an
explicit following `conv.r4` (intervening nops are allowed). Other internal-F
shapes, including Roslyn's `conv.r.un;conv.r8`, diagnose rather than first
rounding to binary32 and then widening. `uint`16777217 must never silently
become double16777216. Native negative fixtures cover both32/64-bit unsigned
inputs, and direct-CIL cases cover bare/nop-separated precision requests.
This is an explicit temporary unsupported shape, not malformed metadata or a
retired C# capability. Integer binary64 conversion remains unfinished.

Verification:688/688 selected compiler cases passed in417.433s (292 unselected),
including332 binary64 ROMs and11913 managed numeric observations. After adding
the unsigned-precision guard,17/17 affected native-conversion and direct-CIL
cases passed in146.528s (426 unselected). The follow-up preserves the direct
unsigned64→binary32 counterexample and checks both32/64-bit unsigned-to-double
diagnostics. Workspace all-target/all-feature checking, separate rustdoc and
formatting passed. Independent reviews found no confirmed defect in the casts
or precision guard. Full migration and cross-platform acceptance remain open.
