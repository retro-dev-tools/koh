# Machine rewrite behavioral contracts

All38 methods in `Backends/Sm83PeepholeTests.cs` have native dispositions: one
PORT for six independent instruction lengths and37 REPLACE decisions for private
byte-edit predicates. The source has43 assertion sites. No method is deleted or
deferred, and the C# definitions remain until final compiler-test deletion.

## Selected architecture

The native backend emits from verified typed IR; it does not expose the old
peephole edit list, opcode-length helper, safe-call whitelist or per-module
dead-store flag. Recreating that postprocessor would duplicate machine models
and require new flag/alias/relocation proofs. Instead, private locals can be
optimized in typed IR, while native bus reads/writes remain observable effects.
This is deliberately conservative for hardware memory. Existing game timing
contracts remain separate; no cycle or code-size improvement is claimed here.

Ordinary managed calls clobber flags under the documented ABI. Old conservative
"flags may survive RET" rules are not a new public calling convention. Explicit
CIL values, multi-byte carry/borrow/shift consumers, branch results and call
returns must remain correct. IRQ entry/RETI has its distinct AF-preservation
contract, retained in [IRQ execution tests](irq-calls.md).

## Native evidence

`tests/fixtures/RewriteContracts` is built with Roslyn in Debug and Release.
`tests/sm83.rs::machine_rewrites_preserve_carry_control_and_observed_memory`
lowers its real CIL before optimization, executes runtime/bound argument forms
before/after production optimization, and verifies every program. Canonical ELF
and the Rust linker produce the ROMs executed by the existing C# emulator.

Eighteen memory/control shapes across eight input pairs give1152 ROMs. Full64-bit
carry/borrow and mixed left/right shifts give384 more, covering carries through
zero bytes, wrapped subtraction, both high-bit patterns and counts0/1/7/8/15/
31/32/33/63/64/65/127. Independent Rust wrapping arithmetic and masked shifts
define expected results. The initial1536 executions check full return buffers, restored
SP and reserved guards. Five subsequent [indexed-address shapes](indexed-addresses.md)
add320 executions, bringing the current fixture total to1856.

A small test-adapter `MemoryHook` records physical reads and writes at selected
WRAM, HRAM, VRAM and LY addresses. Exact traces retain overwritten values,
intervening reads, aliased writes, call mutations and distinct slots. Reads retain
addresses/counts without assuming the emulator's initial LCD/LY values or a
particular cycle count; the VRAM program explicitly disables LCD first. At most
64 events are collected, while expected traces have at most five; additional
observations therefore fail. Production emulator code is unchanged.

The observer sees bus events, not simulated IRQ scheduling. This directly guards
the selected conservative memory-effect contract even when final memory contents
alone would hide a deleted write. SP-relative private frames do not recreate the
legacy shared static spill-frame problem. Actual IRQ/reentrancy execution and
production managed calls/recursion remain separately tested.

Six parameterized Rust driver cases exercise the existing debugger's disassembly
API against independently declared NOP, immediate load, absolute jump, CB-prefixed
SWAP and both LDH encodings. They check exact bytes, expected lengths1/2/3/2/2/2,
and the next instruction's address. The fixture goes through canonical ELF and
the Rust linker. This replaces a compiler-private decoder helper without adding
a second decoder or assembler. The still-live C# debugger/common opcode component
is invoked externally; every assertion belongs to a cargo-nextest Rust target.

## Source-test disposition

Native locations: `tests/sm83.rs`, `tests/support/rewrites.rs`, the Roslyn fixture
and `tests/fixtures/EmulatorHost` observer above. ISA cases live in
`tests/driver/symbols.rs::sm83_disassembly_preserves_independent_instruction_boundaries`.
Production banked-call acceptance uses `tests/driver.rs`.

| Source method | Decision | Independent behavioral replacement |
| --- | --- | --- |
| `InstructionLength_MatchesTheOpcodeTable` | PORT | Six known ISA encodings, exact disassembly lengths and next-PC boundaries |
| `ConvertsZeroLoad_WhenFollowedByAFlagRedefiningOp` | REPLACE | Full-width carry/borrow and zero/nonzero branch results; no mandatory XOR substitution |
| `ConvertsZeroLoad_WhenFollowedByAFlagRewritingCbRotate` | REPLACE | Multi-byte shift results across two high-bit patterns and12 counts |
| `KeepsZeroLoad_WhenFollowedByCarryConsumer` | REPLACE | Carry through zero bytes and borrow propagation across all64 bits |
| `KeepsZeroLoad_WhenFollowedByACbCarryRotate` | REPLACE | Shift results preserve cross-byte carry inputs, including high bits |
| `KeepsZeroLoad_WhenFollowedByAControlFlowBoundary` | REPLACE | Explicit values survive `Tail`/`NotTail`/`ReturnJoin`; ordinary ABI flags are clobbered, IRQ AF rules remain separate |
| `KeepsZeroLoad_WhenNextInstructionIsABranchTarget` | REPLACE | Both paths through `Join`/`BranchStores`/`IncrementEntry`, with full results and traces |
| `FoldsHlLoadWithFollowingIncrement` | REPLACE | `PostIncrement` observes both pointed bytes and the advanced pointer |
| `FoldsHlStoreWithFollowingDecrement` | REPLACE | `PostDecrement` observes the stored byte and previous address |
| `DoesNotFoldWhenIncrementIsABranchTarget` | REPLACE | `IncrementEntry` reaches increment with and without a preceding load |
| `DoesNotFoldAnUnrelatedInstruction` | REPLACE | Independent arithmetic and pointer/data observations remain distinct |
| `FoldsTailCallToJump` | REPLACE | Valid `Tail` call/return preserves `x+3` and SP; no mandatory tail rewrite |
| `DoesNotFoldTailCall_WhenCallTargetIsNotWhitelisted` | REPLACE | Actual production far-call cookies/returns and recursive bank restoration remain correct |
| `DoesNotFoldTailCall_WhenCallIsNotFollowedByRet` | REPLACE | `NotTail` performs live arithmetic after the call |
| `DoesNotFoldTailCall_WhenRetIsABranchTarget` | REPLACE | Both `ReturnJoin` paths return the correct called value and continuation result |
| `DeletesDeadStore_WhenSameSlotIsOverwrittenAdjacently` | REPLACE | `Adjacent` observes both physical writes and the final read; private-local optimization is a separate typed-IR contract |
| `DeletesDeadStore_AcrossANonReadingInstruction` | REPLACE | `AcrossArithmetic` preserves both bus writes and independent arithmetic |
| `KeepsStore_WhenTheSecondTargetsADifferentSlot` | REPLACE | `Different` observes distinct addresses and both values |
| `KeepsStore_WhenAReadIntervenes` | REPLACE | `InterveningRead` observes the earlier value before overwrite |
| `KeepsStore_WhenAPointerWriteIntervenes` | REPLACE | `AliasWrite` tests both runtime pointer destinations and every write |
| `KeepsStore_WhenAddressIsAboveWram` | REPLACE | `Hram` observes both FF80 writes and its read |
| `KeepsStore_WhenAddressIsBelowWram` | REPLACE | `Vram` observes both9800 writes and its read with LCD explicitly off |
| `KeepsStore_WhenTheDeadStoreIsABranchTarget` | REPLACE | `BranchStores` executes both incoming control paths and observes the selected write |
| `KeepsStore_WhenAJoinIntervenes` | REPLACE | `Join` preserves the initial write and optional later overwrite |
| `KeepsStore_WhenDeadStoreEliminationIsDisabled` | REPLACE | Native bus stores are observable regardless of a private enable flag; exact write traces retain values visible to external observers |
| `DeletesRedundantReload_AfterStoreToSameSlot` | REPLACE | `Adjacent`/`AcrossArithmetic` retain the final loaded value; typed private-local forwarding has its existing native checks |
| `DeletesRedundantReload_AfterLoadOfSameSlot` | REPLACE | `ReloadAfterAlu` retains both reads and independent arithmetic |
| `DeletesRedundantReload_AcrossAStoreToAnotherSlot` | REPLACE | Distinct-slot and aliased-slot traces distinguish intervening writes |
| `KeepsReload_WhenAIsWrittenBetween` | REPLACE | `ReloadAfterAlu` preserves the reloaded byte plus the intervening full-width calculation |
| `KeepsReload_WhenAPointerWriteIntervenes` | REPLACE | Both `AliasWrite` destinations change the appropriate later read |
| `KeepsReload_WhenACallIntervenes` | REPLACE | `Call` observes a valid callee's mutation before reloading |
| `KeepsReload_WhenAddressIsNotWram` | REPLACE | `ReadPair` records both LY bus reads without requiring equal/time-specific values |
| `KeepsReload_WhenTheReloadIsABranchTarget` | REPLACE | Both join paths retain their selected read and correct value |
| `KeepsReload_WhenAJoinClearsTheMirror` | REPLACE | `Join`/`BranchStores` prevent a value from the wrong predecessor being reused |
| `KeepsReload_WhenEliminationIsDisabled` | REPLACE | Observable memory reads are retained independently of legacy IRQ/private-frame flags |
| `DeletesJumpToFallThrough` | REPLACE | Verified CFG simplification and execution preserve both selected successors |
| `KeepsJump_WhenNotMarkedRedundant` | REPLACE | Runtime branch alternatives in `ReturnJoin`/`BranchStores` remain distinct |
| `DeletesJumpToFallThrough_EvenWhenTheJumpIsABranchTarget` | REPLACE | Shared joins/increment entries remain valid before/after CFG merging and relocation |

Initial real-CIL/trace cases passed2/2 in16.293s. Independent ISA cases passed6/6
in8.645s. The final expanded library/rewrite/ISA/mapper/production-bank-call
selection passed236/236 in38.223s, exit0 (`/tmp/koh-rewrite-contracts-final.log`),
with695 explicitly unselected cases. No private edit-list API, byte-identical ROM
or unmeasured timing claim is retained.
Source accounting: `/tmp/koh-rewrite-source-assertions.log`. Workspace discovery
finds1903 cases,1726 compiler cases, zero ignored
(`/tmp/koh-rewrite-discovery.json`). Rustfmt, CSharpier and diff checks passed.
These are focused results, not a new full-workspace/platform pass.
