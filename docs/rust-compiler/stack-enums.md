# Enums, stack buffers and physical spill widths

This increment executes the unchanged `gb-2048-cs/Board.cs` component through the
native pipeline. It does not establish the complete graphical game's acceptance
or switch the production SDK away from the C# compiler.

## Language and storage contract

Nongeneric sealed enums deriving from canonical `System.Enum` lower to their
declared integer storage type. A single special `value__` instance field defines
the width and signedness; other fields must be static literals. All eight C# enum
underlying types are supported. Method binding retains metadata identity and
overloads; storage normalization happens after binding. Locals, parameters,
returns, static fields, array elements and indirect operations use the same
normalization. Small signed values are extended at evaluation-stack boundaries.
Typed `ldobj`, `stobj`, `initobj` and `sizeof` support these scalar shapes and
primitive pointer storage; aggregates remain a separate requirement.

Enum names remain significant for signature matching, while unboxed execution
uses their underlying storage. Array element compatibility also uses underlying
and reduced types: enum/backing-type interchange is not inherently malformed IL.
See ECMA-335 I.8.7.1 and II.14.3 in the
[sixth edition](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).

`localloc` reserves a distinct fixed buffer in the current invocation's guarded
WRAM0 frame. Its size must be statically known, at most 3840 bytes in total per
function, and the allocation site must not be in a control-flow cycle. Actual
frame capacity also includes locals, spills, ABI storage and scratch, so 3840
payload bytes are not a promise that the final frame fits. Dynamic sizes, repeated
sites and excessive capacity produce diagnostics with IL offsets. These limits
avoid changing SP during the method; general dynamic allocation is not silently
approximated by reusing one buffer. A method may contain at most 256 sites.

Buffers are zeroed before use, including when initial RAM is nonzero. A zero-byte
allocation returns native zero. Each recursive invocation owns separate buffers;
the existing guard traps before an exhausted frame writes. Native pointer lifetime
remains the unsafe C# contract; this does not make returning a pointer to expired
storage valid. Managed frame-reference escape checks remain in force.

## Physical representation

SSA integer semantics remain 32 or 64 bits, and packed call arguments and return
buffers retain those widths. A bounded monotone unsigned upper-bound analysis
allows frame spills to omit provably zero upper bytes. Unknown or negative values
keep their full width. Address-exposed locals reserve their accessible width;
possible partial writes conservatively combine the stored bound with an alias
bit mask using OR. Using max here would lose combinations of retained high bits
and newly written low bits.

Selection synthesizes zero upper bytes at full-width consumers and ABI transfers.
Signed comparisons and shifts use semantic signs and count masks rather than a
shortened spill's high bit or width. Carry chains, partial stores and conversions
preserve the same rule. This is representation selection, not a smaller C# integer
type. Consecutive last-use conversions may reuse a reserved temporary slot; edge
transfers still use simultaneous-copy storage.

The analysis improves the Board component enough to fit the existing ROM0-only
code path. It does not implement banked code or justify a performance claim
against the old compiler. ROM placement and overflow diagnostics remain owned by
the linker.

## Rust-owned evidence

`tests/driver.rs::real_2048_board_and_stack_buffers_execute` builds real Roslyn
Debug/Release fixtures for DMG and CGB. The fixture links the actual sample Board
source. Rust computes all four moves with an independent merge model and checks
the board bytes, score, state flags and unchanged input array in emulator memory.
CGB variants select bank 2 before managed bank-1 operations and check preservation.
This component test preserves the sample source and assets.

The same cases execute recursive buffers, poisoned-RAM zero initialization,
zero-byte allocation, all enum widths, enum arrays/statics/ref writes, pointer
aliases and guarded recursive exhaustion. Dynamic and oversized allocations must
fail compilation. Heap snapshots prove stack buffers did not allocate there.

`tests/sm83.rs::narrow_spills_preserve_full_width_arithmetic_and_call_results`
covers 32/64-bit arithmetic, conversions, signed ordering and semantic shift
counts with independently expected emulator results. The partial-local-alias
cases preserve high bits and carry through byte/word writes.
`tests/ir.rs` adds fixed-size acceptance and dynamic/oversized/cyclic rejection.
All assertions are in Rust and discoverable by nextest; C# is fixture/host code.
Full test-run evidence is recorded in `progress.md` after completion.
