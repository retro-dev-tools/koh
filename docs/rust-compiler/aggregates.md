# Value types

The native compiler supports nongeneric value types with sequential layout and
unmanaged values with explicit layout. The SDK uses the native compiler. This
does not complete the compiler migration.

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
Generic shapes, AutoLayout and InlineArray diagnose explicitly. Sequential class,
string and array fields occupy two-byte reference cells. Nested inline values
propagate the reference-storage marker. Copies duplicate inline bytes while
retaining shared referenced allocations. Explicit managed layouts and scoped
byref fields diagnose until overlap and scoped lifetime semantics are supported.
Reference signature validation does not recursively lay out referenced array
element values, allowing a value type to contain an array of itself. Cached
inline nesting still observes the same64-level bound in either cache order.
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

## Value constructors

Nongeneric unmanaged value types support parameterless and parameterized
constructors, including chaining through ordinary calls, by-value/ref arguments,
static initialization and recursive factories. `default(T)` still zeroes the
value without invoking a user constructor; `new T()` invokes an explicit
parameterless constructor when Roslyn emits that call.

The newobj target must be an instance, nongeneric, void `.ctor` marked with both
SpecialName and RTSpecialName. Class and generic construction now use the
[closed-type construction contract](closed-types.md); the constructor tests retain
the value, frame and untouched-heap checks alongside invalid metadata diagnostics.
The lowering evaluates and retains explicit argument values, zeroes a private
frame slot, calls the constructor with its typed `this` followed by those
arguments, then snapshots the completed value. One slot is reused per reachable
IL site in each invocation. Entry initialization preserves the IR's existing
local invariant; each execution resets the slot before the call. Constructor
locals count toward the same local/frame/stack limits. They are appended after
CIL translation, so a malformed CIL local index cannot access them.

This uses existing local, zero, address, call and load IR operations. A separate
construction operation/backend path was unnecessary. Returning a snapshot avoids
aliasing the reusable slot; recursion gets separate hardware-stack frames. The
newobj argument evaluation/receiver convention follows ECMA-335 III.4.21 and the
[.NET instruction contract](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes.newobj).
No register allocator, calling convention or linker change is needed.

Readonly field address loads are allowed: Roslyn uses them for ordinary static
and nested struct reads. Direct writes still require the declaring constructor.
The former blanket address rejection confused correct CIL with verifiability;
ECMA-335 III.4.15 distinguishes them and warns that indirect mutation of an
init-only static outside initialization has unpredictable behavior. Such writes
are not a supported C# guarantee. The optimizer derives no immutability or
non-aliasing assumptions from init-only metadata.

Focused Debug/Release × DMG/CGB execution plus metadata/local-bound mutations and
unsupported class/generic diagnostics initially passed 6/6, exit0,37.427s
(`/tmp/koh-value-constructors-expanded.log`). The new constructor ROMs check
parameterless versus default initialization, value/ref arguments, chaining,
static readonly initialization, nested readonly reads, recursive returns,
repeated calls, zero padding and complete bank guards. A subsequent loop case
constructs directly within one frame and preserves a prior by-value snapshot;
its input IL contains newobj within a backward branch. The final focused run
including that assertion passes7/7,exit0,40.564s
(`/tmp/koh-value-constructors-final-focused.log`). The first full run exposed a
CLI test assumption: newobj now resolves Exception and reports missing
System.Runtime. That reference check remains; a separate direct throw fixture
preserves unsupported-opcode and input/output preservation assertions. The completed full run then reported673/677, zero skips,exit100,839.665s
(`/tmp/koh-value-constructors-workspace-final.log`). Its remaining four failures
were diagnostic expectations: missing System.Runtime after newobj resolution,
and a call message that lost the opcode number. Numeric opcode detail is
restored. Original exception-construction reference checks remain, with separate
direct-throw fixtures for unsupported-opcode and module-initializer execution.
The final focused regression run passes17/17,exit0,60.713s
(`/tmp/koh-value-constructors-regression-final.log`), including all four failures.
A clean full-suite run of the corrected head is still pending; do not report
677/677 from these scoped results. Twelve rejection probes
check malformed flags/targets, hidden-local access, direct readonly stores and
unsupported shapes. Final full-suite validation of the corrected head remains pending.

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

[Aggregate array elements](arrays.md) now have implementation and focused
execution evidence, including odd strides, whole64-byte snapshots and pinned
field access. Full workspace691/691 and final focused4/4 checks pass. Boxing,
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
