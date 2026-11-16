# Native interface binding

Nongeneric reference interfaces support implicit and explicit implementations,
interface inheritance, class overrides and reimplementation. Interface references
use the existing typed two-byte object reference in fields, statics, arrays,
arguments, results and bound delegates. Allocation retains the concrete class
identity; interfaces cannot be allocated or contain instance storage.

## Binding rules

The binder uses resolved assembly/token identities and exact signatures. It
selects explicit MethodImpl mappings before public virtual name/signature
matches, including inherited explicit mappings during reimplementation. A new
same-name method alone does not remap an inherited interface slot. Selected
class slots still honor virtual overrides on the actual receiver.
Default interface declarations also use a concrete class implementation when
one exists; a default body does not disqualify the declaration as a call slot.

[ECMA-335 II.22.27](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
permits MethodImpl bodies defined by the implementing class or a superclass.
Both MethodDef and MemberRef encodings resolve normally, including referenced
assemblies. Koh supports inherited bodies rather than imposing the tested .NET
runtime's stricter same-type restriction. Patched metadata fixtures independently
assert the resulting arithmetic in the existing SM83 emulator. The .NET
TypeLoadException is recorded separately and is not the acceptance oracle.
An additional class-slot MethodImpl alias executes on both .NET and SM83.
Unrelated or missing bodies, inaccessible strict declarations, incompatible
signatures, final declarations, duplicate mappings, invalid indices and cycles
diagnose. Existing metadata and binding work/depth
budgets remain enforced.

Reachability selects only implementations of allocated classes and reachable
call sites; validating interface completeness does not make unused bodies live.
Typed dispatch functions reuse the ordinary verified banked ABI, including
64-bit and aggregate parameters/results. No separate interface calling convention
or shared runtime dispatch scratch is introduced.

## Runtime type checks

ROM0 descriptors contain a parent pointer, a u16 membership count and that many
interface descriptor pointers. Each class includes its inherited/transitive
memberships. Linker symbols and relocations determine all final addresses.
The object header and field offsets remain unchanged. The variable descriptor
replaces the two-byte parent-only descriptor: ancestry alone cannot establish
interface membership, and linear membership scans avoid a second runtime table.
ROM0 capacity is checked before emission and layout remains linker-owned.

`castclass` preserves null and checks class ancestry or interface membership;
an invalid cast terminates with fault10. `isinst` returns the same reference on
success and null on failure. Managed references cannot be fabricated from raw
integers. Checks preserve CGB SVBK, including frames with offsets beyond the
SM83's signed SP-relative displacement range.

## Evidence and remaining language work

`tests/driver/interfaces.rs` builds real Roslyn Debug/Release assemblies and a
separate reference library. Four DMG/CGB cases exercise29 successful entries,
two runtime faults, forced separate ROM banks and untouched WRAM-bank2 guards.
Ordinary managed execution supplies corroborating results for28 entries; the
hardware entry checks SCY7 and SCXEE through the native ROM. Metadata cases
cover malformed mappings, inherited MethodDef/MemberRef bodies and unsupported
default/generic/boxed execution shapes. `tests/ir.rs` verifies typed isinst
operands, optimizer remapping/source origins and forbidden interface allocation.

Default-interface fallback/body selection, static virtual contracts, generic instantiation,
boxing/constrained value-type dispatch and covariant class MethodImpl returns
remain explicit implementation gaps. They are not malformed merely because Koh
cannot execute them yet, and this increment does not retire those capabilities
from the full migration goal.

Final selected regression:34/34 pass in419.564s, exit0, including the interface
matrix, metadata/IR/layout checks, existing virtual dispatch, real2048/JRPG games
and real-game source-debugger callers. Log: `/tmp/koh-interface-final.log`.
The preceding broader677-case run passed676 and failed one obsolete blanket
interface-layout rejection assertion (`/tmp/koh-interface-regression.log`,
1005.408s, exit100). That assertion was replaced with positive identity/storage
checks and passes in the final selection; the original broad run is not green.
No current-head full-workspace or cross-platform result is claimed here.
