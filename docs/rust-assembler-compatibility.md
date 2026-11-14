# Assembler compatibility evidence

The C# assembler is the existing Koh behavioral reference. RGBDS 1.0.1 supplies
independent language, object-format, and linking evidence. Koh extensions retain
their documented behavior. This report covers named fixtures and workflows;
it does not establish complete compatibility with either assembler.

## Checks and classifications

`scripts/verify-rust-assembler.py` reports four checks separately:

- Seven assembly/object/link/runtime workflows, including the 2048 sample.
- An independent semantic fixture assembled by native Koh and RGBDS 1.0.1.
- Nine malformed-input pipelines checked with C#, native Koh, and RGBDS.
- Positive raw-string `Emit` fixtures extracted from the C# assembler tests.

The reviewed positive corpus has 233 fixtures: 183 exact semantic matches,
18 native rejections, and 32 other differences. The raw statuses remain in
`corpus-results.json`. Fifty reviewed classifications distinguish these expected
differences from unclassified failures; this is not a claim that all 233 match.
The extraction also inventories 115 negative or nonasserted raw literals as
excluded. Ordinary C# string literals, generated sources, and filesystem test
setups are outside this extraction.

Each entry in
[`expected-differences.json`](../tools/rust-asm-verify/expected-differences.json)
binds one exact fixture name and source SHA-256 to a rationale, regression
pointer, and decoded native graph. These graphs are reviewed **regression
baselines**, not independently generated correctness results. Classification
requires the actual graph values to remain unchanged. A changed source, unknown
difference, missing regression, changed graph, or failed required link fails the
comparison.

Rich-format-only cases must also assemble as RGBDS, preserve the recorded
placement, size, combination flags, expressions and symbols, and link to the
recorded ROM. Invalid-source classifications require independent RGBDS rejection.

All fifteen rich-format graph baselines were separately checked against decoded
RGBDS 1.0.1 objects and identical linked ROMs. Single-bank unspecified placement
was normalized to bank zero for this comparison. The six union references add
an explicit local `UNION` after `SECTION UNION` to express Koh's implicit member
scope. Floating alignment baselines use RGBDS's minimum-padding base constraint;
they were updated after an independently reproduced capacity bug was fixed.
The linked alignment regression checks a nearly full ROM where unnecessary
padding would prevent an otherwise valid placement.

## Reviewed differences

| Category | Expected behavior and evidence |
| --- | --- |
| Empty data directives | Empty `DB`, `DW`, and `DL` reserve one, two, and four bytes, including in RAM and nested local unions. C# sometimes reserves zero bytes. The independent semantic fixture asserts a seven-byte reservation. |
| Word strings and character maps | `DW "Hi"` occupies four bytes. A mapping `"de" -> $64,$65` emits `64 00 65 00` through `DW`. Native output matches independently decoded RGBDS objects and linked ROMs. |
| FOR final state | A loop ending at eight retains eight; a zero-iteration loop retains its starting value. C# often retains the last executed value or omits the symbol. The independent fixture asserts normal and zero-iteration final states. |
| Signed constants | `$80000000` has the signed 32-bit value `-2147483648`. C# can retain the same bit pattern as a positive 64-bit value. Both the independent fixture and expression regressions check the arithmetic. |
| Fill options | `PUSHO; OPT p$42; DS 1; POPO; DS 1` emits `42 00`. C# ignores the changed fill value in this fixture. The independent fixture executes the same option stack. |
| LOAD layout and restoration | Code bytes remain in ROM while the execution section reserves RAM. Labels bind to the execution section during LOAD and restore the previous scope afterward. C# object metadata can reserve zero RAM bytes or retain the wrong label section. Independent object records, linked ROMs, and LOAD PC relocation regressions check these boundaries. |
| Numeric aliases | Section-free numeric aliases are constants in the canonical graph. The deferred EQU-chain fixture preserves values `4,4,5` and `after_pad` offset five; C# tags some aliases as labels. Forward EQU chains remain a Koh extension and are not justified by RGBDS acceptance. |
| Immutable definitions | Assigning a new value to an immutable `EQU` requires `REDEF`. Repeating an immutable definition, even with the same value, is an error. This follows RGBDS; the C# assembler accepts some duplicate definitions. |
| Private label spellings | Anonymous labels and unanchored macro-local labels have different private names. Their definitions, offsets, byte output, and relocation behavior are checked. Unanchored macro-local labels remain a Koh extension. |
| Empty macro arguments | The named trailing-comma argument fixture has `_NARG = 1` and emits `01`, matching RGBDS; C# emits `00`. |

Independent migration probes assembled all 32 differing fixtures with both
native Koh and pinned RGBDS after normalizing only legacy macro/constant
declaration syntax. Thirty linked to identical ROMs. The two sources rejected by
RGBDS were the forward EQU chain and unanchored macro-local label fixtures.
Those retain Koh-specific regression coverage. Decoded symbol values and RAM
sizes were inspected as well; ROM equality alone does not validate them.

The executable independent reference is
[`reference-semantics.asm`](../tools/rust-asm-verify/fixtures/reference-semantics.asm).
It uses assertions for loop state, empty data widths, signed arithmetic and LOAD
sizes, and compares native/RGBDS linked bytes for fill options and string data.

## Explicit format and language limits

Fifteen positive fixtures require the richer output adapter: eight floating
alignment cases, six union-section cases, and one pair of case-distinct raw
identifiers. They remain explicit `.kobj` v4 errors and must succeed through the
RGBDS adapter with their graph invariants intact. UNION/FRAGMENT linking also has
an independent cross-object regression checking maximum-size union allocation,
fragment concatenation, imported addresses, and rebased labels.

The three malformed positive-reference fixtures contain an instruction outside
a section or invalid `OPT b.$FF` / `OPT b.$00` options. Native Koh and RGBDS reject
them; the C# assembler accepts them.

Koh keeps per-byte `DS count, @` evaluation: `DS 4,@` at address zero emits
`00 01 02 03`. RGBDS emits four zero bytes. `SECTION UNION` also retains Koh's
implicit first local union member. These extensions require their own semantics;
rejection by RGBDS alone does not classify them as errors.

The v4 adapter cannot preserve arbitrary relocation expressions, floating
alignment, combination flags, imports, or unresolved range constraints. It
rejects these cases. Its C# linker uses case-insensitive names, so symbol names
are restricted to ASCII and case-equivalent names are rejected. Unicode section
names remain available with conservative case-collision rejection. The canonical
graph and RGBDS adapter retain case-sensitive and Unicode names.

Raw identifier `#` is a lexical escape. Public object symbols use names such as
`ld` and `DEF`. The cross-file verifier links native and RGBDS producers and
consumers in all four combinations and checks `C3 00 40 34 12` at `$0100`.

UTC/version builtins are available. `SOURCE_DATE_EPOCH` requires a nonnegative
decimal UNIX timestamp within the supported ISO 8601 range; malformed values
are errors. Local-timezone builtins are unavailable. This identifies the pinned
language reference without claiming complete RGBDS support.

## Evidence boundaries

The sample `.kobj` path compares the complete C#-linked native and C# ROMs.
Its native `.o` ROM is independently compared with RGBDS after a temporary
source-tree copy converts legacy macro and constant declarations. Tracked sample
files remain unchanged. A failed or different C# `.o` is recorded as a reference
discrepancy only after the native/reference comparison passes, with actual linker
diagnostics retained.

The execution fixture runs the actual Koh emulator and checks WRAM `2A 2B` after
arithmetic and a call/return. The negative pipelines record exact exit codes and
diagnostics; RGBDS defers undefined symbols and JR range errors to linking.
Line-map ranges and locations are checked, while identical coalescing is not
required. UTC/version builtins, `_NARG`, and source-only macro/string definitions
are excluded from numeric symbol-table comparisons. Hosted CI and physical
hardware results remain separate evidence.
