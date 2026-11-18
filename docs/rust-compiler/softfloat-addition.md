# Corrected binary32 runtime addition

The inherited integer runtime returned raw0 for the addition of raw1 and raw1,
although those inputs are the smallest positive binary32 subnormals and their
sum is raw2. The same failure occurred in a Rust-compiled ROM and in managed
execution. NaN classification also failed independently in an exponent sweep.
Neither the old compiler nor the runtime's previous finite-only comments define
correct floating semantics for the new compiler.

## Selected correction

Keep the existing bit-pattern API and ordinary referenced CIL implementation.
Treat exponent-zero operands with effective exponent1 and no implicit leading
significand bit; reserve that bit for normal values. Stop left normalization at
effective exponent1 and encode an exponent-zero result when its rounded
significand remains subnormal. Retain guard/round/sticky bits and ties-to-even
rounding, including rounding carry into the next exponent or infinity.

Handle infinities and NaNs before finite arithmetic. Opposite infinities produce
a quiet NaN; NaN operands are quieted. Exact zero keeps the negative sign only
when both summands are negative. NaN payload/sign equality with the host is not
required: the tests assert a quiet NaN instead of copying a host-specific bit
pattern. The [official Rust binary32 contract](https://doc.rust-lang.org/std/primitive.f32.html)
documents ties-to-even arithmetic, signed-zero sums and variable NaN patterns;
Rust arithmetic provides the independent test oracle.

Preserving flush-to-zero would embed a known numerical error into future CIL
lowering. Rejecting all subnormals would discard useful representable values.
The local correction preserves the API and existing game library while adding
the missing numeric behavior, without another runtime framework or dependency.
The compiler and emulator implementations are unchanged.

## Execution evidence and limits

Fourteen extra input pairs cover positive/negative subnormals, transition to
normal, cancellation to the smallest subnormal, both zero signs, infinities,
quiet/signaling NaNs, finite overflow and halfway rounding on even/odd mantissas.
Together with six original finite pairs, four rstest configurations execute80
actual Roslyn→Rust→ELF→banked ROM programs in the existing emulator. Every
non-NaN result bit, NaN classification/quieting, actual MMIO, completion and
frame/heap/bank guards is checked.

Managed tests retain all145 original numeric assertions and add14 boundary plus
256 deterministic exponent-stratified pairs, totaling415 numeric observations.
The sweep covers each exponent with sampled significands/signs; it is not
exhaustive verification of every operand pair. Read-only numerical review found
no confirmed blocker in normalization, rounding, cancellation or special values.

Before the correction, all3 focused cases fail in17.504s, exit100
(`/tmp/koh-softfloat-ieee-red.log`), including the actual ROM's raw0-versus2
discriminator. After the correction, all14 runtime cases pass14/14 in64.421s,
exit0 (`/tmp/koh-softfloat-ieee-first.log`).
After formatting, final14/14 passes45.734s, exit0
(`/tmp/koh-softfloat-ieee-final.log`); Rustfmt/CSharpier checks pass. Baseline
15d0cff hosted run43968285185 separately passes1373/1373 in1953.920s with zero
skips, rustdoc and .NET fast tests successful. That baseline predates this fix;
new-head hosted/cross-platform validation must be checked separately.

This is AddF32 runtime validation, not completed float/double CIL lowering or
runtime-key dispatch. Other software arithmetic/conversion routines still need
their own numeric-domain audit and edge tests before general floating support
can be claimed. No additional legacy method is counted complete by this fix.
