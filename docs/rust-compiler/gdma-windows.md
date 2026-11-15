# Complete GDMA windows

## Requirement and API decision

CGB general-purpose DMA must finish while VRAM is accessible. A transfer's
start mode alone is insufficient. Pan Docs specifies32 dots per16-byte block
at either CPU speed; VBlank lasts4560 dots. See
[CGB transfer timings](https://github.com/gbdev/pandocs/blob/master/src/CGB_Registers.md#transfer-timings).

The old `Cgb.CopyToVram` required callers to wait before calling. Its register
setup and ordinary CIL call overhead could then consume the remaining window.
Full-frame sample traces found1920-byte transfers starting at LY0dot410 in
Release and LY1dot132 in Debug. Cube images and the CPU-only mode3 guard had
passed, demonstrating why neither was sufficient DMA evidence.

Moving setup before an internal wait fixed those sample calls, but did not
establish the public2048-byte maximum. A direct Roslyn fixture then failed in
both configurations: Debug started at LY146dot248, Release at LY145dot292,
each requesting4096 transfer dots. These failures were reported by the
existing emulator host's independent GDMA timing checks
(`/tmp/koh-gdma-max-red.log`,2/2 failed,26.754s, exit100).

The operation now owns synchronization: LCD-on requests use at most1024 bytes
per VBlank, with addresses and control prepared before each wait. LCD-off
requests retain the hardware-sized transfer. The public raw preconditions
remain aligned source/destination, a multiple-of16 count no greater than2048,
valid source bank and sufficient destination capacity; zero is a no-op and
DMG performs no transfer. This unit does not add general DMA range validation.
The duplicate internal wait helper is deleted and TileSet uses the same operation.

Alternatives were keeping caller-owned timing, which the executable failures
disprove, or relying on a larger single burst, which has insufficient margin
after ordinary CIL polling. Smaller synchronized bursts preserve useful copy
capacity without adding a compiler-specific hardware primitive.

This deliberately changes presentation latency. The full-frame sample's two
1920-byte calls now span four VBlanks, allowing partial-page seams while keeping
its128x120 viewport and CGB LCD continuously enabled. Callers no longer add
their own wait immediately before this operation. A blanket compatibility rule
would preserve an unsafe API convention; the hardware requirement takes priority.

## Native evidence and limits

`driver::direct_gdma_maximum_preserves_every_byte_at_varied_phases` compiles
the actual library in Debug/Release and executes canonical ELF-linked ROMs in
the existing emulator. Eight delayed entry phases exercise2048-byte requests.
Additional zero,16,1024,1040,1920 and LCD-off2048 cases check exact VRAM including
untouched tails, distinct source halves, the other VRAM bank, WRAM bank2 and balanced return state.
DMG cases require unchanged VRAM. No managed compiler is invoked.

The real `cubes::cube_boots_and_animates` cases additionally capture each LCD-on
FF55 start from the external emulator. Rust checks mode, LY, dot and complete
transfer cost, reserving eight extra dots. Eleven parameterized boundary tests
cover1920 and2048 budgets, invalid lines/dots, and accidental HBlank mode.
The viewport-aware image validator retains palette, alpha, centering, area,
connectivity and two-capture animation requirements.

The first corrected focused selection passed15/15 in53.813s, exit0:
`/tmp/koh-gdma-chunks-green.log`. Broader validation completed44passed/1failed
in239.891s, exit100 (`/tmp/koh-gdma-regression.log`,98 unrelated cases filtered).
The failure is retained as required acceptance. Full-frame DMG
Debug remains a required animation failure at450/750 frames. Its only observed
frame changes were83 and437; profiling attributes6232152T before first capture
to `Mem.Copy`. No frame budget is increased and the failing case is retained.
This evidence is emulator execution, not physical hardware validation or a
whole-migration completion claim.

The subsequent [native byte-copy correction](memory-copy.md) resolves that DMG
animation failure at the same450/750 budgets. All8 double-buffered/full-frame
cases pass in the924-case workspace run; the failures above describe this
earlier DMA checkpoint, not the latest result.

Final distinct-source-half fixture recheck:4/4 passed in29.211s, exit0,
`/tmp/koh-gdma-patterns-final.log`;108 unrelated tests filtered. These four
Debug/Release × DMG/CGB cases execute56 ROM probes. Reproduce all affected
paths, including the required animation failure, with:

```sh
cargo nextest run -p koh-compile --test driver --test cubes --locked -j1 --no-fail-fast \
  -E 'binary(cubes) | test(direct_gdma_maximum) | test(actual_tileset_load) | test(full_cgb_map_transfers) | test(full_graphics_showcase)'
```
