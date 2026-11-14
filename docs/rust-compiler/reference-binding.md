# Native reference binding

`koh_compile::resolve::Resolver` binds metadata from explicitly supplied PE/CLI
images without executing them. Definitions retain assembly index and metadata
token identity. Parsing or binding a method does not yet compile its body.

## Identity and reachability

An AssemblyRef must match assembly name, all four version components, culture and
public-key token. Full public keys are reduced using the CLI SHA-1 token rule;
this is identity matching, not signature authentication. ASCII name/culture case
is folded; namespace and type/member names remain case-sensitive. General CLR
retargeting, Windows Runtime binding, multi-module files and directory-search
fallback are deliberately not implemented by this boundary.

Duplicate full assembly identities are errors, rather than first/last-file wins.
The future driver must deduplicate repeated paths and choose the implementation
image when both reference and implementation assemblies have the same identity.
A metadata-only reference may bind successfully but cannot supply executable CIL;
body loading remains a distinct requirement. The SDK must provide Koh.GameBoy's
implementation for reachable runtime methods.

Only dependencies needed by the requested reference are resolved. Loading a game
does not eagerly bind every attribute/framework reference or embed BCL code.
Transitive body reachability and dead-code elimination remain lowering work.

## Types and members

- TypeDefs, scoped TypeRefs, nested types and explicit type forwarders bind to
  stable definitions. TypeSpec generic parents retain their type arguments.
- Methods match name, calling convention, generic arity, return type and parameter
  types; named signature types resolve across assembly-local token spaces.
- Fields match their full declared signature and retain generic parent arguments.
- MethodSpecs retain method arguments and their originating assembly. Generic
  instantiation arities are checked recursively in bound signatures and arguments.
- Generic parameter indices must be unique and contiguous in their metadata owner.
  Contextual use of VAR/MVAR and generic constraints still need the caller's typed
  instantiation context; binding does not claim those checks are complete.
- Nonzero coded indices with row zero are rejected. They cannot alias a local
  type through the metadata library's row-only null predicate.
- Array rank/element type determine signature type identity; size/lower-bound
  annotations do not distinguish array types. Runtime array bounds remain data.

Inherited nongeneric lookup is implemented. Inherited generic substitution and
vararg call-site binding remain explicit incomplete native paths, not retired
product capabilities or claimed supported execution. Array pseudo-methods require
array lowering rather than invented MethodDefs. Member access checks and virtual
dispatch are also separate from selecting a definition.

## Bounds and diagnostics

The set accepts up to 1,024 images and 256 MiB combined input. Metadata indexes
limit copied names to 64 MiB. Types/member names and generic-owner arities are
indexed once; nested owner graphs reject cycles without recursive traversal.
Positive type bindings are cached within the immutable image set. Type/forwarder
traversal and signature validation have a 64-level bound. Candidate signature
search is limited to 4 MiB per declaring type. Failures are errors, never guessed
methods, zero-valued fields or silently omitted code.

The metadata preflight now supplies the corrected Module row layout and the
standard export/resource/deprecated assembly row widths missing from clrmeta
0.1.0's size calculator. Actual table parsing stays in the dependency. Narrow/
wide-index layout tests and real forwarder images exercise these corrections.

## Evidence

Rust tests build real Debug and Release Entry → Bridge → Runtime projects with
Roslyn, including overloads, generic methods/classes, fields and nested types.
They verify transitive target identity, expected integer widths, retained generic
arguments, exact-version failures, duplicate identities and type forwarding.
Another case binds System.Object's constructor from the SDK's strong-named
System.Runtime reference metadata without executing either assembly.

Malformed regressions patch real fixture metadata in place: invalid nonzero
zero-row ResolutionScopes must not resolve to an existing local type, and changing
a generic definition's declared arity must invalidate nested signature and
MethodSpec instantiations. Both cases failed before their fixes. No old compiler
output is their oracle. These tests do not establish ROM execution or replace the
legacy compiler-owned behavioral suite yet.

References: [ECMA-335 II.6, II.22 and II.23](https://ecma-international.org/publications-and-standards/standards/ecma-335/),
[.NET assembly identity](https://learn.microsoft.com/dotnet/standard/assembly/names).
