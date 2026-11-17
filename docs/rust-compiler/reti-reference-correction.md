# RETI reference-environment correction

Native interrupt ABI testing found all saved CPU registers, SP and PC restored
exactly, but IME remained false at the return boundary. The existing emulator
called its delayed-EI helper for RETI as well. The compiler already emitted D9;
changing generated code to accommodate that behavior would encode an emulator bug.

[Pan Docs interrupt semantics](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Interrupts.md)
requires RETI to return with interrupts enabled. Only EI has a following-instruction
delay. The C# instruction bus now distinguishes delayed enabling from immediate
enabling; RETI selects immediate enabling and clears pending EI latch state.
EI retains its existing delayed behavior. No emulator migration is involved.

`InterruptAndHaltTests.Reti_ReenablesInterruptsAtReturnBoundary` checks both an
ordinary return and immediate dispatch of an already-pending interrupt at that
same boundary. These two cases belong to the still-live emulator component,
so they remain in its TUnit project. Compiler-owned IRQ/ABI integration tests
remain native Rust tests driven by cargo-nextest.

After formatting, the complete emulator project passed175/175, zero skipped,
exit0; its build had zero warnings/errors. Completed logs:
`/tmp/koh-reti-emulator-final-build.log` and
`/tmp/koh-reti-emulator-final-tests.log`. The original native failure is in
`/tmp/koh-interrupt-restore-diagnostic.log`; it is not reported as a passing test.
