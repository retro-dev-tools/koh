# Compiler symbol-address replacement

All three methods in `Sm83/Sm83SymbolAddressTests.cs` now have native acceptance
in `driver::symbol_addresses::symbols_identify_emitted_instructions_entry_and_mutable_storage`.
The original tests contain seven assertion sites and no parameterized rows.
Their Roslyn fixtures exercise calls, cartridge entry and a mutable byte global;
legacy symbol spelling and fixed placement are implementation details.

| Source method and purpose | Independent requirement | Contract | Decision and native coverage |
| --- | --- | --- | --- |
| `FunctionLabel_SymbolAtActualCodeAddress_NotDoubleCountingCodeBase`: compare Helper's label with its encoded call target | Section-relative ELF symbols and relocations must identify the actual emitted instructions; Helper must execute | Public object/debug addresses; old CODE base and name spelling are private | PORT complete: two explicitly placed code banks, helper-target relocations, every nonrelocated code byte at the resolved address, and independently expected helper writes |
| `EntryFunctionLabel_SymbolMatchesHeaderEntryPoint`: Main equals the header jump | The cartridge header must reach compiler startup, which establishes the stack and initializes storage before invoking the managed entry | Public boot/entry behavior; Main being the header target is an obsolete ABI assumption | REPLACE complete: decode the actual header jump, compare it to relocated startup, distinguish banked managed entry, and execute from PC0100 with poisoned RAM |
| `WramGlobalLabel_SymbolAtWramBase`: counter equals C000 | Mutable global symbols must identify their actual allocated WRAM bytes | Public memory/debug addresses; default C000 placement is private | REPLACE complete: request C020 and C040 placements, compare each field label with its section-relative offset, and independently read the physical bytes from C# pointers |

The fixture retains a statement after Helper's call and gives Helper its own
observable field. It writes Counter1 and Evidence2, leaving Zero at its required
default0. Pointer reads of the explicitly requested physical addresses return
the three packed bytes; a separate Zero read occupies the high return byte.
Expected field values are independent of generated code. The expected byte
order follows the actual field offsets, without requiring a particular allocator
ordering. Startup must clear A5-poisoned RAM at the relocated storage address.
The test also requires balanced stack/guards and untouched heap/CGB bank2.

The native test uses actual Debug/Release Roslyn DLLs and matching Portable PDBs,
then compiles both with and without PDB input. Each configuration executes both
explicit placements. Four rstest cases therefore execute16 ROMs through canonical
ELF round-trip, Rust linking and the retained C# emulator. No byte-identical
Debug/Release output is required. Relocation operands are inspected through their
ELF expressions, avoiding the old unstructured search for a CD byte that could
also occur in data or an immediate. The linker remains responsible for layout
and header finalization; no test-side linker is introduced.

Fresh execution passes4/4 in20.128s, exit0
(`/tmp/koh-symbol-address-execution.log`). No production change was needed.
All three source methods are accounted for: one PORT and two REPLACE, with no
DELETE/DEFER or physical source retirement. This closes three source dispositions,
not four because rstest expands four configurations. The global ledger becomes
232/565 complete (231 native replacements and one earlier obsolete-parser DELETE),
333 outstanding.

Rustfmt and CSharpier checks pass. Workspace/all-feature nextest discovery lists
1,823 cases, including1,646 compiler cases with zero ignored; all four new cases
are discoverable and unignored. The mechanical source cross-check still finds
565 legacy test methods and confirms this group's three methods/seven assertion
sites. These counts do not substitute for the per-method requirements above.
