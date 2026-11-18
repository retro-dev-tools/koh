# Native compiler driver and cartridge startup

The native `koh-compile` executable reads a managed assembly and explicitly
supplied implementation references. It never executes their managed code.

```sh
cargo build -p koh-compile -p koh-link --locked
target/debug/koh-compile game.dll -r Library.dll -o game.o
target/debug/koh-link -o game.gb game.o
```

The reusable library boundary is `compile::object(&Resolver, Option<&str>)`.
`compile::object_with_debug` additionally accepts optional PDB bytes in assembly
order and validates them. The CLI reads matching adjacent Portable PDBs when present;
`--pdb PATH` overrides root symbols and `--no-pdb` disables discovery. Source
ranges use existing ELF/KDBG metadata; [details](source-debugging.md).
The default entry is the managed PE entry point, or a unique static `Main` in
a library. `--entry Namespace.Type::Method` selects an explicit entry; nested
types use `Namespace.Outer+Inner::Method`. Entries currently take no arguments
and return void, Int32 or UInt32. Ambiguous entries are errors.

Reachable [closed static generic methods](generic-methods.md) now specialize
supported primitive/string/object/vector arguments, including transitive
reference-DLL calls. Other generic shapes remain explicit unfinished boundaries.

## Object and startup ownership

The compiler combines emitted functions into one canonical `koh_objects::Object`,
remapping section and symbol references before calling the existing ELF writer.
It does not place relocatable sections, resolve addresses or finalize headers.
Those remain Rust linker responsibilities. Compatible stack UNION contributions
share one reservation; incompatible reservations and duplicate definitions fail.

Startup disables interrupts, initializes SP to D000 and clears IF/IE before
calling the selected entry. Normal return leaves its 32-bit result in DE:HL
(zero for void), restores SP to D000 and stops at `__koh_exit`. Trap paths stop
at `__koh_abort`, with A identifying stack exhaustion (1), arithmetic failure (2)
an unexpected vector (3), or a null managed reference (4). These symbols support independent execution tests.
This is an initial startup contract, not completed interrupt/runtime support.

## Input and output safety

Paths use native OS strings, including paths with spaces. Canonical paths
deduplicate references and prevent overwriting an input assembly or supplied PDB. Limits are
64 MiB per image, 256 MiB combined and 1024 distinct images. Compilation completes
before output is written through a newly created temporary file and rename.
Rejected inputs preserve a previous output. Reference order does not change the
tested output bytes.

Canonical Koh.GameBoy intrinsic attributes are identified by assembly and type
identity, not a coincidentally matching simple name. [Register/region access](hardware.md)
uses explicit bus operations; unsupported intrinsic kinds diagnose. Interrupt
metadata in any supplied assembly also diagnoses, including uncalled handlers.
These are temporary boundaries, not decisions to retire hardware capabilities.
[Type/module constructors](initializers.md) now execute through once guards.

## Evidence and remaining integration

Nine Rust driver cases cover Debug/Release and executable/library Roslyn output,
entry selection, nested names, transitive calls, recursion, stack exhaustion,
deterministic ELF, output safety and metadata guards. They execute 18 additional
ROM scenarios through the existing C# emulator. Independent assertions check
return values, restored SP and protected-memory sentinels.

A separate process demonstration under `/tmp/koh native compiler demo` built
C# with SDK 10.0.115, invoked native koh-compile and koh-link, and executed both
DMG and CGB-only ROMs. Both returned `00 0000002A D000 OK`, exit 0. The CGB ROM
header flag was independently checked as C0. The ELF was 11200 bytes and each
ROM 32768 bytes. These are functional observations, not performance comparisons.

The SDK still invokes the C# compiler. Portable PDB/KDBG, graphics samples,
complete runtime/language support and removal of that compiler remain required
in this same PR. This increment does not establish product acceptance.
