# Narrowing and observable integer widths

The native IR keeps sub-int32 CIL evaluation values at int32. Storage conversions
carry their actual8/16-bit extent and signedness. The backend can choose smaller
physical slots only when its range proof permits them. Recreating an I8/I16 SSA
demotion pass would duplicate the old model rather than establish correctness.

The requirements are numeric: truncation observes low bits, extension preserves
the declared sign, every escaping use retains its width, and comparisons, shifts
and division retain their full semantics. Zero-extended200 is not signed-byte
minus56; zero-extended40000 remains greater than1000. Comparing a byte against300
must not silently compare against44. CIL int32 shift counts use their low five
bits even if physical storage occupies one byte.

These follow ECMA-335 evaluation-stack and integer conversion/operation semantics,
not the previous compiler's output. The independent Rust oracle uses explicit
`u32` masks, `i32` sign extension, wrapping arithmetic and numeric comparisons.
It does not interpret compiler IR or use the C# optimizer as an oracle.

## Native acceptance

`rust/koh-compile/tests/support/narrowing.rs` constructs typed graphs and defines
independent numeric expectations. Five rstest cases in `tests/sm83.rs` execute
them through canonical ELF, the Rust linker and the existing C# emulator:

- Nine arithmetic/escape/shift/division shapes at8 and16 bits,22 input tuples each.
- Nine equality/signed/unsigned/mixed-extension comparison shapes at each width.
- Direct byte-counter conditions and separately bounded wrapping loops. Results
  contain both the final byte and actual iteration count, so truncating260 trips
  to four cannot pass merely because the byte agrees modulo256.

Every expression runs with runtime parameters and constant-bound inputs, before
and after production `optimize::simplify`. The emitter always applies its actual
range legalization. All64 result bits, stack and reserved-memory guards are
checked; original and optimized graphs verify. Count guards enforce792 ROMs per
scalar configuration and56 loop ROMs:3224 total, including16 zero-divisor faults.
Successful returns check all64 bits and balanced SP; fault cases check the exact
arithmetic trap and reserved-memory guards without expecting exception unwinding.

Packed observations retain the full int32 inner sum as well as byte/word outer
results. The two-truncation graph deliberately has no wide sink. Inputs include
200/50/100,300-producing sums,40000/1000, sign/wrap/carry boundaries, and counts
8/10/16/31/32/33/40/64/negative-one.

## Source-test disposition

Source: `tests/Compiler/Koh.Compiler.Tests/Ir/NarrowPassTests.cs`.
All19 methods are **REPLACE complete**, discoverable through the five native
rstest cases above. None is deleted or deferred; legacy definitions remain until
the C# compiler/test project is removed.

| Source method | Behavioral requirement / native shape |
| --- | --- |
| `Add_DemotesWhenOperandsAreExtensionsAndResultIsTruncated` | `sum`, low-byte result after zero extension |
| `Add_DemotesToI16WhenOperandsAreExtensionsFromI16` | `sum`, low-word result including carry beyond bit15 |
| `Add_DemotesWithMixedZextAndSextOperands` | `mixed`, zero/sign-extended addends and modular result |
| `Add_DemotesWithConstantOperand` | `constant`, adding300 retains the exact low-byte44 contribution |
| `Chain_DemotesEveryLinkWhenOnlyTheOuterResultEscapesViaTrunc` | `chain`, three addends and final truncation |
| `Chain_DoesNotDemoteWhenAnInnerLinkAlsoEscapesAtWideWidth` | `wide-chain`, full inner sum and narrow outer results |
| `Add_DoesNotDemoteWhenResultAlsoObservedAtWideWidth` | `wide-sum`, simultaneous int32/word/byte observations |
| `Add_DoesNotDemoteWhenTruncatedToTwoDifferentWidths` | `two-widths`, byte and word results without a wide sink |
| `Shl_IsNeverDemotedEvenWhenOperandsAndConsumerLookEligible` | `shift`, int32 count masking beyond storage width |
| `UDiv_IsNeverDemotedEvenWhenOperandsAndConsumerLookEligible` | `divide`, unsigned quotient and preserved zero-divisor fault |
| `Compare_SltWithZextOperandsRemapsToUltAndFoldsToTheCorrectValue` | `zlt`,200/50 yields0 without signed-byte reinterpretation |
| `Compare_SltWithSextOperandsStaysSltAndFoldsToTheCorrectValue` | `slt`, the byte pattern200 means minus56 and yields1 against50 |
| `Compare_SgeWithZextI16OperandsRemapsToUgeAndFoldsToTheCorrectValue` | `zge`,40000>=1000 yields1 |
| `Compare_UnsignedPredicateStaysUnchangedForEitherExtensionKind` | `zult`/`sult`, unsigned ordering after both extension kinds |
| `Compare_EqDemotesForMatchingExtensionKind` | `zeq`/`seq`, equality after matching extensions |
| `Compare_DoesNotDemoteMixedExtensionKinds` | `mixed-eq`, identical byte patterns need not remain equal |
| `Compare_DoesNotDemoteWhenConstantDoesNotFitTheNarrowRange` | `constant-lt`, retain300 rather than truncating to44 |
| `NarrowPass_NarrowsAByteLoopCounterCompareAndIncrement` | Direct byte condition, real backedges,10 trips and bit7 boundaries |
| `Optimize_WiresNarrowPassIntoTheDefaultPipeline` | Invoke the optimizer used by `compile::reachable`, then emit/execute both forms |

The source contains73 assertion sites:14 verifier checks, five constant numeric
observations and54 implementation assertions. Native verification replaces the
verifier checks; exact44/0/1/1/0 observations remain among executed cases.
Private `changed` flags, operand identity, instruction counts, I8/I16 node widths
and named-pass wiring are replaced by semantic/hardware invariants. Some old
refusal rules are conservative policies, not universal prohibitions: another
legalization may safely narrow physical work while preserving every observation.

No source method here measures ROM size or CPU cycles. This acceptance does not
claim a performance win or close the separately inventoried measured
`CilNarrowingEndToEndTests` benchmark. That group now has separate
[production C# and calibrated cost acceptance](cil-narrowing-contracts.md),
including strict emitted-byte and T-cycle reductions for equivalent kernels.

## Evidence

The expanded selection passed299/299 in24.238s, exit0
(`/tmp/koh-narrowing-acceptance.log`), including all220 compiler unit cases,
comparison/conversion properties and allocation regressions. Source assertion
counts: `/tmp/koh-narrowing-source-assertions.log`. Final count-guard verification:
`/tmp/koh-narrowing-final.log`:5/5 passed in16.028s, exit0, including all3224 ROMs.
Workspace discovery finds1874 cases, including1697 compiler cases, zero ignored
(`/tmp/koh-narrowing-discovery.json`). Rustfmt and diff checks passed.
