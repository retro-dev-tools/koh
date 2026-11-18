# AllowByRefLike and ordinary closed arguments

The AllowByRefLike generic parameter flag permits ref-like arguments; it does
not require them. Rejecting every declaration with that flag rejected valid
ordinary C# instantiations, including .NET10 enumeration interfaces with byte
arguments. The
[C# constraint contract](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/generics/constraints-on-type-parameters)
and [language specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/classes)
distinguish permission from the argument's actual ref-like identity.

Ordinary closed primitive, reference, array, string, value and closed generic
value arguments now pass this flag check. All other special constraints still
apply. The existing closed type and method instantiation machinery, layouts and
ABI are reused. This is not general ref-like lifetime support.

Actual ref-like value arguments are identified from their raw definition's
System.Runtime.CompilerServices.IsByRefLikeAttribute marker, including closed
generic definitions. Without AllowByRefLike they diagnose an invalid generic
argument; with the flag they diagnose unsupported ref-safety validation. Integer
fields inside a ref struct do not make it an ordinary struct. The reserved
marker contract is inspected as metadata, without executing an attribute
constructor or loading managed runtime implementations.

Marker lookup reuses the Koh attribute constructor decoder. It bounds work with
the shared generic budget and checks constructor signature, prolog/named-argument
payload and duplicates. An unrelated Demo.IsByRefLikeAttribute is not the marker.
No unsafe Rust, new dependency, alternative metadata frontend or host execution
path is introduced. The runtime's
[marker handling](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/coreclr/vm/methodtablebuilder.cpp)
is investigative evidence; the language contract determines accepted arguments.

Four rstest cases in driver::variance::allow_ref_like_accepts_ordinary_arguments
execute eight entry methods in Debug/Release × DMG/CGB:32 ROMs with canonical ELF
roundtrip, the Rust linker and existing C# emulator. Every entry independently
expects42, balanced SP and intact runtime guards. Cases cover int/long, ordinary
and closed generic structs, object references, arrays, strings and a generic
class with the anti-constraint. Actual and closed generic ref-like arguments
reject; removing the flag rejects with the missing-permission diagnostic.
Malformed marker payloads and a duplicate marker reject explicitly. A same-named
attribute in another namespace remains a valid ordinary-struct control.

An earlier Rust mutation assertion in closed_types_preserve_layouts_and_static_initialization
incorrectly expected flag32 on Cell<int> to reject. It now links and executes the
original Objects contract with that flag and independently expects42. Invalid
reference constraints and variant class flags still reject. No original source
test method receives duplicate disposition credit for correcting this assertion.

The real Steps iterator probe now passes this declaration check and reaches the
explicit exception/extra-section rejection. The three original iterator source
methods remain outstanding: structured finally/disposal and generated state
machine runtime dependencies still need implementation and execution tests.

Expanded compiler-library/reference/interface/generic/variance/Int128 validation
passed308/308 in133.153s, exit0;481 cases explicitly unselected
(`/tmp/koh-allow-ref-final.log`). Rustfmt, CSharpier and diff checks pass. The
earlier focused ordinary-argument/layout selection passed8/8 in22.423s before
additional malformed-marker probes were added. No new source-method disposition
is counted and final hosted validation remains outstanding.

```sh
cargo nextest run -p koh-compile --lib --test driver --test references --locked --no-fail-fast -E 'binary(koh_compile) | binary(references) | test(reference_contracts::) | test(generic_types::) | test(interfaces::) | test(variance::) | test(int128::)'
```
