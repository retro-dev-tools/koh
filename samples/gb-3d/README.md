# Runtime-generated 3D cube

Three Game Boy ROMs render the same filled, rotating cube from ordinary Koh C#. There are no authored
tiles, prerendered angles, or frame assets: the mesh is transformed, culled, depth-sorted, rasterized,
dithered, and encoded into Game Boy 2bpp tile memory at runtime.

| ROM | Viewport | Presentation strategy |
| --- | --- | --- |
| `double-buffered/cube-double-buffered.gb` | 96x80 | Two 120-tile pages; render/upload the inactive page, then flip the tile map in VBlank. |
| `full-frame/cube-full-frame.gb` | 128x128 | One 256-tile page; blank the LCD for a complete 4 KiB upload. |
| `racing-beam/cube-racing-beam.gb` | 32x32 | Compact generated page plus per-scanline SCX changes synchronized to HBlank. |

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

The verifier boots all three ROMs in both DMG and CGB modes, runs 600 hardware frames, checks that the
framebuffer is nonblank, and writes screenshots under `verify/out/`. Pass one or more renderer names
(`double-buffered`, `full-frame`, `racing-beam`) to limit a run.

The PPU still consumes tile-formatted VRAM because that is the Game Boy's display hardware. Here the
tiles are a transport for a runtime-generated bitmap, not a tileset or authored art pipeline.
