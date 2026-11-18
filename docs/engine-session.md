# Desktop engine session

`Koh.Emulator.App --session [rom] [--dap=<pipe>]` runs without a window.
`--headless` also disables audio. Without a ROM it starts paused with an empty
display. Boot ROM overrides retain their existing command-line syntax.

The Rust desktop owns stdin, stdout and process lifetime. Send UTF-8 JSON
objects separated by newlines; read exactly one JSON reply per command.
Diagnostics and audio pacing go to stderr. There are no unsolicited stdout
messages. EOF or `quit` stops execution and flushes SRAM before process exit.
Wait for exit before treating shutdown persistence as successful.

Each command requires a nonnegative signed 64-bit `id` and a string `command`.
Replies echo `id` and contain `ok`, `error` and `snapshot`. An unsuccessful
reply has an error string and no snapshot. Malformed commands can be corrected
on the same connection. Duplicate properties are rejected. Commands are
limited to 16384 UTF-16 code units and JSON depth 8. An oversized or incomplete
command ends the session with exit 1, still flushing SRAM.

```json
{"id":1,"command":"load","path":"/games/2048.gb","paused":true}
{"id":2,"command":"snapshot","debug":true,"address":49152}
{"id":3,"command":"resume"}
```

| Command | Additional fields | Result |
| --- | --- | --- |
| `snapshot` | None | Copy coherent current state |
| `load` | `path`: string; optional `paused`: bool, default false | Validate/load ROM; retain current system on failure |
| `reset` | None | Reload current ROM; preserve pause state |
| `pause`, `resume` | None | Complete at a frame boundary |
| `step` | None | Execute one instruction while paused |
| `joypad` | `button`: Up/Down/Left/Right/A/B/Start/Select; `down`: bool | Apply an input edge |
| `fastForward` | `enabled`: bool | Enable/disable uncapped execution |
| `releaseInput` | None | Release all buttons and fast-forward |
| `save`, `restore` | None | Complete `<rom>.state` operation before replying |
| `quit` | None | Pause, reply, then exit and flush `<rom>.sav` |

All commands accept optional `debug` (bool, default false) and `address`
(unsigned 16-bit integer, default 0). These select additional snapshot data.
Common fields are validated before mutation.

Snapshots use camelCase properties. `protocolVersion` is 1. `romPath` and
`mode` are null in the empty state; otherwise mode is `Dmg` or `Cgb`.
`frame`, `paused` and `fastForward` describe execution state. `cpu` carries
the registers, flags and total T-cycles from the same boundary. `framebuffer`
is base64-encoded 160×144 RGBA8888 bytes. Pixel alpha is opaque.

With `debug`, `palettes` contains decoded RGB8 `{r,g,b}` colors, DMG palette
registers and mode; `vram` carries base64 `rgba`, width 128, height 192 (DMG)
or 384 (CGB); `memory` carries `baseAddress` and base64 `bytes` of length 256,
with 16-bit wraparound and current cartridge banking. Otherwise these are null.
Every image/window is copied between frames; rendering never borrows live
emulator buffers. The client must validate version, enum values and lengths.

The engine thread also processes commands while paused. DAP dispatch uses the
same boundary queue. Failed save writes are reported; corrupt restore rolls
back the core before replying. Existing raw `.state` format remains in use.

## Completed Linux verification

- CI solution Release build: exit 0, zero warnings/errors;
  `/tmp/koh-engine-solution-build.log`.
- Serial fast suite: exit 0, 581 total, 579 succeeded, zero failures, two
  existing opt-in graphics skips; `/tmp/koh-engine-fast-tests-final.log`.
- Fresh NativeAOT publish to `/tmp/koh-gpui-engine-native`: exit 0, no warnings;
  `/tmp/koh-engine-native-publish.log`. Engine SHA256:
  `0f4be5e41e9e67f53943633a94242797e52c2bd1aff37fd222215dc5cc468398`.
- Nine process cases against that native executable: exit 0, all nine passed;
  `/tmp/koh-engine-native-process-tests.log`. Includes source breakpoints from
  actual DWARF, stepping/memory, DAP EOF/disconnect with stdin held open,
  DMG/CGB images/inspectors, paused save/restore, corrupt-state rollback,
  joypad press/release, fast-forward, SRAM on EOF/Quit/oversized input.
- The managed debugger project also passed all 49 cases after the process
  fixtures gained source-breakpoint checks and the published-engine override;
  exit 0, `/tmp/koh-engine-debugger-tests-final.log`.
- Real `gb-2048-v2` ROM through the native session: 123 frames, 8638408 T-cycles,
  changed 160×144 opaque RGBA output, save/restore and exit 0;
  `/tmp/koh-engine-native-2048.log`. Captured framebuffer SHA256:
  `59ab7ed41472db0b2ce1bb52643df9314fba7182a625f8bd1c9c36e906ca78aa`.

To test a published engine, set `KOH_TEST_EMULATOR` to its absolute path when
running the debugger test project's `*ProcessTests` cases. Without it, the same
tests run the managed engine with the test host's runtime/dependency files.
This verification does not claim audible output or a rendered GPUI window.
