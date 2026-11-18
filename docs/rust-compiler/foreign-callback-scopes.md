# Cross-assembly callback type arguments

Constructed delegates now use the same canonical closed-type bindings as ordinary
generic classes. Each argument retains its own assembly scope. Libraries can store
callbacks for game-defined classes/structs, and games can use referenced types in
callbacks, including nested delegates and distinct declaring/method arguments.

## Decision and API

The previous delegate path copied raw signatures with one assembly index for the
whole argument list. Rejecting foreign names prevented incorrect token rebasing,
but a separate substitution model was unnecessary once scoped closed types existed.
That special path is removed. Target matching now compares each nominal type in
its original scope; equal-width enums/integers and equal-sized structs stay distinct.
The raw signature-copy utility retains its guard on paths that cannot represent
foreign bindings. No placeholder value, fake metadata token or extra format is used.

The Rust API deliberately changes: `resolve::DelegateDefinition::type_arguments`
contains `TypeArgument { assembly, ty }` records, replacing unscoped signatures
plus the single `argument_assembly` field. `resolve::TypeArgument` reexports the
existing canonical record. In-repository consumers assert both signatures and
scopes; keeping the old API with ambiguous scope values would hide the bug.
C# authoring APIs, callback payloads, calling ABI, ELF and KDBG formats do not change.
Delegates count in the existing4096 closed-type and4096 callback-kind limits;
no larger capacity is promised. Existing work/depth/node limits remain enforced.

Closure validates runtime declarations and constraints before interning, even for
an unused delegate type argument. Variant delegates share interface variance-position
rules. Reference/value constraints, invalid flag words and ref-safety restrictions
remain checked. Invalid bindings must not poison later valid resolver use.

## Behavioral coverage

Rust `tests/driver/foreign_callbacks.rs` builds real Debug/Release Roslyn fixtures
with a referenced GenericLibrary. Four DMG/CGB cases execute44 ELF-linked ROMs:

| Entry | Independent requirement |
| --- | --- |
| LocalValue / RemoteValue | Struct transport in both scope directions; changed result, unchanged input copy |
| LocalReference | Game class retains identity and shared mutation through library storage |
| Enum | Referenced enum retains nominal identity and full5000000001 value |
| Arrays | Array of game structs retains reference aliasing |
| Nested | Delegate argument contains game and library value types in separate scopes |
| Captured | Returned library closure retains a game object and observes later mutation |
| MethodScopes | Game declaring argument and library method argument remain distinct |
| Constraint / Factory | Actual class constraint and covariant result bind correctly |
| UnusedFactory | Constructed delegate is itself an ordinary generic-method argument |

Every ROM returns42, restores SPD000 and passes guards. Complete256-byte packets
check42/123456780000002A, enum5000000001 and untouched5A. Matching root/library PDBs
survive ELF to final KDBG: reached library bodies have Library.cs mappings;
metadata-only dependencies do not fabricate executable library mappings.
Four additional Debug/Release cases reject forged equal-sized struct targets,
enum-to-long targets, wrong special constraints/variance, missing references and
invalid runtime constructors. Six further ROM runs validate resolver reuse after
errors. No malformed assembly is executed on .NET.

Four existing variant-delegate Rust cases replace obsolete compound-type rejection
with executable42 and a complete untouched-heap check. Their flag, allocation,
SP, symbol/bank and malformed-input assertions remain. No legacy source-disposition
credit is added.

## Evidence and remaining gates

Initial4/4 failed at the foreign-name guard (`/tmp/koh-foreign-callback-red.log`,
exit100). First8/8 passed25.642s (`/tmp/koh-foreign-callback-first.log`, exit0).
The first expanded460-case run passed456 and found four diagnostic/obsolete-boundary
expectations; it was not green. Full declaration validation preserves the specific
malformed-delegate diagnostic; valid compound shapes now execute instead.
Corrected462/462 passed216.581s, exit0,443 explicitly unselected
(`/tmp/koh-foreign-callback-corrected.log`). After adding PDB/reachability and
missing-reference assertions, affected8/8 passed17.298s, exit0,581 unselected
(`/tmp/koh-foreign-callback-debug-final.log`). No production changes followed the
expanded run. KDBG serialization/mapping checks are not source-breakpoint tests;
current-head SDK/debugger and three-platform acceptance are not claimed here.

All five LINQ source methods remain outstanding. Query implementation, array/interface
adaptation, recursive constraints and other migration gates remain open.
Ledger509/565,56 outstanding; legacy deletion awaits replacement acceptance.

Fresh discovery2061 workspace/1884 compiler cases, zero ignored, exit0
(`/tmp/koh-foreign-callback-discovery.json`). Compiler doctests ran separately:
zero examples, exit0 (`/tmp/koh-foreign-callback-doc.log`). CLI build, rustfmt,
CSharpier and diff checks pass. The existing cubes dead-code warning remains.
Fresh CLI pipeline probe still rejects array RTTI at Where IL_0040, exit1/no object
(`/tmp/koh-foreign-callback-linq-probe.log`); no LINQ execution is claimed.
