# Managed calls and reachability

The initial integer frontend now resolves static managed `call` instructions
through the supplied assembly set. Resolution retains full assembly/member
identity. Calls pop parameters in reverse evaluation-stack order, check their
types, restore declaration order for marshalling and push only non-void results.
Small argument/return storage is normalized by the callee as before.

IR calls carry target definition, typed signature and argument values. Function
verification checks argument uses/types and result shape. Program verification
also requires a unique definition for every target and an exactly matching
signature; a well-typed call site alone cannot validate an unrelated callee.

`compile::reachable` lowers each reached definition once using an iterative work
queue. Direct/mutual recursion cannot recurse the compiler's lowering stack.
Output is sorted by assembly/token for deterministic order. Limits are 4,096
methods and two million aggregate IR entries. Unreachable methods do not pull in
their unsupported IL, attributes or BCL dependencies. This is body reachability;
IRQ roots and initializer semantics still require explicit integration.

The SM83 caller reserves return/argument bytes below its current SP, copies values
from its still-live frame, CALLs, captures the return into that frame and restores
SP. The capacity guard precedes reservation, marshalling writes and CALL. Hardware
return-address bytes and temporary PUSH AF bytes reuse the same reserved boundary.
The callee separately checks its frame before writes. No fixed shared return or
argument scratch is used. Existing caller values therefore survive nested calls.

Each function object carries a canonical WRAM0 UNION reservation for `.koh.stack`
at `$C100`, size `$F00`. The linker combines these reservations across objects and
rejects overlapping globals. This is ELF metadata consumed by the existing linker,
not compiler-owned layout. Startup/global/heap integration remains incomplete.

## Evidence and remaining work

Rust tests compile three real C# projects (Entry → Bridge → Runtime) in Debug and
Release and check the exact transitive method set. Twelve new ROM scenarios cover
result 42 from a recursive sum across references, mixed 32/64-bit arguments/results,
values live across calls, mutual recursion, void calls and deep-stack traps. Two
additional ROM scenarios cover direct caller preservation and a call-buffer guard.
Together with the preceding backend suite this is 154 executions: 141 expected
normal returns and 13 expected traps. Guard memory lies strictly below `$C100`;
valid stack bytes at/above that address are not asserted immutable.

Removing the caller guard in a controlled mutation made the sentinel test report
`CORRUPT`, even though the callee still had its frame guard. The production guard
was restored before verification. Stack reservation/overlap, call signatures,
missing transitive references and reachable unsupported execution have separate
assertions. The C# emulator implementation is unchanged.

Initialization was a real omission found during this increment: ordinary method
reachability could omit a declaring type's `.cctor` or `<Module>..cctor`. Real C#
throwing initializers reproduced both successful-but-wrong lowering paths before
their fixes. That increment introduced temporary diagnostics. They are now replaced
by [actual constructor execution](initializers.md); throwing bodies still diagnose
at their unsupported exception operations rather than being silently omitted.
[C# module initializer semantics](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/attributes#23510-the-moduleinitializer-attribute)
provide an independent reason these bodies cannot be silently skipped.

Instance/virtual calls, generic instantiation, memory/objects, IRQ/banked calls,
full access checking, initializer execution, optimization, native CLI/startup,
Portable PDB/KDBG and SDK integration remain incomplete. No legacy compiler test
is retired and no game sample acceptance is claimed by this increment.

Plan: typed-call verification → guarded ABI emission → transitive metadata/body
lowering → independent Roslyn/emulator scenarios → review/negative initialization
regressions → workspace nextest and separate doctests → one progress commit in
the existing draft PR. All tests are Rust-owned; C# fixtures/host only supply SDK
input and the existing execution engine.
