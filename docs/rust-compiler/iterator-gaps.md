# Iterator investigation and null base metadata

The original three CilIteratorTests methods remain outstanding. A real Roslyn
fixture now reproduces Steps(5), yielding0 through4, consumed by foreach with
independently expected sum10. This is not yet a supported iterator acceptance
test: compilation still rejects the variant IEnumerable<byte>/IEnumerator<byte>
shapes before reaching execution.

The first resolved-reference run instead reported invalid metadata token02000000.
The bug was in Resolver::type_argument: a null TypeDefOrRef Extends index was
converted to a non-null TypeDef token during value/reference classification.
Interfaces legitimately have a null Extends column; interface inheritance uses
InterfaceImpl. This follows ECMA-335 II.22.37, rules9/13, not CLR permissiveness:
[ECMA-335](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).

The fix skips base classification for the null index. Non-null tokens still go
through the existing resolver, and class/interface layout validation remains in
place. It adds no special treatment for IEnumerable or compiler-generated types.

The native reference regression also compiles an ordinary method accepting the
actual framework IDisposable interface, passes null and checks result42 through
ELF roundtrip, Rust linking and the existing C# emulator in Debug/Release × DMG/CGB.
Framework assembly bytes are explicit inputs, using the same installed .NET10
reference helper as retained-IL tests. No host framework method is executed.

The iterator fixture currently asserts the actionable unsupported-variance
diagnostic instead of silently omitting the call or reporting malformed metadata.
This is temporary gap coverage, not permission to retire iterator behavior or
count its three source methods complete. Next work must address exact variant
interface instantiations, then structured finally/disposal and generated state
machine runtime dependencies. Early break, nested/repeated enumeration and
cleanup side effects remain required independent execution cases.

Validation:296/296 compiler-library/reference/interface/generic/variance tests
passed in98.178s, exit0;485 cases explicitly unselected
(`/tmp/koh-interface-base-final.log`). Rustfmt, CSharpier and diff checks pass.
The original resolved iterator probe failed2/2 at the null TypeDef token before
the fix (`/tmp/koh-iterator-resolved.log`); after the fix both configurations
reach the explicit variance diagnostic (`/tmp/koh-iterator-base.log`).

```sh
cargo nextest run -p koh-compile --lib --test driver --test references --locked --no-fail-fast -E 'binary(koh_compile) | binary(references) | test(reference_contracts::) | test(generic_types::) | test(interfaces::) | test(variance::)'
```
