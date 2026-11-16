# Guarded VRAM byte stores

The private graphics attribute store uses address-less metadata
`[KohIntrinsic("vramwrite")]` with exact static signature
`void(byte* destination, byte value)`. Dispatch does not depend on the method or
owner name. The managed body performs an ordinary store for the desktop runtime;
native compilation emits the hardware operation directly. Invalid metadata
signatures diagnose at the CIL call site.

The IR operation requires a native pointer and int32 byte storage, has no result,
and remains observable when its result would be unused. Optimization remaps both
operands and retains the CIL offset. The backend stores the low byte exactly once
in the currently selected VRAM bank. Addresses outside8000–9FFF abort with fault9
before writing. It does not select VBK/SVBK or allocate storage.

Arguments are loaded before synchronization. The backend saves the pointer while
loading the value, including extended stack addressing that clobbers DE, then
keeps the value in D and destination in HL. Both temporary pushes are included
in the stack-capacity guard. It disables interrupts, polls STAT until mode0/1,
then immediately stores. Conservatively counting the entire successful STAT
read instruction, the read-to-store sequence is44 T-cycles at normal speed.
Even a sample at the end of mode0/1 is followed by80 accessible mode2 dots.
Double-speed CPU execution consumes fewer dots. LCD-off execution remains valid.

Independent hardware basis: [VRAM access and interrupt exclusion](https://github.com/gbdev/pandocs/blob/master/src/Accessing_VRAM_and_OAM.md)
and [PPU timing](https://github.com/gbdev/pandocs/blob/master/src/Rendering.md).
Emulator phase checks supplement this timing argument; they are not physical
hardware measurements or an assertion that the emulator is an oracle.

## Interrupt policy

Successful execution leaves interrupts disabled, including LCD-off calls.
Consequently nonempty CGB Bg/Win attribute writes have this restriction;
DMG no-ops and empty fills do not execute the operation. An interrupt between
the access check and store would invalidate the timing proof; silently guessing
the previous IME state would be incorrect because SM83 does not expose a readable
IME register. No IME-preservation or reentrant graphics guarantee is claimed.
The final interrupt/runtime contract remains a whole-migration acceptance gate;
this restriction must not disappear from game-facing documentation.

## Regression

Expanded real JRPG dialogue acceptance reproduced157Debug/239Release attempts
to write VRAM during mode3. The old wait/store/readback loop retried dropped
stores until the image converged. A trace places the failing stores inside
MapWriter.WriteAttrVerified at palette6 dialogue-window cells. This was a library
timing defect, not a compiler requirement to preserve.

Replacing the racy sequence with the metadata operation makes the unchanged
sample pass the exact zero-violation assertion, three dialogue pages, NPC
collision and retained callback return with unchanged Hero identity/state.
Final isolated Debug/Release run:2/2 pass178.562s,exit0. Core typed IR and
SM83 address/large-frame tests:6/6 pass6.162s,exit0. Direct phase sweeps pass4/4 in110.511s,exit0:9,856 probes across
Debug/Release, DMG/CGB, LCD-on/off,154lines at two dot phases, four endpoint/
large-frame/selected-bank entries. They assert one actual store, exact full VRAM
contents, preserved SP/SVBK/live locals, disabled IME and bounded duration.
The PR-worktree primitive/phase/dialogue checkpoint passes12/12 in275.699s,
exit0; isolated broader regression passes396/396 in472.676s,exit0, including
existing bounded copies,2048 SDK gameplay and all IR/SM83 cases. Final expanded
JRPG battle checks pass2/2 in56.038s in the PR worktree. This does not establish
complete JRPG battle/pixel acceptance or whole-migration completion.

Read-only independent review found no blocking defect in operand loading,
stack guards, metadata/IR consistency or the timing argument. The phase suite
samples two dot positions per scanline at normal CPU speed and uses a fixed
dot252 drawing boundary for its unscrolled/no-OBJ setup. It is not an exhaustive
test of PPU phases, variable rendering stalls, double speed or interrupt
reentrancy. Exact stored VRAM contents supplement the position assertions.
