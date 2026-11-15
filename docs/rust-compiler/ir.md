# Native typed IR

The native compiler uses SSA values and explicit block arguments. This is a direct
function representation, not a frontend/backend plugin API. CIL's evaluation stack
becomes values; joins pass stack values on edges. Mutable CIL locals are explicit
indexed storage operations, preserving addressable local identity for later
legalization. Promoting locals to SSA is optional optimization, not a correctness
prerequisite. A stack-machine IR would leave merge/type errors implicit; reproducing
the old C# IR would import assumptions about widths and static frames.

Managed functions retain their assembly/token definition and source IL offsets.
Generated [initializer guards](initializers.md) use the reserved FF identity tag,
not a metadata MethodDef, and retain the real constructor in `InitializerAddress`.
Generated checks associate with the triggering IL offset; helper locations are
synthetic. Block zero is the entry; its
parameters match the function signature. Other block parameters are phi values,
with one typed argument per incoming edge. Value IDs are unique per function.
Every use must be dominated by its definition; same-block instruction definitions
must precede uses. All blocks must be reachable, nonempty control paths terminate,
and an edge cannot target the entry block. Unreachable CIL is removed during
lowering, before constructing this normal form. Return values match the signature.

Initial operations cover integer constants, wrapping arithmetic, signed/unsigned
comparisons, shifts, conversions and explicit local loads/stores. Integers have
32/64-bit evaluation widths; native integers are 16 bits on SM83. Comparisons
produce CIL int32 zero/one. Shift counts are int32; execution masks counts according
to the left operand's width. Signedness belongs to operations, not stored bit
patterns. Checked arithmetic, memory, aggregates, calls and exceptions require
explicit operations and validation as lowering is completed; this initial model
does not declare those features retired or claim executable support.

Conversions first truncate to the smaller of source width and conversion width,
then sign- or zero-extend into the result type. Thus `conv.i8` of int32 -1 is
int64 -1; `conv.u8` zero-extends the int32 bit pattern. Small argument/local/return
storage has explicit narrowing operations; byte/sbyte/short/ushort/char/bool use
int32 evaluation values rather than silently acquiring 32-bit storage semantics.
Local loads require entry initialization, including an earlier entry store for
entry-block loads. The initial frontend rejects non-initlocals methods with locals
until definite-assignment lowering is implemented.

The initial lowerer builds one block per reachable CIL instruction, transporting
stack values as parameters. This simple checked form preserves every original
offset; straight-line block coalescing is needed before practical code generation.
Unreachable instructions are still decoded for encoding validity but not lowered
or used to pull in code dependencies. No exception semantics are guessed.

The verifier validates graph/definitions/types before dominance. It computes
immediate dominators over reverse postorder, avoiding a quadratic matrix of block
sets. Input limits bound blocks, values and edges. Diagnostics include method
identity, block and IL offset where applicable. There is no verifier bypass in
the intended lowering-to-codegen boundary.

This document describes the in-progress model; it is not the final supported CIL
contract or backend ABI. Semantic execution tests remain required.

## Increment plan and acceptance

1. Exercise valid joins/loops and invalid graph, dominance, type and storage uses
   in Rust tests, then implement the verifier.
2. Lower independently authored IL with stack joins, backward branches, explicit
   locals and diagnostic failures; verify every resulting function.
3. Build real Roslyn Debug/Release fixtures and interpret the resulting IR in a
   bounded test-only harness against literal mathematical results.
4. Review width/control-flow assumptions, run workspace nextest and separate
   doctests, and commit the initial lowering increment to existing draft PR #55.

The interpreter is a test helper, not a production managed runtime or substitute
for SM83/emulator acceptance. These tests begin replacement of the old
`IrBuilderAndVerifierTests` control-flow invariants and CIL arithmetic scenarios;
they do not yet retire any old DLL-to-ROM assertion. Memory and exception execution,
object/generic execution, optimization and source debugging remain required work
in this same PR. Initial integer machine emission and typed static calls are now
implemented; see [SM83 ABI](sm83-abi.md) and [calls](calls.md).
