# Object representation and migration

## Decision

The assembler produces a format-independent object graph. KOBJ v4 is a legacy
interchange adapter, not the definition of that graph. RGBDS RGB9 revision 13 is
a second adapter, pinned and tested with RGBDS 1.0.1. Neither adapter may invent
an operator, substitute zero for a missing symbol, or discard relocation rules.

The initial Rust prototype copied `SymbolName`, `SymbolOffset`, `SymbolShift`
and numeric patch kinds from C#. That duplicates expression meaning and permits
contradictions: `HIGH(label)+1` is not `HIGH(label+1)`, and `LOW(label)` in a word
patch cannot become an unmasked address. Those cached fields are removed from
the public object representation. Adapters derive any restricted encoding from
the authoritative expression and reject expressions they cannot preserve.

## Representation

- Section and symbol IDs identify validated graph entries. Imported symbols are
  explicit declarations; a dangling ID is an error, never an implicit zero.
- Expressions retain all operators and references until assembly or linking can
  evaluate them. Operator kinds are typed; strings are expansion-time values,
  and an unlowerable string/function produces a diagnostic.
- Patch width is independent of its meaning. Absolute integers, signed offsets,
  PC-relative displacements and high-memory addresses have distinct semantics.
  A PC-relative patch records its own PC section, instruction offset and bias.
  The containing storage section may differ, as it does inside LOAD.
- Provenance records definition/use locations and expansion ancestry. File names
  and lines refer to actual sources. Unavailable locations remain absent rather
  than being manufactured as a plausible source line.
- Validation checks section and symbol references, patch bounds, expression
  structure, placement constraints and source records before serialization.

The source parser's expression tree is not an object-format API. Its names are
resolved/interned into the object graph at the assembly boundary. Internal
construction records may be mutable while expanding source; consumers receive
the validated graph. Import permission is an assembly policy, not a file-format
opcode fallback.

## KOBJ v4 interoperability

The current C# `koh-link` reads KOBJ through `KobjReader`; it has no RGBDS object
reader. Therefore replacing KOBJ outright now would break existing workflows.
The v4 adapter retains the established header, section/symbol records, line maps
and representable relocations. The C# reader and linker remain migration tests.

V4 cannot encode arbitrary expression trees, floating alignment, or every range
rule. A request to emit such an object fails explicitly with a format-specific
diagnostic. The assembler must still retain the complete object for a capable
adapter; it must not degrade its representation to make v4 emission succeed.

## KOBJ v5 versus RGBDS

An eventual v5 would need versioned expression opcodes, explicit operand widths
and range policies, a separate PC section/offset/bias, stable symbol references,
source/expansion records, section size separate from stored bytes, and bounded
length-prefixed records with rejection of unknown required features. A magic
version bump without a linker evaluator and bounds-checked reader would not
solve the problem.

RGBDS already specifies expression opcodes, imported symbols, PC context,
section placement and source nodes, and has a maintained independent linker.
Its disadvantages are coupling to an external revision and limited room for
Koh-specific debug/constraint metadata. It is the preferred existing target for
rich relocations, but adopting it as Koh's only interchange format requires a
C# reader/linker implementation or an explicitly approved workflow change.

This PR does not emit a speculative v5 or remove v4. It separates the internal
representation now, preserves v4 where lossless, and tests RGBDS independently.
The next format decision can therefore be made with actual language/linker
requirements rather than inherited C# class fields.

## Evidence rules

Exact KOBJ wire-layout tests protect the existing contract; decoded comparisons
handle symbol ordering and different valid folding choices. Relocation tests
link objects and inspect the resulting bytes. RGBDS acceptance alone does not
prove runtime correctness, and C# agreement alone does not prove language
correctness. Missing imports, unsupported operators, lossy KOBJ expressions,
wrong PC sections and range violations need explicit negative tests.

The sample's historical `\\@` comment is a report to investigate. It is not
evidence of a defect in the current assembler; reproduction results are recorded
separately against the pinned historical C# source and RGBDS 1.0.1.
