# Reference-class layout foundation

Status: validated metadata/layout work, not executable class support. The native
compiler still rejects reference-class lowering, and2048v2/JRPG remain required
failing samples. This unit does not complete their test dispositions, allocation,
dispatch, source debugging or the compiler replacement.

## Requirements from actual consumers

Both games construct a scene and pass it to `Game.Run`. Scene classes override
Enter/Update/Exit and transition with freshly constructed scenes.2048v2 also
stores a Board reference, a byte-array field and a readonly string/score pair.
JRPG additionally uses interface callbacks and stored delegates. These are
observable game capabilities to implement, not old compiler conventions to copy.

The previous instance-signature path assumes every receiver is an unmanaged
value type. Sending a class through that path causes the misleading
`Aggregate must be a sealed framework System.ValueType` diagnostic. Classes
need reference identity and shared mutable storage; stack value-copy semantics
cannot implement them.

The selected semantic requirements are independently justified by
[ECMA-335](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf),
[newobj](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes.newobj)
and [callvirt](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes.callvirt):
fresh instances, zero/null fields before constructor execution, inherited state,
receiver aliasing and runtime override selection. CLR fixture results corroborate
those rules; the retiring compiler is not an oracle.

## Target layout choice

The reusable `layout::class` reader resolves an ordinary nongeneric AutoLayout
reference class independently of the existing unmanaged value-layout reader.
It reserves a two-byte runtime type-identity prefix, then appends instance fields
in metadata order, packed by byte. Derived classes preserve the full base prefix
before adding their own fields. Hidden fields remain distinct by declaring
assembly/token identity, regardless of equal names. Static fields consume no
instance storage. Empty classes retain the two-byte identity prefix.

SM83 memory accesses are byte-addressable, so scalar alignment adds padding
without a hardware requirement. The old compiler's alignment existed partly to
fit its element-scaled GEP representation; that is not a new ABI requirement.
Unmanaged struct-valued fields retain their independently defined internal
layout/padding and are copied as complete values. Reference/string/array/interface
fields occupy two-byte bus references. Numeric fields use their declared storage
widths, with target native pointers at16 bits. Self-referential fields are legal:
only inheritance and inline value layouts recurse, not referenced objects.

The two-byte prefix avoids copying the old optional one-byte dispatch-tag limit.
Actual type-ID assignment, allocation, constructor calls and dispatch are still
pending. The new typed IR must distinguish references from raw native integers
and preserve reference-valued fields, including nested arrays; treating every
object as an untyped bus address would lose the verification contract.

AutoLayout is the initial implemented class-layout mode. Sequential/explicit
class layouts and generic instances diagnose instead of ignoring their metadata;
this is not a final feature-retirement decision. Interface identities are valid
field references but have no concrete object layout. Abstract class layouts are
valid base prefixes; allocating an abstract class must separately diagnose.
Floating field widths do not establish software floating-point execution.

## Bounds and failure handling

Object size, including header, cannot exceed the4KiB managed arena. Inheritance
and field-type nesting have64-level bounds; the class cache has16384 entries.
A resolver-wide1048576-unit work budget charges metadata scans, signature bytes
before parsing and inherited field-reference copies. Inherited fields share
immutable `Arc<Field>` descriptors; array-shape vectors are not deep-copied per
subclass. Cached cumulative depth enforces the same64-level limit regardless of
lookup order. Budget failure preserves existing immutable cache hits.
Malformed ranges, inheritance cycles, sealed bases, unexpected layout metadata
and invalid field kinds diagnose. Missing referenced types are resolved normally,
never substituted by a pointer placeholder. Allocation and runtime RAM exhaustion
still require their separate execution checks.

## Required following work

Implement typed reference storage/SSA joins/subtyping and checked heap allocation,
then constructors, fields and direct instance calls with observable ROM tests.
Each newobj evaluation must produce a fresh object until explicit capacity
failure; reusing one allocation per IL site would violate identity/aliasing.
Set runtime type identity before constructor execution, including virtual calls
from a base constructor. Callvirt must null-check even nonvirtual targets;
ordinary base calls must remain direct. Virtual/interface reachability needs a
bounded fixed point over instantiated types and call sites, not the entire BCL.
Banked access, reference-valued arrays, callbacks, reentrancy and capacity checks
must compose with the existing ABI. Keep both real games and all compiler-owned
semantic tests on the final completion checklist.

## Current tests and evidence

The layout target uses actual Debug/Release Roslyn assemblies with a separate
base-class reference assembly. It checks exact target offsets and declared field
identities, inherited/shadowed fields, self references, primitive/native/float/enum
storage widths, nested-array references and inline unmanaged values. The CLR
fixture returns42 from fresh identity/default-field/inherited-shadow/alias checks;
that corroborates language requirements, not native object execution.

Negative cases cover generic and unsupported class layouts, value/interface
misclassification, object capacity, missing external bases, cycles, sealed bases
and malformed field ranges, with panic capture around mutated PE metadata.
A generated3000-sibling/400-field hierarchy tests the shared work bound.

Review exposed two implementation defects, then regressions reproduced them:
cached base chains bypassed the active-recursion depth bound, and large array
signatures could amplify through uncharged deep copies. The adversarial PE
fixture redirects200 field rows to a valid10334-byte nested rank32 array
signature, without generating a huge input file or risking OOM in the red run.
Both lookup orders, immutable descriptor sharing and pre-parse byte charging are
now tested. The review red run has1 pass/3 failures in9.302s; the corrected
selection passes5/5 in14.719s, exit0 (`/tmp/koh-class-review-green.log`).
The follow-up review found no remaining blocker in those fixes. The unmanaged
value-layout engine retains its separate limits; this is not a whole-compiler
resource/security certification. Final layout/reference regression passes58/58
in241.459s, exit0, zero skips (`/tmp/koh-class-metadata-regression.log`):48 layout
cases, including25 new class cases, plus10 reference-resolution cases.
Workspace/all-features/all-targets `cargo check` succeeds in46.58s, with the
pre-existing unused `build_host` warning in the cube test support module.
Separate workspace rustdoc succeeds (four crates, zero current doctests).
Logs: `/tmp/koh-class-workspace-check.log`, `/tmp/koh-class-doctests.log`.

## Legacy traceability

| Legacy requirement | Current native evidence / disposition |
| --- | --- |
| `CilVirtualDispatchTests::InheritedFields_UsePrefixLayout_NoOverlap` | PORT remains incomplete: metadata tests preserve base/hidden-field offsets and identities; native constructor/field execution is still required. |
| `CilClassStringFieldTests::StringCtorParameter_StoredInClassField_ReadBackViaLengthAndIndexer` | PORT remains incomplete: string fields have validated two-byte layout, but constructor/store/load/string-access ROM behavior is not implemented. |
| `CilVirtualDispatchTests::VirtualCalls_TagDispatchThroughBaseTypedStatic` and `SceneMachine_GameRun_DeferredChangeWithCtorState` | PORT remains incomplete: a header reservation is not executable dispatch or scene acceptance. |
| Old scalar-aligned class layout / element-scaled GEP internals | REPLACE with the documented packed target ABI and independent offset/alias tests; old padding and helper-class names are not compatibility contracts. |

No class method is marked complete or deferred. The565-method ledger remains
45 complete and520 partial/outstanding. This incremental metadata work stays in
the single migration PR; final acceptance still requires integrated native
objects and real games, followed by removal of the retiring compiler.
