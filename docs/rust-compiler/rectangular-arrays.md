# Rectangular-array requirements

Status: not implemented. The current native compiler rejects rectangular arrays
explicitly. Managed CI for6eda360, run43799726853/job133076706253, reaches
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
