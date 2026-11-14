# Shared native objects

`rust/koh-objects` owns the object graph used by both the native assembler and
linker. ELF32 SM83 is the native interchange format; see
[the ABI specification](rust-linker/elf32-sm83.md). KOBJ serialization and its
managed reader/writer are removed.

Sections retain region, combination mode, address/bank constraints, alignment,
data, relocation patches and source maps. Symbol IDs and section IDs identify
validated graph entries. Imports are explicit and never resolve to zero.
Expressions retain every supported operator until placement and linking.

Patch width and meaning are separate: absolute, signed, PC-relative and
high-memory patches have explicit range checks. A PC-relative patch records its
own PC section and bias; LOAD may place code and its execution PC in different
regions. Provenance includes definition/use locations and expansion ancestry.

The ELF reader validates IDs, bounds, section constraints and expression shape
before exposing the model. The linker rejects duplicate exports, unresolved
imports, incompatible section combinations, placement conflicts, arithmetic
errors, out-of-range patches and images above the cartridge limit. Unsupported
output-format values fail rather than being silently truncated.

The RGBDS RGB9 revision 13 assembler adapter remains independently exercised
against RGBDS 1.0.1. It is an interoperability adapter, not the native linker
input. Native ELF metadata and relocation encodings must preserve the shared
model; neither format adapter may invent an operator or discard a PC rule.
