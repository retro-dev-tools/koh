# Referenced runtime and string-table contracts

Seven source methods now have dedicated native replacements in
`tests/driver/reference_contracts.rs`, with ordinary Roslyn fixture classes in
`FrameworkContracts/Program.cs`. Twenty-eight ROM executions cover seven entry
points in Debug/Release × DMG/CGB, through canonical ELF write/read and Rust linking.

| Source method | Independent native acceptance | Decision |
| --- | --- | --- |
| `CilMdArrayTests.StringArrays_ReferenceElementsAndLengths` | Original HELLO/WORLD!/GB table, parameter count/first-character reads, original AB/CDE runtime array and lengths; add referent mutation through an alias | PORT |
| `CilReferenceTests.HalAndMemMethods_LowerOnDemandAndProduceExpectedState` | Original stackalloc buffers, referenced copy1..8, fill7A and BGP1B, observed as a complete packet | PORT |
| `CilReferenceTests.HalAndMemMethods_DebugAndReleaseProduceIdenticalObservableState` | Both builds independently match every expected packet byte and finish | REPLACE equality-only oracle |
| `CilReferenceTests.SingleSetTile_LowersOnDemandAndWritesTheRightCell` | Original cell(5,3)=9 and all outside cells unchanged after explicit initialization | PORT |
| `CilReferenceTests.TilemapClear_TransitivelyLowersSetTileAndClearsEveryCell` | All1024 target cells7, other map/tile/attribute memory untouched; actual referenced library runs | REPLACE required private callee shape |
| `CilReferenceTests.TilemapClear_DebugAndReleaseProduceIdenticalObservableState` | Both builds independently match the complete physical VRAM image | REPLACE equality-only oracle |
| `CilReferenceTests.UncalledHalMethods_AreNeverLoweredIntoTheModule` | Uncalled unsupported Console body does not require its missing assembly; it and an unused CGB helper have no emitted method symbol | REPLACE private IR method-name lists |

The six reference methods contain21 lexical assertion sites; the string-table
method contains two. These source-level counts do not count all observations
inside the original C# success predicates. New Rust checks expose those values
directly instead of comparing only an aggregate verdict.

Three extra string-table entries require the documented null/bounds traps and
unchanged reserved-memory guards: a zero-initialized reference element must be
null, and index3/-1 must not read outside the three-element table. String-array
mutation must change the readonly field's referent; readonly does not freeze an
array's contents or turn references into private ROM headers.

The direct tile fixture explicitly turns the LCD off and initializes its target
map before setting the cell. The old fixture relied on an implicit zero VRAM and
PPU timing; neither emulator constructor defaults nor a lucky write window is a
hardware contract. Raw target-map clearing leaves the other poisoned map intact.
Referenced copy/fill methods may be intrinsic operations rather than emitted
function bodies; the old requirement that a particular private SetTile function
appear inside Clear is replaced by complete observable results and reachability.

The four raw-arena source methods were already counted complete in
[raw-arena.md](raw-arena.md); their stale frontend markers are reconciled without
extra credit. The separate `CallIntoBcl_IsADiagnosticNeverLoweredOrMiscompiled`
case is now replaced separately by [retained-IL method semantics](retained-il.md):
resolvable managed Math.Max executes, while native-only Math.Sqrt diagnoses.
This does not promise general BCL compatibility.

Initial harness runs exposed two test assumptions, corrected before acceptance:
the fixture copier uses Program.cs only, so the new classes were consolidated
there; null faults use code04 in the native ABI, not the test's mistaken01.
No production compiler/emulator/API changes were needed. Final expanded
compiler-library/reference/framework/UTF-16 validation passed243/243 in29.177s,
exit0,530 explicitly unselected (`/tmp/koh-reference-contracts-final.log`).

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --test references --locked \
  -E 'binary(koh_compile) | binary(references) | test(reference_contracts::) | test(framework::) | test(strings_preserve_utf16_calls)'
```
