# Palette storage and RGB contracts

The native `tests/driver/palettes.rs` builds real Debug/Release Roslyn assemblies,
resolves Koh.GameBoy, compiles canonical ELF and runs linked DMG/CGB ROMs in the
existing emulator. Palette storage and output registers are observed after
explicit initialization; emulator constructor defaults are not requirements.

| Legacy `Frontends/CilPalettesRgbTests.cs` method | Decision | Independent contract |
| --- | --- | --- |
| `SetBgSetObj_OnDmg_WriteDmgShadesToBgpAndObp0` | PORT | Authored shade bytes reach BGP/OBP0 on DMG; RGB bytes reach separate CGB palette memories. |
| `SetObj_OnDmg_Slot1_WritesObp1` | PORT | DMG object slot1 selects OBP1 without changing OBP0. |
| `SetBgSetObj_OnCgb_WriteRgb555ColorsToPaletteRam` | PORT | Four little-endian colors occupy the requested eight-byte BG/OBJ slots. |
| `SetBgSetObj_OnDmg_SlotsAboveHardwareRange_AreNoOps` | PORT | Unsupported DMG slots preserve existing shade registers; the same valid slots store colors on CGB. |
| `RgbMake_PacksChannelsIntoRgb555` | REPLACE | Rust computes `(r & 31) | ((g & 31) << 5) | ((b & 31) << 10)` independently, replacing a host call to the same C# implementation. |

These five methods contain27 assertion sites and seven parameterized cases,
expanding to42 assertions. The native four rstest configurations execute36
successful ROMs plus six hardware-fault ROMs. Additional coverage includes every
RGB channel across32 byte-valued inputs, slot7, both data ports, LCD-on writes,
large frame offsets, repeated writes after EI and armed double-speed CGB state.
Full BG/OBJ memories, final index/data ports, complete result packets, DMG shade
registers, stack balance, OAM and alternate WRAM guards are checked. Five malformed
intrinsic declarations per configuration yield20 explicit compilation failures.

## Hardware transaction

The prior library waited for VRAM access before entering CIL helpers. Register
preparation and calls could consume that interval: the new LCD-on probes exposed
actual mode3 BG writes in both CGB build configurations. The fix is a small Rust
operation, `palettewrite`, selected by metadata on a static
`void(byte index, ushort color)` method with data port FF69 or FF6B. Invalid
contracts fail compilation; odd indices, indices above62 and absent CGB hardware
trap with hardware fault9. IRQ roots reject this non-reentrant operation.

The backend stages the index and color, disables interrupts, selects auto-increment,
polls STAT until mode0/1, and emits the two data bytes without calls or stack access.
From the sampled STAT value to the last data write takes52 normal-speed T-cycles,
less than the80-dot mode2 interval before mode3. Double-speed CPU execution reduces
the elapsed dots. It then restores the final high-byte index with auto-increment
disabled. Staging uses four transient stack bytes; large-offset tests check that
address calculation cannot corrupt the color or index through register aliasing.

The operation leaves IME disabled. This explicit postcondition avoids pretending
SM83 can read and restore IME; it matches the cooperative graphics/DMA contract.
Preserving arbitrary caller IME would require a broader software interrupt ABI.
Moving all writes to VBlank would impose unnecessary latency. Public palette
signatures are retained; duplicated object-color writers are removed. Authors
using interrupts must explicitly re-enable them after the transaction.

KEY1=FF is ambiguous: CGB double speed with its switch armed also reads FF.
The compiler guard and `Cgb.IsColor` now clear the writable arm bit only when
needed, read back, and restore the armed state on CGB. No STOP is executed, so
current speed is unchanged. Both direct intrinsic and ordinary library paths
assert that speed remains doubled and the switch remains armed.

Hardware justification: [Pan Docs palettes](https://github.com/gbdev/pandocs/blob/master/src/Palettes.md)
defines RGB555 storage, separate64-byte banks, indexing, auto-increment and mode3
restrictions; [rendering](https://github.com/gbdev/pandocs/blob/master/src/Rendering.md)
defines the80-dot OAM phase; [KEY1](https://github.com/gbdev/pandocs/blob/master/src/CGB_Registers.md#ff4d--key1spd-cgb-mode-only-prepare-speed-switch)
defines the writable arm bit and STOP-controlled speed change. ROM execution
checks these selected contracts; it does not establish physical-hardware accuracy
of every emulator timing behavior.

## Verification record

The armed-state reproduction passed DMG cases and failed both CGB cases before
the detection fix:2/4 passed,27.384s, exit100,
`/tmp/koh-palette-armed-red.log`. After the fix all four palette configurations
passed; two neighboring HAL cases exposed obsolete exact manual-index traces.
Their replacement replays the hardware indexing protocol and preserves exact
effective byte writes, full memories, bank writes and final index readback.
Final focused14/14 passed33.322s, exit0,1273 explicitly unselected:
`/tmp/koh-palette-armed-final.log`. This includes all four palette drivers, four
HAL drivers and six IR cases. IRQ rejection and all six CGB Debug/Release cube
variants passed8/8 in39.810s, exit0,981 unselected:
`/tmp/koh-palette-speed-neighbors.log`. Existing CPU control/speed-switch cases
passed4/4 in9.059s, exit0,643 unselected:
`/tmp/koh-palette-cpu-control.log`. Separate compiler rustdoc succeeds with zero
doctests. These are local Linux selections, not a full current-head or hosted
cross-platform run. The full migration remains incomplete.

Reproduce the focused selection with the pinned .NET SDK on PATH:

```sh
cargo nextest run -p koh-compile --test driver --test ir --lib --locked \
  --no-fail-fast -E 'test(palettes::) | test(cgb_hal::) | test(palette_write)'
cargo nextest run -p koh-compile --test driver --test cubes --lib --locked \
  --no-fail-fast -E 'test(palette_transactions) | test(cpu_control_and_speed_switch_execute) | (test(cube_boots_and_animates) & test(cgb))'
cargo test -p koh-compile --doc --locked
```
