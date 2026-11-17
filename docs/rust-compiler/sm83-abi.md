# Native SM83 ABI

Functions use dynamic hardware-stack activation frames. Per-function static frames
would alias ancestor locals during recursion. A separate software stack would need
additional interrupt-safe pointer publication; SP-relative frames avoid that shared
state. The first allocator spills SSA values into deterministic frame slots.
Register residency and lifetime-based slot reuse are optimizations, not semantics.

[Native unwind records](native-unwind.md) describe exact source-breakpoint and
call-resume states through existing ELF/KDBG symbols, without runtime frame changes.

[Strings](strings.md) use a distinct16-bit ROM0 reference: u16 UTF-16 unit count,
then u16 units, all little-endian. Program-wide literal interning preserves
reference identity across calls and assemblies. Accessors check null/full-width
indices and never switch WRAM banks. String fields/arguments/results use two
bytes; Length/char results retain their int32 evaluation/call widths.

[Reference classes](class-layout.md) use distinct typed16-bit references into
the shared checked WRAM-bank1 arena. Each allocation starts with a two-byte
ROM0 descriptor address, followed by packed inherited and declared fields.
The descriptor contains its parent descriptor address, a u16 interface count,
then that many u16 interface descriptor addresses. System.Object's parent and
count are zero. Standard ELF symbols/relocations let the linker place descriptors.
Allocation zeroes fields and installs identity before constructor execution.
Reference-valued fields/static storage/array elements use typed two-byte cells;
raw memory loads cannot invent managed reference identities. Calls and returns
pass the reference itself, preserving aliases. Null access faults with code4;
arena exhaustion faults with code5. Heap accesses preserve CGB SVBK.
[Class virtual dispatch](virtual-dispatch.md) uses typed synthetic functions and
ordinary banked calls; direct base calls remain direct. Exact runtime descriptor
tests select implementations, and checked receiver casts follow parent links
without changing reference identity. Invalid runtime type faults use code10.
[Interface/explicit MethodImpl dispatch](interfaces.md) reuses this calling ABI;
interface casts test transitive membership in the runtime descriptor.

[OAM DMA](oam-dma.md) uses a linker-placed8-byte HRAM routine installed once at
boot. Its source page is passed in A; no shared WRAM scratch is required. The
CALL return word is pushed before DMA and read after completion. Current calls
require IME0; startup DI establishes that state. HRAM capacity/relocation belong
to the linker, and unreferenced DMA reserves nothing.

[HBlank streams](raster-stream.md) use HL source, B line, C count and D data.
Setup reserves two transient stack bytes; the timing loop uses no stack or calls.
Nonempty streams leave IME disabled; zero count leaves IME unchanged. Invalid
line ranges use fault6; LCD-off nonempty streams use hardware fault9.

Frame word transfers use BC and may clobber A, DE, HL and flags. Bank-switch
helpers likewise clobber flags; no current caller depends on incoming flags
surviving them. Byte loads/stores used inside arithmetic carry chains still preserve
AF. Small fields can advance BC directly after the managed-null check. Locals
larger than eight bytes live after the hot small-slot/scratch area, preserving
stable local addresses and total frame capacity while reducing long SP-relative
address sequences. Local order in memory is not a game-facing contract.

The [enum/stack increment](stack-enums.md) adds conservative zero-upper-byte
spill compression. Evaluation widths and call/return buffers below remain
unchanged; physical spills may use fewer bytes, with explicit extension at
consumers. Address-exposed storage retains its accessible width. Fixed acyclic
stack buffers live in each invocation's guarded frame and are zeroed before use.

Production cartridge calls use [banked ROM calls](rom-banking.md): on callee entry,
SP addresses a two-byte ROM0 return-stub address, followed by a four-byte caller
PC/bank cookie. The caller-owned return buffer follows, then packed arguments in
declaration order. Low-level standalone emission tests use near calls without
the cookie. Each value uses
its evaluation width (int32: four bytes, int64: eight, native: two), little-endian.
Void has no return bytes. The callee reserves its frame below SP, initializes its
entry parameters from the argument block, and writes its result to the caller's
return buffer before restoring SP and executing RET. All registers/flags are
caller-clobbered. SP and caller storage survive the call.

[Aggregate values](aggregates.md) occupy their complete metadata layout
in argument/return buffers. Typed aggregate references occupy two bytes. A value
constructor uses the ordinary void-call ABI, with `this` as the first reference
argument. Newobj allocates a compiler-private local per IL site, resets it before
the call and loads an independent value afterward. These locals live in the
same guarded activation frame and cannot be addressed by CIL local indices.
Sequential aggregates may contain two-byte class, string and array reference
cells, with nested inline values. Copying the aggregate copies these cells,
preserving shared referents rather than cloning heap allocations. Local, static,
class-field and array-element storage use the same layout. Explicit layouts with
managed references and scoped byref fields are currently rejected. Reference-cell
indirection requires the declared cell type; it is not a native integer load/store.

Primitive managed references also occupy two bytes and retain their pointee type
in IR. [Local/argument addresses](local-references.md) refer to stable storage in
the owning invocation, including ancestor frames during recursion. Null managed
dereferences abort through `__koh_trap_null` (fault 4); native bus address zero
remains legal. Managed references to the current frame cannot be returned.

[Primitive array references](arrays.md) occupy two bytes and point at a packed
count/payload allocation in linker-reserved WRAMX bank 1. Managed-reference memory
operations select that bank and restore SVBK; native pointers retain raw selected
bank semantics. Internal allocation calls reserve their extra machine return word
as well as AF scratch in the guard. Heap exhaustion and bounds abort with faults
5 and 6. This does not establish interrupt safety, GC or ROM banking.

[Raw allocations](raw-arena.md) return native16 bank1 bus pointers, preserving
SVBK. The same4KiB arena holds upward managed allocations and downward raw blocks.
`__koh_heap_cursor` and `__koh_raw_used` are independent startup-zeroed16-bit
offset/count words in WRAM0; their sum cannot exceed4096. `Mem.Reset` clears raw
usage only. Native pointer access remains the caller's bank responsibility.

The frame contains locals, SSA values and temporary storage for simultaneous block
argument transfers and arithmetic. Temporary PUSH AF/POP AF pairs preserve carry
across SP-relative address calculations; their two bytes are included in capacity
checks. No arithmetic helper shares fixed WRAM scratch. Byte carry chains cannot
depend on flags surviving address generation unless explicitly preserved.

[Pinned pointer scopes](pinned-pointers.md) add per-invocation pin flags and a
saved SVBK byte to the guarded frame. Live heap pins reserve bank1 for native bus
access; the final release or method return restores the caller's register.
Unbanked frame/static pins leave it unchanged. Known explicit SVBK writes and
transitive bank-writing calls diagnose while a pin may be active. Selection
before acquisition or after final release is allowed. Native-zero
pin cleanup is checked and nonzero fabrication aborts with fault8.

The initial stack region is WRAM0 `$C100..$CFFF`, with startup SP `$D000`.
Frame guards run before allocation/writes; a stack failure branches to
`__koh_trap_stack`. A caller must reserve/check its complete argument/return block
and the two-byte CALL return address before CALL. Integer divide-by-zero and signed
division overflow branch to `__koh_trap_arithmetic`. Trap entry does not return.
Signed remainder of minimum/-1 also faults, matching the chosen division-overflow
behavior required by C# remainder semantics. Hardware/native stack overflow is not
allowed to become a successful ROM build or silent WRAM corruption.

Static integer calls and recursion are now exercised by the [call increment](calls.md).
Interrupts, banked data and the wider runtime remain incomplete. Interrupt support must reserve its worst-case
stack budget before enabling IME and preserve interrupted registers/flags; IRQ
recursion/nesting constraints need separate tests. The linker-visible stack UNION
reservation, startup, globals, bounded heap and banked calls are integrated.
The current debugger's heuristic stack scan cannot distinguish spill bytes from
return addresses; accurate frame inspection needs integration evidence.

Emission uses `koh_objects::Object` and its ELF writer. Code and internal branch
targets are symbols/relocations; ROM placement, bank capacities, header/checksums
belong to `koh-link`. Banked calls use the documented ROM0 return stub/cookie.
Banked data and Portable PDB mappings remain requirements in this same PR.

Plan: execute literal arithmetic/ABI cases in the C# emulator, then real Roslyn
Debug/Release integer methods; keep unsupported emission diagnostic until each
operation has a correct SM83 implementation. No test-only emulator replaces the
existing production emulator core.

## Executed integer slice

The backend implements every operation currently in the initial integer IR:
constants, local storage, add/subtract, bit operations, multiply, signed/unsigned
division/remainder, shifts, comparisons, conversions, negation/not, edge transfers
and return. Division is restoring long division with an overflow bit retained for
the remainder; multiplication is width-bounded shift/add. Signed helpers normalize
magnitudes and restore signs. No integer arithmetic is delegated to C#.

Small multiplication magnitudes use an8x8 register loop with a16-bit product;
larger operands keep the width-bounded loop. A local helper per function/width
uses the existing25-byte scratch region: inputs0/8, result16, sign24. The CALL
return shifts its SP-relative addresses by2; the entry guard reserves4 scratch
bytes for that return and its temporary push. This is an internal same-bank
routine, not a managed call or another preserved-register convention. See
[semantic proof and execution measurements](magnitude-multiply.md).

The address-less `memfill` contract is exact `void(byte*,byte,ushort)` metadata.
IR stores address as native16 and value/count as int32; the emitter truncates
the latter to8/16 bits, captures them before writing and performs ascending
byte writes through DE with a BC counter. A four-byte temporary guard covers
operand capture. It preserves selected banks, supports zero count and wraps
bus addresses modulo65536; it adds no managed bounds or PPU scheduling. See
[memory contract and independent observations](runtime-memory.md).

The address-less `memcopy` contract is exact `void(byte*,byte*,ushort)` metadata.
The emitter captures count's low16 bits and both native pointers before buffer
access, using two temporary pushes covered by the four-byte guard. DE is the
destination, HL source, BC count; each ascending read precedes its write.
Both pointers wrap at16 bits and zero count touches neither buffer. All working
registers are temporary under the spilled-value ABI. No implicit bank or PPU
changes occur; see [copy contract and alias tests](memory-copy.md).

`vblankcopy` uses the same native pointer/count capture and guarded temporary
pushes, but returns a zero-extended16-bit copied count. Nonempty calls disable
interrupts, validate LCD-on state and copy a bounded VBlank prefix; zero count
preserves IME and touches no hardware/buffer. Its gate reserves instruction
latency as well as the two-line tail. See [exact timing and tests](vblank-copy.md).

Rust tests serialize emitted objects to ELF, read them back, link with `koh-link`,
and invoke a minimal C# host around the unchanged emulator. The host initializes
test-owned output/guard memory, steps with a fixed bound and reports memory/SP;
hardware expectations and assertions are in Rust. The compiler never executes
game DLLs; separate desktop fixtures check ordinary CLR library behavior.
The fixture uses ordinary Roslyn output with Portable PDBs; matching PDB reading
and source breakpoints are still incomplete.

The initial suite executes 140 ROM scenarios: 30 arithmetic cases, 54
comparison/shift/conversion/unary cases, 36 division-boundary/fault cases, one
cyclic phi transfer, one guarded-stack failure and 18 real C# scenarios across
Debug/Release. Ten executions intentionally trap; the other 130 must return the
expected value with balanced SP. Guard sentinels must survive all runs.
This is CPU/memory evidence, not game graphics, interrupts, recursion or hardware
validation. These scenarios do not establish final language compatibility.

ELF retains method symbols and local IL-offset markers. Relocation diagnostics
use explicitly virtual CIL locations, not fabricated C# source locations. PDB-to-
machine source spans and KDBG function/frame metadata remain required.
The emitter caps the byte stream at 16 KiB while emitting, rejects oversized
frames/call storage, and verifies IR before selecting instructions. It never
returns a partially emitted object after a capacity failure.

Review found diagnostic IL-offset overflow and test output/build-directory races;
both are fixed. The max-offset regression failed before its fix. Default-threaded
focused tests pass as well as serial execution. No legacy compiler test is retired
by this initial backend slice; the full migration remains incomplete.

Local command: `KOH_DOTNET=/path/to/dotnet cargo test -p koh-compile --test sm83`.
Workspace acceptance continues to use `cargo nextest run --workspace --all-features --locked`
and separate `cargo test --doc --workspace --all-features --locked`.

Instruction encoding/flag reference: [gbdev opcode data](https://github.com/gbdev/gb-opcodes/blob/master/Opcodes.json).

Pure scalar modular addition recursion may become an [accumulator loop](accumulator-recursion.md).
Its source identity/offsets remain, but eliminated calls have no physical stack
frames. Other recursion retains this checked ABI and its capacity limits.

## Binary32 transport

`F32` uses four-byte raw binary32 storage and the existing four-byte argument and
return convention. Explicit `I32`/`F32` reinterpretation copies bits without a
numeric conversion. Addition/subtraction/multiplication/division call metadata-selected
`f32.add`/`f32.sub`/`f32.mul`/`f32.div`
managed CIL helpers with raw `uint(uint, uint)` arguments/results. Negation flips
the sign bit without a call. Comparisons call `f32.cmp`, with raw
`byte(uint,uint)` signature and equal/less/greater/unordered codes0/1/2/3;
the caller applies the CIL ordered/unordered predicate. See [float lowering](float-lowering.md)
for the currently implemented operations and remaining limitations.

Binary32/integer32 conversion calls carry one four-byte argument/result. Metadata
signatures distinguish signed integers from raw unsigned bits; IR transport uses
`I32`. Native16 operands extend with the selected instruction signedness. See
[conversion semantics](float-conversions.md) for unchecked overflow/NaN and
narrowing choices; this does not change the calling convention.
Binary32/integer64 conversion uses eight-byte arguments or results on the integer
side and the same existing integer64 caller storage. No binary64 value or
intermediate is introduced by those helpers.

`F64` uses the same eight-byte storage/calling mechanism with distinct float IR
identity. Metadata `f64.add`/`f64.sub` helpers carry two raw unsigned64 values and
return one; `f64.cmp` returns byte classification. Negation flips the sign bit.
See [binary64 contract and limits](binary64.md); unsupported precision conversion
never becomes an implicit truncating integer operation.
