# Closed constrained interface calls

The Rust frontend supports `constrained. thisType; callvirt` for supported closed
class/interface and nongeneric or closed generic struct receivers implementing
nongeneric and instantiated invariant interface contracts. Closed generic reference receivers use the
[closed dispatch contract](closed-types.md#closed-reference-dispatch). Standard C#
generic constraints and `ref T` arguments produce this CIL; authors need no Koh
attribute or alternative API.

[ECMA-335 III.2.1](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
distinguishes reference dereferencing, direct value implementation calls and
boxing-dependent fallback. Koh dereferences a correctly typed reference cell before
ordinary virtual/interface dispatch. For a struct's own or implemented interface
method, it passes the existing managed pointer directly to the selected body.
Mutation therefore reaches the caller; ordinary value copies remain independent.
Neither path invents an object layout or calling convention.

The prefix type retains its assembly scope through generic substitution. The
receiver must be the matching managed-pointer type. Implicit implementations match
public instance virtual methods by scoped signature; explicit MethodImpl bodies must
match the signature, flags and declaring struct. Existing nominal assignability,
generic constraints and work budgets remain enforced. Generic interface methods
use the same closed argument binding as ordinary calls.

Prefix/call pairs are validated before stack lowering. Branches cannot enter the
call after its prefix. Dangling prefixes and wrong type tokens diagnose with IL
offsets. Modern constrained static-interface call/ldftn extensions diagnose as
unfinished support, rather than malformed metadata. Boxing-dependent base methods,
default-interface value fallback, primitive receiver runtimes, method-generic
dispatch on constructed reference owners and variance-aware interface assignability
remain unfinished. Closed declaring layouts and static
storage are implemented separately; see [closed types](closed-types.md).
None of the unfinished dispatch behavior is approved for retirement.

Instantiated contracts preserve the closed interface and body-owner identities,
including methods with their own generic arguments on struct receivers. See
[interface binding and execution evidence](closed-types.md#instantiated-invariant-interface-contracts).

## Native evidence

`driver::generics` builds actual Debug/Release DLLs/PDBs and executes DMG/CGB ROMs
through canonical ELF and the Rust linker. Cases check original struct mutation,
independent copies, explicit struct implementations, class/interface aliases,
generic interface methods with packed structs, and null reference fault4.
The value-only entry additionally requires a completely untouched managed heap.
Wrong closed receiver types and MethodImpl bodies belonging to another struct
must diagnose; eight rstest cases cover prefix/branch/type-token boundaries.

The original unsupported opcode failure is `/tmp/koh-constrained-red.log`, exit100.
Initial expanded library/generic acceptance passes186/186 in41.615s, exit0
(`/tmp/koh-constrained-generic.log`). Selected library/IR/interface/generic/class
regression passes524/524 in252.077s (`/tmp/koh-constrained-regression.log`). After
adding the untouched-heap assertion and interface-alias case, final4/4 passes
39.612s (`/tmp/koh-constrained-final.log`):116 ROMs across four configurations.
Read-only review found no confirmed blockers. Selected local runs do not prove
complete migration acceptance; legacy source-method dispositions remain91/565.

## Closed generic value receivers

Metadata method/MethodImpl rows are read through the underlying TypeDef, while
the selected call retains the exact closed struct owner. Losing that owner would
erase field widths or confuse a reference-library payload with a local row of the
same number. The existing constrained pointer is passed directly through the
normal specialized call ABI. No boxing, heap allocation or alternate runtime
calling convention is introduced.

`driver::generic_types::closed_types_preserve_layouts_and_static_initialization`
now executes `ConstrainedDispatch` in all four Debug/Release × DMG/CGB modes:

- Repeated calls mutate the original struct and return42 then43.
- Byte and64-bit receiver payloads retain complete values and distinct counters.
- Explicit interface Read differs from the public same-name method.
- Interface methods with their own generic arguments forward a packed struct and a full64-bit value;
  implicit and explicit bodies have distinct counter effects.
- A referenced generic struct preserves a local packed payload through a call.
- A MethodImpl edited to borrow another generic value definition's body diagnoses
  with member/IL context, retaining nominal identity checks.

The existing test also retains banked ELF round-trip, resolver-history byte
determinism, frame/heap guards and the untouched alternate CGB WRAM bank. Initial
`/tmp/koh-closed-constrained-red.log` rejects all four configurations at the old
constructed-value guard. Basic execution then passes4/4 in31.726s. This work adds
runtime coverage without changing the229/565 legacy disposition ledger.

Final selected library/closed-type/ordinary-generic run passes237/237 in150.387s,
exit0 (`/tmp/koh-closed-constrained-regression.log`),438 outside the selection.
The new nominal-body mutation and existing wrong-receiver/MethodImpl checks pass.
Rustfmt, CSharpier and separate workspace/all-feature rustdoc pass (four crates,
zero documentation cases). These extend existing rstest configurations; no new
test declarations or legacy disposition closures are claimed. Complete hosted
validation and the remaining compiler migration gates are still required.
