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

Library selection compared [object](https://github.com/gimli-rs/object), which
provides both parsing and ELF writing, with [rust-elf](https://github.com/cole14/rust-elf),
whose documented interface focuses on safe ELF parsing. Using the latter would
still require a writer or a second library. The existing object low-level APIs
cover both directions without inventing a processor identity or hand-writing ELF
headers and string/section tables.

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

For example, a resolved compiler section is represented as:

```json
{"sections":[{"name":"CODE","kind":0,"combination":"Normal","address":336,"bank":0,"align":0,"align_offset":0,"size":1,"data":[118],"patches":[],"lines":[],"source":null}],"symbols":[{"name":"Main","binding":"Exported","definition":{"Label":{"section":0,"offset":0}},"source":null}]}
```

Section and symbol IDs are zero-based integers. Definitions are `"Import"`,
`{"Absolute":42}`, or `{"Label":{"section":0,"offset":0}}`. Expressions use
external enum tags, for example `{"Binary":["Add",{"Symbol":0},{"Literal":1}]}`.
A patch contains `offset`, `width` (`Byte`, `Word`, `Long`), `semantics`
(`Absolute`, `Signed`, `HighMemory`, or `{"PcRelative":{"bias":2}}`),
`pc` (`section`, `offset`), `expression`, and `source`.
Sources contain `file`, positive `line`/`column`, and `expansion` locations;
line records contain `offset`, `count`, `file`, `line`, and optional `expansion`.
The command accepts at most 64 MiB of JSON and rejects unknown enum variants,
invalid models, and output exceeding the ELF resource budget. Standard input is
not an assembly syntax tree, a serialized C# model, or a compatibility object.

## CPU addresses, banks, and placement

ELF32 describes 32-bit container offsets, sizes and integer bit patterns. It does
not make the SM83 a 32-bit processor. A placed label has a 16-bit CPU window
address and a separate bank number. Relocations referencing its address produce
the window address; `BANK(label)` produces the bank, not high address bits.
The flat ROM file offset of ROMX is `bank * 0x4000 + address - 0x4000`.

| Kind | CPU window, inclusive | ABI bank constraints |
| --- | --- | --- |
| ROM0 | 0000–3FFF | 0 |
| ROMX | 4000–7FFF | 1–65535 in objects; linked cartridge limited to 8 MiB, banks 1–511 |
| VRAM | 8000–9FFF | 0–1 |
| SRAM | A000–BFFF | 0–255 |
| WRAM0 | C000–CFFF | 0 |
| WRAMX | D000–DFFF | 1–7 |
| OAM | FE00–FE9F | 0 |
| HRAM | FF80–FFFE | 0 |

Bank constraints describe object placement; they do not promise every cartridge
mapper or hardware mode can select every bank. Cartridge code owns mapper and
RAM-bank switching. I/O registers are not allocatable sections. Fixed compiler
hardware-address symbols use SHN_ABS; allocated compiler RAM globals use actual
RAM sections, splitting contiguous WRAM allocations at CFFF/D000.

Missing address or bank means floating. Fixed sections reserve their locations
before floating sections. Placement is deterministic: fixed address, fixed bank,
descending alignment, descending size, then declaration order. Floating placement
chooses the first fitting aligned gap in ascending bank order. No section crosses
its window; nonempty allocations in the same region/bank cannot overlap.
An alignment constraint is `address mod 2^align = align_offset` (exponent 0–16).
Incompatible fixed addresses, banks and congruences are errors.

Normal section names must be unique across inputs. FRAGMENT sections with the
same name/kind concatenate in input order; labels and PC contexts follow each
fragment's offset, and fragment address/alignment constraints are adjusted for
that offset. UNION sections share a base and reserve the maximum member size;
they are allowed only in RAM. Conflicting combination/kind constraints fail.
Local symbols remain object-local, imports resolve exported symbols by exact
case-sensitive name, and duplicate exports or unresolved imports fail.

## Expression and encoding semantics

Arithmetic uses RGBDS-compatible 32-bit two's-complement values. Addition,
subtraction, multiplication, negation and nonnegative exponentiation wrap at
32 bits. Division rounds toward negative infinity; the remainder follows the
divisor's sign. Division by zero and negative exponents fail. Negative shifts
reverse direction; shifts at or beyond 32 bits produce zero, except arithmetic
right shifts of negative values produce minus one. Comparisons/logical operators
produce zero or one. HIGH and LOW explicitly select bits 8–15 and 0–7.

Absolute encoding accepts signed or unsigned values of its width: byte
−128–255, word −32768–65535, and all i32 bit patterns for long. Signed encoding
uses only the signed range. High-memory byte operands accept 00–FF or FF00–FFFF;
other values fail. JR targets must have a signed/unsigned 16-bit representation;
the displacement wraps in the CPU's 16-bit address space, then must fit −128–127.
An explicit HIGH/LOW operation is distinct from silently narrowing a value.

The current-location value uses the patch's execution section and offset,
independently of where bytes are stored. Its PC-relative bias identifies the PC
after the instruction. This preserves LOAD execution semantics. Multi-symbol
expressions and section queries resolve after placement. BANK of an absolute
constant fails. Unknown operators, undefined symbols/sections, invalid types,
overflowing encodings and unapplied relocations are errors before ROM output.

## ROM, symbols, and debugging

Cartridge images are zero-padded to a power of two, at least 32 KiB and at most
8 MiB. The linker writes the size code at 0148, optionally selects CGB-compatible
80 or CGB-only C0 at 0143, then fixes the header checksum at 014D and the big-endian
global checksum at 014E–014F. Cartridge finalization supplies the canonical boot
logo at 0104–0133; input sections supply the title, cartridge
type and remaining hardware-specific header content.
Raw mode emits exactly the requested size, preserves all header-address bytes,
and rejects overflow instead of enlarging the image.

PR #55 moves logo ownership from producers to cartridge finalization: a native
compiler ROM without those bytes linked successfully but stopped in the boot
ROM's logo check. Emitting another logo table in the compiler would duplicate
header policy. Normal output now always installs the canonical boot logo;
custom or deliberately malformed header bytes require raw mode. See
[PanDocs](https://raw.githubusercontent.com/gbdev/pandocs/master/src/The_Cartridge_Header.md).

The C# assembler/linker golden-byte bridge fixtures use raw mode: their data
deliberately occupies 0104 and is not a cartridge logo. Their byte assertions
remain unchanged. Cartridge boot and header checks use normal output instead.

SYM lists address labels as `bank:address name`; absolute constants are omitted.
Ordering is deterministic by bank, address and name. Debug output is a separate
ELF32 file containing DWARF 5 source ranges and symbol definitions. It preserves
32-bit banked addresses, signed constants and assembly expansion ancestry;
ranges no longer split at 255 bytes. ROM/SYM linking without debug output remains
available through managed `LinkOptions.EmitDebugInfo=false`. The existing
exact-PC stack convention is unchanged. See the [DWARF profile and validation
status](../dwarf-migration.md); object placement/relocations remain ABI 1.

CLI outputs are written through temporary files and renamed, with the ROM last.
Input/output aliasing is rejected, and validation failures preserve existing ROM
outputs. Equivalent noncanonical ELF layouts are outside ABI 1 and fail explicitly.

Canonical ELF output is limited to 64 MiB, including alignment padding, metadata
and tables. The writer checks a conservative layout budget before file allocation;
NOBITS logical size does not count as payload bytes.
