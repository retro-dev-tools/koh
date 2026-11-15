# Managed ROM calls

Cartridge emission places managed methods in floating ROMX sections and declares
an MBC5 cartridge (header type 0x19, without external RAM). The Rust linker owns
bank assignment, capacity, relocation evaluation and final header checksums.
The compiler emits ordinary ELF bank relocations; it does not lay out banks.

The initial scope is banked code. Literal pools and initialized constant data
remain in ROM0 so existing two-byte immutable references stay valid across calls.
ROM0 capacity remains checked; arbitrary banked data references are not supported
by this change. A method must fit one 16 KiB bank. Unsafe native accesses must
respect runtime mapper ownership; raw mapper writes can invalidate executing code.

## Call ABI

All production managed calls use the same stack convention:

| Callee-entry SP offset | Contents |
| --- | --- |
| 0 | Two-byte ROM0 return-stub address |
| 2 | Original caller return PC |
| 4 | Caller ROM bank, little-endian u16 |
| 6 | Return buffer, followed by packed arguments |

The caller reserves the four-byte cookie and return/argument storage with the
usual stack-capacity guard. It writes its own linker-derived bank to cookie+2,
passes target address in BC and target bank in DE, and CALLs `__koh_far_call`.
The fixed ROM0 trampoline moves the incoming return PC into the cookie and
replaces the machine return address with `__koh_far_return`. It switches the
mapper then jumps to BC. The callee writes its return buffer and executes RET.
The ROM0 return stub pops the original PC and bank, restores the mapper, and
jumps back. At that point SP addresses the return buffer, and the caller releases
only the remaining return/argument storage. All registers and flags are clobbered.

Each invocation owns its cookie, including recursion and calls within a bank.
There is no shared bank shadow or static activation frame. Startup uses the same
convention with a ROM0 caller (bank zero). Leaf runtime helpers such as HRAM DMA
retain their separate documented register ABI and cannot call managed methods.
The low-level standalone function emitter remains a near-call test interface;
it is not the production cartridge path.

MBC5 supports banks 0–511, with the low eight bits written at 0x2000 and the high
bit at 0x3000. Both writes execute in ROM0, before any fetch from the selected
ROMX bank. See [PanDocs MBC5](https://raw.githubusercontent.com/gbdev/pandocs/master/src/MBC5.md).
Interrupt entry remains unsupported: startup disables interrupts. This design
does not claim safety for an interrupt handler that changes the mapper between
the two writes; interrupt ABI and mapper ownership need their own verification.

## Verification in progress

Rust integration cases compile actual C# Main/Sum recursion in Debug/Release,
round-trip canonical ELF, force distinct banks including 257/258, and execute
on the existing DMG/CGB emulator. Expected result is independently 42, with
SP restored to D000. The initial test fails on ROM0-only emission. Broader ABI,
capacity, full graphics execution and full regression checks remain required
before this increment is marked complete.

Initial expanded ABI run passed 4/4 cases (eight ROM executions), 37.635s,
`/tmp/koh-rombank-abi.log`. These include 64-bit values, aggregate returns,
byrefs, void/narrow calls and strings. Read-only review found no concrete defect
in stack transitions, guards, relocation remapping or startup symbol indices.

The unchanged Release graphics showcase now compiles and links, logs
`/tmp/koh-full-gfx-banked-{compile,link}.log`. A 600-frame no-boot probe checked
the full checkerboard, checker tiles, four OAM sprite records and zero mode-3
VRAM writes. Its black PNG is not graphics acceptance: skipping boot leaves
CGB attributes poisoned FF. More importantly, actual Koh CGB boot from PC0
remains in its logo-check loop at 0287 (`/tmp/koh-banked-gfx-boot-execute2.log`,
exit134). The cartridge logo bytes are currently zero. Linker-owned canonical
header finalization and a real boot regression are required next; this sample
is not yet bootable, and no production emulator or game API was changed.
