# Video lifecycle and input contracts

All four `Frontends/CilVideoJoypadTests.cs` methods now have native replacements.
This increment closes three methods:13 source assertion sites, five original
expanded cases and22 expanded assertions. The initialization method was already
closed by the real tile/video initialization driver and is not counted twice.

## Independent requirements

[LCDC bit meanings](https://raw.githubusercontent.com/gbdev/pandocs/master/src/LCDC.md)
justify F7 for enabled LCD, window at9C00,8000 tile data,8×16 objects and enabled
objects/background. The original window coordinates24/40 require WX31/WY40.
Extra transitions independently check sprite/window disable, stopping the display,
changing object size while stopped and restarting with the retained layer choices.
While stopped, only the LCD-enable bit is constrained; the private convention for
the other hardware bits is not treated as a game-facing requirement.

[Rendering timing](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Rendering.md)
defines154 scanlines and a4560-dot VBlank. Frame markers must occur on lines144–153
and successive markers must be70224±4560 dots apart. This permits different work
inside each VBlank while excluding same-frame returns and skipped whole frames.
Actual SCX changes are observed after CPU instructions; authored LY reads are
checked independently. Three commits retain frame counts1,2,0, including byte wrap.
Only the first dirty sprite shadow causes DMA; subsequent clean commits still wait.

[JOYP matrix selection and active-low input](https://raw.githubusercontent.com/gbdev/pandocs/master/src/Joypad_Input.md)
justify the direction masks. The test host explicitly holds Right before the first
two `Pressed` calls:1 then0. Only afterward does the existing SCX handshake release
Right and press Left. The packet checks released state, a new edge2, held edge0,
full-mask2, `IsPressed` and real register round trips. No constructor input state
or legacy compiler result supplies the expected values.

## Source disposition

| Source method | Decision and native target | Contract retained |
|---|---|---|
| LcdcComposesAcrossShowCallsAndStart | PORT complete; `driver::video_joypad::video_lifecycle_and_joypad_edges_execute`, Composition/LayerTransitions | Actual LCDC F7, WX31, WY40; independent additional layer transitions |
| Init_ClearsBothTileMapsAndOamAndSetsDefaultDmgPalette | Already PORT complete; `driver::actual_tileset_load_copies_vram_and_preserves_banks`, VideoInit | Whole owned map/OAM bytes, palettes and stopped display; no new credit |
| EndFrame_WaitsForVblankAndAdvancesFrameCount | PORT complete; new driver, Frames | Real VBlank observations, count1, additional clean-frame pacing and255→0 wrap |
| Pressed_ReportsRisingEdgeOnceForAHeldButton | PORT complete; new driver, Held | Right remains held across the first two calls; added release/new-button phases |

Four rstest cases build ordinary Roslyn DLLs in Debug/Release, reuse a resolver
across four roots and execute16 DMG/CGB ROMs after native compilation, ELF
round-trip and Rust linking. Native compilation verifies IR. Assertions retain
result42, restored SP, guard bytes, packet tails, complete OAM/owned VRAM contents
and alternate WRAM-bank sentinels. Test-only lifecycle observation explicitly
starts LCDC off and sets scroll/window state, poisons memory and records PPU ticks.
The existing emulator algorithms are unchanged. This does not establish arbitrary
CGB attribute initialization or replace the independent rendered-game tests.

## Runtime correction and evidence

The first execution selection failed4/4 in12.637s, exit100
(`/tmp/koh-video-joypad-second.log`): the minimal frame commit returned after
VBlank. Clean maps still entered both large commit helper frames. Moving the dirty
checks to their sole dispatcher avoided those frames, but Debug first commits
still left insufficient time for the caller: the intermediate run passed2/4
in12.334s (`/tmp/koh-video-joypad-dispatch.log`).

Map preparation now returns whether either map has pending work. `Video.EndFrame`
uses that result to skip the dispatcher entirely on clean frames. Dirty commits
retain the same preparation, bank pin and transfer paths; per-map dirty checks
remain in the dispatcher. Public game APIs and the compiler ABI are unchanged.
These are ordinary target-runtime C# methods compiled by Rust, not compiler logic.
The original LY requirement was retained rather than accepting late frame returns.

Focused4/4 passed12.815s, exit0 (`/tmp/koh-video-joypad-clean.log`). Affected
graphics/framework/input23/23 passed58.089s, exit0
(`/tmp/koh-video-joypad-regression.log`); real showcase/full-map/lazy-map10/10
passed53.425s, exit0 (`/tmp/koh-video-joypad-games.log`). All four real showcase
configurations remain checked. These are selected Linux results, not current-head
full workspace/three-host acceptance. An initial host compile probe failed on a
local-name collision; it supplied no ROM correctness evidence.

Ledger534/565 complete (533 native replacements, one earlier obsolete parser
DELETE),31 outstanding, zero legacy definitions physically retired.

All-feature discovery reports2123 workspace/1946 compiler cases, zero ignored and
all four new cases (`/tmp/koh-video-joypad-discovery.json`, exit0). Separate
workspace rustdoc (currently zero cases), rustfmt and diff checks passed. CSharpier
formatted the changed fixture/runtime files. The existing cubes `build_host`
dead-code warning remains. Discovery is not full-suite execution.

```sh
cargo nextest run -p koh-compile --test driver --locked -E 'test(video_joypad::)'
cargo nextest run -p koh-compile --test driver --locked -E 'test(full_graphics_showcase) | test(full_cgb_map_transfers) | test(lazy_maps)'
```
