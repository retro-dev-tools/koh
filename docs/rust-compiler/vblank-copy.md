# Bounded VBlank copy

DMG tilemap flushes use the address-less `vblankcopy` intrinsic with the exact
static signature `ushort(byte* destination, byte* source, ushort count)`. The
private `MapWriter.FlushRun` declaration carries this metadata; the compiler
does not recognize its name or owner. Its managed body throws because hardware
timing is a target operation, not a host .NET fallback. The game-facing graphics
API and CGB GDMA path are unchanged.

The native operation copies an ascending prefix and returns its actual length.
Pointers retain raw bus and selected-bank semantics; capacity, accessible source
storage and VRAM destination remain caller-owned. Arguments are evaluated before
the operation. Count narrows to unsigned16; the result zero-extends to int32.
Zero count neither accesses source/destination nor reads LCDC/LY or changes IME.

A nonempty call disables interrupts and leaves them disabled. LCD-off execution
aborts with hardware fault9. A call initially outside LY144–149 waits for the next
VBlank; otherwise it uses the current window. Each byte samples LY and proceeds
only for144–150. The gate-to-store sequence takes less than one456-dot scanline,
so even the latest accepted sample writes no later than LY151. At least the last
two VBlank scanlines remain reserved. Partial progress is normal, including for
short requests started late in the window. The caller retains the uncopied range.
No GDMA, VBK/SVBK changes or helper-call ABI is involved.

This policy follows the hardware's [VBlank status](https://github.com/gbdev/pandocs/blob/master/src/STAT.md)
and [VRAM access restrictions](https://github.com/gbdev/pandocs/blob/master/src/Accessing_VRAM_and_OAM.md),
including the need to exclude interrupts between a timing check and access.
The conservative deadline includes instruction latency rather than assuming that
a sampled LY remains current until the store. This does not establish interrupt
handler lowering or a general reentrant graphics API.

The old per-byte managed loop drained only140 of1024 dirty cells after300
settling frames in the native spilled-value ABI, leaving old title text among
partially drawn2048 tiles. A primitive copy loop avoids repeated general-purpose
frame traffic without teaching the optimizer a private library method pattern.
The initial native guard also allowed a late LY151 sample to write at LY152;
read-only review identified it and the phase sweep reproduced LY152/dot4 before
the guard was tightened. The game capture budget and pixel assertions were kept.

Rust-owned `tests/driver/vblank.rs` builds Roslyn Debug/Release fixtures and drives
the existing C# emulator on DMG/CGB. It probes154 scanlines at dots0/452 for empty,
one-byte, short, oversized and large-frame callers. Assertions cover exact copied
prefixes and source-read counts, untouched VRAM, write addresses/values/LY/dots,
partial progress, duration, IME and SP/SVBK preservation. The large caller retains
a live64-bit field and a200-byte stack allocation. The test verifies that the
intrinsic's actual emitted code includes the address path for offsets above127.
`tests/ir.rs` covers typed operands, required results, optimizer remapping and
observable unused results. `tests/sm83.rs` covers count truncation, empty LCD-off
execution and explicit nonempty LCD-off faults.

These are emulator and encoding checks, not physical hardware measurements.
