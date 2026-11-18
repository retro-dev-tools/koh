# Runtime-generated 3D cube

Three Game Boy ROMs render the same filled, rotating cube from ordinary Koh C#. There are no authored
tiles, prerendered angles, or frame assets: the mesh is transformed, culled, depth-sorted, rasterized,
dithered, and encoded into Game Boy 2bpp tile memory at runtime.

| ROM | Viewport | Presentation strategy |
| --- | --- | --- |
| `double-buffered/cube-double-buffered.gb` | 96x80 | Two 120-tile pages; render/upload the inactive page, then flip the tile map in VBlank. |
| `full-frame/cube-full-frame.gb` | 128x120 | One 240-tile page; CGB uses bounded GDMA bursts across VBlanks with the LCD on; DMG blanks the LCD for one bulk `Mem.Copy` upload. |
| `racing-beam/cube-racing-beam.gb` | 64x64 | Compact generated page plus per-scanline SCX changes synchronized to HBlank. |

The third experiment originally attempted to replace shared tile rows just behind the PPU fetch. Real
DMG timing showed that even a very narrow generated-C# copy could not reliably meet HBlank. The shipped
version therefore uses the viable raster-effect form of the technique: C# still draws the cube pixels,
then races the beam with scanline scroll changes. `Ppu.StreamScrollX` streams a precomputed table in
native code; ordinary C# polling/computation cannot guarantee HBlank timing. Nonempty streams leave
interrupts disabled. See [the contract and executable evidence](../../../docs/rust-compiler/raster-stream.md).

All ROMs are dual-compatible. On DMG they use the four-shade background palette. On CGB they detect
`KEY1`, switch to double speed, and program a color background palette. The scene and renderer remain
the same on both machines.

## Build

```powershell
dotnet build samples/csharp/gb-3d/double-buffered/CubeDoubleBuffered.csproj
dotnet build samples/csharp/gb-3d/full-frame/CubeFullFrame.csproj
dotnet build samples/csharp/gb-3d/racing-beam/CubeRacingBeam.csproj
```

Each project emits its `.gb` beside the project file. `dotnet run --project <project>` builds and opens
the ROM in Koh.Emulator.

## Headless verification

```powershell
cargo nextest run -p koh-compile --test cubes --locked
```

Rust tests compile the actual samples in Debug/Release, link canonical ELF and boot all three ROMs
in the existing DMG/CGB emulator. Two captures must show a shaded, connected, centered cube that
animates within bounded frame counts. VRAM-write guards, complete GDMA timing and actual racing-beam
SCX/scanline traces also run. Select a variant with nextest filters, for example
`-E 'test(racing_)'` or `-E 'test(double_)'`.

The same target drives the shared renderer through all256 phases at64x64,96x80 and128x120 in both
Roslyn configurations. Rust checks nonempty output, palette, two-pixel edge clearance and a10..75%
bounding-area band. This preserves the old CLR geometry sweep; it is not all-phase native ROM
execution, nor a sweep of the separate Canvas-based double-buffered renderer. Use
`-E 'test(shared_renderer_sweeps)'` for only this check. See [test disposition](../../../docs/rust-compiler/cube-phase-sweep.md).

For a persistent PNG capture of a built ROM, use the existing emulator CLI:

```powershell
New-Item -ItemType Directory -Force artifacts
dotnet run --project src/Emulator/Koh.Emulator.App -c Release -- --headless samples/csharp/gb-3d/racing-beam/cube-racing-beam.gb --frames=500 --screenshot=artifacts/cube-racing-beam.png --mode3-report
```

The obsolete standalone C# verifier and its solution entries have been removed. Generated captures
belong in artifacts, not tracked sample assets.

The PPU still consumes tile-formatted VRAM because that is the Game Boy's display hardware. Here the
tiles are a transport for a runtime-generated bitmap, not a tileset or authored art pipeline.

## Performance

The measurements below describe the retired C# compiler's output, not the Rust
compiler. Native validation and remaining failures are tracked in
[the compiler migration evidence](../../../docs/rust-compiler/progress.md).
CGB copies now prepare registers before waiting and limit LCD-on bursts to1024
bytes: full-frame presentation spans four VBlanks, with partial-page seams.
This intentionally replaces the unsafe two-burst assumption; the larger viewport
and continuously enabled CGB display remain. A CPU-only mode3 guard cannot prove
DMA safety, so native tests also check complete-transfer timing and exact VRAM.

The current [bulk-memory contract](../../../docs/rust-compiler/memory-copy.md)
lowers `Mem.Copy`/`Mem.Fill` through native Rust IR and SM83 loops. Its3840-byte
copy probe takes217748T at normal CPU speed; this is not a whole-frame cadence.
The historical C# compiler measurements below used framebuffer differences
to measure frames per render/present cycle:

| ROM | CGB cadence | DMG cadence |
| --- | --- | --- |
| double-buffered | 19-47 frames/flip (one GDMA transfer per VBlank) | 340-344 frames/flip (VBlank-chunked `Mem.Copy`, 7 bytes/VBlank) |
| full-frame | 23-51 frames/cycle (two-VBlank GDMA halves) | 59-114 frames/cycle (one LCD-off `Mem.Copy(3840)`) |
| racing-beam | 17-41 frames/cycle | 33-80 frames/cycle (one LCD-off `Mem.Copy(1024)` plus the HBlank-paced SCX wobble) |

double-buffered's and full-frame's DMG paths keep the LCD-off/vblank-chunked tradeoffs documented in
each `Surface.cs`; on CGB those two switch to `Cgb.CopyToVram` general-purpose DMA instead.
racing-beam is the exception: its `Present()` doesn't branch on `Cgb.IsColor()` at all — both DMG and
CGB take the same LCD-off `Mem.Copy(1024)` upload ahead of the per-scanline SCX wobble.
