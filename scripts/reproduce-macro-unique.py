#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
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
    parser = argparse.ArgumentParser(description='Reproduce the historical macro unique-suffix report')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--helper', type=Path, default=ROOT / 'tools/rust-asm-verify/bin/Release/net10.0/koh-rust-asm-verify.dll')
    parser.add_argument('--output', type=Path, default=Path(tempfile.gettempdir()) / 'koh-macro-unique-investigation')
    builds = parser.add_mutually_exclusive_group()
    builds.add_argument('--build', action='store_true', help='Build the C# helper before running')
    builds.add_argument('--no-build', action='store_false', dest='build', help='Reuse the existing helper (default)')
    parser.set_defaults(build=False)
    options = parser.parse_args()
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    helper = options.helper.resolve()

    def run(name, tag, args):
        args = list(map(str, args))
        process = subprocess.run(args, cwd=ROOT, capture_output=True, text=True)
        (output / f'{name}-{tag}.log').write_text(
            f'COMMAND={args!r}\nEXIT={process.returncode}\nSTDOUT:\n{process.stdout}\nSTDERR:\n{process.stderr}',
            encoding='utf-8',
        )
        return process

    def require(process, context):
        if process.returncode:
            raise RuntimeError(f'{context} failed: {process.stderr.strip()}')
        return process

    versions = {}
    for tool in ('rgbasm', 'rgblink'):
        version = require(run('tools', tool, [tool, '--version']), tool).stdout.strip()
        if version != f'{tool} v1.0.1':
            raise RuntimeError(f'Expected {tool} v1.0.1; got {version!r}')
        versions[tool] = version
    if options.build:
        require(run('helper', 'build', [options.dotnet, 'build', ROOT / 'tools/rust-asm-verify/RustAsmVerify.csproj', '-c', 'Release', '-m:1']), 'helper build')
    if not helper.is_file():
        raise RuntimeError(f'Helper missing: {helper}; use --build or --helper')
    sample_path = ROOT / 'samples/asm/gb-2048/src/macros.inc'
    sample = sample_path.read_text(encoding='utf-8')
    (output / 'sample-macros.inc').write_text(sample, encoding='utf-8')
    results = []
    for name, source in fixtures(sample).items():
        asm = output / f'{name}.asm'
        asm.write_text(source, encoding='utf-8')
        rgbds_asm = output / f'{name}-rgbds.asm'
        rgbds_asm.write_text(re.sub(r'(?m)^([A-Za-z_][A-Za-z_0-9]*):\s*MACRO\b', r'MACRO \1', source), encoding='utf-8')
        cs_object = output / f'{name}.kobj'
        rgb_object = output / f'{name}.o'
        cs_rom = output / f'{name}-csharp.gb'
        rgb_rom = output / f'{name}-rgbds.gb'
        cs = run(name, 'csharp-assemble', [options.dotnet, helper, 'assemble', 'kobj', asm, cs_object])
        rgb = run(name, 'rgbasm', ['rgbasm', '-o', rgb_object, rgbds_asm])
        result = {'case': name, 'csharp_exit': cs.returncode, 'rgbasm_exit': rgb.returncode}
        results.append(result)
        if cs.returncode:
            result['unexpected_failure'] = 'C# assembly failed'
            continue
        dump = require(run(name, 'csharp-dump', [options.dotnet, helper, 'dump', cs_object]), 'C# dump')
        obj = json.loads(dump.stdout)
        if len(obj['sections']) != 1 or not obj['sections'][0]['data']:
            result['unexpected_failure'] = 'Expected one nonempty Main section'
            continue
        cs_link = run(name, 'csharp-link', [options.dotnet, helper, 'link', cs_rom, cs_object])
        result['csharp_link_exit'] = cs_link.returncode
        result['csharp_sections'] = [{'name': s['name'], 'data': s['data']} for s in obj['sections']]
        if cs_link.returncode:
            result['unexpected_failure'] = 'C# link failed'
            continue
        if name == 'existing-no-global-anchor-fixture':
            result['expected_rgbds_rejection'] = rgb.returncode != 0 and 'Unqualified local label' in rgb.stderr
            if not result['expected_rgbds_rejection']:
                result['unexpected_failure'] = 'RGBDS local-label behavior changed'
            continue
        if rgb.returncode:
            result['unexpected_failure'] = 'RGBDS assembly failed'
            continue
        rgb_link = run(name, 'rgblink', ['rgblink', '-x', '-o', rgb_rom, rgb_object])
        result['rgblink_exit'] = rgb_link.returncode
        if rgb_link.returncode:
            result['unexpected_failure'] = 'RGBDS link failed'
            continue
        cs_data, rgb_data = cs_rom.read_bytes(), rgb_rom.read_bytes()
        result['rgbds_sha256'] = hashlib.sha256(rgb_data).hexdigest()
        result['section_bytes_equal'] = all(
            len(cs_data) >= (s['address'] or 0) + len(bytes.fromhex(s['data']))
            and len(rgb_data) >= (s['address'] or 0) + len(bytes.fromhex(s['data']))
            and cs_data[(s['address'] or 0):(s['address'] or 0) + len(bytes.fromhex(s['data']))]
            == rgb_data[(s['address'] or 0):(s['address'] or 0) + len(bytes.fromhex(s['data']))]
            for s in obj['sections']
        )
        if not result['section_bytes_equal']:
            result['unexpected_failure'] = 'Linked section bytes differ'
    report = {
        'source_head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'sample_comment_sha256': hashlib.sha256(sample_path.read_bytes()).hexdigest(),
        'helper_sha256': hashlib.sha256(helper.read_bytes()).hexdigest(),
        'assembler_source_diff_from_bf0a5fc': subprocess.check_output(['git', 'diff', 'bf0a5fc', '--', 'src/Asm/Koh.Assembler'], cwd=ROOT, text=True),
        'versions': versions,
        'results': results,
    }
    (output / 'results.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    for result in results:
        print(f"{result['case']}: {result.get('unexpected_failure', 'expected outcome')}")
    print(f'Artifacts: {output}')
    return int(any('unexpected_failure' in result for result in results))


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError) as error:
        raise SystemExit(str(error))
