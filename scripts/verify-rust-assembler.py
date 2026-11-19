#!/usr/bin/env python3
"""Verify native ELF linking against independent RGBDS 1.0.1 and emulator execution."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / "target/release" / ("koh-test-host.exe" if os.name == "nt" else "koh-test-host")

def run(args, *, expect=0):
    result = subprocess.run(list(map(str, args)), cwd=ROOT, text=True, capture_output=True)
    if result.returncode != expect:
        raise AssertionError(f"Command returned {result.returncode}, expected {expect}: {args}\n{result.stdout}{result.stderr}")
    return result

EXECUTION = '''SECTION "Entry", ROM0[$100]
    jp Start
SECTION "Code", ROM0[$150]
Start:
    ld sp, $fffe
    ld hl, $c000
    ld a, 40
    add a, 2
    ld [hl+], a
    call Increment
    ld [hl], a
Done:
    jr Done
Increment:
    inc a
    ret
'''

RELOCATIONS = '''SECTION "Code", ROM0
Start::
    jp Target
    ld hl, Buffer
    ld a, HIGH(Buffer)
    ldh [Scratch], a
    dw Target + 2
    jr .next
.next:
    nop
Target::
    ret
SECTION "Buffer", WRAM0
Buffer:: ds 4
SECTION "Scratch", HRAM
Scratch:: ds 1
'''

MACROS = '''DEF COUNT EQU 3
MACRO emit
    db \\1
ENDM
SECTION "Macro", ROM0[$200]
REPT COUNT
    emit $42
ENDR
IF COUNT == 3
    dw $1234
ELSE
    db 0
ENDC
    db "Koh", LOW($1234), HIGH($1234)
'''

OPCODES = '''SECTION "Instructions", ROM0[$200]
    nop
    stop
    halt
    di
    ei
    ld bc, $1234
    ld de, $2345
    ld hl, $3456
    ld sp, $fffe
    ld [$c000], sp
    ld a, [bc]
    ld [de], a
    ld a, [hl+]
    ld [hl-], a
    ld b, 42
    ld [hl], 43
    ldh [$ff80], a
    ldh a, [$ff81]
    ldh [c], a
    ldh a, [c]
    ld hl, sp + 4
    add sp, -4
    ld sp, hl
    push af
    pop af
    inc bc
    dec de
    inc [hl]
    dec a
    add hl, bc
    add a, b
    adc a, [hl]
    sub c
    sbc a, 1
    and d
    xor e
    or h
    cp l
    rlca
    rrca
    rla
    rra
    daa
    cpl
    scf
    ccf
    rlc b
    rrc c
    rl d
    rr e
    sla h
    sra l
    swap [hl]
    srl a
    bit 7, [hl]
    res 2, a
    set 4, b
    jp nz, $200
    jp hl
    call z, $200
    jr nc, @
    ret c
    reti
    rst $38
'''


def adapt_sample(directory):
    destination = directory / "gb2048-rgbds"
    shutil.copytree(ROOT / "samples/asm/gb-2048/src", destination, dirs_exist_ok=True)
    changes = []
    for path in sorted(destination.rglob("*")):
        if not path.is_file():
            continue
        source = path.read_text()
        adapted, macros = re.subn(r"(?m)^([ \t]*)([A-Za-z_.$][\w.$]*):[ \t]*MACRO\b", r"\1MACRO \2", source)
        adapted, constants = re.subn(r"(?m)^([ \t]*)([A-Za-z_.$][\w.$]*)[ \t]+(EQU|EQUS)[ \t]+", r"\1DEF \2 \3 ", adapted)
        if macros or constants:
            path.write_text(adapted)
            changes.append(dict(file=str(path.relative_to(destination)), macros=macros, constants=constants))
    (directory / "gb2048-reference-adaptations.json").write_text(json.dumps(changes, indent=2))
    return destination / "main.asm"

def compare_case(name, sources, directory, assembler, linker, reference_sources=None, execute=False, pin_sample=False, host=HELPER):
    native, reference = [], []
    for index, source in enumerate(sources):
        elf = directory / f"{name}-{index}.elf.o"
        rgb = directory / f"{name}-{index}.rgb.o"
        run([assembler, source, "-o", elf])
        if elf.read_bytes()[:4] != b"\x7fELF":
            raise AssertionError("Native output is not ELF")
        reference_source = (reference_sources or sources)[index]
        run(["rgbasm", "-I", str(reference_source.parent) + "/", "-o", rgb, reference_source])
        native.append(elf)
        reference.append(rgb)
    reference_rom = directory / f"{name}-rgbds.gb"
    native_rom = directory / f"{name}-native.gb"
    map_path = directory / f"{name}-rgbds.map"
    run(["rgblink", "-p", "0", "-m", map_path, "-o", reference_rom, *reference])
    if pin_sample:
        run([linker, "-o", directory / f"{name}-floating.gb", *native])
        placements = {}
        for line in map_path.read_text().splitlines():
            region = re.match(r"(ROM0|ROMX|VRAM|SRAM|WRAM0|WRAMX|OAM|HRAM) bank #(\d+):", line)
            if region:
                kind, bank = region[1], int(region[2])
            section = re.search(r'SECTION: \$([0-9a-fA-F]+).*\["([^"]+)"\]', line)
            if section:
                placements[section[2]] = (kind, bank, int(section[1], 16))
        tree = reference_sources[0].parent
        def pin(match):
            kind, bank, address = placements[match[1]]
            bank_option = f", BANK[{bank}]" if kind in ("ROMX", "VRAM", "SRAM", "WRAMX") else ""
            return f'SECTION "{match[1]}", {kind}[\u0024{address:04x}]{bank_option}'
        for path in tree.rglob("*"):
            if path.is_file():
                path.write_text(re.sub(r'^SECTION\s+"([^"]+)"[^\n]*', pin, path.read_text(), flags=re.M))
        native = [directory / f"{name}-pinned.elf.o"]
        run([assembler, reference_sources[0], "-o", native[0]])
    expected = reference_rom.read_bytes()
    run([linker, "--raw", f"--size={len(expected):x}", "-o", native_rom, *native])
    if native_rom.read_bytes() != expected:
        raise AssertionError(f"{name}: complete raw ROM differs from RGBDS")
    if execute:
        print(run([host, "AssemblerVerify", "execute", native_rom]).stdout.strip())
        print(run([host, "AssemblerVerify", "execute", reference_rom]).stdout.strip())
    return dict(name=name, bytes=len(expected), status="passed", placement="RGBDS layout pinned in temporary sources; floating image also built" if pin_sample else "unconstrained")

def negative_cases(directory, assembler, linker):
    cases = {
        "unknown-opcode": 'SECTION "Bad", ROM0\nbogus\n',
        "invalid-register": 'SECTION "Bad", ROM0\nld q, a\n',
        "bit-range": 'SECTION "Bad", ROM0\nbit 8, a\n',
        "jr-range": 'SECTION "Bad", ROM0[$100]\njr $4000\n',
        "undefined-symbol": 'SECTION "Bad", ROM0\njp MissingSymbol\n',
        "division-zero": 'SECTION "Bad", ROM0\ndb 1 / 0\n',
        "unmatched-endc": 'SECTION "Bad", ROM0\nENDC\n',
        "recursive-macro": 'MACRO recurse\nrecurse\nENDM\nSECTION "Bad", ROM0\nrecurse\n',
        "duplicate-global": 'SECTION "Bad", ROM0\nDuplicate:\nDuplicate:\n',
    }
    results = []
    for name, text in cases.items():
        source = directory / f"negative-{name}.asm"
        source.write_text(text)
        for tag, asm, link in [("native", assembler, linker), ("rgbds", "rgbasm", "rgblink")]:
            obj = directory / f"negative-{name}-{tag}.o"
            command = [asm, source, "-o", obj] if tag == "native" else [asm, "-o", obj, source]
            result = subprocess.run(list(map(str, command)), cwd=ROOT, text=True, capture_output=True)
            phase = "assemble"
            if result.returncode == 0:
                phase = "link"
                result = subprocess.run(list(map(str, [link, "-o", obj.with_suffix(".gb"), obj])), cwd=ROOT, text=True, capture_output=True)
            results.append(dict(name=name, tool=tag, phase=phase, exit=result.returncode, stderr=result.stderr))
            if result.returncode == 0 or not result.stderr.strip():
                raise AssertionError(f"{name}: {tag} accepted invalid input or omitted diagnostics")
    return results

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--rust", type=Path, default=ROOT / "target/release/koh-asm")
    parser.add_argument("--linker", type=Path, default=ROOT / "target/release/koh-link")
    parser.add_argument("--host", type=Path, default=HELPER)
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    for tool in ("rgbasm", "rgblink"):
        if run([tool, "--version"]).stdout.strip() != f"{tool} v1.0.1":
            parser.error(f"{tool} v1.0.1 is required")
    if not options.no_build:
        run(["cargo", "build", "--release", "--locked", "-p", "koh-asm", "-p", "koh-link", "-p", "koh-test-host"])
    directory = (options.output or Path(tempfile.mkdtemp(prefix="koh-native-verify-"))).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    results = []
    for name, text in [("execution", EXECUTION), ("relocations", RELOCATIONS), ("macros", MACROS), ("opcodes", OPCODES)]:
        source = directory / f"{name}.asm"
        source.write_text(text)
        results.append(compare_case(name, [source], directory, options.rust, options.linker, execute=name == "execution", host=options.host))
    results.append(compare_case("semantics", [ROOT / "tools/rust-asm-verify/fixtures/reference-semantics.asm"], directory, options.rust, options.linker))
    results.append(compare_case("gb2048", [ROOT / "samples/asm/gb-2048/src/main.asm"], directory, options.rust, options.linker, [adapt_sample(directory)], pin_sample=True))
    consumer, provider = directory / "consumer.asm", directory / "provider.asm"
    consumer.write_text('SECTION "Consumer", ROM0[$100]\njp Target\ndw Target + 3\ndb HIGH(Target), BANK(Target)\n')
    provider.write_text('SECTION "Provider", ROMX[$4000], BANK[2]\nTarget::\nret\n')
    results.append(compare_case("cross-file", [consumer, provider], directory, options.rust, options.linker))
    negatives = negative_cases(directory, options.rust, options.linker)
    (directory / "results.json").write_text(json.dumps(dict(workflows=results, negatives=negatives), indent=2) + "\n")
    for result in results:
        print(f"PASS {result['name']}: {result['bytes']} bytes")
    print(f"PASS {len(negatives)} negative tool pipelines; artifacts: {directory}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
