# Pinned array pointers and the WRAM bank window

The native compiler accepts pinned locals containing a primitive/enum managed
reference or a primitive/enum vector. This covers Roslyn's element-address and
whole-array `fixed` forms. Other pinned shapes retain explicit type diagnostics.
Heap allocations are immobile; pinning does not introduce a garbage collector.
ECMA-335 II.7.1.2 requires the referenced object to remain in place while pinned;
see the [standard](https://www.ecma-international.org/wp-content/uploads/ECMA-335_5th_edition_december_2010.pdf).

## Why bank ownership is necessary

Managed array operations select heap bank1 for each access and restore SVBK.
A native pointer is a 16-bit bus address, so a pointer into `$D000..$DFFF` cannot
independently encode its heap bank. Ignoring the pinned modifier would let a
graphics copy read bank2 after its caller selected that bank. It would compile
successfully and copy the wrong bytes.

Moving the heap into WRAM0 would reduce the existing heap/stack capacities;
changing all native pointers into tagged wider values would change the entire
bus and call ABI. Instead, a live heap pin maps bank1 for the duration of its
scope. Native copy/fill routines therefore operate on the same array allocation.
Frame/static addresses are unbanked and do not acquire a bank-window pin.

Each invocation reserves one flag per pinned local and one saved-bank byte in
its guarded frame. The first heap pin saves SVBK; additional pins share that
mapping. Releasing the last heap pin restores the saved register. Cleanup order
does not need to be LIFO. Method return also restores a live mapping, covering
early returns and Release IL that omits an explicit final local clear. Recursive
and nested calls own separate flags and save/restore the bank they inherited.
Null pins do not map a bank; an empty whole-array `fixed` produces a null pointer.

Pinning temporarily changes the observable SVBK register on CGB. Code must not
remap the WRAM window while a heap-derived native pointer is in use. The compiler
conservatively rejects a pinned method if it or a transitive callee contains a
known explicit write to SVBK. Bank selection in its caller is supported. This
whole-method restriction can reject bank changes before or after the lexical
`fixed` scope; it avoids pretending to solve arbitrary pointer/bank aliasing.
Raw dynamic bus writes remain unsafe and must respect the reserved mapping,
just as they must respect stack and heap reservations. This is not ROM banking,
interrupt/reentrancy support or permission to use a pointer after its pin ends.

## IR and ABI checks

`StorePinned` explicitly preserves pin-local identity through CFG simplification
and spilling. Ordinary stores may initialize a pinned local to zero exactly once
at entry; subsequent writes must use the pin operation. The verifier checks local
indices before indexing state, pointee types, initialization and result shape.
Managed-frame escape propagation includes pinned stores; a pin cannot disguise
a returned reference to the current invocation.

Roslyn clears a pinned byref with native zero. That input has a runtime zero
check; a nonzero value aborts with fault8 through `__koh_trap_pin`, rather than
fabricating a managed reference. Arrays use typed null clearing. Flag storage,
saved state and temporary AF pushes all participate in the existing frame guard.
No global pin scratch or second runtime implementation is introduced.

## Evidence and remaining work

Rust owns all assertions in `tests/{driver,ir,sm83}.rs`. Four Debug/Release ×
DMG/CGB driver cases compile real C# and assert that both pinned metadata shapes
are present. They execute28 ROM scenarios, including actual `Koh.GameBoy.Mem.Copy`
into LCD-off VRAM, array mutation, nested/recursive/loop pins, null/empty arrays,
unbanked references and restoration of a caller-selected CGB bank2. Full bank1
and bank2 snapshots detect alias corruption. Eight compile probes reject direct
or transitive known bank writes. No game-facing API or emulator core is changed.

Nine IR cases cover initialization/cleanup, malformed stores/indices/results and
managed-frame escape. One CPU test executes five native-clear scenarios: zero
returns42; four nonzero addresses trap8. Review identified and fixed a verifier
indexing-before-validation panic; its new regression requires a diagnostic.
The focused selector also includes an existing reservation test, so its15 cases
contain14 new tests. Full run evidence is recorded in `progress.md`.

This proves pinned pointer and copy behavior, not full `TileSet.Load`, DMA timing,
framebuffer graphics, a complete graphical sample, source debugging or SDK
replacement. Those remain requirements in this PR.
