# Primitive arrays and bounded heap

Semantic reference: [ECMA-335, sixth edition](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf),
Partition III array instructions and integer storage rules. Koh's bank layout,
allocator lifetime and 16-bit native width below are explicit target extensions.

The native path supports mutable, one-dimensional, zero-based arrays of CLI
integer primitives: bool, char, signed/unsigned 8/16/32/64-bit integers and native
integers. Arrays retain element identity in IR; references occupy two bytes.
`newarr`, `ldlen`, integer `ldelem`/`stelem`, typed `ldelem`/`stelem` and `ldelema`
lower to allocation, length and checked element-address operations. Locals,
parameters, return values, static fields and null joins preserve array identity.
Only reachable methods contribute code or storage.

Array objects contain a little-endian unsigned 16-bit element count followed by
packed primitive elements. The reference points at the count. There is no padding;
the SM83 accesses multi-byte values as individual bytes. Empty arrays allocate
distinct, non-null two-byte objects. Every allocated byte is initialized before
the reference is returned. Element stores truncate to the declared storage width;
loads obey the CIL opcode's signedness. Native integers use Koh's two-byte ABI.

The linker places a 4096-byte WRAMX bank-1 reservation named `.koh.heap`; the
compiler does not choose its address or perform layout. A two-byte bump cursor is
part of startup-zeroed WRAM0 static storage and counts against its capacity.
Allocation checks the original int32/native length before narrowing, then the
scaled size and remaining capacity before publishing the cursor or touching the
heap. A byte array can contain at most 4094 elements. There is currently no
collection, freeing or reset; allocations live until the ROM restarts. Games
must budget their total allocations. This is an initial allocator contract, not
general .NET allocation support or a retirement of the existing runtime APIs.

Length and element access reject null. Bounds checks retain the original index
width, so negative values and 65536 cannot wrap into valid small indices.
Allocation failure aborts with fault 5; bounds failure uses fault 6; null uses
fault 4. These are deterministic nonreturning ROM faults, not managed exceptions.
Exception execution and unwinding remain unsupported.

Array access and primitive managed-reference loads/stores select WRAM bank 1,
then restore the caller's SVBK register. This also applies to element references
passed through ordinary `ref` calls. Stack/static references remain in unbanked
WRAM0. Explicit native pointers retain the caller-selected hardware bank semantics.
Interrupt execution is still unsupported; supporting it requires validation of
bank preservation and interruption during allocation. These tests do not prove
interrupt safety or ROM banking.

Allocation uses one internal machine helper per allocating function. Its CALL
return address and temporary AF pushes are included in the function's stack
capacity guard. Scratch storage belongs to the current invocation. Direct word
bounds checks avoid materializing temporary int32 booleans. General comparisons
walk operands through register-held pointers, and conversion extension bytes are
written contiguously. Stack copies may clobber flags; no emitter caller relies on
flags surviving a copy. Arithmetic carry chains still explicitly preserve flags.
Branches may inspect only the low byte of a direct comparison result, whose other
bytes are provably zero; arbitrary int32 branch conditions retain full-width tests.
Mixed native/int32 comparisons widen the native operand, sign-extending for signed
ordering and zero-extending otherwise. This is Koh's explicit 16-bit target
extension: the CLI specifies native widths of 32 or 64 bits, not a 16-bit platform.

Primitive type tokens are recognized only under the named, signed framework
assembly identities. This does not require copying or lowering the BCL. Arbitrary
user types named `System.Int32` are not primitive array storage. Missing ordinary
references continue to fail resolution. Struct, object, reference-element, nested,
multidimensional and floating-point arrays remain explicit diagnostics. RVA array
initialization (`RuntimeHelpers.InitializeArray`) is still pending; existing sample
asset arrays and broader array/runtime contracts remain migration requirements.

## Evidence and test disposition

`tests/driver.rs::primitive_arrays_are_zeroed_checked_and_bank_safe` runs real
Roslyn Debug/Release fixtures through native CLI → ELF round trip → Rust linker →
existing C# emulator, on DMG and CGB. Its ten entries per configuration are 40 ROM
scenarios: eight normal returns and 32 intentional faults. Rust asserts exact
results, balanced normal-return SP, stack guards and all 4096 bytes of each tested
WRAM bank. Banks are poisoned before startup; unused heap bytes and CGB bank 2
must remain unchanged. Cases cover all twelve primitive element types, aliasing,
ref calls, static arrays, default zeroing, null joins, empty-object identity,
negative/high lengths and indices, exact-capacity allocation and exhaustion.
Sixteen CLI invocations additionally require diagnostics for currently unsupported
array shapes. IR tests cover twelve element identities, five malformed operations
and both null/array merge orders, including optimizer verification.
Six malformed-IL cases distinguish native integer arrays from short/char arrays
despite their equal physical widths. The real fixture also carries null/array
joins through a loop and returns an element reference across a managed call.

These are partial PORT/REPLACE coverage for `CilArrayLengthTests`, primitive
portions of `CilMemoryTests` and mutable-array scenarios identified in
[the frontend audit](frontend-audit.md). Expected bytes follow CLI integer/array
semantics and SM83 little-endian memory, not old compiler output. Representation
checks apply to the documented native ABI. No complete legacy method is retired:
RVA initialization, framework allocator APIs, aggregates, multidimensional arrays,
and associated sample behavior are still required. This increment does not prove
source debugging, graphics or complete game acceptance.
