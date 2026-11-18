# Native tile loading and rendered frames

The native pipeline executes the unchanged `TileSet.Load`, `Mem.Copy`, `Mem.Fill`
and CGB palette/DMA routines in `Koh.GameBoy`. The compiler does not replace these
methods with a test renderer or desktop implementation. Four Rust driver cases
cover Roslyn Debug/Release on DMG and CGB, with12 ROM executions in total.

## Independent observations

The LCD-off copy fixtures load two tiles at index3. They compare the complete
8192-byte VRAM bank and4096-byte heap against independently expected bytes,
including poison outside the allocations and destination. CGB bank1 VRAM and
bank2 WRAM must remain untouched. A caller-selected WRAM bank2 must be restored.

An explicit padding allocation puts the aligned source at `$D010`; destination
tile3 begins at `$8030`. On CGB, the emulator reports those programmed GDMA source
and destination registers and an inactive, completed general-purpose transfer.
Unaligned and DMG paths must leave DMA parameters at their pre-execution values.
These observations distinguish DMA execution from an equivalent CPU copy without
treating compile time or instruction count as a correctness oracle.

The render fixture loads one alternating bit-plane tile, clears the background
map, sets explicit DMG/CGB palettes and enables the display. The unchanged emulator
then runs two frames. Rust checks every160×144 RGBA pixel against the independent
2bpp bit-plane equation, including alpha; this catches orientation, plane order,
tile repetition and palette errors. CGB uses white/red/green/blue RGB555 extrema;
DMG checks the emulator's canonical four grayscale shades.
The bit-plane and RGB555 contracts come from
[Pan Docs tile data](https://github.com/gbdev/pandocs/blob/master/src/Tile_Data.md)
and [palette documentation](https://github.com/gbdev/pandocs/blob/master/src/Palettes.md).

The host also writes PNG through the existing framebuffer writer and Rust checks
its signature and dimensions. The existing emulator PNG tests retain ownership
of compression/format correctness. A single test-friend declaration allows the
native compiler's C# execution shim to call that writer; no emulator algorithm
changes or second PNG encoder are introduced. Captures live in test-owned
temporary directories, never tracked sample assets.

## Ownership and limits

`tests/driver.rs::actual_tileset_load_copies_vram_and_preserves_banks` owns all
assertions and is discoverable by cargo-nextest. C# files under
`tests/fixtures/Tiles` are Roslyn inputs. `EmulatorHost` only drives the existing
emulator, snapshots memory and writes its framebuffer. Graphics source assets
and the game-facing API remain unchanged.

The fixture prepares LCD-off state directly and sets `Video.IsCgb` using the
actual hardware detection method. It does not claim full `Video.Init`, a complete
sample game, sprite/object/scene support, LCD-on transfer safety, source debugging
or SDK replacement. Those requirements remain active in this PR. The original
TileSet test's boot/Video.Init dependency is still outstanding even though its
tile-copy behavior now has stronger native execution evidence.

Focused copy/render checks passed4/4 before the DMA observations were added.
The formatted strengthened run passed4/4, exit0,17.682s
(`/tmp/koh-tiles-dma.log`). Source review found no concrete defect. Current-tree
workspace and standalone demonstration results are recorded in `progress.md`.

## Standalone demonstration

From the repository root, after installing the pinned .NET SDK and building the
native tools, the following host-native commands preserve ROMs and captures for
inspection. Run builds serially. The test host uses the existing C# emulator.

```sh
cargo build -p koh-compile -p koh-link --locked
koh_demo_dir=$(mktemp -d)
dotnet build rust/koh-compile/tests/fixtures/Tiles/Tiles.csproj \
  -c Release -m:1 -p:KohRoot="$PWD" -o "$koh_demo_dir/managed"
dotnet build rust/koh-compile/tests/fixtures/EmulatorHost/EmulatorHost.csproj \
  -c Release -m:1 -p:KohRoot="$PWD" -o "$koh_demo_dir/host"
target/debug/koh-compile "$koh_demo_dir/managed/Tiles.dll" \
  -r "$koh_demo_dir/managed/Koh.GameBoy.dll" --entry Program::Render \
  -o "$koh_demo_dir/tiles.o"
target/debug/koh-link -o "$koh_demo_dir/tiles.gb" \
  --report-json "$koh_demo_dir/tiles.json" "$koh_demo_dir/tiles.o"
koh_exit_addr=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(format(next(s["value"] for s in d["symbols"] if s["name"]=="__koh_exit"),"x"))' "$koh_demo_dir/tiles.json")
dotnet "$koh_demo_dir/host/EmulatorHost.dll" --native-frame \
  "$koh_exit_addr" "$koh_demo_dir/tiles.gb" > "$koh_demo_dir/execution.txt"
```

The host writes `tiles.png` and `tiles.rgba` next to `tiles.gb`. For CGB, select
`Program::BankedRender` and pass `--cgb-only` to the linker. This emits the colored
pattern and checks the caller's WRAM bank restoration. The Rust acceptance command
is `cargo nextest run -p koh-compile --test driver -E 'test(actual_tileset)' --locked`.
