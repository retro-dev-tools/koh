# Integer and binary32 conversions

The compiler lowers signed `conv.r4` and unsigned `conv.r.un` from int32 or Koh's
16-bit native integers into binary32. `conv.r4` on an existing binary32 value
preserves its bits. These use metadata keys `i32.toF32` and `u32.toF32`, with
checked raw signatures `uint(int)` and `uint(uint)`. Native operands are extended
to32 bits with the instruction's signedness before the call. Integer-to-float
rounding uses nearest/ties-to-even, including values above2^24 and integer limits.

`conv.i4`/`conv.u4` use `f32.toI32`/`f32.toU32`, requiring raw signatures
`int(uint)`/`uint(uint)`. In-range finite values truncate toward zero. Koh chooses
clamping at the32-bit target limits and NaN→0 for unchecked overflow/NaN results.
That is an explicit target policy: ECMA-335 III.3.27 leaves those results
unspecified. It is not an assertion that every .NET runtime uses this policy.
The library's old NaN→signed-limit result is changed to match the chosen policy;
its public signatures remain unchanged.
Retaining the old NaN limits or introducing a trap were alternatives. The former
had no documented portable contract; the latter would introduce an exception
for an unchecked opcode. Explicit clamping/zero avoids host dependence and keeps
the existing finite saturation behavior useful.

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
The current supported expression domain uses binary32 precision. Binary64
operations still reject; no successful binary64 conversion silently loses bits.
64-bit integer conversions in either direction and checked conversions remain
explicitly unsupported pending their implementation. Do not mistake the32-bit
increment for complete floating-language support.

## Evidence and disposition

`driver/float_conversion.rs` compiles actual Roslyn Debug/Release DLLs and runs
24 entries on DMG/CGB (96 ROMs). They check signed/unsigned input distinction,
integer rounding, signed native extension, truncation, NaNs/infinities/limits,
narrowing choices, canonical ELF, banked runtime calls, full result bits and
stack guards. Renaming `FromI32` to `CastI32` proves metadata-key selection.
Missing helper keys, a wrong raw signature,64-bit shapes and checked casts must
produce member/IL diagnostics.

`driver/softfloat_host.rs` adds43 explicit observations and1024 deterministic
observations spanning all256 binary32 exponent fields. Each unsigned/signed
conversion has an independent Rust numeric oracle. Batches stay within ordinary
Windows command-line limits. This is sampled coverage, not exhaustive proof.

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
