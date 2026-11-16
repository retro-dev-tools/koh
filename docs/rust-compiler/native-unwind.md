# Native source-breakpoint stack inspection

The production compiler uses the [banked call ABI](rom-banking.md). A debugger
cannot distinguish its saved return addresses from spilled integers by scanning
stack words. Its stack starts at D000, not the boot ROM's FFFE. Register contents
are caller-clobbered and cannot identify a caller's frame.

Three approaches were considered:

- Scanning plausible stack words adds no metadata, but invents callers from data
  and cannot recover the saved ROM bank. It is unsuitable for native frames.
- A runtime frame-pointer chain adds instructions, frame storage and shared-state
  publication rules for interrupts and partially executed prologues.
- Exact-PC unwind records add no ROM instructions or frame storage. Existing ELF
  and KDBG symbols can carry them without introducing another object/debug format.

The third approach is implemented. It describes the production banked convention
only. The standalone near-call emitter used by backend tests is not an additional
production ABI; it emits no unwind records.

## Record contract

An ordinary local label named
`__koh_unwind_v1_b_DDDD_<method-symbol>_<unique-index>` describes the machine state
at that exact label address and bank. `DDDD` is four hexadecimal digits giving
the nonnegative byte distance from current SP to this function's entry SP.
The suffix is nonempty and distinguishes labels with otherwise equal recipes.
The `__koh_unwind_` namespace is reserved for compiler metadata.

Records are emitted at function entry, surviving IL instruction/terminator
boundaries and exact managed-call resume addresses. Ordinary body records use
the allocated frame size. Resume records additionally include argument and result
storage that the caller has yet to release; the consumed bank cookie is excluded.
This distinction is necessary when unwinding through a suspended caller.

For entry SP `E`, the banked call layout is:

| Address | Word |
| --- | --- |
| E | ROM0 `__koh_far_return` stub |
| E + 2 | Caller resume PC |
| E + 4 | Saved caller ROM bank, little-endian u16 |

After the callee and return stub complete, the caller's SP is `E + 6`.
The debugger uses that SP and the caller's exact resume-address recipe for the
next step. It never reuses the current hardware bank for an older caller.

## Validation and unknown states

The debugger requires a label of the supported version/kind at the exact bank/PC.
Identical duplicate recipes are harmless; conflicting or malformed recipes stop
unwinding. It checks the C100–D000 stack region, distance and cookie bounds, the
actual return-stub symbol, ROM address/bank capacity and strictly increasing SP.
Frame count is bounded at32. KDBG v1's existing bank limit remains255; the native
linker explicitly rejects debug information outside that representation.

No nearest-label interpolation is allowed. Prologue/epilogue interiors, temporary
push/pop sequences, runtime helpers, far-call/return transitions, interrupts and
unknown assembly may lack recipes. The debugger returns only the proven frame
prefix at such PCs. Arbitrary-instruction unwinding is not claimed. Source
breakpoints and saved call-resume addresses are the verified inspection points.
Assembly-only sessions retain their existing best-effort path; its assumptions
are not part of the native ABI.

The compiler includes record names and overhead in the per-function metadata
budget and the cumulative64MiB emission budget, even without PDBs. These labels
remain out of the debugger's user-facing Symbols and Source Context scopes.

## Tests

Rust drives the existing C# DebugSession/DAP through a thin fixture process.
Debug/Release ROMs place managed functions at the same4000 address in different
banks, verify source-mapped caller chains and recursion, inspect actual ROM bytes
using the selected bank, and resume to independently expected result42 with SP
restored. DMG and CGB execution are represented.

Eighteen hand-built states test absent, future, malformed, conflicting and duplicate
records; unsupported near records; stack limits; corrupt stub/PC/bank cookies;
and the frame-count bound. Separate Rust cases check cumulative marker accounting
without PDBs, the exact budget boundary and arithmetic overflow. No C# compiler
implementation or managed test framework supplies the assertions.

The real2048 SDK cases verify physical source breakpoints, the ordered framework/
entry-point caller chain and resumed named-function breakpoints in Debug/DMG and
Release/CGB. Final edited Rust debugger cases pass11/11 in99.619s. The existing
noncompiler-owned debugger regression suite separately passes37/37, zero skips.
Final hosted/cross-platform validation remains a whole-migration gate.
