# Binary32 division

CIL `div` on two `F32` values selects the checked `f32.div` raw-bit helper;
the Rust compiler lowers its ordinary CIL body through verified IR and SM83/ELF.
Integer division retains its existing checked semantics. Float division by zero
produces signed infinity, or NaN for zero/zero, without an integer-divide trap.
Unsigned-only CIL arithmetic variants still reject float operands.

The previous helper returned raw0 for raw1 / 1.0. Both managed execution and a
Rust-compiled ROM reproduced that result; the independently expected result is
raw1. Subnormal flushing and missing NaN/infinity classification are corrected
numeric bugs, not compatibility requirements. The public helper API is unchanged.

## Rounding argument

Handle NaNs, infinities and zeros before finite integer division. Normalize both
nonzero significands and account for each shift in its effective exponent. If
the numerator significand is smaller, double it and reduce the result exponent.
Their ratio is then in [1,2).

For a normal result, scale the numerator by23 bits. For a subnormal result,
reduce this scale by the underflow distance before dividing. Numerators occupy
at most48 bits and denominators at most25 bits. The only negative scale requiring
division is -1: double the denominator instead. Smaller scales have magnitude
strictly below half the smallest subnormal and round to signed zero. This avoids
negative/oversized CIL shift-count masking.

Integer quotient and remainder are exact. Compare twice the remainder with the
denominator; increment on greater-than-half or on a tie with an odd quotient.
Rounding occurs once, including underflow. A rounded subnormal may become the
smallest normal. No existing compiler output is used as an oracle.
[Rust's binary32 arithmetic contract](https://doc.rust-lang.org/core/primitive.f32.html)
supplies independent round-to-nearest/ties-to-even results, consistent with
ECMA-335 I.12.1.3 and III.3.31. NaNs are checked as quiet NaNs, without requiring
another platform's payload or sign choice.

## Execution evidence and limitations

`driver/softfloat_host.rs` adds26 boundary pairs and256 deterministic
exponent-stratified pairs. `driver/float32.rs` adds15 typed division entries and
one raw helper entry, run in Debug/Release × DMG/CGB (64 division ROMs). Three
additional entries preserve the exact legacy rounded-addition and subtraction
inputs. The full Float32 group now runs192 ROMs, including canonical ELF/linking,
banked helpers, source metadata, full-width results, normal completion and stack
guards.

Remainder remains explicitly unsupported and identifies `f32.rem` with member/IL
context. Numeric conversions and binary64 remain unfinished. This is sampled
coverage plus an independently derived algorithm, not exhaustive verification of
every binary32 input pair or general .NET compatibility.

## Additional source dispositions

All source methods below are in `Frontends/CilFloatTests.cs`; their native cases
are discoverable through cargo-nextest under `driver/float32.rs`.

| Source method | Behavioral contract / independent oracle | Disposition |
| --- | --- | --- |
| `F32_Arithmetic_MatchesHost` | All9 original parameter rows: add1.5+2.5 Debug/Release, add0.1+0.2, subtract3.5−1.25 and1−4, multiply2×3 and−1.5×4, divide7/2 and1/3; independently expected full binary32 results | PORT: `Main`, `RoundedSum`, `PositiveDifference`, `OriginalDifference`, `Product`, `NegativeProduct`, `Quotient`, `RepeatingQuotient`, all in both modes/cartridges |
| `F32_Add_RoutesThroughRealSoftFloatFunction` | Originally one private helper-name assertion; useful intent is executable software addition with referenced code | REPLACE: exact native results and metadata-selected helper renaming prove behavior without freezing the helper name |
| `F32_Arithmetic_DebugAndReleaseProduceIdenticalObservableState` | Rounded0.1+0.2 must complete with the correct result in both configurations | PORT: each mode independently returns3E99999A at the normal exit with intact guards, stronger than equality alone |
| `F32_Rem_NoMatchingRuntimeKey_IsADiagnosticNotAMiscompile` | Unsupported remainder must return an error naming `f32.rem`, never a ROM with invented values | PORT: actual5.5%2 CIL rejects with member/IL and operation identity |

These four methods close3 PORT/1 REPLACE dispositions. The arithmetic method's
shared `AssertPasses` expands to36 original assertions: no diagnostics, verified
IR, completion and numeric success for each of9 rows. The helper-name assertion
is replaced; the three Debug/Release and two remainder assertions retain their
behavioral meaning. Total42 applicable expanded assertions are accounted for.
Together with the previous negation and comparison groups,9 of22 float methods
now have native replacements:34 original parameter-expanded cases and211 explicit
expanded assertions (including shared four-assertion `AssertPasses` calls).
Compiler-internal helper names and SCX/SCY verdict packets are replaced by compile
success, verified output and complete CPU return/guard observations.

Final local `/tmp/koh-float-div-final.log`:610/610 selected tests pass in184.595s,
exit0 (292 unselected). This includes every compiler library/IR/lowering test,
192 Float32 ROMs,284 comparison ROMs,80 raw-addition ROMs,979 managed numeric
observations and all four CLI primitive-array configurations. This is local
evidence, not a complete current-head hosted or cross-platform validation claim.
Workspace all-target/all-feature check and separate rustdoc pass, exit0;
rustdoc has no cases. Rustfmt/CSharpier and diff checks pass.
