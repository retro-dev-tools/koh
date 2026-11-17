# Integer and binary32 conversions

The compiler lowers signed `conv.r4` and unsigned `conv.r.un` from int32/int64 or Koh's
16-bit native integers into binary32. `conv.r4` on an existing binary32 value
preserves its bits. These use metadata keys `i32.toF32` and `u32.toF32`, with
checked raw signatures `uint(int)` and `uint(uint)`. Native operands are extended
to32 bits with the instruction's signedness before the call. Integer-to-float
rounding uses nearest/ties-to-even, including values above2^24 and integer limits.
The64-bit keys are `i64.toF32`/`u64.toF32`, with signatures `uint(long)` and
`uint(ulong)`. These transport all eight argument bytes before rounding once.

`conv.i4`/`conv.u4` use `f32.toI32`/`f32.toU32`, requiring raw signatures
`int(uint)`/`uint(uint)`. In-range finite values truncate toward zero. Koh chooses
clamping at the selected32/64-bit target limits and NaN→0 for unchecked overflow/NaN results.
That is an explicit target policy: ECMA-335 III.3.27 leaves those results
unspecified. It is not an assertion that every .NET runtime uses this policy.
The library's old NaN→signed-limit result is changed to match the chosen policy;
its public signatures remain unchanged.
Retaining the old NaN limits or introducing a trap were alternatives. The former
had no documented portable contract; the latter would introduce an exception
for an unchecked opcode. Explicit clamping/zero avoids host dependence and keeps
the existing finite saturation behavior useful.
`conv.i8`/`conv.u8` use `f32.toI64`/`f32.toU64`, with signatures `long(uint)` and
`ulong(uint)`. The result retains all eight bytes through calls and memory stores.

For `conv.i1`/`conv.u1`/`conv.i2`/`conv.u2` and native conversions, apply that
32-bit conversion and then the existing integer truncation/sign extension.
Values representable in the smaller destination remain exact after truncation
toward zero. Out-of-range narrow conversions therefore retain low bits, rather
than adding a separate saturation helper for each width. For example,130→sbyte
produces−126 and300.75→byte produces44. Such floating overflow is unspecified by
the CLI; these are documented Koh choices, not portable C# expectations. Native
conversion follows Koh's explicit16-bit target extension.

The sixth-edition ECMA-335 definition of `conv.r.un` produces the internal `F`
type; it does not impose the older documentation's fixed binary64 intermediate.
Unsigned integer conversion currently uses binary32 precision; integer↔binary64
casts still reject. `conv.r.un` currently requires explicit following `conv.r4`
(intervening nops allowed); other internal-F shapes diagnose until integer
binary64 conversion is complete. Binary64 arithmetic and binary32↔binary64
precision casts are separately supported without routing integers through the
wrong precision.
Checked conversions remain explicitly unsupported pending overflow handling.
Do not mistake integer/binary32 conversion support for complete floating-language
support; binary64 integer conversions and remainder execution remain unfinished.
The [binary64 core](binary64.md) supports separate eight-byte arithmetic/storage
and [precision casts](binary64.md#precision-casts).

## Wide rounding and bounds

Unsigned64-bit magnitudes normalize their leading bit to position63, retaining
the top24 significand bits and all40 discarded bits. Compare the discarded part
with2^39 and round once, including carry into the next exponent. Signed input
uses an unsigned magnitude, including the two's-complement minimum. No double
conversion participates. For example,2^63+2^39+1 must round to binary32
`5F000001`; first rounding through binary64 could instead produce `5F000000`.

Float-to-uint64 extracts the24-bit significand and shifts according to the
unbiased exponent. Values below1 truncate to zero, exponents≥64 saturate, and
remaining shifts stay in range0..40. Signed conversion clamps the magnitude at
the signed bounds before applying its sign. NaNs map to zero for both signed
and unsigned results; infinities clamp. Existing integer64 calls/return storage carry
these values without a new ABI or compiler integer type.

## Evidence and disposition

`driver/float_conversion.rs` compiles actual Roslyn Debug/Release DLLs and runs
50 entries on DMG/CGB across two groups (200 ROMs). They check signed/unsigned input distinction,
integer rounding, signed native extension, truncation, NaNs/infinities/limits,
narrowing choices, canonical ELF, banked runtime calls, full result bits and
stack guards. Renaming `FromI32` to `CastI32` proves metadata-key selection.
Missing helper keys, wrong raw argument/return signedness and checked casts must
produce member/IL diagnostics. The old temporary64-bit rejection cases are
replaced by positive input/output checks. Sixteen wide-result entries store all
eight result bytes in WRAM and assert every byte, surrounding memory and CGB
bank2 isolation, avoiding a low-word-only oracle.

`driver/softfloat_host.rs` adds43 explicit observations and1024 deterministic
observations spanning all256 binary32 exponent fields. Each unsigned/signed
conversion has an independent Rust numeric oracle. Batches stay within ordinary
Windows command-line limits. This is sampled coverage, not exhaustive proof.
Wide conversion adds48 boundary and1024 exponent-stratified observations. The
full software-host group checks3118 observations. Both sides of integer rounding
ties, signed/unsigned limits, NaNs, infinities and subnormals are included.

| Source method in `Frontends/CilFloatTests.cs` | Original checks | Native disposition |
| --- | --- | --- |
| `F32_ToInt_MatchesHost` |3.7→3,−3.7→−3,1000.25→1000, each Debug/Release | PORT: `Positive`, `Negative`, `Thousand`, exact native return bits and completion/guards in both cartridge modes |
| `F32_FromInt_MatchesHost` |0,5,−5,1000000,16777217, each Debug/Release | PORT: `FromZero`, `FromFive`, `FromNegative`, `FromMillion`, `Main`, exact independent binary32 encodings |

Both methods are cargo-nextest cases. Their8 original parameter-expanded cases
expand the four-assertion `AssertPasses` helper across two configurations:
64 assertions covering diagnostics, IR verification, completion and numeric
success. Native compile success/verification and full CPU return/guard checks
preserve those contracts without retaining the old SCX/SCY packet convention.
The completed float group now covers11 of22 methods,42 original parameter-expanded
cases and275 explicit expanded assertions. No source test or useful feature is
deleted; remaining conversions and binary64 coverage are still outstanding.

Primary references: [ECMA-335, sixth edition](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf),
I.12.1.3 and III.3.27, and the [Rust numeric cast rules](https://doc.rust-lang.org/reference/expressions/operator-expr.html#numeric-cast).

Final local `/tmp/koh-float-conversion-final.log`:619/619 selected tests pass
in218.223s, exit0 (292 unselected), including all library/IR/lowering tests,
96 conversion ROMs and all earlier Float32, comparison, software-runtime and
CLI-array groups. The software host checks2046 numeric observations in total.
Independent read-only review found no confirmed correctness defect; its direct
`conv.r4` identity coverage request is implemented in `tests/lowering.rs` for
negative zero, both NaN signs/signaling input and the smallest subnormal, before
and after optimization. Review alone is not execution evidence.

Wide regression `/tmp/koh-float-wide-final.log`:632/632 selected tests pass
in253.196s, exit0 (292 unselected), including all library/IR/lowering tests,
200 conversion ROMs and all earlier arithmetic/comparison/software-runtime/array
groups. Expanded wide metadata probes pass4/4 in24.789s. Independent read-only
review found no confirmed width, rounding, bounds or ABI defect. The148-method
disposition ledger is unchanged: these are additional native width requirements,
not fabricated legacy test closures.
Workspace all-target/all-feature check and separate rustdoc pass, exit0;
rustdoc currently has no cases. Rustfmt/CSharpier and diff checks pass.
