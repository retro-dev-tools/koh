# Rectangular arrays as generic arguments

Closed generic methods accept the existing zero-based rank-two rectangular
array types. Binding resolves the element in its original assembly, then retains
rank and validates size/lower-bound metadata. Accepted zero-bound encodings now
normalize for nominal identity through [closed-type binding](closed-types.md).
It does not reinterpret a referenced type's
metadata row in the caller's assembly. The ordinary array shape validator still
rejects rank-three, explicitly sized and nonzero-based signatures.

This closes an inconsistency: a rectangular array could be allocated, accessed
and passed to an ordinary method, but could not pass through `Identity<T>`.
No new array layout, allocation scheme or calling convention is introduced.
Generic instance work accounting includes rectangular wrappers alongside vector
wrappers. Supported constructed generic declaring layouts and static storage
are covered by the later [closed-type increment](closed-types.md).

Typed `initobj`, `ldobj`, `stobj` and `cpobj` lowering now uses the existing
`FieldStorage` model, which includes array reference cells. Previously this
path selected element `Storage`, which cannot represent an array reference.
The verifier continues requiring the exact typed reference cell for managed
loads/stores; native pointers cannot manufacture managed references. Default
initialization writes the null reference, not a zeroed array allocation.

These requirements follow [ECMA-335, sixth edition](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf),
the array/generic type rules and the `cpobj`, `initobj`, `ldobj` and `stobj`
instructions. In particular, III.4.5 initializes a reference home to null rather
than allocating an instance. The independent behavioral expectations are reference identity,
null defaults and explicitly authored element values, not legacy compiler output.

## Regression evidence

`driver::generics::generic_method_instances_execute_transitively` now includes
`Program::GenericRectangles` in Debug/Release × DMG/CGB. Roslyn emits real DLLs
and matching Portable PDBs. Rust compiles, round-trips canonical ELF, links banked
methods and executes the ROM in the existing C# emulator. The fixture checks:

- Primitive elements and packed local/foreign structs with colliding metadata
  row numbers but different field widths and layouts.
- Transitive generic calls across assemblies, recursion, reference constraints,
  alias mutation, both dimensions and full signed 32/64-bit element values.
- Generic default nulls, byref swaps and clearing an array reference cell.
- Rank-three generic arguments diagnose with member/IL context.
- Deterministic objects, specialized method/source mappings, heap/frame guards
  and isolation of the other CGB WRAM bank through the existing driver checks.

Two additional `rstest` cases check foreign class/value element scope and array
shape preservation during substitution. Existing rectangular-array diagnostic
tests remain the authority for rejected shapes and malformed helper metadata.

The first run failed at generic argument binding with `Compound generic arguments
require instantiated type binding` (`/tmp/koh-generic-rectangle-red.log`, exit100).
After binding support, all four configurations exposed the independent default
initialization restriction (`/tmp/koh-generic-rectangle-green.log`, exit100).
After selecting reference-cell storage, all four passed in41.741s
(`/tmp/koh-generic-rectangle-storage.log`, exit0). The final expanded Linux
regression includes byref swaps/clears and passes544/544 selected cases in741.555s,
exit0 (`/tmp/koh-generic-rectangle-regression.log`);400 other driver cases are
excluded by the selector. It runs all compiler-library, lowering and SM83 cases,
plus the generic and rectangular-array drivers. This is not a full-workspace run.
Workspace all-feature/all-target checking and separate rustdoc pass (four crates,
zero doctests). The existing unused cube `build_host` warning remains. Nextest
discovery lists1746 cases; both new scope cases are unignored. Rustfmt,
CSharpier and diff checks pass.

This adds coverage without claiming additional completed legacy dispositions.
The ledger remains198/565 complete,367 outstanding; the old compiler and test
definitions remain pending full migration acceptance.
