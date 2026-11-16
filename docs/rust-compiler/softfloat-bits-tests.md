# Software-float integer runtime test migration

Status: all23 source methods have complete native dispositions:22 PORT and one
REPLACE. Twelve rstest cases pass12/12 in53.059s, exit0
(`/tmp/koh-softfloat-first.log`). The existing15d0cff hosted full-suite run does
not include these new cases; current-increment full verification is separate.

This group covers ordinary referenced integer code in `Koh.GameBoy.SoftFloat`.
It does not establish float/double CIL lowering or KohRuntime dispatch support.
The inherited library excluded general NaN/infinity/subnormal operands. AddF32
now has a [tested edge correction](softfloat-addition.md); other routines still
need auditing. Legacy limitations are not a general IEEE contract or approved
feature retirement.

| Original CilKohRuntimeTests method | Requirement / independent oracle | Disposition and native location |
| --- | --- | --- |
| SoftFloatAddF32_LowersCleanlyAsOrdinaryReferencedCode | Resolve and include the actual referenced AddF32 body, verify its IR and link it | REPLACE, complete / `driver::softfloat_bits::referenced_softfloat_integer_body_matches_independent_ieee_results` |
| SoftFloatAddF32_ComputesTheCorrectIeeeBits | Original1.5+2.5 has raw result40800000; bounded completion writes SCYEE/SCX1 | PORT, complete / same four rstest cases; full I32 result observed externally |
| SoftFloatAddF32_DebugAndReleaseProduceIdenticalObservableState | Each IL shape independently produces the expected bits and actual MMIO | PORT, complete / Debug/Release × DMG/CGB |

The source has three methods, six TUnit assertion sites, five expanded executions
and11 expanded assertions. All are compiler-owned; none is deferred to a .NET test
project. IR function-list representation is not itself a contract, but actual
reference resolution, body reachability and observable arithmetic are retained.

Six finite input pairs cover original1.5+2.5, rounding0.1+0.2, opposite signs
−2.5+0.75, cancellation1+(−1), exponent gap100+0.5 and sticky alignment1000000+0.25.
Rust f32 arithmetic on the explicit input bit patterns supplies the independent
expected result. The old compiler and SoftFloat implementation never compute the
oracle. Target code uses only uint bit patterns and ordinary calls.

The four native cases compile Roslyn DLLs/PDBs, include the actual GameBoy
method, round-trip canonical ELF, place managed helpers in separate ROM banks,
link and execute24 ROMs in the existing emulator. All four result bytes and
actual SCY/SCX/BGP/WY are asserted, with bounded completion, restored stack/frame
guards and untouched heap/CGB bank2. No source-level verdict can mask upper bits.

## Managed runtime differential tests

All20 `SoftFloatDifferentialTests` methods are implemented as eight Rust groups
in `driver::softfloat_host`; all eight pass. Their purpose stays the
same: test the managed library, not float CIL lowering or target CPU code. A
test-only C# host invokes public SoftFloat methods and prints scalar results;
all assertions/oracles are Rust. No compiler algorithm or test framework remains
in that host.

| Source methods | Requirement / independent justification | Rust case / disposition |
| --- | --- | --- |
| AddF32_MatchesHost, SubF32_MatchesHost, MulF32_MatchesHost, DivF32_MatchesHost | Finite arithmetic, rounding, cancellation, exponent alignment | f32-arithmetic / PORT complete,14 outputs |
| NegF32_MatchesHost | IEEE sign change on ±3.5 | f32-negation / PORT complete,2 outputs |
| F32Comparisons_MatchHost | All six predicates for six pairs, including signed zeros | f32-comparison / PORT complete,36 outputs |
| ToI32_MatchesHost, ToU32_MatchesHost, FromI32_MatchesHost, FromU32_MatchesHost | Truncation, helper saturation, integer→float rounding | f32-integer / PORT complete,22 outputs |
| AddF64_MatchesHost, SubF64_MatchesHost, MulF64_MatchesHost, DivF64_MatchesHost | Same finite arithmetic obligations at64-bit width | f64-arithmetic / PORT complete,13 outputs |
| F64Comparisons_MatchHost | All six predicates and pairs, including signed zeros | f64-comparison / PORT complete,36 outputs |
| ToI64_MatchesHost, ToU64_MatchesHost, FromI64_MatchesHost | Wide truncation/saturation and rounding at2^53+1 | f64-integer / PORT complete,14 outputs |
| ToF64_MatchesHost, ToF32_MatchesHost | Exact widening, correctly rounded narrowing for all original inputs | width-conversion / PORT complete,8 outputs |

The source totals20 methods,85 expanded rows,30 assertion sites and145 expanded
assertions. Every input and all comparison assertions remain. Rust f32/f64
arithmetic and casts compute expected bits independently; neither the old
compiler nor the tested library computes expectations.

Out-of-range integer expectations test the public helper's explicit saturation.
They do not specify unchecked CIL conversion outside its defined range or make
that API a permanent compiler convention. Finite samples also do not establish
general NaN/infinity/subnormal support.

The subsequent AddF32 correction expands the group to14 cases,80 ROMs and415
managed observations. The original85 rows and145 assertions remain; additional
numeric coverage does not increase the completed legacy-method count.

Read-only review confirms all source inputs, predicates and signed-bit checks
remain. No compiler-owned case is deferred; the C# host carries no assertions.
No production compiler, GameBoy API or emulator implementation changes.
Workspace/all-target/all-feature check passes in16.78s (existing unused cubes
test-helper warning); separate workspace rustdoc passes with zero doctests.
Rustfmt and CSharpier checks pass. Hosted baseline15d0cff verification remains
pending and is not evidence that this additional test increment ran in CI.
