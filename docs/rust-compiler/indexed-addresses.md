# Indexed pointer preservation

All five `Backends/Sm83GepFusionTests.cs` methods now have native replacements
for seven original assertion sites: four PORT decisions for visible address/data
behavior, one REPLACE for the private `FusedGep` eligibility set. The native
compiler does not copy that allocation context or its fusion rule.

## Required behavior

Computing an address from a runtime base and displacement must preserve both
operands until the selected computation occurs. A subsequent load/store, a second
use, intervening arithmetic or a call must not corrupt it. This is the CIL/C#
pointer and live-value contract, not a requirement for a particular slot, register,
instruction schedule or immediate-use optimization.

Five real C# methods extend `tests/fixtures/RewriteContracts`. The existing Rust
test `sm83::machine_rewrites_preserve_carry_control_and_observed_memory` lowers
their actual Debug/Release CIL and verifies programs before machine generation.
Each of eight base/index pairs executes with runtime arguments and bound constants,
before/after production optimization: **320 added ROM executions**. The complete
fixture now executes1856 ROMs, including the preceding1536 rewrite cases.

Independent Rust arithmetic computes the16-bit address from the base plus signed
16-bit index. Inputs retain every original physical address/value example:
C050+3→77, C050+4→200, C060+1 stores9/returns10, C070+2 changes41→42.
Additional inputs cross a256-byte page, use -1/-16 displacements, and use index
0101 to detect accidental8-bit truncation. All resulting addresses are in the
explicitly observed WRAM window C020–C1FF.

Exact watched read/write sequences include write values; returning the expected
value from the wrong address cannot pass. The delayed case retains its pointer
across a real helper call and combines the live32-bit arithmetic result with the
loaded byte into a64-bit result. The reuse case must write1, then2, then read2
at the same computed address. Full return buffers, restored stack pointer and
reserved guards are checked by the existing native ABI harness.

Only the existing test adapter's watched WRAM window is extended. No production
emulator, compiler, linker or game API changes. The harness reserves a64-event
trace; these indexed cases require at most four watched events, and equality
checks the entire captured trace. No writes to tracked assets or raw parameter
injection into legacy static frame slots are used.

## Source disposition

| Source method | Sites | Decision | Native method and independently checked result |
|---|---:|---|---|
| `SingleUseGep_ImmediatelyStored_ComputesCorrectAddress` | 1 | PORT | `IndexedStore`: exact physical write77 and subsequent read at base+index. |
| `SingleUseGep_ImmediatelyLoaded_ComputesCorrectAddress` | 1 | PORT | `IndexedLoad`: known200 initialization and exact physical indexed read, unsigned result preserved. |
| `TwoUseGep_StillComputesBothUsesCorrectly` | 2 | PORT | `IndexedTwice`: write9/read9 at the same address, result10. |
| `DeferredSingleUseGep_AcrossInterveningInstructions_StillComputesCorrectly` | 1 | PORT | `IndexedDelayed`: read41, retain pointer across arithmetic/helper call, write/read42; high32 result also verifies intervening live data. |
| `FusedGep_IsInternalContextSet_ForImmediatelyConsumedGepOnly` | 2 | REPLACE | `IndexedReuse`: both address computations and two uses preserve exact write1/write2/read2 behavior before/after optimization. No private admission set is reintroduced. |

All five are discoverable through the two existing Debug/Release rstest cases.
The private eligibility assertions did not specify a public size/cycle budget;
none is invented to preserve that optimization's name. Real copy/render budget
tests remain separate acceptance requirements. This batch claims address and
liveness correctness, not a target-speed improvement or pointer-loop completion.

## Validation

Initial expanded rewrite cases passed2/2 in15.094s, exit0,207 explicitly unselected
(`/tmp/koh-indexed-address-initial.log`). Final compiler-library/catalog/ISA/
rewrite selection passed245/245 in35.833s, exit0,703 explicitly unselected
(`/tmp/koh-indexed-address-final.log`). It includes all1856 compiled ROMs and the
independent instruction-effect matrix. Rustfmt, CSharpier and diff checks passed.
No tests are ignored or deferred and no concurrency/timeout/assertion is relaxed.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --test opcodes --test sm83 --locked --no-fail-fast \
  -E 'binary(koh_compile) | binary(opcodes) | test(sm83_disassembly) | test(sm83_machine_effects) | test(machine_rewrites_preserve)'
```

Full current-head hosted validation, remaining source dispositions and the full
compiler migration remain open.
