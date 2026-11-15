# Division cost and semantic width

The cube's edge interpolation repeatedly divides small integer magnitudes.
Generic stack-byte long division took51,268 SM83 T-cycles for the independent
I32 test30000/31. This is an execution cost measurement, not host compilation
performance or proof that the cube renders correctly.

Division now uses a sixteen-iteration register algorithm when both normalized
unsigned magnitudes fit sixteen bits. HL carries quotient, BC divisor, DE
remainder; AF saves the loop counter on the hardware stack. Temporary pushes
are sequential and require only the existing two-byte scratch allowance.
Larger magnitudes retain the full-width algorithm. Zero and signed overflow
checks precede dispatch; sign correction follows it at the original width.
Thus I32 -32768/-1 returns32768 rather than acquiring I16 overflow semantics.
Remainder retains the dividend's sign. No C# type is narrowed by this choice.

Measured test-ROM execution, including harness/call overhead:

| Operation | Before | After |
|---|---:|---:|
| I32 30000/31 |51268T|4532T|
| I64 30000/31 |not measured|6212T|

The regression budget is20000T per test ROM, below one third of a normal-speed
70224T frame. It bounds this workload; it promises no per-frame game throughput.
Tests also check exact result967, balanced SP and stack guards.

`tests/sm83.rs` checks unsigned boundary operands0,1,255,32768,65534,65535,
65536,131071; signed quotient/remainder combinations; Native/I32/I64 widths;
ordinary and twenty-I64-local frames; full-width minimum values and faults.
Expected arithmetic comes from independently computed integer division and
remainder, not the previous compiler. The test host uses the existing C# CPU.

Evidence: `/tmp/koh-division-cost-red.log` fails at51268T before the change;
`/tmp/koh-division-sm83.log` passes50/50 backend tests in160.779s;
`/tmp/koh-division-boundaries.log` passes9/9 in34.443s with measured cycles.
The latter contains six expanded boundary cases and two cost cases. The full
backend run preceded those six additions; the focused run validates them.
Cube behavior and VRAM timing require separate acceptance, with unchanged
frame budgets. Portable PDB, classes, interrupts and migration closure remain
outstanding in PR55.

Read-only review checked carry propagation, quotient-bit insertion, large
stack offsets, scratch balance and width-dependent signed overflow; no findings.
