# Native static and array alignment

The canonical `Koh.GameBoy.KohAlignedAttribute(int)` applies to nonliteral static
fields. Metadata must contain one positive power of two from 1 through 4096,
without named arguments. Invalid targets, constructor signatures and payloads
produce diagnostics. Referenced attributes are resolved by assembly identity.

For primitive scalar fields the attribute aligns their WRAM0 address. Padding
counts against the existing 256-byte static storage capacity. The ELF section
records the required alignment; the linker owns final placement.

For primitive array fields it aligns the mutable payload, rather than the
two-byte reference slot or length header. Allocation advances to
`align_up(cursor + 2, alignment) - 2` before reserving header and payload. Padding
counts against the 4096-byte bank-1 heap and remains untouched. Capacity is checked
before changing the cursor or writing the allocation. Payload bytes retain normal
zero initialization. Empty arrays still have distinct headers.

The compiler follows local stores and SSA block arguments back to allocations in
the assigning method and applies the strongest required alignment. Assignments
from factory calls or external aliases currently diagnose explicitly: changing
identity by copying an existing array would violate managed reference semantics.
This restriction is temporary, visible, and does not establish general support
for interprocedural heap placement.

The real `Video.Init` uses aligned MapWriter and Sprites arrays. Rust-owned
Debug/Release × DMG/CGB tests execute it through the native compiler and existing
emulator, checking complete poisoned heap/VRAM/OAM snapshots, palette registers,
return value, stack restoration and CGB bank restoration. This proves initialization,
not sprite rendering, LCD-on timing or a complete game.

Pointer alignment expressions also exercise mixed CIL native-int/int32 operands.
Non-shift arithmetic and bitwise operations use native width when either operand
is native; shift counts accept native values. Koh's existing native width is
16 bits. Ten independent CPU cases check both operand orders and signed/unsigned
division/remainder, multiplication, bitwise operations and native shift counts.
