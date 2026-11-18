# Retained IL in ReadyToRun references

The PE/CLI reader now admits standalone ReadyToRun version16 images with retained
CLI IL. It previously required ILONLY and therefore rejected the installed
.NET10 CoreLib before reaching a valid managed method. ReadyToRun can clear
ILONLY while retaining the input metadata and IL, as specified by the
[.NET format](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/docs/design/coreclr/botr/readytorun-format.md).
The current header layout/signature/version come from the
[runtime header](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/coreclr/inc/readytorun.h).

Acceptance requires IL_LIBRARY, a mapped/bounded ManagedNativeHeader, RTR
signature, supported major version16 and a bounded section-directory count.
Native entry points, unrecognized mixed-mode images, composite/embedded-MSIL
forms, other major versions and malformed/truncated directories reject explicitly.
No native section, host code or ReadyToRun entry point is executed, translated or
copied. Existing per-method checks still require a managed IL body and all
reached operations must satisfy the supported CIL subset. Native-only methods
remain unsupported. No unsafe Rust, dependency or second frontend is added.

This is metadata/IL input support, not a ReadyToRun runtime or a promise of
general BCL compatibility. Metadata-only reference assemblies do not supply
implementation bodies. Calls beyond the built-in metadata contracts need the
actual compatible implementation references; the test supplies both the runtime
System.Runtime facade and CoreLib as input files, without invoking their methods
on .NET.

## Replacing the blanket BCL test

`CilReferenceTests.CallIntoBcl_IsADiagnosticNeverLoweredOrMiscompiled` has two
original lexical assertion sites requiring blanket BCL rejection. Decision:
REPLACE with per-method semantics/availability checks in
`driver::reference_contracts::resolved_bcl_integer_max_has_correct_semantics`.

The original `Math.Max(3,5)` contains ordinary supported managed IL and now runs
to independently expected5. Five additional signed boundary pairs produce
-3, Int32.MaxValue, Int32.MaxValue,0,0, checked as full four-byte values.
The actual [.NET implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Math.cs)
is ordinary signed comparison/return CIL. There is no Math-specific compiler
shortcut or whitelist. Only the reached CoreLib Math.Max body may be emitted;
an inlined equivalent is also valid. The rest of CoreLib is not embedded.

The adjacent native `Math.Sqrt` has no managed body and must diagnose its member
and missing CIL implementation. This preserves the useful unsupported-call
requirement without treating a valid pure managed method as forbidden merely
because of its assembly name. The old BCL guard was an architectural choice,
not an ECMA-335 rule.

Four rstest cases run Debug/Release × DMG/CGB: eight linked/executed ROMs, four
native-body diagnostics and36 malformed/unsupported header mutations. Mutations
cover native entries, missing header, short/oversized directory bounds, bad
signature, older major version, composite/embedded flags and excessive counts.
Unsupported valid formats are distinguished from truncated inputs in diagnostics.

The decisive first run failed2/2 at the old assembly gate. After initial reader
support both original Max configurations passed; the final stricter header,
native-method and reachability checks passed4/4 in7.418s before the last two
extra header mutations were added. The expanded final compiler/reference/IR
selection passed242/242 in23.943s, exit0,535 explicitly unselected
(`/tmp/koh-bcl-max-final.log`). No old C# compiler behavior supplies the oracle.

Additional reader/signature/opcode/layout/literal-limit selection passed147/147
in40.257s, zero skipped (`/tmp/koh-r2r-readers.log`). Local commands use the
pinned .NET10 SDK through KOH_DOTNET:

```sh
cargo nextest run -p koh-compile --lib --test driver --test references --locked --no-fail-fast -E 'binary(koh_compile) | binary(references) | test(reference_contracts::) | test(ir_contracts::)'
cargo nextest run -p koh-compile --test assembly --test signature --test opcodes --test layout --test literal_limits --locked --no-fail-fast
```
