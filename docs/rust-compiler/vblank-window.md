# VBlank polling and bounded map writes

The hardware contract is a window, not an exact scanline observation. VBlank
covers lines144–153, ten456-dot lines. Polling for equality with144 can miss
that entire line when generated Debug code takes too long between MMIO reads.
See the [Pan Docs rendering timings](https://github.com/gbdev/pandocs/blob/master/src/Rendering.md).

`Ppu.WaitVBlank` first leaves any current VBlank window, then waits until
`LY >= 144`. The LCD must be on. Two successive calls therefore await distinct
windows. This preserves the useful frame-wait capability without treating the
old implementation or its scanline-equality convention as a specification.

The Rust `driver::vblank_wait_observes_first_window_at_varied_entry_phases`
test compiles actual C# in Debug and Release, emits canonical ELF, links it,
and runs the existing emulator in DMG and CGB modes. Each case tests eight
entry delays plus two successive waits. It checks early-VBlank return,
bounded latency, balanced stack and unchanged poisoned WRAM. The old code
missed the first window in Debug phase3 on both machines:137692T against the
90000T first-window bound (`/tmp/koh-vblank-phase-red.log`,2/4 cases failed).

The range fix exposed a separate unsafe map-copy path. Showcase tracing
identified `MapWriter.FlushRun`: a line152 check preceded a VRAM store by948
dots, reaching line0 mode3 and causing five violations. Its byte-range
comparison now stops before152 and prepares the source byte and destination
before checking LY. Uncopied cells remain dirty for a later frame; no graphics
assertion or frame budget is relaxed. This conservative two-line reserve is
validated by observable execution, not claimed as a universal timing proof
for arbitrary future compiler changes.

A proposed global LY-read-to-VRAM-write metric was rejected: it included
unrelated writes, and DMG runs could contain none. It could not prove the
map-copy reserve. The acceptance guard instead observes actual mode3 writes
throughout execution, alongside exact VRAM/OAM, animation and framebuffer
assertions. Failure diagnostics retain method/bank/PC and recent LY context.

This unit covers the double-buffered cube and graphics paths selected by the
regression command. Full-frame and racing-beam structural acceptance remain
separate outstanding requirements. It does not complete the compiler migration.

Completed run:28/28 passed in161.668s, exit0,90 unrelated tests filtered;
`/tmp/koh-vblank-final-green.log`. Reproduce from the repository root:

```sh
cargo nextest run -p koh-compile --test driver --test cubes --locked -j1 \
  -E 'binary(cubes) | test(actual_tileset_load) | test(full_cgb_map_transfers) | test(full_graphics_showcase) | test(lazy_maps) | test(vblank_wait)'
```

Post-hook showcase/VBlank recheck:8/8 passed in87.227s, exit0,
`/tmp/koh-vblank-postcommit.log`;100 unrelated cases filtered.

Requires the .NET10 SDK and existing emulator fixtures. `KOH_DOTNET` can select
the SDK executable. The local run used a separate Cargo target directory.
