# Native compiler contract

C# remains the game-authoring language. Roslyn/.NET10 compiles ordinary source
to a CLI assembly and optional Portable PDB; Koh reads those files without
executing the assemblies. Koh supplies one CIL frontend and one SM83 backend,
not a C# parser, managed VM or frontend/backend plugin platform.

This contract describes the supported execution model. It is not a promise of
general .NET compatibility, a full CLR security/verifiability implementation,
or support for arbitrary runtime-generated IL stubs. C# accessibility and source
typing are checked by Roslyn. Koh additionally checks metadata/binding, typed
evaluation stacks, its IR invariants, supported operations and target capacities.

## Language and storage

| Area | Supported contract and detailed evidence |
| --- | --- |
| Integers | bool, char and signed/unsigned8/16/32/64-bit storage; target-native16-bit integers/pointers; trusted System.Int128/UInt128 operations. CIL small-integer promotion, signedness, truncation, checked conversions/arithmetic and shift masks are retained. [IR](ir.md), [128-bit integers](int128.md), [checked arithmetic](checked-arithmetic.md). |
| Floating point | Binary32/binary64 arithmetic, comparisons, remainder and documented conversions through validated software-runtime lowering. No hardware floating ALU is assumed. [Float contract](float-lowering.md), [conversions](float-conversions.md), [remainder](floating-remainder.md). |
| Control flow | CIL stack joins, conditional/unconditional branches, loops, switch, direct/transitive calls, returning entries, and guarded mainline recursion. Debug and Release IL need equal observable semantics, not identical ROM bytes. [Control flow](control-flow-memory.md), [calls](calls.md). |
| Structs | Supported sequential/explicit layouts, scalar/reference fields, nested values, copies, local/field/array storage, constructors, byrefs and value returns. Reference-containing overlaps are constrained. [Layout](class-layout.md), [struct contracts](struct-contracts.md), [returns](struct-returns.md). |
| Classes | Checked arena allocation, zero/null state, constructors, inherited fields, reference identity, virtual/abstract overrides and closed-world dispatch. No garbage collector is embedded. [Objects](class-layout.md), [dispatch](virtual-dispatch.md), [memory](runtime-memory.md). |
| Arrays | Length-carrying one-dimensional arrays, supported scalar/aggregate/reference elements, zero initialization, indexing, aliasing, bounds and zero-based rank2 rectangular arrays. Nonzero lower bounds and unsupported ranks/shapes diagnose. Readonly fields do not make mutable elements immutable. [Arrays](arrays.md), [rectangles](rectangular-arrays.md), [generic elements](generic-rectangles.md). |
| References/pointers | Supported ref/out/in, typed field/element indirection, borrowed aliases, constant bounded stackalloc and checked pinning. Current-frame address escape and unsupported lifetime shapes diagnose. Raw native pointers retain bus/bank/ownership obligations. [Local refs](local-references.md), [pins](pinned-pointers.md), [stack buffers](stack-enums.md). |
| Generics | Reachable closed method/type instantiations, distinct declaring/method scopes, constraints, per-closed-owner statics, supported constrained/interface calls and inherited bodies. Instantiation/depth/work budgets diagnose. [Methods](generic-methods.md), [closed storage](closed-types.md), [constraints](generic-constraints.md). |
| Delegates | Supported static/instance/virtual method groups, capturing/noncapturing lambdas, nominal generic signatures, shared captures and stored callbacks. No arbitrary integer function handles, calli or general multicast runtime. [Delegates](delegates.md). |
| Strings | UTF-16 literal strings through locals/fields/parameters/returns, null/reference identity, Length, indexing and indexed foreach. Embedded NUL and surrogate code units are retained. This does not add a general string/BCL implementation. [Strings](strings.md). |
| Iteration/LINQ | Ordinary supported Roslyn state machines, normal structured finally/disposal, and the documented deferred LINQ/query/reduction surface. No generated-name iterator recognizer or general managed runtime is required. [Cleanup/iterators](finally-iterators.md), [LINQ](linq-runtime.md). |
| Statics | Reachable static storage/initializers, closed generic owner identity, initialization guards and capacity checks. [Initializers](initializers.md), [storage](static-contracts.md). |

## Deliberate exclusions

Boxing/unboxing, cross-instantiation variance adaptation, variance-aware runtime
casts/type tests, general reflection/dynamic loading, arbitrary BCL calls and
catch/filter/fault/exceptional unwinding are outside this contract. Valid variance
declarations and exact calls are accepted; valid unsupported execution must not
be mislabeled malformed simply because a tested CLR rejects a shape. Inherited
MethodImpl bodies follow the metadata requirements and checked scoped binding,
not a CLR rejection used as an oracle. [Boundary rationale](runtime-boundaries.md)
and [reference binding](reference-binding.md) record alternatives and evidence.

No useful capability is retired merely to close a legacy test. The
[565-method disposition matrix](test-disposition.md) records independent
requirements, replacements and the one obsolete private-parser deletion. Old
register allocation, static frames, private IR encodings and library conventions
are not public language requirements.

## Target ABI and hardware

The [ABI](sm83-abi.md) uses guarded dynamic activation frames, explicit packed
argument/return storage and verified multi-byte movement. Byte register aliases,
parallel edge copies, recursion and transient scratch space must remain correct.
Capacity failures never justify wrapping offsets or dropping a method/data item.

Managed code uses MBC5 banked calls. Each function must fit one16KiB bank; the
linker owns bank placement, relocations and final cartridge headers. Constants
and strings remain in checked ROM0 pools; arbitrary bank-bearing immutable data
references are excluded. Managed/raw allocations share the documented checked
[RAM plan](ram-layout.md). Unmanaged code must respect compiler-owned storage
and mapper state; arbitrary bus pointers are not safe managed references.

[Interrupt execution](irq-calls.md) supports all five roots and bounded acyclic
scalar helper graphs with register/flag/mapper restoration and RETI. Mainline
recursion remains allowed within guards. Allocation, floating/reference/aggregate
IRQ execution, unsafe device/bank changes and nested enabling are rejected.
Loops are permitted, without a termination or worst-case-time guarantee.

Koh.GameBoy is a useful metadata-driven hardware/target-runtime library, not the
host compiler. Intrinsic contracts validate their payload/signature; runtime keys
select reachable target implementations. Ordinary library CIL, including software
arithmetic helpers, is compiled by Rust into ROM code. No C# reader/lowerer or
in-process compilation fallback survives in MSBuild. Tile-sheet generation stays.

The current library provides bounded VRAM/map/palette operations because arbitrary
managed polling/copy sequences do not acquire fixed hardware deadlines. Palette
critical writes explicitly leave IME disabled; callers manage interrupt state.
Do not infer instruction timing or old helper layouts from C# syntax. Historical
runtime snapshots used for compiler measurements are not current timing contracts.

## Debugging and rejection

Matching Portable PDBs retain useful source/method provenance through lowering,
optimization, ELF and KDBG. Builds without symbols work. Compiler-origin native
breakpoints, mapped disassembly and stack recipes are tested against the existing
debugger. KDBG v1's bank range is narrower than MBC5 execution; unsupported high
debug banks diagnose instead of truncating. No DWARF/debugger rewrite is included.

Missing references/bodies, malformed metadata, unsupported operations/shapes and
static capacity failures produce diagnostics with member/IL offset and source
location when available. Supported runtime checks use documented fault paths;
they are not catchable CLR exceptions. Reachable unsupported code cannot silently
disappear, return a placeholder or panic because the input is user-controlled.

Local unfiltered validation passes2239/2239, including2062 compiler cases, with
zero ignored/skipped and separate successful doctests. These are actual Roslyn,
IR/machine, SDK, game and debugger tests, with independent literal/math/hardware
observations; they do not make the old compiler or emulator initialization a
specification. See [current evidence](progress.md) for final platform results and
known limits. This contract is not a claim of physical-hardware conformance.
