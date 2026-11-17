# Array and UTF-8 literals

Ordinary C# array initializers create mutable arrays. The native compiler stores
their initializer payload in ROM and copies it into a fresh array on each call.
The count belongs to the runtime array header; it need not prefix the ROM payload.
This follows [C# array creation and initialization](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/arrays).

The legacy `DetectArrayLiteralIdioms` unconditionally replaced matching `newarr`
instructions with a ROM global and suppressed initialization. It checked the
initializer shape and byte count, but not later writes or identity observations.
That representation cannot be a contract for mutable arrays. The new regression
changes one invocation's element to99 and verifies the next invocation still
contains22, with both arrays and their neighbors checked in RAM. ROM promotion
would require an independent immutability and identity proof; this increment
does not introduce such an optimizer.

C# UTF-8 literals are different: their readonly storage may remain in ROM.
The [language proposal](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-11.0/utf8-string-literals.md)
places a terminator outside the view's logical length. The compiler retains the
complete Roslyn RVA blob, including that terminator, and separately carries the
length. `Koh` and `Koh\0` therefore remain distinct views. Unicode observations
use UTF-8 bytes, not UTF-16 character counts.

## Native contract and ABI

Resolved framework `ReadOnlySpan<byte>` literal views occupy six bytes: a
little-endian ROM0 address followed by an int32 element count. Their nominal
closed type identity is retained. Ordinary stack copies, arguments and memory
returns carry the whole view; the compiler does not model the host framework's
managed-reference field layout. Empty/default views have zero address and length.

`ByteSpanLiteral` names validated RVA bytes and an int32 length operand. Emission
checks that the extent fits the blob before constructing the view. `ByteSpanElement`
checks the full unsigned index against the logical length before producing a
`RomByteRef`. Negative and past-end indices take the bounds fault; unused indexing
retains that effect through optimization. The backend emits ordinary canonical
ELF literals and relocations, with layout and capacity delegated to the linker.

The readonly reference permits byte loads. The IR verifier rejects stores,
numeric conversion and fabricated constants of that type. This preserves the
[framework's readonly indexing and bounds contract](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/ReadOnlySpan.cs).
Required readonly return modifiers are resolved and checked; unknown required
modifiers diagnose rather than being discarded.

Supported members in this increment are literal pointer/length construction,
`Length`, indexing, and `Empty`. Literal views can be copied, passed and returned.
Array-backed views, arbitrary native pointers, writable spans, other element
types, `Slice`, pinning and readonly-reference locals/returns are not covered by
this contract. They remain compiler work or explicit product decisions; this
increment does not retire useful legacy span capabilities. Unsupported tested
members/shapes diagnose with member/IL context. ROM banking of these literals is
still ROM0-only and shares the linker's capacity diagnostics.

## Metadata work accounting

The real framework reference assembly exposed a false generic-budget rejection.
Constraint validation formerly charged every assembly parameter times every
assembly constraint, even for a nongeneric member. It now charges the full
parameter-table scan before selecting the owner, then each actual constraint
scan for that owner's parameters. The1048576 limit and validation rules remain;
the charge matches the work performed. Existing contradictory-flag, closed-owner
and instantiated-interface regressions are included in acceptance.

## Source-test disposition

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLiteralTests.cs`.
Native replacement: `rust/koh-compile/tests/driver/literals.rs`, with the real
Roslyn fixture `tests/fixtures/Literals` and verifier cases in `tests/spans.rs`.

| Source method | Requirement and independent evidence | Classification | Decision / cargo-nextest coverage |
| --- | --- | --- | --- |
| `LocalArrayLiteral_LandsInRomWithInitialBytesNotRebuiltAtStartup` | Original five payload bytes and reads22/55; C# mutable/fresh array semantics | Mixed numeric contract and unsafe representation assumption | REPLACE complete: physical ELF ROM payload, both runtime arrays and stronger mutation isolation; no required ROM array object or initializer-global name |
| `LocalArrayLiteral_DebugAndReleaseProduceIdenticalObservableState` | Both Roslyn modes produce the independently defined array values | Public contract | PORT complete: both agree with expected0x1637 and the complete array snapshots |
| `U8Literal_LandsInRomWithInitialBytesNotRebuiltAtStartup` | `Koh!` byte values4B/21, raw trailing NUL outside length4 | Public data/length contract plus private global naming | REPLACE complete: actual ELF bytes, relocated execution, unchanged heap and checked view length; no `__rvablob` name assumption |
| `U8Literal_DebugAndReleaseProduceIdenticalObservableState` | Both modes produce the same independently defined UTF-8 values | Public contract | PORT complete: both agree with0x4B21 and exact byte/length observations |

These four methods contain16 assertion sites,30 after their original mode
parameterization. Successful object generation enforces the diagnostic/verifier
assertions. ROM payload checks retain initialized bytes and their placement;
runtime checks retain all four numeric reads. Private global names, IR enum
values and representation-specific prefixes are replaced, not copied. No useful
case is deleted or deferred. Together with [128-bit acceptance](int128.md), all
six source methods in this group have native replacements.

Four rstest configurations cover nine successful entries and three bounds faults:
48 ROM executions across Debug/Release and DMG/CGB, checking full heap snapshots,
stack guards and the alternate CGB bank. Unicode, shared prefixes, embedded NUL,
empty/default views, cross-method value passing and mutable array isolation add
independent boundaries. Twelve unsupported-shape/member diagnostics remain visible.
Nine native verifier cases cover readonly stores/conversions, operand types,
shape/extent/definition checks, fabricated references and retained fault effects.

The initial expanded selection passed247/247 in58.591s. The final selection,
including explicit unsupported-shape diagnostics and two additional IR cases,
passed249/249 in58.278s, exit0 (`/tmp/koh-literals-final.log`). Source assertion
counts are reproduced in `/tmp/koh-literals-source-assertions.log`. This is focused
acceptance, not a full workspace or cross-platform pass.
After the final signature review, the233 unit/span/literal cases passed again
in11.791s, exit0 (`/tmp/koh-literals-reviewed.log`). Workspace discovery finds
1869 cases,1692 compiler cases, zero ignored. Separate workspace doctests completed
successfully for all four crates (zero examples), and rustfmt/diff checks passed.
