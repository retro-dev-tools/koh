# CPU control and startup speed switching

Reachable `KohIntrinsic` metadata kinds `di`, `ei`, `halt`, `nop` and `stop`
require an address-less static, nongeneric `void()` signature with the default
calling convention. Invalid parameters, return types, addresses and conventions
are diagnosed at the caller/member and IL offset. No desktop body executes.

Typed `CpuControl` IR has no operands or result. Every operation is observable,
including NOP: optimization preserves order and treats it as a barrier. The
backend emits F3, FB, 76, 00 and 10 00 respectively. These are CPU instructions,
not atomic C# statement boundaries. EI enables interrupts after the following
machine instruction; generated instructions can intervene before another C#
statement. HALT can exhibit the hardware HALT bug with a pending enabled request.
STOP without an armed CGB speed switch can suspend execution.

The existing `TryEnableDoubleSpeed` helper was not accepted as a semantic baseline:
its unconditional EI could enable interrupts that the caller had disabled, and
its IF write discarded pending requests. IME is unreadable, so automatic save and
restore cannot implement a truthful contract. A tracked software IME shadow would
add global state and remain wrong for external machine code or interrupt entry.

The selected contract is a startup helper called with IME disabled. It preserves
IE, temporarily masks IE during STOP, deselects JOYP, arms KEY1 and restores IE.
It leaves IME disabled and does not clear IF. DMG returns false without STOP;
already-fast CGB returns true without toggling back. Existing 3D callers execute
it during startup and require no source migration. Enabling handlers later is a
separate, explicit operation. No general interrupt-state-preserving critical
section API is promised by this helper.

Independent references: [SM83 instruction encodings](https://github.com/gbdev/pandocs/blob/master/src/CPU_Instruction_Set.md),
[IME and interrupt behavior](https://github.com/gbdev/pandocs/blob/master/src/Interrupts.md),
and [KEY1 speed switching](https://github.com/gbdev/pandocs/blob/master/src/CGB_Registers.md).

## Native tests and limits

`driver::cpu_control_and_speed_switch_execute` compiles actual C# in Debug/Release
and links DMG/CGB ROMs. It checks raw DI/EI/NOP, delayed EI, HALT followed by an
externally requested wake-up with IME disabled, repeated speed-switch calls,
returned results, KEY1 state/disarming, IE/pending-request preservation, stack
restoration and malformed intrinsic contracts. The IME probe captures a game
marker before cartridge shutdown's DI; observing only the exit loop would hide
the old helper bug. Both CGB cases failed with IME1 before the helper correction
(`/tmp/koh-speed-ime-red.log`). Missing DI lowering failed independently first
(`/tmp/koh-cpu-control-red.log`).

Six IR cases verify result typing, observable operations and instruction order,
including STOP's second byte. Tests use the existing C# emulator unchanged;
only its test host adds probes. Emulator speed switching omits the physical
switch stall, so these cases cannot establish PLL/PPU timing safety. Unarmed
STOP wake-up, HALT-bug combinations, handler dispatch, interrupt ABI and
reentrancy remain open migration requirements.

Hosted native run43739101141 exposed a stale rejection fixture: it used the now
supported `halt` kind to stand in for an unknown intrinsic. The fixture now uses
`unsupported-cpu-control`; rejection assertions remain intact and actual HALT
execution has separate native coverage. The corrected eight-variant rejection
case passed locally during the workspace run in24.998s. This does not establish
that the whole workspace run or hosted follow-up has passed.
