# Native typed IR

The native compiler uses SSA values and explicit block arguments. This is a direct
function representation, not a frontend/backend plugin API. CIL's evaluation stack
becomes values; joins pass stack values on edges. Mutable CIL locals are explicit
indexed storage operations, preserving addressable local identity for later
legalization. Promoting locals to SSA is optional optimization, not a correctness
prerequisite. A stack-machine IR would leave merge/type errors implicit; reproducing
the old C# IR would import assumptions about widths and static frames.

`I128` represents the full bit pattern of [framework 128-bit integers](int128.md).
Signedness belongs to operations and scalar storage. Its 16-byte values are not
compressed by the backend's 64-bit range analysis. `Constant(u64)` zero-extends;
negative wide values require an explicit signed conversion or arithmetic.

`LocalAddress` produces a typed reference into stable invocation storage.
Narrow metadata storage is normalized on both local loads and stores; padded high
bytes are not an alias invariant. [Reference checks](local-references.md) reject
current-frame returns, preserve borrowed aliases and distinguish managed null from
native bus address zero. The origin analysis is conservative for local reassignment
and reference-returning calls; it is not a general scoped-reference solver.

[String](strings.md) is a distinct reference type with literal, length and checked
character operations. It cannot be used as an array, native memory address or
numeric operand. UTF-16 char results use unsigned int32 evaluation values.

[Objects](class-layout.md) carry a validated class identity, immediate base and
storage extent. `NewObject` is effectful; `Upcast` only names an immediate base
whose prefix fits the source. Lowering inserts each step for inherited views
and common-ancestor SSA joins. `CheckObject` preserves nonvirtual callvirt's null
fault even when its target does not access fields. `ObjectFieldAddress` requires
the exact owner type and an extent beyond the two-byte header, within the object.
Reference cells distinguish class, string and array storage from scalar byrefs.
Managed-reference loads require the matching cell; a native-address load cannot
fabricate an object directly or through an object array. Static identities have
one compatible reference-cell shape across the program, preventing equal-width
aliases from changing their class or numeric/reference kind. Constructors and
direct calls retain ordinary verified function signatures and source IL offsets.

Sequential value structs can contain class, string and array references, including
inline nested structs. Their identity includes a reference-storage marker;
whole-value copies retain independent scalar storage and shared reference cells.
`FieldAddress`, `ExtractField` and `LocalAddress` carry scalar/value or array
storage explicitly. Managed aggregate loads/stores require exact typed references;
raw native addresses cannot project their reference fields. `ldind.ref` and
`stind.ref` require matching reference cells. Explicit managed layouts and scoped
byref fields currently diagnose rather than guessing overlap or lifetime rules.

Field projections are trusted declarations from validated CLI metadata. The
verifier checks their types, extents and reference-storage categories, but does
not independently authenticate every offset against a canonical field registry.
Hand-authored Rust IR is not an untrusted input format or a memory sandbox;
metadata lowering must provide the actual field identity, offset and storage.

Virtual sites target generated FE functions with ordinary verified signatures.
`IsClass` tests exact descriptor identity; `CastClass` preserves null and checks
parent descriptors before returning a typed reference. Both reject numeric or
string/array operands. Checked casts remain observable even with unused results.
`TypeFault` is a terminal control operation with no successors or return value.
See [slot binding, reachability and runtime checks](virtual-dispatch.md).

Typed constant reuse and local forwarding preserve SSA dominance. Cross-block
forwarding requires the identical SSA value on every incoming path and excludes
address-exposed or pinned locals. Calls and memory writes cannot alias the remaining
private local storage. A bounded worklist computes this agreement; functions whose
block/local product exceeds one million conservatively skip that optimization.
Block-local forwarding retains its conservative effect barriers. Dead-value
removal preserves calls, memory writes, allocation and traps even when their
results are unused. Diamond agreement/disagreement and real byref mutations are
covered in `tests/ir.rs` and the ancestor-frame C# execution cases.

After local simplification, a single program pass may replace direct calls to
argument-free integer literal-return functions. The entire callee body must
contain only constants and conversions proven to leave those constants unchanged;
all effects, memory operations, branches and changed-bit conversions exclude it.
Typed results and caller IL offsets survive replacement, then local simplification
and program verification run again. This is not general inlining or an iterative
interprocedural framework. Callee symbols remain available in emitted metadata.

[Raw arena](raw-arena.md) operations are observable effects. `ArenaAllocate`
requires an int32 size and returns native16; `ArenaReset` has no operands/result.
Neither can be removed when its result is unused. Reset invalidates raw storage
only; managed arrays retain their lifetime and allocation cursor.

`MemoryFill` is an ordered raw-memory effect with native16 address and int32
value/count operands, no result. Its runtime contract narrows value/count to
8/16 bits; zero count performs no bus access. All three uses participate in
dominance, dead-value reachability and operand remapping. It is a memory-write
barrier, not a pure expression. Native pointer capacity/banking and PPU timing
remain caller-owned; see [the exact contract and tests](runtime-memory.md).

`MemoryCopy` is the corresponding ordered read/write effect with native16
destination/source and int32 count, narrowed to unsigned16. It has no result;
all three operands survive remapping and participate in use/dominance checks.
Local forwarding treats it as a memory barrier. Read/store order is ascending,
including overlapping buffers; zero count accesses neither buffer. See
[copy semantics and executable evidence](memory-copy.md).

`VBlankCopy` has the same address/count operands and an int32 copied-count result.
It is observable even when its result is unused. Operand remapping retains all
three uses; local forwarding treats it as a memory barrier. The operation also
has timing, interrupt and fault effects; see [its hardware contract](vblank-copy.md).

`HBlankStream` has native16 source, int32 first/count narrowed to unsigned8,
a validated fixed display register and no result. All three uses survive operand
remapping; the effect survives dead-value removal. See [raster semantics](raster-stream.md).

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

## Table switches

`Switch` consumes one int32 selector and chooses a zero-based case edge using its
unsigned value, or the default edge when outside the table. Every edge transports
the remaining evaluation stack with normal typed block arguments. Empty tables,
duplicate destinations and backward targets are valid. This follows ECMA-335
III.3.66 in the [sixth edition](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).
Exception-region transfers remain outside the supported execution subset.

The verifier checks selector definition/dominance/type and all edge arguments.
CFG simplification rewrites switch selectors and edges through the same aliases
and block remapping as branches. Spill liveness and upper-bound propagation
include default and case edges. Method/IL provenance remains on the terminator.

SM83 selection uses full-width equality comparisons followed by the existing
simultaneous edge transfers. It adds no indirect-call convention or jump-table
runtime. This linear selection is deliberately simple; large tables may hit the
existing ROM0 capacity diagnostic. It does not promise constant-time dispatch.

Limits are 16,384 cases per switch, 262,144 control-flow edges per IR function and
1,048,576 edge arguments. The lowerer checks cumulative argument capacity before
cloning the remaining stack into table edges. A regression retains128 values
across16,384 targets and requires the allocation-budget diagnostic. These bounds
supplement existing block/value/stack limits, rather than permitting an allocation
spike before verification.

Rust tests execute unsigned boundary selectors, remaining stack values, empty and
duplicate tables and backward targets through the C# emulator. Real Roslyn Debug
and Release fixtures assert that an actual `switch` opcode exists, then execute
an independent wrapping checksum over all cases/defaults. Invalid selector types,
undefined selectors, edge arguments and case capacity have verifier regressions.
