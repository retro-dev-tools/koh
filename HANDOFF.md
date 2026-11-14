# PR #55 working handoff

Branch: `feat/rust-cil-compiler`, isolated from the main checkout's modified screenshot.
Baseline: integrated linker migration `9cd1472`. Draft PR #55 already exists.
The C# compiler remains production. Keep the full attachment goal active.

## Implemented increments

All 565 legacy test methods have source-level dispositions, not completed coverage
migration. None is retired or fully replaced yet. All three audit documents are
complete; no compiler-owned coverage may be deferred to another migration.

Native metadata/body, CIL encoding and signature readers plus full-identity
reference/member binding are committed. Binding review's malformed zero-row scopes
and nested generic-arity regressions were observed red then green. Generic inherited
substitution and contextual constraints remain incomplete.

The next increment adds typed SSA IR/verifier and initial primitive-integer CIL
lowering. See `docs/rust-compiler/ir.md` for invariants and temporary limits.
Real Roslyn Debug/Release cases are interpreted against eight literal scenarios
per configuration. These are IR tests, not machine execution. Review found four
host-width bugs in the test helper; added regressions failed before their fixes.
Root owns all implementation/tests. Audit and review agents are finished; no writers
are delegated. No native executable, ELF emitter, SDK switch or new ROM exists yet.

## Current evidence

Completed workspace nextest: 364/364 passed, 0 skipped, exit 0, including 188
compiler cases (36 new IR/lowering cases). Log `/tmp/koh-ir-final-nextest.log`.
Separate workspace doctests exit 0, no cases (`/tmp/koh-ir-doc.log`); stable fmt
check exit 0 (`/tmp/koh-ir-fmt-check.log`). Earlier 360-case run predates four
width regressions. Legacy compiler baseline: 814/814 passed with one test at a time.
All 14 hosted checks passed for binding head `215ce9e`. Check the new IR head
separately after push; prior results do not validate its hosted tree.

## Next

Choose/document the SM83 ABI and advance integer lowering to canonical ELF and
actual C# emulator execution. Complete missing language/runtime, optimization,
generic, Portable PDB/debug, SDK, samples and test-migration requirements within
this single PR. Never redefine success as a reader or IR milestone. Keep the old
compiler until replacement acceptance, then delete it entirely as required.

## Verification discipline

Build serially per target directory. KOH_DOTNET selects pinned .NET SDK 10.0.115.
Read completed logs and direct exit codes. Use stable rustfmt. Reuse koh-objects
ELF and the Rust linker; execute with the existing C# emulator. Preserve tracked
sample assets and the main checkout's unrelated screenshot. Commit logical units.
