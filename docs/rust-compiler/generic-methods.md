# Closed static generic methods

The native compiler now specializes reachable unconstrained static generic methods
on nongeneric declaring types. Concrete arguments may be supported primitive
types, strings, object references or supported vectors of those types. A method
and its full argument list identify an instance; byte and ushort remain distinct
even though both use the CIL int32 evaluation-stack type.

[ECMA-335 II.9.3–9.4 and II.23.2.15](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
justify method-parameter scope, closed substitution and MethodSpec arity.
Method arguments substitute into return/parameter/local signatures, addressed
storage, typed CIL operands and nested MethodSpecs. Recursive calls reuse the
same closed identity. Ordinary reachability includes referenced method bodies
and leaves unrelated unsupported float bodies out of the ROM.

Instances use internal0xfb identities in the existing function model. This is
neither a metadata table nor an object format. Each compilation resets its
instance cache, so an earlier selected entry cannot alter later ELF output.
The original MethodDef supplies diagnostics, initializer ownership and PDB lookup;
instance symbols stay distinct. The ABI, linker and debugger formats are unchanged.
Depth64,65536 substitution nodes,4096 instances and aggregate work/storage budgets
of1048576 bound expansion, in addition to ordinary IR and method-count limits.

## Acceptance

Rust `driver::generics::generic_method_instances_execute_transitively` drives real
Roslyn Debug/Release × DMG/CGB. Each of four cases executes seven entries
(six distinct entries and a repeated Main after class allocation),28 ROMs total:

- Original transitive Identity/Wrap byte5 and ushort1000 calls write/read BGP130.
- Int64 recursion preserves-5000000001; UInt64 locals preserveFEDCBA9876543210.
- String/vector references preserve length and element identity/value.
- Generic byref swaps preserve ushort1000/42 and distinct strings; typed arrays,
  byte/long elements and default UInt64 local initialization produce known results.
- A separate reference DLL forwards byte/ushort calls through its own generic
  Identity bodies, retaining its Library.cs source mappings.
- Remaining signed/unsigned widths, native integers, Boolean, char and object
  arguments preserve their declared values, including a surrogate code unit.

Each object has instance source provenance, round-trips canonical ELF, is linked
with managed functions in separate banks and reaches the expected return with
SP D000, intact WRAM0 guards and unchanged CGB bank2. Fresh and reused resolvers
must emit identical ELF bytes, including the reverse-order class-history probe.
Unreached unsupported methods do not appear in symbols; a missing reference is
allowed for the independent Main but diagnosed for the entry that calls it.
Unit cases check unbound indices, wrapper/parameter
order, depth boundaries and aggregate-budget overflow before counter mutation.

The original generic-call rejection fails in `/tmp/koh-generic-red.log`, exit100.
The initial expanded local suite passes11/11 in24.867s before reference-DLL and
aggregate-budget additions. Final20/20 passes65.508s, exit0 in
`/tmp/koh-generic-final.log`:10 unit cases, four generic driver cases, two existing
source-range cases, two constructor metadata cases and two reachable-call cases.
The latter now execute the previously rejected generic call and return1.
The broader566-case run passed564, with precisely those two obsolete rejection
assertions failing before their replacements;405.122s, exit100. That run is not
reported as green. Intermediate success is not whole-goal proof.

## Explicit remaining work

Named type arguments need assembly-aware substitution. Generic declaring types
need per-instantiation layouts/static storage. Constraints require actual
validation; instance/virtual generic calls and generic delegates require binding.
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
