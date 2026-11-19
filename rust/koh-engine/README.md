# Native execution host

`koh-engine` owns one `Machine` and `Debugger` on its execution thread.
Session stdin and DAP framing threads submit through a bounded 32-entry queue.
All mutations, inspections, snapshots and persistence run between frames or
instructions, including while paused. There is no managed runtime fallback.

```sh
cargo build -p koh-engine --release --locked
koh-engine --session --headless game.gb
koh-engine game.gb --headless --frames=120 --screenshot=frame.png
koh-engine game.gb --dap=example --headless
```

Session commands and snapshots follow `docs/engine-session.md`. CLI boot-ROM
overrides, input scripts, source-mapped mode-3 reports and PNG captures retain
their existing syntax. Default DMG/CGB boot images come from the core's native
assembly build; the CPU executes them normally.

Session `.state` files are the existing raw core payload. They do not use the
separate `SaveStateFile` utility's STES/version/hash envelope. Failed restore
rolls the core back before returning an error. SRAM/state writes use a temporary
file and replacement rename; ROM changes and normal/error shutdown flush SRAM.

DAP uses `/tmp/CoreFxPipe_NAME` on Unix and `\\.\pipe\NAME` on Windows. EOF or
disconnect ends the engine even if session stdin remains open. Broken DAP output
is treated as a disconnected client. Diagnostics remain on stderr.

OpenAL is loaded from the executable's directory or platform library search
path. Missing libraries/endpoints leave session/DAP operation available with
wall-clock pacing. The sink preserves eight 512-sample buffers, seven-buffer
startup warmup, remainder staging, underrun restart and rewind/reset behavior.

Run the actual process and reference comparisons:

```sh
KOH_REFERENCE_ENGINE=/path/to/immutable/Koh.Emulator.App \
ALSOFT_DRIVERS=null LD_LIBRARY_PATH=/path/to/openal \
cargo test -p koh-engine --locked -- --include-ignored
```

The explicitly ignored tests require the reference environment variable for independent DMG/CGB CPU/snapshot,
raw-state, decoded PNG and source-mapped mode-3 comparisons. The OpenAL null
backend enables the sink's frame-sized warmup/reset/restart check across three resets.
Default runs show these checks as ignored; explicitly selecting them without their
required environment fails. The remaining process tests run normally. No audible-output claim follows from
the null-backend check.
