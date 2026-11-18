# Framework behavior acceptance

All five methods in `Frontends/CilFrameworkTests.cs` (14 lexical assertion sites,
ten original Debug/Release argument rows) have native replacements in
`tests/driver/framework.rs` and the ordinary Roslyn `FrameworkContracts` fixture.
Five entry points run in Debug/Release × DMG/CGB:20 ROM executions.

| Source method | Native fixture and independently checked requirement | Decision |
| --- | --- | --- |
| `Rng_MatchesDesktopSequenceAndDerivedDraws` | `Random`: eight xorshift16 draws, reseed, modulo/range, zero recovery, mixing, chance endpoints and max0/no extra draw | REPLACE desktop parity with independent Rust arithmetic and contract properties |
| `Timer_CountsDownFiresOnceAndRestarts` | `TimerContract`: literal13-observation packet for idle,3-tick countdown, one firing tick, restart1, stop100 and start0 | PORT |
| `Assets_StaticStructHandles_LoadVramAndDrawMap` | `Assets`: original32 authored tile bytes at slot4,2/4/4/5 tile handle values,2x2 map dimensions and original four map cells | PORT |
| `Input_LatchedEdgesAndRepeatCadence` | `InputContract`: repeated same-frame queries, held/released Right, signed dpad direction and Left repeat delay3/interval2 | PORT |
| `GameBootAndEndFrame_TickClockAndLatchInput` | `GameContract`: boot clock0/nonzero RNG, clock3 after three frames, latched A | PORT |

The old RNG test executed the production C# runtime to manufacture its expected
draws. The new oracle uses independent16-bit arithmetic for the documented7/9/8
xorshift, with a separate exhaustive model check visiting all65535 nonzero states.
That model check is not a claim that65535 draws were executed in a ROM. Zero-seed
recovery remains a nonzero property, intentionally not a frozen private fallback
constant. Other deterministic bytes and stream positions are checked exactly.
Timer and input expectations are literal event sequences, not calls back into
the production runtime or assumptions about its private fields.

Test-only emulator modes press Right before input execution, release it at the
fixture's SCX1 marker and press Left at SCX2. They require both phases before
completion. The game-loop mode holds A from the start. These use the existing
emulator input API; production emulator behavior is unchanged. The initial marker
is explicitly initialized, independent of emulator constructor defaults.

Packets at DF00 expose each observation to Rust rather than hiding all failures
behind a C# success flag. Result42, SP, reserved-memory guards and poisoned packet
prefix/suffix remain checked. For assets and game frames, every available VRAM
byte, unchanged CGB attribute bank, OAM, LCDC91 and palette controls are checked.
The existing mode3/DMA guards remain active. Static struct handles and referenced
runtime calls pass through actual native compilation, canonical ELF round-trip,
Rust linking and existing C# emulator execution.

No production compiler/emulator/API implementation changes are needed. The test
contract preserves useful game behavior, not historical compiler class shapes,
RNG implementation identity or an in-process desktop oracle. Legacy compiler
and test deletion remains a separate final acceptance gate.

Initial host build failed because the new input modes lacked the Joypad namespace
import; the import was corrected. Focused execution then passed5/5 in9.818s.
Final compiler-library/framework/font/map regressions239/239 passed38.553s,
exit0,516 explicitly unselected (`/tmp/koh-framework-final.log`). Stable fmt,
CSharpier and diff checks passed.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --locked \
  -E 'binary(koh_compile) | test(framework::) | test(font_text::) | test(bg_win::) | test(lazy_maps_preserve) | test(full_cgb_map_transfers)'
```
