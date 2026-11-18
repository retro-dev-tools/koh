# Independent superoptimization tool

The standalone C# Superopt tool remains an emulator-backed exploration tool. It
is not called by `koh-compile`, the SDK compiler task or the linker. Its random
execution filter is not a proof that mined rewrites are safe for production.
The native compiler does not import its results automatically.

The previous instruction-domain check imported the legacy compiler's MIR decoder.
It now derives instruction boundaries and operands from the existing
`Koh.Opcodes` table, admits only explicit register/flag operations, and rejects
memory operands, SP, control/device operations, invalid opcodes and truncated
instructions. Immediate bytes are consumed as data, even when their values are
otherwise forbidden opcodes. No C# compiler IR, optimizer or metadata reader is
retained in the tool.

Both Superopt projects no longer reference `Koh.Compiler`. Production dependencies
are the existing emulator and opcode library. The16 original tool test methods
remain under their tools owner: enumeration/domain filtering, emulator oracle,
liveness-sensitive mining and report formatting are standalone tool requirements,
not migrated compiler-owned tests. They remain TUnit tests because this separate
C# tool and the C# emulator remain live components. No compiler test is deferred
to them and no credit is added to the565-method compiler ledger.

Three additional tool tests check the compiled assembly dependency boundary,
15 rejected malformed/unobservable sequences, and two accepted sequence shapes
including opcode-like immediate data. Red validation passed16 original tests
and failed exactly the new compiler-dependency assertion:17 total, exit2,
19.340s (`/tmp/koh-superopt-red-tests.log`). After replacement, all19 passed,
zero skipped, exit0,18.468s (`/tmp/koh-superopt-green-tests.log`). The build
completed with zero warnings/errors, exit0. The production
`koh-superopt.deps.json` contains neither Koh.Compiler nor Mono.Cecil.

The TUnit test runner's code-coverage extension independently bundles its own
Mono.Cecil binaries. That is not a compiler runtime dependency and does not
justify retaining the direct compiler-only package reference after deletion.

Validation uses a separate artifact directory to avoid changing outputs belonging
to the active native compiler regression run:

```sh
dotnet build tests/Tools/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj \
  -c Release --artifacts-path /tmp/koh-superopt-migration -m:1
dotnet /tmp/koh-superopt-migration/bin/Koh.Superopt.Tests/release/Koh.Superopt.Tests.dll \
  --maximum-parallel-tests 1
```

This is Linux evidence. The final solution build and cross-platform validation
still need to exercise the changed tool projects after legacy removal.
