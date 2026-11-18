# Native metadata signatures

The signature boundary consumes CLI blobs without executing assemblies. It uses
the existing clrmeta `TypeSig`/`MethodSig` result types, avoiding another metadata
model. This is a parser and structural validation boundary; recognizing a shape
does not establish that the compiler can lower or execute it yet.

## Why an isolated parser

The pinned metadata library allocates vectors directly from signature counts,
recurses without a depth limit, and reads signed compressed array lower bounds
using a different sign rule from ECMA-335. For example, `01` encodes -64 and `7F`
encodes -1 in the one-byte signed format. Negative bounds require sign extension
based on the encoded 7/14/29-bit width, not zigzag-style integer decoding.
Wrapping its public parser would require parsing the full grammar first to bound
all nested allocations. One small bounded parser directly producing its existing
result types avoids doing that work twice or maintaining a fork of the library.

## Contract

- `signature::method`: method/call-site signatures, including function pointers.
- `signature::method_definition`: additionally rejects call-site-only sentinels,
  unmanaged/explicit-this definition conventions and generic vararg definitions.
- `signature::field`, `locals`, `type_spec`, `method_spec`: their distinct blob
  markers and type-position rules, with no ignored trailing bytes.
- Preserves integer/float/string/object/native widths, class/value-type tokens,
  pointer/byref types, generic parameters/instances, arrays and shapes, custom
  modifiers, function pointers, pinned locals and vararg boundaries.
- Ref fields are parsed, as specified by modern .NET's ECMA addendum. Pinned
  fields, void fields/elements, nested byrefs, zero-rank arrays, array shape counts
  above rank, malformed generic instances and invalid encoded type tags fail.
- 64 KiB per blob, 64 nested type levels, counts checked against remaining input
  before allocating, and canonical compressed integers. Failures identify the
  signature byte position. Assembly accessors add member/local context.
- Assembly method access also checks agreement between Static and HASTHIS flags.
  Local signatures are obtained from the validated StandAloneSig body token.

Binding still needs to validate referenced row existence, generic indices and
instantiation arity/constraints against the declaring type/method. Required custom
modifiers remain visible; lowering must honor them or reject them explicitly.
Parsing a function pointer, typed reference, vararg, ref field or an array shape
does not silently promise a supported execution implementation.

## Tests

`tests/signature.rs` contains normative compressed-integer vectors, grammar/context
cases, allocation/depth limits, and deterministic arbitrary/truncated-input probes.
`tests/assembly.rs` parses actual Debug/Release PE32 and PE32+ Roslyn signatures
for generic methods and structs, byref parameters, unmanaged function pointers,
rectangular arrays and ref structs. It asserts generic parameter/return identity,
parameter widths and representative field shapes, not just absence of diagnostics.

References: [ECMA-335 II.23.2](https://ecma-international.org/publications-and-standards/standards/ecma-335/),
[modern .NET ref fields](https://github.com/dotnet/runtime/blob/main/docs/design/specs/Ecma-335-Augments.md#ref-fields).
No legacy Koh compiler output serves as the signature oracle.
