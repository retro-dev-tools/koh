# Checked integer multiplication

`mul.ovf` and `mul.ovf.un` now lower entirely in Rust for I32, I64 and native16,
including Native/I32 mixtures in either operand order. The signedness comes
from the opcode, not the managed storage name. Overflow reaches the existing
effectful range guard and arithmetic fault2; catchable exceptions and managed
reference arithmetic remain unsupported. The input/result categories follow
[ECMA-335 III.3.49 and Table III.7](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).

I32/native inputs are extended to I64 according to opcode signedness. Their
exact product fits signed64 or unsigned64 respectively. The existing checked
conversion then verifies the original result width. C# byte/sbyte/short/ushort
arithmetic still uses normal promotion and a later checked narrowing operation.

I64 multiplication needs both words of the full128-bit product. Using the low
word alone would incorrectly accept `2^32 * 2^32` as zero. Let `B=2^32`,
`a=a0+B*a1` and `b=b0+B*b1`, with each digit belowB. The frontend constructs
ordinary typed I64 IR operations:

```text
p    = a0*b0
t    = a1*b0 + floor(p/B)
w1   = t mod B
w2   = floor(t/B)
u    = a0*b1 + w1
low  = (u mod B)*B + (p mod B)
high = a1*b1 + w2 + floor(u/B)
```

Every partial product is at most`(B-1)^2`. The carry into`t` is at most`B-2`,
so`t <= B^2-B-1`. Also`u <= B^2-B`. Thus these additions fitu64 without losing
a carry. Finally`high <= B^2-2`, including its intermediate partial sum.
The low-word halves are disjoint; shifts/masks and OR assemble them exactly.
No128-bit IR type, new backend opcode or runtime calling convention is needed.

Unsigned overflow occurs exactly when`high != 0`. For signed inputs, reinterpret
the unsigned128-bit result modulo`2^128`: subtract`b` from its high word when
`a` is negative, and subtract`a` when`b` is negative. Arithmetic right shifts
produce zero/all-ones masks for those adjustments. The result fits signed64
exactly when the adjusted high word equals the sign extension of the low word.
The true product of two signed64 inputs fits signed128, so this test covers the
whole mathematical product, including minimum×−1 and negative-result boundaries.

The extended real-C# fixture runs276 roots in each Debug/Release × DMG/CGB
configuration:184 successes and92 faults, totaling1104 ROMs across addition,
subtraction and multiplication. Multiplication adds115 roots, covering square-root
boundaries, limb carries, minimum×−1, discarded results, loop overflow and8/16-bit
checked narrowing. Expected unsigned products use Rustu128; signed products use
i128. Failure must occur before the result/heap write, with frame and bank guards
intact. Success verifies all eight result bytes, stack balance and surrounding RAM.

The direct CIL target now runs all six checked arithmetic opcodes × five operand
shapes, before/after optimization:870 ROM executions. The multiplication cases
add350 executions, including a nonzero high word with a zero low word and exact
signed-minimum results. The former unsupported-multiplication tests now verify
positive lowering; float/mismatched-type diagnostics remain. No useful assertion
was removed to obtain a passing suite.

The initial red test rejected opcodeD8. The first four C# configurations then
passed in126.641s; the50-case direct-CIL/diagnostic selection passed in66.748s.
Independent read-only review found no confirmed defects in the carry bounds,
signed correction, narrowing or test oracles. The source disposition ledger
remains180/565; unrelated legacy methods are not closed by this feature.

Final broad regression passed799/799 in548.424s (416 outside the selection),
exit0: compiler library, IR, lowering, all SM83 tests and checked arithmetic/
conversion drivers. Largest managed function was3616 bytes in all four C#
configurations, within the16KiB bank limit. This measures fixture code capacity,
not comparative compiler binary size or compilation performance. Primary log:
`/tmp/koh-checked-mul-final.log`.

The existing Rust test interpreter now reports fault2 only for a false guard;
unsupported operations, type faults and nontermination still fail. Ten additional
rstest cases exercise512 deterministic pairs each, before/after optimization:
10240 IR evaluations against standard i128/u128 products. Full-range operands,
zero/one factors, half-width products, signed patterns and−1/max factors cover
both successful and overflowing products. The complete lowering target passed
111/111 in6.722s, zero skips, exit0
(`/tmp/koh-checked-mul-lowering-final.log`). This late change is test-only;
production code is unchanged from the799-case run. Independent review found no
test-harness defect. These sampled cases complement the carry proof and actual
CPU executions; they are not exhaustive testing of every128-bit product.

Workspace all-target/all-feature checking, separate rustdoc and formatting pass.
The existing unused `cubes` helper warning remains. Workspace nextest discovery
lists1659 cases; all67 checked-arithmetic/diagnostic cases are present and not
ignored. Discovery is not a full1659-case execution result. Full migration and
current-head cross-platform acceptance remain unfinished.
