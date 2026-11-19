# Managed machine oracle

`oracle.cs` emits three deterministic, generated SM83 programs and executes them
through the managed engine: DMG, CGB, and CGB with an actual KEY1/STOP speed
switch. The program bytes are the test inputs, not downloaded ROMs.

The generator uses .NET SDK 10.0.115 with an assembly reference to the accepted
`Koh.Emulator.dll` built from baseline
`e1336b048a67549ed99a8849ac78479213f570c8`. Its SHA-256 is
`973a5dbe4f4ede0893a4adbde947e56835823b7afdb9e2292902576ed8512281`.
No project reference or Rust code participates in fixture generation.

To regenerate, put `oracle.cs` in a temporary net10.0 executable project with a
`Reference`/`HintPath` to that assembly, then pass an output directory as the
program's sole argument. The adjacent `.gb` files are its generated programs.

Each `.bin` contains eight length-prefixed raw `GameBoySystem.WriteState`
payloads: after 0, 1, 10, 100, 1000 and 10000 instructions, followed by two
`RunFrame` calls. Then come the exact front and back RGBA buffers (92160 bytes
each), signed 32-bit PCM sample count, little-endian signed 16-bit samples,
signed 32-bit mode-3 hook event count and the hook records. Each record is
address:u16, value:u8, PC:u16, LY:u8, dot:i32 and PPU mode:i32, little-endian.
Each hook record then includes IME:bool, CPU T-cycles:u64, system ticks:u64,
LCDC:u8, SCX:u8, boot-mapped:bool and current ROM bank:u8. A final count-prefixed
HBlank event list contains system ticks:u64, LY:u8, dot:i32 and SCX:u8. These
timestamps and pre-increment HBlank dots come from the managed callbacks.

The Rust test compares all 24 raw payloads byte-for-byte and reads each into a
fresh machine before comparing its writer output. It also compares all 552960
framebuffer bytes, 7495 PCM samples and every mode-3 write's address, value,
PC, LY, dot and mode. The source program exercises CPU/bus ordering, poisoned
power-on RAM, register writes, tile/window/sprite rendering, CGB banks and
palettes, all audio channels, timer reload, serial transfer, HRAM execution
during OAM DMA, GDMA dot burning, HBlank DMA and normal/double-speed clocks.

Final managed captures:

| Case | System dots | CPU T-cycles | PCM samples | PC |
| --- | ---: | ---: | ---: | --- |
| Dmg-0 | 272828 | 272828 | 2701 | 0F3D |
| Cgb-0 | 272892 | 272828 | 2701 | 0F3D |
| Cgb-1 | 215038 | 413256 | 2093 | 0F3F |

SHA-256:

| File | Hash |
| --- | --- |
| Cgb-0.gb | `de0634afafa733a4f909c837a11a9a1e817ef5fd4200add6dd88436f7edbafb2` |
| Cgb-1.gb | `c2ec91072423fd0c62ca22e51ea7cbac483598b09e921febc03df93be0d3ea0b` |
| Dmg-0.gb | `05f7d7fd478b766acbf40127c58794524d6378261dd8347b6bafa7fc2d3baf0f` |
| Cgb-0.bin | `ba65c9cfe43a296e6aede9235f567596e136a4c7ee5960b29384da1c7832b453` |
| Cgb-1.bin | `7b9ea0beae787b1a536cedb7cd4d95008b361074823584f10c3a8fb85496190c` |
| Dmg-0.bin | `fe149a0fb2611062a1909c757390c5a551f8c77b9713f7990203c22d9196ff3d` |

Focused Rust cases port the behaviors in `MmuTests`, `PowerOnStateTests`,
`CgbBankingTests`, `BootRomTests`, `OamDmaTests`, `HdmaTests`, `SerialTests`,
`GameBoySystemTests` and `RunUntilSpinDetectionTests`. Boot coverage checks
family/size, overlay/header hole, one-way unmap, raw-state latch restore and
fingerprint rejection before mutation. The boot identity remains FNV-1a,
matching the managed format. No post-boot CPU or palette state is fabricated.

`Machine::write_state` writes the raw payload used by sessions. It does not
add the separate `SaveStateFile` utility's STES envelope. Failed restore rollback
belongs to the caller. Neither framebuffer pixels nor joypad input are included
in the managed raw payload. Existing `StopCondition` bank-filter/VBlank/Return
metadata remains inert, matching the managed run loop.
