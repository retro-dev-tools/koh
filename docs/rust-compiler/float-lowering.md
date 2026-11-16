# Native floating CIL lowering increment

Status: binary32 addition/subtraction/negation/storage implemented; full floating-language support remains incomplete.

Float values need distinct IR types; treating them as integer stack values would
accept invalid mixed CIL and permit integer optimizations to change float
semantics. Use explicit bit reinterpretation at the software-runtime boundary.
Keep integer arithmetic operations separate. The initial increment handles
binary32 constants/calls/storage and software addition, building on the tested
AddF32 numerical correction. Other floating operations must diagnose until their
lowering and numeric behavior are implemented and independently validated.

Recognize KohRuntime attributes through the same qualified metadata ownership
contract as existing Koh attributes. Index operation keys lazily once per resolver,
diagnose malformed/duplicate declarations, and select a helper with a checked
raw-bit signature when an operation is lowered. Reachability then includes only
the chosen helper and its transitive ordinary CIL dependencies. This is a fixed
CIL/SM83 contract, not a frontend/backend plugin registry.

Test actual Roslyn Debug/Release DLLs through typed IR, canonical ELF, Rust link
and the existing emulator. Include float/int type separation, scalar calls,
branch joins, address-backed storage and explicit unsupported-operation errors.
No C# parser, compiler fallback or managed assembly execution is introduced into
the production compiler. The library's integer routines remain ordinary game
runtime code compiled by Rust.

## Current contract and ABI

`float` constants, arguments, returns, branch joins, managed references, static
fields and single-dimensional arrays retain their binary32 bits in four-byte
storage. The IR distinguishes `F32` from integers. `Reinterpret` permits only
`I32`/`F32` bit transport; integer arithmetic, comparisons and numeric conversion
cannot consume a float. SM83 uses the existing four-byte calling/storage ABI.

Addition/subtraction select `f32.add`/`f32.sub` from qualified `Koh.GameBoy.KohRuntimeAttribute`
metadata owned by assembly `Koh.GameBoy`. The selected method must be static,
nongeneric managed CIL with raw signature `uint(uint, uint)`. Missing keys,
duplicates, invalid declarations/payloads and missing implementation bodies are
errors. Reference-only declarations are allowed until selected. Selection does
not depend on a method name or prior compiler invocation.

Qualified ownership replaces historical simple-name matching: unrelated
attributes must not acquire compiler privileges accidentally. A duplicate
hard-coded helper/address registry would create another source of truth; the
existing metadata contract supplies the operation identity instead. No public
attribute constructor changes are required.

Negation reinterprets the operand, flips its sign bit with integer XOR, and
reinterprets the result. This preserves signed zero, subnormal magnitude and NaN
payload bits without a call. Requiring the old `NegF32` helper would add a stack
frame to a bit operation without improving semantics. The game library helper
remains available for raw-bit callers; its historical use is not an ABI rule.

Multiplication, division, comparisons, numeric float
conversions and binary64 lowering remain unfinished and explicitly reject.
This increment does not retire those requirements or claim general .NET float
compatibility. The separate addition arithmetic tests cover subnormals, rounding,
signed zeros, infinities and NaNs; they are not exhaustive IEEE verification.

## Evidence

`tests/driver/float32.rs` compiles actual Roslyn Debug/Release assemblies for DMG
and CGB, executing 60 ROMs across arithmetic, calls, joins, references, fields and
arrays. Negation includes all three old finite inputs plus tiny values,
infinity and NaN. Subtraction checks finite values, gradual underflow and
infinity-minus-infinity. It checks full return bits, frame guards, PDB/ELF/KDBG provenance,
metadata-driven helper renaming and missing/duplicate/wrong-signature errors.
`tests/ir/float32.rs` checks type separation and bit transport.
`tests/sdk/float32.rs` builds Debug/DMG and Release/CGB projects with paths
containing spaces through `dotnet build` and executes their generated ROMs in
the existing C# emulator. Runtime-payload tests also reject every truncated
prefix, noncanonical lengths, invalid UTF-8, named arguments and trailing bytes.

## Source test dispositions

| Source method in `Frontends/CilFloatTests.cs` | Requirement/oracle | Decision and Rust location |
| --- | --- | --- |
| `F32_Neg_MatchesHost` (three rows, Debug and Release) | Unary float negation; independently expected binary32 patterns for 1.5, -2.5 and +0 | PORT: `driver/float32.rs`, all inputs in both modes and both cartridge modes; full return bits and stack guards |
| `F32_Neg_RoutesThroughRealSoftFloatFunction` (one assertion) | Historically asserted a private helper name, not numerical behavior | REPLACE: the same native executions include a real parameterized CIL `neg`, including special values; helper naming is deliberately unconstrained |

Both replacements are discoverable under cargo-nextest. These close two source
methods (1 PORT/1 REPLACE), preserve all six original expanded numeric checks,
and replace the one compiler-internal helper assertion. They do not close the
mixed-operation `F32_Arithmetic_MatchesHost` method or the remaining float tests.

Independent semantics: [ECMA-335, sixth edition](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf),
I.12.1.3 and III.3.50/III.3.64. The standard permits implementation precision
at least that of the nominal type; this compiler uses binary32. Negating NaN
must remain NaN; exact sign/payload preservation is the selected bit-operation
implementation, not a claim that all .NET implementations choose the same bits.

Local verification: `/tmp/koh-float32-sub-neg-final.log`, 551/551 selected tests
passed in 71.684s, exit0 (296 unselected). This includes all compiler library/IR
tests, the 60 Float32 ROM executions and existing software runtime tests.
Workspace all-target/all-feature check and separate rustdoc pass, exit0; rustdoc
currently contains no cases. This is local evidence, not cross-platform proof.
