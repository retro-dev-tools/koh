# Floating remainder

Binary32 and binary64 CIL `rem` now select `f32.rem` and `f64.rem`, with raw
`uint(uint,uint)` and `ulong(ulong,ulong)` signatures. Runtime method names are
not compiler conventions: renamed helpers compile, while missing keys and wrong
return signedness produce member/IL diagnostics. The helpers are ordinary
reachable Koh.GameBoy CIL compiled by Rust; no host floating runtime is used in
production. Integer remainder and invalid floating `rem.un` are unchanged.

## Semantics and arithmetic bounds

The [C# remainder contract](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#12134-remainder-operator)
uses a quotient truncated toward zero. This differs from nearest-integer IEEE
remainder:7%2 is1. A finite residual has the dividend's sign; an exact multiple
preserves that sign on zero. Finite values modulo infinity retain their bits;
infinity modulo anything, or a zero divisor, produces NaN. NaN operands produce
quiet NaNs without fixing an unspecified host payload or sign.

`RemF64` classifies special values before normalization. For finite nonzero
magnitudes it normalizes53-bit significands, aligns their exponents through
binary long division and retains only the residual. Before each subtraction the
residual is below twice the normalized divisor; subtracting once and shifting
keeps intermediates below54 bits. The maximum exponent gap is2097. No floating
quotient is formed, so an overflowing quotient does not lose the remainder.

The residual is exact integer arithmetic. A nonzero result is a multiple of the
minimum binary64 subnormal, so final normalization cannot require an exponent
below-51 in the biased representation used here. The subnormal encoding shift
is therefore at most52, and no rounding step is needed.

`RemF32` widens both operands exactly, calls the same core and narrows the exact
residual. This is safe specifically for remainder: if the dividend is smaller,
the result is the original representable value. Otherwise, at the divisor's
scale the integer residual is smaller than its24-bit significand and remains
an exact multiple of the binary32 minimum subnormal. It is representable in
binary32, so widening and narrowing introduce no second rounding. This argument
does not justify using binary64 intermediates for other binary32 operations or
integer conversions.

## Native behavior and source disposition

Actual Roslyn Debug/Release × DMG/CGB fixtures add17 remainder entries per width:
136 ROM executions. They cover signs, exact multiples, zero, subnormal operands
and residuals, normal transitions, maximum exponent gaps, infinities and NaNs.
The maximum-finite binary64 value modulo three minimum subnormals returns raw2
within the unchanged two-million-instruction harness limit. Independent integer
modular arithmetic also justifies that vector:
`(2^53-1) * 2^2045 mod 3 = 2` in minimum-subnormal units.

The complete native arithmetic tables now run65 binary32 entries and159
binary64 entries in each configuration:896 ROM executions. Both check exact CPU
completion/results, stack restoration, every surrounding heap byte and the CGB
bank guard; array cases retain their complete expected payloads. Portable PDB
locations pass through canonical ELF and linked KDBG as before.

Managed numeric observations add20+256 binary32 and20+2048 binary64 remainder
pairs against independent Rust `%`, with quiet-NaN classification instead of
host payload equality. The full numeric oracle groups contain16433 observations.
Sampled checks and the bounds argument are not exhaustive verification of every
operand pair.

| Source test | Behavioral requirement | Native disposition |
| --- | --- | --- |
| `CilFloatTests.F32_Rem_NoMatchingRuntimeKey_IsADiagnosticNotAMiscompile` | Missing runtime support diagnoses rather than treating float bits as integers | PORT retained: remove the `f32.rem` key explicitly and assert its member/IL diagnostic in `driver/float32.rs` |

The shipped library now provides remainder, so its previous lack of a matching
key is an obsolete fixture assumption. The missing-provider requirement and its
two original assertions remain, with successful numerical execution added. No
legacy source method is deleted or newly counted complete by this increment:
the ledger remains159/565 complete,406 outstanding; the float source group
remains22/22 methods,74 original cases and504 expanded assertions.

Verification: the initial native run passed8/8 cases in198.466s. After adding
the oracle batches and metadata-rename checks, the broader selection passed
714/714 in518.748s (292 unselected). Workspace all-target/all-feature checking,
separate rustdoc and formatting passed; the existing unused `cubes` test helper
warning remains. Independent review found no confirmed defect or important
coverage gap. These local Linux results do not establish full migration or
current-head cross-platform acceptance.

```sh
cargo nextest run -p koh-compile --test driver \
  -E 'test(float32::roslyn_) | test(float64::) | test(rem_)' --locked
```
