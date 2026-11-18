# LCD-on tile transfer timing

Native execution exposed a runtime assumption tied to the old compiler's cycle
costs. Waiting for vblank before computing a chunk and calling `Mem.Copy` did
not make the subsequent bus writes safe. The new Rust-owned cases attach the
existing emulator `Mode3WriteGuard`; they also check each general-purpose DMA
start against its remaining vblank dots and compare every poisoned VRAM byte.

The initial Debug/Release × DMG/CGB run failed4/4
(`/tmp/koh-lcd-on-red.log`, exit100). Instrumented DMG execution observed the
first CPU write10044 dots after the last LY144 read, followed by2680 dots per
byte. The available full vblank is4560 dots. Aligned CGB GDMA started14160 dots
after that read, at LY21, dot92. The independent byte check also found unchanged
VRAM poison. These failures are not accepted legacy behavior.

The library now prepares each CPU target and value before `Ppu.WaitVBlank` and
performs one direct four-byte store afterward. The total is always count*16;
unaligned native loads/stores use the SM83 backend's bytewise implementation.
This retains four bytes per blanking wait without invoking a copy loop after the
wait. GDMA prepares its address registers and
control byte before waiting, then triggers immediately after the wait. The new
internal CGB helper has one caller that already establishes CGB hardware, aligned
addresses and a positive block-aligned size. Public game-facing signatures and
the public nonwaiting `Cgb.CopyToVram` implementation are unchanged. Register
addresses still come from the existing metadata-driven Hardware properties.
There are no compiler name checks, new intrinsics or emulator algorithm changes.

The old1920-byte GDMA burst also failed after this restructuring: it started at
LY146, dot104, and required3840 dots where only3544 remained
(`/tmp/koh-lcd-on-large.log`, exit100). LCD-on bursts now use at most1024 bytes
(2048 DMA dots). The1920-byte fixture exercises two bursts and checks the final
source/destination registers `D410/8430`, complete payload and all bank guards.
LCD-off transfers retain their existing2048-byte hardware ceiling.

The bounded focused run passed4/4, exit0,23.790s
(`/tmp/koh-lcd-on-bounded.log`), covering Debug/Release on DMG/CGB and ten new
ROM scenarios. The formatted one-byte CPU version then passed the complete
workspace:617/617,0skips,exit0,456.891s (`/tmp/koh-lcd-on-workspace.log`). The final
four-byte refinement passed4/4,exit0,23.464s (`/tmp/koh-lcd-on-word.log`). After
restoring the original LCD-on bytes1..32 at tile7, the final focused run passed
4/4,exit0,23.357s (`/tmp/koh-lcd-on-final-focused.log`). These cases execute26 ROMs,
including the ten new LCD-on scenarios. Combined native coverage is441 compiler
cases and454 ROM scenarios (385 normal/69 traps), with four framebuffer checks.
Separate doctests (no cases) and rustfmt checks exited0. This records the actual
verification scopes rather than claiming a second full run after the refinement.

The still-live C# test project built with zero warnings/errors,exit0,11.56s
(`/tmp/koh-lcd-on-dotnet-build.log`); its filtered TileSet tests passed7/7,0skips,
exit0,7.431s (`/tmp/koh-lcd-on-dotnet-tests.log`). Read-only review found no concrete
defects. Old compiler execution is integration evidence, not the timing oracle.

`driver::actual_tileset_load_copies_vram_and_preserves_banks` owns these cases.
The original `CilTileSetTests.Load_LcdOn_ChunksAcrossVblanksAndNeverWritesDuringMode3`
requires a32-byte LCD-on load and zero prohibited writes; its native replacement
adds Release, CGB, aligned/unaligned paths, DMA-duration and full-bank checks.
This completes that method's native behavioral replacement. Other graphics and
complete sample acceptance remain open. Interrupt handlers remain unsupported
by the native compiler; enabling
them later requires revalidating these bounded timing sequences.
