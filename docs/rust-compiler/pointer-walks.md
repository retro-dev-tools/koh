# Loop-carried pointer behavior

All five `Backends/Sm83LoopPointerPhiResidencyTests.cs` methods now have native
dispositions for19 lexical assertion sites (21 after the original two-row
selector parameter expands). Two PORT decisions retain observable walks; three
REPLACE decisions remove private register-pair, fusion and edge-sync dictionaries.
Each incoming control-flow edge must supply its own pointer, and the backedge
must advance that pointer correctly. No particular HL/DE assignment is required.

## Independent execution

Three real C# methods extend `tests/fixtures/RewriteContracts`:

- `WalkChoice` selects C100 or C200 from a runtime parameter, then follows one
  shared load/store/post-increment loop. Each loaded byte is written to C300.
- `WalkLinear` retains the original C100 single-buffer walk with bytes1..20.
- `WalkStore` selects a buffer and writes AA on each iteration, retaining the
  other buffer's distinct data.

The fixture seeds both buffers itself through ordinary compiled CIL. The Rust
oracle independently declares every seed write, every selected read/write,
counter exhaustion, final pointer and returned data. Exact physical traces
reject selecting the wrong predecessor even if a final value happens to match.
The selected buffer starts with10/40 respectively; original four-byte walks must
end with13/43. The linear eight-byte walk must end with8; the20-byte store walk
must write exactly20 AA bytes. Counts0/4/8/20 run for both selector values.

Runtime and bound arguments, before/after optimization, Debug and Release give
**192 added ROM executions**. The runtime variant compiles the same unchanged
function IR under both selector values; it does not fold the branch in advance.
Full64-bit results include the final16-bit pointer, last byte or both selected/
unselected prefixes, and the exhausted counter. Canonical ELF and the Rust linker
feed the existing C# emulator. Programs verify before generation; full returns,
restored SP and reserved guards are also checked.

The existing test-only observer now covers C020–C3FF with a128-event cap because
seeding and the longest walk require82 events. Exact equality still checks the
entire trace; overflowing the cap cannot match any expected trace. This does not
relax assertions or introduce emulator/compiler algorithms. There is no new
production representation, allocator context or manually injected legacy frame.

## Source dispositions

| Source method | Sites | Decision | Native behavior |
|---|---:|---|---|
| `MultiEntry_BothArmsGetTheirOwnInitSync` | 6 | REPLACE | `WalkChoice` must use each arm's own base and access the exact selected bytes; no edge-sync dictionary is copied. |
| `MultiEntry_EachArmWalksItsOwnBuffer_RunsCorrectlyOnHardware` | 2 | PORT | Both selectors at count4 produce13/43 with exact C100/C200 read sequences and final C104/C204 pointers. |
| `LoadWalk_PhiGetsRegisterPair_AndDerefIsFused` | 8 | REPLACE | `WalkLinear` verifies all loaded bytes, output writes and pointer increments; slot/register placement is private. |
| `StoreWalk_PhiGetsRegisterPair_PrefersHl` | 2 | REPLACE | `WalkStore` includes20 exact AA writes with unchanged alternate prefix and correct post-loop pointer. |
| `LoadWalk_RunsCorrectlyOnHardware` | 1 | PORT | The original eight-byte1..8 walk ends with8; every intermediate output byte and final C108 pointer are also asserted. |

All are nextest-discoverable through the two existing Debug/Release rstest cases.
The complete rewrite fixture now executes2048 ROMs (1536 original,320 indexed,
192 pointer walks). This establishes semantics and live-value preservation, not
pointer-loop timing or completion of separate copy/render budget requirements.

## Verification

The first build exposed two untyped integer literals in the new Rust oracle;
declaring their byte types fixed that test compilation error. Execution then
passed2/2 in18.441s, exit0 (`/tmp/koh-pointer-walk-execution.log`). No production
code was changed to obtain that result. The combined final compiler-library/catalog/ISA/rewrite/copy run passed257/257
in51.763s, exit0,691 explicitly unselected (`/tmp/koh-pointer-walk-final.log`).
It includes all2048 compiled rewrite ROMs and existing copy extent/overlap/budget
cases. Rustfmt, CSharpier and diff checks passed. Current-head hosted acceptance
and remaining compiler/runtime/loop-budget work are still open.

The copy-runtime audit was also reconciled: all ten `MemRuntimeTests` methods
already had central native dispositions, despite stale backend-audit text.
Fresh existing extent/overlap/budget coverage passed12/12 in14.825s, exit0,
506 explicitly unselected (`/tmp/koh-copy-disposition-refresh.log`). Those cases
are not counted again in the completion ledger. Allocator layout and historical
method names are not specifications; current ordered-copy and unchanged timing
limits remain independently checked.
