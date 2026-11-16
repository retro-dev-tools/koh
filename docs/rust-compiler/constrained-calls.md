# Closed constrained interface calls

The Rust frontend supports `constrained. thisType; callvirt` for supported closed
class/interface and struct receivers on nongeneric declaring types. Standard C#
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
public instance virtual methods by raw signature; explicit MethodImpl bodies must
match the signature, flags and declaring struct. Existing nominal assignability,
generic constraints and work budgets remain enforced. Generic interface methods
use the same closed argument binding as ordinary calls.

Prefix/call pairs are validated before stack lowering. Branches cannot enter the
call after its prefix. Dangling prefixes and wrong type tokens diagnose with IL
offsets. Modern constrained static-interface call/ldftn extensions diagnose as
unfinished support, rather than malformed metadata. Boxing-dependent base methods,
default-interface value fallback, primitive receiver runtimes and constructed
generic declaring layouts remain unfinished. None is approved for retirement.

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
