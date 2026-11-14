# PR #55 working handoff

Branch: feat/rust-cil-compiler, isolated from main checkout modified screenshot.
Baseline: integrated linker migration 9cd1472. Existing PR #55 remains draft.
The C# compiler remains production. Full attachment migration goal stays active.

## Implemented

All 565 legacy test methods have source dispositions, not completed migration.
None is fully retired/replaced yet. No compiler-owned coverage may be deferred.
Native metadata/body, CIL encoding/signature readers and full-identity reference
binding are committed, plus typed SSA verification/initial integer lowering.
Contextual generics/inherited substitution and broader lowering remain incomplete.

The current backend implements every operation in the initial integer IR, emits
koh-objects canonical ELF/SM83 and executes in the unchanged C# emulator. Read
sm83-abi.md for dynamic SP frames, caller-owned return/argument buffers, guarded
WRAM0 capacity, arithmetic traps, spilled values and simultaneous edge transfers.
ELF retains method and IL markers; they are not C# source/PDB/KDBG mappings.
No production native CLI/startup, managed calls, SDK switch or game sample exists.

Root owns all implementation/tests. All read-only reviews are finished. Review
found IL-offset diagnostic overflow and shared test paths/build dirs; fixed with
max-offset red/green regression and isolated directories. Default cargo test
threading passes. Emission caps bytes during generation, not after huge allocation.

## Evidence

Workspace nextest: 374/374 passed, 0 skipped, exit 0; 198 compiler cases, including
10 backend cases. Log /tmp/koh-sm83-nextest.log. Seven backend tests execute 140
ROM scenarios through ELF serialization/readback, Rust linker and C# emulator:
130 expected normal returns, 10 traps, all guard sentinels intact. Includes 18
real Roslyn Debug/Release scenarios. Other cases cover symbols and diagnostics.
Focused default-threaded suite: /tmp/koh-sm83-reviewed.log (10/10).
Separate workspace doctests exit 0, no cases (/tmp/koh-sm83-doc.log); stable
rustfmt check exit 0 (/tmp/koh-sm83-fmt-check.log).
Legacy baseline remains 814/814. All 14 hosted checks passed on preceding IR head
579b910. Check new backend head separately; old CI is not final-tree validation.

## Next

Implement managed calls/reachability and a native entry path. Reserve the ABI
stack region through linker-visible RAM sections before adding globals/heap;
current pure-function tests have no global allocator. Add call capacity checks
before argument/return writes and CALL. Keep dynamic activation identity.
Complete missing language/runtime, optimization, generic, banking, IRQ, Portable
PDB/debug, SDK, sample, test-audit/migration and legacy deletion requirements in
this single PR. Do not redefine success as the integer slice. Rough progress is
16%; 100% means every original completion criterion verified. Keep old compiler
until replacement acceptance, then delete it entirely. Do not merge PR yourself.

## Verification

Build serially per target directory. KOH_DOTNET selects SDK 10.0.115. Read completed
logs/direct exit codes; use stable rustfmt. Reuse koh-objects ELF and Rust linker,
execute with C# emulator. Preserve tracked sample assets and main screenshot.
Commit/push logical increments and update this draft PR, never open another PR.
