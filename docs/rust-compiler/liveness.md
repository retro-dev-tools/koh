# Live-value preservation test migration

All four baseline `Ir/IrLivenessTests.cs` methods (15 assertion call sites)
receive native behavioral replacements. Liveness sets and `IsTrackable` are
private implementation details; the independent requirement is that distinct
live values retain their bits until their final use.

| Source method | Decision | Native behavior |
| --- | --- | --- |
| `Liveness_ParameterIsLiveInAtEntry` | REPLACE, complete | Execute `(n+1)+n` with the original parameter used after an intermediate result. Check all returned bits before/after optimization. |
| `Liveness_ValueLiveAcrossBranches` | REPLACE, complete | An entry-defined `n+1` feeds both diamond arms. Return the selected `n+2`/`n+3` through a block parameter, including false and high-bit-only true conditions. The old test's unused arm arithmetic and constant-zero return could not expose miscompilation. |
| `Liveness_LoopCarriedValuesLiveAroundBackEdge` | REPLACE, complete | A signed-bound counting loop preserves its original bound and induction value through repeated backedges. Check empty, positive, signed-minimum and−1 inputs; the exit returns the actual induction value. |
| `Liveness_ConstantsAreNotTracked` | REPLACE, complete | Execute the same live-parameter expression with constants1 and7 across width/sign/carry boundaries. Assert resulting bits rather than requiring a private constant classification or allocation strategy. |

`tests/sm83.rs::live_values_survive_entry_branches_and_backedges` is discoverable
by cargo-nextest: nine rstest cases cover three shapes × native16/I32/I64.
`tests/support/liveness.rs` constructs the typed IR directly. Every function
verifies before optimization and after simplification, then emits canonical
ELF for the Rust linker and runs in the existing C# emulator.

The nine cases execute186 ROMs:72 entry/constant,72 branch and42 loop runs.
Each scenario runs before/after optimization. Entry/branch inputs include0,
1,127,0xffff, the selected sign bit and full-width maximum; some patterns
coincide at16 bits. Loop bounds0/1/2/10/64/minimum/−1 distinguish signed
comparison from unsigned counting. Independent Rust formulas use explicit
modular arithmetic, branch selection and signed-bound classification. The
existing host checks all returned bytes, normal-return stack balance and
reserved-memory guards. The source tests never executed the constructed IR.

No liveness analysis framework or production compiler change is added solely
to reproduce the old sets. No game capability or useful behavioral assertion
is dropped. Private set membership/emptiness checks are replaced by stronger
observable value preservation; verifier checks remain explicit.

The ledger becomes175/565 dispositions complete:174 native replacements and
one obsolete parser-only DELETE decision;390 remain outstanding. Legacy
definitions are still physically present pending final compiler removal.

Verification: the focused nine cases passed9/9 in18.892s, exit0. The complete
IR/SM83 targets then passed467/467 in208.909s, zero skips, exit0
(`/tmp/koh-liveness-final.log`). Command:
`cargo nextest run -p koh-compile --test ir --test sm83 --locked`.
Rustfmt and diff checks pass. These local Linux tests do not establish full
migration or current-head cross-platform acceptance.
