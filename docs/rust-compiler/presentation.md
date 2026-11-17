# Presentation timing and API review

The current `Koh.GameBoy` API is evidence of authoring intent, not a design
baseline. Public names, static wrappers, deferred shadows and framework
conventions must earn their place against hardware cost and game-authoring needs.
This increment executes the real graphics demo with independently checked
observations; it does not establish every sample or API behavior.

## Observed failure

The unchanged graphics showcase booted, drew its initial background and animated
sprites, but its numeric HUD stayed at tile zero. A stronger Rust test retained
the requirement for visible increments of 17. Native timing traces showed
`FlushRun` repeatedly starting around LY24, after vblank had ended, and therefore
copying zero cells. A prior test-decoder mistake about space padding was corrected
before this independent raw-zero result was accepted as evidence.

Generated initializer checks, arithmetic and local transfers contributed to
latency. A fast initialized-state check, register-pointer arithmetic and
cross-block forwarding improve generated code without changing CIL arithmetic
or alias semantics. The arithmetic helper used by software multiplication and
division retains its C-register preservation; only instruction selection uses
the caller-clobbered pointer form. Forwarding requires the same SSA value on all
incoming paths and excludes address-exposed and pinned locals. Its bounded
analysis may conservatively skip large functions.

Those improvements alone did not restore the HUD. Relying on an earlier caller's
vblank observation was an unjustified runtime assumption across Debug/Release
IL and calling-convention costs. The private commit/copy paths now wait for a
fresh window when preparation finishes late. Zero-length copies do not wait.
CPU copying remains bounded by the vblank check; uncompleted work remains dirty.
No game source, assets or production emulator code changed.

## Deliberate tradeoff

A late commit can now span an additional physical frame instead of indefinitely
deferring visible progress. Existing names and counters are not a timing model:
`Video.FrameCount` counts logical presentation calls, not elapsed hardware frames.
The eventual API must distinguish those meanings and make completion/budget
behavior explicit. Preserving old method signatures was not the reason for this
change; restoring safe observable progress was.

General inlining or a faster allocator can improve cost but cannot establish a
permanent timing guarantee for an arbitrarily distant synchronization point.
Removing the HUD or relaxing its assertions would abandon useful behavior.
No framework or alternate compiler was introduced.

## Full-map clear correction

Full-map acceptance exposed a separate runtime defect: `Bg.Clear` called the
direct VRAM fill even while the LCD was on. The retained mode-3 guard observed
355 illegal writes. `MapWriter.Clear` now fills its shadow and marks the full
map dirty in that state. With the LCD off it copies immediately and resets stale
dirty/prepared state. This restores safe clearing without making a caller choose
an internal copy path. Keeping the unsafe path or requiring every caller to stop
the LCD would impose unnecessary hardware choreography on a basic game operation.

The new `full_cgb_map_transfers_preserve_vram_and_complete` cases execute both
full BG/window fills and LCD-on clear in Debug/Release. They compare every byte
of VRAM, unchanged bank1/OAM, return/stack state and independently calculated
GDMA duration against the remaining vblank. The initial two-million-instruction
harness limit stopped in setup of the second map after the first had completed
(marker03). Only this larger fixture uses a bounded eight-million-instruction
budget; all existing cases retain their original bound and all hardware/data
assertions remain in place. The subsequent hardware red run is
`/tmp/koh-full-map-hardware-red.log`, exit100. After the clear fix both cases
passed in18.720s (`/tmp/koh-full-map-clear-green.log`, exit0).

## Evidence and remaining limits

The Rust `full_graphics_showcase_boots_and_animates` cases compile the actual
sample source with Roslyn, invoke native `koh-compile`, read canonical ELF, link
with Rust and execute through Koh's actual DMG/CGB boot ROM and existing emulator.
They assert boot handoff, bounded startup, scrolling, exact quarter-turn sprite
spacing, complete background/tile/OAM snapshots, visible score progression,
independently expected checker pixels and zero mode-3 VRAM writes. Release CGB
also corrupts the logo and requires explicit boot rejection. Captures live in
temporary test directories; tracked sample assets are untouched.

The first stronger run passed 4/4 Debug/Release × DMG/CGB cases in 40.883s
(`/tmp/koh-flush-resync-first.log`, exit0). The full suite after diagnostic tracing
removal is recorded separately. This uses an ordinary Roslyn fixture project;
the Koh SDK still invokes the old compiler, and source debugging is not proven.

The full workspace subsequently passed769/769, zero skipped, in1997.889s,
exit0 (`/tmp/koh-showcase-workspace.log`). That binary predates the final score
chronology check, full-map cases and clear correction. Final focused runs after
the correction passed8/8 showcase/sprite cases in56.058s and6/6 full-map/text
cases in48.744s, both exit0. Separate workspace documentation tests passed
(no cases). These scopes do not claim a full771-case rerun.

Full 1024-cell spans on both CGB maps now have executable checks. The inherited
LY margin is still not a proof for every possible transfer start phase or future
generated-code cost. Interrupt-driven updates and reentrancy remain unimplemented.
The later [showcase timeline](showcase-timeline.md) now closes palette toggles
across60 logical iterations and exact sprite/score phases with simultaneous
OAM/palette observations for every update through79. This is sample acceptance,
not a general interrupt/reentrancy or arbitrary transfer-phase proof.
