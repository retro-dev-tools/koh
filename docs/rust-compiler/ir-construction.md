# Native IR construction test migration

This closes the five methods and five assertion call sites in baseline
`Ir/IrBuilderAndVerifierTests.cs`. They test private compiler interfaces;
typed control flow, call arguments and return values are independently necessary
for sound code generation. None establishes a public textual IR format.

| Source method | Decision | Native replacement / independent requirement |
| --- | --- | --- |
| `Builder_ProducesVerifiableModule` | REPLACE, complete | `ir::construction::constructed_loop_survives_optimization_and_canonical_object_roundtrip` and `sm83::constructed_sum_loop_executes_before_and_after_optimization`: directly construct a typed two-parameter loop, verify it and execute its sum. |
| `Builder_RoundTripsThroughText` | REPLACE, complete | Deterministic canonical ELF emission and read/write round-trip replace private C# IR printer/parser stability; the same function executes before/after optimization. No new IR text parser is needed. |
| `Verifier_FlagsMissingTerminator` | REPLACE, complete | Rust `Block` requires a nonoptional `Terminator`; an unterminated block cannot be constructed. Negative tests reject an empty function, invalid successor and missing return value before emission. |
| `Verifier_FlagsReturnTypeMismatch` | REPLACE, complete | A native16 function returning an I32 signature is rejected, including the machine-emitter boundary. |
| `Verifier_FlagsCallArgTypeMismatch` | REPLACE, complete | A native16 argument passed to an I32 call parameter is rejected before emission. |

All replacements are discoverable by cargo-nextest. The original valid fixture
contains a loop with two phis but never executes it. The replacement uses block
parameters for the induction value and accumulator and runs36 ROMs: six inputs
at16/32/64 bits, before/after optimization. Expected results use the independent
formula `n*(n-1)/2` modulo the selected width. Inputs0/1/2/10/257/400 cover an
empty loop, one iteration, accumulation and native16 wraparound. The existing
C# emulator host checks all returned bytes, stack balance and reserved-memory
guards. No C# compiler implementation is involved in constructing these objects.

Three pure Rust positive cases check verification, optimized verification,
deterministic ELF bytes and canonical ELF round-trip. Five negative construction
cases check rejection by both verifier and emitter. This adds11 discoverable
cases and36 ROMs, replacing five source assertions with stronger behavioral
checks. The separate seven-method `IrRoundTripTests` group remains open; its
broader canonical fixture and explicit-entry contract are not silently closed.

This group changes tests only. The legacy source remains until final compiler
removal. The ledger becomes164/565 complete,401 outstanding; no feature is
retired and full migration remains incomplete.

Verification: `cargo nextest run -p koh-compile --test ir --test sm83 --locked`
passed454/454 cases, zero skips, in190.582s (exit0). Completed log:
`/tmp/koh-ir-construction-final.log`. Rustfmt and diff checks pass. This is local
Linux evidence for these two targets, not complete hosted/platform acceptance.
