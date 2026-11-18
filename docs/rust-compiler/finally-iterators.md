# Structured cleanup and ordinary Roslyn iterators

The compiler lowers structured normal try/finally exits and ordinary Roslyn yield
state machines. The three original CilIteratorTests methods now have native
replacements. This does not implement catchable managed exceptions.

## Contract and ABI

Small/fat EH tables and chained sections are read with bounds, clause/section
limits and explicit diagnostics. CIL instruction boundaries, original local
indices, region entry and control transfers are checked before normalization.
Normal branches cannot escape protected regions or enter handlers. Leave can
enter the first instruction of a disjoint protected block. Both leave and
endfinally discard the evaluation stack; endfinally does not require it to have
been empty. These follow ECMA-335 I.12.4.2.8, II.25.4.5–6 and III.3.35/3.46:
[standard](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf).
The positive raw-CIL cases protect these rules independently of Roslyn's usual
empty-stack output. A provisional empty-stack rejection was corrected against
the standard before publication.

Each finally has a private int32 continuation local in the ordinary per-call
frame. A leave records all pending continuations and enters the innermost
handler; endfinally dispatches to the next handler or original destination.
Shared handler code avoids duplicating cleanup for each exit. Per-call locals
preserve pending returns across nested cleanup and recursion; no static frame,
global continuation pointer or new calling convention is introduced. Original
maxstack/local validation is preserved, including maxstack0 methods whose
internal dispatch needs a temporary value. Resource/frame/bank limits diagnose.

Normalization maps compiler-generated operations to original leave/endfinally
IL offsets. Typed IR, optimization, SM83 emission and ELF/KDBG use those source
offsets. It does not fabricate PDB positions beyond the managed method body.

Catch, filter, fault and exceptional unwinding remain unsupported and diagnose
explicitly. Target runtime traps remain fatal and do not unwind cleanup. Shared
or split nonstructured EH layouts also diagnose as unsupported, rather than
being assumed malformed. Partially overlapping ranges and invalid transfers
diagnose invalid input. A yield body with its own try/finally can introduce a
Roslyn fault clause and is still outside this contract; consumer cleanup and
observable Dispose on normal completion/early break are tested.

## Iterator dependencies and corrected bugs

Generated iterator classes lower as ordinary reachable CIL, including exact
generic interface dispatch. No generated type/field names are recognized.
Two existing bugs blocked this path: System.IDisposable was incorrectly treated
as an unknown primitive, and a nongeneric dispatch signature path discarded the
assembly scope of a returned closed generic interface. System named types now
retain their ordinary class/value identity; dispatch uses the same scoped
method typing for generic and nongeneric methods. Two redundant signature
conversion functions were deleted.

System.Environment.CurrentManagedThreadId is an explicit native runtime contract:
Koh has one logical managed thread, identity1. Hardware interrupts do not create
managed threads. The getter must resolve to the trusted framework definition
with the exact static int32/no-argument signature and ordinary call opcode.
This supports Roslyn's enumerator reuse check without interpreting native BCL
code, using an OS thread ID, rewriting generated fields or claiming managed
threading support. Other native-only methods still reject. Framework metadata
is supplied explicitly; the SDK test uses its standard reference assemblies.

## Source test disposition

Source: tests/Compiler/Koh.Compiler.Tests/Frontends/CilIteratorTests.cs. Three
methods, five parameter-expanded cases, seven lexical assertion sites/thirteen
expanded assertions. Expectations are public language/MMIO behavior: sum0..4 is10,
two sums0..3 total12, and both build configurations must produce those results.
The old compiler's concrete-type tracking and IR classes are not contracts.

| Source method | Decision | Native requirement and location |
| --- | --- | --- |
| ForeachOverIterator_VerifiesCleanAndComputesExpectedValue | PORT | driver::reference_contracts::roslyn_iterator_state_machines_execute, Sum: verified native pipeline, actual BGP write/read10 |
| ForeachOverIterator_DebugAndReleaseProduceIdenticalObservableState | REPLACE | Both configurations independently require BGP10; equality alone could accept two wrong results |
| TwoIndependentForeachLoops_VerifiesCleanAndComputesExpectedValue | PORT | Same Rust case, Twice: actual BGP write/read12 |

All replacements are discoverable by cargo-nextest. No compiler coverage is
deferred and no legacy definitions are physically deleted in this increment.

## Execution and debug evidence

Four Debug/Release × DMG/CGB iterator cases execute44 ROMs: original sums,
repeated/nested/empty enumeration, early break, a generic iterator, full signed
64-bit yielded values, thread identity, and observable disposal on normal/early
exit. The unused nongeneric boxing getter is not required for the typed consumer.
Four cleanup cases execute40 ROMs: normal/early/nested exits, break/continue,
cleanup within cleanup, recursion, saved returns, full64-bit returns and sequential
protected blocks. Exact trace packets check cleanup order and memory effects.
All objects roundtrip through canonical ELF and the Rust linker.

Four SDK cases run ordinary dotnet build with standard references, paths with
spaces, both configurations/hardware modes, ROM/ELF/KDBG output and emulator
result42. Four existing-debugger sessions boot the ROM, stop on a source line in
finally, disassemble real instructions and unwind to the source caller with a
live consumed stack frame. Portable PDB matching and original IL offsets are
checked, including newly generated continuation operations.

The first cleanup run failed4/4 at the blanket section gate. Later probes exposed
the named-type, native getter and scoped-signature bugs above; they were fixed
in shared code. A broad651-case checkpoint passed649 and failed two obsolete
expectations of the old generic 'sections' diagnostic. Those now require the
precise unsupported-catch diagnostic. New shared/split layout tests exposed a
missing validation distinction and were corrected in the normalizer.

Final selection:315/315 passed in62.156s, exit0,797 explicitly unselected
(`/tmp/koh-finally-disposal-final.log`). This includes the corrected diagnostic
cases and all new parser, control-flow, ROM, SDK and debugger checks. The broader
checkpoint covers existing dispatch/delegate/generic/PDB/recursion/SM83 behavior;
it is not reported as a full green workspace run. Final hosted validation remains
outstanding. Fresh workspace discovery:2017 cases,1840 compiler cases, zero ignored.

```sh
cargo nextest run -p koh-compile --lib --test driver --test assembly --test references --test sm83 --test sdk --locked --no-fail-fast -E 'binary(koh_compile) | binary(assembly) | binary(references) | test(reference_contracts::) | test(roslyn_reachable_calls_and_recursion_execute) | test(iterators::) | test(debugger_breaks_in_finally)'
```
