# Roslyn byte arithmetic and measured cost

All six `Frontends/CilNarrowingEndToEndTests.cs` methods have native replacements
in `tests/driver/narrowing.rs`. The source has11 assertion sites, nine expanded
cases and16 expanded assertions. Four rstest configurations build actual
Debug/Release Roslyn DLLs and run DMG/CGB ROMs through native ELF and the Rust
linker. No compiler algorithm or optimization switch is added for testing.

| Source method | Decision | Native replacement |
| --- | --- | --- |
| `ByteCompare_UnsignedOrderingIsCorrectThroughNarrowing` | PORT | Counting loops produce bytes200/50 dynamically; actual BGP must be0 after comparison. |
| `ByteCompare_DebugAndReleaseAgree` | PORT | Both configurations independently produce0, rather than merely agreeing with each other. |
| `ByteCompare_NarrowPassActuallyFiredOnTheCompare` | REPLACE | Retain the discriminating high-bit numeric observation and native width/compare regression coverage; retire private I8 nodes and named-pass wiring. |
| `ByteAccumulateLoop_ProducesTheCorrectSum` | PORT | The original byte-counter loop adds3 fifty times and writes150 to actual BGP. |
| `ByteAccumulateLoop_DebugAndReleaseAgree` | PORT | Each configuration independently produces150; an additional200-iteration byte sum must wrap600 to88. |
| `ByteAccumulateLoop_NarrowingReducesRomSizeAndCycles_MeasuredDelta` | REPLACE | Strictly compare emitted initialized-section bytes and actual CPU T-cycles for equivalent byte and int32 accumulation kernels; assert equal150 outputs and strict reductions in both configurations/devices. |

## Independent semantics and benchmark boundary

CIL evaluates the byte additions and comparisons at int32 width. Converting to
byte retains the low eight bits. Consequently byte200 remains positive when
compared with byte50, and repeated byte addition agrees modulo256 with an int32
sum converted once at the end. The wide baseline uses the same byte loop counter,
step3 and50 iterations, but an int32 accumulator. Both kernels must produce150.
The wrap probe independently checks `(200*3) & 255 == 88`.

The previous test disabled a private C# IR demotion pass. Rust's range analysis
instead selects physical storage widths without changing the CIL int32 contract.
Adding a production optimization-off API solely to preserve that old switch is
unnecessary. The new comparison measures two mathematically equivalent source
kernels through the same production pipeline. It is explicitly **not** a
pass-enabled/pass-disabled causal measurement of one unchanged assembly.
Both strict size and cycle assertions remain; this is not a data-only replacement.

Size is the sum of initialized section data, excluding ELF serialization, debug
metadata and cartridge padding. Cycle measurements cover entry at PC0100 through
the native exit symbol, including common startup/return work; they are not
per-iteration hot-loop measurements or real boot-ROM timings. Both programs run
on the same emulator mode with the same initial conditions. Results and stack/
reserved-memory guards are checked before comparing cost.

Two independent canonical ELF objects calibrate the observer on each run. Each
executes JP0150, three16-bit immediate loads, one8-bit immediate load, one LDH
store and either one or six NOPs, stopping before HALT. Expected totals are76
and96 T-cycles from the [SM83 opcode table](https://github.com/gbdev/gb-opcodes/blob/master/Opcodes.json):
`16 + 3*12 + 8 + 12 + 4*N`. Registers, SP and BGP are checked too. This validates
the selected counter/window, not every instruction implemented by the emulator.
The four cases execute16 compiler-produced ROMs plus eight calibration ROMs.

## Measurements

The calibrated local run passed4/4 in10.544s, exit0,651 explicitly unselected:
`/tmp/koh-narrowing-contract-calibrated.log`. DMG and CGB totals agree:

| Configuration | Byte section bytes / T-cycles | Int32 section bytes / T-cycles | Reduction |
| --- | --- | --- | --- |
| Debug | 778 / 41472 | 844 / 56016 | 66 bytes / 14544 T-cycles |
| Release | 743 / 33724 | 809 / 48268 | 66 bytes / 14544 T-cycles |

The original dynamic compare produces0 in both modes; the byte/wide sums each
produce150 and the wrap probe88. These measurements establish the scoped cost
contract, not an overall performance win against the previous C# compiler.
The separately open190/740-dot loop ceilings are unchanged. Final neighboring
regression and current discovery are recorded in [progress](progress.md).

```sh
cargo nextest run -p koh-compile --test driver --test sm83 --locked \
  --no-fail-fast --success-output final \
  -E 'test(narrowing) | test(integer_conversion) | test(unsigned_comparison)'
```
