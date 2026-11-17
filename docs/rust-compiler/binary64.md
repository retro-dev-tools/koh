# Binary64 core

Status: constants, bit transport, addition/subtraction, negation, comparisons and
eight-byte storage/calls are implemented. Multiplication/division and numeric
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
`f64.add`/`f64.sub` require `ulong(ulong,ulong)`, while `f64.cmp` requires
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
general compiler integer type. Existing binary64 multiply/divide routines still
use unverified paths and are not selected by this increment.

Independent [Rust binary64 arithmetic](https://doc.rust-lang.org/core/primitive.f64.html)
provides numeric observations consistent with ECMA-335 I.12.1.3. Fourteen boundary
pairs and2048 deterministic exponent-stratified additions cover gradual underflow,
halfway rounding, cancellation, signed zeros, overflow, infinities and NaNs. NaNs
are classified as quiet without freezing a host sign/payload. Sampled checks and
the significand/bounds argument are not exhaustive verification of every pair.

## Native tests and source disposition

`driver/float64.rs` runs23 entries in Debug/Release × DMG/CGB (92 ROMs): actual
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
The float disposition group now covers12 of22 methods,46 original
parameter-expanded cases and307 explicit expanded assertions. No legacy source
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
acceptance. Binary64 multiplication, division and numeric conversions remain
explicitly unsupported.
