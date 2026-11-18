# Shared canvas transfers and real cube acceptance

Canvas used a separate LCD-on CPU copy loop with an empirically chosen
three-byte chunk. Pointer, size and call setup after WaitVBlank consumed the
remaining blanking window under the native ABI. Actual double-buffered DMG
runs recorded806 Release/826 Debug mode3 writes over2000 postboot frames
(`/tmp/koh-cube-word-all.log`). A copied historical chunk count is not a
hardware safety invariant.

Canvas and tile loading now share `TileSet.CopyToVram`, an internal whole-tile
transfer boundary. LCD-off behavior remains direct CPU copy or aligned CGB
GDMA at the hardware's2048-byte ceiling. LCD-on CPU copies prefetch each
four-byte value and destination before waiting; LCD-on GDMA prepares registers
and control before waiting, with the tile loader's1024-byte bound. Canvas
passes complete16-byte tiles, so the four-byte CPU stride has no partial tail.
Map-writer scheduling remains separate because it returns partial progress.
Double-buffer presentation still flips the visible tile page only after all
bytes have landed. No implicit LCD-off flash is added.

The independent replacement is execution and pixels, not a required DMA choice
or method name. The existing4 cube animation/VRAM probes passed49.586s with
zero mode3 writes after sharing transfers; see
`/tmp/koh-cube-shared-transfer.log`. Stronger assertions then showed why that
alone was insufficient: an early blank and later cube satisfy inequality.

The Rust `cubes` target compiles actual sample sources with Roslyn in Debug and
Release, emits ELF with the native compiler, links it and runs the existing C#
emulator through its real boot ROM. Both captures must use the authored palette,
stay inside the centered96x80 viewport, contain multiple nonbackground shades,
have a plausible centered projection and form one8-connected region. Both
captures must also differ. Alpha, dimensions and boot/frame bounds are checked.
Six synthetic cases check that the image validator rejects blank, bad alpha,
wrong palette, out-of-viewport and disconnected images. No golden image or
previous compiler output is used as the oracle.

Frame limits remain CGB200/500 and DMG1100/2000 after boot. The strengthened
suite currently fails early-capture timing; that failure remains required work
inside PR55. It is not waived as an aspirational sample or deferred to emulator
migration. The fixture serializes shared Roslyn builds both through nextest's
roslyn group and a process-local lock for ordinary cargo test invocation.
