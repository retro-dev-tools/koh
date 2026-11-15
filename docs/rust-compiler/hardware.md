# Metadata-driven hardware access

Native compilation recognizes `Koh.GameBoy.KohIntrinsicAttribute` through its
resolved assembly/type identity. The marker remains a C# library type; no C# code
runs while compiling. A type with the same simple name in an unrelated assembly
does not acquire hardware semantics.

The attribute constructor contract is `(string kind, int address)`. Its blob must
have the ECMA custom-attribute prolog, a bounded UTF-8 serialized string, a signed
32-bit address and zero named arguments, with no trailing bytes. Malformed known
payloads diagnose even on unreachable declarations. Unknown intrinsic kinds may
exist in unused library code but diagnose if reached.

Implemented call-site contracts:

| Kind | Static signature | Native behavior |
| --- | --- | --- |
| register | `byte Read()` | One byte read, zero-extended to the CIL stack type |
| register | `void Write(byte value)` | One byte write, truncating to eight bits |
| region | Pointer/native-integer return, no parameters | Constant 16-bit bus address |
| di/ei/halt/nop/stop | `void()` | Observable [CPU instruction](cpu-control.md), no address |
| oamdma | `void(byte page)` | Blocking [HRAM DMA routine](oam-dma.md), no address |
| alloc | `byte*(int size)` | Checked [raw arena allocation](raw-arena.md), no address |
| heapreset | `void()` | Release raw blocks while preserving managed arrays, no address |
| memfill | `void(byte*, byte, ushort)` | Exact ascending [byte fill](runtime-memory.md), no address |
| memcopy | `void(byte*, byte*, ushort)` | Ordered [byte copy](memory-copy.md), including overlap; no address |

Addresses must fit 0000..FFFF. A property marker applies to its accessor methods;
a method marker can define a direct hardware alias. Multiple matching method and
property markers are errors. Instance or generic intrinsic methods are rejected.
The supported register contract deliberately matches the library's byte register
API. A region pointer does not promise support for every possible pointed-to type.

Call lowering emits typed IR for the corresponding effect, rather than a call
to the desktop body. Consequently `Gb`'s pinned desktop buffer and `Hardware`'s
desktop static state/initializers are not embedded in a ROM. Ordinary reached
methods use [real initializer lowering](initializers.md); this exception belongs solely
to the explicit intrinsic contract. There is no duplicate compiler address table.

Memory effects remain ordered and are not eliminated as unused results. VRAM and
MMIO access use the real SM83 bus; LCD timing restrictions still apply. Interrupt
handlers remain pending and produce diagnostics. Reentrant allocation is unsupported.
These temporary omissions are not feature retirements.

## Pointer arithmetic and target width

Mixed native/int32 addition and subtraction explicitly convert the int32 operand
to the target native width before using the existing integer IR. Fixed-width
integer operations retain their established semantics. The accepted CIL operand
combinations follow the [documented add rules](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes.add?view=net-10.0).

Koh deliberately represents unmanaged pointers and native integers with 16 bits,
matching the SM83 bus. This is a target ABI extension, not general CLI native-size
compatibility: ECMA-335 describes 32/64-bit native integers. Pointer/native arithmetic
wraps modulo 65536. Games requiring wider arithmetic should use fixed-width
`int`/`long`. Maintaining wider pointer temporaries would cost RAM/instructions
while hardware dereferences still use 16 address bits. Typed-pointer scaling and
additional mixed arithmetic shapes need their own acceptance evidence; this
increment specifically checks byte-pointer addition and subtraction.

## Evidence and traceability

The Hardware Roslyn fixture uses the actual `Hardware.LCDC/BGP/SCX/SCY` and
`Gb.Vram/TileData/TileMap` APIs in Debug and Release. It disables the LCD before
VRAM writes, checks register readback and little-endian VRAM data, uses positive
and negative byte-pointer offsets, and returns 42 with balanced SP and intact
memory guards in the existing C# emulator. It also checks custom method aliases
and a region whose desktop body throws. Reachability must contain only Main;
the native CLI separately emits and links the same program.

The prior PROPERTY guard test rejected a then-unimplemented valid register. It
now rejects an unknown intrinsic kind; the valid contract is covered by the
positive execution tests above. No assertion that hardware must fail was retained
as a permanent product contract. Negative Roslyn variants cover address overflow,
invalid register/region signatures, null kind, duplicate annotations, unknown
control kinds and uncalled interrupt roots. Native unit tests cover truncation,
prolog, string encoding, named arguments and trailing data. Four independent scalar
cases check mixed-width wrapping/negative results.

This advances the frontend audit's intrinsic/runtime and VRAM pointer requirements.
It does not retire a whole legacy compiler test method, prove LCD-on access timing,
or establish game graphics/debugger acceptance.
