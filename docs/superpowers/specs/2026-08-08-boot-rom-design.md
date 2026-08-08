# Boot ROM support

**Date:** 2026-08-08
**Status:** design, approved for planning

## Why

Commit `5fc842e` removed the HLE boot handoff from `GameBoySystem`: the emulator no
longer fabricates the state a real boot ROM leaves behind. It powers on at `$0000`
with every register zero and RAM `$FF`-poisoned, and it deliberately shipped with no
boot ROM mechanism at all — no overlay, no `$FF50` latch, no file loading.

That leaves the emulator unable to run any real cartridge: nothing initialises `SP`,
so the first `CALL` wraps to `$FFFF` and clobbers `IE`. The fix is not for the
toolchain to emit a reset stub — on hardware `$0000–$00FF` *is* the boot ROM, and a
cartridge that boots without one is the same fabrication `5fc842e` deleted, relocated
into the linker. The fix is to execute a real boot ROM.

Koh ships its own, written in Koh assembly and built by Koh's own toolchain, and
accepts an external dump (the stock Nintendo one, or SGB/AGB) as a substitute.

## Non-goals

- No HLE "skip boot" path. If you want the machine at `$0100`, run a boot ROM.
- No redistribution of Nintendo's boot ROM, and no download helper for one. External
  blobs are supplied by the user at runtime and never copied into the repo or a
  repo-tracked cache.
- No SGB or AGB boot ROMs authored by Koh. Their sizes are accepted (they match the
  DMG and CGB shapes), so a user dump works; Koh only authors DMG and CGB.
- No boot ROM required at the `Koh.Emulator.Core` layer. See "Layering" below.

## Layering: why the boot ROM is not a constructor parameter

`new GameBoySystem` appears at 67 call sites across 59 files. The large majority are
`Koh.Compiler.Tests` and `Koh.Emulator.Core.Tests` cases that construct the machine
and then drive `Pc`/`Sp` directly to exercise one instruction or one compiled
function. Those tests must **not** have a boot ROM execute first.

So "won't run without a boot ROM" is an application-layer policy, not a core-class
invariant, which is also what every comparable emulator does (SameBoy, mGBA, ares all
load the boot ROM as a step after the machine exists). `GameBoySystem`'s constructor
is unchanged; a boot ROM is inserted afterwards. This also dissolves a mode-resolution
problem: the caller cannot know which boot ROM family it needs until the cartridge
header has been read, and that resolution lives inside the constructor
(`GameBoySystem.cs:49`). Construct first, read `system.Mode`, then load the matching
blob.

## Components

### 1. `Koh.Emulator.Core/Boot/BootRom.cs`

A value type wrapping the bytes, so an invalid blob is unrepresentable past the
boundary and `Mmu` consumes something already validated.

```
enum BootRomFamily { Dmg, Cgb }

sealed class BootRom
    static BootRom FromBytes(ReadOnlySpan<byte> bytes)
    BootRomFamily Family { get; }
    ReadOnlySpan<byte> Bytes { get; }
    ulong Fingerprint { get; }
```

A sealed class, not a `readonly struct`: a struct cannot hold a `ReadOnlySpan<byte>`
field, and a struct over a `byte[]` field reintroduces a `default(BootRom)` with a null
array — exactly the invalid state this type exists to eliminate. As a class,
`BootRom?` is naturally nullable and every instance that exists is valid.

`FromBytes` copies the input rather than aliasing it, so a caller reusing its buffer
cannot mutate a mapped boot ROM.

Family is derived from **length**, not from the caller's declared hardware mode:

| Length          | Family | Covers                          |
| --------------- | ------ | ------------------------------- |
| `0x100` (256)   | `Dmg`  | DMG, DMG0, MGB, SGB, SGB2       |
| `0x900` (2304)  | `Cgb`  | CGB, CGB-E, AGB                 |
| anything else   | —      | rejected                        |

Length-driven means an unrecognised but correctly-sized dump just works, with no table
of known hashes to maintain. Rejection names both valid sizes and the size received.
Hash-matching to *name* a known dump in the UI is possible later, as cosmetics only,
never as a gate.

### 2. `Mmu` — the overlay

`Mmu` holds a `BootRom?`. `ReadByteInternal` case `0x0` (`Mmu.cs:141`) consults it
ahead of `_cart.ReadRom(address)`:

- `Dmg`: overlay covers `address < 0x100`.
- `Cgb`: overlay covers `address < 0x100 || (address >= 0x200 && address < 0x900)`.

The `$0100–$01FF` gap in the CGB case is deliberate and load-bearing: it is the
cartridge header, and it must stay visible to the boot ROM that is reading the logo,
the title, and `cartType` out of it. Both ranges fall inside case `0x0`, so no other
routing case changes.

Writes are never intercepted — `$0000–$08FF` writes go to the cartridge (MBC
registers) exactly as they do today, on hardware and here.

### 3. `IoRegisters` — the `$FF50` latch

`$FF50` currently exists only as a write-only classification (`IoRegisters.cs:253`,
so reads return `$FF`). Add a `case 0xFF50:` to `IoRegisters.Write` (line 260) that,
on any **non-zero** value, records the unmap. Because reads of `$FF50` are already
forced to `$FF` by that classification, the flag can live in `_io[0x50]` itself — and
`IoRegisters.WriteState` already serialises `_io` wholesale (line 428), so save-state
support for the latch costs nothing.

The latch is one-way: once set it is never cleared for the life of the machine. There
is no path back to a mapped boot ROM, matching hardware.

`Mmu`'s overlay check gates on this flag.

### 4. `GameBoySystem.LoadBootRom`

```
public void LoadBootRom(BootRom rom)
public bool BootRomMapped { get; }
```

Validates `rom.Family` against the already-resolved `Mode` — `Dmg` family for
`HardwareMode.Dmg`, `Cgb` for `HardwareMode.Cgb` — and rejects a mismatch, because a
CGB machine running a 256-byte boot ROM is not a device that exists and silently
accepting it produces baffling behaviour. Then hands the blob to `Mmu`.

Calling it after execution has begun is a programming error and throws; the boot ROM
must be in place before the first tick.

### 5. `src/Koh.Boot` — Koh's own boot ROMs

A new library project holding `dmg_boot.asm` and `cgb_boot.asm`, written in the
repo's existing RGBDS-style assembly (same dialect as `samples/gb-2048/src`),
assembled at build time by `Koh.Asm` + `Koh.Link`, and exposed as:

```
public static class KohBootRoms
    public static ReadOnlySpan<byte> Dmg { get; }
    public static ReadOnlySpan<byte> Cgb { get; }
```

`Koh.Boot` references nothing from the emulator. `Koh.Emulator.App`, `Koh.Verify`,
and the test projects reference `Koh.Boot`. `Koh.Emulator.Core` does not — it stays a
pure executor that takes bytes. The build graph is a DAG:
`Koh.Core → Koh.Emit → Koh.Asm`/`Koh.Link → Koh.Boot → Koh.Emulator.App`.

Build integration: a target that runs before `CoreCompile`, invoking the built
`Koh.Asm`/`Koh.Link` via `dotnet exec`, writing the `.bin` outputs into
`$(IntermediateOutputPath)` and adding them as `EmbeddedResource`. Build ordering is
forced with `<ProjectReference ... ReferenceOutputAssembly="false" />` on the two CLI
projects.

*Fallback if that proves fragile on a clean CI build:* check in the assembled `.bin`
files and add a test that re-assembles from source and asserts byte-equality. This
keeps the build graph flat at the cost of two committed binaries. Note this is an
exception to the repo's "don't commit built ROMs" rule and should be taken only if
needed.

### 6. Linker — exact-size output

`RomWriter.BuildRom` already takes `minSize`, and both `FixHeaderChecksum` and
`FixGlobalChecksum` already bail on `rom.Length < 0x0150` (`RomWriter.cs:65,79`) — so
a 256-byte image needs no new checksum logic and must not get any. The one real gap is
`NextPowerOfTwo(maxAddr)`, which would round the 2304-byte CGB image up to 4096.

Add `bool padToPowerOfTwo = true`. When false, `romSize = maxAddr` with `minSize` as a
floor and no rounding. Surface it on `Koh.Link` as `--raw` (disable padding) and
`--size=<n>` (set the floor), alongside the existing `-o`/`--sym`/`--kdbg`.

### 7. `Koh.Emulator.App` — selection policy

`Program.cs` already parses `--flag=value` prefixes (lines 18–33). Add:

- `--boot-rom-dmg=<path>` / `--boot-rom-cgb=<path>` — per-family slots. Needed because
  mode is resolved per cartridge, so one path is not enough for a session that opens
  both a DMG and a CGB game.
- `--boot-rom=<path>` — single override applied to whichever family the loaded
  cartridge resolves to. The common one-shot case.
- Nothing supplied for a family → `KohBootRoms`.

`EmulatorApp`, `HeadlessRunner`, and `Koh.Verify/RomHarness` construct the system,
read `system.Mode`, resolve the blob, and `LoadBootRom` before the first frame.

A persistent config file with the same two slots is deliberately deferred.

### 8. Save states

The `$FF50` latch rides along free in `_io` (see §3). Additionally, `Mmu.WriteState`
(line 306) records `BootRom.Fingerprint` — a 64-bit FNV-1a over the blob's bytes, no
crypto dependency, no `System.Security.Cryptography` reference — and `ReadState`
refuses a state whose fingerprint does not match the currently-loaded blob (`0` means
no boot ROM was loaded, and matches only `0`). A state captured mid-boot is meaningless
against different boot code, and failing loudly beats resuming into a machine that
never ran the code its state implies.

## The boot ROM behavioural contract

Koh's boot ROMs are an independent implementation, not a byte-for-byte clone of
Nintendo's. What they owe is the observable handoff state.

**On screen:** the full ~2.5-second logo scroll and the two-note chime, then unmap.
This is the honest replacement for the `BootLogo.cs`/`ArmBootAnimation` fabrication
that `5fc842e` deleted — the logo now reaches VRAM because code put it there. It costs
~150 frames on App/Verify/Compat runs; the compiler and CPU tests never load a boot
ROM, so they are unaffected.

**Validation:** both the Nintendo logo (`$0104–$0133`) and the header checksum
(`$014D`) are checked, and a failure locks up, exactly as on hardware.
`Sm83Backend.BuildHeader` already emits a correct logo (`Sm83Backend.cs:941`) and
`RomWriter` fixes the checksum, so every Koh-produced ROM passes — and a hand-written
test ROM that would hang on a real Game Boy hangs here too, which is the point. The
48-byte reference logo is duplicated into the boot ROM's assembly source; it already
exists in `Sm83Backend.NintendoLogo` for the cartridge-header side.

**Register state at handoff to `$0100`:**

| Mode                 | AF     | BC     | DE     | HL     | SP     |
| -------------------- | ------ | ------ | ------ | ------ | ------ |
| DMG                  | `01B0` | `0013` | `00D8` | `014D` | `FFFE` |
| CGB, CGB cartridge   | `1180` | `0000` | `FF56` | `000D` | `FFFE` |
| CGB, DMG cartridge   | `1180` | `0100` | `FF56` | `000D` | `FFFE` |

**I/O state at handoff:** `LCDC=$91` (LCD on, BG on), `BGP=$FC`, `OBP0`/`OBP1=$FF`,
`NR52=$F1` (DMG), VRAM cleared apart from the logo tiles and tilemap entries the boot
ROM wrote. CGB additionally initialises the background palettes and, for a DMG
cartridge, selects a compatibility palette.

### Known limitation: `DIV` and open-bus I/O at handoff

`DIV` at handoff is whatever the boot ROM's own cycle count produces. Koh's boot ROM
will not take the same number of cycles as Nintendo's, so it will not hand off the
same `DIV` value. Mooneye's `boot_div-*` and `boot_hwio-*` acceptance tests assert the
stock values and can therefore only pass with a real dump loaded — which is a correct
outcome of executing rather than fabricating, and is precisely the kind of divergence
the no-HLE stance exists to make visible rather than hide. This must be documented
where the compat suites are registered, not silently skipped.

## Testing

**`Koh.Emulator.Core.Tests/BootRomTests.cs`** — mechanism, using small hand-written
stub blobs, no dependency on `Koh.Boot`:

- `BootRom.FromBytes` accepts `0x100` → `Dmg` and `0x900` → `Cgb`; rejects `0xFF`,
  `0x101`, `0x800`, and empty, with the size named in the diagnostic.
- `LoadBootRom` rejects a `Dmg` blob on a CGB system and vice versa.
- Reads of `$0000–$00FF` come from the blob while mapped, from the cartridge after.
- CGB: `$0100–$01FF` reads the cartridge header even while mapped; `$0200–$08FF` reads
  the blob.
- A non-zero write to `$FF50` unmaps; a zero write does not; a second write cannot
  re-map.
- Save-state round-trip preserves the latch; a state with a mismatched boot ROM hash
  is refused.

**`Koh.Boot.Tests/KohBootRomTests.cs`** — the contract above:

- Run Koh's DMG boot ROM against a Koh-built cartridge to handoff; assert the register
  table, `SP=$FFFE`, `PC=$0100`, the latch set, the overlay gone, `LCDC`/`BGP`, and
  logo tiles present in VRAM.
- Same for CGB, both with a CGB cartridge and a DMG one.
- Corrupt the logo → the machine never reaches `$0100` within 600 frames (~10s, well
  past the ~150-frame scroll). Same for a corrupted `$014D`.

**Unchanged:** `PowerOnStateTests` still asserts the no-boot-ROM contract (all
registers zero, RAM `$FF`-poisoned) — that path is not going away, it is just no longer
the only one.

**Updated to load a boot ROM:** `MooneyeTests`, `BlarggTests`,
`BlarggDmgSoundTests`, `Acid2Tests`, and `Koh.Verify/RomHarness` — all of which
currently construct a system and immediately `RunFrame()` with no `PC` set.

Note these compat suites cannot verify anything in a fresh checkout:
`tests/fixtures/` does not exist, so every case hits `Skip.Test("ROM missing")`. They
become real verification only after `scripts/download-test-roms.sh`, which the
implementation must actually run rather than treating a green skip as a pass.

## Acceptance

1. `dotnet build Koh.Ci.slnf` — 0 warnings.
2. `dotnet msbuild build.proj -t:Test` green.
3. `scripts/download-test-roms.sh`, then the Mooneye/Blargg/Acid2 suites run
   (not skip) and are no worse than before `5fc842e`. Specifically: Mooneye's
   `boot_regs-*` should now **pass** on Koh's own boot ROM, since the register table
   above is exactly what it asserts — that is the cheapest external check that the
   handoff contract is right. `boot_div-*`/`boot_hwio-*` stay dump-only and must be
   registered with that reason stated, not silently omitted.
4. `dotnet run --project samples/gb-2048-cs` shows the logo scroll, plays the chime,
   and reaches the game.
5. The same ROM under an external stock dump via `--boot-rom=` also reaches the game.
