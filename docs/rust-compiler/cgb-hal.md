# Referenced CGB HAL test migration

The remaining five `Samples/CgbHalTests.cs` methods (eight assertion call
sites) now have native replacements. Together with the two speed-switch
methods already mapped in [CPU control](cpu-control.md), all seven methods
and15 original assertion sites have dispositions backed by native execution.

| Source method | Decision | Native contract |
| --- | --- | --- |
| `CgbHal_CompilesThroughTheRealBackend` | REPLACE, complete | `driver::cgb_hal::referenced_cgb_hal_preserves_banks_and_palette_bytes` builds a real Roslyn DLL referencing Koh.GameBoy, compiles and links native ELF, then executes the actual Cgb/Ppu calls on DMG/CGB. The prior test only checked diagnostics from C# compiler classes. |
| `SelectVramBank_SelectsBank1_InCgbMode` | PORT, complete | Assert selected bank1 and VBK readback, then actual bank1 writes with bank0 unchanged. A round trip through bank0 checks independent contents in both banks. |
| `SelectVramBank_IsInertOnDmg` | PORT, complete | Assert bank0 remains selected, normal single-bank writes succeed, and the CPU issues no VBK/palette writes. Ignored device writes cannot hide incorrect lowering. |
| `SetBackgroundColor_WritesPaletteRam_InCgbMode` | PORT, complete | Palette0/color0 stores0x34/0x12 at bytes0/1; assert all remaining BG bytes and all OBJ bytes unchanged. |
| `SetBackgroundColor_WritesTheRequestedPaletteAndColorSlot` | PORT, complete | Palette2/color3 stores0xff/0x7f at bytes22/23; assert all other bytes unchanged. A full32-color sweep checks every valid slot and final address63. |

Independent hardware justification: [Pan Docs VRAM banking](https://github.com/gbdev/pandocs/blob/master/src/CGB_Registers.md#vram-banks)
specifies VBK bit0 and its readback; [CGB palette RAM](https://github.com/gbdev/pandocs/blob/master/src/Palettes.md#lcd-color-palettes-cgb-only)
contains64 bytes each for BG and OBJ, with little-endian two-byte colors and
mode3 access restrictions. The tests use valid palette/color indices and do
not freeze historical argument-masking policy or the current public API.

Four rstest configurations run Debug/Release × DMG/CGB. Six entry points per
configuration produce24 ROMs: compile/execute smoke, bank1, bank round trip,
palette0/color0, palette2/color3 and all32 valid background colors. The sweep
uses the independent grayscale value `slotIndex*0x421`, ending at0x7fff.
Rust checks full BG/OBJ contents, selected/index registers, exact CPU MMIO
write order and absence of those writes on DMG, return values, stack balance,
reserved-memory guards and unchanged WRAM banks. CGB checks cover1536
palette-byte observations across12 ROMs, not just the four original bytes.

The existing emulator host gains only setup and observation: explicit LCD-off
state, VRAM/palette sentinels and register-write snapshots. The added adapter contains no
compiler algorithms or behavioral acceptance assertions. The smoke entry
explicitly enables the LCD for its Ppu polling calls, waits for VBlank before
disabling it, and completes. Palette writes occur with the LCD off. Constructor
LCDC/palette defaults are not treated as hardware requirements. DMG snapshots
omit CGB-only palette storage and the nonexistent second VRAM bank; only real
DMG memory and attempted CPU writes are tested.

No emulator production implementation, compiler algorithm, game-facing API or
speed-switch policy changes. These tests do not establish physical speed-switch
timing, pending-interrupt hazards, general PPU timing accuracy or boot-ROM
execution. The separate SDK game tests retain real boot and rendering coverage.

The ledger becomes180/565 complete dispositions:179 native behavioral
replacements and one obsolete private-parser DELETE decision;385 remain
outstanding. No legacy definition is physically removed yet.

Verification: the final26-case selection passed26/26 in117.293s, exit0;
394 cases were outside the explicit filter. It covers the new HAL cases,
speed switching, real tile-sheet loading, full CGB map transfers, basic CIL
packets, canonical behavior and CLI entry selection. Completed log:
`/tmp/koh-cgb-hal-mode-final.log`. Rustfmt and CSharpier checks pass. No
production changes were required. This is local Linux evidence, not complete
game/physical-hardware/cross-platform acceptance.

Workspace/all-features nextest discovery lists1592 cases, including all32
added by the current four-commit batch; none of those32 is ignored. Discovery
does not mean1592 tests were executed locally. The discovery build retains the
existing unused `cubes` test-helper warning.
