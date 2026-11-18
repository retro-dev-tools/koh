# Checked integer arithmetic

The Rust frontend lowers `add.ovf`, `add.ovf.un`, `sub.ovf`, `sub.ovf.un`,
[`mul.ovf` and `mul.ovf.un`](checked-multiplication.md)
for I32, I64 and Koh's16-bit native integers. Signed and unsigned variants
check representability in the result width before subsequent observable
operations. Overflow reaches the existing unhandled arithmetic fault2.
Catchable exceptions and managed-reference arithmetic remain unsupported.
Normative input/result categories come from [ECMA-335 III.3.2, III.3.65 and
Table III.7](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).
Floating operands and I32/I64 mixtures are rejected with member/IL diagnostics.
The [Microsoft API page](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes.add_ovf)
shows an ordinary-add type table including floating operands; ECMA's
overflow-arithmetic table explicitly excludes them and is the
normative basis for these diagnostics.

The underlying add/subtract wraps at its explicit IR width. Unsigned addition
overflows iff `result < left`; unsigned subtraction underflows iff `left < right`.
For signed addition the sign bit of `(left ^ result) & (right ^ result)` marks
overflow: both inputs must differ in sign from the wrapped result. For signed
subtraction the corresponding expression is `(left ^ right) & (left ^ result)`:
the inputs differ in sign and the result differs from the minuend. Typed
comparisons produce an I32 predicate for the existing effectful `RequireRange`.
The optimizer cannot discard that guard when a result is unused. No new IR
model, runtime helper, calling convention or backend instruction is introduced.

Native/I32 operand mixtures retain native16 result type. The frontend extends
the native operand to I32 according to opcode signedness, checks the wide
operation, then checks the result against native16 bounds. A result outside
I32 also cannot fit native16. A result inside I32 must still pass the final
native16 check. This avoids losing significant bits of an I32 operand before
checking overflow. Both operand orders are tested. The16-bit native width is
a documented Koh extension; general desktop-native-int compatibility is not
promised. Explicit conversion instructions retain their separately documented
truncation/range behavior.

Managed-reference overflow arithmetic remains explicitly unsupported, although
ECMA permits selected `.un` reference-offset/difference combinations. A numeric
address-overflow check alone does not establish managed-reference bounds or
lifetime. Existing typed array/reference operations and explicit unmanaged
pointer conversions remain the supported paths. This omission is not described
as malformed metadata or inferred from a desktop runtime rejection.

`tests/driver/checked_arithmetic.rs` drives real Roslyn Debug/Release input in
DMG/CGB configurations. With multiplication, each runs276 roots:184 successes
and92 faults, totaling1104 ROMs. Signed/unsigned16/32/64-bit boundary pairs cover max/min, carries,
borrows, opposite signs, zero, discarded results and loop overflow. Independent
Rust i128 arithmetic checks representability. Success checks all eight stored
result bytes, SP and the complete surrounding heap/bank guards; failure must
reach fault2 before any result/heap write. Matching Portable PDB input and final
KDBG headers are checked too.
Eight explicit byte/sbyte/short/ushort roots additionally exercise promoted
I32 arithmetic followed by checked narrowing, rather than equating small C#
storage types with native16 arithmetic.

`tests/sm83.rs::checked_cil_arithmetic_matches_wide_integer_oracles` independently
constructs CIL for all six opcodes × five operand-type combinations, including
both Native/I32 orders. Boundary pairs run before/after optimization:
870 ROMs with direct i128/u128 expectations, full returned bytes, stack balance and
reserved-memory guards. These cases exercise actual runtime inputs rather than
requiring constant-folded IR. Lowering tests reject invalid float and I32/I64
operands and verify positive checked-multiplication lowering.

The initial Roslyn regression failed on unsupported opcodeD6. After lowering,
the initial153-root C# configuration cases passed in64.772s; the32-case direct-CIL and
diagnostic selection passed in41.967s. Broader validation is recorded separately.
Independent read-only review found no confirmed defects in the bit identities,
mixed-width checks, effectful guard placement or i128 oracles.
The legacy disposition ledger stays180/565: adding a new supported operation
does not close unrelated source scenarios. Full migration remains incomplete.

The broad compiler/library/IR/lowering/SM83/checked-driver selection passed
780/780 in462.124s (416 excluded), exit0. After the late managed-reference
diagnostic was added, the complete lowering target and all new arithmetic ROM
cases passed119/119 in112.746s (519 excluded), exit0. Logs:
`/tmp/koh-checked-arithmetic-final.log` and
`/tmp/koh-checked-arithmetic-diagnostics-final.log`. Workspace all-target/all-feature
checking and separate rustdoc passed; the existing unused `cubes` helper warning
remains. The final small-storage fixture expansion is recorded below.

With the eight byte/sbyte/short/ushort roots added, all four configuration
cases passed again in66.741s, exit0:644 Roslyn-generated ROMs including the
new narrowing combinations. Log: `/tmp/koh-checked-arithmetic-small-final.log`.
No production code changed after the119-case follow-up.
Workspace/all-features nextest discovery lists1633 cases, including all41
new arithmetic/diagnostic cases with none ignored. Discovery does not establish
a full1633-case execution result.
