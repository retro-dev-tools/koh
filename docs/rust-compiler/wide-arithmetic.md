# Wide C# arithmetic acceptance

`tests/driver/wide_arithmetic.rs` replaces all14 methods in
`Frontends/CilWideArithmeticTests.cs`. The baseline has28 parameter rows and five
unparameterized methods:33 executions, with639 original numeric vectors before
build-mode repetition. Every original operand/count is retained. Runtime arrays
replace the old64-bit switch workaround, and pinned heap result packets replace
writes into C800/C900, which can overlap the native activation stack.

## Independent requirements

[C# arithmetic](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#1213-arithmetic-operators)
defines unchecked modular multiplication and truncating integer division.
Signed remainder follows the dividend's sign. Shift counts are masked to five
or six bits, with distinct signed and zero-filling right shifts. The Rust oracle
uses wider signed/unsigned integer arithmetic and explicit width masks, then
serializes expected little-endian bytes. Desktop .NET execution corroborates the
results; it is not the sole oracle.

The source arrays are read through loop-carried indices. Constant shifts use a
helper with literal counts selected by runtime index; dynamic shifts read counts
from arrays. This preserves both CIL shapes without depending on the retiring
compiler's early-exit routines, register assignment or strength-reduction pass.
`compile::object` verifies IR before and after optimization. Canonical ELF is
round-tripped and Rust-linked with managed functions placed in separate banks.

Each Rust case builds actual DLL/PDBs in Debug and Release and executes every
group in the existing C# emulator. The two rstest cases select DMG/CGB. Every
successful packet must match independently expected bytes in order, then match
the other build mode. Native completion PC, returned pointer range, restored SP,
WRAM0 sentinels and untouched CGB bank2 are checked. The pointer identifies the
packet without depending on allocation order or a fixed private array header.
Fault cases must reach the arithmetic abort, not merely stop running.

## Disposition matrix

All14 methods assert useful arithmetic behavior; none tests a required C# compiler
class layout. Seven equality-only methods are strengthened because unordered
collection equivalence could accept permuted or consistently wrong output.

| Original method | Requirement / independent justification | Decision | Rust grouping |
| --- | --- | --- | --- |
| WideMultiply_MatchesCSharpInt32Wraparound |17 modular products, including zero, signs, high bytes and overflow | PORT complete | Mul32/ExtraMul32 |
| WideMultiply_DebugAndReleaseProduceIdenticalResults | Build-mode-independent ordered products, also checked against arithmetic | REPLACE complete | Mul32, explicit D/R packet equality |
| WideDivide_MatchesCSharpInt32Semantics |17 truncating signed quotients and magnitude boundaries | PORT complete | Div32 |
| WideDivide_DebugAndReleaseProduceIdenticalResults | Ordered quotients with independent expected values | REPLACE complete | Div32, explicit D/R equality |
| ConstPow2Divide_MatchesCSharpInt32Semantics |19 signed divisions by literal2/128, including negative rounding | PORT complete | Pow2 |
| ConstPow2Divide_DebugAndReleaseProduceIdenticalResults | Ordered literal-divisor results in both IL modes | REPLACE complete | Pow2, explicit D/R equality |
| ConstShift_MatchesCSharpInt32Semantics |12 original counts ×11 operands ×3 operators | PORT complete | Shl32Const/Shr32Const/Logical32Const |
| ConstShift_DebugAndReleaseProduceIdenticalResults | Ordered shift matrices with independent signed/logical expectations | REPLACE complete | Three32-bit const groups, explicit D/R equality |
| WideMultiply64_MatchesCSharpInt64Wraparound |11 products, including five-byte magnitudes and signed limits | PORT complete | Mul64/ExtraMul64 |
| WideDivide64_MatchesCSharpInt64Semantics |11 signed quotients, including five-byte magnitudes and limits | PORT complete | Div64 |
| WideMultiply64_DebugAndReleaseProduceIdenticalResults | Ordered64-bit products with modular oracle | REPLACE complete | Mul64, explicit D/R equality |
| WideDivide64_DebugAndReleaseProduceIdenticalResults | Ordered64-bit quotients with truncating oracle | REPLACE complete | Div64, explicit D/R equality |
| ConstShift64_MatchesCSharpInt64Semantics |7 original counts ×8 operands ×3 operators | PORT complete | Shl64Const/Shr64Const/Logical64Const |
| ConstShift64_DebugAndReleaseProduceIdenticalResults | Ordered64-bit signed/logical shift matrices | REPLACE complete | Three64-bit const groups, explicit D/R equality |

All groupings are within the nextest-discoverable
`driver::wide_arithmetic::roslyn_wide_arithmetic_vectors_match_ordered_rom_bytes`.
Nothing in this compiler-owned group is deferred or kept in TUnit as an owner.
The legacy definitions remain only until the entire compiler project is removed.

## Expanded coverage and corrected policy

There are37 groups per mode:25 successful and12 arithmetic faults. The successful
groups produce2275 scalar results, including every original vector, unsigned
division/remainder, signed remainder, extra full-width products, dynamic shifts
and negative/oversized counts. Across D/R × DMG/CGB this is148 ROM executions,
9100 ordered result observations and48 fault executions. These are execution
counts, not additional independent language rules or legacy method counts.

The audit corrected the native policy for signed minimum `% -1`. CIL permits an
arithmetic exception for this pair, while
[C# §12.13.4](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#12134-remainder-operator)
ties its overflow behavior to division. Koh already faults for signed minimum
`/ -1`, so remainder must also fault. The former zero result was a Rust semantic
bug, not a compatibility requirement. One shared guard now handles both before
absolute-value conversion or narrow fast-path selection. Native16/32/64 boundary
tests require fault2; ordinary signed remainders retain their numeric behavior.

The first real-C# run exposed the discrepancy; the isolated native regression
then reproduced zero where fault2 was expected (`/tmp/koh-wide-remainder-red.log`,
exit100). The corrected initial DMG D/R matrix and boundary test pass2/2 in
46.671s (`/tmp/koh-wide-remainder-green.log`). Final selected regression passes
551/551 in328.728s, exit0 (`/tmp/koh-wide-migration-regression.log`), including all
157 library,312 IR and80 SM83 cases plus both driver cases. The276 other driver
cases are deliberately unselected. Workspace all-target/all-feature check,
separate rustdoc and rustfmt pass; this is not a full compiler completion claim.
