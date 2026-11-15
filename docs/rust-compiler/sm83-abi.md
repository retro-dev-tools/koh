# Native SM83 ABI (initial integer implementation)

Functions use dynamic hardware-stack activation frames. Per-function static frames
would alias ancestor locals during recursion. A separate software stack would need
additional interrupt-safe pointer publication; SP-relative frames avoid that shared
state. The first allocator spills SSA values into deterministic frame slots.
Register residency and lifetime-based slot reuse are optimizations, not semantics.

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

On callee entry, SP addresses the two-byte machine return address. The caller-owned
return buffer follows, then packed arguments in declaration order. Each value uses
its evaluation width (int32: four bytes, int64: eight, native: two), little-endian.
Void has no return bytes. The callee reserves its frame below SP, initializes its
entry parameters from the argument block, and writes its result to the caller's
return buffer before restoring SP and executing RET. All registers/flags are
caller-clobbered. SP and caller storage survive the call.

[Unmanaged aggregate values](aggregates.md) occupy their complete metadata layout
in argument/return buffers. Typed aggregate references occupy two bytes. A value
constructor uses the ordinary void-call ABI, with `this` as the first reference
argument. Newobj allocates a compiler-private local per IL site, resets it before
the call and loads an independent value afterward. These locals live in the
same guarded activation frame and cannot be addressed by CIL local indices.

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

The frame contains locals, SSA values and temporary storage for simultaneous block
argument transfers and arithmetic. Temporary PUSH AF/POP AF pairs preserve carry
across SP-relative address calculations; their two bytes are included in capacity
checks. No arithmetic helper shares fixed WRAM scratch. Byte carry chains cannot
depend on flags surviving address generation unless explicitly preserved.

[Pinned pointer scopes](pinned-pointers.md) add per-invocation pin flags and a
saved SVBK byte to the guarded frame. Live heap pins reserve bank1 for native bus
access; the final release or method return restores the caller's register.
Unbanked frame/static pins leave it unchanged. Known explicit SVBK writes reachable
from a pinned method diagnose; bank selection belongs in its caller. Native-zero
pin cleanup is checked and nonzero fabrication aborts with fault8.

The initial stack region is WRAM0 `$C100..$CFFF`, with startup SP `$D000`.
Frame guards run before allocation/writes; a stack failure branches to
`__koh_trap_stack`. A caller must reserve/check its complete argument/return block
and the two-byte CALL return address before CALL. Integer divide-by-zero and signed
division overflow branch to `__koh_trap_arithmetic`. Trap entry does not return.
Signed remainder of minimum/-1 is zero. Hardware/native stack overflow is not
allowed to become a successful ROM build or silent WRAM corruption.

Static integer calls and recursion are now exercised by the [call increment](calls.md).
Interrupts, banking and the wider runtime remain incomplete. Interrupt support must reserve its worst-case
stack budget before enabling IME and preserve interrupted registers/flags; IRQ
recursion/nesting constraints need separate tests. The linker-visible stack UNION
reservation is implemented; startup/globals/heap must still be integrated.
The current debugger's heuristic stack scan cannot distinguish spill bytes from
return addresses; accurate frame inspection needs integration evidence.

Emission uses `koh_objects::Object` and its ELF writer. Code and internal branch
targets are symbols/relocations; ROM placement, bank capacities, header/checksums
belong to `koh-link`. Initial functions are ROM0 code. Banked calls/data and
Portable PDB mappings remain later requirements in this same PR.

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

Rust tests serialize emitted objects to ELF, read them back, link with `koh-link`,
and invoke a minimal C# host around the unchanged emulator. The host initializes
test-owned output/guard memory, steps with a fixed bound and reports memory/SP;
all behavioral expectations and assertions are in Rust. No game DLL is executed.
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
