# IR round-trip test dispositions

The seven baseline `Ir/IrRoundTripTests.cs` methods contain20 assertion call
sites, without parameter attributes. Six methods receive native replacements;
one obsolete parser-only method receives a DELETE disposition. Legacy test
definitions remain until the final compiler deletion.

| Source method / assertion sites | Decision | Native location and independent requirement |
| --- | --- | --- |
| `Canonical_ParsesAndVerifiesClean` /1 | REPLACE, complete | `driver::ir_contracts::canonical_ir_behaviors_execute_from_real_cil`: real Roslyn Debug/Release CIL passes native verification, emits canonical ELF and executes typed calls/branches/data access. |
| `Canonical_HasExpectedStructure` /9 | REPLACE, complete | Same test checks initialized table bytes1/2/3, mutable counter1, both comparison outcomes, method-call result260 and switch outcomes11/22/33. Module/function/block counts and names were private representation assertions. Managed functions are assigned distinct ROM banks before linking and execution. |
| `Print_Parse_Print_IsStable` /1 | REPLACE, complete | Canonical object emission is deterministic across repeated compilation and ELF read/write; the object then executes. Private C# IR text is not preserved. |
| `EntryFlag_RoundTrips` /4 | REPLACE, complete | `driver::native_driver_emits_one_linkable_deterministic_elf` explicitly verifies `Main` is after another method, executes the default root42 and explicit alternate roots0/0x89ABCDEF for Debug/Release DLL/EXE inputs. Metadata entry identity replaces a text flag. |
| `Bitcast_RoundTripsAndVerifies` /3 | REPLACE, complete | `driver::ir_contracts` executes six ushort→pointer→ushort patterns:0/1/0x7fff/0x8000/0xc000/0xffff. Existing `ir::float32::float_bits_are_distinct_and_backend_legal` additionally verifies same-width integer/float bit transport. |
| `Parser_RejectsNumericOpToken` /1 | DELETE, complete decision | Tests `Enum.TryParse` behavior in a private text parser. Production source has no `IrParser.Parse` caller, and the native compiler accepts PE/CIL rather than this text. No parser/enum-number input boundary survives to port. Malformed PE/IL remains covered by native frontend/CLI tests. |
| `Bitcast_RejectsMismatchedSize` /1 | PORT, complete | `ir::construction::size_changing_bitcasts_are_rejected`: native16↔I32, I32→F64 and I64→F32 fail verification and emission. Existing float IR confusion cases extend these checks. |

The new fixture turns previously unexecuted canonical IR into observable ROM
behavior. Four Debug/Release × DMG/CGB cases run two roots each: eight ROMs,
36 canonical packet words and24 pointer packet words. All words are compared
byte-for-byte; return value, stack balance, frame guards and CGB bank2 isolation
are checked. Initialized data is preserved as a language behavior without
requiring the old IR's ROM-global representation or module/member counts.

The first harness expected a direct pointer→int cast to zero-extend. That
expectation was unjustified: [C# §24.5](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/unsafe-code#245-pointer-conversions)
leaves pointer/integer mapping implementation-defined, and Koh deliberately has
16-bit native values. The fixture now explicitly converts through unsigned
`ushort`, then widens to I32, while retaining all address bits and all packet
checks. No emulator or compiler output was altered to match the failed test.

The separate text format has no in-repository production consumer; maintaining
a second frontend solely for private round-trip assertions would add an input
surface without improving C# authoring or debugging. Rust typed construction,
verification and canonical ELF artifacts cover those developer requirements.
No game-facing capability is retired by this decision.

Quantified result:171/565 source dispositions complete,394 outstanding:
170 native behavioral replacements and one obsolete parser-only DELETE
decision. No legacy definition is physically removed yet. Full migration
remains incomplete.

Verification:40/40 selected nextest cases passed in91.011s (exit0), including
four strengthened CLI DLL/EXE entry cases, four canonical/pointer execution
cases, construction/bitcast IR checks and floating execution/IR regressions.
744 cases were outside the explicit selection. Completed log:
`/tmp/koh-ir-contracts-final.log`. Rustfmt and CSharpier checks pass.

A final bank-presence assertion also passed4/4 cases in15.755s, exit0,
confirming multiple managed banks for each executed root
(`/tmp/koh-ir-contracts-bank-check.log`). The final formatting-only correction
passed Rustfmt; no semantic code changed after this run.
