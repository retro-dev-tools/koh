# Indirect integer memory and CFG simplification

The native frontend now lowers `ldind.i1/u1/i2/u2/i4/u4/i8/i` and
`stind.i1/i2/i4/i8/i`. Addresses are 16-bit native integers. Unmanaged pointer
signatures and native signed/unsigned integers use that representation; managed
byrefs, floating-point indirect operations and aggregate copies remain pending.
Accepting a pointer signature does not implement the pointed-to managed type.

IR memory operations carry an explicit width. Loads sign- or zero-extend to the
evaluation-stack type; stores truncate to their explicit width. The verifier
rejects incompatible addresses, widths and result/value types. Reads and writes
remain ordered operations. No alias-based elimination or motion is implemented.

The backend transfers bytes in increasing address order, least significant byte
first, without assuming alignment. Address increments wrap at 16 bits. Accesses
use the SM83 memory bus, including its hardware restrictions; an unsafe pointer
does not turn ROM into writable memory or bypass VRAM access timing. Multi-byte
transfers are not atomic. Interrupt support and its shared-memory contract remain
pending. Access to compiler frame storage through raw pointers is not a supported
memory ownership contract.

## Removing artificial block and frame costs

Initial lowering deliberately uses one block per CIL instruction to make stack
merges explicit. That form is verified, then unconditional chains with a unique
predecessor are merged. Eliminated block parameters are substituted in every use,
including memory addresses, calls and successor arguments. The pass preserves
instruction order, method identity and retained instruction IL offsets, and
verifies the resulting IR again. It neither folds arithmetic nor removes memory
effects. No general optimizer framework was added.

Stack layout reuses equal-width slots for temporaries whose uses remain within
their defining block. A slot stays occupied through the instruction that last
uses it. Block parameters and values used in another block keep separate storage.
Edge-transfer scratch, locals and arithmetic scratch remain separate. Contiguous
stack copies and constants avoid rebuilding an address for every byte.

## Independent checks

The Memory C# fixture writes and reads known 8/16/32/64-bit and native values at
unaligned WRAM addresses. It checks signed extension, unsigned extension,
little-endian byte order, overlapping writes and a pointer argument across a
managed call. Debug and Release both run through canonical ELF, the Rust linker
and existing C# emulator, with explicit failure codes for each assertion. Success
requires result 42, balanced SP and intact protected-memory guards.

The combined Debug fixture initially exceeded the ROM0 capacity diagnostic.
It was retained while redundant CFG transfers and repeated byte-address setup
were corrected. This is evidence for required code-size work, not a retired
Debug language shape. Further optimization and banked code remain required.

Ten parameterized verifier cases exercise valid and invalid memory widths/types.
Existing independent integer/control-flow expectations are also checked after
CFG simplification, including loops and joins; a second simplification must be
identical. Existing recursion, live caller values and cyclic block transfers
exercise storage separation. A boundary ROM test places frame SP at C102 so
the final legal scratch word is C100; one byte less must trap before writes.
Review caught a transient nested-push implementation that would need four scratch
bytes; contiguous memory transfers removed that extra push before acceptance.

These cover native memory/IR requirements identified by the legacy source audit,
but do not retire whole legacy test methods or establish arrays, object layout,
static initialization, hardware intrinsics or game-level acceptance.

Traceability: `frontend-audit.md` records `CilStructTests.PointerWriteToVram_ReadBack`
and `PointerArithmetic_OffsetWriteReadBack` as PORT requirements. The new tests
establish their underlying indirect integer access semantics in WRAM, including
stronger width and alias assertions. They do not yet discharge VRAM timing or
pointer arithmetic. `backend-audit.md`'s aliasing requirements motivate preserving
all memory effects; no memory-elimination pass is introduced here. Remaining
legacy scenarios stay open and executable in the existing suite until replaced.
