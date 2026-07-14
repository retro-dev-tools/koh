# Runtime-generated 3D cube

Three Game Boy ROMs render the same filled, rotating cube from ordinary Koh C#. There are no authored
tiles, prerendered angles, or frame assets: the mesh is transformed, culled, depth-sorted, rasterized,
dithered, and encoded into Game Boy 2bpp tile memory at runtime.

| ROM | Viewport | Presentation strategy |
| --- | --- | --- |
| `double-buffered/cube-double-buffered.gb` | 96x80 | Two 120-tile pages; render/upload the inactive page, then flip the tile map in VBlank. |
| `full-frame/cube-full-frame.gb` | 128x120 | One 240-tile page; on CGB, two GDMA transfers across two VBlanks with the LCD kept on; on DMG, blank the LCD for one bulk `Mem.Copy` upload. |
| `racing-beam/cube-racing-beam.gb` | 64x64 | Compact generated page plus per-scanline SCX changes synchronized to HBlank. |

The third experiment originally attempted to replace shared tile rows just behind the PPU fetch. Real
DMG timing showed that even a very narrow generated-C# copy could not reliably meet HBlank. The shipped
version therefore uses the viable raster-effect form of the technique: C# still draws the cube pixels,
then races the beam with scanline scroll changes. It does not silently substitute either other backend.

All ROMs are dual-compatible. On DMG they use the four-shade background palette. On CGB they detect
`KEY1`, switch to double speed, and program a color background palette. The scene and renderer remain
the same on both machines.

## Build

```powershell
dotnet build samples/gb-3d/double-buffered/CubeDoubleBuffered.csproj
dotnet build samples/gb-3d/full-frame/CubeFullFrame.csproj
dotnet build samples/gb-3d/racing-beam/CubeRacingBeam.csproj
```

Each project emits its `.gb` beside the project file. `dotnet run --project <project>` builds and opens
the ROM in Koh.Emulator.

## Headless verification

```powershell
dotnet run --project samples/gb-3d/verify/Cube3dVerify.csproj
```

The verifier boots all three ROMs in both DMG and CGB modes, runs a per-ROM-per-mode frame budget tuned
to each demo's measured render+present cadence (see `verify/Program.cs`), and checks structural
properties a real rendered cube must have (deterministic, not a golden-image comparison): at least two
distinct non-background shades on screen, all of them inside one bounding box comfortably clear of the
screen edge and of a plausible size, a uniform background outside that box, and a later frame that
differs from the first (the cube is animating, not frozen). It also sweeps the shared renderer across
all 256 rotation phases per viewport geometry, checking the same edge-clearance/area-band properties
independent of any ROM's timing. It writes screenshots under `verify/out/`. Pass one or more renderer
names (`double-buffered`, `full-frame`, `racing-beam`) to limit a run.

The PPU still consumes tile-formatted VRAM because that is the Game Boy's display hardware. Here the
tiles are a transport for a runtime-generated bitmap, not a tileset or authored art pipeline.

## Performance

Every present path moves pixel data through `Mem.Copy`/`Mem.Fill` (`src/Koh.Compiler/Frontends/CSharp/
MemRuntime.cs`), the Koh C# runtime's bulk-memory primitives, rather than hand-rolled per-byte loops.
Frame-by-frame framebuffer diffing against the built ROMs (the technique in `verify/Program.cs`'s
comments) measured these steady-state render+present cadences (frames per cycle; a smaller number is a
faster flip):

| ROM | CGB cadence | DMG cadence |
| --- | --- | --- |
| double-buffered | 19-47 frames/flip (one GDMA transfer per VBlank) | 340-344 frames/flip (VBlank-chunked `Mem.Copy`, 7 bytes/VBlank) |
| full-frame | 23-51 frames/cycle (two-VBlank GDMA halves) | 59-114 frames/cycle (one LCD-off `Mem.Copy(3840)`) |
| racing-beam | 17-41 frames/cycle | 33-80 frames/cycle (one LCD-off `Mem.Copy(1024)` plus the HBlank-paced SCX wobble) |

double-buffered's and full-frame's DMG paths keep the LCD-off/vblank-chunked tradeoffs documented in
each `Surface.cs`; on CGB those two switch to `Cgb.CopyToVram` general-purpose DMA instead.
racing-beam is the exception: its `Present()` doesn't branch on `Cgb.IsColor()` at all — both DMG and
CGB take the same LCD-off `Mem.Copy(1024)` upload ahead of the per-scanline SCX wobble.
