# Checked numeric casts

The native compiler accepts all20 `conv.ovf.*` and `conv.ovf.*.un`
opcodes for8/16/32/64-bit integers and Koh's16-bit native integers.
`.un` interprets integer source bits as unsigned. Floating operands are
truncated toward zero before checking the destination range; NaN and infinity
fail. Negative fractions between−1 and0 therefore convert to unsigned zero.
These rules follow [ECMA-335, III.3.28–3.29 and Table III.8](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).

Failure reaches the existing unhandled arithmetic-fault ABI: fault code2,
`__koh_trap_arithmetic` and `__koh_abort`. Catchable exceptions and checked
addition/subtraction/multiplication remain unsupported. This does not promise
general support for every expression inside a C# `checked` block.

Integer checks use explicit signed/unsigned comparisons before conversion.
Floating checks use metadata keys `f32.checkInt` and `f64.checkInt`, requiring
static signatures `byte(uint, byte, byte)` and `byte(ulong, byte, byte)`.
Arguments are raw float bits, destination width and a0/1 signed flag. Helpers
reject invalid widths/flags and classify the exact truncated magnitude.
Unchecked conversion helpers run only after the predicate succeeds.

Typed IR represents the check as effectful `RequireRange(I32)`, without a
result. Verification checks its operand definition, type and dominance;
optimization preserves the check even when its converted value is discarded.
Emission reads the predicate's actual legalized storage width. An initial
four-byte assumption incorrectly read adjacent bytes of compact predicates;
native boundary tests exposed and now cover that defect.

`tests/driver/checked_casts.rs` drives209 scenarios in each Debug/Release ×
DMG/CGB configuration:132 successful values and77 faults, totaling836 ROMs.
Checks include all returned bytes, unchanged heap on failure, frame guards,
normal-return stack balance and CGB bank isolation. Metadata rename, missing
key and argument/return signedness probes test the runtime contract.
`tests/ir/range_guards.rs` checks invalid IR and optimizer preservation;
`tests/lowering.rs` independently exercises all20 opcodes before/after
optimization. The Rust-owned managed runtime oracle adds214 range observations.

The legacy disposition ledger remains159/565 complete: these new boundary
tests do not close unrelated legacy scenarios. Full migration is incomplete.

Local regression:747/747 selected tests passed in658.402s, exit0;
292 tests were outside the explicit selection. The selection includes compiler
library, IR and lowering tests plus floating, software arithmetic, primitive
array and checked-cast driver cases. Native cast execution covers836 ROMs;
the complete managed numeric oracle now checks16647 observations.

```sh
cargo nextest run -p koh-compile --lib --test ir --test lowering --test driver \
  -E 'binary(koh_compile) | binary(ir) | binary(lowering) | test(float) | test(softmath) | test(primitive_arrays_are_zeroed_checked_and_bank_safe) | test(checked_casts::)' --locked
```

Workspace all-target/all-feature checking and separate rustdoc passed. Rustfmt
and CSharpier passed (four C# files). The workspace check retains the existing
unused `cubes` test-helper warning. Hosted validation must run on the new head;
this local selection does not establish full migration or cross-platform proof.
