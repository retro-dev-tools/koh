# Small-magnitude multiplication

The native compiler previously ran a full-width shift/add loop for every
nonconstant product. The independent ROM probe -30*-127 returned3810 correctly
but cost47080T at I32 and185896T at I64. Both exceeded the20000T execution
budget; see `/tmp/koh-multiply-cost-red.log`.

For widths of at least sixteen bits, multiplication now normalizes operand
magnitudes at the original width. If both magnitudes fit eight bits, a register
loop computes the unsigned sixteen-bit product. Otherwise the original
full-width loop computes it. The result is extended and the saved XOR sign
restored at the original width. These operations preserve multiplication
modulo2^N, including unsigned high-bit values and MIN: two's-complement
negation is an additive inverse in that ring. No checked-overflow behavior is
introduced into unchecked CIL multiplication.

One private routine per function and result width shares the generated code.
The first inline attempt made the actual cube renderer exceed its16KiB bank;
`/tmp/koh-cube-fast-math.log` records the explicit capacity diagnostic. Sharing
the helper fixes that capacity failure without creating another runtime ABI or
splitting a function across banks. Inputs occupy scratch0/8, product16, sign24.
The helper compensates for its two-byte CALL return address. Its scratch push
needs two more bytes; the entry guard reserves four rather than two temporary
bytes. No caller-visible register preservation rule changes.

The shared helper's probe costs3828T at I32 and6260T at I64, including test
harness and call overhead. These are SM83 execution measurements, not host
compilation performance. `/tmp/koh-multiply-shared.log` passes9/9 in27.012s.
`tests/sm83.rs` additionally tests255/256 dispatch boundaries, zero, signed and
unsigned high-bit values, MIN, modular overflow, twenty-I64-local frames,
forty chained products in one bank and guarded stack exhaustion.
The complete backend plus real tile-transfer regression passed70/70 in221.574s,
exit0 (`/tmp/koh-multiply-transfer-regression.log`);90 unrelated driver cases
were filtered. This includes all66 backend cases, not only the cost probes.

Read-only review found no arithmetic or stack defect. Whole-game acceptance
remains open: the stronger cube probes still find blank early captures in CGB
Debug/Release and DMG Debug at unchanged budgets. DMG Release passes both
structural captures; the complete4-case result is1passed/3failed in51.034s
(`/tmp/koh-cube-shared-math.log`, exit100). At the early CGB capture, native
symbols identify `Canvas.FillTriangle`; see `/tmp/koh-cube-first-owner.log`.
