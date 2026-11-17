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

Constructed generic reference-type virtual/interface dispatch, instantiated
interface contracts,
variance-aware assignability and boxing-dependent fallback remain unfinished.

Closed generic value receivers now execute through the existing
[constrained call contract](constrained-calls.md#closed-generic-value-receivers),
including explicit implementations and generic interface methods. This does not
establish constructed generic reference dispatch or instantiated interfaces.
This increment does not approve their retirement or classify valid unsupported
CLI shapes as malformed. Existing C# compiler/test definitions remain until full
migration acceptance. No extra legacy dispositions are claimed:198/565 complete,
367 outstanding. Full game/debug/release/performance/platform acceptance is separate.

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
Nested open generic method-signature shapes and general constructed dispatch
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
