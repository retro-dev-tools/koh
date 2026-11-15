# Native byte fill

`Mem.Fill(byte* destination, byte value, ushort count)` retains its authoring
signature and exact ascending byte-write behavior. Its ROM implementation is
now the address-less `memfill` runtime contract. The ordinary C# body remains a
simple byte loop for desktop execution; it is not a compiler implementation.
The Rust frontend validates the metadata signature and lowers the call to typed
`MemoryFill`. The Rust backend emits its complete implementation.

## Semantics and boundaries

- The address is Koh's native16 pointer. Value and count use int32 evaluation
  stack storage, truncated respectively to8 and16 bits at this call boundary.
  This follows [ECMA-335, I.12.1.2](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf):
  narrow method parameters truncate like other narrow stores. The backend tests
  intentionally supply wider values rather than relying on Roslyn conversions.
- Count0 performs no destination read or write. Otherwise exactly count writes
  occur, one byte per ascending bus address. Native addresses wrap modulo65536.
  The operation neither reads the destination nor selects a VRAM/WRAM bank.
- This is unsafe raw memory, not an array operation. The caller owns capacity,
  selected bank, PPU access timing and interrupt/reentrancy discipline. It adds
  no implicit VBlank wait, managed bound, allocation or LCD-off flash.
- Known constant fill ranges spanning SVBK are rejected when reachable from a
  pinned method, including transitive calls. Constant integer conversions are
  evaluated with their source/target widths using a linear dependency queue.
  Dynamic raw addresses/counts retain the existing caller-owned bank contract;
  this check does not establish general alias or bank-provenance safety.
- Only address-less, static, nongeneric `void(byte*, byte, ushort)` is valid.
  Other return, pointer, scalar or address shapes are rejected at the calling
  method/IL offset. This does not add general CIL `initblk` support.

The effect has no result. IR verification checks all three operand types and
use/definition rules. Optimization preserves the write and remaps every operand;
it is not a pure expression or an aliasing exemption. Codegen captures the
count, address and byte before writing, keeps count in BC, destination in DE and
byte in H, and uses the existing four-byte temporary stack guard. The taken
loop costs52 SM83 T-cycles per byte; the last branch costs four fewer cycles.

## Why replace the old loop

The real CGB cube profile attributed8733956T before its early capture to the
old `Mem.Fill` body (`/tmp/koh-cube-profile-first.log`). The native compiler had
to spill the C# loop's values; inheriting source tricks designed around the old
C# compiler's byte-register allocator did not provide a useful runtime contract.
An aligned word-loop attempt still missed the independently chosen five-frame
execution budget. The direct Rust operation is smaller and preserves byte bus
ordering without alignment restrictions or source-level unrolling.

The test ROM fills1142 bytes in15 calls, including zero length, with gaps and
endpoint guards. Measured total CPU execution, including harness/call overhead:

| Native compiler with runtime implementation | Debug | Release |
| --- | ---: | ---: |
| Previous C# byte-loop body |1434496T|1276400T|
| Investigated aligned word loop |814812T|722476T|
| Native `MemoryFill` |91492T|91492T|

These are ROM execution measurements, not a comparison of compiler speed,
binary size or peak host memory. Both DMG/CGB and all four pointer alignments
have the same native result. Reds: `/tmp/koh-fill-cost-red.log` and
`/tmp/koh-fill-word.log`; green: `/tmp/koh-fill-native.log`4/4 in26.807s.

## Evidence and deliberate incompatibility

`driver::memory_fill_exact_extents_alignment_and_cost` checks entire poisoned
WRAM banks, values0/80/FF and others, zero/tiny/odd/255/256/257/300 lengths,
four alignments, no overrun at DFFF and unchanged bank2. A separate bus trace
requires exactly `FFFE:A7,FFFF:A7,0000:A7,0001:A7`, with no extra write at0002.
Objects round-trip through canonical ELF before linking. Five malformed
metadata shapes are checked; instance/generic/calling-convention mutations are
not claimed as new coverage here.

The pinned-range test first accepted the illegal SVBK-spanning fill
(`/tmp/koh-fill-pinned-red.log`). The corrected verifier passes direct and
transitive rejection cases plus safe zero-length/nonbanking pinned fills.
`/tmp/koh-fill-all-contracts.log` passes20/20 in68.415s:10 driver cases,
six typed-IR cases and four backend truncation/large-frame/stack cases.
`/tmp/koh-fill-wrap.log` passes4/4 in34.553s with the additional bus trace.
The two desktop cases independently execute112 guarded fills under .NET.
Additional ROMs check a partial fill through a pointer to an int local and a
zero-length local alias; subsequent loads must observe12A7A7A7 and12345678,
respectively, without changing either poisoned heap bank. Four additional IR
cases distinguish signed/unsigned narrow-count conversions, zero truncation
and truncated native addresses before classifying SVBK spans.
After formatting, `/tmp/koh-fill-final-posthook.log` passes24/24 in125.887s,
exit0, including those local-alias and conversion cases;361 unrelated cases
were filtered. The earlier conversion-test fixture lacked the required null
pin initialization; it was corrected without changing that verifier invariant.

The retiring C# compiler does not understand `memfill`. This is intentional:
the SDK already invokes Rust exclusively, and the full PR must delete that
compiler and its test project. No compatibility implementation is added to it.
Review explicitly checked and accepted that scope; it found no Rust arithmetic,
conversion-queue, stack or wrap defect.

The stronger real-cube run improves to9/10 test cases: six validator cases and
three game cases pass; DMG Debug still fails its early capture. Its profile
attributes49518400T to `Ppu.WaitVBlank`, whose one-scanline polling still needs
correction. `/tmp/koh-cube-native-fill.log` is exit100,59.485s; the unchanged
frame limits and geometry assertions remain required inside PR55.

The broader IR/backend/fill/tile/lazy-map/showcase/cube regression completed
309 cases:308 passed,1 failed,466.164s,exit100
(`/tmp/koh-fill-graphics-regression.log`).82 unrelated driver cases were
filtered. The sole failure remains the DMGDebug cube's early capture; no
arithmetic, IR, fill, tile, lazy-map or showcase case failed.
The post-hook production/SDK selection passed30/31 in273.776s,exit100
(`/tmp/koh-fill-postcommit-sdk.log`), with the same sole cube failure.
The actual SDK graphics dotnet build/run passed87.842s and reports zero mode3
writes; the extra local-alias/conversion cases were added afterward.
