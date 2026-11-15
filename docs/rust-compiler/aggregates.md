# Unmanaged value types

The native compiler supports nongeneric unmanaged value types with sequential
or explicit layout. This increment includes actual Koh.GameBoy Sprite execution;
it does not complete the compiler migration. The SDK still uses the C# compiler.

## Layout and representation

Layout follows ECMA-335 II.10.7, II.22.16 and II.22.18:
https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf
Sequential fields retain declaration order and packing constraints. Explicit
fields retain their offsets and overlapping byte views. Default packing is8;
native integers and pointers use Koh's16-bit target ABI. A nonzero declared size
is the larger of that size and field extent, without further tail rounding.
Without a declared size, natural tail alignment applies. Empty values occupy
at least one byte. Known .NET fixture probes independently establish sizeof5
for sequential and explicit declared Size5; these exposed and corrected an
initial rounding bug. Production compilation never executes input assemblies.

Canonical identities use assembly and type definition, rather than field names
or size. The cached reader bounds values at4096bytes, fields at4096/type,
nesting at64, dependency traversal at1048576 and cache entries at16384.
Reference fields, generic shapes, AutoLayout and InlineArray diagnose explicitly.
The limits bound parsing; actual frame/ROM capacity can reject smaller programs.

IR Struct stores inline bytes; StructRef stores a typed16-bit reference.
Storage records scalar or aggregate layout. LocalAddress, LoadMemory and
StoreMemory share these widths; ZeroStruct initializes all bytes, including
padding. FieldAddress verifies owner, offset and extent; ExtractField reads a
value snapshot and preserves scalar sign extension. Numeric operations cannot
consume aggregate values. The verifier checks canonical sizes and prevents
references derived from current-frame locals escaping through returns.

## Calls and memory

Value arguments and results use the existing packed caller-owned ABI and copy
all storage bytes. Byref arguments alias their source. Value-type instance this
is a typed reference, preceding explicit parameters. Typed ldobj/stobj/initobj,
cpobj and sizeof use metadata layouts. Copies snapshot their source before
writing the destination, including overlapping storage. A managed null field
reference traps before adding its offset.

Managed field references preserve managed heap mapping and restore SVBK.
Raw Native field addresses remain Native and retain the caller's selected bank.
The compiler does not manufacture managed references from raw banked pointers.
Backend copies and zeroing above8bytes use counted loops to fit ROM0; scalar
paths retain their existing encodings. This trades execution time for code size;
no measured performance improvement is claimed. The Rust linker remains the
only authority for placement and capacity.

## Evidence and limits

Rust layout tests cover14 layout/unsupported-shape cases and9 malformed/capacity
cases, each against Debug and Release metadata. Typed IR tests cover identity,
widths, field bounds/provenance and derived-reference escape rejection.
Machine-code tests exercise5/16/64-byte values across calls, initialization,
signed extraction and null-field traps in the existing C# emulator.

Real Roslyn fixtures execute in Debug/Release × DMG/CGB. Sprite calls actual
Video.Init and library handle methods, checks every shadow-OAM allocation byte,
bank restoration and unchanged VRAM/OAM. Broader fixtures check64-byte value
arguments/results, nested/static fields, explicit5-byte overlaps, all integer
field widths, addressed instance arguments, overlapping64-byte copies and raw
banked pointers. Expected values and poisoned memory are independent assertions,
not comparisons with the old compiler's output.

Parameterized constructors/newobj, aggregate array elements, boxing,
reference-bearing values and generic values remain outstanding migration work.
Do not infer their support from successful Sprite or unmanaged-value fixtures.
Portable PDB/source debugging and ordinary SDK native compilation also remain
separate acceptance gates. The full migration is not complete.

The final explicit cpobj variant replaces the known fixture's adjacent matching
ldobj/stobj pair with cpobj plus padding NOPs, preserving method length, tokens
and branch offsets. The original Roslyn image remains a separate execution.
Both forms check overlapping Packet copies, return42 and complete bank guards.
Focused Debug/Release × DMG/CGB cases pass4/4,exit0,25.109s
(`/tmp/koh-aggregate-cpobj.log`). The preceding workspace run passes675/675,
zero skipped,exit0,832.362s (`/tmp/koh-aggregate-workspace.log`). These totals
include499 compiler cases. Combined native coverage is478 ROM scenarios,
408 normal returns and70 traps; four full-frame comparisons remain unchanged.
