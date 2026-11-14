# Koh ELF32/SM83 ABI 1

Koh uses relocatable ELF32, little endian, System V OS ABI, `ET_REL`, `EM_NONE`,
entry point zero and no program headers. `e_flags` is `0x4b4f4801`.
The [official ELF machine registry](https://gabi.xinuos.com/v42/elf/a-emachine.html)
assigns `EM_NONE=0` and does not assign SM83. Koh does not use Z80 or AVR machine IDs.
The mandatory SHT_NOTE `.note.koh` has owner `Koh\0`, type 1, and descriptor
`SM83` followed by little-endian u32 ABI version 1. Unknown versions/flags fail.
This is a private Koh ABI, not a registered SM83 ELF ABI.

`object` 0.40 supplies the low-level ELF writer and ELF32 parser. Its high-level
architecture enum cannot identify SM83; low-level APIs preserve EM_NONE and raw
relocation numbers. Default features are disabled. No separate ELF implementation
or proprietary object wrapper is used.

## Standard records

Section indices 1 through N correspond to model sections, in model order.
Their ELF names are the original names, including duplicates for fragments/unions.
ROM is SHT_PROGBITS with SHF_ALLOC|SHF_EXECINSTR; RAM is SHT_NOBITS with
SHF_ALLOC|SHF_WRITE. `sh_size` is the model size; `sh_addr=0`. For zero alignment offset,
`sh_addralign=1<<align` and the file payload is aligned accordingly. Nonzero
congruence offsets require `sh_addralign=1` and the extension constraint.
Bank, fixed address and congruence alignment live in the extension because ELF's
single linear address space cannot represent Game Boy bank placement.

The standard `.symtab` contains a null symbol followed by model symbols, stable
partitioned with defined local symbols first and globals/imports after them.
Symbols are STT_NOTYPE, STV_DEFAULT, size zero. Imports are SHN_UNDEF/global,
absolute constants SHN_ABS (the i32 bit pattern in st_value), labels reference the
actual section and store their section-relative offset. `.strtab` owns names.
Metadata maps original model IDs to symtab indices and preserves local import
binding, which differs from the binding required for undefined ELF references.

Each section containing patches has `.rela.koh.<zero-based-model-section-id>`.
Its SHT_RELA header links `.symtab`, targets the model section and has entsize 12.
One entry per patch, in patch order. `r_offset` is storage offset. For simple
literal, symbol, or symbol-plus-i32 expressions, `r_sym` is the real symbol index
(or zero for literals), and `r_addend` is the literal. Expression bytes are absent
from the extension for these cases. Complex expressions have type 255, symbol
zero and addend zero; their expression is held solely in the RPN extension.

| Type | Meaning |
| --- | --- |
| 1, 2, 3 | Absolute byte, word, long |
| 4, 5, 6 | Signed byte, word, long |
| 7, 8, 9 | PC-relative byte, word, long |
| 10 | High-memory byte |
| 255 | Extended expression; width/semantics in patch record |

These numbers are local to this explicitly identified ABI. PC-relative patches
retain an independent execution PC and bias; LOAD can store bytes in ROM while
executing at a RAM address. High-memory and signed range checks remain linker
semantics, not a lossy arithmetic rewrite.

## `.koh.meta`

The extension contains only placement, ordering, relocation context/complex
expressions, and debug provenance. Section bytes, sizes/names, and symbol
names/definitions live solely in standard ELF records.
All integers are little endian. `u8` is one byte; `u32` is four bytes; i32 uses its
two's-complement u32 representation. A string is u32 UTF-8 byte length then bytes.
An optional i32/source is u8 tag 0 absent or 1 followed by the value. Other tags fail.
Counts are u32, bounded by remaining input before allocation.

Header: u32 version=1, u32 section count N, u32 symbol count M.
For each model section:

1. u8 kind (ROM0, ROMX, VRAM, SRAM, WRAM0, WRAMX, OAM, HRAM = 0..7),
   u8 combination (normal=0, union=1, fragment=2).
2. Optional i32 fixed address, optional i32 bank, u8 align exponent,
   i32 align offset, optional source.
3. u32 line count; each line: u32 offset, u32 count, string file, u32 line, u32 expansion count,
   then expansion locations (string file, u32 line, u32 column).
4. u32 patch count; each patch: u32 storage offset, u8 width (byte=0, word=1,
   long=2), u8 semantics (absolute=0, signed=1, PC-relative=2, high-memory=3).
   PC-relative semantics is followed immediately by u32 bias.
5. Each patch continues with u32 execution section model ID, u32 execution offset,
   source, u32 expression byte count, then expression bytes. Zero bytes means the
   standard RELA record is authoritative; otherwise exactly one RPN value is required.

Then for each model symbol: u32 standard symtab index, u8 model binding
(local=0, exported=1), optional source.

A source is string file, u32 line, u32 column, u32 expansion count, then expansion
locations (string file, u32 line, u32 column). Line/column are positive; expansion
order is preserved. Line maps and patch sources preserve their existing independent
meaning rather than synthesizing locations from section names.

## RPN version 1

Operands precede operators. Strings and IDs use the encodings above.
Section references are u8 0 + u32 model section ID, or u8 1 + string external name.

| Opcode | Operand / operation |
| --- | --- |
| 1 | i32 literal |
| 2 | u32 model symbol ID |
| 3 | current execution address |
| 4 | u32 model symbol ID, BANK |
| 5 | current execution bank |
| 6, 7, 8 | section reference: bank, size, start |
| 9, 10 | u8 memory kind: total size, start |
| 0x20..0x26 | negate, bit-not, logical-not, HIGH, LOW, bit width, trailing zeros |
| 0x40..0x53 | add, subtract, multiply, divide, modulo, power, bit-or, bit-and, bit-xor, logical-and, logical-or, equal, not-equal, greater, less, greater-equal, less-equal, shift-left, shift-right, unsigned-shift-right |

Unknown opcodes, stack underflow, more/fewer than one result, expression depth over
256, invalid IDs, invalid regions and malformed/trailing records fail validation.
Readers require canonical ABI-1 encoding, comparing reconstructed standard records
and metadata against the input. This rejects contradictory headers, altered RELA,
extra sections, unsupported ELF encodings, trailing bytes and duplicate metadata.
Arbitrary user section names remain supported; internal metadata is the final
matching named section, validated against the canonical layout.

## Native process boundary

ELF `.o` is the sole disk object interchange. The native service returns ELF `data`
and an `object` JSON semantic model for managed frontend inspection. The shared
Rust model uses serde's default external enum tagging and snake_case field names.
The compiler passes the same model JSON through `koh-link --emit-elf -o file.o`;
this is a documented process API, not another persisted object format. Deserialized
models must pass `Object::validate` before ELF writing or linking.
