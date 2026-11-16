# Native class virtual dispatch

Status: executable custom-class virtual dispatch; full migration remains open.
Interfaces, explicit MethodImpl mappings and generic virtual instantiations
still require implementation and explicit diagnostics. This is not general
.NET compatibility or completed scene-game acceptance.

## Semantic requirements

[ECMA-335 II.10.3](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
defines distinct slots for newslot methods and inherited matching slots for
reuse-slot overrides. Runtime type selects an implementation, while ordinary
call remains direct, including base calls. Callvirt null-checks even methods
that never read this. The object's most-derived identity exists before base
constructors execute; overrides can observe fields still holding zero/null.
An inherited implementation remains callable on derived objects.

These are language requirements, not the retiring compiler's byte-tag design
or a commitment to the current Scene/Game API. Useful game-state/callback
capabilities remain required independently of that API's quality.

## Implementation choice

The binder resolves nongeneric class slots from declaring assembly/token,
method name and exact metadata signature. It distinguishes newslot from reuse,
checks final/strict-access constraints and diagnoses ambiguous implementations.
Explicit MethodImpl mappings currently diagnose rather than guessing from names.
Same-name unrelated generic overloads remain dead when not selected.

Reachability repeatedly processes ordinary calls, reachable NewObject classes
and virtual call sites until no additional selected override body is found.
An override that allocates another class can add a new dispatch case and make
further bodies reachable. Only allocation instructions reachable in the lowered
CFG participate; this conservative closure precedes constant optimization.
Uninstantiated overrides are not embedded merely because their classes occur
in an assembly. Methods are still bounded to4096; virtual binding shares a
1048576-unit budget charging class/method scans and signature bytes. Failures
include the originating caller and IL offset.

Each call site targets a generated typed IR function, identified by reserved FE
plus the declared MethodDef row and assembly. Its ordinary signature preserves
the receiver type, all parameter widths and return type. Deterministically
ordered exact-class tests select a body, a checked cast establishes its receiver
type, and an ordinary Call invokes it. The same verified banked calling ABI
handles recursion, 64-bit/byref values and MBC5's ninth bank bit. A new indirect
call ABI or second register allocator is unnecessary. Linear tests add overhead;
table optimization can follow measured need without changing language semantics.

IsClass compares the existing ROM0 descriptor identity. CastClass follows its
parent descriptors, bounded to65 nodes (64 named classes plus System.Object).
Both preserve CGB SVBK and keep managed references distinct from raw integers.
CIL castclass supports class references, including null-preserving casts; invalid
casts terminate with fault10 under the exception-free runtime contract. String/
array casts and general exception handling remain unsupported. TypeFault is an
explicit terminal IR control operation, with no placeholder return value.

Synthetic dispatcher locations remain synthetic; original call instructions
retain caller/IL identity. Portable PDB source and stack debugging are separate
unfinished acceptance gates, not established by these function symbols.

## Evidence and remaining work

`driver::virtual_slots_and_constructor_dispatch_execute` uses real Debug/Release
Roslyn assemblies and the existing C# emulator. Cases cover three runtime types
reloaded from static storage in a loop, inherited implementations, independent
new slots, direct base calls, constructor-time dispatch, null, transitive class
allocation, valid/invalid/null casts and int64/byte/byref signatures. Each managed
function and dispatcher is constrained to a different ELF ROMX bank, including
bank257+, before canonical ELF roundtrip and Rust linking. SVBK2, bank2 poison,
frame guards, checked returns and an uninstantiated throwing override are checked.
Main exposes a32-byte managed observation packet checked directly by Rust:
each type's Area/Kind pair is18/227,12/130 and4/163, followed by zero padding.
Byte-valued kinds above0x7f exercise unsigned widening. A matching total alone
cannot conceal compensating wrong selections.

The initial16 ROM probes passed4/4 in17.475s. Expanded32 passed4/4 in18.967s.
The first banked/IR selection passed278/278 in24.479s, exit0,134 unrelated cases
filtered (`/tmp/koh-virtual-banked.log`). These intermediate results predate the
final three-type loop/signature-prefilter and malformed-metadata review fix.
The final selected run passes283/283 in43.495s, exit0,132 unrelated cases
filtered (`/tmp/koh-virtual-packet-green.log`), including the packet and corrected
forged-framework tests. The workspace/all-features run completed1047 cases in
1487.093s:1045 passed, two stale diagnostic assertions failed, zero skipped
(`/tmp/koh-virtual-workspace.log`). Both failures required the old word callvirt
for a forged framework reference, while resolution now rejects its exact missing
assembly identity earlier. The corrected two cases require System.Runtime,
public-key-token and caller/IL context and pass in the final selected run.
This is not a claim that the original full command exited green; it exited100.
Hosted validation of the next commit remains required.

Review found duplicate leaf overrides could silently select the first body.
PE mutations independently reparse the changed MethodDef names/signatures;
both Debug/Release cases reproduced success where rejection was required
(`/tmp/koh-virtual-ambiguity-red.log`, two failures in8.103s, exit100).
The binder now checks all same-class candidates, with explicit negative cases.
An80-class/80-site generated Roslyn fixture checks the work-limit diagnostic.

An ordinary2048v2 Release SDK build now advances through Game::Run and fails at
Assets::Load IL_0005: managed-reference fields inside value structs remain
unsupported, exit1 in29.78s (`/tmp/koh-virtual-2048-sdk.log`). This is the next
compiler gap, not game acceptance or a reason to remove useful asset handles.
Scene transitions, JRPG interfaces/delegates, interrupts, complete test migration,
source debugging and retiring compiler deletion remain required in the same PR.
