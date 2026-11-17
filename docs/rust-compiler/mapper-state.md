# Mapper-state behavioral contracts

The six methods in `Ir/RedundantBankSelectEliminationTests.cs` have native REPLACE
dispositions. Their17 assertion sites contain six verifier checks and11 private
Changed/store-count checks. They never execute a cartridge or observe selected
data. Replacements check actual physical-bank reads, valid callees and ordinary
memory effects rather than requiring a bank-select elimination pass.

`tests/fixtures/BankingContracts` contains ordinary unsafe C# built by Roslyn in
Debug and Release. `tests/sm83.rs::mapper_writes_and_calls_preserve_physical_rom_reads`
lowers actual CIL before optimization, checks runtime and bound arguments before/
after production optimization, verifies every program, and emits canonical ELF.
Distinct data sections are placed in explicit banks by the Rust linker. The
fixture declares MBC5 in its entry object; the linker owns placement, ROM size,
logo and checksums. Tests independently verify the final physical payload bytes
before executing the ROM in the existing C# emulator.

The independent register contract is [Pan Docs MBC5](https://raw.githubusercontent.com/gbdev/pandocs/master/src/MBC5.md):
0x2000 selects the low eight ROM-bank bits;0x3000 selects the ninth bit; bank zero
is accessible in the switchable window; RAM-enable writes do not select ROM.
Payloads at physical bank-relative offset3FF0 are7 in bank0,31h in bank3,42h in
bank4,F5h in bank255 andA3h in bank259. Reads use window address7FF0. Each program
explicitly initializes the mapper bits it uses, without assuming constructor or
power-on values.

| Source method | Independent requirement | Native replacement |
| --- | --- | --- |
| `RemovesConsecutiveSelectsOfTheSameBank` | Re-selecting bank3 preserves the mapped byte | `Same` observes3131h; no required store count |
| `KeepsSelectsOfDifferentBanks` | Selecting3 then4 changes the physical byte | `Different` observes3142h |
| `CallInvalidatesKnownBank` | A callee may change the mapper; a subsequent explicit selection must take effect | Valid ROM0 `Change` selects4; caller observes31h,42h,31h across the call/reselect |
| `OrdinaryMemoryWriteIsTransparent` | Ordinary WRAM writes do not select ROM | `Ordinary` observes bank3's31h and WRAM value42, with stack/guard checks |
| `WriteToAnotherMbcRegisterInvalidates` | Distinguish actual MBC register effects, not a blanket legacy invalidation rule | `OtherRegister` verifies RAM-disable leaves ROM3; `HighBit` observes3→259→3 with the same low bank byte |
| `VariableBankSelectClearsKnownBank` | A runtime selector changes the physical mapping; later re-selection restores3 | `Variable` covers0/3/4/255 and returns31h, selected payload,31h |

Nine low-bank input scenarios and one high-bit scenario, two argument forms,
before/after optimization and both Roslyn configurations give80 ROMs. Every run
checks the full return, restored SP and reserved guards. The fixture executes at
most four ROMs per batch, including the8MiB high-bank images. No memory benchmark
or cycle improvement is claimed.

## ABI boundary

Mapper-changing fixture code and its callees are explicitly checked to use the
standalone emitter's fixed ROM0 text sections. Changing a ROMX mapping while
executing from that window would invalidate instruction fetches. This test-only
near-call harness is not a second production compiler. Production managed calls
retain the [banked cookie/trampoline ABI](rom-banking.md), whose separate
Debug/Release DMG/CGB execution cases remain in the acceptance selection.

These replacements do not declare a managed banked-data API, automatic far data
pointers, IRQ mapper reentrancy or arbitrary raw mapper writes from ROMX safe.
Those remain explicit product/runtime work. The compiler does not recreate the
old bank-shadow model or infer redundant writes without a mapper proof.

Initial mapper acceptance passed4/4 in11.273s, exit0
(`/tmp/koh-mapper-contracts-green.log`), with203 explicitly unselected cases.
Early fixture failures identified an overbroad code-section assertion (the
non-code WRAM stack declaration has a different section kind) and a missing
MBC5 cartridge declaration in the standalone bootstrap. Both fixture premises
were corrected; no production emulator behavior or hardware expectation changed.

Final expanded library/mapper/production-bank-call/neighboring-optimizer checks
passed230/230 in37.535s, exit0 (`/tmp/koh-mapper-contracts-final.log`), with693
explicitly unselected cases. The source audit is recorded in
`/tmp/koh-mapper-source-assertions.log`. This is focused acceptance, not a new
full-workspace or hosted-platform result.
Workspace discovery finds1895 cases,1718 compiler cases, zero ignored
(`/tmp/koh-mapper-discovery.json`). Rustfmt, CSharpier and diff checks passed.
