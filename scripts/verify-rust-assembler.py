#!/usr/bin/env python3
"""Compare native assembler artifacts with C# and RGBDS 1.0.1."""

import argparse
import hashlib
import json
import re
import textwrap
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
from assembler_reference import COMMIT as REFERENCE_COMMIT, REFERENCE, HELPER as REFERENCE_HELPER, ensure_reference


ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / "tools/rust-asm-verify/bin/Release/net10.0/koh-rust-asm-verify.dll"
RUST = ROOT / "target/release/koh-asm"


def run(args, *, expect=0):
    result = subprocess.run([str(arg) for arg in args], cwd=ROOT, text=True, capture_output=True)
    if result.returncode != expect:
        raise AssertionError(
            f"Command returned {result.returncode}, expected {expect}: {' '.join(map(str, args))}\n"
            f"{result.stdout}{result.stderr}"
        )
    return result.stdout


def helper(*args):
    executable=REFERENCE_HELPER if args[0] in ("assemble","corpus") else HELPER
    return run(["dotnet", executable, *args])


class RgbObject:
    def __init__(self, path):
        self.data = path.read_bytes()
        self.pos = 0
        self.sections = []
        self.symbols = []
        assert self.take(4) == b"RGB9"
        assert self.integer() == 13
        symbol_count, section_count, node_count = self.integer(), self.integer(), self.integer()
        for _ in range(node_count):
            self.take(8)
            kind = self.byte() & 127
            if kind:
                self.string()
            else:
                self.take(self.integer() * 4)
        for _ in range(symbol_count):
            name, kind = self.string(), self.byte()
            symbol = {"name": name, "visibility": {0: 0, 1: 2, 2: 1}[kind]}
            if kind != 1:
                self.take(8)
                symbol.update(section=self.integer(), value=self.integer())
            self.symbols.append(symbol)
        for _ in range(section_count):
            name = self.string()
            self.take(8)
            size, kind = self.integer(), self.byte()
            address, bank, align, align_offset = self.integer(), self.integer(), self.byte(), self.integer()
            section = dict(name=name, size=size, kind=kind, address=address, bank=bank,
                           align=align, align_offset=align_offset, patches=[])
            if kind & 7 in (2, 3):
                section["data"] = self.take(size).hex()
                for _ in range(self.integer()):
                    self.take(8)
                    offset, pc_section, pc_offset = self.integer(), self.integer(), self.integer()
                    patch_kind = self.byte()
                    rpn = self.take(self.integer()).hex()
                    section["patches"].append(dict(offset=offset, pc_section=pc_section,
                                                   pc_offset=pc_offset, kind=patch_kind, rpn=rpn))
            self.sections.append(section)
        self.assertions = []
        for _ in range(self.integer()):
            self.take(21)
            expression = self.take(self.integer()).hex()
            self.assertions.append(dict(rpn=expression, message=self.string()))
        assert self.pos == len(self.data), "Trailing RGBDS object bytes"
        for symbol in self.symbols:
            if "section" in symbol:
                index = symbol["section"]
                symbol["section"] = self.sections[index]["name"] if index >= 0 else None

    def take(self, count):
        if count < 0 or self.pos + count > len(self.data):
            raise AssertionError("Truncated RGBDS object")
        result = self.data[self.pos:self.pos + count]
        self.pos += count
        return result

    def byte(self):
        return self.take(1)[0]

    def integer(self):
        return struct.unpack("<i", self.take(4))[0]

    def string(self):
        end = self.data.index(0, self.pos)
        return self.take(end - self.pos + 1)[:-1].decode()


class KobjObject(RgbObject):
    def __init__(self, path):
        self.data, self.pos = path.read_bytes(), 0
        assert self.take(5) == b"KOH\x00\x04"
        assert self.byte() == 1
        sections = []
        for _ in range(self.short()):
            name, kind = self.string(), self.byte()
            address = self.integer() if self.byte() else None
            bank = self.integer() if self.byte() else None
            data = self.take(self.integer()).hex()
            patches = []
            for _ in range(self.short()):
                offset, patch_kind, pc_after = self.integer(), self.byte(), self.integer()
                self.take(8)
                symbol, addend, shift = self.string(), self.integer(), self.integer()
                patches.append(dict(offset=offset, kind=patch_kind, pcAfter=pc_after,
                                    symbol=symbol or None, addend=addend, shift=shift))
            lines = []
            for _ in range(self.integer()):
                lines.append(dict(offset=self.integer(), count=self.integer(),
                                  file=Path(self.string()).name, line=self.integer()))
            sections.append(dict(name=name, kind=kind, address=address, bank=bank,
                                 data=data, patches=patches, lines=lines))
        assert self.byte() == 2
        symbols = []
        for _ in range(self.short()):
            name, kind, visibility, section = self.string(), self.byte(), self.byte(), self.string()
            value = struct.unpack("<q", self.take(8))[0]
            symbols.append(dict(name=name, kind=kind, visibility=visibility, section=section or None, value=value))
        assert self.byte() == 255 and self.pos == len(self.data)
        self.model = dict(sections=sections, symbols=symbols)

    def short(self):
        return struct.unpack("<H", self.take(2))[0]

    def string(self):
        length, shift = 0, 0
        while True:
            byte = self.byte()
            length |= (byte & 127) << shift
            if byte < 128:
                return self.take(length).decode()
            shift += 7
            assert shift <= 28


def verify_corpus(directory, rust):
    corpus = directory / "corpus"
    corpus.mkdir(exist_ok=True)
    fixtures, excluded = [], []
    method_pattern = re.compile(r"public\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\([^)]*\)")
    emit_pattern = re.compile(r'var\s+(\w+)\s*=\s*Emit\(\s*("{3,})(.*?)\2\s*\)', re.S)
    for path in sorted((REFERENCE / "tests/Asm/Koh.Assembler.Tests").rglob("*.cs")):
        text = path.read_text()
        methods = list(method_pattern.finditer(text))
        for index, method in enumerate(methods):
            body = text[method.end():methods[index + 1].start() if index + 1 < len(methods) else len(text)]
            match = emit_pattern.search(body)
            if not match:
                continue
            name = f"{path.stem}-{method[1]}"
            if not re.search(rf"Assert\.That\(\s*{match[1]}\.Success\s*\)\.IsTrue\(\)", body):
                excluded.append(dict(name=name, reason="No positive success assertion"))
                continue
            source = textwrap.dedent(match[3]).strip("\n")
            output = corpus / f"{name}.asm"
            output.write_text(source)
            fixtures.append(str(output))
    manifest = corpus / "manifest.json"
    manifest.write_text(json.dumps(fixtures))
    references = json.loads(helper("corpus", manifest))
    results = []
    for reference in references:
        path = Path(reference["path"])
        if not reference["success"]:
            results.append(dict(name=path.stem, status="reference-rejected", diagnostics=reference["diagnostics"]))
            continue
        output = path.with_suffix(".rust.kobj")
        output.unlink(missing_ok=True)
        assembled = subprocess.run([str(rust), str(path), "-o", str(output)], cwd=ROOT, text=True, capture_output=True)
        if assembled.returncode:
            results.append(dict(name=path.stem, status="rust-rejected", diagnostics=assembled.stderr))
            continue
        try:
            compare_kobj(KobjObject(Path(str(path) + ".csharp.kobj")).model, KobjObject(output).model)
            results.append(dict(name=path.stem, status="passed"))
        except (AssertionError, ValueError) as error:
            results.append(dict(name=path.stem, status="different", diagnostics=str(error)))
    expected = json.loads((ROOT / "tools/rust-asm-verify/expected-differences.json").read_text())["fixtures"]
    for result in results:
        path = corpus / (result["name"] + ".asm")
        result["source_sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        entry = expected.get(result["name"])
        if entry and entry["source_sha256"] != result["source_sha256"]:
            result["classification_error"] = "Expected-difference source changed; review the classification"
            continue
        if result["status"] == "passed" or not entry:
            continue
        try:
            verify_expected_difference(entry, result, path, rust)
            result["classified_expected"] = True
            result["classification_reason"] = entry["reason"]
            result["regression"] = entry["regression"]
        except (AssertionError, ValueError) as error:
            result["classification_error"] = str(error)
    report = dict(results=results, excluded=excluded)
    (directory / "corpus-results.json").write_text(json.dumps(report, indent=2))
    counts = {status: sum(result["status"] == status for result in results)
              for status in ("passed", "reference-rejected", "rust-rejected", "different")}
    print(f"Existing raw Emit fixtures: {len(fixtures)}; {counts}; negative/nonasserted excluded: {len(excluded)}")
    classified = sum(result.get("classified_expected", False) for result in results)
    failures = sum(bool(result.get("classification_error")) or
                   (result["status"] != "passed" and not result.get("classified_expected")) for result in results)
    print(f"Classified expected differences: {classified}; unclassified failures: {failures}")
    for result in results:
        if result.get("classification_error") or (result["status"] != "passed" and not result.get("classified_expected")):
            print(f"  {result['status']}: {result['name']}: {result.get('classification_error', '')}")
    return failures


def kobj_snapshot(model):
    return dict(sections=sorted(({key: section[key] for key in
                                 ("name", "kind", "address", "bank", "data", "patches")}
                                for section in model["sections"]), key=lambda section: section["name"]),
                symbols=numeric_symbols(model["symbols"]))


def rgbds_snapshot(model):
    return dict(sections=sorted(model.sections, key=lambda section: section["name"]),
                symbols=numeric_symbols(model.symbols))


def legacy_reference_source(source):
    adapted = re.sub(r"(?m)^([ \t]*)([A-Za-z_.$][\w.$]*):[ \t]*MACRO\b", r"\1MACRO \2", source)
    return re.sub(r"(?m)^([ \t]*)([A-Za-z_.$][\w.$]*)[ \t]+(EQU|EQUS)[ \t]+", r"\1DEF \2 \3 ", adapted)


def verify_expected_difference(entry, result, path, rust):
    assert entry["reason"] and entry["regression"]
    regression = ROOT / entry["regression"].split("::")[0]
    assert regression.is_file(), f"Independent regression is absent: {regression}"
    if "::" in entry["regression"]:
        function = entry["regression"].split("::")[1]
        assert re.search(rf"\bfn\s+{re.escape(function)}\b", regression.read_text()), "Regression function is absent"
    compare(result["status"], entry["status"], "Expected-difference status changed")
    if entry["mode"] == "kobj":
        actual = kobj_snapshot(KobjObject(path.with_suffix(".rust.kobj")).model)
        compare(actual, entry["expected_graph"], "Expected native object invariants changed")
    elif entry["mode"] == "rgbds":
        assert entry["diagnostic_contains"] in result["diagnostics"]
        output = path.with_suffix(".classified.o")
        output.unlink(missing_ok=True)
        run([rust, path, "--format", "rgbds", "-o", output])
        actual = rgbds_snapshot(RgbObject(output))
        compare(actual, entry["expected_graph"], "Expected rich-object invariants changed")
        rom = path.with_suffix(".classified.gb")
        run(["rgblink", "-p", "0", "-o", rom, output])
        compare(hashlib.sha256(rom.read_bytes()).hexdigest(), entry["linked_rom_sha256"],
                "Expected rich-object linked ROM changed")
    elif entry["mode"] == "rejected":
        assert entry["diagnostic_contains"] in result["diagnostics"]
        reference = path.with_suffix(".reference.asm")
        reference.write_text(legacy_reference_source(path.read_text()))
        output = path.with_suffix(".reference.o")
        run(["rgbasm", "-o", output, reference], expect=1)
    else:
        raise AssertionError(f"Unknown classification mode: {entry['mode']}")


def compare(left, right, context):
    if left != right:
        if isinstance(left, bytes) and isinstance(right, bytes):
            differences = [(index, a, b) for index, (a, b) in enumerate(zip(left, right)) if a != b]
            raise AssertionError(f"{context}; sizes {len(left)}/{len(right)}, first differences {differences[:8]}")
        if isinstance(left, list) and isinstance(right, list):
            differences = [(a, b) for a, b in zip(left, right) if a != b]
            raise AssertionError(f"{context}; counts {len(left)}/{len(right)}, first differences {differences[:3]}")
        raise AssertionError(f"{context}\nC#: {left!r}\nRust: {right!r}")


def compare_kobj(left, right):
    for key in ("sections", "symbols"):
        left[key].sort(key=lambda item: item["name"])
        right[key].sort(key=lambda item: item["name"])
    compare(numeric_symbols(left["symbols"]), numeric_symbols(right["symbols"]), "Kobj symbols differ")
    compare(len(left["sections"]), len(right["sections"]), "Kobj section count differs")
    for a, b in zip(left["sections"], right["sections"]):
        for key in ("name", "kind", "address", "bank"):
            compare(a[key], b[key], f"Kobj section {a['name']} {key} differs")
        compare(len(a["data"]), len(b["data"]), f"Kobj section {a['name']} size differs")
        x, y = bytearray.fromhex(a["data"]), bytearray.fromhex(b["data"])
        for patch in a["patches"] + b["patches"]:
            size = {0: 1, 1: 2, 2: 4, 3: 1}[patch["kind"]]
            x[patch["offset"]:patch["offset"] + size] = bytes(size)
            y[patch["offset"]:patch["offset"] + size] = bytes(size)
        compare(x.hex(), y.hex(), f"Kobj section {a['name']} bytes differ")
        for patch in b["patches"]:
            assert patch["symbol"], "Rust emitted an unresolvable kobj patch"
        for line in b["lines"]:
            assert 0 <= line["offset"] <= line["offset"] + line["count"] <= len(y)
            assert line["line"] > 0 and line["file"]


def compare_rgbds(left, right):
    def sections(obj):
        return sorted(({key: section[key] for key in
                        ("name", "kind", "size", "address", "bank", "align", "align_offset")}
                       for section in obj.sections), key=lambda section: section["name"])
    compare(sections(left), sections(right), "RGBDS section attributes differ")
    compare(numeric_symbols(left.symbols), numeric_symbols(right.symbols), "RGBDS symbols differ")


def numeric_symbols(symbols):
    return sorted((symbol for symbol in symbols if symbol.get("kind", 0) <= 1
                   and not symbol["name"].startswith(("__UTC_", "__RGBDS_"))
                   and symbol["name"] != "_NARG"), key=lambda symbol: symbol["name"])


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


def verify_case(name, source, directory, rust, execute=False, reference_source=None):
    models = {}
    for format_name, extension in (("kobj", ".kobj"), ("rgbds", ".o")):
        outputs = {}
        for language in ("csharp", "rust"):
            output = directory / f"{name}-{language}{extension}"
            output.unlink(missing_ok=True)
            if language == "csharp":
                helper("assemble", format_name, source, output)
            else:
                run([rust, source, "-o", output, "--format", format_name])
            outputs[language] = output
        if format_name == "kobj":
            models = {language: json.loads(helper("dump", output)) for language, output in outputs.items()}
            compare_kobj(models["csharp"], models["rust"])
            roms = {}
            for language, output in outputs.items():
                rom = directory / f"{name}-{language}-koh.gb"
                rom.unlink(missing_ok=True)
                helper("link", rom, output)
                roms[language] = rom
            compare(roms["csharp"].read_bytes(), roms["rust"].read_bytes(), "Koh-linked ROMs differ")
            if execute:
                print("  " + helper("execute", roms["rust"]).strip())
        else:
            left, right = RgbObject(outputs["csharp"]), RgbObject(outputs["rust"])
            source_only = {symbol["name"] for symbol in models["csharp"]["symbols"] if symbol["kind"] > 1}
            left.symbols = [symbol for symbol in left.symbols if symbol["name"] not in source_only]
            compare_rgbds(left, right)
            native_rom = directory / f"{name}-rust-rgb.gb"
            native_rom.unlink(missing_ok=True)
            run(["rgblink", "-p", "0", "-o", native_rom, outputs["rust"]])
            reference = directory / f"{name}-reference.o"
            reference.unlink(missing_ok=True)
            reference_input = reference_source or source
            run(["rgbasm", "-I", str(reference_input.parent) + "/", "-o", reference, reference_input])
            reference_rom = directory / f"{name}-reference.gb"
            reference_rom.unlink(missing_ok=True)
            run(["rgblink", "-p", "0", "-o", reference_rom, reference])
            compare(reference_rom.read_bytes(), native_rom.read_bytes(), "RGBDS reference ROM differs")
            csharp_rom = directory / f"{name}-csharp-rgb.gb"
            csharp_rom.unlink(missing_ok=True)
            csharp_link = subprocess.run(["rgblink", "-p", "0", "-o", str(csharp_rom), str(outputs["csharp"])],
                                         cwd=ROOT, text=True, capture_output=True)
            if csharp_link.returncode or csharp_rom.read_bytes() != native_rom.read_bytes():
                print(f"  C# RGBDS output differs for {name}; native output matches RGBDS 1.0.1 reference")
                (directory / f"{name}-csharp-reference-difference.txt").write_text(
                    f"Native output matches rgbasm/rgblink 1.0.1. C# link exit: {csharp_link.returncode}\n"
                    + csharp_link.stdout + csharp_link.stderr)
            if execute:
                print("  " + helper("execute", native_rom).strip())


def adapt_sample(directory):
    destination = directory / "gb2048-rgbds"
    shutil.copytree(ROOT / "samples/asm/gb-2048/src", destination, dirs_exist_ok=True)
    changes = []
    for path in sorted(destination.rglob("*")):
        if not path.is_file():
            continue
        source = path.read_text()
        adapted, count = re.subn(r"(?m)^([ \t]*)([A-Za-z_.$][\w.$]*):[ \t]*MACRO\b", r"\1MACRO \2", source)
        adapted, definitions = re.subn(r"(?m)^([ \t]*)([A-Za-z_.$][\w.$]*)[ \t]+(EQU|EQUS)[ \t]+", r"\1DEF \2 \3 ", adapted)
        if count or definitions:
            path.write_text(adapted)
            changes.append(dict(file=str(path.relative_to(destination)), macro_declarations=count, constant_declarations=definitions))
    (directory / "gb2048-reference-adaptations.json").write_text(json.dumps(changes, indent=2))
    return destination / "main.asm"


def verify_cross_file(directory, rust):
    sources = [directory / "cross-consumer.asm", directory / "cross-provider.asm"]
    sources[0].write_text('SECTION "Consumer", ROM0[$100]\n jp Target\n dw Target + 3\n db HIGH(Target), BANK(Target)\n')
    sources[1].write_text('SECTION "Provider", ROMX[$4000], BANK[2]\nTarget::\n ret\n')
    roms = {}
    for language in ("csharp", "rust", "reference"):
        objects = []
        for index, source in enumerate(sources):
            output = directory / f"cross-{language}-{index}.o"
            output.unlink(missing_ok=True)
            if language == "csharp":
                helper("assemble", "rgbds", source, output)
            elif language == "rust":
                run([rust, source, "--format", "rgbds", "-o", output])
            else:
                run(["rgbasm", "-o", output, source])
            RgbObject(output)
            objects.append(output)
        rom = directory / f"cross-{language}.gb"
        run(["rgblink", "-p", "0", "-o", rom, *objects])
        roms[language] = rom.read_bytes()
    compare(roms["reference"], roms["rust"], "Cross-file RGBDS reference ROM differs")
    compare(roms["csharp"], roms["rust"], "Cross-file C# ROM differs")
    compare(roms["rust"][0x100:0x107], bytes.fromhex("c3 00 40 03 40 40 02"), "Cross-file relocation values differ")
    raw_sources = [directory / "raw-consumer.asm", directory / "raw-provider.asm"]
    raw_sources[0].write_text('SECTION "RawConsumer", ROM0[$100]\njp #ld\ndw #DEF\n')
    raw_sources[1].write_text('SECTION "RawProvider", ROMX[$4000], BANK[2]\n#ld::\nret\nDEF #DEF EQU $1234\nEXPORT #DEF\n')
    objects = {}
    for language in ("rust", "reference"):
        for index, source in enumerate(raw_sources):
            output = directory / f"raw-{language}-{index}.o"
            output.unlink(missing_ok=True)
            command = ([rust, source, "--format", "rgbds", "-o", output] if language == "rust" else
                       ["rgbasm", "-o", output, source])
            run(command)
            names = {symbol["name"] for symbol in RgbObject(output).symbols}
            assert "ld" in names and "DEF" in names and not any(name.startswith("#") for name in names)
            objects[language, index] = output
    for consumer in ("rust", "reference"):
        for provider in ("rust", "reference"):
            rom = directory / f"raw-{consumer}-consumer-{provider}-provider.gb"
            run(["rgblink", "-p", "0", "-o", rom, objects[consumer, 0], objects[provider, 1]])
            compare(rom.read_bytes()[0x100:0x105], bytes.fromhex("c3 00 40 34 12"), "Raw identifier cross-tool ABI differs")


def verify_negative_cases(directory, rust):
    cases = {
        "unknown-opcode": 'SECTION "Bad", ROM0\nbogus\n',
        "invalid-register": 'SECTION "Bad", ROM0\nld q, a\n',
        "bit-range": 'SECTION "Bad", ROM0\nbit 8, a\n',
        "jr-range": 'SECTION "Bad", ROM0[$100]\njr $4000\n',
        "undefined-kobj": 'SECTION "Bad", ROM0\njp MissingSymbol\n',
        "division-zero": 'SECTION "Bad", ROM0\ndb 1 / 0\n',
        "unmatched-endc": 'SECTION "Bad", ROM0\nENDC\n',
        "recursive-macro": 'MACRO recurse\nrecurse\nENDM\nSECTION "Bad", ROM0\nrecurse\n',
        "duplicate-global": 'SECTION "Bad", ROM0\nDuplicate:\nDuplicate:\n',
    }
    results = []
    for name, source in cases.items():
        path = directory / f"negative-{name}.asm"
        path.write_text(source)
        result = dict(name=name, commands=[], passed=True)
        for language in ("csharp", "rust", "reference"):
            output = directory / f"negative-{name}-{language}.o"
            output.unlink(missing_ok=True)
            command = (["dotnet", REFERENCE_HELPER, "assemble", "kobj", path, output] if language == "csharp" else
                       [rust, path, "-o", output] if language == "rust" else
                       ["rgbasm", "-o", output, path])
            process = subprocess.run(list(map(str, command)), cwd=ROOT, text=True, capture_output=True)
            record = dict(language=language, phase="assemble", exit_code=process.returncode,
                          stdout=process.stdout, stderr=process.stderr)
            result["commands"].append(record)
            if language == "reference" and name in ("jr-range", "undefined-kobj") and process.returncode == 0:
                process = subprocess.run(["rgblink", "-o", str(output.with_suffix(".gb")), str(output)],
                                         cwd=ROOT, text=True, capture_output=True)
                result["commands"].append(dict(language=language, phase="link", exit_code=process.returncode,
                                               stdout=process.stdout, stderr=process.stderr))
            location = (re.search(rf"{re.escape(path.name)}:\d+:\d+:", process.stderr) if language != "reference" else
                        re.search(rf"{re.escape(path.name)}\(\d+\)", process.stderr))
            if process.returncode != 1 or not location:
                result["passed"] = False
        results.append(result)
        print(f"{'PASS' if result['passed'] else 'FAIL'} negative-{name}")
    (directory / "negative-results.json").write_text(json.dumps(results, indent=2))
    failures = sum(not result["passed"] for result in results)
    print(f"Negative fixtures: {len(results) - failures} passed; {failures} failed")
    return failures


def verify_independent_semantics(directory, rust):
    source = ROOT / "tools/rust-asm-verify/fixtures/reference-semantics.asm"
    roms = []
    for language in ("rust", "reference"):
        output = directory / f"semantic-{language}.o"
        output.unlink(missing_ok=True)
        command = ([rust, source, "--format", "rgbds", "-o", output] if language == "rust" else
                   ["rgbasm", "-o", output, source])
        run(command)
        graph = RgbObject(output)
        sizes = {section["name"]: section["size"] for section in graph.sections}
        compare(sizes["Execution"], 2, "LOAD execution reservation changed")
        compare(sizes["PlainRam"], 2, "Empty RAM data reservation changed")
        rom = directory / f"semantic-{language}.gb"
        run(["rgblink", "-p", "0", "-o", rom, output])
        roms.append(rom.read_bytes())
    compare(roms[0], roms[1], "Independent semantic regression ROM differs")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--rust", type=Path, default=RUST)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--skip-corpus", action="store_true")
    options = parser.parse_args()
    for tool in ("dotnet", "rgbasm", "rgblink"):
        if not shutil.which(tool):
            parser.error(f"Required tool is absent: {tool}")
    for tool in ("rgbasm", "rgblink"):
        compare(run([tool, "--version"]).strip(), f"{tool} v1.0.1", "RGBDS 1.0.1 required")
    ensure_reference(build=not options.no_build)
    print(f"Historical C# behavioral reference: {REFERENCE_COMMIT}")
    if not options.no_build:
        run(["dotnet", "build", ROOT / "tools/rust-asm-verify/RustAsmVerify.csproj", "-c", "Release", "-m:1"])
        run(["cargo", "build", "--release", "-p", "koh-asm"])
    directory = options.output or Path(tempfile.mkdtemp(prefix="koh-rust-asm-verify-"))
    directory.mkdir(parents=True, exist_ok=True)
    print("Symbol comparison excludes UTC/RGBDS builtins, _NARG, and source-only macro/string definitions.")
    failures = []
    cases = [("execution", EXECUTION, True), ("relocations", RELOCATIONS, False),
             ("macros", MACROS, False), ("opcodes", OPCODES, False)]
    for name, text, execute in cases:
        source = directory / f"{name}.asm"
        source.write_text(text)
        try:
            verify_case(name, source, directory, options.rust, execute)
            print(f"PASS {name}")
        except (AssertionError, ValueError) as error:
            failures.append(name)
            (directory / f"{name}-failure.txt").write_text(str(error))
            print(f"FAIL {name}: {error}")
    sample = ROOT / "samples/asm/gb-2048/src/main.asm"
    try:
        verify_case("gb2048", sample, directory, options.rust, reference_source=adapt_sample(directory))
        print("PASS gb2048")
    except (AssertionError, ValueError) as error:
        failures.append("gb2048")
        (directory / "gb2048-failure.txt").write_text(str(error))
        print(f"FAIL gb2048: {error}")
    try:
        verify_cross_file(directory, options.rust)
        print("PASS cross-file-rgbds")
    except (AssertionError, ValueError) as error:
        failures.append("cross-file-rgbds")
        (directory / "cross-file-failure.txt").write_text(str(error))
        print(f"FAIL cross-file-rgbds: {error}")
    invalid = directory / "invalid.asm"
    invalid.write_text('SECTION "Bad", ROM0\n    ld a, 300\n')
    output = directory / "invalid.kobj"
    output.write_bytes(b"preserve")
    try:
        run([options.rust, invalid, "-o", output], expect=1)
        compare(output.read_bytes(), b"preserve", "Failed assembly overwrote output")
        print("PASS invalid-output-preservation")
    except AssertionError as error:
        failures.append("invalid-output-preservation")
        print(f"FAIL invalid-output-preservation: {error}")
    print(f"Artifacts: {directory}")
    print(f"Result: {7 - len(failures)} passed; {len(failures)} failed")
    semantic_failures = 0
    try:
        verify_independent_semantics(directory, options.rust)
        print("PASS independent-semantic-reference")
    except (AssertionError, ValueError) as error:
        semantic_failures = 1
        print(f"FAIL independent-semantic-reference: {error}")
    negative_failures = verify_negative_cases(directory, options.rust)
    corpus_failures = 0 if options.skip_corpus else verify_corpus(directory, options.rust)
    return bool(failures or semantic_failures or negative_failures or corpus_failures)


if __name__ == "__main__":
    raise SystemExit(main())
