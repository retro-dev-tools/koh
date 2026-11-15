# Raw arena contract

The useful requirement is bounded writable storage for graphics and other unsafe
hardware-facing code. The old DE00 top address, shared managed/raw reset lifetime
and unchecked 16-bit size wrap are not contracts to preserve.

The selected design shares the existing 4 KiB WRAM bank1 region. Managed arrays
grow upward, with their existing length headers and payload alignment. Raw blocks
grow downward, without headers or initialization. Two independent used-byte
cursors must satisfy `managed_end + raw_used <= 4096` after every allocation.
Both allocation directions check this invariant before committing a cursor or
touching payload memory. Alignment padding consumes capacity. The linker owns
the arena placement; the compiler references its symbol rather than introducing
a second address table or a second bank allocator.

`Mem.Alloc(int)` accepts0..4096 bytes subject to the remaining shared capacity.
Negative, wider/truncated and exhausted requests trap with allocation code5.
Zero bytes returns the current raw frontier, possibly the arena's one-past end;
it reserves no storage and cannot be dereferenced. Raw alignment is one byte;
callers needing stronger alignment reserve padding explicitly. There is no
individual free or implicit zero initialization.

`Mem.Reset()` releases raw blocks only. Managed arrays and their aliases remain
valid; raw pointers are invalid after reset until replaced by a new allocation.
Reusing an address is allowed but observing old freed contents is not a lifetime
guarantee. Resetting a shared managed cursor would silently invalidate live C#
references, so that legacy behavior is rejected. Independent raw/managed regions
would waste scarce WRAM, while a general free-list/GC would add complexity that
the current graphics use does not require.

Returned raw pointers address bank1. Alloc/reset preserve the selected SVBK;
unsafe callers must select bank1 while dereferencing these pointers. Managed
array operations continue to save/select/restore the bank themselves. Arena
mutation is not reentrant; interrupt allocation remains unsupported until the
interrupt ABI has an independently verified contract.

## Native ABI and validation

`alloc` metadata requires address-less static `byte*(int32)`; `heapreset` requires
address-less static `void()`. Both require nongeneric default-convention methods.
They lower to typed, observable `ArenaAllocate`/`ArenaReset` operations. Size is
int32 and the result is native16; verification rejects other operand/result
types. No desktop body is lowered as a fallback.

Startup zeros two little-endian WRAM0 words: `__koh_heap_cursor` is the managed
end offset; `__koh_raw_used` is the raw used-byte count. Both are relative to the
linker-placed `__koh_heap` region. Reset writes only the second word. Neither raw
operation touches SVBK or raw payload bytes. The desktop reference body provides
the same raw capacity/address/reset rules; invalid sizes raise argument errors
and exhaustion raises `OutOfMemoryException` rather than a ROM fault.

## Acceptance cases

`RawArena` contains ordinary C# fixtures for distinct blocks, reset alongside a
live managed array, SVBK preservation, zero/full allocation, exact shared
capacity, collision in each direction, aligned payload padding, negative and
oversized requests, high-byte truncation, and exhaustion. The Rust driver checks
return/trap state, stack restoration, exact poisoned WRAM bytes and untouched
bank2 in Debug/Release on DMG/CGB. Each of four native driver cases executes13
ROM scenarios and rejects two malformed intrinsic signatures. The desktop case
executes the actual managed body and compares address offsets, distinct storage,
reset, invalid sizes, exhaustion and zero-size behavior with independent values.
Five IR cases check types, retained unused allocations and observable reset.

Initial missing-feature red: `/tmp/koh-raw-arena-red.log`, exit100. The desktop
boundary separately failed on unchecked wrapping and the old arena address
(`/tmp/koh-raw-desktop-red.log`, exit100). The expanded raw/primitive/aggregate/
aligned/RVA regression passed26/26 in136.631s, exit0
(`/tmp/koh-raw-arena-array-regression.log`;265 unrelated cases filtered). Full
workspace and representative game acceptance of this increment remain separate.

The largest sample buffer fits alongside its renderer arrays, but the graphics
library also eagerly creates both1024-byte map shadows in `Video.Init`. That
initialization contract requires measured game-level validation; successful
allocation fixtures do not prove that every sample fits or renders correctly.

At the raw-arena checkpoint, all three ordinary Release SDK builds produce128KiB ROMs without warnings
(`/tmp/koh-raw-{full-frame,racing-beam,double-buffered}-build.log`). Headless runs
with real boot ROMs produced160x144 PNGs after1200frames and zero mode3 writes.
The full-frame and racing-beam captures visibly contain cubes; double-buffered
still shows the boot logo. That is a failed sample, not acceptance: diagnosing
its CPU/allocator state and preserving the canvas behavior remain required.
Captures/reports are outside tracked sample assets under `/tmp/koh-raw-*`.
The subsequent [lazy-map correction](lazy-maps.md) fixes allocation pressure;
complete canvas rendering/presentation acceptance remains open.
