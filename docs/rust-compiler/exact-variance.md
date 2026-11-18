# Exact variant interface instantiations

Closed instantiations of variant interface declarations now bind and call using
their exact nominal interface identity. A variance annotation is not itself a
conversion. The old blanket declaration rejection prevented ordinary calls and
blocked compiler-generated enumeration interfaces before their actual IL.

Examples IProducer<Payload>, IConsumer<Payload> and IProducer<int> now execute
their actual interface dispatch, returning independently expected42. Value-type
arguments are allowed; they do not gain reference variance conversions. The
existing object layout, call ABI and exact interface dispatch remain in use.
No compiler-generated iterator name or BCL type is special-cased.

Declaration checks follow
[ECMA-335 II.9.7](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf):
return types are positive, parameters and method generic constraints negative,
inherited interfaces positive, and nested generic variance composes positions.
Byrefs/pointers require invariant use; arrays preserve position. Ordinary static
members are exempt. Static virtual declarations are checked under the
[.NET augment](https://github.com/dotnet/runtime/blob/main/docs/design/specs/Ecma-335-Augments.md).
This validates unused declarations; it does not add static interface dispatch.
Reserved/conflicting flag words still diagnose before variance-position checks.
Signature forms without supported validation diagnose explicitly. Recursive
signature work shares the bounded generic work counter and64-level depth limit.

Conversions between different instantiations remain unsupported. Compile-time
coercions diagnose; explicit castclass/isinst to a variant interface reject
until variance-aware runtime matching exists. This prevents silently returning
false for a valid covariant type test using only exact identity tables.
Boxing and variance adaptation for delegates are not implemented by this change.
Subsequent [AllowByRefLike handling](allow-ref-like.md) admits ordinary closed
arguments while actual ref-like arguments still require lifetime support.

Rust cases in driver/variance.rs execute Debug/Release × DMG/CGB, with canonical
ELF roundtrip and the existing C# emulator. Six entries cover exact producer and
consumer dispatch, value return, nested covariance/contravariance/array positions,
an invariant byref control and a static virtual declaration. Metadata mutations
reverse producer/consumer direction, mark invariant byrefs variant, or put a
static virtual return in a negative position; each must diagnose. Existing
reserved/contradictory flags, special reference constraints, delegate callbacks,
heap guards and boxing rejection remain checked. Cross-instantiation conversion,
type-test and cast probes must reject explicitly. No old compiler result is the
execution oracle and no legacy source-method disposition receives new credit.

Subsequent [structured cleanup and iterator support](finally-iterators.md) now
executes the Steps fixture and closes its three source methods. Fault clauses
and exceptional unwinding remain outside that explicitly documented contract.

Final compiler-library/reference/interface/generic/variance validation passed
300/300 in99.675s, exit0;485 explicitly unselected
(`/tmp/koh-variance-exact-corrected.log`). Rustfmt, CSharpier and diff checks pass.
An earlier expanded run failed12 cases at Roslyn CS8920: an unimplemented static
abstract interface cannot itself be a generic argument. The corrected static
virtual fixture provides a default body and retains the same signature check;
no assertion is dropped and static-interface call support is not claimed.

```sh
cargo nextest run -p koh-compile --lib --test driver --test references --locked --no-fail-fast -E 'binary(koh_compile) | binary(references) | test(reference_contracts::) | test(generic_types::) | test(interfaces::) | test(variance::)'
```
