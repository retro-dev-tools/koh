# SDL3 native backend

Implementation lives in `src/UI/KohUI.Backends.Sdl3`; SDL3 Renderer owns native presentation and SDL owns windows/events. KohUI layout/widgets and OpenAL remain. Emulator core, PNG capture and headless DAP remain independent of video initialization.

The migration belongs to one branch/PR against master. Do not merge automatically. The pre-existing modified JRPG battle screenshot is excluded; its SHA-256 remains `fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`.

## Verification

- Full Release solution: zero warnings/errors.
- Linux x64 NativeAOT emulator and native UI demo publish successfully; SDL notices/native libraries are included.
- Renderer/input regressions pass with software surfaces, dummy windows, X11 and Wayland; both SDL software and Vulkan renderer paths were exercised, including 2x Wayland output density. See `docs/sdl3.md`.
- NativeAOT ROM/DAP execution, integer-scaled images, debugger panels and mouse interaction were exercised. Standalone headless PNG/DAP work without SDL/OpenAL libraries.
- Extension suite: 44 passed; browser UI test: 1 passed. Full suite: 2494 passed before the final nested-text/font-copy fixes. Latest focused renderer matrix: 5 passed on each native test environment. Both final Linux NativeAOT publishes completed; the subsequent whole-solution rebuild was interrupted at the user's request. Do not resume tests without being asked.

## Environment

Use SDK `/tmp/koh-dotnet-10.0.115`. A stopped unrelated Aspire process holds the default NuGet configuration lock; `NUGET_SCRATCH=/tmp/koh-sdl-nuget-scratch` avoids that lock for this session. Builds run serially; full tests use one module/test at a time with a 2 GiB GC heap cap.

Remaining runtime gaps: physical GPU/display behavior, monitor DPI hot-moves, and native Windows/macOS desktop sessions. Wayland software drawing uses SDL-managed Vulkan presentation; X11/dummy/offscreen software does not require it. Existing repository licensing/release blockers remain separate from the added SDL notices.
