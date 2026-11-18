# Closed generic construction

Roslyn's `new T()` emits `System.Activator.CreateInstance<T>()`. The Rust compiler
recognizes the trusted framework owner and exact static generic signature, closes
the argument in its originating assembly, and uses the ordinary constructor path.
No assembly is executed during compilation and no reflection runtime is embedded.

Reference types allocate distinct objects and invoke their own public parameterless
constructor. Value types invoke an explicit public parameterless constructor when
present; otherwise supported integer/native/value storage is zero initialized.
An explicit struct constructor's side effects occur once per construction.
`System.Object` receives ordinary distinct object allocation. Constructor bodies
and transitive reference-library calls participate in normal reachability.

Nonpublic value-type activation, abstract/interface reference construction, missing
public constructors and unsupported closed storage shapes diagnose explicitly.
General Activator overloads, constructed generic layouts, boxing and constrained
calls remain unfinished. This contract does not claim general reflection support.
An unrelated same-named method is compiled normally, not treated as an intrinsic.

## Evidence

`tests/driver/generics.rs` compiles actual Debug/Release DLLs and PDBs, links
canonical ELF with the Rust linker, and executes DMG/CGB ROMs in the existing C#
emulator. New cases check distinct class/root objects, zero defaults, packed
structs, explicit constructor result42 and exactly two side effects across local
and reference-library activation. A forged owner executes the ordinary method and
returns42. Invalid signatures, nonpublic value constructors and invalid reference
activation reject with member/IL diagnostics. Existing source/determinism and
stack/bank guards remain enabled.

The original failure is recorded in `/tmp/koh-generic-construction-red.log`.
Selected compiler/IR/SM83 regression:572/572 passed in315.603s, before the final
mutation additions. Final generic/constructor/virtual/recursion selection:12/12
passed in89.200s (`/tmp/koh-generic-construction-final.log`). These are local
selected results, not whole-workspace or whole-migration acceptance.
