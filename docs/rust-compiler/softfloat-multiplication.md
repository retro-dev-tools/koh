# Binary32 multiplication

CIL `mul` on two `F32` operands uses the existing checked `f32.mul` metadata
contract and ordinary reachable managed CIL helper. Float/int mixed operands and
integer-only variants still reject. The Rust compiler supplies all lowering,
verification and SM83/ELF generation; no managed compiler or host float evaluation
participates in production compilation.

The old library helper flushed all subnormal operands/results to zero and did
not classify infinities or NaNs. The same raw1 × 1.0 probe returned raw0 in both
the CLR and a Rust-compiled ROM. Independent binary32 arithmetic requires raw1.
Changing that behavior is a numeric bug correction, with no public API change.

The corrected helper handles NaNs/zero/infinity before finite arithmetic. Nonzero
subnormals normalize their integer significand and reduce their effective
exponent. Two normalized24-bit significands yield an exact48-bit integer product.
Its leading bit determines the ordinary rounding shift23/24. A subnormal result
increases that shift before rounding, so normalizing and gradual underflow do
not introduce two rounding steps. The remainder is compared with the exact
halfway bit; ties increment only an odd retained significand. Carry can produce
the smallest normal or infinity. Deep underflow checks shifts before64-bit CIL
shift masking could wrap the count. Zero results preserve the product sign.

The tests use independent [Rust binary32 multiplication](https://doc.rust-lang.org/core/primitive.f32.html)
as the round-to-nearest/ties-to-even oracle, consistent with ECMA-335 I.12.1.3.
Finite values and infinities are bit exact; NaNs must be quiet NaNs, without
requiring another platform's sign or payload policy. Explicit halfway probes
exercise normal ties, subnormal ties, the normal/subnormal transition and
overflow rounding. The significand/exponent argument above justifies the
algorithm independently of sampled differential results.

## Tests and limits

- `driver/softfloat_host.rs`:26 explicit boundary pairs plus256 deterministic
  exponent-stratified pairs. Original arithmetic observations remain unchanged.
  The C# host only invokes public library methods and prints raw bits; Rust owns
  every expected result and assertion.
- `driver/float32.rs`:13 actual typed CIL multiplication entries plus one raw
  helper entry, each in Debug/Release × DMG/CGB (56 multiplication ROMs).
  At this increment the full Float32 group executes116 ROMs. These assert full-width results,
  canonical ELF/linking, banked helpers, source metadata and stack guards.
- At this increment division/conversion/binary64 remained unfinished and the
  disposition ledger stayed142/565. Subsequent [division work](softfloat-division.md)
  completes the mixed arithmetic rows; conversions and binary64 remain open.

This is sampled numeric and execution coverage, not exhaustive proof over every
pair of binary32 bit patterns or a general .NET floating-point compatibility claim.

Final local `/tmp/koh-float-mul-final.log`:28/28 selected tests pass in235.656s,
exit0 (320 unselected). This includes all116 Float32 ROMs,284 comparison ROMs,
80 raw-addition ROMs,697 managed numeric observations and both SDK builds with
paths containing spaces. These SDK cases continue to exercise the existing
addition entry; multiplication execution is proved by the native driver cases.
Workspace all-target/all-feature check and separate rustdoc pass, exit0;
rustdoc currently contains no cases. Rustfmt and CSharpier checks pass.
