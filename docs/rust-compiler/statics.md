# Static integer storage and primitive references

Native lowering supports reachable, nongeneric static fields containing supported
primitive integers or unmanaged pointers. `ldsfld` and `stsfld` become explicit
width-aware memory operations. `ldsflda` yields a typed primitive managed reference.
Fields are resolved by metadata identity, including MemberRefs in other assemblies;
unreached fields consume no storage. Literal/RVA data, generic storage, managed
reference fields and instance-field execution remain separate pending features.

Each field-address operation carries its resolved identity and allocation width.
Program verification rejects inconsistent widths for one identity. The SM83 emitter
imports a field symbol through a word relocation; it does not select a RAM address.
The cartridge object defines one packed, zero-fill `.koh.statics` WRAM0 section
and symbols at field offsets, using the existing object model and ELF serializer.
The Rust linker places the section and resolves all addresses.

Startup clears that section through its relocated base before calling game code.
It does not rely on power-on RAM contents or emulator defaults. Initial code and
stack layout currently leave 256 bytes of WRAM0 for static storage; larger reachable
storage is an explicit capacity error. Other linked allocations can reduce the
available space further, which remains a linker error. This is an initial capacity
boundary, not the final game memory/banking strategy. Heap, object/data storage and
remaining sample requirements still need coordinated RAM allocation.

## Primitive references

IR `ByRef(Scalar)` retains the primitive target type while using a 16-bit address
in the ABI. `ref byte` cannot be passed as `ref int`; memory operations through a
managed reference must match its storage width. Native pointers remain explicitly
untyped bus addresses. Calls, returns, local copies and block parameters preserve
reference types. Arithmetic requires explicit conversion to a native pointer;
nonzero reference constants and conversions that fabricate references are rejected.

Current accepted references originate from static primitive fields or incoming
primitive-reference parameters. Local-frame addresses, aggregate references,
readonly/modifier semantics and reference lifetime checks remain pending. These
tests do not establish ancestor-frame address correctness. No optimization infers
non-aliasing from different reference values. Returning a reference to a static
field and mutating that field through another call use the same actual storage.

## Initialization remains required

Zero-fill storage is not type-constructor execution. Reached type/module
initializers, including field accesses into a type with a constructor, still
diagnose pending the initialization state machine. Readonly field writes/addresses
also require the initializer rules. The next increment must execute constructors
once with independently tested ordering and recursion semantics; none of these
capabilities is retired. Unreachable types with unsupported initializers do not
prevent compiling unrelated code.

## Evidence

The Statics fixture builds actual Debug/Release C# projects with a reference library.
It checks initial zeros, 8/16/32/64-bit and native storage, bool/char, signed versus
unsigned loads, shared fields across assemblies, `ref byte`/`ref int` arguments
and a returned reference alias. The native CLI emits ELF, the Rust linker produces
a ROM, and the existing C# emulator must return 42 with balanced SP and intact guards.
Test-only host mode poisons WRAM with A5 and verifies the poison before startup;
initial values cannot pass merely because the emulator happens to clear RAM.

Only 31 bytes of fields are reached. Dead oversized fields and an unused type with
an object initializer remain absent. Selecting the oversized entry must fail with
a static-storage capacity diagnostic. Selecting constructor-dependent or generic
fields must diagnose, rather than return default placeholder values. IR cases cover
field identity/width/result checks, cross-function consistency, managed-reference
call types and incompatible dereference widths.

Execution initially returned the fixture's zero-check failure: stack-address setup
clobbered HL before the relocated field pointer's high byte was saved. Preserving
the pointer in BC corrected the observed failure. Source review checked startup
loop/relocation, byte widths, stack scratch use and reference typing.

Traceability: the frontend/backend source audits require shared static storage,
byref mutation and address identity across calls. This increment supplies native
behavioral evidence for static primitive addresses. It does not retire an entire
legacy test method or establish constructors, structs, arrays, IRQ or game acceptance.
