# Strings and text

The native compiler supports literal strings, locals, static string fields,
parameters and returns, null merges/checks, reference identity, `Length`,
indexing and Roslyn's indexed string `foreach`. Both `call` and `callvirt` to
the exact trusted `System.String` accessors are recognized. Assembly identity,
declaring type, calling convention, parameter and return types must match;
same-name methods or forged framework references are not intrinsics.

Strings retain UTF-16 code units, including embedded NUL, surrogate pairs and
isolated surrogates. `Length` returns the count of units as an int32, and indexing
returns an unsigned 16-bit `char` on the int32 evaluation stack. These are the
[Length](https://learn.microsoft.com/en-us/dotnet/api/system.string.length?view=net-10.0)
and [Chars](https://learn.microsoft.com/en-us/dotnet/api/system.string.chars?view=net-10.0)
contracts. The former compiler's ASCII-only rejection and byte-per-character
ROM encoding are intentionally replaced; preserving them would change C#
semantics. GameBoy's built-in tile font remains an ASCII font, and its explicit
`(byte)` conversions still truncate characters according to C# integer rules.

## Representation and bounds

IR `String` is distinct from arrays, native pointers and managed element refs.
Its ABI value is a 16-bit reference to an immutable ROM0 object containing a
little-endian u16 unit count followed by little-endian u16 units. It has no
terminating character. Null is zero and never aliases the empty object.
`StringLiteral`, `StringLength` and `StringChar` retain method/IL provenance.
The verifier rejects array operations, fabricated nonzero constants, pointer
conversions and ordered string comparisons; reference equality and unsigned
null checks are supported. Safe CIL cannot write or take an element reference
into literal storage.

The program emitter interns reachable literals by their entire unit sequence,
in deterministic sorted order across methods and assemblies. It emits one
canonical ELF ROM0 data section and symbolic addresses, leaving placement and
relocations to the Rust linker. Standalone function emission uses its existing
local literal pool. No heap allocation, bank switch or managed runtime occurs
when accessing a string. Calls and static fields use the same reference width.

An individual object is limited to 8191 units; the combined intern pool is at
most 16 KiB. Actual code and other data share ROM0, so the linker can reject a
smaller program. Capacity errors are explicit. Null receivers trap with code4;
negative, past-end, empty and nonzero-high-word indices trap with code6 before
reading a character. Exceptions are not caught or emulated.

Content equality/other String members, allocation/concatenation/formatting, ref/out
string parameters, string arrays,
reference-bearing aggregates/classes and fixed string pointers remain
unsupported at this increment. Unknown reachable String members diagnose by
member name. These are outstanding runtime work, not a declaration that the
whole migration is complete or that useful capabilities have been retired.

## Optimizer and backend capacity correction

The unchanged actual `Video.Init` plus both text methods initially required
18355 bytes in Debug ROM0. Release fit. The fixture and capacities were retained.
Block-local forwarding replaces local loads from known preceding stores/loads;
every other effectful or trapping instruction invalidates that cache. Typed
constant reuse stays within a block. Dead nontrapping pure values and stores to
locals with no remaining reads, addresses or pin uses are removed. Addressed
and pinned locals retain storage. No assumption that arbitrary memory/calls
cannot alias local storage is used. Division, memory reads/writes, calls,
allocations, and string null/bounds checks remain observable roots even if their
results are unused. CFG verification runs before and after optimization.

One- and two-byte frame copies now use direct loads/stores. Their flags and
temporary registers were already clobbered by the old general copy sequence;
all callers were inspected. Byte arithmetic still uses the flag-preserving
helpers needed for carry chains. Full arithmetic, alias, recursion, pin and
graphics regressions must pass alongside string execution.

## Native test traceability

`tests/fixtures/Strings` is ordinary Roslyn C#; Rust driver tests build it in
Debug/Release and execute canonical ELF → Rust linker → existing C# emulator
on DMG/CGB. `Main` checks cached/returned strings, `foreach`, Latin/NUL/surrogate
units, empty/null joins, cross-method literal identity, exact byte conversions,
caller bank preservation and ref/native alias writes. Rust independently checks
the complete interned UTF-16 pool, returned value42, balanced SP and every byte
of both poisoned heap banks. `ArrayCopies` checks exact ordered `SCORE` bytes
from foreach and a pointer/string helper in separate arrays, including all heap
guards. `TextLibrary` executes actual `Video.Init`, `Text.Draw` and
`DrawToWindow`, checking exact background/window tile indices. It does not
claim full font/framebuffer or complete sample-game acceptance.

The final video cases additionally use the existing host's poisoned video mode:
Rust checks every VRAM byte, both exact text runs, untouched tile data/CGB
attributes, zero OAM and LCD/palette registers directly from emulator snapshots.

Six null/bounds programs preserve exact trap codes. Two metadata cases mutate
getter signatures, framework identity and literal tokens, check member/IL
diagnostics, reject unsupported Concat and execute direct-call variants.
IR cases check types, literal capacity/units, effect barriers, trap retention
and optimizer idempotence. All are discoverable by cargo-nextest.

Run `KOH_DOTNET=/path/to/dotnet cargo nextest run --workspace --all-features
--no-fail-fast --locked`; run `cargo test --doc --workspace --all-features
--locked` separately. See [progress](progress.md) for completed log scopes.
