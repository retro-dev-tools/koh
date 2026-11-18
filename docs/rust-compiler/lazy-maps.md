# Allocate map shadows when needed

The first native real-game probe found double-buffered startup aborting with
allocation fault5: managed storage already consumed2464 bytes before its1935-byte
canvas request. The shared arena is4096 bytes. The private map writer had eagerly
allocated both1024-byte shadows when `Video.Init` cleared the hardware maps,
although the standalone canvas uses neither shadow.

Increasing the arena would require stealing guarded stack space or depending on
extra CGB banks, while leaving the unnecessary cost on DMG. Removing map clears
or replacing the sample would drop useful behavior. Instead, an unallocated map
records its uniform clear tile in one byte. LCD-off clear still writes every
hardware map cell immediately. The first real cell mutation creates only that
map's aligned shadow and seeds it from the recorded tile. Same-value valid writes
remain no-ops without allocation. Invalid flattened indices still reach the
managed bounds check and fault; they cannot disappear behind the no-op path.

LCD-on clear still creates/updates a full shadow and queues the full map. Clearing
an existing shadow updates both its contents and uniform state; LCD-off clear
also invalidates pending transfers. Once allocated, shadows retain their managed
lifetime. The existing dirty/prepared flags imply that a shadow exists. Nullable
annotations express the new storage lifecycle; native array accesses retain
their runtime null and bounds checks.

Public method signatures and display behavior are retained because the observed
problem is eager storage, not an authoring requirement for another configuration
API. The existing API was inspected critically, not treated as a convention.
Canvas/map composition restrictions and its presentation paths still need review.

## Evidence and test changes

Four `driver::lazy_maps_preserve_uniform_backgrounds_and_raw_storage` nextest
cases cover Debug/Release × DMG/CGB. They reserve2500 raw bytes, issue same-value
writes to both unused maps, clear BG to a nonzero tile with LCD off, then mutate
either BG or window after LCD-on. Exact heap/VRAM/OAM snapshots check the uniform
seed, alignment, unchanged neighbors, raw endpoints, unused memory and untouched
second banks. Invalid zero-valued writes to each unused map must fault6.

The initial game fault is recorded in `/tmp/koh-canvas-boot-red.log`; the original
lazy-budget red is `/tmp/koh-lazy-maps-red.log`. Separate serial red runs exposed
unnecessary no-op allocations (`/tmp/koh-lazy-noop-serial-red.log`) and an early
no-op return that bypassed bounds (`/tmp/koh-lazy-bounds-red.log`).

The final serial graphics/map/sprite/text regression passed22/22 in155.775s,
exit0 (`/tmp/koh-lazy-map-verified.log`;72 unrelated driver cases filtered).
Earlier overlapping runs are not final verification evidence. Two existing
heap-image expectations now require only the OAM allocation after `Video.Init`,
with the unused map regions left poisoned; all hardware map/palette/OAM and
sprite-value assertions remain. Temporary C010 trace writes were removed from
the full-map fixtures because arbitrary WRAM scratch can alias compiler statics.

The game now gets beyond allocation: its probe reports managed492 + raw1935.
That is not complete game acceptance. The current CGB capture budget expires
inside `Canvas.EdgeX` with rendered WRAM but no uploaded frame; DMG reports
mode3 writes in its chunked presentation path. Compiler arithmetic cost and
presentation timing remain active work, with no relaxed frame/VRAM assertions.
