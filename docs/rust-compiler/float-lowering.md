# Native floating CIL lowering increment

Status: binary32 addition/storage implemented; full floating-language support remains incomplete.

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

Addition selects `f32.add` from qualified `Koh.GameBoy.KohRuntimeAttribute`
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

Subtraction, multiplication, division, negation, comparisons, numeric float
conversions and binary64 lowering remain unfinished and explicitly reject.
This increment does not retire those requirements or claim general .NET float
compatibility. The separate addition arithmetic tests cover subnormals, rounding,
signed zeros, infinities and NaNs; they are not exhaustive IEEE verification.

## Evidence

`tests/driver/float32.rs` compiles actual Roslyn Debug/Release assemblies for DMG
and CGB, executing 24 ROMs across addition, calls, joins, references, fields and
arrays. It checks full return bits, frame guards, PDB/ELF/KDBG provenance,
metadata-driven helper renaming and missing/duplicate/wrong-signature errors.
`tests/ir/float32.rs` checks type separation and bit transport.
`tests/sdk/float32.rs` builds Debug/DMG and Release/CGB projects with paths
containing spaces through `dotnet build` and executes their generated ROMs in
the existing C# emulator. Runtime-payload tests also reject every truncated
prefix, noncanonical lengths, invalid UTF-8, named arguments and trailing bytes.
