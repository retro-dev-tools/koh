# Closed method constraint validation

Reachable static generic instantiations validate their actual closed arguments
before registration or optimization. Supported rules include reference/value
special constraints, public parameterless constructor eligibility, nongeneric
base/interface constraints, and constraints referring to another method parameter.
Value types can satisfy their declared interfaces without introducing runtime
boxing into a passthrough method. Struct constraints also validate transitive
calls into reference DLLs.

Independent references are [ECMA-335 II.9.11, II.10.1.7, II.10.5.1,
II.22.20–21 and II.22.23](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).
Constraint references may be recursive; substituting closed arguments and checking
assignability does not require rejecting a cycle. Constructor eligibility requires
the concrete argument type's own public parameterless instance constructor, or a
value type. Inherited, private and protected constructors do not qualify. Validation
does not execute constructors or implement Roslyn's `new T()`/Activator lowering.

Nominal identities remain assembly-scoped. Known Object/ValueType/Enum/Array
constraints follow their CLI meaning. A ValueType constraint alone does not imply
the nonnullable-value special constraint: the core reference type itself can
satisfy the former. Framework definitions use the resolver's checked assembly
identity, not just a System namespace/name. Resource accounting covers constructor
scans, ancestry, interfaces and framework identity lookup.

The shared PE metadata reader rejects invalid constraint owners/targets, duplicate
rows and noncontiguous owner groups. Reached applications reject invalid method
variance/reserved flags, contradictory reference/value flags, multiple class
constraints, Void constraints and value InterfaceImpl rows naming a class.
Unsupported shapes remain distinct from failed constraints.

## Native acceptance

The Rust generic driver builds actual Roslyn DLLs/PDBs in Debug/Release and runs
DMG/CGB ROMs through canonical ELF and the Rust linker. The expected values follow
ordinary typed identity, field, alias and constructor-eligibility rules.

Positive entries cover primitive/packed-value struct constraints, reference/null
arguments, new() eligibility, nominal bases, reference/value interfaces, combined
base+interface constraints, enum constraints and T:U. A patched valid self-parameter
constraint executes the same42 result. Neither C# source syntax restrictions nor
a particular CLR's acceptance are the oracle for that CIL shape.

Metadata mutations test special-constraint violations, method variance/reserved
flags, wrong nominal constraints, multiple bases, invalid value InterfaceImpl,
private/protected/abstract/virtual constructor cases, forged System.ValueType
identity, malformed owner/target rows and duplicates. Bank/stack guards, independent
results, source provenance and fresh/reused resolver ELF equality remain enabled.

The initial generic constraint rejection is reproduced in
`/tmp/koh-generic-constraints-red.log`, exit100. Review found that a value-type
InterfaceImpl could name an implementing class; its new regression fails in
`/tmp/koh-generic-constraints-interface-red.log`, then the validator is corrected.
Owner0 was also incorrectly accepted; its regression fails in
`/tmp/koh-generic-constraints-owner-red.log` before the shared reader fix.
Finished verification results are recorded in progress.md.

The selected compiler/IR/SM83 regression passes572/572 in335.233s, exit0
(`/tmp/koh-generic-constraints-regression.log`). After the reader corrections and
final invalid-constructor/framework probes,213/213 library/reader/reference/driver
cases pass294.935s, exit0 (`/tmp/koh-generic-constraints-final.log`). The final
noncontiguous-row addition passes4/4 in29.294s
(`/tmp/koh-generic-constraints-final-kinds.log`). Those four cases execute48 ROMs
and reject20 metadata mutations per configuration. These are selected local
results; the preceding named-argument head0913e97 separately passed1318/1318
hosted workspace/all-features tests plus rustdoc and .NET fast tests.

## Remaining boundaries

Constructed generic constraints/declaring types, ref-safety for byref-like generic
parameters, primitive framework interface metadata, nonidentical array constraints
and unusual constructor calling conventions still need explicit support.
Generic method-pointer/virtual targets, boxing and constrained calls remain
separate runtime work. Full constraint visibility/metadata auditing remains part
of final compiler acceptance. No general .NET compatibility or completed compiler
migration is claimed, and none of these features is approved for retirement.
