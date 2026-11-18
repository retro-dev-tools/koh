# ISA encoding contracts

All four `Backends/Sm83EncodingTests.cs` methods and the one
`Sm83/DisassemblerLengthTests.cs` method now have native replacements for34
lexical assertion sites. The former only looked up the shared C# instruction
table, despite comments suggesting they exercised backend emission; the latter
compared two local decoders. Neither supplied an independent correctness oracle.

Four rstest cases in `tests/driver/symbols.rs` retain all33 named ISA vectors:
ten loads, eleven ALU/pair operations, seven control instructions and five CB
shifts. Literal bytes, immediates, operand identities, lengths and next-PC values
are independently declared from the
[Pan Docs ISA tables](https://gbdev.io/pandocs/CPU_Instruction_Set.html) and
[RGBDS instruction reference](https://rgbds.gbdev.io/docs/gbz80.7).
Canonical ELF/Rust-linked cartridges are read through the existing debugger API.
No test looks up either old table to obtain expected bytes. The explicit-A ALU
text is the debugger's current canonical rendering, not a permanent language/ABI
convention; the equivalent shorthand is not a compiler correctness defect.

The [machine-effect matrix](machine-effects.md) executes all named encodings.
This follow-up adds immediate ADD, ADD HL,DE, INC HL and all four conditional
absolute jumps. ADD HL checks all four carry/12-bit-half-carry combinations and
preserves Z; INC HL includes FFFF→0000 with every flag input preserved. Each
conditional jump takes both outcomes under the16 initial flag patterns. Full
registers/flags, PC/SP, stack and bus effects must match independent Rust results.
The matrix now has14224 probes:443 basic vectors under32 states, plus48 directed
pair-arithmetic states. These remain instruction probes; separate compiled
Roslyn tests cover the actual lowering/backend/optimizer pipeline.

The previous512-vector [boundary test](instruction-boundaries.md) independently
specifies every base/CB length, exact bytes, following NOP and unknown encoding.
It replaces decoder parity rather than reinstating the old MIR decoder.

| Source method | Sites | Decision | Native requirement/location |
|---|---:|---|---|
| `Loads_MatchTable` | 10 | REPLACE | `sm83_disassembly_names_independent_encoding_vectors::loads`; executable register/immediate/memory loads in the machine matrix. |
| `Alu_MatchTable` | 11 | REPLACE | `::alu`; all scalar operations plus immediate ADD and pair arithmetic execute with exact flags/results. |
| `ControlFlow_MatchTable` | 7 | REPLACE | `::control`; RET/CALL/JP/conditional targets, preserved state and stack bytes execute independently. |
| `CbShifts_MatchTable` | 5 | REPLACE | `::cb`; all five exact CB vectors plus the complete256-entry executable CB matrix. |
| `EveryOpcode_HasTheSameLength_InBothDecoders` | 1 | REPLACE | `sm83_disassembly_covers_every_encoding`, both rstest cases; independent lengths replace local decoder agreement. |

All five are cargo-nextest discoverable. No compiler-owned DEFER, duplicate
decoder, ISA production table, assembler, or production emulator change is added.
Private APIs and exact emitted instruction sequences are not frozen.

Expanded validation passed245/245 in32.462s, exit0, with703 explicitly unselected
cases (`/tmp/koh-encoding-contracts-final.log`): full compiler library, opcode
catalog, thirteen disassembly cases, nine machine-effect cases and two
Debug/Release rewrite cases (1536 compiled ROMs). Initial execution exposed only
the test's shorthand/full-A rendering mismatch; correcting that expected label
retains exact bytes and semantics. Rustfmt and diff checks passed. Full current-
head hosted validation and the overall migration remain incomplete.
