# Rectangular-array requirements

Status: implemented and locally validated; whole migration remains incomplete.
Managed CI for6eda360, run43799726853/job133076706253, reached
`World::IsWalkable IL_0013` in the actual JRPG sample and rejects its rank2
`byte[,]`. This is an ordinary language/storage requirement, not evidence that
the current framework API is a design convention. Flattening the sample merely
to evade missing array semantics would conceal this migration gap.

The baseline `CilMdArrayTests` contains two methods/four Roslyn cases. Its
rectangular case checks initialized3×4 data through a field and parameter,
runtime3×5 allocation, independent row/column writes and zero initialization.
Its string-array case checks literal/runtime reference arrays, array length,
string length and character reads through parameters. The latter uses SZARRAY,
not multidimensional storage, and must retain separate traceability.

The old test's private global initializer bytes and automatic ROM folding are
implementation assumptions. A readonly array field prevents reference
reassignment, not element mutation. Native acceptance must test ordinary mutable
array behavior, not reproduce a legacy readonly-array placement decision.

Before implementation, preserve these independently justified requirements:

- Recognize CLI array signatures and the array type's `.ctor`, `Get`, `Set` and
  `Address` member references. These are runtime-provided array methods, not
  ordinary definitions to resolve in a managed runtime. Validate their exact
  signatures, receiver shape, calling conventions and use of `newobj`/calls.
- Preserve rank and element storage through locals, parameters, returns, static
  fields, struct/class fields, null joins and typed indirect reference cells.
  Equal pointer widths must not erase rank or element identity.
- Use row-major indexing with the rightmost dimension varying fastest. Check
  every original signed index against its own dimension before computing a
  linear address. Negative/oversized indices must not become valid after16-bit
  truncation or allow a row overflow to alias the next row.
- Validate dimensions and the full scaled allocation size before publishing heap
  state. Zero-sized dimensions still require validation of all other dimensions.
  Capacity and dimension representation limits must fault explicitly, including
  products that overflow a host or target arithmetic width.
- Copy RVA initializer payloads into fresh mutable storage. Validate payload
  extent against the product and element width; do not include target headers
  in managed initializer bytes. Zero all allocation bytes before publication.
- `Length` is the product; dimension queries validate their dimension index.
  Preserve zero lower bounds for the chosen initial subset. Nonzero lower bounds,
  unsupported ranks/shapes and generic elements require explicit diagnostics,
  not guessed SZARRAY semantics or silent rank reduction.
- Keep managed access bank1-safe and restore SVBK, including element byrefs,
  calls and reference-bearing values. Reuse the checked heap and normal calling
  ABI; do not create an array-only allocator or call convention.

Native tests must compile actual Debug/Release C# and execute on DMG/CGB. Port the
source cases with exact independent observations, then add per-dimension bounds,
empty dimensions, checked capacity/product failures, aliases/mutation, null,
typed rank mismatch and transitive referenced-method cases. Inspect actual JRPG
progress again after implementing these semantics. This document does not close
either source method or establish JRPG gameplay acceptance.

## Native implementation under validation

Vector and rectangular references retain distinct typed identities, including
reference cells and fields. Rank2 zero-based arrays use the normal checked heap
and calling convention. The six-byte header stores total element count, rows,
and columns as little-endian words, followed by row-major element storage.
Dimensions must each fit an unsigned16-bit word, even when the other dimension
is zero. The scaled payload and header must fit the existing4096-byte heap.
These are explicit target capacity limits, not claims about CLR array limits.

Runtime-provided constructor/Get/Set/Address member signatures are validated.
System.Array length, rank, dimension and bound queries retain null and dimension
checks. RVA data is copied into writable heap storage even for readonly fields.
Index checks precede truncation and validate each axis independently.

The actual Roslyn fixture `RectangularArrays` and Rust driver cover mutable
aliases/initializers, null references through calls and byref cells, zero-sized
dimensions, distinct empty allocations, dimension/long/bound queries, and
borrowed int element references. All16 Debug/Release × DMG/CGB cases passed in
88.861s (`/tmp/koh-rectangle-expanded.log`, exit0). Another four configuration
cases execute13 fault probes each: negative/oversized indices, per-axis bounds,
invalid query dimensions, negative/oversized dimensions with an empty other
axis, capacity/product overflow, and null-before-index/query checks. All52
probes passed in30.212s (`/tmp/koh-rectangle-faults-corrected.log`, exit0).
The shared-allocator regression ran353 tests:349 passed, four failed solely on
the previous blanket rank2 rejection assertion. That assertion is replaced by
execution returning4, an exact zeroed payload/header snapshot and bank checks;
rank3 remains explicitly rejected. Allfour corrected cases passed in49.521s.
The original run was not green.

Four additional aggregate/reference cases pass25.692s: copied value elements
retain independent64-bit scalar fields while sharing object and nested-array
referents, element byrefs mutate the original, and all untouched references and
wide values start zero. Two metadata cases pass11.500s, each rejecting static
calling conventions and non-int32 dimensions for constructor/Get/Set/Address
members with member/IL diagnostics. Mutation positions are checked against exact
Roslyn signatures, including the separate aggregate signatures in the fixture.

An ordinary Release SDK build of the unchanged JRPG sources now passes map
lowering and fails in `Villager::Interact`, at IL_0005 of method0x0600002a:
`Reference class has no base type`. `/tmp/koh-jrpg-rectangle-build.log` records
exit1 after45.16s. This is the next interface/delegate dependency, not successful
JRPG compilation or gameplay. The sample describes itself as a north-star;
its intended behavior remains an explicit migration target.

Final selected30-case run passes183.953s, exit0. Added16/64-bit scalar width
probes and the source fixture's pre-alias55 read pass8/8 in46.495s, exit0.
Review found no production defect; its coverage requests are addressed by
nonzero-bound, explicit-size and malformed System.Array query mutations. Both
configuration cases, containing13 metadata mutations each, pass11.361s, exit0.
Logs: `/tmp/koh-rectangle-final.log`, `/tmp/koh-rectangle-widths.log`, and
`/tmp/koh-rectangle-shape-query.log`. These are selected local results, not
final full-workspace or cross-platform acceptance for this head.
