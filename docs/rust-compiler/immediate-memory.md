# Immediate byte-memory operands

The counted/copy loop investigation exposed stack materialization of literal
native addresses. CIL CFG joins initially hide those literals behind block stack
parameters, so folding only before CFG simplification does not expose them to
instruction selection. The optimizer now folds integer conversions after CFG
aliases and accumulator-recursion normalization are resolved. The operation masks to the
minimum source/conversion/result width, sign-extends when requested, then masks
to the result width. Inputs/results above64 bits remain on the existing path.
Propagation has a bounded64 rounds per invocation; an unresolved conversion is
retained, never replaced by an assumed value.

The first broader run exposed an ordering dependency: folding before recursive
normalization hid the return-width evidence used by its proof. Folding now runs
after that transform, preserving the existing admission/rejection tests rather
than relaxing them to accept an unexpectedly recursive or rewritten result.
That earlier900-case run passed891 and failed nine in250.523s, exit100
(`/tmp/koh-immediate-memory-regression.log`), including actual deep-recursion
stack faults. After the ordering fix, focused46/46 passed11.774s, exit0,566
explicitly unselected (`/tmp/koh-immediate-memory-ordering.log`). Existing
recursion admission/rejection and executed modular results remain unchanged.

The backend selects absolute byte loads/stores for constant raw native addresses:
LD A,(a16)/LD (a16),A, or LDH for FF00..FFFF. Managed references retain their
existing null/bank checks. Signed loads preserve int32 sign extension; stores
retain low-byte truncation. A constant address is omitted from stack emission
only when every use embeds that address directly. If it escapes through another
operand, the original value still has ordinary storage. Effects stay in source
order. The ABI, frame capacities, IR types and hardware metadata remain unchanged.

The instruction selection uses the [SM83 opcode encodings](https://github.com/gbdev/gb-opcodes/blob/master/Opcodes.json).
No memory operation is removed, reordered or widened. Omitting a constant's
machine bytes does not omit its IL marker; ordinary source/debug propagation
still applies to emitted instructions.

## Independent validation

Ten rstest conversion cases check8/16/32/64-bit truncation, signed/unsigned
extension, native C100, source-width-sensitive extension, bit63 and repeated
simplification. Surviving converted constants retain their original IL offsets.
Tests inspect the actual returned constant and its source location.

Eight machine tests execute80 ROMs across WRAM C1FF and HRAM FF90, unsigned and
signed reads, escaping and embedded-only addresses, five input bit patterns,
before/after simplification. Exact result bits and stack/memory guards are
checked. Byte memory reads correctly produce int32 IR values before any wider
conversion; the first draft's int64 load was rejected by the verifier and the
test was corrected to use the documented IR contract.

Focused41/41 passed26.001s, exit0,837 explicitly unselected:
`/tmp/koh-immediate-memory-corrected.log`. This includes existing constant-call,
raw bank-span conversion and narrowing acceptance. Marked windows, with the
same source instrumentation and runtime observer:

| Workload | Before | After |
| --- | --- | --- |
| Counted100 stores | 55588 T-cycles | 50760 T-cycles |
| Initialize/copy32 bytes | 75504 T-cycles | 71124 T-cycles |

After the ordering fix, the broader compiler library, full IR and SM83 targets,
Roslyn narrowing, Portable PDB, palette and map selection passed900/900 in249.935s,
exit0,633 explicitly unselected (`/tmp/koh-immediate-memory-final.log`). Separate
compiler rustdoc passed with zero doctests (`/tmp/koh-immediate-memory-doc.log`).
This is local Linux evidence, not a full workspace or final cross-platform run.

Constant conversion folding alone left both windows unchanged; the direct
address instruction selection is necessary for these reductions. The marked
windows include instrumentation overhead, as documented in [loop costs](loop-costs.md).
The original190/740-dot ceilings remain unmet and unchanged. This increment
closes no additional legacy source-test dispositions:560/565 remain complete,
five outstanding. Final acceptance and legacy compiler removal remain open.

```sh
cargo nextest run -p koh-compile --lib --test ir --test sm83 --test driver \
  --locked --no-fail-fast \
  -E 'binary(koh_compile) | binary(ir) | binary(sm83) | test(narrowing::) | test(pdb::) | test(palettes::) | test(bg_win::)'
```
