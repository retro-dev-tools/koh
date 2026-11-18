# Executable instruction effects

The remaining15 `Backends/MirDecoderTests.cs` methods now have native behavioral
replacements for66 lexical assertion sites. Together with the four
[instruction-boundary replacements](instruction-boundaries.md), all19 methods
and81 source assertion sites have dispositions. No compiler-owned case is
deferred, and the legacy test definitions remain pending final compiler removal.

## Independent expectations

`tests/driver/machine_effects.rs` declares opcode families and computes expected
results in Rust from ordinary byte arithmetic and the
[RGBDS instruction reference](https://rgbds.gbdev.io/docs/gbz80.7),
[gbdev flag tables](https://gbdev.io/gb-opcodes/optables/) and
[Pan Docs interrupt contract](https://gbdev.io/pandocs/Interrupts.html).
Neither the legacy decoder nor emulator implementation supplies expected values.
Addition/subtraction use wide sums, unsigned borrow and nibble carry; comparisons
preserve A. Bit operations and shifts explicitly calculate their outputs and
flags. Each probe compares every byte register, all four flags, PC, SP, IME,
HALT state, four stack bytes, the watched WRAM byte, LCDC, and ordered data-bus
accesses including their values. Unchanged state is asserted as strictly as the
result. No instruction-cycle or STOP/power-on behavior is claimed.

Nine rstest cases execute436 opcode/sequence vectors under two register patterns
and every one of the16 initial flag patterns: **13952 fresh instruction probes**.
The families are63 register/memory loads,16 immediate loads,64 ALU instructions,
16 INC/DEC instructions, all256 CB instructions and21 control/memory sequences.
The immediate set checks both zero and a nonzero byte. The two register patterns
include zero, nibble/byte boundaries and opposing sign bits; they are not an
exhaustive enumeration of operand values. All16 flag inputs expose preserved,
cleared, set and carry-dependent flag behavior.

The Rust ELF fixture and Rust linker produce each cartridge. A small `machine`
command in the existing test-only `DebuggerHost` executes one instruction (or
EI followed by NOP) in a fresh existing C# emulator instance and returns raw
state. It does not decode instructions or calculate answers. A bounded memory
hook records all writes and non-ROM reads; ROM instruction fetches are excluded.
Sixteen events is a checked capacity, not silent truncation; these probes need
at most two data events. This branch has no dependency on compiler algorithms.

These are instruction tests with explicit preconditions: PC/SP/registers/memory,
LCD off, IE/IF zero and an initial IME value. They do not run the boot sequence or
assume constructor/post-boot defaults. LCDC loads therefore read the explicitly
written zero, not an emulator startup value. NOP/DI begin with IME set; EI and
RETI begin with it clear. RETI must enable it immediately; EI alone must not,
while EI then NOP must. IE/IF zero excludes interrupt dispatch from these probes.

There is no native mutable MIR or post-emission peephole decoder to duplicate.
Private register/flag effect bitsets are replaced by observed results and
preservation. A conservative old read-mask entry does not require a fake data
dependency: XOR A may correctly lower to a constant zero. The compiler's typed-IR
memory, optimization and call rules are checked separately, including the
[traced real-Roslyn rewrite tests](rewrite-legality.md). Standalone instruction
probes do not by themselves prove CIL lowering or register allocation correct.

## Source disposition

All15 decisions are REPLACE and discoverable in cargo-nextest under
`driver::symbols::machine_effects::sm83_machine_effects_follow_independent_isa_contracts`.
Every row replaces private effect masks with an independently justified hardware
behavior. The numeric column counts lexical source assertions, not expanded runs.

| Source method | Sites | Required behavior and native case |
|---|---:|---|
| `Decode_XorAWritesAAndAllFlags` | 4 | `alu`: XOR A returns zero and exact flags, preserves other registers, no data access. Private conservative A-read mask is not mandated. |
| `Decode_LoadRegRegHasNoFlagEffect` | 4 | `loads`: B receives C; all flag combinations and unrelated registers preserved. |
| `Decode_LoadImmediateWritesRegisterOnly` | 3 | `immediates`: A receives zero/nonzero immediate without changing flags or other registers. |
| `Decode_AdcFromMemoryReadsCarryAAndHl` | 7 | `alu`: ADC reads the exact [HL] byte once, combines A and initial carry, preserves HL and sets exact flags. |
| `Decode_IncByteWritesZnhButNotCarry` | 4 | `inc_dec`: INC B computes byte/nibble/zero behavior and preserves initial C. |
| `Decode_CbBitTestsWithoutWritingRegister` | 3 | `bit`: BIT 0,A sets exact Z/N/H, preserves C, A and other state. |
| `Decode_ControlFlowIsClassified` | 6 | `control_memory`: actual RET/CALL/JP/JR/JR NZ/HALT PC, SP, stack accesses and halted state, including both conditional-branch outcomes. |
| `Decode_PushPopAfTouchAllFlags` | 6 | `control_memory`: PUSH AF stores high then low bytes; POP AF restores A and flags with low-nibble masking; exact SP/stack/data trace. |
| `Decode_MmioLoadStoreLengthsAndEffects` | 8 | `control_memory`: E0/F0/E2/F2 LCDC and EA/FA WRAM accesses, exact addresses/values/order and PC increments, unaffected registers/flags preserved. |
| `Decode_HlPostIncrementLoadWritesHl` | 4 | `control_memory`: 2A loads the old [HL] byte then increments HL; 32 also checks store/post-decrement. |
| `Decode_EveryLoadRegRegLeavesFlagsUntouched` | 3 | `loads`: all63 encodings including memory endpoints; 76 separately checked as HALT. |
| `Decode_EveryAluOpWritesAllFlagsAndReadsA` | 2 | `alu`: all64 encodings with exact ADD/ADC/SUB/SBC/AND/XOR/OR/CP results/flags; CP preserves A. |
| `Decode_EveryCbFamilyHasTheExpectedFlagShape` | 4 | `rotate_shift`, `bit`, `reset`, `set`: all256 encodings, every flag input; BIT never writes data, RES/SET preserve flags. |
| `Decode_EveryIncDecByteWritesZnhNotCarry` | 2 | `inc_dec`: all16 encodings, including read/write [HL], exact arithmetic and C preservation. |
| `Decode_DiEiAreSideEffectingButNopIsNot` | 6 | `control_memory`: NOP preserves IME and all data; DI disables; EI delay and RETI enabling are observable, with no unrelated clobber. |

## Validation

Initial nine-family execution passed9/9 in10.494s, exit0
(`/tmp/koh-machine-effects-initial.log`). After removing the new unused import
and formatting the C# adapter, expanded validation passed **241/241 in29.956s**,
exit0, with703 explicitly unselected cases (`/tmp/koh-machine-effects-final.log`).
It includes the complete compiler library, CIL catalog, nine instruction-effect
cases, nine instruction-boundary cases and both Debug/Release traced rewrite
cases (1536 actual compiled ROMs). Rustfmt, CSharpier and diff checks passed.
Workspace discovery lists1915 tests, including1738 compiler cases, zero ignored
(`/tmp/koh-machine-effects-discovery.json`). The existing cubes dead-code warning
remains outside this changed test target.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --test opcodes --test sm83 --locked \
  -E 'binary(koh_compile) | binary(opcodes) | test(sm83_disassembly) | test(sm83_machine_effects) | test(machine_rewrites_preserve)'
```

Production compiler/emulator/debugger code and ABI are unchanged. This closes
the inventoried source group, not overall compiler/runtime/product acceptance;
final current-head three-platform validation remains required.
