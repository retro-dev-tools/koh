# Closed generic layouts and storage

Reachable invariant generic classes and sequential value types have separate
layouts for each closed argument list. Ordinary constructors, instance/static
methods, method generics on those owners, nested constructed arguments, arrays
as arguments and reference-bearing value types use the existing IR and SM83 ABI.
Generic static fields and constructor state belong to the closed declaring type,
not its uninstantiated metadata definition. No C# game-library convention or API
change is required.

The storage contract follows the [C# specification's closed-type rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/types#843-open-and-closed-types)
and [static-field rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/classes#1552-static-and-instance-fields).
Argument exclusions follow [ECMA-335, sixth edition, II.9.4](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).
Legacy compiler output and emulator initialization are not acceptance oracles.

## Binding requirements

A metadata TypeDef identifies a template; it cannot identify the storage of both
`Pair<byte>` and `Pair<ulong>`. Substitution must distinguish declaring parameters
`!n` from method parameters `!!n`, retain the assembly of foreign arguments and
apply those bindings to signatures, locals, fields and IL type operands. Erasing
the distinction would alias fields/statics and select incorrect copy widths.

The resolver interns `(definition, closed arguments, class/value kind)`. Closed
methods additionally retain that declaring identity. Fields retain both their
metadata origin and closed owner. The existing metadata index continues resolving
templates and overloads; the binding layer supplies storage identities afterwards.
This avoids rewriting PE tables, duplicating method bodies in metadata or adding
a second compiler representation for ordinary methods.

Private identities use0xf9 for types,0xf8 for fields and the existing0xfb method
instances. The small metadata compatibility layer uses signature tag3 for an
interned closed-type reference after parsing. Every input signature parser rejects
tag3, and raw IL type tokens cannot name these identities. Internal references
are checked against the pool and assembly before use. These are compilation-local
identities, not a serialized object format; output remains canonical ELF32/SM83.

Value/class layouts preserve substituted field-signature assembly scopes.
Packed local and referenced payloads therefore retain their distinct widths even
when metadata row numbers collide. The IR verifier accepts closed nominal
identities while retaining storage-size, reference-cell and use/type checks.
Instruction selection and argument/return movement use the existing widths.

Generic argument constraints share the ordinary method validation path. Work is
charged before scanning metadata for either owner kind. Pointer/byref/typedref/void
arguments reject even when a method never accesses its parameter. Substitution
checks composed depth and node/payload costs before copying arguments. Constraint
recursion, instantiation count and total work remain bounded and diagnose.

## Initialization and lifetime

Each closed static field has its own ELF symbol and WRAM allocation. Each closed
`.cctor` is an ordinary specialized method with a separate state byte and guard.
The0xf7 guard identity maps to that0xfb constructor; raw constructors retain0xff
guards. The existing not-started/initializing/complete states and recursive
initializer behavior are unchanged. Mainline prefix/field checks select the closed
constructor; writes to readonly fields check the exact closed declaring constructor.
The backend's initialized fast path recognizes both guard kinds.

Startup clears the emitted static storage. All instantiated fields and state bytes
count against the existing WRAM capacity; there is no aliasing fallback when they
do not fit. IRQ restrictions continue rejecting initializer/runtime state that
cannot safely execute in an interrupt.

Closed type/member pools and all layout/delegate descriptors that refer to them
reset for each compilation. This is required for deterministic successive builds
on one resolver: a descriptor from an earlier pool must not retain a recycled
identity. Method source identities still map to the original MethodDef for
matching Portable PDB sequence points; no debug format changes are introduced.

## Execution and regressions

`driver::generic_types::closed_types_preserve_layouts_and_static_initialization`
drives actual Roslyn DLLs/PDBs through Rust lowering, ELF round-trip, banked linking
and the existing C# emulator in Debug/Release × DMG/CGB. The fixture covers:

- Distinct byte/64-bit/packed-struct layouts and full signed/unsigned field values.
- Generic value constructors, unboxed instance methods, copies and reference
  fields; local/foreign payloads and foreign generic owners.
- Four separate static instantiations, once-only constructors, readonly defaults
  and cross-instantiation isolation with an independently expected counter.
- Declaring-context `new T()` for constructed reference/value types.
- Generic delegates carrying constructed values, with warm/cold compilation
  histories compared to fresh resolvers byte-for-byte.
- Bound callbacks on local and foreign constructed owners, including aggregate returns
  and receiver mutation after binding.
- Vector and rectangular-array arguments, nominal signedness and zero-bound identity.
- Original reference-library PDB lines through ELF and linked KDBG ranges.
- Constraint flag mutations and a pointer argument hidden behind a method that
  never uses T; failures must retain member/IL context.
- Frame/heap guards and preservation of the other CGB WRAM bank.

`declaring_constraint_scans_obey_the_work_budget` builds independent small/large
constraint fixtures. An ordinary nongeneric method accepts the constructed type,
so a generic-method guard cannot conceal a missing type guard. Eight valid
constraints execute42;1,024 valid constraints reject before expensive scanning.
Additional `rstest` cases bound composed substitutions and array-shape payloads.
Accepted zero lower-bound encodings normalize to one nominal array identity after
shape validation. Generic explicit-layout types reject under ECMA-335 II.10.1.2,
even when their fields do not use the parameter.

Initial evidence and corrected regressions:

- `/tmp/koh-closed-types-red.log`: constructed value lowering rejected.
- `/tmp/koh-closed-types-first.log`: generic return substitution occurred too late.
- `/tmp/koh-closed-types-context.log`: initial4/4 configurations passed in24.909s.
- `/tmp/koh-closed-types-constraints-red.log`: invalid unused pointer argument
  incorrectly produced an object before the binding guard was added.
- `/tmp/koh-closed-types-construction-red.log`: declaring-context `new T()` rejected
  before both argument scopes and constructor origins were retained.
- `/tmp/koh-closed-types-wide.log`: generic delegate normalization recursed; the
  run was interrupted after repeated stack aborts and is not a pass.
- `/tmp/koh-closed-types-cache-red.log`: cold compilation reused a stale delegate
  descriptor and diagnosed inconsistent nominal storage.
- `/tmp/koh-closed-types-budget-red.log`: the large valid type escaped the work
  guard before charging moved into shared constraint validation.
- `/tmp/koh-closed-types-review-fixes.log`:214/214 selected cases passed in103.929s,
  exit0, including all four prior delegate-stack-abort configurations and both
  review-found regressions. This precedes final composed-copy bounds.

Final scoped evidence: `/tmp/koh-closed-types-owned-final.log` reports221/221
selected cases passing in83.682s;430 cases are excluded. The closed-type driver
covers41 ROM executions and29 rejected compilations, including the independent
constraint-budget fixtures. Eleven new library cases cover substitution limits
and canonical array identity. Earlier broad310/310 and delegate273/277 results
are investigative checkpoints; the latter exposed four new bound-callback failures
fixed by retaining the instantiated owner at receiver coercion. They are not a
current full-workspace pass.

## Remaining work

Variance-aware assignability and boxing-dependent fallback remain unfinished.

[Exact variant interface binding](exact-variance.md) now supports nominal calls;
cross-instantiation variance conversions and runtime tests remain unsupported.
The historical checks below originally distinguished unsupported declarations from
invalid special-constraint combinations; their valid-declaration cases now execute. The variance diagnostic cannot mask
simultaneous reference-type and nonnullable-value-type constraints.
[ECMA-335 II.10.1.7 and II.23.1.7](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
define the incompatible constraints and their flag encoding; II.9.5 permits
variance on interfaces and delegate classes. The new real-Roslyn `Variance`
fixture covers both interface directions in Debug/Release, with valid original
metadata also executed on .NET. Each of four native test cases checks legal
variance with/without the reference constraint, six invalid flag combinations,
and an invariant control that compiles and executes42 with balanced SP, guards
and the full heap preserved. Boxing is still rejected at opcode008C with member
and IL context, rather than producing a placeholder.

The focused four new cases and four existing closed-type execution/constraint
cases passed8/8 in27.138s, exit0 (`/tmp/koh-variance-flags-final.log`). The failing
interface cases in `/tmp/koh-variance-flags-compile-red.log` first reproduced the
contradictory-constraint misclassification. An exploratory delegate generic-argument
probe reached an earlier unsupported compound-binding path; it did not prove
the presumed delegate flag bug and was not retained as that regression. These
checks do not establish variance-aware runtime dispatch, general boxing support,
or completion of the supported-subset policy.
Method-generic reference dispatch is covered [below](#generic-methods-on-closed-reference-receivers).

Closed generic value receivers now execute through the existing
[constrained call contract](constrained-calls.md#closed-generic-value-receivers),
including explicit implementations and generic interface methods. Instantiated
invariant contracts are covered by the [interface binding work below](#instantiated-invariant-interface-contracts).
This increment does not approve their retirement or classify valid unsupported
CLI shapes as malformed. Existing C# compiler/test definitions remain until full
migration acceptance. No extra legacy dispositions are claimed:229/565 complete,
336 outstanding. Full game/debug/release/performance/platform acceptance is separate.

Workspace discovery lists1,763 cases; all17 new cases are unignored. Formatting
checks pass (Rust and two C# fixture files). The workspace check retains the
existing unused `build_host` warning in the cubes test target. Rustdoc runs four
crates with zero documentation cases. These checks do not imply all1,763 ran.

## Closed base classes

Base-class TypeSpecs now substitute the current declaring arguments before
layout. The closed base identity participates in the existing ancestry, field
prefix and receiver-conversion paths. Sealed checks inspect its underlying
TypeDef, rather than indexing metadata with a compilation-local identity.
Nested substitution, per-assembly argument scope and existing cycle/depth/work
limits remain shared with ordinary closed types. No authoring API or ABI change
is needed. This follows the [C# base-class specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/classes#1524-class-base-specification).

The existing four-configuration driver now executes `Program::Inheritance`:
byte/64-bit base storage, writes through a base reference, derived-field
isolation, nested/reordered declaring arguments, a nongeneric subclass and a
referenced base with a local packed payload. Literal expected field values,
banked ELF execution, heap/stack guards and resolver-history determinism remain
checked. Mutating the generic base to sealed must diagnose with IL context.
This adds runtime coverage without claiming a legacy test-disposition closure.

Generic virtual/interface dispatch remains separate. Successful direct base calls
do not establish that contract.

Final selected evidence: `/tmp/koh-inheritance-regression.log` reports438/438
passing in460.872s, exit0, with375 excluded. This includes library/lowering tests,
delegates, virtual-slot binding, object identity, IRQ dispatch and all closed-type
configurations with sealed-base rejection. Rustfmt and separate all-feature
workspace rustdoc pass (four crates, zero doc cases). Both formatted C# fixtures
retain the hashes used in the final run. Full current-head CI remains required.

## Inherited method references

Method lookup can now carry the caller's declaring/method arguments through a
constructed base chain. The resolved method retains its exact closed declaring
identity; existing specialization, argument movement and source identity then
apply. Base traversal is shared with class layout. Lookup checks signatures,
assembly scopes, overload ambiguity and method arity; traversal and substitution
are bounded and charged to the existing generic work budget.

The distinction between member inheritance and metadata ownership matters:
[ECMA-335 II.9.8 and II.22.25](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
identify the declaring type and uninstantiated member signature. A field reference
naming a derived type that does not declare that field must not be repaired by
walking its bases. The old resolver did that for nongeneric fields; the new
resolver diagnoses the unresolved field instead. This changes an erroneous
metadata lookup, without changing valid C# authoring APIs.

`driver::generic_types::inherited_member_references_bind_the_closed_base` edits
actual Roslyn metadata and runs Debug/Release × DMG/CGB cases. Valid method cases
retain inherited overloads, MethodSpec arguments, a generic caller and a foreign
base returning a local packed payload. Full results execute in the existing
emulator. Invalid field cases retain an IL-context diagnostic. The managed host
is a separate compatibility observation; literal values and metadata ownership
justify the assertions. No legacy source-test disposition is claimed.

An initial test incorrectly expected the edited field reference to execute.
`/tmp/koh-inherited-members-managed.log` exposed MissingFieldException; isolating
the edits showed method-only cases execute42 while field edits fail. This was a
bad test expectation, not evidence for retiring a valid generic capability or
calling well-formed but unresolved metadata structurally malformed. The earlier
native acceptance of that field shape was removed. The corrected eight-case
checkpoint passes65.825s in `/tmp/koh-inherited-members-declaring-final.log`.
Nested open generic method-signature shapes and general instantiated-interface dispatch
still need broader acceptance; this increment does not claim their completion.

Final owned selection:249/249 pass262.796s, exit0,436 excluded,
`/tmp/koh-inherited-members-owned-final.log`. Library invariants, all reference
resolution cases, closed layouts, generic calls and static/byref cases pass.
The earlier245/249 run exposed four fixture row-collision failures: placing the
new RemoteBase at the end of GenericLibrary preserves the deliberate equal-row
local/foreign types and every existing assertion. This was a fixture regression,
not a reason to remove the collision checks. Final Rustfmt, CSharpier and separate
workspace/all-feature rustdoc pass (four crates, zero doc cases). Workspace
discovery lists1,804 tests; all eight new cases are unignored. Discovery is not
full-suite execution. No legacy source disposition credit is added.

## Closed reference dispatch

Nongeneric virtual methods on closed reference types retain their declaring
identity through slot selection, signature checking, method enumeration and the
existing dispatch IR. Metadata rows and names come from the underlying MethodDef;
they are not indexed with a compilation-local method identity. Scoped signature
comparison reuses the inherited-member binding path, and typed call signatures
retain the complete specialized method rather than dropping its closed owner.
No new dispatch runtime, object header or calling convention is introduced.

Nongeneric interface contracts support implicit, explicit, inherited and default
implementations on these receivers. MethodImpl bodies bind to the matching closed
ancestor even when the implementing leaf is nongeneric. This follows the body
ownership rule in [ECMA-335 II.22.27](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf);
the same slot rules still select overriding implementations. A body from another
class is rejected. Runtime rejection of an inherited body is not an acceptance
oracle or a reason to retire that valid shape.

The existing four-mode driver adds byte/64-bit receivers, explicit versus public
implementations, overrides on generic classes, a nongeneric leaf, aggregate copy returns,
default methods with observable nested calls, a referenced class returning a local
packed value, constrained reference cells and captured virtual delegates. Counter
effects and complete payload values distinguish the implementations; changing a
receiver's payload after binding tests retained reference identity. The existing banked ELF,
PDB, heap/stack and resolver-history checks remain.

Four new rstest cases in
`driver::generic_types::closed_methodimpl_bodies_keep_the_inherited_declaring_scope`
edit real Roslyn MethodImpl rows. Closed generic owners and nongeneric leaves use
an inherited generic-base body, execute independently expected counter values and
reject an unrelated body with IL context. Methods with their own generic arguments
and instantiated interface contracts were unfinished at that checkpoint; subsequent
sections document their execution coverage. Variance
remains unfinished. No feature retirement
or additional legacy disposition closure is claimed.

Evidence: the original guard rejects4/4 in
`/tmp/koh-closed-reference-red.log`; basic interface execution passes4/4 in32.510s.
Extended fields/overrides pass4/4 in32.876s. The nongeneric-leaf inherited-body
case first fails in `/tmp/koh-closed-reference-fixed-body-red.log`, then the
corrected eight-case selection passes56.467s. Broader binder/library/interface/
default/delegate/virtual/object regression passes322/322 in744.216s
(`/tmp/koh-closed-reference-regression.log`),357 outside the selection. After
adding the final callback/reference-cell/foreign-return cases and constructor
acceptance,10/10 pass68.525s (`/tmp/koh-closed-reference-consumers-final.log`),450
outside the selection. Production Rust code did not change between those runs.

Rustfmt, CSharpier and separate workspace/all-feature rustdoc pass (four crates,
zero doc cases). Workspace discovery lists1,808 tests; all four new cases are
unignored. The existing unused cubes-host warning remains. Full current-head
hosted validation is still required; source dispositions stay229/565 complete.

## Instantiated invariant interface contracts

InterfaceImpl binding now substitutes the declaring type arguments before
layout, constraint checks and dispatch consume the interface identity. All three
consumers share the resolver path. Closed identities remain distinct, including
different instantiations of the same interface on one receiver; inherited
interfaces retain declaration order. Metadata scans remain work-bounded and
reject null, invalid and non-interface targets.

`Program.InstantiatedInterfaces` in the ClosedTypes fixture checks implicit byte
and ulong implementations, explicit implementations on one receiver, and an
inherited generic interface on a generic base. Values and per-method counters
must match independently known results through Roslyn, ELF emission, linking
and emulator execution. The initial four Debug/Release and DMG/CGB cases reject
valid InterfaceImpl TypeSpec rows; after binding they pass4/4 in34.089s
(`/tmp/koh-instantiated-interfaces-green.log`). The final fixture additionally
distinguishes byte and sbyte contracts on the same receiver and checks a referenced
class returning a game-local packed value by copy.

Constrained struct calls bind the declaration before selecting a body, compare
member signatures in the closed interface/receiver scopes, then specialize the
selected body on the actual receiver. Explicit MethodImpl selection uses both
the method definition and closed owner. Its body must belong to that exact
receiver instantiation. Method arguments remain separate from declaring arguments;
the original managed pointer remains the receiver, preserving caller mutation.

The ClosedInterfaces fixture checks implicit and explicit generic structs,
multiple same-width contracts, generic methods on instantiated interfaces,
packed aggregate copies, complete ulong values and a referenced struct whose
argument comes from the game assembly. All four Debug/Release and DMG/CGB
executions require return42, restored stack/guards and an untouched managed heap
(no boxing). The initial cases failed4/4 with unbound class-layout diagnostics;
the first constrained/reference/generic regression passed12/12 in66.138s.
Expanded value cases passed4/4 in14.302s. Logs:
`/tmp/koh-instantiated-value-red-budget.log`,
`/tmp/koh-instantiated-value-green.log`,
`/tmp/koh-instantiated-value-expanded.log`.

The final consumer run passed14/14 in82.948s, including a MethodImpl mutation
that names the correct generic definition with the wrong closed body owner.
The compiler rejects it with method/IL context. The same run executes the
formerly rejected GenericInterface contract and retains the BoxedInterface
diagnostic and all existing malformed-interface checks. Log:
`/tmp/koh-interface-final-consumers.log`.

The prior128-case regression found a dropped layout scan charge and two stale
generic-interface rejection assertions. The original scan charge is restored;
both interface-budget cases pass. GenericInterface now joins the actual ROM
execution cases; BoxedInterface retains its explicit diagnostic expectation.
Variance and boxing/default-interface value fallback remain unfinished.
No feature retirement or additional legacy
disposition closure is claimed.

Final selected library/driver regression passes132/132 in407.720s, exit0,
551 outside the selection (`/tmp/koh-instantiated-interfaces-final-regression.log`).
This includes the unchanged interface-budget assertions, default/interface and
virtual dispatch, retained generic/constrained cases, callbacks, interrupt roots
and wide arithmetic. Rustfmt and separate workspace/all-feature rustdoc pass
(four crates, zero doc cases). Full current-head hosted and product acceptance
remain required.
Workspace nextest discovery lists1,812 cases; all four new constrained-interface
cases are unignored. CSharpier checks all three changed C# fixtures. Fixture
hashes remain unchanged through final verification. The existing unused cubes
host warning is unchanged.

## Generic methods on closed reference receivers

Slot binding retains a raw MethodDef, its closed declaring owner and its formal
method signature. Applying method arguments happens after selecting the body;
the selected body's own declaring scope remains attached. This distinguishes
class arguments from method arguments and avoids comparing overloads only after
their substitutions happen to coincide. The existing method-identity interner
stores these scoped declarations; no second dispatch model or runtime dictionary
is introduced. Empty method arguments on a positive-arity declaration are valid
only during binding. `method_types` requires the complete argument list before
producing typed IR; the new unit test checks that boundary. Actual specialization
still validates arity and constraints. Declaration lookup shares the existing
count/work budgets. The unused owner-free specialization wrapper is removed.

The ClosedTypes `GenericReferenceMethods` entry executes generic virtual
overrides, implicit and explicit generic interface methods, a generic default
interface body and a captured generic virtual callback. Scalar and packed
arguments/results retain full widths. Counter effects distinguish bodies and
retained receivers. A reference-assembly class implements a reference-assembly
interface while its class/method arguments include game-local packed values;
ordinary calls and callbacks preserve their assembly scopes and copy semantics.
The existing four-mode driver keeps resolver-history byte determinism, banked
ELF round-trip, PDB/KDBG and heap/frame checks.

Four new rstest cases in
`generic_methodimpl_bodies_keep_the_closed_ancestor` edit real Roslyn MethodImpl
rows. Generic owners and a nongeneric leaf borrow a virtual generic ancestor
body; normal override selection must still produce the independently expected
counter effects and scalar/aggregate returns. An unrelated generic body rejects
with member/IL context. The ancestor-body ownership rule is ECMA-335 II.22.27;
runtime acceptance is not the oracle for that valid metadata shape.

Initial execution fails4/4 at the unfinished guard (20.730s), then basic override
execution passes4/4 (25.883s). Expanded local cases pass4/4 (26.623s). The final
delegate uses the fixture's existing PairHandler, requiring no framework delegate
assembly. An initial System.Func experiment correctly diagnosed its missing
System.Runtime input; these tests do not claim full BCL compatibility.

Selected library/driver regression passes133/133 in409.320s, exit0,551 outside
the selection (`/tmp/koh-generic-reference-regression.log`). After fixture-only
foreign-body and inherited-MethodImpl additions, final consumers pass8/8 in39.404s,
exit0 (`/tmp/koh-generic-reference-scopes.log`). Production Rust did not change
between these runs. No additional legacy source-test disposition is claimed;
variance, boxing/default-interface value fallback and the broader migration
acceptance gates remain unfinished.

Review exposed a separate correctness bug: a selected override could have a
signature arity that disagreed with its own GenericParam rows. The base call's
reference validation did not validate the selected body. Two new Debug/Release
metadata mutations reproduce successful compilation of that inconsistent shape.
Declaration binding and actual specialization now check the selected MethodDef's
arity against metadata. The unchanged typed-IR arity gate remains in place.
After this fix, all library/reference-resolution tests plus the selected closed
reference, inherited MethodImpl, ordinary/constrained generic and rejection
cases pass248/248 in192.823s, exit0,452 outside the selection
(`/tmp/koh-generic-reference-arity-regression.log`). The original two failures
are `/tmp/koh-generic-arity-red.log`. This final production fix follows the
earlier133-case and8-consumer checkpoints; those are not claimed as reruns of
the arity fix.

Final Rustfmt, CSharpier and separate workspace/all-feature rustdoc pass
(four crates, zero doc cases). Discovery lists1,819 tests, including all seven
new unignored cases: one typed-IR guard, four inherited-body cases and two
override-arity rejection cases. The existing unused cubes-host warning remains.
