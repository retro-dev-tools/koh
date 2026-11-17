# Native leaf interrupt handlers

The Rust compiler recognizes the existing metadata-driven `[Interrupt("VBlank")]`
contract and aliases Stat/LcdStat/Lcd, Timer, Serial and Joypad. Each vector has
one nongeneric static void parameterless handler on a nongeneric declaring type.
Declarations are roots in every supplied assembly, including reference inputs.
Unknown kinds, duplicate vectors/attributes, malformed payloads and invalid
signatures diagnose. Ordinary calls, captures and selecting an IRQ as cartridge
entry diagnose; an interrupt is a hardware root, not a second ordinary entry ABI.

The current leaf subset permits scalar integer computations, locals, branches
and direct static-field loads/stores. The post-lowering validator rejects calls,
allocation, object/reference operations, arbitrary indirect memory, floating
runtime work and CPU-control instructions, including nested EI. Reachable invalid
bodies report member identity and IL offset. This is an explicit intermediate
capability, not retirement of richer interrupt behavior from the full migration.
Static constructors requiring calls are outside the leaf subset. Multi-byte
shared fields are not atomic; games must control synchronization themselves.

## ABI

Each registered vector jumps to a ROM0 wrapper that pushes AF, BC, DE and HL,
calls an ordinary nonbanked leaf body, restores those pairs in reverse order,
then executes D9/RETI. The body and its internal arithmetic helpers stay in ROM0.
It cannot switch ROM/WRAM banks or call code that does so; interrupted MBC5 state
therefore remains intact even when main code occupies banks257 and above.
Static storage is WRAM0. No global scratch frame or shadow return pointer is
introduced. Handler functions retain ordinary method/PDB provenance, with named
wrapper symbols available for disassembly. Arbitrary-PC debugger acceptance is
still a separate incomplete requirement.

Before emitting main code, compute the maximum handler frame size plus16 bytes:
hardware return PC2, saved registers8, wrapper CALL2 and temporary helper depth4.
Every main function/call guard preserves this additional reserve. Handler guards
use the original floor at C100. Thus even an interrupt during main CALL setup or
a temporary helper has room for its complete leaf execution. Oversized combined
frames diagnose; subsequent ordinary recursion still traps before WRAM0 damage.
Nested/reentrant handler execution is forbidden by the validated leaf contract.

[Pan Docs](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Interrupts.md)
independently defines vector selection, interrupt entry and RETI behavior.
A discovered [RETI emulator defect](reti-reference-correction.md) was corrected
separately rather than copied into compiler output.

## Native acceptance and disposition

`tests/driver/interrupts.rs` is discoverable by cargo-nextest. Real Roslyn
Debug/Release fixtures execute on DMG/CGB through serialized canonical ELF and
the Rust linker. The virtual receiver survives actual VBlank interrupts;
counts2/3/5 are checked against vector entries at LY144 and successive periods
70224±24 system ticks. The tolerance allows instruction-boundary delivery;
[PPU timing](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Rendering.md)
defines154 scanlines of456 dots. Software IF requests exercise all five vectors.
Main's managed functions are forced into distinct banks starting257 in one entry.
The test additionally verifies vector relocations and save/CALL/restore/RETI bytes.

The deep-recursion entry has a post-call effect to retain real stack calls.
At a failing stack-guard branch with SP below C200, the test adapter requests a
Timer interrupt and captures the
interrupted CPU state, and checks exact AF/BC/DE/HL/SP/PC and enabled IME at RETI.
Recursion then reaches stack fault1 with the reserved-memory guard intact.
The earlier effect-free recursion fixture optimized to a loop; its timeout is
not evidence of a stack defect. The original restore failure isolated false IME
with every saved register matching, exposing the emulator correction above.

Five methods/nine assertion sites from `CilInterruptTests.cs` now have native
requirements: vector selection replaces private IrFunction property assertions;
actual firing and Debug/Release counting use independently expected counts and
PPU periods; unknown kind/recognized kind and recursive-handler rejection use
metadata/body mutants with panic capture. The old recursive-handler rationale
about a shared RETI epilogue is not adopted: separate wrappers make normal RET
bodies possible, while the present leaf policy forbids calls for explicit
reentrancy/budget reasons. `TagDispatch_CoexistsWithInterruptHandlerRoots` adds
one method/two assertion sites, preserving both virtual results and IRQ effects.
No old compiler/test definitions are physically removed by this increment.

The native Contracts fixture previously expected every valid void IRQ declaration
to fail. That temporary subset restriction is replaced by the executing roots
above; its negative variant now has an invalid int return and checks the actual
static-void contract. Assertions are not weakened to keep the old restriction.

Verification: broad compiler-library/SM83/scene/dispatch/contract selection passed
372/372 in616.386s, exit0 (419 excluded), before the last metadata-label/duplicate
checks and tighter boundary injection. Final current-input library/IRQ/contract
selection passed200/200 in63.640s, exit0 (427 excluded). This includes20 ROM
executions and28 rejected metadata/body/entry variants across two configurations.
Logs: `/tmp/koh-interrupt-final.log`, `/tmp/koh-interrupt-late-final.log`.
The read-only independent review found no confirmed defect in the ABI accounting,
bank restrictions, RETI correction or adapter; it did not run tests. Local focused
results are not whole-workspace execution or cross-platform acceptance.

Workspace all-features/all-targets checking and separate workspace rustdoc pass
(four crates, no current doctests). Discovery lists1739 cases, including all six
new IRQ cases unignored. The pre-existing unused cube-host helper warning remains.
Logs: `/tmp/koh-interrupt-workspace-check.log`, `/tmp/koh-interrupt-doctests.log`,
`/tmp/koh-irq-discovery.json`. Discovery is not whole-workspace execution.
