# Rust linker migration validation

Historical PR54 evidence follows. Compiler-specific C# commands below describe
that baseline; PR55 replaces them with native tests. See the current
[compiler disposition ledger](../rust-compiler/test-disposition.md) and
[CI commands](../ci-validation.md).

This evidence covers the ELF32/SM83 migration in draft PR #54. The
[test-disposition matrix](test-disposition.md) records the pre-migration audit,
individual contracts, replacements and deletion reasons. Passing counts below
describe completed runs, not coverage equivalence.

## Architecture and removal checks

`koh-objects` owns the model, ELF implementation and numeric semantics;
`koh-asm` and `koh-link` share it. Compiler output crosses the documented native
JSON-to-ELF command boundary. Native assembler output retains its original ELF
image. No C# relocation evaluator or object serializer remains.

Removed production components include both `src/Link` projects, KobjReader,
KobjWriter, KobjFormat, RgbdsObjectWriter, RgbdsObjectFormat, PatchEntry,
PatchExpression and the deserialization-only EmitModel constructor. Synthetic
KDBG builders remain only under `tests/Support`, with active reader/DAP tests.

Repository searches found no production emitter, consumer, conversion or package
path for the retired formats. The only surviving format references outside
explicit historical documentation are rejection tests and the disposition audit.
Production restore graphs contain no `Koh.Link` or `Koh.Linker` project dependency.
Fresh build/publish trees contain neither retired linker assemblies nor `.kobj`
artifacts. A PE metadata inspection of published `Koh.Objects.dll` and
`Koh.Native.dll` found no retired object-format or linker implementation types;
EmitModel has one diagnostics-based constructor.

The fresh language-server publish includes `koh-asm`, `koh-link`, the Rust
dependency inventory and the added dependency license texts. Cartridge samples
and both boot ROM intermediates were inspected as ELF32 little-endian, EM_NONE,
Koh flags `0x4b4f4801`, rather than relying on their `.o` extension.

RGBDS object output had no evidenced external consumer requiring an adapter.
Reference verification now assembles the same fixtures independently with
rgbasm/rgblink. The benchmark's native producer/consumer use ELF; its unsupported
Pokecrystal feature gates remain explicit. Its obsolete managed-linker runtime
dependency was removed, without changing the separate managed benchmark suite.

## Completed local checks

Host checks used Linux x64 and .NET SDK 10.0.115. Builds ran serially per output
directory; the memory-hungry .NET suite used one test module and one test at a time.
Every reported run completed, its direct exit status was captured, and its
finished log or resulting artifacts were read again.

| Check | Observed result |
| --- | --- |
| RGBDS-enabled Cargo workspace | Exit 0, all tests passed after shared evaluator, inverse-map and LOAD source-map fixes |
| Rust formatting | Exit 0 |
| Release solution build | Exit 0, zero warnings/errors; all C# sample ROMs generated |
| Full .NET suite before final review corrections | Exit 0: 1,372 passed, 2 native-window skips, zero failures |
| Rebuilt fast .NET suite after initial managed review fixes | Exit 0: 561 passed, 2 native-window skips, zero failures |
| Compiler static-field/array class | Exit 0: 16 passed, including Debug/Release arrays crossing CFFF/D000 |
| Final object/native-boundary class | Exit 0: 10 passed, including debug opt-out and failed-compilation/ELF-image rejection |
| VS Code suite under isolated Xvfb | Exit 0: 44 passed; the initial run without a display failed and is not counted |
| Both root assembly sample targets | Exit 0; ROM, SYM and KDBG produced through ELF/native linking |
| Language-server publish | Exit 0; native tools, notices and absence of retired assemblies inspected |
| Independent pipeline verifier | Exit 0: seven workflows and eighteen negative tool pipelines |
| Macro reproduction verifier | Exit 0: five cases, including the documented native-only language extension |
| SDK incremental/path verifier | Exit 0; expected injected linker failure returned MSBuild exit 1 and preserved the existing ROM |
| Shell syntax checks | Exit 0; then-tracked container benchmark was not executed (obsolete harness retired in PR #55) |

The full local run predates the final focused corrections; it is not represented
as a full run of a later tree. Hosted full-suite results must correspond to the
current PR head. The PR remains draft at the user's request even when checks pass.
Native Windows/macOS checks exercise hosted runners, not physical desktop or
hardware acceptance.

Representative reproducible commands:

```sh
KOH_RGBDS_TEST=1 cargo test --workspace --locked
cargo +stable fmt --all -- --check
dotnet build Koh.Ci.slnf -c Release -m:1
dotnet test --solution Koh.Ci.slnf -c Release --no-build --max-parallel-test-modules 1 -- --maximum-parallel-tests 1
dotnet test --solution Koh.Fast.slnf -c Release --no-build --max-parallel-test-modules 1 -- --maximum-parallel-tests 1
dotnet test --project tests/Compiler/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj -c Release --no-build -- --maximum-parallel-tests 1 --treenode-filter '/*/*/CilStaticsTests/*'
dotnet msbuild build.proj -t:BuildSample2048 -p:Configuration=Release
python3 scripts/verify-rust-assembler.py
python3 scripts/reproduce-macro-unique.py
cargo nextest run -p koh-compile --test sdk
```

Capture each command's exit status directly and inspect its completed log; do not
pipe the command through another program to infer success. Rust-owned SDK tests
use fresh temporary projects and check paths with spaces, no-op builds, CGB/debug
properties, compiler/linker timestamps and selection, native process failures,
preserved old output, asset generation and actual ROM execution. The workspace
nextest command includes them on Linux, Windows and macOS. Unix additionally
checks parent-directory symbolic-link aliases; junction-specific Windows behavior
remains a separate validation limit. See the compiler SDK evidence for current
pass/failure scopes rather than treating these commands as completed acceptance.

## Independent ELF and execution evidence

GNU readelf recognized ELF32 REL, machine None, the Koh/SM83 note, PROGBITS/NOBITS,
real local/global/undefined symbols and RELA. An ALIGN[8] fixture had both a
256-byte file alignment and `sh_addralign=256`. The ABI does not claim a registered
SM83 machine number. [The official registry](https://gabi.xinuos.com/v42/elf/a-emachine.html)
assigns EM_NONE to "No machine" and has no SM83 assignment.

Pinned RGBDS 1.0.1 independently verifies opcode bytes, relocation arithmetic,
cross-object references, section combinations, relative bounds and CPU-address
wrapping. Unconstrained floating placement can legitimately differ. The assembly
2048 byte comparison pins only a temporary source copy to independently obtained
RGBDS placements; the unchanged floating native ROM was separately executed.

Both native and RGBDS execution-signature ROMs wrote WRAM C000=2A and C001=2B
after ten instructions in Koh.Emulator. Separately, mGBA Qt 0.11.0 ran the native
assembly 2048 and C# 2048-v2 ROMs for 600 frames each, muted and offscreen with
isolated ROM/configuration copies. Actual screenshots were inspected, and scripted
Start/directional inputs produced gameplay and score 20. Memory reads and copied
ROM hashes corroborate the observations. Details and artifact directories are
recorded in the disposition matrix. No tracked sample screenshot was changed.

## Historical C# / Rust / RGBDS comparison

The immutable baseline is `9a2cfa8b149af303f0663dbd3564dc8522adcbc4`.
An out-of-tree C# helper referenced that baseline's linker and constructed two
EmitModels in memory, without any KOBJ interchange or production dependency:

| Input | Contract |
| --- | --- |
| Consumer | ROM0 fixed at 0100, bytes C3 00 00, absolute16 patch at offset 1 for exported Target+3 |
| Provider | ROMX bank 2 fixed at 4000, bytes C9 00 00 00, exported Target at offset 0 |

The corresponding assembly inputs were:

```asm
SECTION "Consumer", ROM0[$100]
    jp Target + 3
```

```asm
SECTION "Provider", ROMX[$4000], BANK[2]
Target::
    ret
    ds 3, 0
```

Each producer/linker was run independently. Native raw output used
`--raw --size=10000`; RGBDS used `rgblink -p 0`, followed only by zero padding from
48 KiB to 64 KiB for raw-image comparison. All three raw images matched exactly:
`cbf4e4142c91a336e15d0b3fd60ccf7e0d683878d4b1de6acd00cf0722b26b05`.
All had C3 03 40 at 0100 and C9 00 00 00 at physical offset 8000.

The native cartridge matched `rgbfix 1.0.1 -p 0 -f hg` byte-for-byte:
`ce04a81f882442995a3f635c3c15b04529ea6431f544647266d25575fe9bd204`.
The old C# cartridge differed only at 0148 (00 instead of 01 for a 64 KiB ROM)
and its resulting header checksum at 014D (E7 instead of E6). Every output's
header/global checksum was checked independently. The native result follows the
actual image size; the incorrect old size byte was not copied for compatibility.
The fixture report, helper source and all twelve direct tool outcomes remain in
`/tmp/koh-three-linkers`; the baseline source checkout remained clean.
This is evidence for that fixture, not a claim of complete linker equivalence.

## Deliberate limits

ABI 1 accepts canonical Koh ELF layout and rejects unsupported/equivalent alternate
layouts explicitly. KDBG version 1 cannot represent banks above 255 or absolute
symbol values outside 0–65535; requesting that debug output fails instead of
truncating. ROM/SYM linking remains available without KDBG. Cartridge mapper
selection and runtime bank switching remain program responsibilities. No DWARF,
GUI, optimizer, CIL-frontend or debugger-internals migration was introduced.
