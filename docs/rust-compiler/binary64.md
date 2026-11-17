# Binary64 core

Status: constants, bit transport, addition/subtraction, negation, comparisons and
eight-byte storage/calls and division are implemented. Multiplication and numeric
conversions remain explicitly unsupported pending independently correct runtime
lowering. This is not complete `double` or general .NET floating support.

## IR and ABI

`F64`/`Scalar::F64` retain the nominal floating identity and64-bit width. Only
`I64`↔`F64` raw reinterpretation is allowed; integer arithmetic, integer numeric
conversion, mismatched widths and pointer fabrication do not become valid float
operations. Typed indirect loads/stores, fields, local/byref values and vector
arrays carry all eight bytes. `System.Double` primitive resolution uses the same
authenticated framework identity rules as integer and binary32 storage.

Existing integer64 argument/return storage moves the raw bits. Metadata keys
`f64.add`/`f64.sub`/`f64.div` require `ulong(ulong,ulong)`, while `f64.cmp` requires
`byte(ulong,ulong)` and the equal/less/greater/unordered0/1/2/3 contract. Calls
compile the ordinary reachable library CIL; no host floating execution occurs in
production. Negation flips bit63. Value/fused-branch comparisons use the same
ordered/unordered rule as binary32, including the NaN-sensitive inversion of
`bge`/`ble`. The ABI and canonical ELF32/SM83 object format are unchanged.

`conv.r8` on an existing `F64` is bit-preserving. Other numeric `conv.r8`,
binary64→binary32/integer casts and mixed nominal precision currently diagnose.
This prevents successful casts from silently going through the current binary32
domain. Subsequent precision conversion work must preserve the direct unsigned64
to binary32 halfway-above test; a binary64 intermediate can change that result.
Those shapes are valid CIL but currently unsupported, not malformed metadata.

## Runtime correction

The old `AddF64` returned0 for raw1+raw1 in both the CLR and an independently
executed native ROM, rather than the expected raw2. The corrected helper uses
effective exponent1 without an implicit bit for subnormals, stops normalization
at that exponent and emits exponent0 when the rounded significand remains
subnormal. Separate NaN/infinity/zero handling preserves signed-zero semantics
and quiets arithmetic NaNs. The53-bit significand plus three rounding/sticky bits
fits in64 bits, including the addition carry. This requires no `UInt128` or new
general compiler integer type. The binary64 multiplication routine still uses an unverified path and is not
selected. Division now uses the independently bounded implementation below.

Independent [Rust binary64 arithmetic](https://doc.rust-lang.org/core/primitive.f64.html)
provides numeric observations consistent with ECMA-335 I.12.1.3. Fourteen boundary
pairs and2048 deterministic exponent-stratified additions cover gradual underflow,
halfway rounding, cancellation, signed zeros, overflow, infinities and NaNs. NaNs
are classified as quiet without freezing a host sign/payload. Sampled checks and
the significand/bounds argument are not exhaustive verification of every pair.

## Native tests and source disposition

`driver/float64.rs` runs41 entries in Debug/Release × DMG/CGB (164 ROMs): actual
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
The float disposition group now covers13 of22 methods,47 original
parameter-expanded cases and310 explicit expanded assertions. No legacy source
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
acceptance. Binary64 multiplication and numeric conversions remain
explicitly unsupported.

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
