# Local definite assignment

Koh accepts methods whose CLI `initlocals` flag is clear when each ordinary local
read is preceded by a full assignment on every incoming path. It no longer
rejects all such methods or silently gives their user locals entry zero stores.
The flag comes from the method header; no attribute-name special case is used.
Roslyn's [SkipLocalsInit attribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.skiplocalsinitattribute?view=net-10.0)
provides reproducible ordinary C# fixtures with that flag clear.

## IR and lowering contract

The shared typed IR verifier computes a forward must-assignment fixed point.
Entry starts empty; other reachable blocks start at the complete local set.
Predecessor states meet by intersection, and full local stores add their slot.
Only after convergence does ordered instruction validation check each load.
Assignments on backedges cannot justify a first-iteration unassigned read, and
block vector order does not grant assignment. Invalid indices and unassigned
loads diagnose with method identity and the load's IL offset.

Bitsets use one bit per declared slot, including parameters. Existing IR bounds
limit each state matrix to262144 words; fixed-point work is capped at16777216
word operations and reports an explicit resource diagnostic. Methods assigning
all locals in entry use an immediate result, with ordered entry validation still
checking reads before their first store. The common initialized-CIL case does
not need iterative analysis.

`initlocals` methods retain their explicit typed zero stores. Nonzeroed methods
retain parameter stores but omit user-local zero stores. Compiler-created value
construction temporaries retain their independent initialization contract.
Finally normalization preserves the original initialization flag; its private
continuation locals are assigned by normal leave transfers before dispatch.
No static frame, new calling convention or backend placeholder is introduced.

Address-taking still requires an already definitely assigned local. Initializing
an unassigned local through `initobj`, an `out` call or arbitrary alias writes
requires additional memory/lifetime analysis and remains unsupported. Pinned
locals retain their explicit entry-zero and pin-store rules; ordinary local
assignment does not weaken pin ownership. This is support for a defined subset
of nonzeroed CIL, not permission to read indeterminate frame bytes or a managed
memory-safety proof for arbitrary unsafe code. Stack buffer initialization keeps
its existing separate target contract.

## Behavioral coverage

Rust `lowering` cases cover straight stores, both diamond arms, loop-carried
assignment, unused locals, branch-dependent missing assignment, first-iteration
reads, entry read-before-write and slot boundaries0/63/64/127/255. Positive CFGs
also run with reversed non-entry block order, before and after optimization,
then reach SM83 emission. No test uses uninitialized memory as an expected value.

Four `driver::reference_contracts::roslyn_nonzeroed_locals_execute_after_definite_assignment`
cases build actual Debug/Release Roslyn assemblies and execute24 DMG/CGB ROMs:
diamond, loop, assigned-local alias mutation, finally, recursion and full64-bit
transport. Each returns42, restores SPD000 and preserves memory guards. Finally
and wide cases check their complete256-byte packet, including A5 cleanup and
123456780000002A little-endian transport; untouched bytes retain5A. Tests inspect
the actual method flags and absence of user-local entry stores.

The existing four SDK iterator/finally cases now also author SkipLocalsInit C#
through standard reference assemblies and native compiler/linker discovery,
with paths containing spaces, ROM mode flags, ELF/KDBG output and emulator42.
This strengthens those fixtures; it does not add source-disposition credit.

Initial focused red:9/9 failed at the old blanket gate, exit100,
`/tmp/koh-local-assignment-red.log`. The first expanded run passed772/773;
its one failure was an old Rust assertion that an unused nonzeroed local must
reject. That assertion now checks an actual unassigned read through a finally
region instead, retaining original maxstack/protected-entry checks.
Focused post-fix coverage passed38/38 in8.379s, exit0,
`/tmp/koh-local-assignment-rom.log`;949 explicitly unselected. This precedes the
additional slot-boundary cases and final expanded validation.

Final expanded validation passed1070/1070 in457.594s, exit0,551 explicitly
unselected (`/tmp/koh-local-assignment-final.log`): compiler library, IR, lowering,
SM83 and selected real arithmetic/memory/interface/interrupt/reference/SDK cases.
The four SDK cases passed51.003/50.948/2.615/2.596s; cold SDK validation rebuilt
release native tools separately for Debug and Release. These timings include
build/test infrastructure and are not compiler performance measurements.
CSharpier checked the fixture with exit0. The final native CLI build exited0
(`/tmp/koh-local-assignment-cli-build.log`).

## Known conservative boundary

The verifier does not correlate finally-continuation selectors with local
assignment facts. With initlocals clear, a valid method that returns early on one
try path and assigns a subsequently read local only on the other path can reject
after shared-handler normalization. For example:

```csharp
[System.Runtime.CompilerServices.SkipLocalsInit]
public static unsafe class MixedRules
{
    private static int Mixed(int n)
    {
        int value;
        try { if (n == 0) return 1; value = 42; }
        finally { *(byte*)0xDF00 = 0xA5; }
        return value;
    }
    public static int Zero() => Mixed(0);
    public static int One() => Mixed(1);
}
```

The ordinary Release Roslyn build succeeded with zero warnings/errors
(`/tmp/koh-local-assignment-mixed-build.log`). Both native entries reject with
`Local load lacks definite assignment` at Mixed IL_0019, exit1, no object
(`/tmp/koh-local-assignment-probes.json`). This is an unproved valid CIL shape,
not malformed metadata or a claim that C# leaves `value` unassigned. Supporting
it requires retaining continuation-sensitive facts or a CFG that preserves those
path distinctions. Extra zero stores are not used to hide the limitation.
Ordinary initialized-CIL mixed exits retain their existing supported behavior.

The original iterator-based LINQ probe now advances through the former method
initialization gate and reaches Where IL_0020:
`Generic declaring type parameter !0 requires instantiated storage`, exit1,
no object (same JSON). Array/interface conversion and recursive-constraint gaps
remain separate. No generic framework LINQ success is inferred from this advance.

All five LINQ source methods remain outstanding; no pipeline execution or
recursive generic-constraint fix is implied. Source ledger remains509/565,
56 outstanding. Final migration deletion, product acceptance, performance and
current-head cross-platform validation remain required.

Fresh nextest discovery reports2047 workspace cases,1870 compiler cases and zero
ignored (`/tmp/koh-local-assignment-discovery.json`, exit0). The existing cubes
dead-code warning remains. Discovery is not an unfiltered execution result.
