# OAM DMA

`Hardware.RunOamDma(byte sourcePage)` and metadata-equivalent aliases lower to
native Rust-generated code. The contract is address-less `KohIntrinsic("oamdma")`
(address sentinel -1), static nongeneric `void(byte)` with the default managed
calling convention. Other addresses/signatures diagnose with method/IL context.
The desktop body is not included or executed by the compiler.

The page selects the hardware source at `page * 256`. The intrinsic consumes the
low eight bits of an int32 evaluation value, matching byte parameter storage.
IR `OamDma` has no result and is observable: it survives dead-value elimination
and invalidates local forwarding knowledge. It does not select the source ROM,
WRAM or SRAM bank; the caller must hold the intended mapping through the transfer.
Pinned heap pointers already select bank1 and restore the caller's WRAM bank.

## CPU and memory contract

[PanDocs](https://raw.githubusercontent.com/gbdev/pandocs/master/src/OAM_DMA_Transfer.md)
specifies 160 M-cycles and HRAM-only CPU access during DMG OAM DMA. The routine
must trigger and wait from HRAM; interrupts must be prevented during the transfer.
The compiler's startup disables interrupts; CPU control and bounded interrupt
roots now have separate coverage. This intrinsic requires IME0 and does not change IME.
It does not implicitly wait for vblank; callers such as `Sprites.Flush` own
display timing. Portable source pages are normally 00–DF; raw hardware mapping
rules still apply. This is a blocking operation, unlike the raw FF46 register.

One reachable intrinsic reserves 8 bytes in an ELF HRAM section and emits 8 ROM0
source bytes: `E0 46 3E 28 3D 20 FD C9`. In SM83 terms: trigger FF46 from A,
load 40, decrement/branch until zero, then RET. The wait after the trigger occupies
161 M-cycles before RET, covering the existing emulator's startup delay as well.
The caller loads A before CALL; the return address is pushed before DMA starts,
and popped only after the bus is available. A/flags are clobbered. The existing
two-byte per-frame scratch guard covers the argument loader's PUSH/POP and the
subsequent CALL; those stack uses never overlap.

The Rust linker chooses the HRAM address. Both boot installation and call sites
refer to its symbol; no absolute FF80 convention or second layout pass exists.
Startup copies the bytes once before entry, including recursive entry calls.
The old shared WRAM page scratch and unconditional fixed 11-byte HRAM reservation
are unnecessary and are not reproduced. No DMA means no runtime reservation or
installation. HRAM exhaustion is a linker error. Unsafe raw writes must respect
compiler-owned storage, as with the stack and other runtime allocations.

## Validation

Rust driver tests build ordinary C# in Debug/Release, serialize/read canonical
ELF, link it and execute in the existing C# emulator on DMG/CGB. They cover:

- Two actual transfers through a managed helper and a differently named
  metadata alias, with exact 160-byte OAM/source snapshots and bank restoration.
- Linker relocation around existing HRAM reservations, including the final
  usable 8-byte range ending at FFFE, and explicit exhaustion diagnostics.
- A test-only observer requiring HRAM PC and IME0 while DMA locks the bus;
  a shortened wait-counter mutation must fail that observer.
- Exact signature/address rejection and signed int32 page input retaining its
  low byte; IR verifies typing, no result and optimizer observability.
- Untouched VRAM and alternate WRAM banks, guards and balanced SP on return.

The raw `Hardware.DMA` property is tested separately. The observer stops CPU
execution immediately after the FF46 write, then advances the existing DMA
device 700 T-cycles. It checks register readback, completed transfer and all 160
bytes. This intentionally partial execution tests asynchronous register semantics;
it is not evidence that ROM code may continue fetching during the bus lock.
It also proves an unreferenced blocking intrinsic does not allocate HRAM.

These raw cases port both OamDmaTrigger methods in
`CilSpritePaletteHardwareTests.cs`. Their 6 assertion sites expand to 488 checks
across 3 original executions (Debug/Release plus their comparison). The native
cases use independent expected data in both configurations and both targets,
with whole-source guards. The remaining register and desktop-library methods now
have [native sprite/palette replacements](sprite-contracts.md).
Production emulator code is unchanged; only the external fixture observes it.

## Historical pre-banking graphics probe

The following failure predates banked managed calls. The current real showcase
passes all four Debug/Release × DMG/CGB cases in the
[sprite runtime regression](sprite-contracts.md); do not treat this old probe as
the current sample status.

The unchanged `samples/csharp/gb-gfx-demo/Game.cs` was compiled by Roslyn in an
isolated standard SDK project, then accepted by `koh-compile` after this fix.
The ELF contains 48073 ROM0 bytes across 80 allocated ROM sections (`readelf -S`),
exceeding 16 KiB; `koh-link` correctly rejects placement. This remains a known
sample acceptance failure requiring banked code/data support, not a passing game
or a retired example. The isolated project is a language probe, not native SDK
integration. See [progress](progress.md) for exact logs and completed test scopes.
