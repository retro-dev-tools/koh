# Comparison and conversion optimization contracts

Eleven source methods are REPLACE dispositions, complete in native tests.
The five methods in `Ir/RedundantCompareEliminationPassTests.cs` contain23
assertion sites; the six in `Ir/RedundantConversionEliminationPassTests.cs`
contain20. The [source matrix](backend-audit.md) names every method. No source
method or compiler implementation has been physically deleted by this increment.

The independent requirements are integer comparison predicates, boolean0/1
normalization, preservation of low bits through extension/truncation, and modular
increment. Private node identity, Changed flags, optimizer pass ordering and old
I8/I16 evaluation-stack conventions are discarded. CIL small integers occupy
I32 stack values. Different-width truncation may legally disappear when its
input range proves it redundant; extension after truncation must still lose
high bits. No claim is made that Rust reproduces each old graph rewrite.

`tests/support/optimizer_contracts.rs` builds actual CIL and lowers it through the
native frontend. Comparison widths8/16/32/64 cover negated equality, unsigned
less-than with boolean comparisons against zero, its complement, signed greater
than compared against one, and arbitrary integer equality against zero.
Conversion widths8/16/32 cover zero/sign-extension round trips, different-width
truncation, extension after truncation, and increment followed by narrowing.
Byte/word conversions use I32 intermediates; I32 conversions also exercise I64.

`tests/lowering.rs` has35 parameterized cases checking verified IR before and
after the production optimizer against Rust masks, signed/unsigned predicates
and wrapping arithmetic:44,440 evaluations including boundary and deterministic
pseudorandom inputs. `tests/sm83.rs` has35 corresponding cases:920 ROM executions
through canonical ELF, the Rust linker and the existing C# emulator. Native
checks retain full return bytes, stack pointer and reserved-memory guards.
These are compiler-owned tests discoverable by cargo-nextest, not TUnit tests.

The old MapWriter motivation does not make private graph shapes a cycle contract.
The separate [bounded VBlank copy contract](vblank-copy.md) covers graphics
transfer timing; this increment makes no new timing or performance claim.

Verification: `cargo nextest run -p koh-compile --test lowering --test sm83`
(with the documented SDK environment and serial build target) completed310/310,
zero skipped,510.277s, exit0. Completed log:
`/tmp/koh-opt-contracts-final.log`. This checks both complete affected test
targets, not the whole workspace or hosted platform matrix.
