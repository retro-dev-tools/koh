# Native compiler fixture hosts

Developer-only execution helpers for `koh-compile` integration tests. This
crate is not published or installed with the toolchain. Run `koh-test-host`
with one of these names as its first argument, followed by the existing fixture
arguments:

- `EmulatorHost`: instruction/runtime packets, memory and bus traces, timing,
  video/DMA guards, IRQ ABI checks and framebuffer captures.
- `VBlankHost`: bounded copy/write observations at every scanline boundary.
- `GameHost`: real boot, frame input scripts and exact machine/video dumps.
- `CubeHost`: real boot, animation, GDMA and scanline traces and profiling.
- `ShowcaseHost`: real boot, animation, score timeline and pixel snapshots.
- `DebuggerHost`: line-delimited test requests and the native DAP dispatcher.

`tests/support::Host::command()` selects these native executables. The separate
`ManagedHost` and `SoftFloatHost` fixtures continue to use .NET as CLR reference
oracles. C# authoring fixtures still compile with the selected SDK. There is no
managed execution fallback for the six native hosts.

The support helper builds this crate in the repository's `target/test-host`,
protected by a platform file lock and cached once per nextest run. Ordinary
cargo-test processes get a unique process run ID. Each caller executes a
private copy inside its own temporary Host directory. The compilation target
remains shared for incremental reuse; build artifacts are never executed.

All runtime behavior uses `koh-emulator` and `koh-debugger`. Game-oriented
hosts execute the core's assembled DMG/CGB boot sources. Memory hooks preserve
the managed fixtures' PC, IME, CPU/system clocks and PPU phase observations.
HBlank scanline captures use actual PPU entry callbacks. PNG compression uses
the platform library. Fixture outputs remain beside temporary test ROMs.

The debugger fixture keeps its response-only transport and atomic step commands.
It completes the real boot instruction stream before adopting the debug session,
matching the managed fixture's launch behavior. Production DAP events belong to
the engine host.
