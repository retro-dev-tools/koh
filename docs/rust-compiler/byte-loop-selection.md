# Byte-local updates and branch selection

The counted-store loop now satisfies the original190-dot requirement from
`Sm83LoopCodegenTests.CountedConstAddrStoreLoop_MeetsCeiling`. The existing
`tests/sm83.rs::real_loop_cost_baselines::case_2` retains100 actual stores to
C100 and requires the observed result99, balanced SP, reserved-memory guards
and total measured execution at most19000 T-cycles. This is **PORT** for both
original assertion sites. The previously migrated baseline method is not counted
again. No threshold is raised and no compiler-owned coverage is deferred.

## Selection proof

The backend replaces a load/add-or-subtract-one/truncate/store chain with
INC/DEC(HL) only when the local occupies one byte, the final conversions are
unsigned8-bit truncations, all consumed values have exactly one use, and the
load and store target the same local. Every instruction between them must belong
to the chain or be a constant. Calls, memory effects, unrelated local writes,
escaping old/addition values and signed-extension shapes keep ordinary emission.
The actual local write remains at the original StoreLocal position.

A terminal single-use comparison with equal one-byte operand extents can use
CP with an immediate constant. Signed and unsigned ordering coincide for these
zero-extended physical values0..255. Constant-left operands reverse the predicate;
greater-than tests reject equality before using carry. Wider/negative constants
and unequal extents retain the general comparison path. A constant producer is
omitted only when the comparison is its sole use; edge/return consumers keep it.

The guard may read a byte local directly when its load/conversion chain has no
other users or intervening effects. The bounded selector follows at most eight
conversion links and otherwise retains the ordinary value slot. This is a missed
optimization opportunity, never rejection of valid CIL. Raw immediate stores
also avoid preserving flags unnecessarily: CIL memory operations have no flag
result, and the next selected branch establishes its own comparison flags.

[SM83 encodings](https://github.com/gbdev/gb-opcodes/blob/master/Opcodes.json)
define INC/DEC(HL) as12-T-cycle operations and immediate CP as8. The IR, calling
convention, runtime API, frame layout and preserved interrupt context are unchanged.
No general register-allocation framework or loop-specific method-name dispatch
is introduced. Source IL markers remain even when pure intermediates emit no bytes.

## Independent execution coverage

Ten byte-update rstest cases execute100 ROMs: add/subtract, wrapping at0/255,
signed-byte results, escaping pre-update/addition values and an intervening
write through a real local byref. Inputs0/1/127/128/255 execute before and after
production simplification. These cases passed10/10 before selection was added
(`/tmp/koh-byte-update-before.log`,4.336s, exit0), establishing the starting behavior.

Twenty constant-branch cases execute2800 ROMs: all five predicates, either
constant position and constants escaping through CFG edges. Constants include
0/1/127/128/255/300/FFFFFFFF; ten input patterns include sign and truncation
boundaries. Every expected result uses independent Rust numeric comparisons.
The local-read path and general fallback are both exercised before/after
simplification. Two additional cases execute24 ROMs with an intervening alias
write or an old value escaping the guard, retaining the original snapshot.
These32 new cases execute2924 ROMs in total.

## Measurements and acceptance

The focused35-case run passed35/35 in19.312s, exit0,225 explicitly unselected:
`/tmp/koh-byte-loop-acceptance.log`. It includes all new cases and the three
existing real-Roslyn source probes. The counted source, wrapper, markers and
result observer are unchanged from the previous measurement:

| Probe | Previous total / marked window | Current total / marked window |
| --- | --- | --- |
| Counted100 stores | 51440 / 50760 | 18440 / 17792 |
| Initialize/copy32 bytes | 150904 / 71124 | 137528 / 57780 |

All values are CPU T-cycles on DMG, where they equal dots. The counted acceptance
uses the **18440 total**, including entry, wrapper, markers, all stores, result
read and return transport:184.4 per iteration, below the unchanged190 limit.
It subtracts no presumed overhead and does not mistake the narrower marked window
for the original budget. Like the legacy test, this starts at cartridge entry
without boot-ROM execution; full game boot remains separate acceptance.

The pointer-copy740-dot requirement remains open. Its marker window includes
source initialization and its total includes the full bytewise result observer;
neither measurement is silently substituted for a passing copy ceiling.

Final compiler library, full IR/SM83 and selected PDB/hardware/sample regression
passed944/944 in321.036s, exit0,621 explicitly unselected
(`/tmp/koh-byte-loop-regression.log`), including all four real showcase cases.
Separate compiler rustdoc succeeded with zero doctests. This is local Linux
evidence, not a full workspace or final cross-platform acceptance run.

```sh
cargo nextest run -p koh-compile --test sm83 --locked --no-fail-fast \
  --success-output final \
  -E 'test(byte_local_) | test(byte_constant_branches) | test(real_loop_cost_baselines)'
```
