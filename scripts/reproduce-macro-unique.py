#!/usr/bin/env python3
"""Check reconstructed macro-unique fixtures against the current ELF/RGBDS pipelines."""
import argparse
import json
from pathlib import Path
import re
import runpy
import tempfile

ROOT = Path(__file__).resolve().parents[1]
WAIT = r"""WAIT_VBLANK: MACRO
.wait\@:
    ldh a, [$FF80]
    and a
    jr z, .wait\@
ENDM
"""


def fixtures(sample):
    common = 'DEF TILE_FONT_SPACE EQU $20\n' + WAIT + sample + '\n'
    section = 'SECTION "Main", ROM0[$100]\nMain:\n'
    return {
        'reconstructed-definition-following-macros': common + section
        + 'TILE_ROW $0F,$F0\nFILL_BYTES 2,$A5\nFILL4_HL $42\nFILL4_HL_A\n',
        'reconstructed-wait-and-following-macros': common + section
        + 'WAIT_VBLANK\nWAIT_VBLANK\nTILE_ROW $0F,$F0\nFILL_BYTES 2,$A5\n'
        + 'FILL4_HL $42\nFILL4_HL_A\nGRID_CELLROW 1,2\n'
        + 'farcall 1,FarcallTrampoline\nFarcallTrampoline: ret\n',
        'realistic-nested-repeat': common + r"""WAIT_TWICE: MACRO
 REPT 2
  WAIT_VBLANK
 ENDR
 TILE_ROW \1,\2
ENDM
""" + section + 'WAIT_TWICE $0F,$F0\nWAIT_TWICE $AA,$55\n',
        'existing-at-label-fixture': r"""loop_body: MACRO
call .done\@
nop
.done\@:
ENDM
SECTION "Main", ROM0
main:
loop_body
""",
        'existing-no-global-anchor-fixture': r"""MACRO outer
.label\@:
    nop
ENDM
SECTION "Main", ROM0
outer
outer
outer
""",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rust", type=Path, default=ROOT / "target/release/koh-asm")
    parser.add_argument("--linker", type=Path, default=ROOT / "target/release/koh-link")
    parser.add_argument("--output", type=Path, default=Path(tempfile.gettempdir()) / "koh-macro-unique-investigation")
    options = parser.parse_args()
    options.output.mkdir(parents=True, exist_ok=True)
    verify = runpy.run_path(str(ROOT / "scripts/verify-rust-assembler.py"))
    sample = (ROOT / "samples/asm/gb-2048/src/macros.inc").read_text()
    results = []
    for name, source in fixtures(sample).items():
        asm = options.output / f"{name}.asm"
        asm.write_text(source)
        reference = options.output / f"{name}-rgbds.asm"
        reference.write_text(re.sub(r"(?m)^([A-Za-z_][A-Za-z_0-9]*):\s*MACRO\b", r"MACRO \1", source))
        if name == "existing-no-global-anchor-fixture":
            elf, rom = options.output / f"{name}.o", options.output / f"{name}.bin"
            verify["run"]([options.rust, asm, "-o", elf])
            verify["run"]([options.linker, "--raw", "--size=3", "-o", rom, elf])
            assert rom.read_bytes() == bytes(3), "Unanchored unique-label fixture changed"
            rejection = verify["run"](["rgbasm", "-o", options.output / f"{name}-rgbds.o", reference], expect=1)
            assert "Unqualified local label" in rejection.stderr
            results.append(dict(name=name, status="native extension; RGBDS rejection expected"))
        else:
            results.append(verify["compare_case"](name, [asm], options.output, options.rust, options.linker, [reference]))
    (options.output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
    for result in results:
        print(f"{result['name']}: {result['status']}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
