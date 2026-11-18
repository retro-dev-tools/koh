# Immediate integer arithmetic

The SM83 backend now selects immediate ADC/SBC/AND/OR/XOR forms when the right
operand is a known integer constant. It reuses the existing bytewise arithmetic
path, source/destination slots and range-derived storage widths. It avoids the
right-hand frame address and its per-byte HL increment. Nonconstant operands
retain the existing path. No frontend, ABI, optimizer framework or API changes
are needed.

Carry starts clear as before. Loads, stores and16-bit BC/DE increments between
bytes preserve it, so ADC/SBC retain multi-byte carry/borrow. Literal bytes beyond
the encoded64-bit IR constant are zero, matching existing constant emission for
wider results. The [instruction reference](https://raw.githubusercontent.com/gbdev/rgbds/master/man/gbz80.7)
documents immediate carry arithmetic and register-pair flag behavior; existing
independent ISA tests and executed full-width results supply regression evidence.
No flags are assumed live across an IR arithmetic operation.

Four new rstest cases exercise32/64-bit values at ordinary and over127-byte frame
offsets: all five operations, five constants and eight runtime values. These800
ROMs cover zero, byte/word carries, borrow, sign-bit/all-one values, truncation,
and large-frame address setup. Expected values come from Rust wrapping arithmetic
and bitwise operations, not the old compiler or emitted opcode shapes. Existing
128-bit, alias, stack, checked-arithmetic and Debug/Release execution tests remain
part of the broader backend suite.

Marked Release-CIL costs improve from70900 to68900 T-cycles for100 counted stores
(709 to689 per iteration,2.82%), and85320 to83528 for32 initialized/copied bytes
(2666.25 to2610.25,2.10%). Empty markers remain96. The pointer total includes
initialization; result observation remains outside both windows. These are
measured fixture improvements, not compiler wall-time or whole-game claims.

An earlier byte-result-only experiment passed225 tests but left both loop costs
unchanged. It was replaced before commit: these CIL additions need wider
intermediate results. The final change handles immediate operands at every
supported integer result width without changing arithmetic semantics.

The original190/740-dot ceilings remain open. This is one measured reduction in
frame traffic, not completion of loop optimization or the full migration.

Validation: compiler library and full SM83 target436/436 passed127.245s, exit0,
zero skipped (`/tmp/koh-alu-immediate-final.log`). Real-C# int128/wide arithmetic
8/8 passed98.049s, exit0,514 explicitly unselected
(`/tmp/koh-alu-immediate-wide.log`). Marked timing output is recorded in
`/tmp/koh-alu-immediate.log` (12/12 passed30.538s). Stable fmt/diff checks passed.
Discovery1930 workspace/1753 compiler/zero ignored. Current-head hosted
cross-platform validation remains unproven; the legacy compiler remains pending
final replacement acceptance.
