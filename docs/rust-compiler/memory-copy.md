# Native ordered byte copy

`KohIntrinsic("memcopy")` is an address-less, static, nongeneric
`void(byte*, byte*, ushort)` contract. Rust reads and validates the metadata,
lowers the call to typed IR and emits SM83 code. Other address, return,
destination, source and count shapes diagnose at the calling member/IL offset.
The ordinary C# body remains usable under the .NET runtime; it is not a compiler
implementation or a production fallback. The retiring C# compiler is not extended
to recognize this metadata and must be deleted before migration acceptance.

## Semantics and ABI

- Read one source byte, then write its corresponding destination byte, in
  ascending address order. This defines both overlap directions, including
  cascading copies when destination follows source. It is not a memmove snapshot.
- Source/destination are native16 bus addresses and independently wrap modulo
  65536. Count uses int32 CIL evaluation-stack storage and is narrowed to the
  formal unsigned16 parameter. This does not add general CIL `cpblk` support.
- Zero count accesses neither buffer. Stack argument capture and resource guards
  still apply. The operation has no implicit allocation, bounds, bank selection,
  PPU wait or interrupt/reentrancy discipline; raw callers own those requirements.
- Known destination spans touching SVBK diagnose while a pin may be active,
  including transitive calls and signed/narrow conversions. Source-only reads
  of SVBK are allowed. Dynamic raw-address alias safety is not proven.

`MemoryCopy` has native16 destination/source and int32 count, no result. All three
operands participate in dominance, reachability and remapping. It is an ordered
memory effect, so local forwarding cannot keep a stale value across an aliased
write. The instruction keeps its caller's method identity and IL location.

The emitter captures count's low16 bits in scratch, pushes source then destination,
loads count into BC with the four-byte SP adjustment, then pops DE/HL. The guard
reserves those four temporary bytes. DE is destination, HL source and BC count;
zero is checked before either buffer is accessed. Each taken byte iteration emits
`LD A,[HL+]`, `LD [DE],A`, `INC DE`, `DEC BC`, `LD A,B`, `OR C`, `JP NZ`.
The [SM83 opcode timings](https://github.com/gbdev/gb-opcodes/blob/master/Opcodes.json)
sum to56T per taken iteration; the final branch costs four fewer T-cycles.

## Design and measured evidence

The previous library body encoded assumptions about the retired compiler's byte
loop residency and sibling-loop bugs. Reproducing those conventions in Rust was
not a requirement. An explicit raw-memory operation is smaller and more precise
than a speculative loop-recognition optimizer, and preserves ordered MMIO effects.
The C# body is now a simple byte walk, with overlap behavior explicitly documented.

Independent ROM probes first checked every byte and guard before checking time.
The ordinary-CIL implementation passed its memory assertions but failed all8
Debug/Release × DMG/CGB × small/bulk cost cases:
`/tmp/koh-copy-cost-red.log`. Representative normal-speed CPU costs:

| Probe | Previous Rust ordinary-CIL Debug / Release | Native operation, either configuration |
| --- | ---: | ---: |
| Fourteen short/boundary counts plus DFFF endpoint | 1965668T / 1807572T | 94036T |
| 3840 bytes, nonuniform ROM source → guarded WRAM | 6235252T / 5735156T | 217748T |

These are target CPU execution measurements, not compiler time, binary-size or
peak-memory comparisons against the C# compiler. All four source/destination
paired byte offsets are measured. Raw capacity and selected-bank snapshots
are asserted independently of timing. Debug/Release equality alone is not an oracle.

The first native cost selection passed8/8 in50.685s. A subsequent contract selection
passed31/31 in116.418s (`/tmp/koh-copy-contracts.log`), covering whole-bank poison
snapshots, zero/1/2/3/4/5/7/15/16/17/255/256/257/300/3840 lengths, both pointer-wrap
directions, zero bus reads/writes, local aliases, overlap, five invalid attribute
shapes, pin guards, source-only bank reads, large stack offsets and low-stack traps.
Two desktop cases execute224 independent source/destination-offset copies each.
The final workspace run includes the legacy seven-byte overlap, combined
allocation/copy/fill and64/256/1920 rate probes. All924 tests passed in1638.039s,
exit0, zero skipped/retried (`/tmp/koh-copy-workspace-nextest.log`), including747
compiler cases and the actual native SDK build/run workflow. The separate
`cargo test --workspace --all-features --doc --locked` succeeded; all four crates
currently have zero doctests (`/tmp/koh-copy-doctests.log`).

A final four-case recheck adds direct bank2 access: CGB writes exactly the
selected bank while preserving bank1; DMG uses its single physical window.
It passed4/4 in21.380s, exit0 (`/tmp/koh-copy-final-banks.log`,122 unrelated cases
filtered), executing56 contract/rate ROMs. Measured64/256/1920 costs are
9040T/19824T/113008T, giving56T/byte marginal cost in both configurations/modes.
No production code changed after the full workspace run; only this additional
bank assertion/fixture and documentation were added. Review found no blocking
defect; new dedicated instance/generic/varargs metadata mutations are not claimed.

Reproduce the complete local checks with the .NET10 SDK available:

```sh
cargo nextest run --workspace --all-features --locked -j1 --no-fail-fast
cargo test --workspace --all-features --doc --locked -j1
```

`KOH_DOTNET` can select the SDK executable. The recorded run used a separate
Cargo target directory. Optional RGBDS differential checks require
`KOH_RGBDS_TEST=1` and the pinned tool; they were not enabled in this local run.

The unchanged-budget real double-buffered/full-frame cube selection passes all8
Debug/Release × DMG/CGB cases (`/tmp/koh-copy-backend-cubes.log`,17/17 including
typed IR and backend checks). Cube tests serialize/read canonical ELF before linking.
Racing-beam effect validation, classes, source debugging, interrupts, remaining
test dispositions and legacy compiler deletion are still required by PR55.
