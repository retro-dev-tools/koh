# Native assembler and linker

The Rust workspace contains `koh-asm`, `koh-objects` and `koh-link`.
The assembler emits ELF32 SM83 objects by default. The native linker accepts
multiple ELF objects and produces a ROM, optional RGBDS/BGB symbols and KDBG v1
source maps. The compiler and language server remain managed; `Koh.Native`
bridges the compiler's object graph and native executables.

## Build and use

```sh
cargo build --release --locked -p koh-asm -p koh-link
target/release/koh-asm input.asm -o input.o
target/release/koh-link input.o -o output.gb -n output.sym -d output.kdbg
dotnet msbuild build.proj -t:BuildSample2048
```

Windows executables have the `.exe` suffix. Neither Rust binary requires .NET
or RGBDS. The assembler supports `--format elf` (default) and `--format rgbds`
for RGBDS 1.0.1 interoperability. Native `koh-link` consumes ELF, while
`rgblink` consumes the RGBDS variant. KOBJ is removed.

The linker preserves `-o/--output`, `-n/--sym`, `-d/--kdbg`,
`--raw`, hexadecimal `--size=HEX`, `--help` and `--version`.
The default output is the first input with `.gb`, or `.bin` in raw mode.
The default size is $8000. Cartridge output rounds up to a power of two,
updates the ROM size byte and both checksums, and optionally sets
`--cgb-compatible` or `--cgb-only`. Raw output has the exact requested size;
it never changes cartridge-header bytes and rejects overflow.

KDBG v1 is unchanged: its bank and symbol-address fields have limited width.
Requesting debug output with banks above 255 or symbol values outside
0..65535 fails explicitly. The JSON integration report preserves full values;
the symbol file preserves full bank numbers.

## Object and library interfaces

`koh_objects::Object` is the shared typed model.
`read_elf` and `write_elf` validate objects at the serialization boundary.
See [ELF32 SM83 ABI](rust-linker/elf32-sm83.md) for the container.

`koh_link::link(&[Object], &LinkOptions)` returns ROM bytes, resolved symbols
and line maps. Link-time expressions retain their explicit PC section and
bias, including LOAD contexts. Local names are scoped to their input object;
imports resolve only exported definitions. Section placement supports every
Game Boy region, fixed/floating banks and addresses, alignment offsets,
FRAGMENT concatenation and UNION reservations.

`koh-link --emit-elf -o file.o` reads the managed bridge's typed object JSON
from stdin. `--report-json path` writes lossless resolved symbol and line
metadata alongside normal linking. These are host integration operations.

## Verification

```sh
cargo test --workspace --locked
KOH_RGBDS_TEST=1 cargo test --workspace --locked
python3 scripts/verify-rust-assembler.py
python3 scripts/reproduce-macro-unique.py
```

The Python verifier builds serially and compares complete raw ROM bytes with
independent RGBDS 1.0.1 assembly/linking, then checks a WRAM signature using the
real Koh emulator. `--no-build --rust PATH --linker PATH` reuses binaries.
The sample's comparison pins the independently produced RGBDS layout in
temporary native sources; its unconstrained cartridge is also built separately.
Tests, independent oracles, sample execution and physical hardware are distinct
evidence; passing these checks does not establish complete RGBDS compatibility.
