# Primitive local and argument references

Native lowering supports short and wide `ldloca`/`ldarga` encodings for supported
primitive storage. The result is a scalar-typed managed reference, consistent with
the [CIL address operation](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes.ldloca?view=net-10.0).
Original metadata supplies byte/short/char/bool/native signedness; evaluation-stack
types alone cannot distinguish those storage types.

`LocalAddress` requires an initialized, compatible local slot and returns
`ByRef(Scalar)`. Argument storage is copied into the callee's own slots, so mutating
a by-value argument through its address changes the callee's copy. Taking the address
of an already-managed-reference slot is unsupported; ordinary reference forwarding
loads its value instead. [Nonzeroed IL locals](local-assignment.md) can be addressed
after assignment on every incoming path. Taking an unassigned local's address
for initialization through an alias or an out call still diagnoses pending that
additional analysis.

## Frame identity and narrow storage

Each invocation owns its local storage for its entire active frame. Local slots
are distinct from reusable SSA temporary slots. Address emission computes the
absolute current SP plus the slot offset and preserves that pointer before writing
the result. Later calls and recursion can move SP without changing an ancestor's
already-computed address.

Small primitive locals currently use padded evaluation-stack-sized slots. Reference
writes touch only the declared byte width. The frontend therefore normalizes narrow
local loads as well as stores: otherwise writing +2 through a reference into a
previously negative byte/short would leave stale upper bytes. Negative writes after
a positive assignment likewise require sign extension on the next ordinary load.
No optimizer assumes padded high bytes remain normalized after an aliased write.

## Managed lifetime checks

The verifier rejects returning a reference to current-frame local/argument storage,
matching the [reference lifetime rule](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/return).
A bounded dependency graph propagates frame origin through managed-reference local
stores/loads, block parameters and reference-returning calls. A work queue handles
loops without recursive analysis. Incoming references remain borrowed from their
caller, so returning an incoming reference is supported.

The analysis conservatively treats a reference-returning call as potentially returning
any reference argument and merges all writes to a reference local. It can reject
safe complex reassignment/scoped-return cases; the diagnostic identifies that
unsupported contract. Precise scoped metadata, aggregate references and richer
return summaries remain pending. Explicit conversion to native pointer values
enters the unsafe bus-address contract; this is not a memory-safety proof for raw
pointers or permission to use a dead frame's address.

## Null references and bus address zero

Managed-reference loads/stores check the 16-bit address before accessing memory.
Zero aborts through `__koh_trap_null`, native fault code 4. This provides the current
no-EH subset's defined failure path; managed exception recovery is not implemented.
Native pointers retain raw bus semantics, including valid access to ROM address
0000. Neither check adds stack scratch beyond the existing two-byte reservation.

## Independent evidence

Real Debug/Release Roslyn fixtures check caller-frame references passed through
eight recursive invocations while each invocation retains separate locals/guards.
They also check addressed by-value arguments, positive/negative signed narrow writes,
64-bit mutation and a returned borrowed alias. Expected final value is 42 with
balanced SP and protected memory intact in the existing C# emulator.

Four additional ROM scenarios exercise short/wide local/argument CIL forms directly.
Four more compare null managed-reference reads/stores (fault 4) with native bus-zero
accesses (normal returns). Rust cases reject current-frame references returned directly,
through locals, call results or cyclic block arguments, and validate slot index/type/
initialization. Returning an incoming reference remains a positive case.

Traceability: this advances the frontend audit's primitive ref-parameter mutation
requirement and the backend audit's explicit warning that pointers must retain
ancestor-frame identity across recursion. It does not retire aggregate/float ref
tests or establish structs, arrays, object lifetime, scoped-return compatibility,
SDK/debugger acceptance or full compiler replacement.
