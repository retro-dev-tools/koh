# Reference-class layout foundation

Status: validated metadata/layout foundation and executable object core.
Native ROM probes cover allocation, constructors, fields and direct calls.
Interface dispatch,2048v2/JRPG execution and exact-PC source debugging now have
native acceptance coverage; this does not complete the compiler replacement.

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
The prefix contains a ROM0 descriptor address. Each descriptor contains its
parent descriptor address followed by its transitive interface memberships;
the built-in System.Object descriptor has a zero parent and empty memberships.
Canonical ELF symbols and relocations carry these addresses to the linker.
Typed object references retain class identity through calls, locals and SSA
joins. Reference-valued fields and class/string arrays use typed two-byte cells.
Nested arrays remain pending; [class dispatch](virtual-dispatch.md) now uses these
descriptors. Raw integers cannot fabricate objects.

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

The object-core implementation allocates fresh zeroed storage per dynamic
newobj, sets the descriptor before invoking constructors and preserves CGB
SVBK across heap accesses. It shares the checked4KiB arena with arrays and raw
allocations. Null access faults with code4; exhaustion faults with code5.
Nonvirtual callvirt null-checks even when the method never reads this; ordinary
base calls remain direct. Class virtual reachability now uses a bounded fixed
point over instantiated types and call sites; [interfaces](interfaces.md) now
participate through explicit metadata membership and resolved implementations.
Banked access, reference-valued arrays, callbacks, reentrancy and capacity checks
must compose with the existing ABI. Keep both real games and all compiler-owned
semantic tests on the final completion checklist.

## Current tests and evidence

`driver::native_object_identity_fields_and_faults` executes15 independent
program entries in Debug/Release on DMG/CGB (60 ROM probes, four rstest cases).
Final broader regression passes376/376 in338.797s, exit0 (99 unrelated cases
filtered). A post-format/final-verifier run passes277/277 in40.210s, exit0
(126 unrelated driver cases filtered). Logs: `/tmp/koh-object-final-regression.log`
and `/tmp/koh-object-postformat.log`. Cases cover repeated allocation
at one IL site, zero/null defaults, inherited and aliased fields, sibling/null
SSA joins, recursive linked objects, returned heap-field references, object
arrays, null calls/fields/elements and exhaustion. Exact heap packets check
string constructor arguments, narrow inherited fields, descriptor ancestry,
untouched arena bytes and CGB bank2 poison. Static reference storage is included.
Review reproduced a raw-address load fabricating an object (five cases passed,
one failed before the fix); managed loads now require matching typed cells.
Follow-up cases reject array indirection, conflicting static reference aliases
and inconsistent class identities/shapes across functions. Read-only re-review
found no further blocker; this is not a whole-compiler security certification.

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
| `CilVirtualDispatchTests::InheritedFields_UsePrefixLayout_NoOverlap` | PORT complete: native `ObjectCore::InheritedFields` checks exact before/base/after packets in four configurations. Metadata also covers hidden fields. |
| `CilClassStringFieldTests::StringCtorParameter_StoredInClassField_ReadBackViaLengthAndIndexer` | PORT complete: native `ObjectCore::BannerFields` checks two distinct strings/scores, length and indexed characters in exact heap records. |
| `CilVirtualDispatchTests::VirtualCalls_TagDispatchThroughBaseTypedStatic` | PORT complete: class dispatch tests execute three runtime types through a reassigned static receiver, exact Area/Kind packets and banked calls. |
| `CilVirtualDispatchTests::SceneMachine_GameRun_DeferredChangeWithCtorState` | PORT complete: [scene-flow acceptance](scene-flow.md) now executes actual Game.Run and checks deferred lifecycle order, constructor state and frame count. |
| Old scalar-aligned class layout / element-scaled GEP internals | REPLACE with the documented packed target ABI and independent offset/alias tests; old padding and helper-class names are not compatibility contracts. |

At the original class-layout checkpoint, three class methods had complete native
replacements and the ledger had48 complete and517 partial/outstanding. The
scene-flow acceptance above is a subsequent increment. This work stays in
the single migration PR; final acceptance still requires integrated native
objects and real games, followed by removal of the retiring compiler.

The native Arrays fixture previously asserted that `new object[2]` must fail.
That temporary subset restriction has no language-semantic justification once
typed reference arrays execute. Its replacement checks return value2, a
two-byte length header, four zero bytes for null elements and unchanged arena/
CGB bank2 surroundings. Subsequent [rectangular-array](rectangular-arrays.md)
and [binary32](float-lowering.md) work replaces those temporary restrictions;
nested and rank-three arrays still diagnose.
