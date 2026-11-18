# RVA array initialization

The Rust compiler reads CLI `FieldRVA` data for ordinary Roslyn array literals.
It lowers `ldtoken` and the canonical framework
`RuntimeHelpers.InitializeArray(Array, RuntimeFieldHandle)` call, rather than
recognizing and suppressing a fixed sequence of adjacent instructions. Allocation
still creates a distinct mutable heap object; initialization copies into its
payload. A readonly array field protects its reference, not its elements. No
literal array is silently replaced by a writable reference into ROM.

The [runtime implementation](https://github.com/dotnet/runtime/blob/main/src/coreclr/System.Private.CoreLib/src/System/Runtime/CompilerServices/RuntimeHelpers.CoreCLR.cs)
independently supports the copy contract: the array must be primitive, the field
must provide RVA data, and the array's byte count must fit the source field. A
source field may be larger than the destination. SM83 uses little-endian element
bytes, so the target copies those bytes without swapping. Null and invalid-size
execution use Koh's nonreturning faults 4 and 7; exception unwinding is unsupported.

## Metadata and IR contract

`Assembly::field_data` validates static/nonliteral/HasFieldRVA flags and exactly
one nonzero data RVA. Primitive fixed-size fields and local opaque explicit-layout
value types are supported. Opaque types must have no fields or generic parameters,
derive from the canonical framework `System.ValueType`, and have exactly one valid
`ClassLayout` record. Data size is bounded to 1..4096 bytes and must fit within the
PE section's actual file-backed bytes. Missing/duplicate records, invalid flags,
class/value-type confusion and overflowing/truncated ranges diagnose.

The helper contract checks the declaring type's framework assembly name/public
key identity, namespace, method name, static calling convention, return and both
parameter types. Same-named methods from other assemblies follow ordinary reference
resolution; they do not become runtime intrinsics. Framework reference metadata is
sufficient for this specific operation; the compiler does not execute or embed the
BCL. Other method bodies continue to require normal transitive resolution.

`Type::FieldHandle(Definition)` and `Op::FieldHandle` represent compile-time metadata
identity through the CIL stack and SSA edges. They have no machine bytes or ABI
representation. Constants, arithmetic, storage, conversions and machine calls
cannot fabricate or carry them. Merges require the same definition; runtime-selected
handles, handle locals/fields/returns, method/type handles and reflection are not
implemented. Unsupported shapes diagnose. This is a symbolic metadata operation,
not a zero-valued runtime placeholder.

`Op::InitializeArray` holds the destination reference and validated immutable source
bytes. It has no result and is a memory effect. The verifier checks operand type
and payload bounds; native-integer array initialization is explicitly rejected
because a source PE's native width is not SM83's 16-bit width. Initializer payloads
are bounded to 64 KiB per lowered/verified function and 16 MiB per reachable program.
These are resource guards, not a claim that ROM0 can hold that much data.

## Emission

Literal bytes are ordinary canonical ELF ROM0 sections, separate from instructions.
Local data symbols and word relocations identify them. Equal payloads within one
function can share immutable source storage; each array allocation and copy remains
distinct. The linker owns placement/capacity. The compiler neither patches final
ROM addresses nor creates another object/data format.

At runtime, initialization checks null, selects heap bank 1, reads the element
count and checks `count <= source_bytes / element_bytes` before any payload write.
Only then is the byte count scaled and copied. Zero-length arrays skip the loop.
The caller's SVBK is restored on normal return. Interrupt safety and ROMX literal
banking remain open requirements; current code runs with interrupts disabled.

## Validation and test disposition

Four `driver.rs::rva_literals_initialize_distinct_mutable_arrays` cases compile real
Debug/Release C# with a referenced data library, then run DMG/CGB ROMs in the existing
C# emulator. Their six variants total 24 executions: 16 normal returns and eight
intentional faults. Rust asserts the full poisoned heap, untouched CGB bank 2,
guard bytes, exact results and normal-return SP. Cases cover static readonly
element mutation, two independently allocated identical literals, ten integer
element types, cross-assembly field data, partial/empty copies, source-size failure
before writes and null. IL mutations preserve instruction offsets and distinguish
the copy API from Roslyn's usual equal-sized allocation idiom.

Twelve additional CLI probes reject instance signatures, class/valuetype handle
confusion and an identically named helper under a different assembly scope.
Reader tests use real PE32/PE32+ fixtures and mutate RVA/layout/flag/base metadata;
IR cases reject invalid storage, unbounded payloads and metadata-only call ABIs.
Source review found the standalone call-verifier gap; its four regressions now
exercise both `verify` and `sm83::emit` rejection.

This is partial PORT/REPLACE coverage for literal-array scenarios in the frontend
audit, especially `CilLiteralTests.LocalArrayLiteral_*` and
`CilStaticsTests.StaticReadonlyByteArray_*`.
Mutable identity and observed bytes replace legacy representation/pass-name
assertions. No complete legacy method is retired. Real sample graphics execution,
aggregate/enum/runtime completion, source debugging and SDK integration remain
separate acceptance gates in this same PR.
