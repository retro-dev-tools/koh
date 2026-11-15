# Bounded HBlank display streams

The racing-beam cube's C# polling/computation loop rendered a plausible cube
but missed the intended scanlines in all four Debug/Release × DMG/CGB cases.
A timing wait cannot guarantee that arbitrary following C# code fits in HBlank.
The replacement computes a64-byte scroll table once and calls
`Ppu.StreamScrollX(values, 39, 64)`. HBlank39..102 writes affect rows40..103;
the sample resets SCX in the following VBlank. Viewport, renderer, assets,
animation assertions and capture budgets remain unchanged. The old public API
is evidence of the effect, not a compiler convention.

## Contract

`KohIntrinsic("hblankstream", address)` requires static non-generic default
managed-call-convention `void(byte* source, byte firstLine, byte count)`.
Only display registers FF42..FF43 and FF47..FF4B are accepted: scroll, DMG
palettes and window positions. LCD control, DMA and bank registers are rejected.
Metadata supplies the output address. The initial public API streams SCX;
its CLR body throws because CLR execution cannot provide Game Boy timing.

Zero count accesses neither source nor PPU and leaves IME unchanged. Nonzero
calls disable interrupts and leave them disabled: SM83 cannot read the previous
IME state for restoration. `firstLine < 144` and `count <= 144 - firstLine`
are checked before source access; failure uses bounds fault6. LCD-off nonempty
calls use hardware fault9. The stream waits out any current VBlank, enters the
next VBlank, then writes one sequential table byte in each HBlank starting at
firstLine. There is no implicit final register reset.

Raw source addresses wrap at16 bits. Capacity, accessibility and the selected
ROM/WRAM bank belong to the caller. No banks change and no storage is allocated.
DI prevents handlers from delaying the critical path or remapping the source;
this does not establish general interrupt-handler support. A valid line143
write persists into the next frame until the caller changes the register.

## IR and ABI

`HBlankStream` has native16 source, int32 first/count narrowed to unsigned8,
a validated constant register and no result. All three uses participate in
dominance, liveness and remapping; effects and IL provenance survive optimization.
No C# loop pattern is recognized. HL holds source, B line, C count and D data.
Setup reserves two transient stack bytes, including long-offset addressing.
The timing loop has no stack accesses, calls or bank changes.

After the LY gate, a36T STAT poll leads directly to the fixed-address store.
The conservative transition-to-store bound is76T, below the shortest87-dot
HBlank at normal speed, and faster in CGB double speed. The next byte is
preloaded before waiting for the next line. See
[Pan Docs rendering timing](https://gbdev.io/pandocs/Rendering.html) and the
[SM83 opcode table](https://github.com/gbdev/gb-opcodes/blob/master/Opcodes.json).

## Evidence

`tests/cubes.rs` observes actual PPU line events and SCX writes. At least two
complete physical frames must carry an independently calculated triangle wave
on rows40..103 and zero scrolling above. Writes must be in permitted HBlank
rows or safe zero resets. Timestamp continuity prevents frame splicing. Nine
adversarial traces cover wrong modes/lines/values, missing rows, spliced frames
and truncated records. Eighteen image-validator cases cover all viewports.

`tests/driver.rs::hblank_stream_boundaries_and_hardware_state` builds actual
Roslyn fixtures in both configurations/hardware modes: zero/first/last/full144/
64-line streams, CGB double speed, invalid ranges, LCD-off faults, invalid
metadata, exact source reads/writes, IME, bank preservation, complete guarded
WRAM snapshots and balanced return SP. Backend tests check byte truncation,
large frames and stack exhaustion. IR tests check permitted/invalid registers,
types, effect retention, alias remapping and provenance.

Real raster tests initially fail4/4 (`/tmp/koh-racing-raster-red.log`); native
streams pass4/4 in63.580s (`/tmp/koh-racing-raster-green.log`), exit0. Initial
boundary/IR selection passes10/10 in30.162s; backend/validator selection passes
35/35 in8.023s, both exit0. Focused Linux evidence does not establish full
migration or cross-platform acceptance. Final formatted regression runs every
IR/backend/cube case plus driver boundaries:380/380 pass in415.625s, exit0,
126 unrelated driver cases filtered (`/tmp/koh-raster-regression.log`). Read-only
review found no blocking defect; physical hardware timing was not exercised.
Phase-sweep migration remains open.

Ordinary SDK build also succeeds:
`dotnet build samples/csharp/gb-3d/racing-beam/CubeRacingBeam.csproj -c Release -m:1 --nologo`.
The completed `/tmp/koh-raster-sdk.log` reports0 warnings/errors,38.41s and a
131072-byte ROM. Its header/global checksums and CGB compatibility flag were
checked directly. The SDK leaves a canonical `.o` and optional `.kdbg`; current
KDBG still provides method/IL mappings, not implemented Portable PDB source maps.
