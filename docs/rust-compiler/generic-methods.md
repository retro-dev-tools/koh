# Closed generic methods

The native compiler now specializes reachable static and nonvirtual instance generic methods
on nongeneric declaring types. Concrete arguments may be supported primitive
types, strings, nongeneric classes/interfaces/delegates, enums, nongeneric values
or supported vectors of those types. A method
and its full argument list identify an instance; byte and ushort remain distinct
even though both use the CIL int32 evaluation-stack type.

[Closed argument constraints](generic-constraints.md) now validate supported
special, base/interface and method-parameter requirements before code generation.

Nonvirtual class methods use the ordinary object receiver, including callvirt's
null check; struct methods use the ordinary managed-byref receiver. Specialization
includes the receiver in the typed signature without changing its layout or
allocating a second object. The InstanceMethods fixture checks two class-method
widths, object field side effects and a packed-value return through a mutating
struct receiver in Debug/Release × DMG/CGB. Static flags must agree with the
receiver signature. Generic virtual methods still require instantiated dispatch
and diagnose explicitly; this is unfinished support, not malformed metadata.

Closed static and nonvirtual class method groups can bind delegates through
`ldftn` MethodSpecs. Their typed handles retain the existing closed instance
identity. Callback validation compares both lowered storage and actual CLI types
after assembly-scoped substitution; int32 stack storage does not make signed and
unsigned narrow parameters interchangeable. Callback emission calls the closed
target using the existing delegate ABI. Callback bindings reset alongside generic
instances for each reachability compilation, avoiding stale synthetic identities
when a Resolver is reused; layout-owned delegate type identities remain stable.

The GenericDelegates fixture covers two instantiations of the same static method,
referenced generic methods, a bound class receiver with observable mutation and a
packed struct argument/return owned by another assembly than its generic method.
Existing reused/fresh ELF equality and source-provenance checks apply unchanged.
The original rejection is in `/tmp/koh-generic-delegate-red.log`; a subsequent
determinism failure exposed the callback lifetime bug before its correction.
Generic virtual method pointers, value-type delegate receivers and constructed
generic declaring layouts remain unfinished; no support is implied by this change.
Selected compiler-library/IR/delegate/generic regression passes544/544 in362.459s;
after adding the ushort-to-short rejection probe, final4/4 passes32.431s, exit0.
Logs: `/tmp/koh-generic-delegate-final.log` and
`/tmp/koh-generic-delegate-signedness-final.log`. These are selected local results,
not final workspace/cross-platform acceptance. No legacy disposition count changes.

[ECMA-335 II.9.3–9.4 and II.23.2.15](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
justify method-parameter scope, closed substitution and MethodSpec arity.
Method arguments substitute into return/parameter/local signatures, addressed
storage, typed CIL operands and nested MethodSpecs. Recursive calls reuse the
same closed identity. Ordinary reachability includes referenced method bodies
and leaves unrelated unsupported float bodies out of the ROM.

Each substituted type retains the assembly that owns its metadata tokens. Named
arguments canonicalize to actual TypeDef and assembly identity before cache lookup;
enum identities stay distinct until scalar storage lowering. Parameters, returns,
locals and typed operands bind in their individual scopes. No invented metadata
tokens or alias rows can turn malformed input into a valid type reference.

Instances use internal0xfb identities in the existing function model. This is
neither a metadata table nor an object format. Each compilation resets its
instance cache, so an earlier selected entry cannot alter later ELF output.
The original MethodDef supplies diagnostics, initializer ownership and PDB lookup;
instance symbols stay distinct. The ABI, linker and debugger formats are unchanged.
Depth64,65536 substitution nodes,4096 instances and aggregate work/storage budgets
of1048576 bound expansion, in addition to ordinary IR and method-count limits.

## Acceptance

Rust `driver::generics::generic_method_instances_execute_transitively` drives real
Roslyn Debug/Release × DMG/CGB. Each of four cases now executes eleven regular
entries (including a repeated Main) and one patched valid self-constraint entry:
48 ROMs total. Earlier named-only evidence below covered32 ROMs.

- Original transitive Identity/Wrap byte5 and ushort1000 calls write/read BGP130.
- Int64 recursion preserves-5000000001; UInt64 locals preserveFEDCBA9876543210.
- String/vector references preserve length and element identity/value.
- Generic byref swaps preserve ushort1000/42 and distinct strings; typed arrays,
  byte/long elements and default UInt64 local initialization produce known results.
- A separate reference DLL forwards byte/ushort calls through its own generic
  Identity bodies, retaining its Library.cs source mappings.
- Remaining signed/unsigned widths, native integers, Boolean, char and object
  arguments preserve their declared values, including a surrogate code unit.
- Local and referenced named structs/classes/enums deliberately share TypeDef row
  numbers across assemblies. Pack1 values occupy3/12 bytes with asserted offsets;
  class fields and enum widths also differ. Transitive calls, value arrays,
  reference aliases and remote-class byref swaps must retain the distinct types.
- Interface and nongeneric delegate arguments also share rows across assemblies;
  interface dispatch and different delegate signatures retain their identities.
- Closed constraints cover value/reference/new(), nominal bases/interfaces,
  combined and parameter constraints, plus independent malformed-input probes;
  see [constraint evidence and boundaries](generic-constraints.md).

Each object has instance source provenance, round-trips canonical ELF, is linked
with managed functions in separate banks and reaches the expected return with
SP D000, intact WRAM0 guards and unchanged CGB bank2. Fresh and reused resolvers
must emit identical ELF bytes, including the reverse-order class-history probe.
Unreached unsupported methods do not appear in symbols; a missing reference is
allowed for the independent Main but diagnosed for the entry that calls it.
Unit cases check unbound indices, wrapper/parameter
order, depth boundaries and aggregate-budget overflow before counter mutation.

The named-type rejection is reproduced in `/tmp/koh-generic-named-red.log`, exit100.
The initial scoped binding passes14/14 in27.511s. The broader regression passes
575/575 in362.555s, exit0 (`/tmp/koh-generic-named-regression.log`), including all
compiler-library, IR and SM83 cases in that selection and interface/value/source
drivers. Another268 driver cases are intentionally unselected.
After indexed foreign-arity validation and the final enum/packing/byref probes,
23/23 passes66.511s (`/tmp/koh-generic-named-final.log`). Final fixture additions
for interface/delegate kinds pass4/4 in27.890s
(`/tmp/koh-generic-named-kinds.log`), executing32 ROMs.
Review identified an uncharged foreign GenericParam-table scan; reusing the
existing indexed arity lookup removes that scan entirely. No remaining confirmed
scoped-binding blocker was reported. These are selected results, not a final
full-workspace or cross-platform completion claim.

Current-source SDK acceptance also passes2/2 in172.953s, exit0
(`/tmp/koh-generic-named-games.log`): real2048 Debug/DMG gameplay and graphics,
plus JRPG Release/CGB dialogue/battle/hero-state checks in the existing emulator.

The preceding increment's original generic-call rejection fails in `/tmp/koh-generic-red.log`, exit100.
The initial expanded local suite passes11/11 in24.867s before reference-DLL and
aggregate-budget additions. Final20/20 passes65.508s, exit0 in
`/tmp/koh-generic-final.log`:10 unit cases, four generic driver cases, two existing
source-range cases, two constructor metadata cases and two reachable-call cases.
The latter now execute the previously rejected generic call and return1.
The broader566-case run passed564, with precisely those two obsolete rejection
assertions failing before their replacements;405.122s, exit100. That run is not
reported as green. Intermediate success is not whole-goal proof.

## Explicit remaining work

Closed Roslyn `new T()` now uses the shared ordinary constructor path; see the
[construction contract and acceptance](generic-construction.md). This does not
complete constructed generic layouts or general reflection support.

Constructed generic signatures containing foreign named arguments still require
scoped layout binding; those shapes diagnose rather than reinterpreting tokens.
Generic declaring types
need per-instantiation layouts/static storage. Broader constraint shapes and
virtual generic calls, virtual generic method-pointer targets and
constructed generic type arguments require binding.
These are unfinished compiler features, not malformed CLI shapes or approved
feature retirements. They currently produce explicit diagnostics. Expanding
vector instantiation reaches the existing jagged-array storage diagnostic; no
success or bounded generic recursion claim is made for that unsupported shape.
Floating-point arguments still require software-runtime lowering.

## Legacy test dispositions

`Frontends/CilGenericTests.cs` has six methods. Two now have complete REPLACE
dispositions: `GenericMethod_TwoTypesTransitively_ProducesExpectedValue` replaces
private mangled-name assertions with actual typed behavior, BGP130, reference
reachability and deterministic instances; `GenericMethod_DebugAndReleaseProduceIdenticalObservableState`
requires each actual build mode to produce the independently expected same result.
Both are discoverable through the four rstest cases above. No compiler-owned
case is deferred. The other four methods are now complete in
`driver::wide_arithmetic::legacy_generic_wide_expressions_match_complete_results`:

| Original method | Decision | Native requirement and oracle |
| --- | --- | --- |
| LongArithmetic_AddSubMulDivRem_MatchesHostComputedValue | PORT | Original composed add/sub/mul/div/rem formula; Rust i128 calculation, narrowed to full64 bits |
| ConvI8SignExtends_ConvU8ZeroExtends_MatchesHostComputedValue | PORT | Original negative int and high-bit uint widening; mathematical2147483648−5000000 |
| UlongDivision_IsUnsigned_NotArithmeticShift | PORT | Original high-bit unsigned divide/remainder formula; exact0x0f00000000000000 |
| WideIntArithmetic_DebugAndReleaseProduceIdenticalObservableState | REPLACE | Each mode independently matches all24 packet bytes, then matches the other mode |

The shared Roslyn fixture retains the original expression bodies, with mutable
static inputs passed to helpers so Release does not fold the input arithmetic
away. Each result is stored in a distinct eight-byte packet slot, avoiding the
removed compiler's private return-scratch ABI. Two rstest cases execute both build
modes on DMG/CGB: four ROMs,12 complete64-bit results. Canonical ELF roundtrip,
banked helper calls, stack/heap guards, result bounds and CGB bank2 guards remain
asserted. Final2/2 passes18.073s, exit0
(`/tmp/koh-generic-wide-closure.log`). These test-only additions change no compiler
semantics. All six original methods are now accounted for; broader generic
features remain unfinished.
The C# source definitions remain pending final whole-project removal.
