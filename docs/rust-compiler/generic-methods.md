# Closed static generic methods

The native compiler now specializes reachable static generic methods
on nongeneric declaring types. Concrete arguments may be supported primitive
types, strings, nongeneric classes/interfaces/delegates, enums, nongeneric values
or supported vectors of those types. A method
and its full argument list identify an instance; byte and ushort remain distinct
even though both use the CIL int32 evaluation-stack type.

[Closed argument constraints](generic-constraints.md) now validate supported
special, base/interface and method-parameter requirements before code generation.

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
instance/virtual generic calls, generic method-pointer targets and
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
case is deferred. The other four wide-arithmetic/extension methods remain partial
until all their original expression contracts are traced to native acceptance.
The C# source definitions remain pending final whole-project removal.
