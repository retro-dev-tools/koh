# Compiler/runtime support boundaries

Koh compiles a closed, reachable CIL program for a bounded Game Boy target.
It does not embed the managed runtime. Supporting a metadata declaration does
not imply supporting every CLR conversion or execution mechanism involving it.
These boundaries are product choices to validate in final acceptance, rather
than an instruction to preserve old compiler internals or current library APIs.

## Boxing and variance conversions

Keep value types unboxed. Struct values, arrays, closed generic storage and
supported constrained interface calls retain their existing value/alias
semantics. `box`, `unbox` and `unbox.any` execution is outside this contract.
Adding boxing would require a defined boxed identity, allocation/lifetime,
unboxing alias semantics and dispatch/type-test integration. Treating a struct
as an ordinary reference or copying it to a placeholder would be incorrect.

This is not a retirement of demonstrated old boxing behavior. The baseline
`CilStructReturnTests` explicitly avoids emitting `box` because it is outside
its subset; the old method lowerer has no boxing opcode implementation. Native
`driver::variance::exact_variant_interface_shapes_validate_flags_and_positions`
requires the valid `Program::Boxed` assembly to fail with member, IL offset and
opcode0x008c. A useful improvement is a named boxing diagnostic; numeric opcode
rejection is current behavior, not malformed-metadata classification.

Accept valid covariance/contravariance declaration flags and independently
validate their signature positions. Exact closed interface/delegate identities
remain usable. Cross-instantiation variance conversions and variance-aware
runtime casts/type tests are outside the contract. Exact identity matching must
never silently return false for a valid covariant test. Alternatives are full
variance-aware assignability or rejecting variant declarations entirely. The
former needs adaptation integrated with dispatch and runtime tests; the latter
would unnecessarily reject ordinary exact calls and enumeration declarations.
The chosen middle boundary preserves those useful calls and diagnoses conversion
requests. [Exact variance](exact-variance.md) records execution and negative
Debug/Release cases in `driver/variance.rs`.

## Interrupt execution

Keep the [bounded scalar interrupt ABI](irq-calls.md): all five hardware roots,
transitive acyclic scalar helper calls, private activation frames, reserved stack
capacity, register/flag restoration, RETI and mapper preservation. Mainline
recursion and shared main/IRQ helper activations remain supported within their
documented guards. Loops inside a handler are permitted; no termination or
worst-case execution-time proof is claimed.

Allocation, arbitrary indirect memory, floating values, aggregate/reference
arguments or returns, unsafe bank/device changes, special runtime initialization
and nested interrupt enabling remain rejected in the IRQ graph. This is a
restriction on interrupt execution, not on ordinary game code. General support
would require reentrancy, mapper ownership, transient helper storage and stack
proofs for every added operation. Deferring work to mainline through scalar
state keeps the hardware interface useful without implying those proofs.
`driver::interrupts::interrupt_contract_failures_are_explicit` exercises rejected
graphs; the IRQ helper tests independently check interrupted state and capacity.
Broader IRQ support is not promised by accepting an interrupt attribute.

## ROM data and capacity

Keep banked MBC5 managed code and the existing bank-preserving call ABI. A method
must fit one16KiB bank. The linker selects banks and diagnoses layout overflow.
Literal and initialized constant pools remain in ROM0 with checked capacity.
General immutable ROMX references are outside this contract: a two-byte pointer
cannot carry both bank identity and address across arbitrary mapper changes.
The alternative is a bank-bearing ROM data reference plus pin/copy lifetimes,
which would change storage, field signatures, aggregate copies and debugging.
Silently truncating bank identity is not an alternative.

This does not constrain all data to ROM0. Mutable managed data uses the
[checked RAM layout](ram-layout.md), including expanded contiguous allocations
and bank-crossing pins. Raw pointers retain their documented mapper ownership
obligations. Real game assets and exact hardware observations remain acceptance
requirements; exceeding the chosen capacity must fail explicitly rather than
omit data. `sm83::oversized_machine_code_is_diagnostic`, literal-pool tests and
banked call/array tests cover separate parts of this contract. Final linker
capacity and sample acceptance must still be checked at the release commit.

## Acceptance scope

This document adds no source-test disposition credit and removes no useful test
or production capability. The [language contract](supported-subset.md) consolidates
these boundaries and distinguishes malformed input from valid unsupported
semantics. The [acceptance audit](acceptance.md) records positive/negative tests,
legacy deletion, consumer migration, source debugging, platform results and
performance evidence, together with unresolved release-review items.
