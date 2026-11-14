# PR #55 working handoff

Branch feat/rust-cil-compiler, isolated from main checkout modified screenshot.
Baseline integrated linker 9cd1472. Existing PR #55 stays draft; never merge.
C# compiler remains production. Full attachment migration goal stays active.

## Implemented

All 565 legacy compiler methods have source dispositions, not complete migration.
None is retired/replaced yet. Native readers/reference binding, typed SSA/verifier,
integer lowering/emission and static managed calls/reachability are implemented.
Read calls.md, sm83-abi.md and progress.md for exact scope/limits/evidence.
Calls have explicit signatures validated against actual IR definitions. A finite
work queue lowers each reached definition once; generic/instance/virtual shapes
are still incomplete. No whole BCL or unreachable unsupported bodies are included.
Dynamic SP frames preserve caller values and recursion. Caller capacity checks
precede buffer writes/CALL; callee checks frame before writes. Every ELF object
has a canonical WRAM0 UNION stack reservation, preventing linked global overlap.

Initializer omission was reproduced in real C#: both declaring-type .cctor and
<Module>..cctor must not be ignored. They now produce explicit diagnostics for
reached methods/assemblies until real static storage/initialization lowering lands.
This is pending required implementation, not retirement of those capabilities.
A controlled removal of caller guard produced CORRUPT sentinels despite a callee
guard. The production guard was restored before all final runs.

Root owns all files. Read-only reviews are complete, including module-init fix.
No delegated writers/builds remain. No native CLI/startup/SDK switch, graphics,
PDB/KDBG acceptance or legacy compiler deletion yet. Library entry is
compile::reachable(&Resolver, Definition); output IR is program-verified.

## Evidence

Completed workspace nextest: 383/383 passed, 0 skipped, exit 0; 207 compiler cases.
Log /tmp/koh-calls-nextest.log. Suite executes 154 ROM scenarios through ELF
serialization/readback and Rust linker in unchanged C# emulator: 141 expected
normal returns, 13 traps, guard sentinels below C100 preserved. New cases include
12 real C# scenarios across Debug/Release (transitive calls, mixed widths, void,
recursion/mutual recursion, deep-stack trap), plus caller guard/live value cases.
Exact Main reachability is four methods; dead unsupported code stays absent.
Focused default-threaded tests: 25 IR + 16 lowering + 14 backend passed, exit 0
(/tmp/koh-calls-focused.log). Separate workspace doctests exit 0, no cases
(/tmp/koh-calls-doc.log); stable fmt check exit 0 (/tmp/koh-calls-fmt-check.log).
Legacy baseline 814/814 remains historical. All 14 hosted checks passed for
preceding backend head 6e01f88. Check new call head separately after push.

## Next

Implement native entry/CLI/module emission, metadata hardware contracts, memory,
static globals and real initialization. Then remaining language/runtime, generic,
objects/arrays, optimization, banking/IRQ, PDB/debug, SDK, sample, test audit/
migration, legacy deletion, performance/platform and bounded Docker work in this
single PR. Do not redefine success as integer/call slice. Rough progress 20%;
100% means every original requirement verified. Keep old compiler until replacement
acceptance, then delete it entirely. GameBoy API is preserved by default.

## Verification

Serial builds per output directory. KOH_DOTNET selects .NET SDK 10.0.115; read
completed logs/direct exit codes. Use stable rustfmt. Reuse koh-objects ELF and
Rust linker; execute C# emulator. Preserve sample assets/main screenshot. Commit
and push logical increments to this draft PR; do not open another or merge it.
