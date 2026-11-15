# PR #55 working handoff

## Objective and checkout

Complete the entire native Rust CIL-to-SM83 migration in this single draft PR:
https://github.com/retro-dev-tools/koh/pull/55 . Never merge it. Do not mark the
goal complete for an increment. User wants logical commits and an ASCII progress
bar where 100% means every original requirement is verified. Rough estimate 42%.

Worktree `/home/minorum/projekty/koh-rust-compiler`, branch `feat/rust-cil-compiler`.
Baseline integrated linker migration: `9cd1472`. The main checkout's modified
battle screenshot is unrelated; preserve hash
`fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`.

The C# compiler remains the production SDK compiler. No old test method is fully
retired yet. All 565 have source-level dispositions, not completed Rust migration.
Read docs/rust-compiler/progress.md and source audits for the full acceptance ledger.
The original assignment is also in the conversation and attachment
`/home/minorum/.codex/attachments/f20b0d62-eb77-4947-9b7a-4dcb4328fc7f/pasted-text-1.txt`.

## Implemented

Bounded native PE/CLI/CIL/signature/reference reading; typed SSA/block arguments;
dominance/type/program verification; integer lowering and SM83 emission; direct
calls/recursion with guarded dynamic frames; native CLI/startup and canonical ELF;
indirect memory, static storage and primitive managed references; register/region
intrinsics; actual type/module constructors with once guards and BFI deferral.
CFG chain merging and conservative block-local temporary reuse reduce code/frame
costs. Reuse koh-objects/koh-link; no second linker or object serializer.

Earlier unit adds primitive local/argument addresses, metadata-aware narrow load
normalization, a bounded frame-return escape graph and managed null fault 4.
Read local-references.md, statics.md, initializers.md and sm83-abi.md. Reference
return analysis is conservative for reassignment/scoped-return contracts. Native
pointers retain raw bus semantics. Structs/objects, float/generic/runtime,
IRQ/banking and source-debug/SDK acceptance remain incomplete.

Primitive SZARRAYs now allocate explicitly zeroed packed count/payload objects in
linker-reserved WRAMX bank 1. Full-width length/index checks fault before invalid
writes; empty objects have distinct identities. Array/static/local/return/null
semantics and heap element refs are typed. Managed references preserve SVBK;
native bus pointers retain selected-bank semantics. Internal allocator CALL/AF
scratch is guarded. Read arrays.md. No GC/reset or aggregate/
reference/float/multidimensional array support yet; these remain required work.
RVA initialization now copies validated field bytes into distinct mutable arrays;
compile-time field handles have no machine representation and cannot cross calls.
Exact framework helper identity/signature checks replace pattern suppression.
Canonical read-only ELF sections carry literal bytes; runtime null/size checks
and bank preservation guard copies. Read rva-initializers.md for subset limits.

## Evidence

Latest formatted-tree Linux workspace nextest: 535/535 passed, no skips, exit 0,
333.191s (`/tmp/koh-rva-final-nextest.log`); 359 compiler cases,260 ROM scenarios
(203 normal,57 traps). RVA-focused34/34, including24 ROM cases,12 helper rejection
probes,3 PE variants,11 malformed metadata cases and16 IR invariants. Source
review caught machine-call metadata handle fabrication; negative tests now reject
it through both verifier/emitter. An earlier526-case run was deliberately
interrupted after498 passes for those review fixes, not a full success.
Array-focused 29/29 passes; four D/R x DMG/CGB cases check
40 ROM scenarios and 16 rejection probes, full poisoned-bank snapshots, all twelve
primitive element types, returned refs and loop/null joins. Review caught native/
int16 opcode confusion; six malformed-IL regressions preserve the distinction.
Earlier full run500/501 exposed a lost return-diagnostic context, fixed without
weakening its assertion. Separate doc/fmt checks exit0. Existing C# emulator
executes all ROMs. Eight-level ancestor recursion,
by-value argument addresses, narrow signed ref rewrites, long mutation and borrowed
aliases pass in real Debug/Release Roslyn fixtures. Additional CPU cases cover
short/wide addresses and null refs versus valid native bus address zero.

Focused predecessor suite 117/117. Stronger positive-to-negative byte/short setup
passed 2/2 afterward, with no compiler-code change:
`/tmp/koh-local-ref-signed-final.log`. Separate doctests and stable fmt checks exit 0.
Read-only source review complete; no delegated writers/builds remain. Constructor
head d73963a passed all 14 hosted jobs; completed macOS log confirms 450/450.
Array head4a32ba8 has successful completed Rust/CI workflows, native Linux/Windows/
macOS Test steps; completed macOS log501/501. RVA-head hosted results remain
pending. Historical legacy baseline
814/814 is reference evidence, not a correctness oracle.

## Next work and verification

RVA unit ready for commit/push. Parent owns edits/builds; two read-only review
passes complete, no delegated writers/builds. Verify committed/remote draft state
after publishing and rerun formatted C# fixtures post-hook.

Finish/review/commit/push this unit if not already committed, update the same draft
PR and verify its remote head/draft state. Next pursue enum/switch/pinned-storage
and library/runtime support needed by actual Board/Tiles/gfx-demo sources, then aggregate/array/runtime
support toward real games; keep full IRQ/banking, Portable PDB/KDBG/debugger,
native SDK/incremental/package/CI, sample graphics execution, complete Rust test
audit/migration, C# compiler deletion, performance/platform and bounded Docker gates.
Do not replace the full goal with a polished primitive slice or retire useful
features merely to pass tests. Preserve Koh.GameBoy API and the existing emulator.

Build serially per output directory. Tests use
`KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet`.
Run workspace nextest/all-features/locked and separate workspace doctests. Use
`RUSTUP_TOOLCHAIN=stable cargo fmt --all -- --check`. Capture direct exits in logs
and read completed logs. Commits need the pinned dotnet directory prepended to PATH
for Husky/CSharpier. After its C# formatting, rerun the affected fixture cases.
Stage only this unit's files; do not touch unrelated main-checkout assets or merge.
